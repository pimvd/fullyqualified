// FullyQualified on Azure Container Apps. One resource group per environment (dev, prd). See docs/adr/0014.
// The resource group and the container registry are created by the workflow (they must exist before the image is built).

targetScope = 'resourceGroup'

@allowed(['dev', 'prd'])
param envName string
param location string = resourceGroup().location

@description('Public host name of the app, e.g. dev.fq.vandijkcloud.nl')
param domain string

@description('Name of the existing container registry that holds the image')
param acrName string

@description('Full image reference, e.g. fqdev123.azurecr.io/fullyqualified:abc123')
param image string

@description('Run the worker as its own container app (prd). When false one container runs everything (dev).')
param splitWorker bool = false

@secure()
param postgresPassword string

@secure()
@description('First-run setup code for /setup. Optional.')
param setupCode string = ''

@description('Id of the managed certificate for the domain. Empty until the domain is bound (the workflow does that).')
param certificateId string = ''

@description('Monthly cost budget in EUR for this resource group, with e-mail alerts at 80% actual and 100% forecast.')
param budgetAmount int = 60
param budgetEmail string
@description('First day of a month, e.g. 2026-11-01')
param budgetStart string = '2026-11-01'

var prefix = 'fq-${envName}'
var postgresName = '${prefix}-pg'
var connectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Database=fullyqualified;Username=fqadmin;Password=${postgresPassword};SSL Mode=Require'

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: acrName
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-id'
  location: location
}

// AcrPull for the app identity
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, identity.id, 'acrpull')
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-law'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // cost guard: the first 5 GB a month are free, this stops ingestion above 0.2 GB a day
    workspaceCapping: { dailyQuotaGb: json('0.2') }
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresName
  location: location
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '17'
    administratorLogin: 'fqadmin'
    administratorLoginPassword: postgresPassword
    storage: { storageSizeGB: 32 }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'fullyqualified'
}

// "Allow Azure services": the container apps reach the server over its public endpoint (TLS required).
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: law.properties.customerId
        sharedKey: law.listKeys().primarySharedKey
      }
    }
  }
}

var commonSecrets = [
  { name: 'db-connection', value: connectionString }
]

var commonEnv = [
  { name: 'ConnectionStrings__Default', secretRef: 'db-connection' }
  { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
  { name: 'Scanning__AllowPrivateAddresses', value: 'false' }
  { name: 'Dns__CompareResolvers', value: '1.1.1.1,9.9.9.9,8.8.8.8' }
]

var webSecrets = empty(setupCode) ? commonSecrets : concat(commonSecrets, [{ name: 'setup-code', value: setupCode }])
var webEnv = concat(commonEnv, [
  { name: 'Auth__Origin', value: 'https://${domain}' }
  { name: 'Auth__RpId', value: domain }
], splitWorker ? [{ name: 'Hosting__Role', value: 'web' }] : [], empty(setupCode) ? [] : [{ name: 'Auth__SetupCode', secretRef: 'setup-code' }])

resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-app'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [acrPull]
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        customDomains: empty(certificateId) ? [] : [
          { name: domain, certificateId: certificateId, bindingType: 'SniEnabled' }
        ]
      }
      registries: [{ server: acr.properties.loginServer, identity: identity.id }]
      secrets: webSecrets
    }
    template: {
      containers: [
        {
          name: 'app'
          image: image
          env: webEnv
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
      // a single replica: the scheduler and the in-app jobs expect one writer in dev, and 0 would stop the scans
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = if (splitWorker) {
  name: '${prefix}-worker'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [acrPull]
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: acr.properties.loginServer, identity: identity.id }]
      secrets: commonSecrets
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: image
          env: concat(commonEnv, [{ name: 'Hosting__Role', value: 'worker' }])
          resources: { cpu: json('0.5'), memory: '1Gi' }
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: '${prefix}-budget'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: budgetStart }
    notifications: {
      actual80: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [budgetEmail]
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [budgetEmail]
      }
    }
  }
}

output appName string = web.name
output appFqdn string = web.properties.configuration.ingress.fqdn
output environmentName string = env.name
output domainVerificationId string = env.properties.customDomainConfiguration.customDomainVerificationId

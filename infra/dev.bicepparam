using './main.bicep'

// Dev: one container runs web + scans + scheduler. The rest (image, secrets, certificate) comes from the workflow.
param envName = 'dev'
param domain = 'dev.fq.vandijkcloud.nl'
param splitWorker = false
param budgetAmount = 60
param budgetEmail = 'pim.vandijk@teamrockstars.nl'
param acrName = readEnvironmentVariable('FQ_ACR_NAME')
param image = readEnvironmentVariable('FQ_IMAGE')
param postgresPassword = readEnvironmentVariable('FQ_PG_PASSWORD')
param setupCode = readEnvironmentVariable('FQ_SETUP_CODE', '')
param certificateId = readEnvironmentVariable('FQ_CERT_ID', '')

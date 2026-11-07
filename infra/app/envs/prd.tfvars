# Prd: image tags are hard-coded. To release, open a PR that changes these three lines to a tag that was built on
# master (the first 12 characters of the commit sha, see the `release` workflow run or the registry).
# The merge starts the deploy-prd workflow, which waits for approval on the `prd` GitHub environment.
environment         = "prd"
domain              = "app.fq.vandijkcloud.nl"
web_image_tag       = "REPLACE_WITH_A_BUILT_TAG"
worker_image_tag    = "REPLACE_WITH_A_BUILT_TAG"
postfix_image_tag   = "REPLACE_WITH_A_BUILT_TAG"
mail_hostname       = "mail.fq.vandijkcloud.nl"
mail_from           = "fullyqualified@fq.vandijkcloud.nl"
smtp_relay_host     = ""
web_max_replicas    = 2
worker_max_replicas = 2
budget_amount       = 75
budget_email        = "pim.vandijk@teamrockstars.nl"

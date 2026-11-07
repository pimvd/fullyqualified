# Prd: image tags are hard-coded. To release, open a PR that changes these two lines to a tag that was built
# on master (the first 12 characters of the commit sha; see the `release` workflow run or the registry).
# The merge starts the deploy-prd workflow, which waits for approval on the `prd` GitHub environment.
environment         = "prd"
domain              = "app.fq.vandijkcloud.nl"
web_image_tag       = "REPLACE_WITH_A_BUILT_TAG"
worker_image_tag    = "REPLACE_WITH_A_BUILT_TAG"
web_max_replicas    = 2
worker_max_replicas = 2
budget_amount       = 75
budget_email        = "pim.vandijk@teamrockstars.nl"

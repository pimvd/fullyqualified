# Dev: image tags are passed by the deploy workflow (the commit that was just built).
environment         = "dev"
domain              = "dev.fq.vandijkcloud.nl"
worker_max_replicas = 1
budget_amount       = 75
budget_email        = "pim.vandijk@teamrockstars.nl"

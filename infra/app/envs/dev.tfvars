# Dev: the image tags are passed by the deploy workflow (the commit that was just built).
environment         = "dev"
domain              = "dev.fq.vandijkcloud.nl"
mail_hostname       = "mail.dev.fq.vandijkcloud.nl"
mail_from           = "fullyqualified@dev.fq.vandijkcloud.nl"
smtp_relay_host     = "" # e.g. email-smtp.eu-west-1.amazonaws.com; set it to deliver real mail
worker_max_replicas = 1
budget_amount       = 75
budget_email        = "pim.vandijk@teamrockstars.nl"

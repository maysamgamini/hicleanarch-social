# Connection strings, one per database role. The owner's is used only by the migration task; each
# module's role logs in with its own. Secrets are deleted at once on destroy (no recovery window),
# so the next apply can reuse their names.
#
# The passwords are ephemeral: generated during the apply and sent only to write-only arguments
# (master_password_wo, secret_string_wo), so they never reach the state file. The migration task
# creates each module's role with the password in its secret.

locals {
  # Module: its login role. A module joins this map in the lesson that gives it a schema.
  db_roles = {
    accounts      = "accounts_app"
    relationships = "relationships_app"
  }

  # TLS with the server's certificate checked against the RDS CA bundle, which the image carries.
  db_tls = "SSL Mode=VerifyFull;Root Certificate=/usr/local/share/rds/global-bundle.pem"

  connection_strings = merge(
    { owner = "Host=${local.db_host};Port=5432;Database=social;Username=owner;Password=${ephemeral.random_password.owner.result};Timeout=60;${local.db_tls};Maximum Pool Size=2" },
    { for key, role in local.db_roles : key => "Host=${local.db_host};Port=5432;Database=social;Username=${role};Password=${ephemeral.random_password.db[key].result};Timeout=60;${local.db_tls};Maximum Pool Size=10" },
  )
}

ephemeral "random_password" "owner" {
  length  = 32
  special = false
}

ephemeral "random_password" "db" {
  for_each = local.db_roles
  length   = 32
  special  = false
}

resource "aws_secretsmanager_secret" "connection" {
  for_each                = toset(concat(["owner"], keys(local.db_roles)))
  name                    = "${var.project}/sandbox/db/${each.key}"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "connection" {
  for_each                 = aws_secretsmanager_secret.connection
  secret_id                = each.value.id
  secret_string_wo         = local.connection_strings[each.key]
  secret_string_wo_version = 1
}

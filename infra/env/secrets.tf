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
    posts         = "posts_app"
    timeline      = "timeline_app"
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

# The key feed cursors are signed with (Timeline:Cursor:Keys:Current). Rotating it: move the value to
# a Previous secret first, so the cursors already handed out keep working for a page or two.
ephemeral "random_password" "cursor_key" {
  length  = 48
  special = false
}

resource "aws_secretsmanager_secret" "cursor_key" {
  name                    = "${var.project}/sandbox/timeline/cursor-key"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "cursor_key" {
  secret_id                = aws_secretsmanager_secret.cursor_key.id
  secret_string_wo         = ephemeral.random_password.cursor_key.result
  secret_string_wo_version = 1
}

# Where the timelines live (valkey.tf); a secret like the others, so every address reaches the
# tasks the same way.
resource "aws_secretsmanager_secret" "valkey" {
  name                    = "${var.project}/sandbox/valkey"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "valkey" {
  secret_id                = aws_secretsmanager_secret.valkey.id
  secret_string_wo         = "${local.valkey_address}:6379,ssl=true,abortConnect=false"
  secret_string_wo_version = 1
}

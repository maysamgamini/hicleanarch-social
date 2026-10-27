# Cognito signs people in. The API never calls it per request: it checks each access token against
# the pool's published keys and its client_id. Development and tests use the local issuer instead.

resource "aws_cognito_user_pool" "users" {
  name                = "${var.project}-sandbox"
  deletion_protection = "INACTIVE"
  username_attributes = ["email"]

  password_policy {
    minimum_length    = 12
    require_lowercase = true
    require_uppercase = true
    require_numbers   = true
    require_symbols   = false
  }

  # No self sign-up in the sandbox: the client id is public. The smoke test creates its own user.
  admin_create_user_config {
    allow_admin_create_user_only = true
  }
}

# A public client (no secret), as a mobile app is. Password sign-in only so the smoke test can get a
# token; a real app signs in through Cognito's hosted pages or SRP.
resource "aws_cognito_user_pool_client" "api" {
  name                          = "${var.project}-api"
  user_pool_id                  = aws_cognito_user_pool.users.id
  generate_secret               = false
  explicit_auth_flows           = ["ALLOW_USER_PASSWORD_AUTH", "ALLOW_REFRESH_TOKEN_AUTH"]
  prevent_user_existence_errors = "ENABLED"
}

locals {
  cognito_issuer = "https://cognito-idp.${var.region}.amazonaws.com/${aws_cognito_user_pool.users.id}"
}

#!/usr/bin/env bash
# Applies the infra/env resources that Moto implements against a local Moto container, checks
# them, then destroys them. No AWS account is involved: the provider's endpoints point at Moto,
# and the access key is a placeholder Moto accepts.
#   scripts/moto-apply.sh                  (TF=<path> to use another terraform binary)
# Moto does not implement Aurora Serverless v2 scaling or every ECS detail, so database.tf and
# ecs.tf are covered by the mock-provider tests (infra/env/tests) only.
set -euo pipefail

TF=${TF:-terraform}
here=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
name="social-moto-$$"
cleanup() { docker rm -fv "$name" >/dev/null 2>&1 || true; rm -rf "$work"; }
trap cleanup EXIT

supported=(variables network alb secrets cognito logs iam)
for file in "${supported[@]}"; do cp "$here/infra/env/$file.tf" "$work/"; done

# Moto knows the AWS managed policies (the ECS execution policy) only when asked to load them.
docker run -d --name "$name" -e MOTO_IAM_LOAD_MANAGED_POLICIES=true -p 127.0.0.1::5000 motoserver/moto:5.2.3 >/dev/null
port=$(docker port "$name" 5000/tcp | head -1 | sed 's/.*://')
endpoint="http://127.0.0.1:$port"
for _ in $(seq 1 30); do curl -sf "$endpoint/moto-api/" >/dev/null && break; sleep 1; done

cat > "$work/moto.tf" <<HCL
terraform {
  required_providers {
    aws    = { source = "hashicorp/aws", version = "~> 6.0" }
    random = { source = "hashicorp/random", version = "~> 3.7" }
  }
}

provider "aws" {
  region                      = var.region
  skip_credentials_validation = true
  skip_requesting_account_id  = true
  skip_metadata_api_check     = true

  endpoints {
    cognitoidp     = "$endpoint"
    ec2            = "$endpoint"
    elbv2          = "$endpoint"
    iam            = "$endpoint"
    logs           = "$endpoint"
    secretsmanager = "$endpoint"
    sts            = "$endpoint"
  }
}

# database.tf and valkey.tf are not applied here; the secrets need only their host names.
locals {
  db_host              = "database.moto.invalid"
  valkey_address       = "valkey.moto.invalid"
  valkey_short_address = "valkey-short.moto.invalid"
}
HCL

export AWS_ACCESS_KEY_ID=moto AWS_SECRET_ACCESS_KEY=moto AWS_REGION=us-east-1
"$TF" -chdir="$work" init -input=false >/dev/null
"$TF" -chdir="$work" apply -input=false -auto-approve
count=$("$TF" -chdir="$work" state list | wc -l | tr -d ' ')
echo "applied $count resources against Moto"
"$TF" -chdir="$work" destroy -input=false -auto-approve >/dev/null
left=$("$TF" -chdir="$work" state list | wc -l | tr -d ' ')
test "$left" = "0"
echo "destroyed every resource: none left in state"

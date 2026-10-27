# infra/env

The sandbox environment: a VPC with no NAT gateway, an Application Load Balancer, an ECS Fargate
service (the API and an OpenTelemetry collector sidecar sending traces to X-Ray and metrics to
CloudWatch), a one-off migration task, Aurora Serverless v2 PostgreSQL 17 that pauses when idle,
connection strings in Secrets Manager, and a Cognito user pool.

Everything is destroyable: no deletion protection, no final snapshot, secrets with no recovery
window. Passwords are ephemeral and sent only to write-only arguments, so they never reach the state.

## Checked offline

```bash
terraform -chdir=infra/env init -backend=false
terraform -chdir=infra/env validate
terraform -chdir=infra/env test           # mock providers: no account, no credentials
scripts/moto-apply.sh                # applies network, load balancer, secrets, Cognito, logs and IAM to Moto, then destroys them
```

Moto does not implement Aurora Serverless v2 scaling or every ECS detail; `database.tf` and
`ecs.tf` are covered by the mock-provider tests only.

## Applied in your own account (a learner exercise)

The `sandbox` workflow (`.github/workflows/sandbox.yml`) builds the image, applies this layer with
the service on its current image, runs the migration task, rolls the service onto the release,
smoke-tests it with a Cognito user, and destroys everything. It runs only when the repository
variables from `infra/bootstrap` are set. The course never runs it; its cost and timings are yours to read.

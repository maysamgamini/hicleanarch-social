output "state_bucket" {
  description = "Bucket for the state of infra/env."
  value       = aws_s3_bucket.state.bucket
}

output "ecr_repository_url" {
  description = "Where the sandbox workflow pushes the API image."
  value       = aws_ecr_repository.api.repository_url
}

output "deploy_role_arn" {
  description = "Set it as the AWS_DEPLOY_ROLE_ARN variable of your GitHub repository."
  value       = aws_iam_role.deploy.arn
}

output "deploy_subject" {
  description = "The GitHub workflow identity the deploy role trusts."
  value       = local.deploy_subject
}

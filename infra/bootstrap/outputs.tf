output "state_bucket" {
  description = "Bucket for the state of infra/env."
  value       = aws_s3_bucket.state.bucket
}

output "ecr_repository_url" {
  description = "Where the sandbox workflow pushes the API image."
  value       = aws_ecr_repository.api.repository_url
}

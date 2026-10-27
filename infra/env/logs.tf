resource "aws_cloudwatch_log_group" "api" {
  name              = "/${var.project}/sandbox/api"
  retention_in_days = 1
}

# The collector's built-in ECS configuration sends metrics to this group. Terraform creates it, so
# destroy removes it.
resource "aws_cloudwatch_log_group" "collector_metrics" {
  name              = "/aws/ecs/application/metrics"
  retention_in_days = 1
}

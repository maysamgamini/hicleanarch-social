# The acknowledgments as alarms. The program's metrics reach CloudWatch through the collector
# sidecar's EMF exporter, under its default namespace. The alarms change state; with no topic set
# they notify nobody, and a learner adds an SNS topic of their own.

locals {
  metrics_namespace = "ECS/AWSOTel/Application"

  # Every metric the program emits that an alarm may name (the s2-l19 test checks alarms against it).
  metrics = toset([
    "social.posts.stored",
    "social.posts.seen",
    "social.fanout.lag",
    "outbox.oldest_undelivered_age",
  ])
}

# Fanned out: Round 1 asks that followers see a post within about a minute (design objective).
resource "aws_cloudwatch_metric_alarm" "fanout_lag" {
  alarm_name          = "${var.project}-fanout-lag-p99"
  alarm_description   = "p99 time from a post's commit to its last timeline write is above 60 s for 5 minutes."
  namespace           = local.metrics_namespace
  metric_name         = "social.fanout.lag"
  extended_statistic  = "p99"
  period              = 60
  evaluation_periods  = 5
  threshold           = 60
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
}

# Stored but not delivered: an event older than the sweep's redelivery window is stuck.
resource "aws_cloudwatch_metric_alarm" "outbox_age" {
  alarm_name          = "${var.project}-outbox-oldest-undelivered"
  alarm_description   = "The oldest undelivered event is older than 5 minutes: a handler keeps failing."
  namespace           = local.metrics_namespace
  metric_name         = "outbox.oldest_undelivered_age"
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 3
  threshold           = 300
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
}

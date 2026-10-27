# The rules and the deliveries as alarms. The program's metrics reach CloudWatch through the
# collector sidecar's EMF exporter, under its default namespace. The alarms change state; with no
# topic set they notify nobody, and a learner adds an SNS topic of their own.

locals {
  metrics_namespace = "ECS/AWSOTel/Application"

  # Every metric the program emits that an alarm or the dashboard may name; the terraform test checks
  # every alarm against this list.
  metrics = toset([
    "social.posts.stored",
    "social.posts.seen",
    "social.fanout.lag",
    "social.projection.lag",
    "social.feed.duration",
    "outbox.oldest_undelivered_age",
    "social.invariant.follows_across_block",
    "social.invariant.timelines_above_800",
    "social.invariant.posts_not_fanned_out",
  ])

  # Gauges that must read zero: any other value is a rule broken.
  invariants = {
    follows_across_block = "social.invariant.follows_across_block"
    timelines_above_800  = "social.invariant.timelines_above_800"
    posts_not_fanned_out = "social.invariant.posts_not_fanned_out"
  }

  # Every module with an outbox: its oldest undelivered event has an alarm of its own.
  outbox_modules = toset(["Accounts", "Relationships", "Posts", "Notifications"])
}

resource "aws_cloudwatch_metric_alarm" "invariant" {
  for_each            = local.invariants
  alarm_name          = "${var.project}-invariant-${replace(each.key, "_", "-")}"
  alarm_description   = "${each.value} must read 0; it read more for 2 minutes."
  namespace           = local.metrics_namespace
  metric_name         = each.value
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 2
  threshold           = 0
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
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

# A follow reaches the graph within seconds; suggestions tolerate that, not minutes.
resource "aws_cloudwatch_metric_alarm" "projection_lag" {
  alarm_name          = "${var.project}-projection-lag-p99"
  alarm_description   = "p99 time from FollowStarted's commit to its graph edge is above 60 s for 5 minutes."
  namespace           = local.metrics_namespace
  metric_name         = "social.projection.lag"
  extended_statistic  = "p99"
  period              = 60
  evaluation_periods  = 5
  threshold           = 60
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
}

# The read path's budget: a feed page in 200 ms at p99 (design objective).
resource "aws_cloudwatch_metric_alarm" "feed_p99" {
  alarm_name          = "${var.project}-feed-p99"
  alarm_description   = "p99 of GET /v1/feed is above 200 ms for 5 minutes."
  namespace           = local.metrics_namespace
  metric_name         = "social.feed.duration"
  extended_statistic  = "p99"
  period              = 60
  evaluation_periods  = 5
  threshold           = 200
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
}

# Stored but not handed over: an event older than the sweep's redelivery window is stuck.
resource "aws_cloudwatch_metric_alarm" "outbox_age" {
  for_each            = local.outbox_modules
  alarm_name          = "${var.project}-outbox-${lower(each.key)}-oldest-undelivered"
  alarm_description   = "${each.key}'s oldest undelivered event is older than 5 minutes: a handler keeps failing."
  namespace           = local.metrics_namespace
  metric_name         = "outbox.oldest_undelivered_age"
  dimensions          = { module = each.key }
  statistic           = "Maximum"
  period              = 60
  evaluation_periods  = 3
  threshold           = 300
  comparison_operator = "GreaterThanThreshold"
  treat_missing_data  = "notBreaching"
}

resource "aws_cloudwatch_dashboard" "rules" {
  dashboard_name = "${var.project}-rules"
  dashboard_body = jsonencode({
    widgets = concat(
      [for i, name in values(local.invariants) : {
        type       = "metric", x = (i % 3) * 8, y = 0, width = 8, height = 6
        properties = { title = name, region = var.region, stat = "Maximum", period = 60, metrics = [[local.metrics_namespace, name]] }
      }],
      [for i, name in ["social.fanout.lag", "social.projection.lag", "social.feed.duration"] : {
        type       = "metric", x = i * 8, y = 6, width = 8, height = 6
        properties = { title = "${name} p99", region = var.region, stat = "p99", period = 60, metrics = [[local.metrics_namespace, name]] }
      }],
      [{
        type = "metric", x = 0, y = 12, width = 24, height = 6
        properties = {
          title   = "outbox.oldest_undelivered_age by module", region = var.region, stat = "Maximum", period = 60
          metrics = [for m in local.outbox_modules : [local.metrics_namespace, "outbox.oldest_undelivered_age", "module", m]]
        }
      }],
    )
  })
}

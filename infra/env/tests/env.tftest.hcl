# Checked offline: mock providers answer every call, so no AWS account or credentials are needed.
mock_provider "aws" {
  mock_data "aws_availability_zones" {
    defaults = {
      names = ["us-east-1a", "us-east-1b", "us-east-1c"]
    }
  }

  # A policy document's json must be JSON, which a mock's random string is not.
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }

  # Arguments that must be ARNs get ARN-shaped values.
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::123456789012:role/mock"
    }
  }

  mock_resource "aws_lb_target_group" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/mock/0123456789abcdef"
    }
  }

  mock_resource "aws_lb" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/app/mock/0123456789abcdef"
    }
  }

  mock_resource "aws_secretsmanager_secret" {
    defaults = {
      arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:mock"
    }
  }

  mock_resource "aws_elasticache_replication_group" {
    defaults = {
      primary_endpoint_address = "timelines.mock.cache.amazonaws.com"
    }
  }

  mock_resource "aws_neptune_cluster" {
    defaults = {
      endpoint = "graph.cluster-mock.us-east-1.neptune.amazonaws.com"
    }
  }

  mock_resource "aws_ecs_cluster" {
    defaults = {
      arn = "arn:aws:ecs:us-east-1:123456789012:cluster/mock"
    }
  }
}

mock_provider "random" {}

variables {
  image         = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch-social/api:test"
  service_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch-social/api:test"
}

run "the_database_can_be_destroyed_with_nothing_left_behind" {
  command = apply

  assert {
    condition     = aws_rds_cluster.main.deletion_protection == false
    error_message = "No deletion protection: one destroy removes the sandbox."
  }

  assert {
    condition     = aws_rds_cluster.main.skip_final_snapshot
    error_message = "No final snapshot: a snapshot outlives the destroy and is billed."
  }

  assert {
    condition     = aws_rds_cluster.main.engine == "aurora-postgresql" && startswith(aws_rds_cluster.main.engine_version, "17")
    error_message = "Aurora PostgreSQL 17, the engine the course targets."
  }

  assert {
    condition     = aws_rds_cluster.main.serverlessv2_scaling_configuration[0].min_capacity == 0
    error_message = "The sandbox's database pauses when idle (0 capacity units)."
  }
}

run "every_other_resource_can_be_destroyed" {
  command = apply

  assert {
    condition     = alltrue([for s in aws_secretsmanager_secret.connection : s.recovery_window_in_days == 0])
    error_message = "Secrets are deleted at once, so the next apply can reuse their names."
  }

  assert {
    condition     = aws_cognito_user_pool.users.deletion_protection == "INACTIVE"
    error_message = "The user pool must not refuse a destroy."
  }

  assert {
    condition     = aws_lb.api.enable_deletion_protection == false
    error_message = "The load balancer must not refuse a destroy."
  }

  assert {
    condition     = aws_secretsmanager_secret.valkey.recovery_window_in_days == 0 && aws_elasticache_replication_group.valkey.engine == "valkey"
    error_message = "Timelines live in Valkey, and its secret is deleted at once."
  }
}

run "valkey_is_reached_from_the_tasks_only_over_tls" {
  command = apply

  assert {
    condition     = aws_elasticache_replication_group.valkey.transit_encryption_enabled && aws_vpc_security_group_ingress_rule.valkey_from_tasks.cidr_ipv4 == null
    error_message = "Valkey takes TLS connections from the tasks' security group only."
  }

  assert {
    condition     = strcontains(aws_ecs_task_definition.api.container_definitions, "ConnectionStrings__Valkey")
    error_message = "The program gets Valkey's address from its secret."
  }
}

run "the_program_gets_module_roles_and_only_the_migration_gets_the_owner" {
  command = apply

  assert {
    condition     = !strcontains(aws_ecs_task_definition.api.container_definitions, "ConnectionStrings__Owner")
    error_message = "The serving tasks hold only data rights: the owner's connection string goes to the migration task alone."
  }

  assert {
    condition     = strcontains(aws_ecs_task_definition.migrate.container_definitions, "ConnectionStrings__Owner")
    error_message = "The migration task runs as the schema owner."
  }

  assert {
    condition     = strcontains(aws_ecs_task_definition.migrate.container_definitions, "\"migrate\"")
    error_message = "The migration task runs the image's migrate command."
  }
}

run "tokens_are_checked_against_cognito_and_telemetry_goes_to_the_collector" {
  command = apply

  assert {
    condition     = strcontains(aws_ecs_task_definition.api.container_definitions, "Auth__Authority")
    error_message = "On AWS, the program validates Cognito's tokens (the local issuer is for development only)."
  }

  assert {
    condition     = strcontains(aws_ecs_task_definition.api.container_definitions, "aws-otel-collector")
    error_message = "Every task carries the OpenTelemetry collector sidecar."
  }
}

run "the_database_accepts_only_the_tasks" {
  command = apply

  assert {
    condition     = aws_vpc_security_group_ingress_rule.database_from_tasks.from_port == 5432 && aws_vpc_security_group_ingress_rule.database_from_tasks.cidr_ipv4 == null
    error_message = "PostgreSQL is reached from the tasks' security group only, never from an address range."
  }
}

run "no_service_before_the_first_migration" {
  command = apply

  variables {
    service_image = ""
  }

  assert {
    condition     = length(aws_ecs_service.api) == 0
    error_message = "A new environment gets its service only after the release has been migrated."
  }
}

run "every_alarm_names_a_metric_the_program_emits" {
  command = apply

  assert {
    condition = alltrue(concat(
      [for a in aws_cloudwatch_metric_alarm.invariant : contains(local.metrics, a.metric_name)],
      [for a in aws_cloudwatch_metric_alarm.outbox_age : contains(local.metrics, a.metric_name)],
      [for a in [aws_cloudwatch_metric_alarm.fanout_lag, aws_cloudwatch_metric_alarm.projection_lag, aws_cloudwatch_metric_alarm.feed_p99] : contains(local.metrics, a.metric_name)],
    ))
    error_message = "Every alarm names a metric the program emits."
  }

  assert {
    condition     = alltrue([for a in aws_cloudwatch_metric_alarm.invariant : a.threshold == 0 && a.comparison_operator == "GreaterThanThreshold"])
    error_message = "An invariant gauge alarms on anything above zero."
  }

  assert {
    condition     = length(aws_cloudwatch_metric_alarm.invariant) == 3 && length(aws_cloudwatch_metric_alarm.outbox_age) == 4
    error_message = "Each invariant gauge and each module's outbox has an alarm."
  }

  assert {
    condition     = aws_cloudwatch_metric_alarm.fanout_lag.threshold == 60 && aws_cloudwatch_metric_alarm.fanout_lag.extended_statistic == "p99"
    error_message = "Fanned out within a minute at p99 is the alarm's line."
  }
}

run "neptune_is_serverless_destroyable_and_reached_from_the_tasks_only_over_tls" {
  command = apply

  assert {
    condition     = aws_neptune_cluster.graph.deletion_protection == false && aws_neptune_cluster.graph.skip_final_snapshot
    error_message = "One destroy removes the graph, with no snapshot left behind."
  }

  assert {
    condition     = aws_neptune_cluster_instance.graph.instance_class == "db.serverless" && aws_neptune_cluster.graph.serverless_v2_scaling_configuration[0].min_capacity == 1
    error_message = "The sandbox's graph is serverless, at its smallest."
  }

  assert {
    condition     = aws_vpc_security_group_ingress_rule.graph_from_tasks.from_port == 8182 && aws_vpc_security_group_ingress_rule.graph_from_tasks.cidr_ipv4 == null
    error_message = "Bolt on 8182, from the tasks' security group only."
  }

  assert {
    condition     = startswith(local.graph_uri, "bolt+s://") && strcontains(aws_ecs_task_definition.api.container_definitions, "Graph__Uri")
    error_message = "The program reaches Neptune over Bolt with TLS."
  }
}

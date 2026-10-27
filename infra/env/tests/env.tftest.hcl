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
      arn        = "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/mock/0123456789abcdef"
      arn_suffix = "targetgroup/mock/0123456789abcdef"
    }
  }

  mock_resource "aws_lb" {
    defaults = {
      arn        = "arn:aws:elasticloadbalancing:us-east-1:123456789012:loadbalancer/app/mock/0123456789abcdef"
      arn_suffix = "app/mock/0123456789abcdef"
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

  mock_resource "aws_s3_bucket" {
    defaults = {
      arn                         = "arn:aws:s3:::hicleanarch-social-media-mock"
      bucket                      = "hicleanarch-social-media-mock"
      bucket_regional_domain_name = "hicleanarch-social-media-mock.s3.us-east-1.amazonaws.com"
    }
  }

  mock_resource "aws_cloudfront_distribution" {
    defaults = {
      arn         = "arn:aws:cloudfront::123456789012:distribution/EMOCK"
      domain_name = "dmock.cloudfront.net"
    }
  }

  mock_resource "aws_service_discovery_service" {
    defaults = {
      arn = "arn:aws:servicediscovery:us-east-1:123456789012:service/srv-mock"
    }
  }

  mock_resource "aws_neptune_cluster" {
    defaults = {
      endpoint        = "graph.cluster-mock.us-east-1.neptune.amazonaws.com"
      reader_endpoint = "graph.cluster-ro-mock.us-east-1.neptune.amazonaws.com"
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

run "one_image_two_roles_two_services_each_scaling_on_its_own" {
  command = apply

  assert {
    condition     = strcontains(aws_ecs_task_definition.api.container_definitions, "\"Web\"") && strcontains(aws_ecs_task_definition.worker.container_definitions, "\"Worker\"")
    error_message = "The web service runs Host__Role=Web, the worker service Host__Role=Worker."
  }

  assert {
    condition     = jsondecode(aws_ecs_task_definition.api.container_definitions)[0].image == jsondecode(aws_ecs_task_definition.worker.container_definitions)[0].image
    error_message = "Both roles run one image."
  }

  assert {
    condition     = length(aws_ecs_service.worker[0].load_balancer) == 0 && length(jsondecode(aws_ecs_task_definition.worker.container_definitions)[0].portMappings) == 0
    error_message = "A worker takes no traffic: no port, no load balancer."
  }

  assert {
    condition     = aws_appautoscaling_policy.web_cpu[0].target_tracking_scaling_policy_configuration[0].predefined_metric_specification[0].predefined_metric_type == "ECSServiceAverageCPUUtilization" && aws_appautoscaling_policy.worker_cpu[0].target_tracking_scaling_policy_configuration[0].predefined_metric_specification[0].predefined_metric_type == "ECSServiceAverageCPUUtilization"
    error_message = "Each role scales on its own signal."
  }
}

run "the_timeline_store_holds_only_compact_timelines_and_short_lived_keys_live_apart" {
  command = apply

  assert {
    condition = alltrue([
      for p in aws_elasticache_parameter_group.timelines.parameter :
      (p.name == "zset-max-listpack-entries" && p.value == "1024") || (p.name == "maxmemory-policy" && p.value == "volatile-ttl")
    ]) && length(aws_elasticache_parameter_group.timelines.parameter) == 2
    error_message = "The timeline store keeps sorted sets compact up to 1,024 entries and evicts by TTL."
  }

  assert {
    condition     = aws_elasticache_replication_group.valkey.parameter_group_name == aws_elasticache_parameter_group.timelines.name
    error_message = "The timeline store runs with the timelines' parameters."
  }

  assert {
    condition     = aws_elasticache_replication_group.short_lived.transit_encryption_enabled && aws_elasticache_replication_group.short_lived.engine == "valkey"
    error_message = "The short-lived store is a second Valkey, over TLS."
  }

  assert {
    condition     = strcontains(aws_ecs_task_definition.api.container_definitions, "ConnectionStrings__ValkeyShort") && strcontains(aws_ecs_task_definition.worker.container_definitions, "ConnectionStrings__ValkeyShort")
    error_message = "Both roles get the short-lived store's address from its secret."
  }
}

run "images_live_in_a_private_bucket_read_only_through_cloudfront" {
  command = apply

  assert {
    condition = (aws_s3_bucket_public_access_block.media.block_public_acls && aws_s3_bucket_public_access_block.media.block_public_policy
    && aws_s3_bucket_public_access_block.media.restrict_public_buckets && aws_s3_bucket.media.force_destroy)
    error_message = "The media bucket is private, and the sandbox can destroy it with its images."
  }

  assert {
    condition     = aws_cloudfront_origin_access_control.media.signing_behavior == "always" && aws_cloudfront_distribution.media.default_cache_behavior[0].viewer_protocol_policy == "redirect-to-https"
    error_message = "CloudFront reads the bucket with origin access control and serves over HTTPS."
  }

  assert {
    condition     = strcontains(aws_ecs_task_definition.api.container_definitions, "\"Media__Store\",\"value\":\"S3\"") && strcontains(aws_ecs_task_definition.api.container_definitions, "https://dmock.cloudfront.net")
    error_message = "On AWS the program uses the S3 store, with public URLs through CloudFront."
  }
}

run "hydration_reads_the_reader_endpoint" {
  command = apply

  assert {
    condition     = strcontains(aws_ecs_task_definition.api.container_definitions, "Database__ReaderHost") && strcontains(aws_ecs_task_definition.worker.container_definitions, "Database__ReaderHost")
    error_message = "The program gets Aurora's reader endpoint for its read side."
  }
}

run "the_suggestion_walks_go_to_a_neptune_reader" {
  command = apply

  assert {
    condition     = length(aws_neptune_cluster_instance.graph_reader) == 1 && strcontains(aws_ecs_task_definition.api.container_definitions, "bolt+s://graph.cluster-ro-mock.us-east-1.neptune.amazonaws.com:8182")
    error_message = "One reader instance, and the program walks the graph through the reader endpoint."
  }
}

run "every_public_request_goes_through_the_gateway" {
  command = apply

  variables {
    gateway_image = "123456789012.dkr.ecr.us-east-1.amazonaws.com/hicleanarch-social/gateway:test"
  }

  assert {
    condition     = aws_lb_listener.http.default_action[0].target_group_arn == aws_lb_target_group.gateway.arn && length(aws_ecs_service.gateway[0].load_balancer) == 1
    error_message = "The load balancer sends every request to the gateway."
  }

  assert {
    condition     = length(aws_ecs_service.api[0].load_balancer) == 0 && length(aws_ecs_service.api[0].service_registries) == 1
    error_message = "The monolith's web tasks are reached through the gateway only, by name."
  }

  assert {
    condition     = strcontains(aws_ecs_task_definition.gateway.container_definitions, "http://api.social.internal:8080/") && strcontains(aws_ecs_task_definition.gateway.container_definitions, "ConnectionStrings__ValkeyShort")
    error_message = "The gateway forwards to the monolith by its private name and counts its limits in the short-lived store."
  }

  assert {
    condition     = aws_appautoscaling_policy.gateway_requests[0].target_tracking_scaling_policy_configuration[0].predefined_metric_specification[0].predefined_metric_type == "ALBRequestCountPerTarget"
    error_message = "The gateway scales on the requests each task forwards."
  }
}

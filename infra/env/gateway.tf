# The gateway (s3-l14): YARP on its own ECS service, behind the load balancer, in front of the
# monolith. It counts posting and following per account and anonymous reads per address in the
# short-lived Valkey, and forwards everything else unchanged to the monolith's web tasks, which it
# finds by name through Cloud Map (private DNS in the VPC).

resource "aws_service_discovery_private_dns_namespace" "internal" {
  name = "social.internal"
  vpc  = aws_vpc.main.id
}

resource "aws_service_discovery_service" "api" {
  name = "api"

  dns_config {
    namespace_id   = aws_service_discovery_private_dns_namespace.internal.id
    routing_policy = "MULTIVALUE"
    dns_records {
      type = "A"
      ttl  = 10
    }
  }

  # ECS reports each task's health to Cloud Map; a stopped task leaves the name at once.
  health_check_custom_config {}
}

resource "aws_cloudwatch_log_group" "gateway" {
  name              = "/${var.project}/sandbox/gateway"
  retention_in_days = 7
}

locals {
  gateway_container = {
    name      = "gateway"
    image     = var.gateway_image
    essential = true
    portMappings = [{
      containerPort = 8080
      protocol      = "tcp"
    }]
    environment = [
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
      # The load balancer's X-Forwarded-For is trusted from inside the VPC only.
      { name = "ForwardedHeaders__KnownNetwork", value = aws_vpc.main.cidr_block },
      { name = "ReverseProxy__Clusters__monolith__Destinations__monolith__Address", value = "http://${aws_service_discovery_service.api.name}.${aws_service_discovery_private_dns_namespace.internal.name}:8080/" },
    ]
    secrets     = [{ name = "ConnectionStrings__ValkeyShort", valueFrom = aws_secretsmanager_secret.valkey_short.arn }]
    stopTimeout = 30
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.gateway.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "gateway"
      }
    }
  }
}

resource "aws_ecs_task_definition" "gateway" {
  family                   = "${var.project}-gateway"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.execution.arn
  container_definitions    = jsonencode([local.gateway_container])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

resource "aws_ecs_service" "gateway" {
  count           = var.gateway_image == "" || var.service_image == "" ? 0 : 1
  name            = "gateway"
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.gateway.arn
  desired_count   = var.gateway_count
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.tasks.id]
    assign_public_ip = true
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.gateway.arn
    container_name   = "gateway"
    container_port   = 8080
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  lifecycle {
    ignore_changes = [desired_count]
  }

  depends_on = [aws_lb_listener.http]
}

resource "aws_appautoscaling_target" "gateway" {
  count              = length(aws_ecs_service.gateway)
  service_namespace  = "ecs"
  resource_id        = "service/${aws_ecs_cluster.main.name}/${aws_ecs_service.gateway[0].name}"
  scalable_dimension = "ecs:service:DesiredCount"
  min_capacity       = var.gateway_count
  max_capacity       = var.max_gateway_count
}

resource "aws_appautoscaling_policy" "gateway_requests" {
  count              = length(aws_appautoscaling_target.gateway)
  name               = "${var.project}-gateway-requests"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.gateway[0].service_namespace
  resource_id        = aws_appautoscaling_target.gateway[0].resource_id
  scalable_dimension = aws_appautoscaling_target.gateway[0].scalable_dimension

  target_tracking_scaling_policy_configuration {
    # Design estimate: a proxy forwards several times the requests a web task serves; 2,000 a minute per task.
    target_value = 2000
    predefined_metric_specification {
      predefined_metric_type = "ALBRequestCountPerTarget"
      resource_label         = "${aws_lb.api.arn_suffix}/${aws_lb_target_group.gateway.arn_suffix}"
    }
  }
}

resource "aws_ecs_cluster" "main" {
  name = "${var.project}-sandbox"
}

locals {
  api_service    = "api"
  worker_service = "worker"

  # The running program gets only the modules' connection strings; the owner's goes to the
  # migration task alone.
  module_secrets = concat([for key, role in local.db_roles : {
    name      = "ConnectionStrings__${title(key)}"
    valueFrom = aws_secretsmanager_secret.connection[key].arn
    }],
    [
      { name = "ConnectionStrings__Valkey", valueFrom = aws_secretsmanager_secret.valkey.arn },
      { name = "ConnectionStrings__ValkeyShort", valueFrom = aws_secretsmanager_secret.valkey_short.arn },
      { name = "Timeline__Cursor__Keys__Current", valueFrom = aws_secretsmanager_secret.cursor_key.arn },
    ],
  )
  owner_secret = { name = "ConnectionStrings__Owner", valueFrom = aws_secretsmanager_secret.connection["owner"].arn }

  api_container = {
    name      = "api"
    image     = var.image
    essential = true
    portMappings = [{
      containerPort = 8080
      protocol      = "tcp"
    }]
    environment = concat([
      { name = "OTEL_EXPORTER_OTLP_ENDPOINT", value = "http://localhost:4317" },
      { name = "OTEL_SERVICE_NAME", value = "social-api" },
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
      { name = "ForwardedHeaders__KnownNetwork", value = aws_vpc.main.cidr_block },
      { name = "Graph__Uri", value = local.graph_uri },
      { name = "Graph__ReaderUri", value = var.graph_readers > 0 ? local.graph_reader_uri : local.graph_uri },
      { name = "Graph__Auth", value = "none" },
      { name = "Host__Role", value = "Web" },
      # Hydration reads Aurora's reader endpoint with each module's own login (s3-l11).
      { name = "Database__ReaderHost", value = local.db_reader_host },
    ], local.media_environment)
    # SIGTERM, then this many seconds to finish requests, then SIGKILL.
    stopTimeout = 30
    secrets     = local.module_secrets
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.api.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "api"
      }
    }
  }

  # The AWS Distro for OpenTelemetry collector, a sidecar with its built-in ECS configuration: it
  # receives OTLP on localhost:4317 and exports traces to X-Ray and metrics to CloudWatch.
  collector_container = {
    name      = "otel-collector"
    image     = var.collector_image
    essential = false
    command   = ["--config=/etc/ecs/ecs-default-config.yaml"]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.api.name
        awslogs-region        = var.region
        awslogs-stream-prefix = "collector"
      }
    }
  }
}

resource "aws_ecs_task_definition" "api" {
  family                   = "${var.project}-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  container_definitions    = jsonencode([merge(local.api_container, { image = coalesce(var.service_image, var.image) }), local.collector_container])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# The release image, run once per deploy with the argument `migrate`, as the schema owner. The
# sandbox workflow applies with the service still on its current image, runs this task, and only
# then applies again with service_image set to the release.
resource "aws_ecs_task_definition" "migrate" {
  family                   = "${var.project}-migrate"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 256
  memory                   = 512
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  container_definitions = jsonencode([merge(local.api_container, {
    name         = "migrate"
    command      = ["migrate"]
    portMappings = []
    environment = [
      { name = "Auth__Authority", value = local.cognito_issuer },
      { name = "Auth__ClientId", value = aws_cognito_user_pool_client.api.id },
      { name = "Graph__Uri", value = local.graph_uri },
      { name = "Graph__Auth", value = "none" },
    ]
    secrets = concat(local.module_secrets, [local.owner_secret])
  })])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

# Created by the second apply of a deploy, once the database is migrated (see migrate above).
resource "aws_ecs_service" "api" {
  count           = var.service_image == "" ? 0 : 1
  name            = local.api_service
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.api.arn
  desired_count   = var.desired_count
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.tasks.id]
    assign_public_ip = true
  }

  # Not behind the load balancer since s3-l14: the gateway finds the tasks by name (gateway.tf).
  service_registries {
    registry_arn = aws_service_discovery_service.api.arn
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  # Autoscaling owns the task count once the service exists (autoscaling.tf).
  lifecycle {
    ignore_changes = [desired_count]
  }

  depends_on = [aws_lb_listener.http]
}

# The same image as a worker: Host__Role=Worker runs event delivery, jobs and recovery passes and
# serves nothing but its health checks, so it has no port in the load balancer.
locals {
  worker_container = merge(local.api_container, {
    name         = "worker"
    image        = coalesce(var.service_image, var.image)
    portMappings = []
    environment  = [for e in local.api_container.environment : e.name == "Host__Role" ? { name = "Host__Role", value = "Worker" } : e]
  })
}

resource "aws_ecs_task_definition" "worker" {
  family                   = "${var.project}-worker"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = 512
  memory                   = 1024
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.task.arn
  container_definitions    = jsonencode([local.worker_container, local.collector_container])

  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = "X86_64"
  }
}

resource "aws_ecs_service" "worker" {
  count           = var.service_image == "" ? 0 : 1
  name            = local.worker_service
  cluster         = aws_ecs_cluster.main.id
  task_definition = aws_ecs_task_definition.worker.arn
  desired_count   = var.worker_count
  launch_type     = "FARGATE"

  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.tasks.id]
    assign_public_ip = true
  }

  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  lifecycle {
    ignore_changes = [desired_count]
  }
}

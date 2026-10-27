# Each role scales on its own signal: web tasks on CPU (the gateway, in front, on requests), workers on CPU
# (fanout and projections are CPU and I/O bound, and a burst of posts shows there first). A burst of
# posts adds workers; a burst of readers adds web tasks; neither waits for the other.

resource "aws_appautoscaling_target" "web" {
  count              = length(aws_ecs_service.api)
  service_namespace  = "ecs"
  resource_id        = "service/${aws_ecs_cluster.main.name}/${aws_ecs_service.api[0].name}"
  scalable_dimension = "ecs:service:DesiredCount"
  min_capacity       = var.desired_count
  max_capacity       = var.max_web_count
}

resource "aws_appautoscaling_policy" "web_cpu" {
  count              = length(aws_appautoscaling_target.web)
  name               = "${var.project}-web-cpu"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.web[0].service_namespace
  resource_id        = aws_appautoscaling_target.web[0].resource_id
  scalable_dimension = aws_appautoscaling_target.web[0].scalable_dimension

  # Since s3-l14 the load balancer counts the gateway's requests, not the web tasks': web tasks scale
  # on CPU, and the gateway on the requests per target (gateway.tf).
  target_tracking_scaling_policy_configuration {
    target_value = 60
    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageCPUUtilization"
    }
  }
}

resource "aws_appautoscaling_target" "worker" {
  count              = length(aws_ecs_service.worker)
  service_namespace  = "ecs"
  resource_id        = "service/${aws_ecs_cluster.main.name}/${aws_ecs_service.worker[0].name}"
  scalable_dimension = "ecs:service:DesiredCount"
  min_capacity       = var.worker_count
  max_capacity       = var.max_worker_count
}

resource "aws_appautoscaling_policy" "worker_cpu" {
  count              = length(aws_appautoscaling_target.worker)
  name               = "${var.project}-worker-cpu"
  policy_type        = "TargetTrackingScaling"
  service_namespace  = aws_appautoscaling_target.worker[0].service_namespace
  resource_id        = aws_appautoscaling_target.worker[0].resource_id
  scalable_dimension = aws_appautoscaling_target.worker[0].scalable_dimension

  target_tracking_scaling_policy_configuration {
    target_value = 60
    predefined_metric_specification {
      predefined_metric_type = "ECSServiceAverageCPUUtilization"
    }
  }
}

# Plain HTTP on port 80: the sandbox has no domain name, so no certificate. Production adds a
# certificate from ACM and an HTTPS listener.

resource "aws_lb" "api" {
  name               = "${var.project}-api"
  load_balancer_type = "application"
  subnets            = aws_subnet.public[*].id
  security_groups    = [aws_security_group.alb.id]

  enable_deletion_protection = false
}

# The gateway's tasks (s3-l14): every public request goes through the gateway, which forwards it to
# the monolith's tasks by their private addresses (service discovery, gateway.tf).
resource "aws_lb_target_group" "gateway" {
  name                 = "${var.project}-gateway"
  port                 = 8080
  protocol             = "HTTP"
  target_type          = "ip"
  vpc_id               = aws_vpc.main.id
  deregistration_delay = 30

  # The gateway's own check: it touches neither the monolith nor Valkey, so a slow monolith never has
  # the load balancer take every gateway task out at once.
  health_check {
    path                = "/gateway/health"
    matcher             = "200"
    interval            = 10
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.api.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.gateway.arn
  }
}

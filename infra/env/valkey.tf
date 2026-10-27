# Valkey 8 on ElastiCache for the timelines: one small node in the private subnets, reached from the
# tasks only. Timelines are derived data (rebuilt from Posts and Relationships on a miss), so the
# sandbox runs no replica; production adds one per zone and Multi-AZ failover.

resource "aws_security_group" "valkey" {
  name        = "${var.project}-valkey"
  description = "Valkey from the tasks only"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "valkey_from_tasks" {
  security_group_id            = aws_security_group.valkey.id
  referenced_security_group_id = aws_security_group.tasks.id
  from_port                    = 6379
  to_port                      = 6379
  ip_protocol                  = "tcp"
}

resource "aws_elasticache_subnet_group" "valkey" {
  name       = "${var.project}-valkey"
  subnet_ids = aws_subnet.private[*].id
}

resource "aws_elasticache_replication_group" "valkey" {
  replication_group_id       = "${var.project}-timelines"
  description                = "Timelines: sorted sets of post ids"
  engine                     = "valkey"
  engine_version             = "8.0"
  node_type                  = "cache.t4g.micro"
  num_cache_clusters         = 1
  subnet_group_name          = aws_elasticache_subnet_group.valkey.name
  security_group_ids         = [aws_security_group.valkey.id]
  transit_encryption_enabled = true
  at_rest_encryption_enabled = true
  apply_immediately          = true
}

resource "aws_secretsmanager_secret" "valkey" {
  name                    = "${var.project}/sandbox/valkey"
  recovery_window_in_days = 0
}

resource "aws_secretsmanager_secret_version" "valkey" {
  secret_id                = aws_secretsmanager_secret.valkey.id
  secret_string_wo         = "${aws_elasticache_replication_group.valkey.primary_endpoint_address}:6379,ssl=true,abortConnect=false"
  secret_string_wo_version = 1
}

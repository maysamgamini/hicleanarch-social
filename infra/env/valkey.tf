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

# The timeline store holds only timelines (s3-l08). A sorted set keeps the compact listpack encoding up
# to 1,024 entries, above the 802 a timeline reaches between an add and its trim; when memory runs out,
# the key closest to its expiry goes first (every timeline has one: the reader least recently seen).
resource "aws_elasticache_parameter_group" "timelines" {
  name   = "${var.project}-timelines"
  family = "valkey8"

  parameter {
    name  = "zset-max-listpack-entries"
    value = "1024"
  }

  parameter {
    name  = "maxmemory-policy"
    value = "volatile-ttl"
  }
}

resource "aws_elasticache_replication_group" "valkey" {
  replication_group_id       = "${var.project}-timelines"
  description                = "Timelines: sorted sets of post ids"
  engine                     = "valkey"
  engine_version             = "8.0"
  node_type                  = "cache.t4g.micro"
  parameter_group_name       = aws_elasticache_parameter_group.timelines.name
  num_cache_clusters         = 1
  subnet_group_name          = aws_elasticache_subnet_group.valkey.name
  security_group_ids         = [aws_security_group.valkey.id]
  transit_encryption_enabled = true
  at_rest_encryption_enabled = true
  apply_immediately          = true
}

# The short-lived store (s3-l08): locks, pull lists, the pulled-author registry, suggestion lists, rate
# limits. Apart from the timelines, so the timeline store's eviction never weighs one of these keys
# against a timeline. Default parameters: volatile-lru, and the registry (no expiry) is never evicted.
resource "aws_elasticache_replication_group" "short_lived" {
  replication_group_id       = "${var.project}-short-lived"
  description                = "Short-lived keys: locks, pull lists, registry, suggestion lists"
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

locals {
  valkey_address       = aws_elasticache_replication_group.valkey.primary_endpoint_address
  valkey_short_address = aws_elasticache_replication_group.short_lived.primary_endpoint_address
}

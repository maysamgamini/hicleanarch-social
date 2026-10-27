# Amazon Neptune for Recommendations' graph: serverless, in the private subnets, reached from the
# tasks only, over Bolt with TLS on 8182. The sandbox uses no IAM database authentication (the .NET
# driver's signed tokens expire in about five minutes; a token-renewing auth manager is the
# production change). Everything is destroyable. Engine 1.4: openCypher over Bolt, and the
# datetime functions the course does not need anyway.

resource "aws_security_group" "graph" {
  name        = "${var.project}-graph"
  description = "Neptune from the tasks only"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "graph_from_tasks" {
  security_group_id            = aws_security_group.graph.id
  referenced_security_group_id = aws_security_group.tasks.id
  from_port                    = 8182
  to_port                      = 8182
  ip_protocol                  = "tcp"
}

resource "aws_neptune_subnet_group" "graph" {
  name       = "${var.project}-graph"
  subnet_ids = aws_subnet.private[*].id
}

resource "aws_neptune_cluster" "graph" {
  cluster_identifier                  = "${var.project}-graph"
  engine                              = "neptune"
  engine_version                      = var.neptune_engine_version
  neptune_subnet_group_name           = aws_neptune_subnet_group.graph.name
  vpc_security_group_ids              = [aws_security_group.graph.id]
  iam_database_authentication_enabled = false
  storage_encrypted                   = true
  deletion_protection                 = false
  skip_final_snapshot                 = true
  apply_immediately                   = true

  serverless_v2_scaling_configuration {
    min_capacity = 1
    max_capacity = 2.5
  }
}

resource "aws_neptune_cluster_instance" "graph" {
  identifier         = "${var.project}-graph-writer"
  cluster_identifier = aws_neptune_cluster.graph.id
  instance_class     = "db.serverless"
  engine             = "neptune"
  apply_immediately  = true
}

# The suggestion walks run on a reader instance (s3-l12), so a burst of them never slows the
# projection's writes; the reader endpoint balances over the readers there are.
resource "aws_neptune_cluster_instance" "graph_reader" {
  count              = var.graph_readers
  identifier         = "${var.project}-graph-reader-${count.index}"
  cluster_identifier = aws_neptune_cluster.graph.id
  instance_class     = "db.serverless"
  engine             = "neptune"
  apply_immediately  = true
  depends_on         = [aws_neptune_cluster_instance.graph]
}

locals {
  graph_uri        = "bolt+s://${aws_neptune_cluster.graph.endpoint}:8182"
  graph_reader_uri = "bolt+s://${aws_neptune_cluster.graph.reader_endpoint}:8182"
}

# Aurora PostgreSQL 17, Serverless v2 at its smallest: it pauses at 0 capacity units after five
# idle minutes and resumes on the next connection. Everything is set so that destroy is complete.

resource "aws_db_subnet_group" "main" {
  name       = "${var.project}-sandbox"
  subnet_ids = aws_subnet.private[*].id
}

resource "aws_rds_cluster" "main" {
  cluster_identifier = "${var.project}-sandbox"
  engine             = "aurora-postgresql"
  engine_mode        = "provisioned"
  engine_version     = var.aurora_engine_version
  database_name      = "social"
  master_username    = "owner"
  # Write-only: the password goes to RDS and to the owner's secret, never to the state (secrets.tf).
  master_password_wo         = ephemeral.random_password.owner.result
  master_password_wo_version = 1
  db_subnet_group_name       = aws_db_subnet_group.main.name
  vpc_security_group_ids     = [aws_security_group.database.id]
  storage_encrypted          = true

  deletion_protection = false
  skip_final_snapshot = true
  apply_immediately   = true

  serverlessv2_scaling_configuration {
    min_capacity             = 0
    max_capacity             = 2
    seconds_until_auto_pause = 300
  }
}

resource "aws_rds_cluster_instance" "writer" {
  identifier          = "${var.project}-sandbox-writer"
  cluster_identifier  = aws_rds_cluster.main.id
  instance_class      = "db.serverless"
  engine              = aws_rds_cluster.main.engine
  engine_version      = aws_rds_cluster.main.engine_version
  apply_immediately   = true
  publicly_accessible = false
}

locals {
  db_host = aws_rds_cluster.main.endpoint
}

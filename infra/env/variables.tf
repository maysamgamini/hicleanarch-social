variable "region" {
  description = "AWS Region; the same as the bootstrap layer."
  type        = string
  default     = "us-east-1"
}

variable "project" {
  description = "Prefix for resource names."
  type        = string
  default     = "hicleanarch-social"
}

variable "image" {
  description = "The release: the API image the migration task runs, as repository URL and tag (<account>.dkr.ecr.<region>.amazonaws.com/hicleanarch-social/api:<git sha>)."
  type        = string
  default     = "public.ecr.aws/docker/library/nginx:stable" # only so that destroy needs no argument
}

variable "service_image" {
  description = "The image the API service runs: the current one until the release is migrated, then the release. Empty: no service yet (a new environment, before its first migration)."
  type        = string
  default     = ""
}

variable "zone_count" {
  description = "Availability Zones the subnets, the load balancer and the tasks span."
  type        = number
  default     = 2

  validation {
    condition     = var.zone_count >= 2 && var.zone_count <= 3
    error_message = "zone_count must be 2 or 3: Aurora and the load balancer need at least two."
  }
}

variable "desired_count" {
  description = "How many web tasks run at first; autoscaling owns the count afterwards."
  type        = number
  default     = 2
}

variable "worker_count" {
  description = "How many worker tasks run at first; autoscaling owns the count afterwards."
  type        = number
  default     = 1
}

variable "max_web_count" {
  description = "The most web tasks autoscaling may run."
  type        = number
  default     = 6
}

variable "max_worker_count" {
  description = "The most worker tasks autoscaling may run."
  type        = number
  default     = 4
}

variable "aurora_engine_version" {
  description = "Aurora PostgreSQL version; 17 is the engine the course targets."
  type        = string
  default     = "17.4"
}

variable "collector_image" {
  description = "AWS Distro for OpenTelemetry collector, run beside the program in each task."
  type        = string
  default     = "public.ecr.aws/aws-observability/aws-otel-collector:v0.49.0"
}

variable "neptune_engine_version" {
  description = "Neptune engine; 1.3.2.0 or later runs every query in Queries.cypher (the opt-in test checks it)."
  type        = string
  default     = "1.4.5.1"
}

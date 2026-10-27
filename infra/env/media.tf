# Images (s3-l10): a private S3 bucket, read by anyone only through CloudFront, which reaches it with
# origin access control; clients upload straight to S3 with a pre-signed PUT the program issues.

resource "aws_s3_bucket" "media" {
  bucket_prefix = "${var.project}-media-"
  force_destroy = true # the sandbox's images go with it
}

resource "aws_s3_bucket_public_access_block" "media" {
  bucket                  = aws_s3_bucket.media.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "media" {
  bucket = aws_s3_bucket.media.id
  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

# A browser's PUT to a pre-signed URL is a cross-origin request.
resource "aws_s3_bucket_cors_configuration" "media" {
  bucket = aws_s3_bucket.media.id
  cors_rule {
    allowed_methods = ["PUT"]
    allowed_origins = var.media_upload_origins
    allowed_headers = ["Content-Type"]
    max_age_seconds = 300
  }
}

# Unfinished multipart uploads are dropped after a day. A whole object uploaded but never put in a
# post is deleted by the program's media cleanup (CleanUpMedia, hourly), which knows which objects
# no post names; a lifecycle rule cannot tell.
resource "aws_s3_bucket_lifecycle_configuration" "media" {
  bucket = aws_s3_bucket.media.id
  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"
    filter {}
    abort_incomplete_multipart_upload {
      days_after_initiation = 1
    }
  }
}

resource "aws_cloudfront_origin_access_control" "media" {
  name                              = "${var.project}-media"
  origin_access_control_origin_type = "s3"
  signing_behavior                  = "always"
  signing_protocol                  = "sigv4"
}

resource "aws_cloudfront_distribution" "media" {
  enabled         = true
  comment         = "${var.project} images"
  price_class     = "PriceClass_100"
  is_ipv6_enabled = true

  origin {
    origin_id                = "media"
    domain_name              = aws_s3_bucket.media.bucket_regional_domain_name
    origin_access_control_id = aws_cloudfront_origin_access_control.media.id
  }

  default_cache_behavior {
    target_origin_id       = "media"
    viewer_protocol_policy = "redirect-to-https"
    allowed_methods        = ["GET", "HEAD"]
    cached_methods         = ["GET", "HEAD"]
    # The AWS managed CachingOptimized policy: an image never changes under its key.
    cache_policy_id = "658327ea-f89d-4fab-a63d-7e88639e58f6"
  }

  restrictions {
    geo_restriction {
      restriction_type = "none"
    }
  }

  viewer_certificate {
    cloudfront_default_certificate = true
  }
}

# Only this distribution reads the bucket.
data "aws_iam_policy_document" "media_from_cloudfront" {
  statement {
    actions   = ["s3:GetObject"]
    resources = ["${aws_s3_bucket.media.arn}/*"]
    principals {
      type        = "Service"
      identifiers = ["cloudfront.amazonaws.com"]
    }
    condition {
      test     = "StringEquals"
      variable = "AWS:SourceArn"
      values   = [aws_cloudfront_distribution.media.arn]
    }
  }
}

resource "aws_s3_bucket_policy" "media" {
  bucket = aws_s3_bucket.media.id
  policy = data.aws_iam_policy_document.media_from_cloudfront.json
}

# The program signs uploads with its task role, checks them with a HEAD, and deletes unattached ones; it never lists.
data "aws_iam_policy_document" "task_media" {
  statement {
    actions   = ["s3:PutObject", "s3:GetObject", "s3:DeleteObject"]
    resources = ["${aws_s3_bucket.media.arn}/media/*"]
  }
}

resource "aws_iam_role_policy" "task_media" {
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.task_media.json
}

locals {
  media_environment = [
    { name = "Media__Store", value = "S3" },
    { name = "Media__Bucket", value = aws_s3_bucket.media.bucket },
    { name = "Media__PublicBase", value = "https://${aws_cloudfront_distribution.media.domain_name}" },
  ]
}

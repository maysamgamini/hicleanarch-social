# GitHub Actions signs in to AWS with short-lived credentials (OpenID Connect): no access keys are
# stored in GitHub. Only workflows of this repository's main branch may take the role.
resource "aws_iam_openid_connect_provider" "github" {
  url            = "https://token.actions.githubusercontent.com"
  client_id_list = ["sts.amazonaws.com"]
}

data "aws_iam_policy_document" "github_assume" {
  statement {
    actions = ["sts:AssumeRoleWithWebIdentity"]
    principals {
      type        = "Federated"
      identifiers = [aws_iam_openid_connect_provider.github.arn]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:aud"
      values   = ["sts.amazonaws.com"]
    }
    condition {
      test     = "StringEquals"
      variable = "token.actions.githubusercontent.com:sub"
      values   = [local.deploy_subject]
    }
  }
}

resource "aws_iam_role" "deploy" {
  name               = "${var.project}-github-deploy"
  assume_role_policy = data.aws_iam_policy_document.github_assume.json
}

locals {
  # The one workflow identity that may take the deploy role.
  deploy_subject = "repo:${var.github_repository}:ref:refs/heads/main"

  account      = data.aws_caller_identity.current.account_id
  sandbox_role = "arn:aws:iam::${local.account}:role/${var.project}-sandbox-*"
}

# What the sandbox workflow does: apply and destroy infra/env, push the image, run the migration
# task, create a smoke-test user. Only the services infra/env uses, in the course's Region; only the
# project's own secrets, log groups, roles, image repository and state.
data "aws_iam_policy_document" "deploy" {
  statement {
    sid = "SandboxServices"
    actions = [
      "ec2:*", "elasticloadbalancing:*", "rds:*", "ecs:*", "cognito-idp:*", "elasticache:*", "neptune-db:*",
      "cloudwatch:*", "logs:DescribeLogGroups",
    ]
    resources = ["*"]
    condition {
      test     = "StringEquals"
      variable = "aws:RequestedRegion"
      values   = [var.region]
    }
  }
  statement {
    sid       = "ProjectSecrets"
    actions   = ["secretsmanager:*"]
    resources = ["arn:aws:secretsmanager:${var.region}:${local.account}:secret:${var.project}/*"]
  }
  statement {
    sid     = "ProjectLogGroups"
    actions = ["logs:*"]
    resources = [
      "arn:aws:logs:${var.region}:${local.account}:log-group:/${var.project}/*",
      "arn:aws:logs:${var.region}:${local.account}:log-group:/aws/ecs/application/metrics*",
    ]
  }
  statement {
    sid       = "ProjectRoles"
    actions   = ["iam:*Role*", "iam:PassRole"]
    resources = [local.sandbox_role]
  }
  statement {
    sid       = "ServiceLinkedRoles"
    actions   = ["iam:CreateServiceLinkedRole"]
    resources = ["*"]
  }
  statement {
    sid       = "PushImagesSignIn"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }
  statement {
    sid = "PushImages"
    actions = [
      "ecr:BatchCheckLayerAvailability", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload",
      "ecr:PutImage", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:DescribeImages",
    ]
    resources = [aws_ecr_repository.api.arn, aws_ecr_repository.gateway.arn]
  }
  statement {
    sid       = "StateBucket"
    actions   = ["s3:ListBucket"]
    resources = [aws_s3_bucket.state.arn]
  }
  statement {
    sid       = "StateObjects"
    actions   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"]
    resources = ["${aws_s3_bucket.state.arn}/env/*"]
  }
}

resource "aws_iam_role_policy" "deploy" {
  role   = aws_iam_role.deploy.id
  policy = data.aws_iam_policy_document.deploy.json
}

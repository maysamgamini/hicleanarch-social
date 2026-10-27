# Checked offline: a mock provider answers every call, so no AWS account or credentials are needed.
mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "123456789012"
    }
  }

  # A policy document's json must be JSON, which a mock's random string is not.
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }
}

run "the_state_bucket_is_named_after_the_account_versioned_and_private" {
  command = apply

  assert {
    condition     = aws_s3_bucket.state.bucket == "hicleanarch-social-tfstate-123456789012"
    error_message = "The state bucket's name must carry the account id, so it is unique."
  }

  assert {
    condition     = aws_s3_bucket_versioning.state.versioning_configuration[0].status == "Enabled"
    error_message = "State must be versioned: an overwritten state can be restored."
  }

  assert {
    condition = alltrue([
      aws_s3_bucket_public_access_block.state.block_public_acls,
      aws_s3_bucket_public_access_block.state.block_public_policy,
      aws_s3_bucket_public_access_block.state.ignore_public_acls,
      aws_s3_bucket_public_access_block.state.restrict_public_buckets,
    ])
    error_message = "The state bucket must block every kind of public access."
  }
}

run "everything_can_be_destroyed" {
  command = plan

  assert {
    condition     = aws_s3_bucket.state.force_destroy && aws_ecr_repository.api.force_delete
    error_message = "A learner removes the bootstrap layer with one destroy: the bucket and the repository must not refuse it."
  }
}

run "images_are_immutable_and_scanned" {
  command = plan

  assert {
    condition     = aws_ecr_repository.api.image_tag_mutability == "IMMUTABLE"
    error_message = "A tag must always name the same image, so a deploy is reproducible."
  }

  assert {
    condition     = aws_ecr_repository.api.image_scanning_configuration[0].scan_on_push
    error_message = "Every pushed image is scanned."
  }
}

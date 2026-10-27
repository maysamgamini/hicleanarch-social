# infra/bootstrap

Applied once per AWS account, by a learner, in their own account: the bucket that holds the
environment layer's state, the image repository, and the role GitHub Actions takes (through
OpenID Connect, no stored keys) to run the sandbox workflow. It keeps its own state locally.

Checked offline, with no account:

```bash
terraform fmt -check -recursive infra
terraform -chdir=infra/bootstrap init -backend=false
terraform -chdir=infra/bootstrap validate
terraform -chdir=infra/bootstrap test
```

Applying it (a learner exercise, in your own account):

```bash
terraform -chdir=infra/bootstrap init
terraform -chdir=infra/bootstrap apply -var github_repository=<you>/hicleanarch-social
```

Then set the repository variables `AWS_DEPLOY_ROLE_ARN` and `TF_STATE_BUCKET` from its outputs.

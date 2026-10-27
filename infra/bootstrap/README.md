# infra/bootstrap

Applied once per AWS account, by a learner, in their own account: the bucket that holds the
environment layer's state, and the image repository. It keeps its own state locally.

Checked offline, with no account:

```bash
terraform fmt -check -recursive infra
terraform -chdir=infra/bootstrap init -backend=false
terraform -chdir=infra/bootstrap validate
terraform -chdir=infra/bootstrap test
```

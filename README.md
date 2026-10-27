# hicleanarch-social

Reference code for the social network course on hicleanarch.com: a news feed with recommendations,
built as a modular monolith with clean architecture on .NET 10, then split into microservices. The
follow graph is projected into a graph database (Neo4j locally; Amazon Neptune with openCypher on AWS).

Each Builder lesson is a tag (`s2-l01`, `s2-l02`, …). Tags are append-only: a fix found later lands
as a new commit and is carried into the next tag.

## What you need

.NET 10 SDK and Docker. Nothing else: no cloud account, no package feed but nuget.org.

```bash
dotnet build Social.slnx
dotnet test Social.slnx
```

## Layout

```text
src/BuildingBlocks/   vendored from hicleanarch-crowdfunding s3-l16 (see PROVENANCE.md)
src/Host/             composes the modules; it holds no rules
src/Modules/          Accounts, Relationships, Posts, Timeline, Recommendations, Notifications
tests/                architecture, unit, integration and graph tests
infra/                OpenTofu, checked offline (fmt, validate, tofu test with mock providers, an apply against Moto)
scripts/              bb-diff.sh shows how the vendored BuildingBlocks differ from upstream
```

Every number in this repository's documents is labelled: a design estimate (with its arithmetic), a
local measurement (with its setup), or a learner exercise (run in the learner's own AWS account).

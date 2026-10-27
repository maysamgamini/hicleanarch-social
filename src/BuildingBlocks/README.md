# Social.BuildingBlocks

What every module of the social network shares, as three packages:

- `Social.BuildingBlocks.Domain`: `Result<T>`, `Error`.
- `Social.BuildingBlocks.Application`: commands, queries, handlers, the dispatcher, integration events, `[ReadsTheWriter]`.
- `Social.BuildingBlocks.Infrastructure`: the outbox and inbox, the database migrator, raw SQL helpers, idempotent requests, the read side, authentication, hosting roles, health, Valkey connections.

They began as a copy of the crowdfunding project's BuildingBlocks (PROVENANCE.md lists every change
since). Versions follow SemVer: a removed or renamed public type or member, or a changed signature or
behaviour a caller relies on, is a new major version; a new public type or member is a minor version;
a fix is a patch. Each module pins the version it was built and tested with.

Pack into the repository's local feed, which NuGet.config maps `Social.BuildingBlocks.*` to:

```bash
scripts/pack-buildingblocks.sh       # a developer's machine: a fresh prerelease each time
dotnet pack BuildingBlocks.slnx -o artifacts/packages   # CI and the Docker images: the release version
```

Publishing to AWS CodeArtifact is a learner exercise in their own account: a domain and repository,
`aws codeartifact login --tool dotnet`, and `dotnet nuget push` of the packages in artifacts/packages.

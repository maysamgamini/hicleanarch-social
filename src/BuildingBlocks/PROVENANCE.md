# Where the BuildingBlocks come from

The three projects in this folder are a vendored copy of the first course's BuildingBlocks
(decision `buildingblocks-reuse`). They are copied as source, not consumed as a package: the
`CrowdFunding.BuildingBlocks.*` packages are published only to the course's CodeArtifact domain,
which needs AWS credentials, and a learner must be able to clone and build with nothing but .NET
and Docker.

| | |
|---|---|
| Source repository | https://github.com/maysamgamini/hicleanarch-crowdfunding |
| Source path | `src/BuildingBlocks` |
| Tag | `s3-l16` |
| Commit | `44ad0a5261ac4c7e241c36b75aee7ccdf701ac7c` |
| Copied | 2026-10-27, into `s2-l00` of this repository |
| Licence | The source repository has no LICENSE file. Both repositories have the same owner, so the copy is allowed; a licence for learners who copy it is an open item for the owner (spec §14). |

`scripts/bb-diff.sh <upstream-tag>` maps the names back and shows every difference from any
upstream tag. Each later re-vendor is a commit of its own with a row below.

## Renamed everywhere

- Namespaces and project names `BuildingBlocks.*` → `Social.BuildingBlocks.*`.
- Meter and activity source names `CrowdFunding.Operations`, `CrowdFunding.Outbox`,
  `CrowdFunding.Events` → `Social.Operations`, `Social.Outbox`, `Social.Events`; the span tag
  `crowdfunding.module` → `social.module`.

## Files

| File | Status | Why |
|---|---|---|
| Domain/Error.cs | kept | |
| Domain/Result.cs | kept | |
| Domain/PublicId.cs | kept | Takes the prefix as an argument; this course's ids are plain UUID strings (decision `post-ids`), so no module uses it yet. |
| Application/Dispatcher.cs | kept | The hand-written dispatcher; no MediatR. |
| Application/Messages.cs | kept | |
| Application/Events.cs | kept | |
| Application/QueryCaching.cs | kept | |
| Application/ReadsTheWriter.cs | kept | |
| Application/TokenBuckets.cs | dropped | Crowdfunding's payment-provider budget. |
| Infrastructure/ErrorResults.cs | kept | Problem replies. |
| Infrastructure/IdempotentRequests.cs | kept | |
| Infrastructure/Operations.cs | changed | Meter name only. |
| Infrastructure/Caching/QueryCacheRegistration.cs | kept | |
| Infrastructure/Caching/ValkeyQueryCache.cs | kept | |
| Infrastructure/Events/EfOutbox.cs | kept | |
| Infrastructure/Events/EventBus.cs | kept | |
| Infrastructure/Events/EventRegistration.cs | kept | |
| Infrastructure/Events/ModuleEvents.cs | kept | |
| Infrastructure/Events/OutboxDispatcher.cs | changed | Meter, activity source and span tag names only. Dispatch already runs in the background: the commit interceptor puts the committed rows on an in-process channel that hosted workers drain, so no request waits for a handler. |
| Infrastructure/Events/OutboxModel.cs | kept | |
| Infrastructure/Hosting/GatewayClientAddress.cs | dropped | Belongs to crowdfunding's gateway; this course's gateway arrives in s3-l14. |
| Infrastructure/Hosting/GracefulStop.cs | kept | |
| Infrastructure/Hosting/HealthEndpoints.cs | kept | |
| Infrastructure/Hosting/ProxyHeaders.cs | kept | |
| Infrastructure/Identity/Authentication.cs | changed | The crowdfunding roles (`creator`, `backer`) and their policies removed: every signed-in user of a social network may do the same things. The deny-by-default fallback policy is kept. |
| Infrastructure/Identity/LocalTokens.cs | kept | |
| Infrastructure/Persistence/DatabaseBusy.cs | kept | |
| Infrastructure/Persistence/DatabaseMigrator.cs | kept | |
| Infrastructure/Persistence/DatabaseUnavailable.cs | kept | |
| Infrastructure/Persistence/IdempotencyKey.cs | kept | |
| Infrastructure/Persistence/IdempotencyKeyPurge.cs | kept | |
| Infrastructure/Persistence/ModuleDatabase.cs | kept | |
| Infrastructure/Persistence/ModuleTransaction.cs | kept | |
| Infrastructure/Persistence/ReadSide.cs | kept | |
| Infrastructure/Valkey/ValkeyRegistration.cs | changed | Registers the shared connection only; the token buckets are gone. |
| Infrastructure/Valkey/InProcessTokenBuckets.cs | dropped | Token buckets. |
| Infrastructure/Valkey/ValkeyTokenBuckets.cs | dropped | Token buckets. |
| Directory.Build.props, README.md | dropped | Packaging settings; this course packs its own copy in s3-l15. |

## Changes after the copy

| Tag | File | Change | Why |
|---|---|---|---|
| s2-l07 | Infrastructure/Persistence/RawSql.cs | added | SQL on a module context's connection and open transaction, for the statements that are rules (pair lock, guarded counter, keyset pages); first written in Relationships (s2-l04), shared once Posts needed it too. |
| s2-l08 | Infrastructure/Events/ModuleWithoutInbox.cs | added | A module whose handlers are idempotent by construction and that publishes nothing (Timeline before it has a schema) registers with the dispatcher without an outbox or an inbox (spec §6.1: "an inbox if a handler is not idempotent by itself"). |
| s2-l19 | Infrastructure/Events/OutboxDispatcher.cs | changed | `outbox.oldest_undelivered_age` reports one measurement per module, tagged `module`, so each module's outbox has its own alarm (the age over all modules is still kept and reported before the first sweep). |
| s3-l01 | Infrastructure/Hosting/HostRoles.cs | added | One image, two roles: a web task keeps no hosted service but the graceful stop; a worker polls the outboxes. |
| s3-l01 | Infrastructure/Events/EventBus.cs, Events/OutboxDispatcher.cs | changed | A bus that holds no committed event (web tasks); `OutboxOptions.PollCommitted` and `PollEvery`: a worker hands on every undelivered row every second, since other tasks committed them. |
| s3-l08 | Infrastructure/Valkey/ValkeyRegistration.cs | changed | `AddShortLivedValkey`: a second, keyed connection (`ConnectionStrings:ValkeyShort`) for short-lived keys, so the timeline store holds only timelines and can evict by TTL; it falls back to the shared connection when not set. |
| s3-l11 | Infrastructure/Persistence/ReadSide.cs | changed | `ReaderOf`: a module's reader connection is `ConnectionStrings:{module}Read`, or its own connection with `Database:ReaderHost` (Aurora's reader endpoint, same login), or its own connection. |
| s3-l15 | Directory.Build.props, README.md | added | The BuildingBlocks as packages: Social.BuildingBlocks.* 1.0.0 (SemVer), packed into the repository's local feed by scripts/pack-buildingblocks.sh and by CI; the modules reference the packages, not the projects. |

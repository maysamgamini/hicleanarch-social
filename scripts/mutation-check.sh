#!/usr/bin/env bash
# Removes each invariant's guard, one at a time, and checks that the invariant's named test fails.
# A guard whose removal leaves its test green is a test that would not catch the rule's loss.
#   scripts/mutation-check.sh            (needs Docker: the tests run on Testcontainers)
set -uo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
cd "$here"

# invariant # file # the guard, as written # what replaces it # the invariant's test
mutations=(
  "1#src/Modules/Relationships/Relationships.Domain/Pair.cs#if (Has(EdgeKind.Block, follower, followee) || Has(EdgeKind.Block, followee, follower))#if (follower == Guid.Empty)#Invariant_1_no_follow_across_a_block"
  "2#src/Modules/Timeline/Timeline.Application/ReadFeed.cs#return item.Author.Active && !hidden.Contains(author) && followed.Contains(author);#return true;#Invariant_2_a_shown_post_is_allowed"
  "3#src/Modules/Timeline/Timeline.Application/ReadFeed.cs#cursor is null || entry.Score < cursor.Score#cursor is null || entry.Score <= cursor.Score#Invariant_3_no_repeats_no_gaps"
  "4#src/Modules/Posts/Posts.Application/Posts.cs#await outbox.Add(new PostPublished(#await Task.CompletedTask; _ = (new PostPublished(#Invariant_4_a_stored_post_reaches_its_followers"
  "5#src/Modules/Timeline/Timeline.Infrastructure/ValkeyTimelines.cs#var score = TimelineRules.Score(postId);#var score = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();#Invariant_5_position_by_time_not_arrival"
  "6#src/Modules/Relationships/Relationships.Infrastructure/Persistence/SqlRelationshipStore.cs#(\"delta\", -1)#(\"delta\", 0)#Invariant_6_counts_equal_facts"
  "7#src/BuildingBlocks/Social.BuildingBlocks.Infrastructure/IdempotentRequests.cs#{ Length: > 0 and <= 100 } key ? key : null#{ Length: > 0 and <= 100 } key ? key + Guid.NewGuid() : null#Invariant_7_a_retry_is_not_a_second_post"
)

# Replaces the guard, as text, exactly once; fails if it is not there exactly once.
replace() {
  python3 - "$1" "$2" "$3" <<'PY'
import sys
path, old, new = sys.argv[1:4]
text = open(path).read()
if text.count(old) != 1:
    sys.exit(f"the guard occurs {text.count(old)} times in {path}")
open(path, "w").write(text.replace(old, new))
PY
}

caught=0
for entry in "${mutations[@]}"; do
  IFS='#' read -r number file old new test <<<"$entry"
  cp "$file" "$file.orig"
  if ! replace "$file" "$old" "$new"; then
    echo "invariant $number: the guard was not found in $file"; mv "$file.orig" "$file"; exit 2
  fi
  dotnet build tests/Integration.Tests -p:TreatWarningsAsErrors=false -v:q >/dev/null 2>&1
  if dotnet test tests/Integration.Tests --no-build --filter "FullyQualifiedName~InvariantTests.$test" >/dev/null 2>&1; then
    echo "invariant $number: guard removed, $test still PASSES: not caught"
  else
    echo "invariant $number: guard removed, $test FAILS: caught"
    caught=$((caught + 1))
  fi
  mv "$file.orig" "$file"
done

dotnet build tests/Integration.Tests -v:q >/dev/null 2>&1
echo "$caught of ${#mutations[@]} guards caught"
test "$caught" = "${#mutations[@]}"

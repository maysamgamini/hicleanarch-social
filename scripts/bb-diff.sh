#!/usr/bin/env bash
# Shows how the vendored BuildingBlocks differ from hicleanarch-crowdfunding at a tag.
#   scripts/bb-diff.sh <upstream-tag> [path to a clone of hicleanarch-crowdfunding]
# The vendored copy's names are mapped back to upstream's first, so only real changes show.
set -euo pipefail

tag=${1:?usage: scripts/bb-diff.sh <upstream-tag> [crowdfunding clone]}
upstream=${2:-../hicleanarch-crowdfunding}
here=$(cd "$(dirname "$0")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

git -C "$upstream" archive "$tag" src/BuildingBlocks | tar -x -C "$work"
mkdir -p "$work/ours"
for project in Domain Application Infrastructure; do
  cp -R "$here/src/BuildingBlocks/Social.BuildingBlocks.$project" "$work/ours/BuildingBlocks.$project"
  rm -rf "$work/ours/BuildingBlocks.$project/bin" "$work/ours/BuildingBlocks.$project/obj"
  mv "$work/ours/BuildingBlocks.$project/Social.BuildingBlocks.$project.csproj" \
     "$work/ours/BuildingBlocks.$project/BuildingBlocks.$project.csproj"
done
find "$work/ours" -type f \( -name '*.cs' -o -name '*.csproj' \) -exec sed -i.bak \
  -e 's/Social\.BuildingBlocks/BuildingBlocks/g' \
  -e 's/"Social\.Operations"/"CrowdFunding.Operations"/' \
  -e 's/"Social\.Outbox"/"CrowdFunding.Outbox"/' \
  -e 's/"Social\.Events"/"CrowdFunding.Events"/' \
  -e 's/"social\.module"/"crowdfunding.module"/' {} +
find "$work/ours" -name '*.bak' -delete

cd "$work"
diff -ru --exclude=bin --exclude=obj src/BuildingBlocks ours || true

#!/usr/bin/env bash
# Packs the BuildingBlocks into the local feed (artifacts/packages) under a fresh prerelease version
# (<version>-local.<UTC time>), and records that version in artifacts/packages/local-version.props, which
# Directory.Packages.props reads. A new version each time means NuGet never restores a stale copy from
# its package cache after the BuildingBlocks change. CI and the Docker images pack the release version
# into a clean feed instead.
#   scripts/pack-buildingblocks.sh
set -euo pipefail
cd "$(dirname "$0")/.."

base=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/BuildingBlocks/Directory.Build.props)
version="$base-local.$(date -u +%Y%m%d%H%M%S)"

mkdir -p artifacts/packages
rm -f artifacts/packages/Social.BuildingBlocks.*-local.*
dotnet pack BuildingBlocks.slnx -c Release -o artifacts/packages -p:Version="$version" -v:q
cat > artifacts/packages/local-version.props <<PROPS
<Project>
  <PropertyGroup>
    <BuildingBlocksVersion>$version</BuildingBlocksVersion>
  </PropertyGroup>
</Project>
PROPS
echo "Social.BuildingBlocks $version packed into artifacts/packages; the modules restore it."

#!/usr/bin/env bash
# Downloads the Tailwind CSS standalone CLI (one binary, no Node) into
# tools/tailwind/bin/, checked against the release's published SHA-256.
#
# The web project's build runs it when it is there (see TailwindBuild in
# src/Roster.Web/Roster.Web.csproj) to recompile wwwroot/css/app.css from
# Styles/app.css and the classes used in the .razor and .cs files. Without it
# the build uses the committed app.css, so only someone changing classes needs
# this.
set -euo pipefail

VERSION="4.3.3"
here="$(cd "$(dirname "$0")" && pwd)"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)  asset="tailwindcss-linux-x64";   sum="dc61b3ac6b8c9ca874c0cc4c57b2409791a64c5540404ca5f5367360babc313a" ;;
  Linux-aarch64) asset="tailwindcss-linux-arm64"; sum="55fd0b241214eff3de1e8ee4f22796662f2d2e7a49bcfca7477cfd0bac398195" ;;
  Darwin-arm64)  asset="tailwindcss-macos-arm64"; sum="cdf646702987a743464dff4d9c60fd4480d1c1e73dd819a9a67f1078815dce9d" ;;
  Darwin-x86_64) asset="tailwindcss-macos-x64";   sum="7922e0953f2110c05976e3bf58f14e643d90427575e766b7d433f5f80cbee7e1" ;;
  *) echo "No Tailwind CLI build for $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac

mkdir -p "$here/bin"
tmp="$(mktemp "$here/bin/.download.XXXXXX")"
trap 'rm -f "$tmp"' EXIT
curl -fsSL -o "$tmp" "https://github.com/tailwindlabs/tailwindcss/releases/download/v$VERSION/$asset"
echo "$sum  $tmp" | sha256sum -c --quiet -
chmod +x "$tmp"
mv "$tmp" "$here/bin/tailwindcss"
trap - EXIT
"$here/bin/tailwindcss" --help | head -1

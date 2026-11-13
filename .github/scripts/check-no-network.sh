#!/usr/bin/env bash
# Fails if shipped source code (src/) references networking APIs.
# Mirrors tests/PbiTwin.Core.Tests/Guards/NoNetworkGuard.cs so the check is visible as its own CI job.
set -euo pipefail

cd "$(dirname "$0")/../.."

patterns=(
  '\bSystem\.Net\b'
  'HttpClient'
  'WebClient'
  'WebRequest'
  'Socket'
  'TcpClient|UdpClient|TcpListener'
  '\bDns\b'
  'Process\.Start\b.*([Hh][Tt][Tt][Pp][Ss]?:|://|[Ww][Ww][Ww]\.)'
  'NavigateUri[[:space:]]*='
)

includes=(--include='*.cs' --include='*.xaml' --include='*.csproj' --include='*.props' --include='*.targets')
excludes=(--exclude-dir=bin --exclude-dir=obj)

found=0
for p in "${patterns[@]}"; do
  if grep -rnP "${includes[@]}" "${excludes[@]}" -e "$p" src/; then
    echo "::error::Forbidden network pattern found: $p"
    found=1
  fi
done

if ! grep -q '<Using Remove="System.Net.Http" />' Directory.Build.targets; then
  echo "::error::Directory.Build.targets must remove the implicit System.Net.Http using."
  found=1
fi

if [ "$found" -ne 0 ]; then
  echo "No-network check FAILED."
  exit 1
fi
echo "No-network check passed: no networking APIs in src/."

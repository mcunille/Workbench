#!/usr/bin/env bash
# Run on Linux with .NET 10 and strace; traces remain in the printed temporary directory.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
echo "Storage durability evidence: $work"
mkdir "$work/probe"
cp "$repo/tests/Workbench.StorageDurabilityTests/Program.cs" "$work/probe/"
for source in BlobObjectId BlobTransfer BlobMaintenance FileSystemBlobStore ConfinedDirectory; do
    cp "$repo/src/Workbench.Server/Storage/$source.cs" "$work/probe/"
done
cat > "$work/probe/Probe.csproj" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
XML
dotnet build "$work/probe/Probe.csproj" -o "$work/bin" --nologo >/dev/null
probe=(dotnet "$work/bin/Probe.dll")
trace() {
    local label="$1" root="$2" action="$3" expected="$4"
    shift 4
    local result=0
    strace -f -qq -yy -e trace=fsync,renameat2,unlinkat "$@" -o "$work/$label.trace" \
        "${probe[@]}" "$root" "$action" > "$work/$label.out" || result=$?
    if [[ "$result" != "$expected" ]]; then
        echo "$label: expected exit $expected, got $result"; cat "$work/$label.trace"; exit 1
    fi
    if [[ "$expected" == 0 ]]; then grep -qx ACK "$work/$label.out"; else grep -qx IO_FAILURE "$work/$label.out"; fi
}
directory_synced() {
    if ! grep -E "fsync\([0-9]+<$2>\) += 0" "$work/$1.trace" >/dev/null; then
        echo "$1: directory fsync missing"; cat "$work/$1.trace"; exit 1
    fi
}
# GIVEN a new object, WHEN staged and published, THEN each rename precedes a directory fsync.
root="$work/normal"; mkdir "$root"
for action in stage publish; do
    trace "$action" "$root" "$action" 0
    directory_synced "$action" "$root"
    awk '/renameat2.*= 0/ { renamed=1 } /fsync.*= 0/ { if (renamed) synced=1 } END { exit !synced }' "$work/$action.trace"
done
# GIVEN a file already flushed, WHEN staging's directory fsync fails, THEN staging cannot acknowledge.
failed="$work/failed-stage"; mkdir "$failed"
trace stage-error "$failed" stage 42 -e inject=fsync:error=EIO:when=2
grep -E "fsync\([0-9]+<$failed>\).*EIO.*INJECTED" "$work/stage-error.trace" >/dev/null
# GIVEN a staged object, WHEN final publication or its retry cannot sync, THEN neither acknowledges.
retry="$work/retry"; mkdir "$retry"
"${probe[@]}" "$retry" stage >/dev/null
trace publish-error "$retry" publish 42 -e inject=fsync:error=EIO:when=1
trace retry-error "$retry" publish 42 -e inject=fsync:error=EIO:when=1
trace retry-success "$retry" publish 0
directory_synced retry-success "$retry"
# GIVEN an already copied snapshot blob, WHEN a retry cannot sync its destination, THEN copying cannot acknowledge.
trace copy-error "$retry" copy-existing 42 -e inject=fsync:error=EIO:when=1
trace copy-retry "$retry" copy-existing 0
directory_synced copy-retry "$retry"
# GIVEN an interrupted directory sync, WHEN retried, THEN publication completes only after success.
trace interrupted "$retry" publish 0 -e inject=fsync:error=EINTR:when=1
grep 'EINTR.*INJECTED' "$work/interrupted.trace" >/dev/null
directory_synced interrupted "$retry"
# GIVEN an existing blob, WHEN deletion cannot sync, THEN failure propagates and an absent-file retry still syncs.
trace delete-error "$retry" delete 42 -e inject=fsync:error=EIO:when=1
trace delete-retry "$retry" delete 0
directory_synced delete-retry "$retry"
echo 'PASS: rename persistence, stage/publication/delete failures, retries, and EINTR.'

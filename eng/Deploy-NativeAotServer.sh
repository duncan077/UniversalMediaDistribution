#!/usr/bin/env bash
set -Eeuo pipefail

umask 077

archive_path=""
deployment_id=""
health_url="http://127.0.0.1:9004/health"
remote_root=""
# Portable RID. Distro-specific ones such as ubuntu.24.04-arm64 come from the
# old RID graph and the .NET 11 SDK rejects them outright with NETSDK1083.
runtime_identifier="linux-arm64"
service_name="umd-server"
version=""

fail() {
    printf 'Server deployment failed: %s\n' "$*" >&2
    exit 1
}

require_value() {
    [[ $# -ge 2 && -n "$2" ]] ||
        fail "Missing value for $1."
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --archive)
            require_value "$@"
            archive_path="$2"
            shift 2
            ;;
        --deployment-id)
            require_value "$@"
            deployment_id="$2"
            shift 2
            ;;
        --health-url)
            require_value "$@"
            health_url="$2"
            shift 2
            ;;
        --remote-root)
            require_value "$@"
            remote_root="$2"
            shift 2
            ;;
        --runtime)
            require_value "$@"
            runtime_identifier="$2"
            shift 2
            ;;
        --service)
            require_value "$@"
            service_name="$2"
            shift 2
            ;;
        --version)
            require_value "$@"
            version="$2"
            shift 2
            ;;
        *)
            fail "Unknown argument '$1'."
            ;;
    esac
done

[[ "$deployment_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$ ]] ||
    fail "The deployment identifier is invalid."
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([+-][0-9A-Za-z.-]+)?$ ]] ||
    fail "The release version is invalid."
[[ "$runtime_identifier" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$ ]] ||
    fail "The server runtime identifier is invalid."
[[ "$service_name" =~ ^[A-Za-z0-9][A-Za-z0-9@._-]{0,63}$ ]] ||
    fail "The systemd service name is invalid."
[[ "$remote_root" == /* && "$remote_root" != *"/../"* ]] ||
    fail "The remote root must be an absolute path without parent traversal."
[[ "$archive_path" == "$remote_root"/incoming/* ]] ||
    fail "The source archive must be inside the deployment incoming directory."
[[ "$health_url" =~ ^http://127\.0\.0\.1:[0-9]+/[A-Za-z0-9/_-]+$ ]] ||
    fail "The health URL must use loopback HTTP."
[[ -f "$archive_path" ]] ||
    fail "The source archive does not exist."

source_directory="$remote_root/source/$deployment_id"
release_directory="$remote_root/releases/$deployment_id"
log_directory="$remote_root/logs"
native_library_directory="$remote_root/native-libs"
build_log="$log_directory/build-$deployment_id.log"
current_link="$remote_root/current"
next_link="$remote_root/current.next-$deployment_id"

[[ ! -e "$source_directory" && ! -L "$source_directory" ]] ||
    fail "The source directory already exists for '$deployment_id'."
[[ ! -e "$release_directory" && ! -L "$release_directory" ]] ||
    fail "The release directory already exists for '$deployment_id'."
[[ ! -e "$next_link" && ! -L "$next_link" ]] ||
    fail "The temporary current link already exists."
[[ ! -e "$current_link" || -L "$current_link" ]] ||
    fail "The current deployment path is not a symbolic link."

mkdir -p \
    "$source_directory" \
    "$release_directory" \
    "$log_directory" \
    "$native_library_directory" \
    "$remote_root/tmp"

exec > >(tee -a "$build_log") 2>&1

printf 'Building UMD server deployment %s.\n' "$deployment_id"

while IFS= read -r archive_entry; do
    [[ "$archive_entry" != /* ]] ||
        fail "The source archive contains an absolute path."
    case "/$archive_entry/" in
        *"/../"*)
            fail "The source archive contains parent traversal."
            ;;
    esac
done < <(tar -tzf "$archive_path")

tar -xzf "$archive_path" -C "$source_directory"

server_project="$source_directory/src/UniversalMediaDistribution.Server/UniversalMediaDistribution.Server.csproj"
[[ -f "$server_project" ]] ||
    fail "The source archive does not contain the server project."

link_native_library() {
    local link_name="$1"
    local system_path="$2"
    local destination="$native_library_directory/$link_name"

    [[ -e "$system_path" ]] ||
        fail "Required native library '$system_path' is unavailable."
    if [[ ! -e "$destination" && ! -L "$destination" ]]; then
        ln -s "$system_path" "$destination"
    fi
}

link_native_library \
    "libz.so" \
    "/lib/aarch64-linux-gnu/libz.so.1"
link_native_library \
    "libbrotlienc.so" \
    "/lib/aarch64-linux-gnu/libbrotlienc.so.1"
link_native_library \
    "libbrotlidec.so" \
    "/lib/aarch64-linux-gnu/libbrotlidec.so.1"
link_native_library \
    "libbrotlicommon.so" \
    "/lib/aarch64-linux-gnu/libbrotlicommon.so.1"

export LIBRARY_PATH="$native_library_directory${LIBRARY_PATH:+:$LIBRARY_PATH}"

dotnet restore \
    "$server_project" \
    --runtime "$runtime_identifier" \
    -p:NuGetAudit=false
dotnet publish \
    "$server_project" \
    --configuration Release \
    --runtime "$runtime_identifier" \
    --self-contained true \
    --no-restore \
    --output "$release_directory" \
    -p:Version="$version" \
    -p:NuGetAudit=false \
    -p:DebugType=None \
    -p:DebugSymbols=false

server_binary="$release_directory/UniversalMediaDistribution.Server"
[[ -x "$server_binary" ]] ||
    fail "The NativeAOT server executable was not produced."
readelf -h "$server_binary" |
    grep -q "AArch64" ||
    fail "The server executable is not an AArch64 binary."

smoke_directory="$remote_root/tmp/smoke-$deployment_id"
[[ ! -e "$smoke_directory" ]] ||
    fail "The smoke-test directory already exists."
mkdir -p "$smoke_directory/data"
smoke_log="$smoke_directory/server.log"
smoke_pid=""

stop_smoke_server() {
    if [[ -n "$smoke_pid" ]] && kill -0 "$smoke_pid" 2>/dev/null; then
        kill "$smoke_pid" 2>/dev/null || true
        wait "$smoke_pid" 2>/dev/null || true
    fi
}
trap stop_smoke_server EXIT

ASPNETCORE_URLS="http://127.0.0.1:19004" \
UMD_SERVER_DATA_DIRECTORY="$smoke_directory/data" \
UMD_SERVER_ALLOW_REGISTRATION="false" \
    "$server_binary" >"$smoke_log" 2>&1 &
smoke_pid=$!

smoke_ready=false
for _ in {1..40}; do
    if curl \
        --fail \
        --silent \
        --show-error \
        --max-time 2 \
        "http://127.0.0.1:19004/health" >/dev/null; then
        smoke_ready=true
        break
    fi
    kill -0 "$smoke_pid" 2>/dev/null ||
        break
    sleep 0.25
done
[[ "$smoke_ready" == true ]] || {
    cat "$smoke_log" >&2
    fail "The new server failed its isolated health check."
}
stop_smoke_server
smoke_pid=""
rm -rf -- "$smoke_directory"

previous_release=""
if [[ -L "$current_link" ]]; then
    previous_release="$(readlink -f "$current_link")"
fi

ln -s "$release_directory" "$next_link"
mv -Tf "$next_link" "$current_link"
systemctl --user restart "$service_name"

service_ready=false
for _ in {1..40}; do
    if curl \
        --fail \
        --silent \
        --show-error \
        --max-time 2 \
        "$health_url" >/dev/null; then
        service_ready=true
        break
    fi
    sleep 0.5
done

if [[ "$service_ready" != true ]]; then
    printf 'The deployed service did not become healthy; rolling back.\n' >&2
    if [[ -n "$previous_release" && -d "$previous_release" ]]; then
        rollback_link="$remote_root/current.rollback-$deployment_id"
        ln -s "$previous_release" "$rollback_link"
        mv -Tf "$rollback_link" "$current_link"
        systemctl --user restart "$service_name"
    fi
    fail "The production health check failed."
fi

printf 'SERVER_DEPLOYMENT_ID=%s\n' "$deployment_id"
printf 'SERVER_RELEASE_DIRECTORY=%s\n' "$release_directory"
printf 'SERVER_PREVIOUS_RELEASE=%s\n' "$previous_release"
printf 'SERVER_DEPLOYMENT_COMPLETE\n'

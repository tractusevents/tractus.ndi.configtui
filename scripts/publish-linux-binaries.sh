#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"

PROJECT="$PROJECT_ROOT/Tractus.Ndi.ConfigTui.csproj"
PUBLISH_ROOT="${PUBLISH_ROOT:-$PROJECT_ROOT}"
CONFIGURATION="${CONFIGURATION:-Release}"
BINARY_NAME="ndi-config-tui"
RIDS=("linux-x64" "linux-arm64")
RESTORE_ARGS=()

publish_dir_for_rid() {
    case "$1" in
        linux-x64) printf '%s\n' "$PUBLISH_ROOT/publish_accessmgr_x64" ;;
        linux-arm64) printf '%s\n' "$PUBLISH_ROOT/publish_accessmgr_aarch64" ;;
    esac
}

host_machine() {
    uname -m
}

is_arm64_host() {
    case "$(host_machine)" in
        aarch64|arm64) return 0 ;;
        *) return 1 ;;
    esac
}

usage() {
    cat <<USAGE
Usage: $0 [options]

Build NativeAOT single-binary Linux releases for x86-64 and AArch64.

Options:
  -o, --output DIR          Output root (default: project root)
  -c, --configuration NAME  Build configuration (default: Release)
      --rid RID            Build one runtime identifier instead of both
      --no-restore         Pass --no-restore to dotnet publish
  -h, --help               Show this help

Environment:
  PUBLISH_ROOT             Alternative output root
  CONFIGURATION            Alternative build configuration
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -o|--output)
            PUBLISH_ROOT="$2"
            shift 2
            ;;
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        --rid)
            RIDS=("$2")
            shift 2
            ;;
        --no-restore)
            RESTORE_ARGS=(--no-restore)
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet was not found on PATH." >&2
    exit 1
fi

mkdir -p "$PUBLISH_ROOT"

for rid in "${RIDS[@]}"; do
    case "$rid" in
        linux-x64|linux-arm64) ;;
        *)
            echo "Unsupported RID: $rid. Expected linux-x64 or linux-arm64." >&2
            exit 2
            ;;
    esac

    publish_dir="$(publish_dir_for_rid "$rid")"

    rm -rf "$publish_dir"
    mkdir -p "$publish_dir"

    echo "Publishing $rid..."
    extra_publish_args=()
    if [[ "$rid" == "linux-arm64" ]] && ! is_arm64_host; then
        if command -v clang >/dev/null 2>&1; then
            extra_publish_args=(-p:CppCompilerAndLinker=clang)
        else
            cat >&2 <<'ERROR'
linux-arm64 NativeAOT cross-publish needs clang on this x86-64 host.

Install clang/lld and rerun this script, or run the script on an AArch64
Linux builder. Debian/Ubuntu example:

  sudo apt install clang lld zlib1g-dev

ERROR
            exit 1
        fi
    fi

    dotnet publish "$PROJECT" \
        -c "$CONFIGURATION" \
        -r "$rid" \
        --self-contained true \
        -p:PublishAot=true \
        -p:StripSymbols=true \
        -p:NativeDebugSymbols=false \
        -p:CopyOutputSymbolsToPublishDirectory=false \
        -o "$publish_dir" \
        "${extra_publish_args[@]}" \
        "${RESTORE_ARGS[@]}"

    if [[ ! -x "$publish_dir/$BINARY_NAME" ]]; then
        echo "Expected executable was not produced: $publish_dir/$BINARY_NAME" >&2
        exit 1
    fi
done

echo
echo "Release binaries:"
for rid in "${RIDS[@]}"; do
    publish_dir="$(publish_dir_for_rid "$rid")"
    ls -lh "$publish_dir/$BINARY_NAME"
done

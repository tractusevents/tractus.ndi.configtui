#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/.." >/dev/null 2>&1 && pwd)"
PROJECT="$PROJECT_ROOT/Tractus.Ndi.ConfigTui.csproj"
PUBLISH_ROOT="${PUBLISH_ROOT:-$PROJECT_ROOT}"
CONFIGURATION="${CONFIGURATION:-Release}"
BINARY_NAME="ndi-config-tui"
INSTALL_BINARY_NAME="ndi-config"
RID=""
OUTPUT=""
PUBLISH=true

usage() {
    cat <<USAGE
Usage: $0 [options]

Build a self-extracting installer with the ndi-config payload appended.

Options:
      --rid RID              Runtime identifier: linux-x64 or linux-arm64
  -o, --output FILE          Installer output path (default: artifacts/install-ndi-config-tui-<rid>.sh)
  -c, --configuration NAME   Build configuration (default: Release)
      --publish-root DIR     Publish output root (default: project root)
      --no-publish           Reuse an existing published binary
  -h, --help                 Show this help

Environment:
  PUBLISH_ROOT               Alternative publish output root
  CONFIGURATION              Alternative build configuration
USAGE
}

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "$1 was not found on PATH." >&2
        exit 1
    fi
}

default_rid() {
    case "$(uname -m)" in
        x86_64|amd64) printf '%s\n' linux-x64 ;;
        aarch64|arm64) printf '%s\n' linux-arm64 ;;
        *)
            echo "Could not determine default RID for machine: $(uname -m)" >&2
            echo "Pass --rid linux-x64 or --rid linux-arm64." >&2
            exit 2
            ;;
    esac
}

publish_dir_for_rid() {
    case "$1" in
        linux-x64) printf '%s\n' "$PUBLISH_ROOT/publish_accessmgr_x64" ;;
        linux-arm64) printf '%s\n' "$PUBLISH_ROOT/publish_accessmgr_aarch64" ;;
    esac
}

read_project_version() {
    sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | sed -n '1p'
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)
            RID="$2"
            shift 2
            ;;
        -o|--output)
            OUTPUT="$2"
            shift 2
            ;;
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        --publish-root)
            PUBLISH_ROOT="$2"
            shift 2
            ;;
        --no-publish)
            PUBLISH=false
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

RID="${RID:-$(default_rid)}"
case "$RID" in
    linux-x64|linux-arm64) ;;
    *)
        echo "Unsupported RID: $RID. Expected linux-x64 or linux-arm64." >&2
        exit 2
        ;;
esac

if [[ -z "$OUTPUT" ]]; then
    OUTPUT="$PROJECT_ROOT/artifacts/install-ndi-config-tui-$RID.sh"
fi

require_command sed
require_command tar
require_command gzip
require_command mktemp

version="$(read_project_version)"
if [[ -z "$version" ]]; then
    echo "Could not read <Version> from $PROJECT" >&2
    exit 1
fi

if [[ "$PUBLISH" == true ]]; then
    "$SCRIPT_DIR/publish-linux-binaries.sh" \
        --rid "$RID" \
        --configuration "$CONFIGURATION" \
        --output "$PUBLISH_ROOT"
fi

publish_dir="$(publish_dir_for_rid "$RID")"
binary="$publish_dir/$BINARY_NAME"
if [[ ! -x "$binary" ]]; then
    echo "Missing published binary: $binary" >&2
    echo "Run scripts/publish-linux-binaries.sh first, or omit --no-publish." >&2
    exit 1
fi

tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT
payload_dir="$tmpdir/payload"
mkdir -p "$payload_dir"
install -m 0755 "$binary" "$payload_dir/$INSTALL_BINARY_NAME"
printf '%s\n' "$version" > "$payload_dir/VERSION"
cat > "$payload_dir/LICENSE.txt" <<'LICENSE'
End User License Agreement ("EULA") for Config Editor for NDI | Tractus Events

IMPORTANT - READ CAREFULLY: This End User License Agreement ("EULA") is a legal agreement between you (either an individual or a single entity) and Tractus Events for the use of the Config Editor for NDI software product, which includes computer software and may include associated media, printed materials, and "online" or electronic documentation ("SOFTWARE PRODUCT").

By installing, copying, or otherwise using the SOFTWARE PRODUCT, you agree to be bound by the terms of this EULA. If you do not agree to the terms of this EULA, do not install or use the SOFTWARE PRODUCT.

SOFTWARE PRODUCT LICENSE

GRANT OF LICENSE
Tractus Events grants you a non-exclusive, non-transferable, royalty-free license to use the SOFTWARE PRODUCT on an unlimited number of computers.

RESTRICTIONS
You may not install, preload, bundle, distribute, or otherwise make available the SOFTWARE PRODUCT on any device, system, computer, media server, appliance, or other equipment that is offered for sale, resale, lease, rental, dry rental, or hire, or that is otherwise supplied to a third party as part of a commercial equipment rental or resale offering, without prior written permission from Tractus Events.

OWNERSHIP
Tractus Events retains all right, title, and interest in and to the SOFTWARE PRODUCT, including all intellectual property rights. The SOFTWARE PRODUCT is protected by copyright laws and international copyright treaties, as well as other intellectual property laws and treaties.

PRIVACY POLICY
The privacy policy for the SOFTWARE PRODUCT is available at https://www.tractusevents.com/privacy.

WARRANTY DISCLAIMER
The SOFTWARE PRODUCT is provided "AS IS" without warranty of any kind, either express or implied, including, but not limited to, the implied warranties of merchantability and fitness for a particular purpose. Tractus Events does not warrant that the SOFTWARE PRODUCT will meet your requirements or that the operation of the SOFTWARE PRODUCT will be uninterrupted or error-free.

LIMITATION OF LIABILITY
In no event shall Tractus Events be liable for any special, incidental, indirect, or consequential damages whatsoever (including, without limitation, damages for loss of business profits, business interruption, loss of business information, or any other pecuniary loss) arising out of the use of or inability to use the SOFTWARE PRODUCT, even if Tractus Events has been advised of the possibility of such damages.

TERMINATION
This EULA is effective until terminated. Your rights under this EULA will terminate automatically without notice from Tractus Events if you fail to comply with any of the terms and conditions of this EULA. Upon termination, you must immediately cease all use of the SOFTWARE PRODUCT and destroy all copies of the SOFTWARE PRODUCT.

MISCELLANEOUS
This EULA represents the entire agreement between you and Tractus Events relating to the SOFTWARE PRODUCT and supersedes all prior or contemporaneous oral or written communications, proposals, and representations with respect to the SOFTWARE PRODUCT or any other subject matter covered by this EULA. This EULA may not be modified except in writing signed by both you and Tractus Events. If any provision of this EULA is held to be void, invalid, unenforceable, or illegal, the other provisions shall continue in full force and effect. This EULA shall be governed by and construed in accordance with the laws of the Province of Ontario, Canada, without giving effect to any principles of conflicts of law.

End of license.
LICENSE

mkdir -p "$(dirname -- "$OUTPUT")"
cat > "$OUTPUT" <<'INSTALLER_STUB'
#!/usr/bin/env bash
set -euo pipefail

PRODUCT_NAME="Config Editor for NDI | Tractus Events"
PRODUCT_VERSION="@PRODUCT_VERSION@"
PAYLOAD_RID="@PAYLOAD_RID@"
PRODUCT_TITLE="$PRODUCT_NAME ($PAYLOAD_RID)"
INSTALL_DIR="/opt/tractus/ndi-config"
LINK_PATH="/usr/local/bin/ndi-config"
LOCAL_DIR="./ndi-config"
BINARY_NAME="ndi-config"
ACCEPT_LICENSE=false
FORCE_ARCHITECTURE=false

usage() {
    cat <<USAGE
Usage: $0 [options]

Install $PRODUCT_TITLE.

Options:
      --accept-license      Accept the EULA without prompting
      --force-architecture  Install even if this machine does not match $PAYLOAD_RID
  -h, --help                Show this help

Root install:
  sudo $0
  Installs to $INSTALL_DIR and creates $LINK_PATH.

User install:
  $0
  Extracts to $LOCAL_DIR.
USAGE
}

show_license() {
    cat <<'LICENSE'
End User License Agreement ("EULA") for Config Editor for NDI | Tractus Events

IMPORTANT - READ CAREFULLY: This End User License Agreement ("EULA") is a legal agreement between you (either an individual or a single entity) and Tractus Events for the use of the Config Editor for NDI software product, which includes computer software and may include associated media, printed materials, and "online" or electronic documentation ("SOFTWARE PRODUCT").

By installing, copying, or otherwise using the SOFTWARE PRODUCT, you agree to be bound by the terms of this EULA. If you do not agree to the terms of this EULA, do not install or use the SOFTWARE PRODUCT.

SOFTWARE PRODUCT LICENSE

GRANT OF LICENSE
Tractus Events grants you a non-exclusive, non-transferable, royalty-free license to use the SOFTWARE PRODUCT on an unlimited number of computers.

RESTRICTIONS
You may not install, preload, bundle, distribute, or otherwise make available the SOFTWARE PRODUCT on any device, system, computer, media server, appliance, or other equipment that is offered for sale, resale, lease, rental, dry rental, or hire, or that is otherwise supplied to a third party as part of a commercial equipment rental or resale offering, without prior written permission from Tractus Events.

OWNERSHIP
Tractus Events retains all right, title, and interest in and to the SOFTWARE PRODUCT, including all intellectual property rights. The SOFTWARE PRODUCT is protected by copyright laws and international copyright treaties, as well as other intellectual property laws and treaties.

PRIVACY POLICY
The privacy policy for the SOFTWARE PRODUCT is available at https://www.tractusevents.com/privacy.

WARRANTY DISCLAIMER
The SOFTWARE PRODUCT is provided "AS IS" without warranty of any kind, either express or implied, including, but not limited to, the implied warranties of merchantability and fitness for a particular purpose. Tractus Events does not warrant that the SOFTWARE PRODUCT will meet your requirements or that the operation of the SOFTWARE PRODUCT will be uninterrupted or error-free.

LIMITATION OF LIABILITY
In no event shall Tractus Events be liable for any special, incidental, indirect, or consequential damages whatsoever (including, without limitation, damages for loss of business profits, business interruption, loss of business information, or any other pecuniary loss) arising out of the use of or inability to use the SOFTWARE PRODUCT, even if Tractus Events has been advised of the possibility of such damages.

TERMINATION
This EULA is effective until terminated. Your rights under this EULA will terminate automatically without notice from Tractus Events if you fail to comply with any of the terms and conditions of this EULA. Upon termination, you must immediately cease all use of the SOFTWARE PRODUCT and destroy all copies of the SOFTWARE PRODUCT.

MISCELLANEOUS
This EULA represents the entire agreement between you and Tractus Events relating to the SOFTWARE PRODUCT and supersedes all prior or contemporaneous oral or written communications, proposals, and representations with respect to the SOFTWARE PRODUCT or any other subject matter covered by this EULA. This EULA may not be modified except in writing signed by both you and Tractus Events. If any provision of this EULA is held to be void, invalid, unenforceable, or illegal, the other provisions shall continue in full force and effect. This EULA shall be governed by and construed in accordance with the laws of the Province of Ontario, Canada, without giving effect to any principles of conflicts of law.

End of license.
LICENSE
}

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "$1 was not found on PATH." >&2
        exit 1
    fi
}

check_architecture() {
    machine="$(uname -m)"
    case "$PAYLOAD_RID:$machine" in
        linux-x64:x86_64|linux-x64:amd64|linux-arm64:aarch64|linux-arm64:arm64)
            return 0
            ;;
    esac

    if [[ "$FORCE_ARCHITECTURE" == true ]]; then
        echo "Warning: installing $PAYLOAD_RID payload on $machine because --force-architecture was supplied." >&2
        return 0
    fi

    echo "This installer contains a $PAYLOAD_RID payload, but this machine is $machine." >&2
    echo "Use the installer built for this architecture, or pass --force-architecture." >&2
    exit 1
}

prompt_license() {
    if [[ "$ACCEPT_LICENSE" == true ]]; then
        return 0
    fi

    show_license
    echo
    if [[ ! -t 0 ]]; then
        echo "Cannot prompt for EULA acceptance on non-interactive input." >&2
        echo "Rerun with --accept-license if you accept the EULA." >&2
        exit 1
    fi

    printf 'Do you accept this EULA? Type yes to continue: '
    read -r answer
    case "$answer" in
        yes|YES|Yes|y|Y) ;;
        *)
            echo "License not accepted. Installation cancelled." >&2
            exit 1
            ;;
    esac
}

payload_start_line() {
    awk '/^__TRACTUS_NDI_CONFIG_PAYLOAD_BELOW__$/ { print NR + 1; found = 1; exit } END { if (!found) exit 1 }' "$0"
}

extract_payload() {
    destination="$1"
    start_line="$(payload_start_line)" || {
        echo "Installer payload marker was not found." >&2
        exit 1
    }

    mkdir -p "$destination"
    tail -n +"$start_line" "$0" | tar -xzf - -C "$destination"
}

install_system() {
    tmpdir="$(mktemp -d)"
    trap 'rm -rf "$tmpdir"' EXIT
    extract_payload "$tmpdir"

    install -d -m 0755 "$INSTALL_DIR"
    install -d -m 0755 "$(dirname -- "$LINK_PATH")"
    install -m 0755 "$tmpdir/$BINARY_NAME" "$INSTALL_DIR/$BINARY_NAME"
    install -m 0644 "$tmpdir/LICENSE.txt" "$INSTALL_DIR/LICENSE.txt"
    install -m 0644 "$tmpdir/VERSION" "$INSTALL_DIR/VERSION"

    if [[ -e "$LINK_PATH" && ! -L "$LINK_PATH" ]]; then
        echo "$LINK_PATH already exists and is not a symlink. Remove it or move it aside first." >&2
        exit 1
    fi

    ln -sfn "$INSTALL_DIR/$BINARY_NAME" "$LINK_PATH"
    echo "$PRODUCT_TITLE $PRODUCT_VERSION installed to $INSTALL_DIR"
    echo "Command: $LINK_PATH"
}

install_local() {
    cat >&2 <<WARNING
Warning: not running as root. Installing to $LOCAL_DIR.
Run this installer with sudo, or from su, to install to $INSTALL_DIR and create $LINK_PATH.
WARNING
    extract_payload "$LOCAL_DIR"
    chmod +x "$LOCAL_DIR/$BINARY_NAME"
    echo "$PRODUCT_TITLE $PRODUCT_VERSION extracted to $LOCAL_DIR"
    echo "Command: $LOCAL_DIR/$BINARY_NAME"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --accept-license)
            ACCEPT_LICENSE=true
            shift
            ;;
        --force-architecture)
            FORCE_ARCHITECTURE=true
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

require_command awk
require_command tail
require_command tar
require_command mktemp
require_command uname
require_command install

check_architecture
prompt_license

if [[ "$(id -u)" -eq 0 ]]; then
    install_system
else
    install_local
fi

exit 0
__TRACTUS_NDI_CONFIG_PAYLOAD_BELOW__
INSTALLER_STUB

sed -i \
    -e "s/@PRODUCT_VERSION@/$version/g" \
    -e "s/@PAYLOAD_RID@/$RID/g" \
    "$OUTPUT"

tar -C "$payload_dir" -czf - . >> "$OUTPUT"
chmod +x "$OUTPUT"

installer_size="$(du -h "$OUTPUT" | awk '{print $1}')"
echo "Installer created: $OUTPUT ($installer_size)"
echo "Payload RID: $RID"
echo "Version: $version"
echo
echo "Run locally: $OUTPUT"
echo "Install system-wide: sudo $OUTPUT"

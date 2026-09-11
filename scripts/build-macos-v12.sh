#!/usr/bin/env bash
set -euo pipefail

SERVER_DIR="$(cd "$(dirname "$0")/.." && pwd)"
WEB_DIR="${JELLYFIN_WEB_SOURCE:-$SERVER_DIR/../Jellyfin Web Client}"
BUILD_ROOT="${JELLYFIN_BUILD_ROOT:-$SERVER_DIR/.build/macos-arm64}"
BUILD_STAMP="${JELLYFIN_BUILD_STAMP:-$(date '+%Y%m%d%H%M%S')}"
FFMPEG_DIR="${JELLYFIN_FFMPEG_DIR:-/Applications/Jellyfin.app/Contents/MacOS}"
APP_STAGE="$BUILD_ROOT/Jellyfin V12.app"
APP_INSTALL="/Applications/Jellyfin V12.app"
export APP_STAGE BUILD_STAMP

if [ "$(uname -s)" != Darwin ] || [ "$(uname -m)" != arm64 ]; then
    echo 'This packaging script requires macOS arm64.' >&2
    exit 1
fi
if [ "${1:-}" != '' ] && [ "${1:-}" != '--install' ]; then
    echo "Usage: $0 [--install]" >&2
    exit 1
fi
[ -f "$WEB_DIR/dist/index.html" ] || { echo 'Build jellyfin-web first with npm ci and npm run build:production.' >&2; exit 1; }
[ -x "$FFMPEG_DIR/ffmpeg" ] && [ -x "$FFMPEG_DIR/ffprobe" ]
mkdir -p "$BUILD_ROOT"
dotnet publish "$SERVER_DIR/Jellyfin.Server/Jellyfin.Server.csproj" -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:JellyfinBuildDateTime="$BUILD_STAMP" \
    -o "$BUILD_ROOT/server-publish"
[ -f "$BUILD_ROOT/server-publish/libcoreclr.dylib" ]
if [ -e "$APP_STAGE" ]; then
    mv "$APP_STAGE" "$BUILD_ROOT/previous-app-$(date '+%Y%m%d%H%M%S')-$$"
fi
mkdir -p "$APP_STAGE/Contents/MacOS" "$APP_STAGE/Contents/Resources"
ditto "$BUILD_ROOT/server-publish" "$APP_STAGE/Contents/MacOS/server"
ditto "$WEB_DIR/dist" "$APP_STAGE/Contents/Resources/jellyfin-web"
cp "$FFMPEG_DIR/ffmpeg" "$FFMPEG_DIR/ffprobe" "$APP_STAGE/Contents/MacOS/"
if [ -f /Applications/Jellyfin.app/Contents/Resources/AppIcon.icns ]; then
    cp /Applications/Jellyfin.app/Contents/Resources/AppIcon.icns "$APP_STAGE/Contents/Resources/"
fi
cat > "$APP_STAGE/Contents/MacOS/launch-jellyfin" <<'LAUNCH'
#!/bin/zsh
APP_ROOT="${0:A:h:h}"
DATA_ROOT="$HOME/Library/Application Support/jellyfin-v12"
mkdir -p "$DATA_ROOT/log" "$DATA_ROOT/config"
if [ ! -f "$DATA_ROOT/config/network.xml" ]; then
    cat > "$DATA_ROOT/config/network.xml" <<'NETWORK'
<NetworkConfiguration><InternalHttpPort>18096</InternalHttpPort><PublicHttpPort>18096</PublicHttpPort><AutoDiscovery>false</AutoDiscovery><EnableRemoteAccess>false</EnableRemoteAccess><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>
NETWORK
fi
exec "$APP_ROOT/MacOS/server/jellyfin" --datadir "$DATA_ROOT" --configdir "$DATA_ROOT/config" --cachedir "$DATA_ROOT/cache" --logdir "$DATA_ROOT/log" --webdir "$APP_ROOT/Resources/jellyfin-web" --ffmpeg "$APP_ROOT/MacOS/ffmpeg" >> "$DATA_ROOT/log/launcher.log" 2>&1
LAUNCH
chmod +x "$APP_STAGE/Contents/MacOS/launch-jellyfin"
python3 - <<'PY'
import os, plistlib
from pathlib import Path
app = Path(os.environ['APP_STAGE'])
with (app/'Contents/Info.plist').open('wb') as f:
    plistlib.dump(dict(CFBundleExecutable='launch-jellyfin', CFBundleIdentifier='org.jellyfin.server.v12.local', CFBundleName='Jellyfin V12', CFBundlePackageType='APPL', CFBundleShortVersionString='12.0.0', CFBundleVersion=os.environ['BUILD_STAMP'], CFBundleIconFile='AppIcon', LSUIElement=True), f)
PY
codesign --force --deep --sign - "$APP_STAGE"
codesign --verify --deep --strict "$APP_STAGE"
if [ "${1:-}" = '--install' ]; then
    pids="$(pgrep -f '^/Applications/Jellyfin V12[.]app/Contents/MacOS/server/jellyfin( |$)' || true)"
    if [ -n "$pids" ]; then
        printf '%s\n' "$pids" | xargs kill -TERM
        for _ in $(seq 1 75); do
            if ! pgrep -f '^/Applications/Jellyfin V12[.]app/Contents/MacOS/server/jellyfin( |$)' >/dev/null; then break; fi
            sleep 1
        done
        if pgrep -f '^/Applications/Jellyfin V12[.]app/Contents/MacOS/server/jellyfin( |$)' >/dev/null; then
            echo 'V12 is still shutting down; installation stopped.' >&2
            exit 1
        fi
    fi
    if [ -e "$APP_INSTALL" ]; then
        ditto "$APP_INSTALL" "$BUILD_ROOT/installed-backup-$BUILD_STAMP"
    fi
    # Only replace the explicitly isolated V12 application after staging and verification.
    rm -rf "$APP_INSTALL"
    ditto "$APP_STAGE" "$APP_INSTALL"
    codesign --verify --deep --strict "$APP_INSTALL"
    open "$APP_INSTALL"
fi
printf 'App: %s\nVersion: 12.0.0-%s\nURL: http://127.0.0.1:18096/web/index.html\n' "$APP_STAGE" "$BUILD_STAMP"

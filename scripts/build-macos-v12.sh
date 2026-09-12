#!/usr/bin/env bash
set -euo pipefail

SERVER_DIR="$(cd "$(dirname "$0")/.." && pwd)"
WEB_DIR="${JELLYFIN_WEB_SOURCE:-$SERVER_DIR/../Jellyfin Web Client}"
BUILD_ROOT="${JELLYFIN_BUILD_ROOT:-$SERVER_DIR/.build/macos-arm64}"
BUILD_STAMP="${JELLYFIN_BUILD_STAMP:-$(date '+%Y%m%d%H%M%S')}"
FFMPEG_DIR="${JELLYFIN_FFMPEG_DIR:-/Applications/Jellyfin V12.app/Contents/MacOS}"
LAUNCH_PORT="${JELLYFIN_HTTP_PORT:-8096}"
if [[ ! "$LAUNCH_PORT" =~ ^[0-9]{1,5}$ ]] || (( 10#$LAUNCH_PORT < 1 || 10#$LAUNCH_PORT > 65535 )); then
    echo 'JELLYFIN_HTTP_PORT must be an integer between 1 and 65535.' >&2
    exit 1
fi
LAUNCH_PORT=$((10#$LAUNCH_PORT))
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
[ -f "$SERVER_DIR/packaging/macos/AppIcon.icns" ]
[ -f "$SERVER_DIR/packaging/macos/StatusBarIcon.png" ]
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
cp "$SERVER_DIR/scripts/migrate-v12-network.py" "$APP_STAGE/Contents/Resources/"
printf '%s\n' "$LAUNCH_PORT" > "$APP_STAGE/Contents/Resources/default-http-port"
cp "$SERVER_DIR/packaging/macos/AppIcon.icns" "$SERVER_DIR/packaging/macos/StatusBarIcon.png" "$APP_STAGE/Contents/Resources/"
cp "$SERVER_DIR/packaging/macos/LICENSE" "$APP_STAGE/Contents/Resources/Launcher-LICENSE.txt"
xcrun swiftc -swift-version 5 -O -target arm64-apple-macos13.0 \
    "$SERVER_DIR/packaging/macos/LauncherCore.swift" "$SERVER_DIR/packaging/macos/main.swift" \
    -framework AppKit -framework ServiceManagement -o "$APP_STAGE/Contents/MacOS/Jellyfin V12 Launcher"
cat > "$APP_STAGE/Contents/MacOS/launch-jellyfin" <<'LAUNCH'
#!/bin/zsh
set -eu
APP_ROOT="${0:A:h:h}"
DATA_ROOT="$HOME/Library/Application Support/jellyfin-v12"
mkdir -p "$DATA_ROOT/log" "$DATA_ROOT/config"
LAUNCH_PORT="$(cat "$APP_ROOT/Resources/default-http-port")"
python3 "$APP_ROOT/Resources/migrate-v12-network.py" "$DATA_ROOT/config/network.xml" "$LAUNCH_PORT" >> "$DATA_ROOT/log/launcher.log" 2>&1
exec "$APP_ROOT/MacOS/server/jellyfin" --datadir "$DATA_ROOT" --configdir "$DATA_ROOT/config" --cachedir "$DATA_ROOT/cache" --logdir "$DATA_ROOT/log" --webdir "$APP_ROOT/Resources/jellyfin-web" --ffmpeg "$APP_ROOT/MacOS/ffmpeg" >> "$DATA_ROOT/log/launcher.log" 2>&1
LAUNCH
chmod +x "$APP_STAGE/Contents/MacOS/launch-jellyfin"
python3 - <<'PY'
import os, plistlib
from pathlib import Path
app = Path(os.environ['APP_STAGE'])
with (app/'Contents/Info.plist').open('wb') as f:
    plistlib.dump(dict(CFBundleExecutable='Jellyfin V12 Launcher', CFBundleIdentifier='org.jellyfin.server.v12.local', CFBundleName='Jellyfin V12', CFBundleDisplayName='Jellyfin V12', CFBundlePackageType='APPL', CFBundleShortVersionString='12.0.0', CFBundleVersion=os.environ['BUILD_STAMP'], CFBundleIconFile='AppIcon', LSMinimumSystemVersion='13.0', NSHighResolutionCapable=True, LSUIElement=True), f)
PY
codesign --force --deep --sign - "$APP_STAGE"
codesign --verify --deep --strict "$APP_STAGE"
if [ "${1:-}" = '--install' ]; then
    process_pattern='^/Applications/Jellyfin V12[.]app/Contents/MacOS/(server/jellyfin|Jellyfin V12 Launcher)( |$)'
    pids="$(pgrep -f '^/Applications/Jellyfin V12[.]app/Contents/MacOS/Jellyfin V12 Launcher( |$)' || true)"
    if [ -z "$pids" ]; then
        pids="$(pgrep -f '^/Applications/Jellyfin V12[.]app/Contents/MacOS/server/jellyfin( |$)' || true)"
    fi
    if [ -n "$pids" ]; then
        printf '%s\n' "$pids" | xargs kill -TERM
        for _ in $(seq 1 75); do
            if ! pgrep -f "$process_pattern" >/dev/null; then break; fi
            sleep 1
        done
        if pgrep -f "$process_pattern" >/dev/null; then
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
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP_INSTALL"
    open "$APP_INSTALL"
fi
printf 'App: %s\nVersion: 12.0.0-%s\nDefault URL: http://127.0.0.1:%s/web/index.html (existing custom network settings take precedence)\n' "$APP_STAGE" "$BUILD_STAMP" "$LAUNCH_PORT"

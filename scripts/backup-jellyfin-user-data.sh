#!/usr/bin/env bash
set -euo pipefail

OS_NAME="$(uname -s)"
case "$OS_NAME" in
    Darwin)
        DEFAULT_DATA_DIR="$HOME/Library/Application Support/jellyfin"
        DEFAULT_CONFIG_DIR=""
        DEFAULT_BACKUP_ROOT="/Volumes/mba2t/backup/jellyfin"
        ;;
    Linux)
        DEFAULT_DATA_DIR="/var/lib/jellyfin"
        DEFAULT_CONFIG_DIR="/etc/jellyfin"
        DEFAULT_BACKUP_ROOT="/home/wiz/jellyfin-userdata-backup"
        ;;
    *)
        echo "Unsupported operating system: $OS_NAME" >&2
        exit 1
        ;;
esac

SOURCE_DIR="${JELLYFIN_DATA_DIR:-$DEFAULT_DATA_DIR}"
CONFIG_DIR="${JELLYFIN_CONFIG_DIR:-$DEFAULT_CONFIG_DIR}"
BACKUP_ROOT="${JELLYFIN_BACKUP_ROOT:-$DEFAULT_BACKUP_ROOT}"
STAMP="${JELLYFIN_BACKUP_STAMP:-$(date '+%Y%m%d-%H%M%S')}"
DEST_DIR="$BACKUP_ROOT/$STAMP"
USER_DATA_DEST="$DEST_DIR/user-data"
CONFIG_DEST="$DEST_DIR/config"
DB_RELATIVE_PATH="data/jellyfin.db"
SOURCE_DB="$SOURCE_DIR/$DB_RELATIVE_PATH"
DEST_DB="$USER_DATA_DEST/$DB_RELATIVE_PATH"
MANIFEST="$DEST_DIR/manifest.txt"
STOP_APP="false"
RESTART_APP="false"
JELLYFIN_SERVICE="${JELLYFIN_SERVICE_NAME:-jellyfin.service}"
JELLYFIN_PROCESS_PATTERN='[/]Applications/Jellyfin.app/Contents/'

usage() {
    echo "Usage: $0 [backup-root] [--stop-app|--stop-service] [--restart-app|--restart-service]"
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --stop-app|--stop-service)
            STOP_APP="true"
            ;;
        --restart-app|--restart-service)
            RESTART_APP="true"
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        -*)
            echo "Unknown option: $1" >&2
            usage >&2
            exit 1
            ;;
        *)
            BACKUP_ROOT="$1"
            ;;
    esac
    shift
done

if [ "$OS_NAME" = "Linux" ] && [ "${EUID:-$(id -u)}" -ne 0 ]; then
    echo "Linux backups require root access to read $SOURCE_DIR and manage $JELLYFIN_SERVICE." >&2
    echo "Run this script with sudo." >&2
    exit 1
fi

if [ ! -d "$SOURCE_DIR" ]; then
    echo "Jellyfin user data directory does not exist: $SOURCE_DIR" >&2
    exit 1
fi

find_jellyfin_pids() {
    pgrep -f "$JELLYFIN_PROCESS_PATTERN" || true
}

jellyfin_is_running() {
    if [ "$OS_NAME" = "Linux" ]; then
        systemctl is-active --quiet "$JELLYFIN_SERVICE"
    else
        [ -n "$(find_jellyfin_pids)" ]
    fi
}

stop_jellyfin() {
    local pids

    if ! jellyfin_is_running; then
        return
    fi

    echo "Stopping Jellyfin before backup"
    if [ "$OS_NAME" = "Linux" ]; then
        systemctl stop "$JELLYFIN_SERVICE"
        if systemctl is-active --quiet "$JELLYFIN_SERVICE"; then
            echo "$JELLYFIN_SERVICE is still running after the stop request." >&2
            exit 1
        fi
        return
    fi

    osascript -e 'tell application "Jellyfin" to quit' >/dev/null 2>&1 || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        if ! jellyfin_is_running; then
            return
        fi
        sleep 1
    done

    pids="$(find_jellyfin_pids)"
    if [ -n "$pids" ]; then
        printf '%s\n' "$pids" | xargs kill -TERM
    fi
    for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
        if ! jellyfin_is_running; then
            return
        fi
        sleep 1
    done

    echo "Jellyfin is still running after graceful stop attempts." >&2
    find_jellyfin_pids >&2
    exit 1
}

restart_jellyfin() {
    if [ "$OS_NAME" = "Linux" ]; then
        echo "Starting $JELLYFIN_SERVICE"
        systemctl start "$JELLYFIN_SERVICE"
    else
        echo "Starting Jellyfin.app"
        open -a Jellyfin
    fi
}

copy_item() {
    local source="$1"
    local target="$2"

    if [ -d "$source" ] && [ -z "$(find "$source" -mindepth 1 -maxdepth 1 -print -quit)" ]; then
        mkdir -p "$target"
        return
    fi

    if command -v ditto >/dev/null 2>&1; then
        ditto "$source" "$target"
    elif [ -d "$source" ]; then
        mkdir -p "$target"
        cp -pR "$source/." "$target/"
    else
        mkdir -p "$(dirname "$target")"
        cp -p "$source" "$target"
    fi
}

backup_database() {
    local source="$1"
    local target="$2"

    mkdir -p "$(dirname "$target")"
    if command -v sqlite3 >/dev/null 2>&1; then
        sqlite3 "$source" <<SQL
.timeout 30000
.backup '$target'
SQL
    elif command -v python3 >/dev/null 2>&1; then
        python3 - "$source" "$target" <<'PY'
import sqlite3
import sys

source = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True, timeout=30)
target = sqlite3.connect(sys.argv[2])
with target:
    source.backup(target)
target.close()
source.close()
PY
    else
        echo "sqlite3 or python3 is required to create a consistent database backup." >&2
        exit 1
    fi
}

database_integrity_check() {
    local database="$1"
    local result

    if command -v sqlite3 >/dev/null 2>&1; then
        result="$(sqlite3 "$database" 'PRAGMA quick_check;')"
    else
        result="$(python3 - "$database" <<'PY'
import sqlite3
import sys

connection = sqlite3.connect(sys.argv[1])
print(connection.execute("PRAGMA quick_check").fetchone()[0])
connection.close()
PY
)"
    fi

    if [ "$result" != "ok" ]; then
        echo "Database integrity check failed: $result" >&2
        exit 1
    fi
}

sha256_file() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1"
    else
        sha256sum "$1"
    fi
}

if [ "$STOP_APP" = "true" ]; then
    stop_jellyfin
fi

mkdir -p "$USER_DATA_DEST"

echo "Backing up Jellyfin user data"
echo "  system: $OS_NAME"
echo "  source: $SOURCE_DIR"
echo "  target: $DEST_DIR"

while IFS= read -r -d '' item; do
    name="$(basename "$item")"
    if [ "$name" = "data" ]; then
        continue
    fi

    copy_item "$item" "$USER_DATA_DEST/$name"
done < <(find "$SOURCE_DIR" -mindepth 1 -maxdepth 1 -print0)

if [ -d "$SOURCE_DIR/data" ]; then
    mkdir -p "$USER_DATA_DEST/data"
    while IFS= read -r -d '' item; do
        name="$(basename "$item")"
        case "$name" in
            jellyfin.db|jellyfin.db-wal|jellyfin.db-shm)
                continue
                ;;
        esac

        copy_item "$item" "$USER_DATA_DEST/data/$name"
    done < <(find "$SOURCE_DIR/data" -mindepth 1 -maxdepth 1 -print0)
fi

if [ -f "$SOURCE_DB" ]; then
    backup_database "$SOURCE_DB" "$DEST_DB"
    database_integrity_check "$DEST_DB"
else
    echo "Warning: Jellyfin database was not found at $SOURCE_DB" >&2
fi

if [ -n "$CONFIG_DIR" ] && [ -d "$CONFIG_DIR" ]; then
    echo "  config: $CONFIG_DIR"
    copy_item "$CONFIG_DIR" "$CONFIG_DEST"
fi

{
    echo "created_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    echo "operating_system=$OS_NAME"
    echo "source_dir=$SOURCE_DIR"
    echo "config_dir=$CONFIG_DIR"
    echo "backup_dir=$DEST_DIR"
    echo "user_data_dir=$USER_DATA_DEST"
    echo "host=$(hostname)"
    if [ "$OS_NAME" = "Linux" ]; then
        echo "jellyfin_service=$JELLYFIN_SERVICE"
        echo "jellyfin_service_state=$(systemctl is-active "$JELLYFIN_SERVICE" 2>/dev/null || true)"
    else
        echo "jellyfin_processes=$(pgrep -fl "$JELLYFIN_PROCESS_PATTERN" | tr '\n' ';' || true)"
    fi
    echo
    echo "[size]"
    du -sh "$USER_DATA_DEST" 2>/dev/null || true
    du -sk "$USER_DATA_DEST" 2>/dev/null || true
    if [ -d "$CONFIG_DEST" ]; then
        du -sh "$CONFIG_DEST" 2>/dev/null || true
    fi
    echo
    echo "[database]"
    if [ -f "$DEST_DB" ]; then
        du -sh "$DEST_DB"
        sha256_file "$DEST_DB"
        echo "quick_check=ok"
    else
        echo "missing $DEST_DB"
    fi
} > "$MANIFEST"

ln -sfn "$DEST_DIR" "$BACKUP_ROOT/latest"

echo "Backup complete"
echo "  backup: $DEST_DIR"
echo "  latest: $BACKUP_ROOT/latest"
echo "  manifest: $MANIFEST"

if [ "$RESTART_APP" = "true" ]; then
    restart_jellyfin
fi

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
        if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ]; then
            DEFAULT_BACKUP_HOME="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
        else
            DEFAULT_BACKUP_HOME="$HOME"
        fi
        DEFAULT_DATA_DIR="/var/lib/jellyfin"
        DEFAULT_CONFIG_DIR="/etc/jellyfin"
        DEFAULT_BACKUP_ROOT="$DEFAULT_BACKUP_HOME/jellyfin-userdata-backup"
        ;;
    *)
        echo "Unsupported operating system: $OS_NAME" >&2
        exit 1
        ;;
esac

TARGET_DIR="${JELLYFIN_DATA_DIR:-$DEFAULT_DATA_DIR}"
CONFIG_DIR="${JELLYFIN_CONFIG_DIR:-$DEFAULT_CONFIG_DIR}"
BACKUP_ROOT="${JELLYFIN_BACKUP_ROOT:-$DEFAULT_BACKUP_ROOT}"
BACKUP_ARG="latest"
BACKUP_ARG_SET="false"
STOP_APP="false"
RESTART_APP="false"
YES="false"
JELLYFIN_SERVICE="${JELLYFIN_SERVICE_NAME:-jellyfin.service}"
JELLYFIN_SERVICE_USER="${JELLYFIN_SERVICE_USER:-jellyfin}"
JELLYFIN_SERVICE_GROUP="${JELLYFIN_SERVICE_GROUP:-jellyfin}"
JELLYFIN_PROCESS_PATTERN='[/]Applications/Jellyfin.app/Contents/'

usage() {
    echo "Usage: $0 [backup-dir|timestamp|latest] [--stop-app|--stop-service] [--restart-app|--restart-service] [--yes]"
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --stop-app|--stop-service)
            STOP_APP="true"
            ;;
        --restart-app|--restart-service)
            RESTART_APP="true"
            ;;
        --yes)
            YES="true"
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
            if [ "$BACKUP_ARG_SET" = "true" ]; then
                echo "Only one backup directory, timestamp, or latest may be specified." >&2
                usage >&2
                exit 1
            fi
            BACKUP_ARG="$1"
            BACKUP_ARG_SET="true"
            ;;
    esac
    shift
done

if [ "$OS_NAME" = "Linux" ] && [ "${EUID:-$(id -u)}" -ne 0 ]; then
    echo "Linux restores require root access to replace $TARGET_DIR and manage $JELLYFIN_SERVICE." >&2
    echo "Run this script with sudo." >&2
    exit 1
fi

copy_tree() {
    local source="$1"
    local target="$2"

    if command -v ditto >/dev/null 2>&1; then
        ditto "$source" "$target"
    else
        mkdir -p "$target"
        cp -pR "$source/." "$target/"
    fi
}

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
    if ! jellyfin_is_running; then
        return
    fi

    if [ "$OS_NAME" = "Linux" ]; then
        echo "Stopping $JELLYFIN_SERVICE before restore"
        systemctl stop "$JELLYFIN_SERVICE"
        if systemctl is-active --quiet "$JELLYFIN_SERVICE"; then
            echo "$JELLYFIN_SERVICE is still running after the stop request." >&2
            exit 1
        fi
        return
    fi

    echo "Stopping Jellyfin.app before restore"
    osascript -e 'tell application "Jellyfin" to quit' >/dev/null 2>&1 || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        if ! jellyfin_is_running; then
            return
        fi
        sleep 1
    done

    echo "Jellyfin is still running after the stop request." >&2
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

database_integrity_check() {
    local database="$1"
    local result

    if command -v sqlite3 >/dev/null 2>&1; then
        result="$(sqlite3 "$database" 'PRAGMA quick_check;')"
    elif command -v python3 >/dev/null 2>&1; then
        result="$(python3 - "$database" <<'PY'
import sqlite3
import sys

connection = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
print(connection.execute("PRAGMA quick_check").fetchone()[0])
connection.close()
PY
)"
    else
        echo "sqlite3 or python3 is required to validate the backup database." >&2
        exit 1
    fi

    if [ "$result" != "ok" ]; then
        echo "Backup database integrity check failed: $result" >&2
        exit 1
    fi
}

case "$BACKUP_ARG" in
    latest)
        BACKUP_DIR="$BACKUP_ROOT/latest"
        ;;
    /*)
        BACKUP_DIR="$BACKUP_ARG"
        ;;
    *)
        BACKUP_DIR="$BACKUP_ROOT/$BACKUP_ARG"
        ;;
esac

if [ -d "$BACKUP_DIR/user-data" ]; then
    RESTORE_SOURCE="$BACKUP_DIR/user-data"
elif [ -f "$BACKUP_DIR/data/jellyfin.db" ]; then
    RESTORE_SOURCE="$BACKUP_DIR"
else
    echo "Backup does not look valid: $BACKUP_DIR" >&2
    echo "Expected either $BACKUP_DIR/user-data or $BACKUP_DIR/data/jellyfin.db" >&2
    exit 1
fi

BACKUP_DB="$RESTORE_SOURCE/data/jellyfin.db"
if [ ! -f "$BACKUP_DB" ]; then
    echo "Backup database is missing: $BACKUP_DB" >&2
    exit 1
fi
database_integrity_check "$BACKUP_DB"

if jellyfin_is_running; then
    if [ "$STOP_APP" = "true" ]; then
        stop_jellyfin
    else
        echo "Jellyfin is still running. Stop it before restoring, or rerun with --stop-app/--stop-service." >&2
        exit 1
    fi
fi

echo "Restore source: $RESTORE_SOURCE"
echo "Restore target: $TARGET_DIR"
if [ -d "$BACKUP_DIR/config" ] && [ -n "$CONFIG_DIR" ]; then
    echo "Config source: $BACKUP_DIR/config"
    echo "Config target: $CONFIG_DIR"
fi

if [ "$YES" != "true" ]; then
    printf "This will move the current target aside and restore the backup. Type RESTORE to continue: "
    read -r answer
    if [ "$answer" != "RESTORE" ]; then
        echo "Restore cancelled."
        exit 1
    fi
fi

STAMP="$(date '+%Y%m%d-%H%M%S')"
PARENT_DIR="$(dirname "$TARGET_DIR")"
PRE_RESTORE_DIR="$TARGET_DIR.pre-restore-$STAMP"
PRE_RESTORE_CONFIG=""

mkdir -p "$PARENT_DIR"
if [ -e "$TARGET_DIR" ]; then
    mv "$TARGET_DIR" "$PRE_RESTORE_DIR"
    echo "Moved current data to: $PRE_RESTORE_DIR"
fi

mkdir -p "$TARGET_DIR"
copy_tree "$RESTORE_SOURCE" "$TARGET_DIR"

if [ -d "$BACKUP_DIR/config" ] && [ -n "$CONFIG_DIR" ]; then
    PRE_RESTORE_CONFIG="$CONFIG_DIR.pre-restore-$STAMP"
    mkdir -p "$(dirname "$CONFIG_DIR")"
    if [ -e "$CONFIG_DIR" ]; then
        mv "$CONFIG_DIR" "$PRE_RESTORE_CONFIG"
        echo "Moved current config to: $PRE_RESTORE_CONFIG"
    fi
    mkdir -p "$CONFIG_DIR"
    copy_tree "$BACKUP_DIR/config" "$CONFIG_DIR"
fi

if [ "$OS_NAME" = "Linux" ]; then
    chown -R "$JELLYFIN_SERVICE_USER:$JELLYFIN_SERVICE_GROUP" "$TARGET_DIR"
    if [ -n "$CONFIG_DIR" ] && [ -d "$CONFIG_DIR" ]; then
        chown -R "$JELLYFIN_SERVICE_USER:$JELLYFIN_SERVICE_GROUP" "$CONFIG_DIR"
    fi
fi

database_integrity_check "$TARGET_DIR/data/jellyfin.db"

echo "Restore complete"
echo "  restored from: $RESTORE_SOURCE"
echo "  restored to: $TARGET_DIR"
if [ -e "$PRE_RESTORE_DIR" ]; then
    echo "  previous data: $PRE_RESTORE_DIR"
fi
if [ -n "$PRE_RESTORE_CONFIG" ] && [ -e "$PRE_RESTORE_CONFIG" ]; then
    echo "  previous config: $PRE_RESTORE_CONFIG"
fi

if [ "$RESTART_APP" = "true" ]; then
    restart_jellyfin
fi

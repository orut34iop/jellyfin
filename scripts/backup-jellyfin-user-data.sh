#!/usr/bin/env bash
set -euo pipefail

SOURCE_DIR="${JELLYFIN_DATA_DIR:-$HOME/Library/Application Support/jellyfin}"
BACKUP_ROOT="${JELLYFIN_BACKUP_ROOT:-/Volumes/mba2t/backup/jellyfin}"
STAMP="${JELLYFIN_BACKUP_STAMP:-$(date '+%Y%m%d-%H%M%S')}"
DEST_DIR="$BACKUP_ROOT/$STAMP"
USER_DATA_DEST="$DEST_DIR/user-data"
DB_RELATIVE_PATH="data/jellyfin.db"
SOURCE_DB="$SOURCE_DIR/$DB_RELATIVE_PATH"
DEST_DB="$USER_DATA_DEST/$DB_RELATIVE_PATH"
MANIFEST="$DEST_DIR/manifest.txt"
STOP_APP="false"
RESTART_APP="false"

while [ "$#" -gt 0 ]; do
    case "$1" in
        --stop-app)
            STOP_APP="true"
            ;;
        --restart-app)
            RESTART_APP="true"
            ;;
        --help|-h)
            echo "Usage: $0 [backup-root] [--stop-app] [--restart-app]"
            exit 0
            ;;
        -*)
            echo "Unknown option: $1" >&2
            echo "Usage: $0 [backup-root] [--stop-app] [--restart-app]" >&2
            exit 1
            ;;
        *)
            BACKUP_ROOT="$1"
            ;;
    esac
    shift
done

if ! command -v sqlite3 >/dev/null 2>&1; then
    echo "sqlite3 is required but was not found." >&2
    exit 1
fi

if [ ! -d "$SOURCE_DIR" ]; then
    echo "Jellyfin user data directory does not exist: $SOURCE_DIR" >&2
    exit 1
fi

find_jellyfin_pids() {
    pgrep -f '[/]Applications/Jellyfin.app/Contents/Resources/jellyfin/jellyfin' || true
}

stop_jellyfin() {
    local pids

    pids="$(find_jellyfin_pids)"
    if [ -z "$pids" ]; then
        return
    fi

    echo "Stopping Jellyfin before backup"
    osascript -e 'tell application "Jellyfin" to quit' >/dev/null 2>&1 || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        if [ -z "$(find_jellyfin_pids)" ]; then
            return
        fi
        sleep 1
    done

    find_jellyfin_pids | xargs kill -TERM
    for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
        if [ -z "$(find_jellyfin_pids)" ]; then
            return
        fi
        sleep 1
    done

    echo "Jellyfin is still running after graceful stop attempts." >&2
    find_jellyfin_pids >&2
    exit 1
}

restart_jellyfin() {
    echo "Starting Jellyfin.app"
    open -a Jellyfin
}

if [ "$STOP_APP" = "true" ]; then
    stop_jellyfin
fi

mkdir -p "$USER_DATA_DEST"

copy_item() {
    local source="$1"
    local target="$2"

    if [ -d "$source" ] && [ -z "$(find "$source" -mindepth 1 -maxdepth 1 -print -quit)" ]; then
        mkdir -p "$target"
        return
    fi

    if command -v ditto >/dev/null 2>&1; then
        ditto "$source" "$target"
    else
        if [ -d "$source" ]; then
            mkdir -p "$target"
            cp -pR "$source/." "$target/"
        else
            mkdir -p "$(dirname "$target")"
            cp -p "$source" "$target"
        fi
    fi
}

echo "Backing up Jellyfin user data"
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
    mkdir -p "$(dirname "$DEST_DB")"
    sqlite3 "$SOURCE_DB" <<SQL
.timeout 30000
.backup '$DEST_DB'
SQL
else
    echo "Warning: Jellyfin database was not found at $SOURCE_DB" >&2
fi

{
    echo "created_at=$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    echo "source_dir=$SOURCE_DIR"
    echo "backup_dir=$DEST_DIR"
    echo "user_data_dir=$USER_DATA_DEST"
    echo "host=$(hostname)"
    echo "jellyfin_processes=$(pgrep -fl '/Applications/Jellyfin.app/Contents/Resources/jellyfin/jellyfin' | tr '\n' ';' || true)"
    echo
    echo "[size]"
    du -sh "$USER_DATA_DEST" 2>/dev/null || true
    du -sk "$USER_DATA_DEST" 2>/dev/null || true
    echo
    echo "[database]"
    if [ -f "$DEST_DB" ]; then
        du -sh "$DEST_DB"
        shasum -a 256 "$DEST_DB"
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

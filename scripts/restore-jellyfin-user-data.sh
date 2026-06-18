#!/usr/bin/env bash
set -euo pipefail

TARGET_DIR="${JELLYFIN_DATA_DIR:-$HOME/Library/Application Support/jellyfin}"
BACKUP_ROOT="${JELLYFIN_BACKUP_ROOT:-/Volumes/mba2t/backup/jellyfin}"
BACKUP_ARG="${1:-latest}"
STOP_APP="false"
YES="false"

shift || true
while [ "$#" -gt 0 ]; do
    case "$1" in
        --stop-app)
            STOP_APP="true"
            ;;
        --yes)
            YES="true"
            ;;
        *)
            echo "Unknown option: $1" >&2
            echo "Usage: $0 [backup-dir|timestamp|latest] [--stop-app] [--yes]" >&2
            exit 1
            ;;
    esac
    shift
done

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

running_pids="$(pgrep -f '/Applications/Jellyfin.app/Contents/Resources/jellyfin/jellyfin' || true)"
if [ -n "$running_pids" ]; then
    if [ "$STOP_APP" = "true" ]; then
        echo "Stopping Jellyfin.app before restore"
        osascript -e 'tell application "Jellyfin" to quit' >/dev/null 2>&1 || true
        for _ in 1 2 3 4 5 6 7 8 9 10; do
            if ! pgrep -f '/Applications/Jellyfin.app/Contents/Resources/jellyfin/jellyfin' >/dev/null 2>&1; then
                break
            fi
            sleep 1
        done
    fi

    if pgrep -f '/Applications/Jellyfin.app/Contents/Resources/jellyfin/jellyfin' >/dev/null 2>&1; then
        echo "Jellyfin is still running. Stop it before restoring, or rerun with --stop-app." >&2
        exit 1
    fi
fi

echo "Restore source: $RESTORE_SOURCE"
echo "Restore target: $TARGET_DIR"

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

mkdir -p "$PARENT_DIR"
if [ -e "$TARGET_DIR" ]; then
    mv "$TARGET_DIR" "$PRE_RESTORE_DIR"
    echo "Moved current data to: $PRE_RESTORE_DIR"
fi

mkdir -p "$TARGET_DIR"
copy_tree "$RESTORE_SOURCE" "$TARGET_DIR"

echo "Restore complete"
echo "  restored from: $RESTORE_SOURCE"
echo "  restored to: $TARGET_DIR"
if [ -e "$PRE_RESTORE_DIR" ]; then
    echo "  previous data: $PRE_RESTORE_DIR"
fi

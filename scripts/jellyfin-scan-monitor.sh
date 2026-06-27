#!/usr/bin/env bash
# Jellyfin "Scan Media Library" 进度监控
#
# 用法:
#   jellyfin-scan-monitor.sh                  # 等同 --once
#   jellyfin-scan-monitor.sh --once           # 打印一次快照
#   jellyfin-scan-monitor.sh --watch [SECS]   # 循环至完成，默认每 300 秒一次
#   jellyfin-scan-monitor.sh --trigger        # 触发一次 "Scan Media Library"
#   jellyfin-scan-monitor.sh --reset-token    # 删除缓存 token 强制重抓
#
# 退出码 (--once):
#   0  Scan 任务正在跑或没运行（监控正常）
#   1  本次 Scan 任务已完成（API state=Idle + LastStatus=Completed + start 时间已切换）
#   2  Jellyfin 进程或 API 不响应
#
# 环境变量:
#   JELLYFIN_DATA_DIR    数据目录            默认 ~/Library/Application Support/jellyfin
#   JELLYFIN_HOST        API host[:port]    默认 127.0.0.1:8096
#   JELLYFIN_TOKEN       管理员 token；若设置则跳过 DB 抓取
#   JELLYFIN_TOKEN_FILE  token 缓存文件     默认 /tmp/jellyfin-monitor-token
#   JELLYFIN_STATE_FILE  状态文件用于差分   默认 /tmp/jellyfin-monitor-state.json
#   JELLYFIN_TASK_ID     RefreshLibrary 任务 id  默认 7738148ffcd07979c7ceb148e06b3aed
#
# 依赖: bash 4+, sqlite3, curl, python3, awk, sed, pgrep
set -u

DATA_DIR="${JELLYFIN_DATA_DIR:-$HOME/Library/Application Support/jellyfin}"
API_HOST="${JELLYFIN_HOST:-127.0.0.1:8096}"
TOKEN_FILE="${JELLYFIN_TOKEN_FILE:-/tmp/jellyfin-monitor-token}"
STATE_FILE="${JELLYFIN_STATE_FILE:-/tmp/jellyfin-monitor-state.json}"
TASK_ID="${JELLYFIN_TASK_ID:-7738148ffcd07979c7ceb148e06b3aed}"
DB="$DATA_DIR/data/jellyfin.db"
LOG_DIR="$DATA_DIR/log"
API="http://$API_HOST"

#--- Helpers ------------------------------------------------------------------

die() { echo "[error] $*" >&2; exit 2; }

fetch_token() {
    [ -n "${JELLYFIN_TOKEN:-}" ] && { echo "$JELLYFIN_TOKEN"; return 0; }
    if [ -f "$TOKEN_FILE" ]; then
        cat "$TOKEN_FILE"
        return 0
    fi
    [ -f "$DB" ] || { echo ""; return 1; }
    # Pick any admin device token from the SQLite Devices table.
    local t
    t=$(sqlite3 "$DB" "SELECT AccessToken FROM Devices LIMIT 1;" 2>/dev/null || echo "")
    [ -z "$t" ] && return 1
    umask 077
    echo "$t" > "$TOKEN_FILE"
    echo "$t"
}

http_jellyfin_running() {
    pgrep -f "/Applications/Jellyfin.app/Contents/MacOS/jellyfin " >/dev/null \
      || pgrep -f "Jellyfin.Server" >/dev/null
}

api_get_task() {
    local token="$1"
    # 拥塞时 API 偶尔超时，重试 3 次
    local body=""
    for attempt in 1 2 3; do
        body=$(curl -sS -m 8 -H "X-Emby-Token: $token" "$API/ScheduledTasks/$TASK_ID" 2>/dev/null)
        [ -n "$body" ] && { echo "$body"; return 0; }
        sleep 2
    done
    return 1
}

#--- Subcommands --------------------------------------------------------------

cmd_trigger() {
    local token
    token=$(fetch_token) || die "no token available (DB missing or empty)"
    local code
    code=$(curl -sS -o /dev/null -w "%{http_code}" -X POST \
                -H "X-Emby-Token: $token" \
                "$API/ScheduledTasks/Running/$TASK_ID")
    echo "[trigger] POST /ScheduledTasks/Running/$TASK_ID -> HTTP $code"
    [ "$code" = "204" ] || exit 3
}

cmd_reset_token() {
    rm -f "$TOKEN_FILE"
    echo "[reset] $TOKEN_FILE removed"
}

cmd_once() {
    local now_epoch now_str
    now_epoch=$(date +%s)
    now_str=$(date "+%Y-%m-%d %H:%M:%S")

    echo "===== Jellyfin Scan 监控  $now_str ====="

    if ! http_jellyfin_running; then
        echo "[进程] !!! jellyfin 不在运行"
        exit 2
    fi

    local token; token=$(fetch_token) || true
    local task_state="?" task_progress="0" task_last_status="" task_last_start="" task_last_end="" pct=""
    if [ -n "$token" ]; then
        local task_json; task_json=$(api_get_task "$token") || task_json=""
        if [ -n "$task_json" ]; then
            eval "$(echo "$task_json" | python3 -c '
import sys, json
t = json.load(sys.stdin)
lr = t.get("LastExecutionResult") or {}
print("task_state=" + repr(t.get("State", "")))
print("task_progress=" + str(t.get("CurrentProgressPercentage") or 0))
print("task_last_status=" + repr(lr.get("Status", "")))
print("task_last_start=" + repr(lr.get("StartTimeUtc", "")))
print("task_last_end=" + repr(lr.get("EndTimeUtc", "")))
')"
            pct=$(printf "%.2f" "$task_progress" 2>/dev/null || echo "$task_progress")
            echo "[任务] state=$task_state  progress=${pct}%"
        else
            echo "[任务] API 无响应 (3 次重试均超时)"
        fi
    else
        echo "[任务] 跳过 API (无 token; 用 --reset-token 或设 JELLYFIN_TOKEN)"
    fi

    [ -f "$DB" ] || die "DB not found at $DB"

    local db_size total movies episodes series seasons
    db_size=$(du -h "$DB" 2>/dev/null | awk '{print $1}')
    total=$(sqlite3 "$DB" "SELECT COUNT(*) FROM BaseItems;")
    movies=$(sqlite3 "$DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.Movies.Movie';")
    episodes=$(sqlite3 "$DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Episode';")
    series=$(sqlite3 "$DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Series';")
    seasons=$(sqlite3 "$DB" "SELECT COUNT(*) FROM BaseItems WHERE Type='MediaBrowser.Controller.Entities.TV.Season';")
    echo "[DB  ] $db_size  total=$total | 电影 $movies | Series=$series Season=$seasons Episode=$episodes"

    local fresh_10m
    fresh_10m=$(sqlite3 "$DB" "SELECT COUNT(*) FROM BaseItems WHERE DateLastSaved > datetime('now','-10 minutes');")
    echo "[新写] 近10分钟 DateLastSaved 新增: $fresh_10m  (DB 真实写入信号)"

    echo "[各库]"
    sqlite3 -separator '|' "$DB" "
        SELECT
          COALESCE(parent.Name,'(unknown)'),
          COUNT(*),
          SUM(CASE WHEN b.DateLastRefreshed IS NULL OR b.DateLastRefreshed='' THEN 1 ELSE 0 END)
        FROM BaseItems b
        LEFT JOIN BaseItems parent ON parent.Id=b.TopParentId
        WHERE b.TopParentId IS NOT NULL
        GROUP BY b.TopParentId
        ORDER BY COUNT(*) DESC;" | awk -F'|' '{
            total=$2; pending=$3; done=total-pending;
            pct=(total>0)?(done*100.0/total):0;
            printf "       %-12s  %7d/%-7d  %5.1f%%\n", $1, done, total, pct
        }'
    local pending_total done_total overall_pct
    pending_total=$(sqlite3 "$DB" "SELECT COUNT(*) FROM BaseItems WHERE TopParentId IS NOT NULL AND (DateLastRefreshed IS NULL OR DateLastRefreshed='');")
    done_total=$((total - pending_total))
    overall_pct=$(awk -v a=$done_total -v b=$total 'BEGIN{if(b>0)printf "%.1f",a*100.0/b;else print "0"}')
    echo "[总进度 DB] $done_total / $total (${overall_pct}%)"

    # 差分对比
    if [ -f "$STATE_FILE" ]; then
        local prev_epoch prev_total prev_done prev_progress
        prev_epoch=$(sed -n 's/.*"epoch":\([0-9]*\).*/\1/p' "$STATE_FILE")
        prev_total=$(sed -n 's/.*"total":\([0-9]*\).*/\1/p' "$STATE_FILE")
        prev_done=$(sed -n 's/.*"done":\([0-9]*\).*/\1/p' "$STATE_FILE")
        prev_progress=$(sed -n 's/.*"task_progress":\([0-9.]*\).*/\1/p' "$STATE_FILE")
        local delta_t=$((now_epoch - prev_epoch))
        local delta_total=$((total - prev_total))
        local delta_done=$((done_total - prev_done))
        if [ "${delta_t:-0}" -gt 0 ]; then
            if [ "$delta_done" -gt 0 ]; then
                local rate eta
                rate=$(awk -v p=$delta_done -v t=$delta_t 'BEGIN{printf "%.1f", p*60.0/t}')
                eta=$(awk -v p=$pending_total -v r=$delta_done -v t=$delta_t 'BEGIN{if(r>0)printf "%.0f",p*(t/60.0)/r;else print "?"}')
                echo "[速率] 已刷 +${delta_done} / ${delta_t}s = ${rate} 项/分 · ETA ${eta} 分钟"
            fi
            echo "[增量] 新入库 +${delta_total} 项"
            if [ -n "$prev_progress" ] && [ -n "${pct:-}" ]; then
                local delta_progress
                delta_progress=$(awk -v a=$task_progress -v b=$prev_progress 'BEGIN{printf "%.2f", a-b}')
                echo "[任务进度增量] ${prev_progress}% → ${pct}% (+${delta_progress})"
            fi
        fi
    fi

    # 实质日志
    local log latest_log since recent congest
    latest_log=$(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1 || true)
    if [ -n "$latest_log" ]; then
        since=$(date -v-5M "+%Y-%m-%d %H:%M:%S" 2>/dev/null || date -d '-5 minutes' "+%Y-%m-%d %H:%M:%S")
        recent=$(awk -v t="$since" '$0 >= "["t' "$latest_log" 2>/dev/null \
            | grep -vE "TransactionLockingInterceptor|HttpsRedirection|DataProtection|Microsoft.AspNetCore|WebSocket|Hosting" \
            | grep -E "Validating|Refresh|Scan|Completed after|Cancelled|People validation|\[ERR\]|\[WRN\]" \
            | tail -6)
        if [ -n "$recent" ]; then
            echo "[近5分钟]"
            echo "$recent" | sed 's/^/       /'
        fi
        congest=$(awk -v t="$since" '$0 >= "["t' "$latest_log" 2>/dev/null | grep -c "Query congestion detected" || true)
        echo "[DB活跃] 近5分钟 query congestion 次数: $congest"
    fi

    # 完成判定 + 状态持久化
    local done_flag=0
    if [ "$task_state" = "Idle" ] && [ "$task_last_status" = "Completed" ]; then
        local prev_run_start
        prev_run_start=$(sed -n 's/.*"task_last_start":"\([^"]*\)".*/\1/p' "$STATE_FILE" 2>/dev/null || echo "")
        if [ -n "$task_last_start" ] && [ "$task_last_start" != "$prev_run_start" ]; then
            echo "[✅ 完成] Scan Media Library 已完成"
            echo "        本次区间: $task_last_start → $task_last_end"
            done_flag=1
        fi
    fi

    cat >"$STATE_FILE" <<EOF
{"epoch":$now_epoch,"total":$total,"done":$done_total,"task_progress":${task_progress:-0},"task_last_start":"${task_last_start:-}"}
EOF

    exit $done_flag
}

cmd_watch() {
    local interval="${1:-300}"
    while :; do
        set +e
        ( cmd_once )
        local rc=$?
        set -e
        case $rc in
            1) echo; echo "[watch] 任务已完成 (退出码=1)，结束循环"; exit 0 ;;
            2) echo; echo "[watch] 服务异常 (退出码=2)，结束循环" >&2; exit 2 ;;
        esac
        echo
        echo "[watch] sleep ${interval}s ..."
        sleep "$interval"
    done
}

#--- Dispatch -----------------------------------------------------------------

case "${1:-}" in
    --trigger)      cmd_trigger ;;
    --reset-token)  cmd_reset_token ;;
    --watch)        cmd_watch "${2:-300}" ;;
    --once|"")      cmd_once ;;
    -h|--help)
        cat <<'USAGE'
Jellyfin "Scan Media Library" 进度监控

用法:
  jellyfin-scan-monitor.sh                  # 等同 --once
  jellyfin-scan-monitor.sh --once           # 打印一次快照
  jellyfin-scan-monitor.sh --watch [SECS]   # 循环至完成，默认每 300 秒一次
  jellyfin-scan-monitor.sh --trigger        # 触发一次 "Scan Media Library"
  jellyfin-scan-monitor.sh --reset-token    # 删除缓存 token 强制重抓

退出码 (--once):
  0  Scan 任务正在跑或没运行（监控正常）
  1  本次 Scan 任务已完成
  2  Jellyfin 进程或 API 不响应

环境变量:
  JELLYFIN_DATA_DIR    数据目录              默认 ~/Library/Application Support/jellyfin
  JELLYFIN_HOST        API host[:port]      默认 127.0.0.1:8096
  JELLYFIN_TOKEN       管理员 token；若设则跳过 DB 抓取
  JELLYFIN_TOKEN_FILE  token 缓存文件        默认 /tmp/jellyfin-monitor-token
  JELLYFIN_STATE_FILE  状态文件用于差分      默认 /tmp/jellyfin-monitor-state.json
  JELLYFIN_TASK_ID     RefreshLibrary 任务 ID  默认 7738148ffcd07979c7ceb148e06b3aed

详见 scripts/jellyfin-scan-monitor.md
USAGE
        ;;
    *)
        echo "Unknown arg: $1" >&2
        exit 64
        ;;
esac

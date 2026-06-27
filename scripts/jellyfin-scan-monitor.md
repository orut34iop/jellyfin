# Jellyfin "Scan Media Library" 进度监控

`scripts/jellyfin-scan-monitor.sh` 用于监控 Jellyfin 服务端的「Scan Media Library」（`RefreshLibrary`）任务进度，特别适合**大规模媒体库**（10w+ 项目）的首次扫描或全量重扫场景。

数据来源混合：

1. **API 权威进度**：`GET /ScheduledTasks/{id}` 给出 `State` 与 `CurrentProgressPercentage`
2. **SQLite DB 真实写入**：直接读 `jellyfin.db`，反映条目数 / 各库刷新情况 / 实际写入活动
3. **日志信号**：抓最近 5 分钟的 `Validating / Refresh / Completed / ERR / WRN` 等关键行 + `Query congestion` 次数

为什么这样混搭：Jellyfin 的 API progress 是「按顶层 Folder 阶段」上报的，遇到大库（一百多万子项）会**长时间不动**，但实际仍在写入；只看 API 容易误判停滞。

## 何时用

- 触发了「Scan Media Library」想盯进度
- 怀疑扫描卡死 / 速率掉到反常水平
- 极速模式（`LocalMetadataOnlyImport=true`）下校验是否在正确路径上跑
- 想知道剩余 ETA

## 依赖

| 工具 | 用途 |
|---|---|
| `bash` 4+ | 脚本宿主 |
| `sqlite3` | 直接读 `data/jellyfin.db` |
| `curl` | 调 Jellyfin REST API |
| `python3` 3.6+ | 解析 API JSON（标准库 `json` 够用） |
| `awk` / `sed` / `pgrep` / `du` | 基础工具 |

macOS 默认全部具备。

## 用法

### 单次快照

```bash
scripts/jellyfin-scan-monitor.sh             # 等同 --once
scripts/jellyfin-scan-monitor.sh --once
```

退出码：

- `0` — Jellyfin 正常，本次扫描尚未完成（或没有运行过）
- `1` — 检测到 `Scan Media Library` 本次跑完了（`State=Idle` + `LastExecutionResult.Status=Completed` 且 `StartTimeUtc` 与上轮记录不同）
- `2` — Jellyfin 进程不在 / API 不响应

### 循环监控直至完成

```bash
scripts/jellyfin-scan-monitor.sh --watch        # 默认每 300 秒
scripts/jellyfin-scan-monitor.sh --watch 60     # 每 60 秒一次
```

完成时（脚本退出码=1）自动退出。

### 触发一次 Scan Media Library

```bash
scripts/jellyfin-scan-monitor.sh --trigger
```

`POST /ScheduledTasks/Running/<id>`，HTTP 204 表示成功。需要管理员 token（自动从 DB 取）。

### 重置 token 缓存

```bash
scripts/jellyfin-scan-monitor.sh --reset-token
```

下次运行会从 DB 重新抓取 device token。换了用户或重装后需要执行。

## 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `JELLYFIN_DATA_DIR` | `~/Library/Application Support/jellyfin` | 数据目录（含 `data/jellyfin.db` 与 `log/`） |
| `JELLYFIN_HOST` | `127.0.0.1:8096` | API host[:port] |
| `JELLYFIN_TOKEN` | _（无）_ | 管理员 token；如设则跳过 DB 抓取 |
| `JELLYFIN_TOKEN_FILE` | `/tmp/jellyfin-monitor-token` | token 缓存文件（mode 0600） |
| `JELLYFIN_STATE_FILE` | `/tmp/jellyfin-monitor-state.json` | 差分对比用状态文件 |
| `JELLYFIN_TASK_ID` | `7738148ffcd07979c7ceb148e06b3aed` | `RefreshLibrary` 任务 ID（Jellyfin 10.x 默认值） |

Linux 上典型用法：

```bash
export JELLYFIN_DATA_DIR=/var/lib/jellyfin
scripts/jellyfin-scan-monitor.sh --watch 120
```

## token 怎么来

脚本 fetch token 顺序：

1. 环境变量 `JELLYFIN_TOKEN`（最高优先级）
2. `$JELLYFIN_TOKEN_FILE`（首次会自动生成，0600）
3. 直接读 `data/jellyfin.db` 的 `Devices` 表第一条 `AccessToken`

走第 3 步的前提：你之前用 Web 客户端登录过（在 Devices 表里留了 token）。如果还没登录，需要先：

```bash
open http://127.0.0.1:8096   # 用浏览器走启动向导，登录一次
```

## 输出样例

```
===== Jellyfin Scan 监控  2026-06-27 02:54:35 =====
[任务] state=Running  progress=78.20%
[DB  ] 1.2G  total=271994 | 电影 50266 | Series=5269 Season=9562 Episode=160297
[新写] 近10分钟 DateLastSaved 新增: 4759  (DB 真实写入信号)
[各库]
       tvshows          4233/175268     2.4%
       movies           8028/50266     16.0%
       jav              7866/25895     30.4%
       中文字幕      9835/19311     50.9%
       成人电影       731/731      100.0%
[总进度 DB] 31215 / 272001 (11.5%)
[速率] 已刷 +2442 / 303s = 484.0 项/分 · ETA 507 分钟
[增量] 新入库 +112 项
[任务进度增量] 71.712% → 72.580% (+0.86)
[DB活跃] 近5分钟 query congestion 次数: 0
```

## 字段释义

| 字段 | 含义 |
|---|---|
| `[任务] state` | `Running` = 任务在跑；`Idle` = 没在跑（已完成或未启动） |
| `[任务] progress` | API 上报的整体百分比，**按顶层 Folder 阶段**而非项数，会成段冻结 |
| `[DB] size / total` | DB 文件大小、`BaseItems` 总条数 |
| `[DB] 电影/Series/Season/Episode` | 按 Type 分桶；扫描早期暴增，元数据阶段稳定 |
| `[新写]` | 近 10 分钟 `DateLastSaved` 新增 —— 这是**真实写入**信号，比 API progress 灵敏 |
| `[各库]` | 每个 CollectionFolder 下 `DateLastRefreshed` 非空比例，做完率 |
| `[总进度 DB]` | 全部库已 refreshed / 总数 |
| `[速率]` | 上一窗口的元数据刷新速率与剩余 ETA |
| `[增量]` | 上一窗口新入库条目数（区分入库阶段和刷新阶段） |
| `[任务进度增量]` | API progress 在窗口内的增量，直接和上一轮对比 |
| `[近5分钟]` | 关键日志（去掉拥塞 / HTTP / WebSocket 噪音） |
| `[DB活跃] congestion` | 近 5 分钟 `Query congestion detected` 次数，DB 锁竞争指标 |

## 怎么读

### Scan 真的在跑吗？

按优先级看以下任一信号：

1. `[任务] state=Running` ✅
2. `[新写]` 持续 > 0（哪怕 API progress 不变）
3. `[各库]` 某一库的已刷数在涨
4. `[DB活跃] congestion` > 0

任意一条满足都说明**在跑**。

### 卡死判断

同时满足才算卡：

- `[任务] state=Running` 但 progress **连续 ≥ 15 分钟不变**
- `[新写]` 连续 ≥ 10 分钟为 0
- `[DB活跃] congestion` = 0
- `[各库]` 所有库 done 计数都不动

只满足其中一条都不算（参考下面的 quirks）。

### ETA 怎么估

主用 `[速率]` 给出的 ETA。**但单 5 分钟窗口噪声很大**，速率可能在 50–3000 项/分跳；连续 3–4 轮的平均更可信。

也可参考 `[任务进度增量]` —— 平均每 5 分钟 0.5 pp 推进，从当前百分比线性外推。

## 已知行为 / 怪相

1. **API progress 会长时间不动**
   - 进度计算按顶层 Folder 阶段，遇到大 collection（如 175k tvshows）会卡在某一档很久，但 DB 在持续写入。
   - 不要据此判断停滞 —— 看 `[新写]` 和 `[各库]`。

2. **各库 done% 可能整批延后写**
   - 极速模式（`LocalMetadataOnlyImport`）下，`DateLastRefreshed` 字段有时整批延后 commit，导致 `[各库]` 比例突然从 0% 跳到 30%。
   - 这也是为何 `[新写]` 是更灵敏的指标。

3. **API 偶尔超时**
   - 扫描高负载时，HTTP 线程可能被 SQLite 长事务挡住，curl 8 秒超时（脚本内置 3 次重试）。
   - 表现：本轮 `[任务] API 无响应`。下轮通常恢复。

4. **`Query congestion` 突增到 100+ 不一定坏**
   - 表示 pessimistic SQLite locking 在批量 commit 时锁竞争升高；只要 `[新写]` 仍在涨就没问题。
   - 单次锁持 ≥ 5 分钟才该警惕（看 `[近5分钟]` 中的 `Query congestion cleared` 行，时长字段）。

5. **ISO 报错（`Error opening UDF/ISO image`）**
   - 极速模式下应跳过 ISO probe，但 `BaseVideoResolver.SetIsoType` 在 `LibraryOptions` 解析竞态下偶发漏掉检查，少量 ISO 仍会被 probe 一次然后报错。
   - 已知问题、不影响其它项目扫描。

6. **post-scan 阶段会再加几千项**
   - 扫描末段（API 98%–100%）会创建聚合条目（如 Studio / Genre / People 索引），DB total 还会再涨 5%–10%。属正常。

## 配合 cron / launchd

每 5 分钟监控一次（追加日志）：

```cron
*/5 * * * * /Users/me/dev/jellyfin/scripts/jellyfin-scan-monitor.sh --once \
    >> /tmp/jellyfin-monitor.log 2>&1
```

完成判定（退出码=1）后停止 cron：

```bash
while ! scripts/jellyfin-scan-monitor.sh --once; [ $? -ne 1 ]; do sleep 300; done
```

或直接用脚本自己的 watch 模式：

```bash
scripts/jellyfin-scan-monitor.sh --watch 300
```

## 故障排查

### `[任务] 跳过 API (无 token; ...)` 

DB 里 Devices 表为空，说明从没用 Web 客户端登录过。打开 `http://127.0.0.1:8096`，走启动向导/登录，让 Jellyfin 把 token 写进去。

### `[任务] API 无响应 (3 次重试均超时)`

- 立刻手测 `curl -m 30 -H "X-Emby-Token: <TOKEN>" http://127.0.0.1:8096/System/Info/Public`，看是不是真的卡。
- 如果 `/System/Info/Public` 都不响应，jellyfin 主线程被 SQLite 锁住了；等几分钟自然恢复或考虑重启（重启会丢一段扫描进度）。

### `[error] DB not found at ...`

`JELLYFIN_DATA_DIR` 设错了。`/System/Info/Public` 里的 `Id` 应该和 `data/jellyfin.db` 里 `BaseItems(Type='UserView' OR 'UserRootFolder')` 对得上。

## 历史触发参数

如果是从 Web UI 触发的，任务 ID 一般是 Jellyfin 内置的 `7738148ffcd07979c7ceb148e06b3aed`（脚本默认就是它）。如果你的 fork 改了这个 ID，覆盖环境变量：

```bash
JELLYFIN_TASK_ID=<your-task-id> scripts/jellyfin-scan-monitor.sh --trigger
```

查询所有任务 ID：

```bash
TOKEN=$(cat /tmp/jellyfin-monitor-token)
curl -sS -H "X-Emby-Token: $TOKEN" http://127.0.0.1:8096/ScheduledTasks \
  | python3 -c 'import sys,json
for t in json.load(sys.stdin):
    print(t["Id"], t["Name"], t.get("Key",""))'
```

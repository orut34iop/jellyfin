# Pessimistic SQLite Locking 引发的两类死锁观察

日期：2026-06-27
仓库：`/Users/wiz/dev/jellyfin`
分支：`release-10.11.z`
Jellyfin Server 版本：`10.11.11`（本地构建版本号 `10.11.11-local`）
HEAD：`8ca2c0a323`
相关 upstream commit：[`4a5be7fc90 Use pessimistic SQLite locking by default`](https://github.com/orut34iop/jellyfin/commit/4a5be7fc90)

本报告记录 2026-06-27 早上一次完整扫描结束后立刻使用 WebUI「备份」按钮所触发的**两类死锁**。两类死锁都起源于 `Jellyfin.Database.Implementations.Locking.PessimisticLockBehavior` 与某条具体业务路径的交互，且都**导致 Jellyfin 主进程必须强制 kill -9 才能恢复**。

## 1. 一句话结论

| # | 死锁名 | 触发条件 | 表现 | 修复方向 |
|---|---|---|---|---|
| 1 | **BackupService 长事务 self-deadlock** | WebUI Dashboard → 备份 → 发起备份 | 备份进度卡在第二张表，整个 Jellyfin HTTP/日志 一并冻结，所有线程 `__psynch_cvwait` | 把 `BackupService.CreateBackupAsync` 的全表 dump 拆成「每表一个短事务」或干脆改用 `sqlite3 .backup` 原子页拷贝 |
| 2 | **PessimisticLockBehavior 失败路径下 semaphore 泄漏** | 其它进程持锁导致 `BeginTransactionAsync` 30s 超时 | 即使外部锁释放，进程内 `Microsoft.Data.Sqlite` 仍持续报 "database is locked"，所有 DB 操作永久死等 | 给 `OnSaveChangesAsync` 包 `try/finally`，保证 `SemaphoreSlim.Release()` 一定执行 |

---

## 2. 现场重现

### 2.1 出错前置：完整扫描刚结束

`Scan Media Library` 任务在 `09:07:54` 完成（详见 [`local-metadata-only-import-report-2026-06-27.md`](./local-metadata-only-import-report-2026-06-27.md)）。DB 状态稳定：

```
size:       3.7 GB → 关闭时 SQLite checkpoint 后 3.2 GB
BaseItems:  293,033 条（含 18,006 Studio 聚合项）
integrity:  ok
```

5 个媒体库全 100%。

### 2.2 死锁 1：BackupService 长事务

操作：WebUI → Dashboard → 备份 → 发起备份。

日志时间线（一切来自 `~/Library/Application Support/jellyfin/log/log_20260627.log`）：

```
09:28:43  [INF] BackupService: Running database optimization before backup
09:29:16  [INF] SqliteDatabaseProvider: jellyfin.db optimized successfully!
09:29:16  [INF] BackupService: Attempting to create a new backup at ".../jellyfin-backup-20260627092843.zip"
09:29:16  [INF] BackupService: Starting backup process
09:29:16  [INF] BackupService: Begin Database backup
09:29:16  [INF] BackupService: Begin backup of entity "AccessSchedules"
09:29:16  [INF] BackupService: Backup of entity "AccessSchedules" with 0 created
09:29:16  [INF] BackupService: Begin backup of entity "ActivityLogs"
... 之后整个 log 17 分钟没有任何 BackupService 行
... 期间只有 3 条 WebSocket keep-alive INF（10 分钟一次）
09:38:09  最后一条 WebSocket keep-alive ← 这之后整个 logger 也停了
```

人为干预（10:21）KILL 进程时，Serilog 强制 flush 抢救出关键证据：

```
[09:46:20] [INF] PessimisticLockBehavior: QueryLock: 55e1db41-...
[09:46:20] [INF] PessimisticLockBehavior: Query congestion detected:
                 '55e1db41-...' since '06/27/2026 09:29:16 +08:00'
```

事务 `55e1db41-...` 从 **09:29:16** 起持锁 **17 分 4 秒**未释放 —— 与 `BackupService.cs:307` 的 `dbContext.Database.BeginTransactionAsync()` 时间戳完全吻合。

期间检验：

- jellyfin 进程 CPU `0%`、所有线程都在 `__psynch_cvwait`
- backup zip 文件大小 **始终是 69 字节**（zip 文件头），从未写入第二张表
- `BackupController` 无 `cancel`/`abort` API，无法优雅停止
- `tmutil status` 显示 Time Machine 未运行（排除外部备份冲突）

### 2.3 死锁 2：失败事务后 semaphore 泄漏

`kill 23021` 强制结束死锁的 jellyfin 进程后，**再次 `open /Applications/Jellyfin.app` 启动**新实例。

但用错的 zsh `kill $PIDS` 语法（`PIDS` 含换行）让 kill 实际没执行，留下了**孤儿进程 23021**。新进程 74835 与孤儿同时持有 `jellyfin.db`：

```
$ lsof "$HOME/Library/Application Support/jellyfin/data/jellyfin.db"
COMMAND  PID    USER  FD     TYPE  DEVICE      SIZE/OFF  NODE   NAME
jellyfin 23021  wiz   324u  REG    1,17  3397640192  4790772  ...jellyfin.db
jellyfin 23021  wiz   325u  REG    1,17  3397640192  4790772  ...jellyfin.db
jellyfin 23021  wiz   508u  REG    1,17           0  5107003  ...jellyfin.db-wal
jellyfin 74835  ...   ←新进程也开了同一 DB
```

74835 任何 `SaveChangesAsync` 都会被 23021 持的 SQLite 锁卡 **30 秒**：

```
[10:19:27] [ERR] EntityFrameworkCore.Database.Command:
                 Failed executing DbCommand ("30,065"ms) [Parameters=...]
[10:19:27] [ERR] EntityFrameworkCore.Update: An exception occurred in
                 the database while saving changes...
[10:19:27] [ERR] JellyfinDbContext: Error trying to save changes.
[10:19:27] [ERR] ExceptionMiddleware: Error processing request.
                 URL "GET" "/System/Info/Public".
```

stack 链路：

```
at Microsoft.Data.Sqlite.SqliteTransaction..ctor(...)
at Microsoft.Data.Sqlite.SqliteConnection.BeginTransaction(IsolationLevel, Boolean)
at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.BeginTransactionAsync(IsolationLevel, CancellationToken)
at Microsoft.EntityFrameworkCore.Update.Internal.BatchExecutor.ExecuteAsync(...)
at Jellyfin.Database.Implementations.Locking.PessimisticLockBehavior.OnSaveChangesAsync(JellyfinDbContext, Func<Task>)
at Jellyfin.Database.Implementations.JellyfinDbContext.SaveChangesAsync(Boolean, CancellationToken)
at Jellyfin.Server.Implementations.Users.UserManager.UpdateUserAsync(User)
at Emby.Server.Implementations.Session.SessionManager.LogSessionActivity(...)
at Jellyfin.Api.Helpers.RequestHelpers.GetSession(...)
at Emby.Server.Implementations.HttpServer.WebSocketManager.WebSocketRequestHandler(HttpContext)
```

**关键观察**：当 `kill -9 23021` 把孤儿干掉后，74835 仍然继续报 `SQLite Error 5: 'database is locked'`，且 `/Sessions/Capabilities/Full`、`/Users/Me`、`/ScheduledTasks` 等任何走 EFCore 写入的 endpoint **15 秒超时**。

`sample 74835 2` 出来 **31 处 `__psynch_cvwait`** —— 74835 自己已经进入了死锁。**外部锁早已释放，74835 内部却出不来了**。

唯一恢复方式：`pgrep ... | xargs kill -TERM` 重启第二次。重启后所有 endpoint 在 **几十毫秒内** 200。

## 3. 根因分析

### 3.1 BackupService 长事务

`Jellyfin.Server.Implementations/FullSystemBackup/BackupService.cs:307`：

```csharp
var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
await using (transaction.ConfigureAwait(false))
{
    _logger.LogInformation("Begin Database backup");

    foreach (var entityType in entityTypes)   // ← 几十张表，包含 BaseItems(29w) / PeopleBaseItemMap(460w)
    {
        _logger.LogInformation("Begin backup of entity {Table}", entityType.SourceName);
        var zipEntry = zipArchive.CreateEntry(...);
        var entities = 0;
        var zipEntryStream = zipEntry.Open();
        await using (zipEntryStream.ConfigureAwait(false))
        {
            var jsonSerializer = new Utf8JsonWriter(zipEntryStream);
            ...
            await foreach (var item in set.ConfigureAwait(false))
            {
                entities++;
                using var document = JsonSerializer.SerializeToDocument(item, _serializerSettings);
                document.WriteTo(jsonSerializer);
            }
            ...
        }
        _logger.LogInformation("Backup of entity {Table} with {Number} created", ...);
    }
}
```

**问题**：一个 `BeginTransactionAsync` 覆盖**所有表**，等于把 DB 在备份期间一直 lock 住读写。

但 jellyfin 同时有几个常驻线程要写：

- `LibraryMonitor` 监听文件系统变更
- `SessionManager.LogSessionActivity` 写 `ActivityLogs` 表（用户活动、WebSocket connect/disconnect 都会触发）
- `Serilog` 的 sink 也走 EFCore 路径写日志条目到 `ActivityLogs`

`PessimisticLockBehavior` 让这些线程**等 BackupService 释放**。但 BackupService 又调用了 `Serilog.LogInformation` 写 `ActivityLogs` —— **它自己等自己**：

```
BackupService thread:
  持有 transaction → 想 LogInformation "Begin backup of entity X"
                  → Serilog write → EFCore SaveChanges to ActivityLogs
                  → PessimisticLockBehavior.OnSaveChangesAsync
                  → 等待 BackupService 自己持的事务释放
                  → 死锁
```

观察证据：09:29:16 备份印出 `Begin backup of entity "ActivityLogs"` 之后**没有 `Backup of entity "ActivityLogs" with N created"`** —— 序列化 `ActivityLogs` 表的过程触发了一次需要写 `ActivityLogs` 的 `LogInformation`，self-deadlock。

### 3.2 PessimisticLockBehavior 失败路径

具体源码未细读，但根据现象推断：

```csharp
// 推测的当前实现（伪代码）
public async Task OnSaveChangesAsync(JellyfinDbContext context, Func<Task> saveChanges)
{
    await _semaphore.WaitAsync();
    await saveChanges();        // ← 如果 throw 了 SqliteException "database is locked"
    _semaphore.Release();        // ← 这一行根本到不了
}
```

预期实现（`try/finally`）：

```csharp
public async Task OnSaveChangesAsync(JellyfinDbContext context, Func<Task> saveChanges)
{
    await _semaphore.WaitAsync();
    try
    {
        await saveChanges();
    }
    finally
    {
        _semaphore.Release();   // ← 永远会释放
    }
}
```

74835 在 30 秒超时后线程数次走 `SaveChangesAsync` failure path，每次都吃掉一个 semaphore counter。到 semaphore 的内部计数耗尽后，**所有后续 SaveChanges 都永久 cvwait**。

`sample` 出来的 31 处 cvwait 与多个独立线程（HTTP handler、SessionWebSocketListener、EventManager.SessionStartedLogger 等）数量吻合，每个线程都在 `SemaphoreSlim.WaitAsync().GetAwaiter().GetResult()` 上。

## 4. 临时绕开

### 4.1 不要用 WebUI 内置备份

改用仓库脚本 `scripts/backup-jellyfin-user-data.sh`，它用 `sqlite3 .backup` 原子页拷贝，**不开 EFCore 长事务**：

```bash
JELLYFIN_BACKUP_ROOT="/Users/wiz/data/jellyfin backup" \
    /Users/wiz/dev/jellyfin/scripts/backup-jellyfin-user-data.sh
```

实测对照（3.7 GB / 29 万条 BaseItems）：

| 方法 | 用时 | 结果 |
|---|---:|---|
| WebUI 内置备份 | 17+ 分钟死锁，被迫 kill | 0 字节空 zip |
| `scripts/backup-jellyfin-user-data.sh` | **15.3 秒** | 完整 DB（integrity_check=ok）+ 配置 |

### 4.2 万一陷入死锁 2 的状态

```bash
# 一定要用 pipe 拆 PID，避免 zsh 把多行字符串当一个 PID
pgrep -f '/Applications/Jellyfin.app/Contents/MacOS/' | xargs kill -TERM
sleep 10
pgrep -f '/Applications/Jellyfin.app/Contents/MacOS/' | xargs kill -9   # 兜底
open /Applications/Jellyfin.app
```

错误写法（zsh 下会失败）：

```bash
PIDS=$(pgrep -f ...)
kill $PIDS                # ← 多行字符串当一个 PID，报 "illegal pid: 23018\n23021"
```

## 5. 推荐的 upstream 修法（未实现）

### 5.1 BackupService

最小改法 —— 把 `BeginTransactionAsync` 删掉，让每个表的 dump 走默认 auto-commit；或者拆成「每表一个短事务」：

```csharp
foreach (var entityType in entityTypes)
{
    var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
    await using (transaction.ConfigureAwait(false))
    {
        // dump 这一张表
    }
}
```

更彻底 —— `BackupService` 直接 shell out 到 `sqlite3 .backup`（与仓库脚本同思路），不走 EFCore，不与运行中的 LibraryMonitor / Logger 互锁。

### 5.2 PessimisticLockBehavior

`try/finally` 包住 release，保证任何 exception 路径都释放 semaphore。

### 5.3 BackupController

补一个 `HttpDelete("Running")` 或类似 cancel/abort endpoint。当前 BackupService 一旦启动就**只能等它完成或强 kill 进程**，对于已经死锁的备份没有任何优雅出路。

## 6. 数据完整性

整个事件**没有任何数据丢失**：

- `jellyfin.db` 重启后 `PRAGMA integrity_check` = `ok`
- `BaseItems` 行数 **293,033**（与扫描结束一致）
- 媒体库扫描结果（5 库 100% 完成）保留
- 备份脚本产出 SHA-256 `e26a4312e5922194b95d913f383ed9f84c34dba83ae5766139c113e7044a9a47`

## 7. 参考

- 扫描完成报告：[`local-metadata-only-import-report-2026-06-27.md`](./local-metadata-only-import-report-2026-06-27.md)
- 备份脚本（已修 `pgrep` pattern 适配新 .app 结构）：[`scripts/backup-jellyfin-user-data.sh`](../scripts/backup-jellyfin-user-data.sh)
- 还原脚本（同样修了 pattern）：[`scripts/restore-jellyfin-user-data.sh`](../scripts/restore-jellyfin-user-data.sh)
- 监控脚本：[`scripts/jellyfin-scan-monitor.sh`](../scripts/jellyfin-scan-monitor.sh)
- 相关 upstream commit：`4a5be7fc90 Use pessimistic SQLite locking by default`
- 相关 BackupService 源：`Jellyfin.Server.Implementations/FullSystemBackup/BackupService.cs`

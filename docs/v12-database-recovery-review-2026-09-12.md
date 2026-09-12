# V12 数据库损坏及恢复复核（2026-09-12）

## 结论

上次处置是停服、`.recover` 数据抢救、替换数据库和恢复扫描，没有数据库损坏
相关的代码提交。复核开始时仓库 HEAD 为 `788acecda0`。不能把“恢复库能启动”解释成
“损坏根因已修复”。本轮复核不替换运行数据库；检查在保留的离线恢复库和通过
SQLite backup API 取得的一致性快照上进行。

## 实测恢复质量

| 检查 | 上次刚恢复的离线库 | 本轮当前库/一致性快照 |
| --- | --- | --- |
| `integrity_check` | `ok`，耗时 187 秒 | 本轮未重复执行完整索引检查 |
| `foreign_key_check` | 16,379 项违规 | 0 项违规 |
| 单集数 | 164,051 | 165,170，已回到损坏前记录的数量 |
| 电影数 | 41,115 | 41,115 |
| 剧集数 | 5,398 | 5,398 |
| 未归属媒体候选的 ID 是否仍缺失 | 1,191 个缺失 | 0 个缺失 |
| journal mode | DELETE | DELETE |

离线库 `/tmp/jellyfin-recovered-20260912.db` 的 `lost_and_found` 共 148,138 条。
其中 1,191 条具有 BaseItems 的 72 列结构：1,107 个单集、84 个季，ID 均不重复，
当时均未进入 BaseItems。其余记录包含可能的索引条目，不能把 148,138 简单解释为
丢失了相同数量的媒体或业务记录，也不能整表盲目插回。

当前重新扫描已补回上述 1,191 个 ID，单集总数也补回了最初相差的 1,119 条。
这说明扫描修复了这部分缺失，但总数和 ID 相同仍不能证明每个元数据字段、关联、
用户编辑值都与损坏前一致。

上次离线恢复库的外键违规分布：

| 子表 | 缺失的父表 | 违规数 |
| --- | --- | --- |
| AncestorIds | BaseItems（两个外键） | 7,067 |
| BaseItemProviders | BaseItems | 768 |
| ItemValuesMap | BaseItems | 2,660 |
| PeopleBaseItemMap | BaseItems | 5,483 |
| BaseItems | BaseItems | 6 |
| BaseItemImageInfos | BaseItems | 395 |

## 需要改进的问题

1. **P1：恢复上线验收不足。** 上次仅以 quick_check 和 HTTP 200 验收。
   本次证实，即使完整 integrity_check 返回 ok，仍然可以有 16,379 项外键违规。
   应独立检查结构、外键、未归属记录、关键类型数量和关键字段；所有检查应基于
   同一静态快照，在恢复库投入运行前完成并记录结果。

2. **P1：数据库损坏后扫描继续工作。**
   `LibraryManager.SavePeopleMetadataAsync` 在 3994 行捕获所有 Exception 后继续
   下一人物；异常日志证实 SQLITE_CORRUPT 也走了此路径。
   首次错误在 launcher.log 的 **11:16:21.033**，并非之前所说的 11:26；
   到 11:28:42 才停止，期间持续报错并滚动淘汰了早期结构化日志。
   应将嵌套的 SQLite CORRUPT/NOTADB 等不可继续错误与单条元数据异常区分，
   在任务范围停止后续数据库工作并记录失败；还需验证异常不会被外层调度器吞掉，
   仅修改这一处 catch 并不能保证整个扫描停止。

3. **P2：恢复丢失了 WAL 模式。** 原损坏库文件头的读写版本为 2/2（WAL），
   恢复库为 1/1，运行库通过正常只读连接查询 journal_mode 也确认是 DELETE。
   `.recover` SQL 未恢复 WAL，迁移历史却已记录 ResetJournalMode，故启动不再
   自动执行该迁移。这会改变读写并发及 I/O 特征，应在完成恢复验收和备份后，
   在服务停止、连接关闭的受控步骤中恢复并验证模式；不应在每个连接打开时反复切换。
   DELETE 本身不等于损坏原因，也没有证据证明改成 DELETE 才解决了本次损坏。

4. **P2：迁移快速备份/回滚缺少 WAL 一致性保障。**
   原 `SqliteDatabaseProvider.MigrationBackupFast` 直接 File.Copy 主文件；
   原 `RestoreBackupFast` 直接覆盖，ClearAllPools 不能替代关闭全部活动连接。
   当 WAL 尚有已提交页或仍有活动连接时，该实现缺少自包含的一致性保证。
   应使用 SQLite backup API 制作备份，验证后保留；回滚应由 SQLite 管理目标事务
   和 WAL，或在连接全部关闭后管理整套数据库文件。此项是代码风险，未证实它触发了这次事故。

## 本轮代码保护

- SQLite provider 内共享故障状态，识别直接或嵌套的错误 11（CORRUPT）、26（NOTADB）。
  EF 命令失败、连接 PRAGMA 初始化失败及 SaveChanges 失败均能触发；一旦触发，阻止
  后续 EF 读写命令并通知宿主停服。BUSY、LOCKED、普通约束异常不触发结构损坏保护。
- 写入 `data/database-corruption.txt`，后续启动会拒绝打开业务数据库。只有保留现场并
  完成恢复验收后才能人工移除此标记；自动监测不允许清除此标记或反复重启。
- 损坏后的关机流程跳过显式 checkpoint、VACUUM、ANALYZE；迁移失败也跳过所有
  自动回滚，避免自动覆盖故障证据。这不保证 SQLite 关闭最后连接时不发生自身的
  checkpoint，也不能撤销已在执行中的事务，不能代替备份。
- 迁移备份改用 SQLite backup API，包含已提交的 WAL 页，支持配置的数据库路径，
  备份键加入 GUID 避免同秒覆盖。回滚前独立检查 integrity_check、foreign_key_check，
  保留回滚前一致性副本，再由 SQLite backup API 执行目标事务；缺失或无效备份明确失败。
- 新增 11 项测试验证真实无效 SQLite 文件、持久化故障标记、同步/异步命令拦截、
  停服维护跳过、WAL 数据备份、自定义路径、保留回滚前记录，以及外键异常恢复拒绝。

### 恢复约束

1. 不删除原始媒体、NFO、图片。不能因为媒体索引可重扫，就丢弃用户数据库或配置。
2. 保留故障库、WAL/SHM/journal 和日志现场；恢复操作在独立副本上进行。
3. 恢复验收必须检查结构、外键、lost_and_found 候选、关键媒体 ID/数量和用户数据。
   数量回到原值不等于所有字段恢复。`.recover` 结果不能直接视为完整备份。
4. 如确需重导，先保存用户、权限、播放记录、收藏、手工编辑和配置；新库验证后再切换，
   原库和回退副本保留。媒体重扫无法重建所有用户数据。
5. 本轮故障阻断覆盖上述应用访问路径；插件自行打开的原生连接、查询结果后续枚举时
   才发生而未传递到这些路径的错误仍需日志监测。根因未定位前不能承诺绝不再次损坏。
6. 本轮备份保存在同一台机器，是逻辑损坏的回退点，不构成独立介质的灾难恢复备份。

本轮持久备份目录：
`/Users/wiz/Library/Application Support/jellyfin-v12/recovery-snapshots/20260912-120353`。
包含 SQLite 一致性快照、config、root、播放列表和 device.txt；校验结果及 SHA-256
记录在该目录的 verification.json 中。原始损坏库、上次离线恢复库均未删除。

验证：新增保护测试 11/11 通过；媒体库、Item 仓储、数据库保护和 EF 迁移回归
共 490/490 通过。macOS arm64 包已构建，版本 `12.0.0-20260912120855`。

部署验收：上述版本已安装到 `/Applications/Jellyfin V12.app`，网页返回 HTTP 200，
`/System/Info/Public` 返回对应时间戳版本。持久快照完整 integrity_check 为 ok
（352 秒），foreign_key_check 为 0（43 秒），SHA-256 已写入 verification.json。
旧进程正常退出后，另存停服状态数据库及 WAL/SHM/journal 和应用日志，目录为
`recovery-snapshots/before-install-20260912120855`。使用应用自带 SQLite 3.53.3
恢复 WAL；重新启动后通过正常只读连接确认 journal_mode 为 wal。未清空媒体库。

用户数据补充核对：故障副本和上述快照中的 Permissions（24 行）、Preferences（13 行）、
DisplayPreferences（1 行）、ItemDisplayPreferences（18 行）逐值一致；Users（1 行）
仅 LastActivityDate、RowVersion 变化，其他字段一致。UserData 两份均为 0 行；
故障副本本身不是可信的故障前基线，因此这不能证明故障前没有播放记录。
结果保存在快照目录 user-data-comparison.json，不包含字段值或密码内容。

## 根因边界

- 原库多个被引用的损坏页面抽样为整页 4,096 字节全零，涉及表和索引；不是只重建
  新增索引就足以修复的问题。仅凭零页不能判定是磁盘故障、程序写入或 checkpoint 问题。
- 实际应用的 `libe_sqlite3.dylib` 报告 SQLite **3.53.3**，已高于官方 WAL-reset 修复
  版本 3.51.3；系统 Python/SQLite 工具报告 3.51.0。应避免用过旧诊断引擎以可写方式
  参与运行库的 checkpoint，但目前没有证据能把事故直接归因于该版本差异。
- 日志中的 `NoLock` 是 Jellyfin 应用层锁策略，不能据此断言 SQLite 文件锁被关闭；
  连接配置实际为 `locking_mode=NORMAL`。
- 上次关闭服务会执行 checkpoint/VACUUM，原始故障时的 WAL 未完整保留。因此现在的
  `.corrupt-20260912` 是停服后的证据副本，不能完整重放故障瞬间的 WAL 状态。
- 当前媒体条目数、关联已恢复到上述检查结果，仍需监测新一轮扫描是否无错误完成。

## 官方依据

- [SQLite 恢复说明](https://www.sqlite.org/recovery.html)：恢复可能丢失、复活或改变记录，
  也可能破坏约束；未归属内容进入 lost_and_found。
- [SQLite 完整性检查说明](https://www.sqlite.org/pragma.html#pragma_integrity_check)：
  外键检查应单独执行 foreign_key_check。
- [SQLite WAL 说明](https://www.sqlite.org/wal.html)：WAL 是持久数据的一部分；
  WAL-reset 的影响范围、修复版本和触发条件在该页说明。

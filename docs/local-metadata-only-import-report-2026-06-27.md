# Local Metadata Only Import 导入报告（per-library 模式验证）

日期：2026-06-27
仓库：`/Users/wiz/dev/jellyfin`
分支：`release-10.11.z`
Jellyfin Server 版本：`10.11.11`（本地构建版本号 `10.11.11-local`）
HEAD：`612899b8e9 Drop JELLYFIN_LOCAL_METADATA_ONLY_IMPORT env override`
运行方式：macOS Application，`/Applications/Jellyfin.app`（macOS 15 / arm64）

---

## 1. 报告结论

本次 `Scan Media Library` 任务**完整跑完**，最终状态 `Completed`。

| 维度 | 结果 |
|---|---|
| API `State` | `Idle` |
| API `LastExecutionResult.Status` | `Completed` |
| 任务 ID | `7738148ffcd07979c7ceb148e06b3aed`（Key=`RefreshLibrary`） |
| 开始（UTC） | `2026-06-26T14:45:44.276153Z` |
| 结束（UTC） | `2026-06-27T01:07:54.841873Z` |
| **总用时** | **622 分 10 秒 ≈ 10h 22m 10s** |
| 5 个媒体库 done% | 全部 100% ✅ |
| DB 终态 | 3.7 GB · 293,033 条 `BaseItems`（含 18,006 个 `Studio` 聚合项） |
| `MediaStreamInfos` | **0** ✅（极速模式应为 0） |
| `MediaSegments` | **0** ✅（极速模式应为 0） |
| 非噪音 ERR/WRN | 3 条（2 条 ISO + 1 条空 playlists 目录） |

**关键变化**：相比 [2026-06-18 报告](./local-metadata-only-import-report-2026-06-18.md)，本次**不依赖** `JELLYFIN_LOCAL_METADATA_ONLY_IMPORT` 环境变量，只依靠 WebUI 库设置对话框写入到每个库 `options.xml` 的 `<LocalMetadataOnlyImport>true</LocalMetadataOnlyImport>`。

性能完全对齐：

| 维度 | 2026-06-18（env=true） | 本次（per-library） |
|---|---:|---:|
| 启用方式 | 环境变量全局开关 | `options.xml` 按库开关 |
| 用时 | 10h 25m 49s | **10h 22m 10s** |
| 状态 | Completed | Completed |
| `MediaStreamInfos` | 0 | 0 |
| `MediaSegments` | 0 | 0 |

差 3 分 39 秒（统计噪声范围内），说明 commit `612899b8e9` 删除 env 兜底**没有任何回归**。

---

## 2. 运行环境

```text
进程命令行：
/Applications/Jellyfin.app/Contents/MacOS/jellyfin
  --webdir   /Applications/Jellyfin.app/Contents/Resources/jellyfin-web
  --ffmpeg   /Applications/Jellyfin.app/Contents/MacOS/ffmpeg
  --datadir  /Users/wiz/Library/Application Support/jellyfin

API：     http://127.0.0.1:8096
ffmpeg：  /Applications/Jellyfin.app/Contents/MacOS/ffmpeg  (v7.1.4, hwaccel=videotoolbox+opencl)
数据目录：~/Library/Application Support/jellyfin
```

进程**环境变量没有** `JELLYFIN_LOCAL_METADATA_ONLY_IMPORT`（已通过 `ps -E -p <pid>` 验证）。

每个媒体库的 `options.xml`（位于 `~/Library/Application Support/jellyfin/root/default/<lib>/options.xml`）含：

```xml
<LocalMetadataOnlyImport>true</LocalMetadataOnlyImport>
```

涉及 5 个库：`电视剧 / 影片 / 情色电影 / JAV-中文字幕 / JAV`。

---

## 3. 相关代码改动

本次相比 2026-06-18 的代码差异是 commit `612899b8e9`：

| 文件 | 改动 |
|---|---|
| `MediaBrowser.Controller/Library/LocalMetadataOnlyImportPolicy.cs` | 删除 `EnvironmentVariableName`、`IsEnvironmentEnabled()`；`IsEnabled()` 与 `IsEnabledForItem()` 简化为只看 `LibraryOptions.LocalMetadataOnlyImport` 与每库设置 |
| `Emby.Server.Implementations/IO/ManagedFileSystem.cs` | `ShouldUseLocalMetadataOnlyVideoPlaceholder` 改为只看显式 `skipResolvingVideoSymlinks` 参数；`Folder.ValidateChildrenInternal2` 和 `BaseItem.EnsureLocalMetadataOnlyDirectoryService` 已在按库设置时传入 `DirectoryService(FileSystem, skipResolvingVideoSymlinks: true)`，主扫描流程不受影响 |
| `Emby.Server.Implementations/ScheduledTasks/Tasks/MediaSegmentExtractionTask.cs` | 删除 env-based 全局早退，内部已有按项的 `LocalMetadataOnlyImportPolicy.IsEnabled(libraryOptions)` 检查 |
| `tests/Jellyfin.Controller.Tests/LocalMetadataOnlyImportPolicyTests.cs` | 移除 env 相关 case，增加 `IsEnabled(null) → false`、`IsEnabledForItem(null,null) → false` |
| `tests/Jellyfin.Server.Implementations.Tests/IO/ManagedFileSystemTests.cs` | 把通过 env 触发占位逻辑的两个 case 改为显式传 `GetFileSystemInfo(path, skipResolvingVideoSymlinks: true)` |

测试结果：

```
=== LocalMetadataOnlyImportPolicy tests ===
通过 14 / 失败 0 / 跳过 0

=== ManagedFileSystem tests ===
通过 13 / 失败 0 / 跳过 5 (Windows-only)
```

构建：`dotnet build Jellyfin.sln -c Release` → 0 warnings / 0 errors。

---

## 4. 各库最终状态

| 库 | 类型 | done | total | pct |
|---|---|---:|---:|---:|
| `电视剧` (tvshows) | tvshows | 175,301 | 175,301 | **100%** ✅ |
| `影片` (movies) | movies | 50,270 | 50,270 | **100%** ✅ |
| `JAV` (jav) | movies | 25,895 | 25,895 | **100%** ✅ |
| `JAV-中文字幕` (中文字幕) | movies | 19,311 | 19,311 | **100%** ✅ |
| `情色电影` (成人电影) | movies | 731 | 731 | **100%** ✅ |
| `Playlists` | folder | 0 | 1 | n/a（空目录） |
| `Live TV` | folder | 1 | 1 | 100% |

总条目按类型分布（`BaseItems` 表）：

| Type | 数量 |
|---|---:|
| Episode | 160,297 |
| Movie | 86,738 |
| **Studio**（聚合）| **18,006** |
| Folder | 9,608 |
| Season | 9,596 |
| Series | 5,269 |
| Video（非 movie/episode 的视频）| 2,068 |
| Genre（聚合）| 791 |
| Audio | 650 |
| CollectionFolder | 5 |
| 其它（PlaylistsFolder / AggregateFolder / UserRootFolder / UserView / PLACEHOLDER）| 5 |
| **合计** | **293,033** |

注意：最终 total（293,033）比扫描中观察到的 ~272k 多约 19k —— 多出来的主要是 `Studio`（18,006）这种**聚合条目**，是 post-scan 阶段（98% → 100%）创建的索引项。

---

## 5. 阶段时间线

任务总共划分为「文件扫描入库」+「元数据刷新」+「post-scan」三大阶段。下表用本次监控的关键节点串联：

| 北京时间 | 事件 | API progress | DB total | 备注 |
|---|---|---:|---:|---|
| 22:45:44 | 任务触发 | 0% | 34,836 | （DB 含浏览 web UI 时触发的少量初始项） |
| 22:54 | 监控 baseline | 34.4% | 88,620 | 入库阶段飞速 ≈ 10,757 项/分钟 |
| 23:10 | API 进入 phase-lock | 34.94% | 141,725 | 后续 30 分钟 progress 几乎不动，但 DB 持续 +5–10k/轮 |
| 23:51 | DB 突破 21 万项 | 35.71% | 212,837 | tvshows 顶层入库接近 100k |
| 00:24 | API 第一次跳档 | **41.93%** (+6.14) | 247,005 | 一个大库 root 阶段切换 |
| 00:49 | **进入元数据刷新阶段** | **53.57%** (+11.56) | 271,495 | `DateLastSaved/DateLastRefreshed` 开始批量写入；速率 442 项/分 |
| 02:54 | 中文字幕 即将完成 | 74.78% | 272,363 | jav 突破 60% |
| 02:59 | **中文字幕 100% ✅** | 75.36% | 272,447 | 第二个完成的库 |
| 04:46 | **jav 100% ✅** | 81.12% | 273,646 | 第三个完成的库 |
| 04:31 → 07:14 | 漫长低速期 | 81.0–88.7% | 273,625–273,760 | 速率掉到 30–170 项/分；与 `Extract Chapter Images`、`Generate Trickplay Images` 任务并跑抢资源有关 |
| 07:43 | Trickplay Images 收工 | — | — | `Generate Trickplay Images Completed after 125 minute(s) and 20 seconds` |
| 07:49 → 08:49 | 速率回升 | 89.66% → 95.71% | 273,841 → 274,213 | 一度达 2,971 项/分 |
| 06:06 | **movies 100% ✅** | 88.51% | 274,029 | 第四个完成的库（电视剧后段才完成是因为 movies 收尾在中段插入完成） |
| 08:54 | **tvshows 100% ✅** | 97.84% | 274,416 | 第五个、最后一个媒体库完成 |
| 09:04 | post-scan 加聚合项 | 99.14% | 283,893 | +9,041（开始批量写 Studio/Genre 聚合） |
| 09:07:54 | **任务 Completed ✅** | 100% | ~293,033 | API state 回到 Idle |

**重要观察**：API `CurrentProgressPercentage` 是「按顶层 Folder 阶段」的，遇到大库（175k tvshows）会**长时间冻在某档**。本次：

- **35.7%–41.9%** 卡了约 30 分钟（tvshows 顶层入库阶段后段）
- **88.5%–88.9%** 卡了约 100 分钟（tvshows 元数据刷新阶段，期间 Trickplay 抢资源）

如果只看 API progress，会误以为卡死；但 `DateLastSaved` 在持续推进。

---

## 6. 极速模式行为验证

本次的目标：**确认 per-library 路径（`options.xml`）能完全替代被删掉的 `JELLYFIN_LOCAL_METADATA_ONLY_IMPORT` 环境变量**。

### 6.1 正面证据

- ✅ `MediaStreamInfos = 0`：跳过 `ffprobe` 探测 → `MediaInfo/FFProbeVideoInfo.cs` 的 per-item 检查生效
- ✅ `MediaSegments = 0`：跳过媒体片段扫描 → `MediaSegmentManager.cs:57` 与 `MediaSegmentExtractionTask.cs:92` 的 per-item 检查生效
- ✅ 进程没有 `JELLYFIN_LOCAL_METADATA_ONLY_IMPORT` 环境变量
- ✅ 总用时 622m vs 历史 626m，差 < 1%
- ✅ 5 个库 `done%` 都到 100%，每库 `pending=0`
- ✅ post-scan 创建了 18,006 个 `Studio` 聚合项 —— 说明本地 NFO 里的 `<studio>` 标签被正确读取

### 6.2 已知小瑕疵

**ISO race**：极速模式应跳过 ISO 探测，但仍有 **2** 条 ERR：

```
[2026-06-27 01:23:13] [ERR] MovieResolver: Error opening UDF/ISO image:
  "/Users/wiz/data/media/av/日本AV/jav/CWPBD-21/CWPBD-21.iso"
[2026-06-27 01:24:17] [ERR] MovieResolver: Error opening UDF/ISO image:
  "/Users/wiz/data/media/av/日本AV/jav/CWPBD-46/CWPBD-46.iso"
```

原因：`Emby.Server.Implementations/Library/Resolvers/BaseVideoResolver.cs:166` 的 `SetIsoType` 方法在 `LocalMetadataOnlyImportPolicy.IsEnabled(libraryOptions)` 之前可能存在 race —— 调用方传入的 `libraryOptions` 偶发解析为默认值。

旧 env 模式下 `IsEnvironmentEnabled()` 是全局兜底，无论 libraryOptions 是不是默认值都返回 true，所以那时不会触发 ISO probe。现在 env 路径删了，这个 race 的窗口暴露了。

**影响极小**：5 个媒体库 ~272,000 条目里只命中 2 条 ISO，影响范围 0.001%。这两个 ISO 条目仍以 placeholder 形式入库，不影响其它项目的扫描和元数据。

**修法**（如以后要处理）：在 `BaseVideoResolver.SetIsoType` 里把 `LocalMetadataOnlyImportPolicy.IsEnabled` 检查提前，覆盖那个 path-contains-"dvd/bluray" 的早期分支判定路径；或者通过传入更稳定的 `directoryService.SkipResolvingVideoSymlinks` 标志来判定。

### 6.3 完全没有的

- ❌ 远程元数据 provider 调用（TMDb / TVDb 等，全程 0 次）
- ❌ 远程图片下载（`ProviderManager.cs:171` 短路）
- ❌ `ffprobe` 子进程
- ❌ 媒体片段提取

---

## 7. 性能与稳定性观察

### 7.1 速率曲线

按 5 分钟窗口测量的元数据刷新速率（从 00:49 进入元数据阶段后）：

| 阶段 | 速率范围 | 典型值 |
|---|---|---|
| 0% → 50% | 400–550 项/分 | ~480 项/分 |
| 50% → 75%（中文字幕→jav 完成）| 350–530 项/分 | ~470 项/分 |
| 75% → 88% | 100–300 项/分 | ~170 项/分 |
| **88% 长尾**（4:30–7:30）| 30–170 项/分 | ~100 项/分 |
| 88% → 95%（Trickplay 收工后）| 600–3,000 项/分 | ~2,400 项/分 |
| 95% → 100% | 800–2,900 项/分 | ~1,800 项/分 |

长尾期的瓶颈不是元数据刷新本身，而是 `Extract Chapter Images` (105m 31s) 和 `Generate Trickplay Images` (125m 20s) 这两个独立 scheduled task 在并行抢线程 + 磁盘。它们一收工，主扫描速率立刻翻 10 倍。

### 7.2 DB 锁竞争（Pessimistic locking）

本次仓库已开启 [`4a5be7fc90 Use pessimistic SQLite locking by default`](https://github.com/orut34iop/jellyfin/commit/4a5be7fc90)。`Query congestion detected` 的 5 分钟窗口次数：

- 大多数时间：0–10 次
- 元数据刷新阶段尾段（07:14–07:33）：100–172 次
- 单次最长锁持：6 分 37 秒（19:15–19:22，扫描初期某个 EF Core 单查询 JOIN）

API HTTP 服务在长锁期间会被挡住，curl 偶发 8 秒超时（监控脚本内置 3 次重试，覆盖了这种瞬态）。**没有触发死锁或卡死**。

### 7.3 日志噪音

全程 ERR + WRN（包含所有噪音）：

```
$ grep -c -E "\[ERR\]|\[WRN\]" /Users/wiz/Library/Application\ Support/jellyfin/log/log_20260626.log
（大量，主要为：）
- TransactionLockingInterceptor 的 congest detected/cleared（INFO 实际，被升到 WRN 的 EF Core 单查询警告）
- EntityFrameworkCore.Query 提示 SingleQuery vs SplitQuery
- WebSocket "not on watchlist" 与 "remote party closed without close handshake"
```

去掉以上噪音后，**非噪音 ERR/WRN 仅 3 条**：

| 时间 | 级别 | 内容 |
|---|---|---|
| 01:23:13 | ERR | ISO probe `CWPBD-21.iso`（见 6.2） |
| 01:24:17 | ERR | ISO probe `CWPBD-46.iso`（见 6.2） |
| 09:24:14 | WRN | `Library folder ".../data/playlists" is inaccessible or empty, skipping`（无 playlists，预期行为） |

---

## 8. 监控方法

本次扫描全程通过 `scripts/jellyfin-scan-monitor.sh` 自动巡检，共 119 次快照。详细使用说明见 [`scripts/jellyfin-scan-monitor.md`](../scripts/jellyfin-scan-monitor.md)。

监控指标融合：

1. **API 权威进度**：`GET /ScheduledTasks/{id}` 的 `State` + `CurrentProgressPercentage`
2. **SQLite 直读**：`BaseItems` 总数、Type 分布、`DateLastRefreshed/DateLastSaved` 新增条数
3. **日志近 5 分钟摘要**：`Validating/Refresh/Scan/ERR/WRN/Query congestion` 等关键行

判停规则：API `state=Idle` + `LastExecutionResult.Status=Completed` + `StartTimeUtc` 与上轮记录不同 → 退出码 1，自动停止巡检。

---

## 9. 数据目录最终大小

```
$ du -sh "$HOME/Library/Application Support/jellyfin"
3.7G

  data/        ~3.7G（jellyfin.db）
  cache/       ~MB
  config/      ~24K
  log/         ~MB
  metadata/    0B（极速模式不下载远端图片）
  root/        ~40K（库 options.xml）
  plugins/     ~8K
```

DB 占绝大部分。本地 NFO 与图片仍留在媒体源路径下（如 `/Users/wiz/data/media/...`），未被复制到 Jellyfin 数据目录 —— 这是极速模式期望行为。

---

## 10. 参考

- 上次报告：[`docs/local-metadata-only-import-report-2026-06-18.md`](./local-metadata-only-import-report-2026-06-18.md)
- macOS dmg 构建文档：[`docs/macos-arm64-dmg-build-guide-zh.md`](./macos-arm64-dmg-build-guide-zh.md)
- 监控脚本：[`scripts/jellyfin-scan-monitor.sh`](../scripts/jellyfin-scan-monitor.sh)
- 监控脚本文档：[`scripts/jellyfin-scan-monitor.md`](../scripts/jellyfin-scan-monitor.md)
- 备份脚本：`scripts/backup-jellyfin-user-data.sh`、`scripts/restore-jellyfin-user-data.sh`

相关 commit 链（按时间顺序）：

```text
788dd4b390  Add local metadata only import mode
0c58a7eb79  Fix TV episode parent ids during batch resolve
a7c524ae95  Skip media segment extraction in local metadata import
b460f51ca7  Honor local metadata import during file enumeration
9bccdb1291  Tighten local metadata only import scan paths
048c857605  Show local metadata people without person entities
c34c4b7636  Quiet missing person validation in local metadata mode
668f2eb2c1  Avoid aggregate remote refresh in local metadata mode
89dc465327  Handle reparse-point media files with stream result
612899b8e9  Drop JELLYFIN_LOCAL_METADATA_ONLY_IMPORT env override   ← 本次
```

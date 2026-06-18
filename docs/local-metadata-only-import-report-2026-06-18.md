# Local Metadata Only Import 导入报告

日期：2026-06-18  
仓库：`/Users/wiz/dev/jellyfin`  
分支：`release-10.11.z`  
Jellyfin Server 版本：`10.11.11`  
运行方式：macOS Application，`/Applications/Jellyfin.app`

## 1. 报告结论

本次媒体库扫描已完成，最终任务状态为 `Completed`。

本次导入符合 `LocalMetadataOnlyImport` 的目标：

- 影片和电视剧条目已大量导入。
- 本地 NFO 元数据被导入。
- 本地图片被关联。
- 未发现 Jellyfin 在扫描阶段打开视频文件。
- 未发现 Jellyfin 启动 `ffprobe` 子进程。
- 未发现 ISO/UDF 解析错误。
- 未发现远程元数据或远程图片下载行为。
- `MediaStreamInfos` 为 `0`，符合“跳过媒体信息探测”的预期。
- `MediaSegments` 为 `0`，符合“跳过媒体段提取”的预期。

唯一持续出现的噪音是 `Cannot compute blurhash`，这是本地图片 blurhash 占位图计算失败，不代表访问视频文件或发起网络请求。

## 2. 功能开关与运行环境

本次验证使用环境变量全局开启：

```bash
JELLYFIN_LOCAL_METADATA_ONLY_IMPORT=true
```

运行进程：

```text
/Applications/Jellyfin.app/Contents/Resources/jellyfin/jellyfin --ffmpeg /opt/homebrew/bin/ffmpeg
```

监测期间确认：

- Jellyfin 主进程 PID：`88798`
- 本地 API：`http://127.0.0.1:8096`
- 任务 ID：`7738148ffcd07979c7ceb148e06b3aed`
- 任务 Key：`RefreshLibrary`

## 3. 本次相关代码提交

本次本地-only导入能力已经提交并推送到 fork 的 `release-10.11.z` 分支。

| Commit | 内容 |
| --- | --- |
| `788dd4b390` | Add local metadata only import mode |
| `0c58a7eb79` | Fix TV episode parent ids during batch resolve |
| `a7c524ae95` | Skip media segment extraction in local metadata import |

主要实现范围：

- `LocalMetadataOnlyImportPolicy`
- `LibraryOptions.LocalMetadataOnlyImport`
- 跳过 ISO/UDF 文件打开
- 跳过 `ffprobe` / media probing
- 视频 symlink 不跟随真实目标
- 禁止远程 metadata provider / remote image provider
- 禁止远程图片转本地
- 跳过媒体片段扫描
- 修复 TV episode 父子关系在批量解析时的挂载问题

## 4. 扫描任务时间

Jellyfin API 返回的最终任务结果：

```text
State: Idle
Status: Completed
StartTimeUtc: 2026-06-17T15:03:56.037839Z
EndTimeUtc: 2026-06-18T01:29:45.353064Z
```

换算为本地时间 `Asia/Shanghai`：

```text
开始：2026-06-17 23:03:56
结束：2026-06-18 09:29:45
耗时：约 10 小时 25 分 49 秒
```

说明：监测过程中 API 的 `LastExecutionResult` 曾保留一条历史失败信息：

```text
Cannot access a disposed object.
Object name: 'IServiceProvider'.
```

这条信息来自上一次执行结果，不是本次最终结果。本次最终结果已经变为 `Completed`。

## 5. 进度曲线摘要

扫描进度不是线性更新，而是明显的“批次式推进”：有时连续几轮保持不变，有时一次跳动较大。整体趋势持续向前，没有观察到真正卡死。

关键采样点如下，时间为本地时间 `Asia/Shanghai`：

| 时间 | 状态 | 进度 | 说明 |
| --- | --- | ---: | --- |
| 02:31:58 | Running | 60.24% | 开始持续监测后的早期样本 |
| 02:44:30 | Running | 60.72% | 缓慢推进 |
| 02:49:33 | Running | 61.20% | 继续推进 |
| 05:51:25 | Running | 77.28% | 中段详细快照 |
| 06:05:25 | Running | 78.00% | 阶段性推进 |
| 06:44:37 | Running | 80.16% | 进入 80% |
| 07:14:40 | Running | 82.56% | 批次完成后明显跳动 |
| 07:17:40 | Running | 86.16% | 大幅跳动 |
| 07:20:40 | Running | 88.56% | 后段加速 |
| 07:35:41 | Running | 90.00% | 进入最后 10% |
| 08:44:41 | Running | 95.04% | 进入最后 5% |
| 09:18:53 | Running | 98.88% | 收尾阶段 |
| 09:24:55 | Running | 99.03% | 超过 99% |
| 09:27:58 | Running | 99.28% | 最后一段继续推进 |
| 09:31:00 | Idle | - | 任务结束 |

阶段性观察：

- `60%` 到 `77%`：整体较慢，但持续增加。
- `77%` 到 `82%`：多次短暂停顿，然后继续跳动。
- `82%` 到 `89%`：出现明显大跳，说明前面批处理完成。
- `90%` 到 `95%`：较平滑，基本每 3 分钟约 `+0.24%`。
- `95%` 到 `99%`：进入收尾阶段，进度单位变小。
- `99%` 到完成：任务从 `Running` 变为 `Idle`，最终状态 `Completed`。

## 6. 最终数据统计

最终数据库统计来自：

- `BaseItems`
- `BaseItemImageInfos`
- `MediaStreamInfos`
- `MediaSegments`

最终快照：

| 指标 | 数量 |
| --- | ---: |
| 总条目 | 672,730 |
| 电影 Movie | 41,038 |
| 电视剧 Series | 5,251 |
| 季 Season | 9,577 |
| 集 Episode | 159,691 |
| Episode 有 `SeriesId` | 159,691 |
| Episode 有 `SeasonId` | 159,691 |
| 电影有简介 | 40,416 |
| 剧集有简介 | 138,441 |
| 图片关系 | 440,357 |
| MediaStreamInfos | 0 |
| MediaSegments | 0 |

重要结论：

- 所有 Episode 都有 `SeriesId`。
- 所有 Episode 都有 `SeasonId`。
- 电视剧父子关系正常。
- `MediaStreamInfos=0`，说明没有生成 ffprobe 媒体流信息。
- `MediaSegments=0`，说明没有执行媒体分段提取。

## 7. 本地用户数据占用统计

统计范围：

```text
/Users/wiz/Library/Application Support/jellyfin
```

该目录是本次 macOS Jellyfin App 的主要本地用户数据目录，包含配置、数据库、缓存、日志、插件和运行数据。检查时没有发现额外的 macOS Caches/Logs 目录：

```text
/Users/wiz/Library/Caches/jellyfin: missing
/Users/wiz/Library/Caches/Jellyfin: missing
/Users/wiz/Library/Logs/jellyfin: missing
/Users/wiz/Library/Logs/Jellyfin: missing
```

### 总占用

```text
Total: 4,160,448 KB
Total: 4,062.94 MiB
Total: 3.97 GiB
```

### 一级目录占用

| 路径 | 大小 | MiB | 占比 | 文件数 |
| --- | ---: | ---: | ---: | ---: |
| `data` | 4,136,516 KB | 4,039.57 MiB | 99.42% | 22 |
| `cache` | 14,412 KB | 14.07 MiB | 0.35% | 165 |
| `log` | 9,472 KB | 9.25 MiB | 0.23% | 4 |
| `config` | 24 KB | 0.02 MiB | 0.00058% | 6 |
| `root` | 16 KB | 0.02 MiB | 0.00038% | 7 |
| `plugins` | 8 KB | 0.01 MiB | 0.00019% | 3 |
| `.jellyfin-data` | 0 KB | 0 MiB | 0% | 1 |
| `metadata` | 0 KB | 0 MiB | 0% | 0 |

结论：本地用户数据几乎全部来自 `data` 目录，占总量约 `99.42%`。

### data 目录文件占用

| 文件 | 大小 | MiB/GiB | 占总用户数据比例 |
| --- | ---: | ---: | ---: |
| `jellyfin.db` | 4,132,340 KB | 4,035.49 MiB / 3.94 GiB | 99.32% |
| `splashscreen.png` | 3,572 KB | 3.49 MiB | 0.09% |
| `jellyfin.db-wal` | 504 KB | 0.49 MiB | 0.0121% |
| `jellyfin.db-shm` | 32 KB | 0.03 MiB | 0.0008% |
| `device.txt` | 4 KB | 0.004 MiB | <0.001% |
| `.jellyfin-data` | 0 KB | 0 MiB | 0% |

结论：导入完成后，本地用户数据主要由 SQLite 主库 `jellyfin.db` 占用。该文件约 `3.94 GiB`，约占整个 Jellyfin 本地用户数据目录的 `99.32%`。

### cache 目录占用

| 路径 | 大小 | 文件数 |
| --- | ---: | ---: |
| `cache/images` | 14,412 KB | 163 |
| `cache/transcodes` | 0 KB | 1 |
| `cache/.jellyfin-cache` | 0 KB | 1 |

结论：本次导入后的 cache 目录很小，主要是 `cache/images`，约 `14.07 MiB`。

### log 目录占用

| 文件 | 大小 |
| --- | ---: |
| `log_20260618.log` | 9,220 KB |
| `log_20260617.log` | 232 KB |
| `FFmpeg.Transcode-2026-06-18_09-59-37_c658be73b67e35a8a35f1864e60b982a_4f20a27d.log` | 20 KB |
| `.jellyfin-log` | 0 KB |

说明：`FFmpeg.Transcode-*` 日志出现在导入完成后的日志目录里。它不是本次媒体库扫描期间的 ffprobe 媒体探测结果；最终扫描验收里 `MediaStreamInfos=0`，且最近扫描日志禁用关键词命中为 `0`。

### 占用结构结论

导入完成后的本地用户数据占用结构非常集中：

- 主数据库 `jellyfin.db`：约 `3.94 GiB`
- 其他数据库辅助文件：不足 `1 MiB`
- 图片缓存：约 `14.07 MiB`
- 日志：约 `9.25 MiB`
- 配置、插件、root、metadata：可以忽略不计

这说明本次本地-only导入没有把本地图片大规模复制进 Jellyfin metadata/cache 目录；主要空间成本是 Jellyfin 数据库中的条目、人员、图片关系、元数据索引等结构化数据。

## 8. 中间快照对比

### 77% 快照

```text
Progress: 77.28%
Total items: 593,417
Movies: 41,038
Series: 5,251
Seasons: 9,558
Episodes: 159,691
Episode with SeriesId: 159,691
Episode with SeasonId: 159,691
Images: 339,447
MediaStreamInfos: 0
MediaSegments: 0
```

### 90% 快照

```text
Progress: 90.24%
Total items: 638,485
Movies: 41,038
Series: 5,251
Seasons: 9,567
Episodes: 159,691
Episode with SeriesId: 159,691
Episode with SeasonId: 159,691
Movie overview: 40,416
Episode overview: 106,056
Images: 397,404
MediaStreamInfos: 0
MediaSegments: 0
```

### 95% 快照

```text
Progress: 95.04%
Total items: 652,807
Movies: 41,038
Series: 5,251
Seasons: 9,576
Episodes: 159,691
Episode with SeriesId: 159,691
Episode with SeasonId: 159,691
Movie overview: 40,416
Episode overview: 133,665
Images: 433,519
MediaStreamInfos: 0
MediaSegments: 0
```

### 完成快照

```text
Task State: Idle
Task Status: Completed
Total items: 672,730
Movies: 41,038
Series: 5,251
Seasons: 9,577
Episodes: 159,691
Episode with SeriesId: 159,691
Episode with SeasonId: 159,691
Movie overview: 40,416
Episode overview: 138,441
Images: 440,357
MediaStreamInfos: 0
MediaSegments: 0
```

变化趋势：

| 阶段 | 总条目 | 图片关系 | 剧集简介 |
| --- | ---: | ---: | ---: |
| 77% | 593,417 | 339,447 | 84,956 |
| 90% | 638,485 | 397,404 | 106,056 |
| 95% | 652,807 | 433,519 | 133,665 |
| 完成 | 672,730 | 440,357 | 138,441 |

这说明在后半段，Jellyfin 仍然在继续写入元数据、图片关系和索引相关条目，并不是单纯等待。

## 9. 本地-only验收指标

最终进程与资源检查：

```text
Jellyfin PID: 88798
Children: 0
Video file descriptors: 0
External TCP connections: 0
```

含义：

- `Children=0`：没有看到 Jellyfin 派生 `ffprobe` / `ffmpeg` 子进程。
- `Video file descriptors=0`：没有看到 Jellyfin 当前打开 `.iso`、`.mkv`、`.mp4` 等视频文件。
- `External TCP connections=0`：没有看到 Jellyfin 对外建立 TCP 连接。

注意：监测过程中曾出现一次 `external_tcp=879` 的误报。原因是当时 `lsof` 没有使用 `-a -p <pid> -iTCP` 组合过滤，导致混入了全系统 TCP 连接。修正命令后 Jellyfin 外部 TCP 连接为 `0`。

## 10. 日志验收

最终检查最近日志：

```text
Latest log:
/Users/wiz/Library/Application Support/jellyfin/log/log_20260618.log

Forbidden log hits in last 5000 lines: 0
Blurhash hits in last 5000 lines: 148
```

禁止类关键词检查范围：

- `Probe Provider`
- `ffprobe`
- `GetMediaInfo`
- `Error opening UDF`
- `Cannot fetch image`
- `image.tmdb.org`
- `m.media-amazon.com`
- `omdb`
- `tmdb`
- `tvdb`

结果：

```text
Forbidden log hits: 0
```

这说明在最终日志窗口内没有观察到：

- ffprobe 调用失败
- Probe Provider 错误
- ISO/UDF 打开错误
- 远程图片下载错误
- TMDb/OMDb/TVDb 相关远程请求痕迹

## 11. Blurhash 说明

日志中仍存在：

```text
Cannot compute blurhash
```

这类错误来自本地图片处理链路：

```text
LibraryManager.UpdateImagesAsync
ImageProcessor.GetImageBlurHash
SkiaEncoder.GetImageBlurHash
BlurHashSharp.SkiaSharp.BlurHashEncoder.Encode
```

它的含义是 Jellyfin 尝试为本地海报、剧照或背景图生成模糊占位符时失败。

这不是以下行为：

- 不是读取视频文件。
- 不是跟随视频 symlink。
- 不是 ffprobe。
- 不是 ISO 解析。
- 不是远程图片下载。
- 不是远程元数据请求。

影响：

- 可能导致部分图片缺少模糊占位图。
- 不影响条目导入。
- 不影响本地 NFO 元数据导入。
- 会增加一些日志噪音。

后续可选优化：

- 在 `LocalMetadataOnlyImport` 开启时跳过 blurhash 计算。
- 或将 blurhash 失败日志降级/去重，减少大量图片导入时的日志噪音。

## 12. 电影与电视剧导入结果

### 电影

最终电影数量：

```text
Movies: 41,038
Movies with overview: 40,416
```

说明：

- 绝大多数电影已经导入简介。
- 没有生成媒体流信息，符合跳过 ffprobe 的预期。
- 本地图片关系已大量写入。

### 电视剧

最终电视剧数量：

```text
Series: 5,251
Seasons: 9,577
Episodes: 159,691
Episode with SeriesId: 159,691
Episode with SeasonId: 159,691
Episodes with overview: 138,441
```

说明：

- Episode 全部挂到了 Series。
- Episode 全部挂到了 Season。
- 电视剧父子关系正常。
- 剧集简介大量导入。

这也验证了 `Fix TV episode parent ids during batch resolve` 这部分修复方向是正确的。

## 13. 与目标验收项逐项对照

| 验收项 | 结果 | 证据 |
| --- | --- | --- |
| 影片条目可以导入 | 通过 | `Movies=41,038` |
| 电视剧条目可以导入 | 通过 | `Series=5,251`，`Episodes=159,691` |
| 标题/简介等从本地 NFO 导入 | 通过 | `Movie overview=40,416`，`Episode overview=138,441` |
| 本地图片导入 | 通过 | `Images=440,357` |
| 不访问视频真实文件 | 通过 | `video_fds=0` |
| 不跟随视频 symlink | 未发现反例 | 无 broken symlink 相关 FileNotFoundException 大量出现 |
| 不运行 ffprobe | 通过 | `children=0`，`MediaStreamInfos=0`，日志关键词 `ffprobe=0` |
| 不解析 ISO/UDF | 通过 | 日志关键词 `Error opening UDF=0` |
| 不发远程元数据请求 | 通过 | `external_tcp=0`，日志关键词 `tmdb/omdb/tvdb=0` |
| 不发远程图片请求 | 通过 | 日志关键词 `Cannot fetch image=0`，`image.tmdb.org=0`，`m.media-amazon.com=0` |
| 默认媒体流可为空 | 通过 | `MediaStreamInfos=0` |
| 媒体段不生成 | 通过 | `MediaSegments=0` |

## 14. 注意事项

1. 本次监测基于运行时 API、SQLite 数据库、进程句柄、TCP 连接和日志关键词综合判断。
2. `video_fds=0` 表示采样时 Jellyfin 没有打开视频文件句柄；它不能数学上证明扫描全程每一毫秒都没有瞬时打开文件，但结合代码改动、日志、子进程和数据库结果，已经能较强地支持本地-only验收。
3. `external_tcp=0` 表示采样时 Jellyfin 没有外部 TCP 连接；结合日志中远程 provider 和远程图片关键词为 0，可以判断本次扫描没有观察到远程抓取行为。
4. `MediaStreamInfos=0` 是本次最重要的结果之一，说明扫描没有通过 ffprobe 写入媒体流信息。
5. `MediaSegments=0` 说明媒体段提取也被有效绕过。

## 15. 后续建议

建议后续做两个小优化：

1. 在 `LocalMetadataOnlyImport` 下跳过 blurhash 计算，减少图片导入阶段 CPU 和日志噪音。
2. 在 Web UI 或库配置中暴露 `LibraryOptions.LocalMetadataOnlyImport`，让这个模式可以按媒体库单独开启，而不是只能依赖全局环境变量。

## 16. 最终结论

本次 `LocalMetadataOnlyImport` 大规模媒体库导入验证成功。

在约 `10 小时 25 分 49 秒` 的扫描过程中，Jellyfin 成功导入了电影、电视剧、季、集、本地简介和本地图片关系。最终没有观察到 ffprobe、ISO/UDF 解析、远程元数据、远程图片下载、视频文件打开或媒体段生成。

该模式已经可以作为大规模本地 NFO + 本地图片 + 视频 symlink 媒体库的极速导入路径继续验证。

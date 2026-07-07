# Moonfin 本机本地文件直连播放维护记录

日期：2026-07-07
仓库：`/Users/wiz/dev/jellyfin`
分支：`release-10.11.z`
相关提交：

- `62f1878db3 Enable local Moonfin media paths`
- `d42b35dff8 Harden Moonfin local path playback`
- `0a0fb145a9 Skip probing Moonfin local file playback`

---

## 1. 背景

当前媒体库里的视频文件多数是符号链接，符号链接目标是 115 网盘通过 CloudDrive App 挂载到本机虚拟盘后的文件。Jellyfin Server 与 Moonfin 播放器运行在同一台 Mac 上。

原始播放链路是 Moonfin 从 Jellyfin 获取 HTTP/HLS/转码地址，再由 Jellyfin 读取本地文件并中转视频流。对于同机播放，这一层中转没有必要，还会引入 Jellyfin ffprobe、转码、动态 HLS playlist 时长等额外问题。

本次修改的目标是：

- 本机 Moonfin 请求 Jellyfin 播放信息时，Jellyfin 直接把媒体库 item 的本地文件路径放进原有 `MediaSourceInfo.Path` 字段。
- Moonfin 识别 `Protocol = File` 且 `Path` 是本地绝对路径后，直接用本机播放器打开文件。
- 不新增 API 字段，不新增配置项，不影响非 Moonfin 客户端。

---

## 2. 生效条件

只有同时满足以下条件才会下发本地文件路径：

| 条件 | 说明 |
|---|---|
| 请求来自本机 | Controller 使用 `HttpContext.IsLocal()` 判断 |
| 客户端是 Moonfin | `Authorization` / 设备信息里的 client 名包含 `Moonfin`，大小写不敏感 |
| 媒体源是本地文件 | `MediaSourceInfo.Protocol == MediaProtocol.File` |
| 文件路径存在 | 使用 `File.Exists(item.Path)` 检查 |

如果任一条件不满足，维持 Jellyfin 原始播放信息行为。

---

## 3. 主要代码位置

| 文件 | 作用 |
|---|---|
| `Jellyfin.Api/Controllers/MediaInfoController.cs` | 在 GET/POST `/Items/{id}/PlaybackInfo` 后处理 `MediaSourceInfo.Path`，并决定是否跳过媒体探测 |
| `Jellyfin.Api/Helpers/MoonfinLocalPlaybackHelper.cs` | 封装 Moonfin 本地路径判断、media source item 解析、Path 替换 |
| `Jellyfin.Api/Helpers/MediaInfoHelper.cs` | `GetPlaybackInfo(..., allowMediaProbe = true)` 增加可选参数，允许特殊路径跳过 probe |
| `tests/Jellyfin.Api.Tests/Helpers/MoonfinLocalPlaybackHelperTests.cs` | 覆盖本机 Moonfin、非 Moonfin、非 File protocol、文件不存在、指定 media source 等场景 |

核心 helper 行为：

```text
TryResolveLocalFilePath(mediaSource, item, getItemById, isLocalRequest, client)
  -> null: 使用 Jellyfin 原有 streaming/transcode 行为
  -> path: 把 MediaSourceInfo.Path 替换成 item.Path
```

如果 `mediaSource.Id` 指向的是另一个 `BaseItem.Id`，helper 会先通过 `ILibraryManager.GetItemById` 找到该 source item，再使用 source item 的 `Path`。这是为了兼容多版本 / alternate media source 场景。

---

## 4. PlaybackInfo 行为

修改后没有新增字段。Moonfin 仍读取原来的 `MediaSources[*]`。

符合条件时，Jellyfin 返回的关键字段类似：

```json
{
  "MediaSources": [
    {
      "Protocol": "File",
      "Path": "/Users/wiz/media/tvshows/.../Episode.mp4",
      "TranscodingUrl": null
    }
  ]
}
```

注意：

- `Path` 是 Jellyfin 媒体库中的符号链接路径，不是 CloudDrive 目标真实路径。
- 对 Moonfin 本地直连来说，`VideoCodec` / `AudioCodec` 可以为空。Moonfin 应在 `Protocol = File` 时直接打开本地文件，不依赖这两个字段决定播放。
- 这不是 115 网盘 302 直链功能，也没有把 115 信息转换为 HTTP 直链。

---

## 5. 跳过媒体探测

在 Moonfin 本地文件播放路径中，Jellyfin 可以跳过 `AddMissingMediaInfoWithProbe` / ffprobe，原因是：

- Moonfin 会直接打开本地文件，不需要 Jellyfin 计算转码参数。
- CloudDrive / 115 虚拟盘上的符号链接文件在 ffprobe 时可能慢、失败或触发额外网络读。
- 当 media streams 缺失时，旧 Moonfin 可能回退到 transcode；现在 Jellyfin 明确下发本地 `File` path 后，Moonfin 不应依赖 Jellyfin 的 codec 字段。

Controller 中的关键分支：

```text
ShouldSkipMoonfinMediaProbe(...)
  true -> GetPlaybackInfo(..., allowMediaProbe: false)
          POST PlaybackInfo 不再调用 SetDeviceSpecificData
  false -> 原有 Jellyfin probe / device profile 逻辑
```

---

## 6. 日志特征

成功命中本地文件路径时，Jellyfin 主日志会出现：

```text
Skipping media probe for Moonfin local file playback. ItemId: ..., MediaSourceId: ...
Using local file path for Moonfin playback. ItemId: ..., MediaSourceId: ..., Path: "/Users/wiz/media/..."
```

Moonfin 侧 `playback_diagnostics.jsonl` 成功直连时应看到：

```json
{
  "event": "playbackDecision",
  "playMethod": "directPlay"
}
```

以及 `mediaKitOpenStart` 中：

```json
{
  "url": {
    "scheme": "",
    "path": "/Users/wiz/media/...",
    "isLocalFilePath": true
  },
  "headerKeys": []
}
```

如果看到 `path` 是 `/videos/{id}/master.m3u8` 或 `/Videos/{id}/stream`，说明 Moonfin 仍在走 Jellyfin HTTP/HLS，不是本地文件直连。

---

## 7. 2026-07-07 验证记录

本地验证环境：

```text
Jellyfin: /Applications/Jellyfin.app
DataDir:  ~/Library/Application Support/jellyfin
Moonfin:  /Applications/Moonfin.app
```

已验证的成功点播：

```text
时间：2026-07-07 17:32:41 +07
ItemId：f5f870d0-1e36-197e-b666-09ee19b21f9b
Path：/Users/wiz/media/tvshows/九号秘事之黑帷背后 (2026)/Season 1/九号秘事之黑帷背后.S01E02.第2集.2160p.WEB-DL.H265.AAC.mp4
Jellyfin：记录 Skipping media probe + Using local file path
Moonfin：playMethod = directPlay，mediaKitOpenStart.url.isLocalFilePath = true
FFmpeg：没有生成 17:32 对应的新 Transcode 日志
```

测试与构建记录：

```text
dotnet test tests/Jellyfin.Api.Tests/Jellyfin.Api.Tests.csproj
  -> 通过 93 个测试

dotnet build Jellyfin.Server/Jellyfin.Server.csproj
  -> 0 warnings / 0 errors

macOS arm64 app
  -> 已发布并安装到 /Applications/Jellyfin.app
  -> /web/index.html 返回 HTTP 200
  -> /System/Info/Public 报告版本 10.11.11-20260707165209
```

---

## 8. 不解决的问题

本修改只针对同机 Moonfin 的本地文件直连，不解决以下问题：

- Jellyfin 对所有符号链接文件的通用 ffprobe / `RunTimeTicks` 探测问题。
- 非本机客户端播放本机路径的问题。非本机客户端不能访问 `/Users/wiz/media/...`。
- 非 Moonfin 客户端的直连播放。
- CloudDrive 挂载失效、目标文件不存在、权限不足等底层文件系统问题。
- Moonfin 以外客户端对 `Protocol = File` 的兼容性。

如果未来要做通用符号链接时长修复，应继续在 Jellyfin 的 media probe / metadata refresh 路径排查，而不是扩展本 Moonfin 特例。

---

## 9. 维护注意

- 修改 `MediaInfoController` 或 `MediaInfoHelper.GetPlaybackInfo` 时，保留 `allowMediaProbe` 语义，避免本地直连路径重新触发 ffprobe。
- 修改 client 识别逻辑时，注意 `MoonfinLocalPlaybackHelper.IsMoonfinClient` 当前是 contains 匹配。
- 修改 `MediaSourceInfo.Path` 相关逻辑时，确认不会把本地路径下发给远程请求。
- 如果要支持更多客户端，建议先新增显式白名单 helper / 测试，不要放宽为所有 `File` protocol。
- 每次改动后至少跑：

```bash
dotnet test tests/Jellyfin.Api.Tests/Jellyfin.Api.Tests.csproj
```

并用 Moonfin 做一次实际点播，确认 Jellyfin 日志有 `Using local file path for Moonfin playback`，Moonfin 诊断日志有 `isLocalFilePath = true`。

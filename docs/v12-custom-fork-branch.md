# Jellyfin V12 定制 fork 分支说明

## 分支定位

本仓库的长期使用分支为 `codex/v12-custom-migration`，推送到 `orut34iop/jellyfin` fork。该分支以 Jellyfin 12.0 正式版为基础，持续吸收官方 `release-12.z` 的维护修复，并承载本机 V12 服务端、Moonfin 播放、媒体库扫描和 macOS 应用所需的定制能力。

远端约定：

- `origin`：官方仓库 `jellyfin/jellyfin`。
- `origin/release-12.z`：Jellyfin 12 系列正式维护分支，是本分支的主要上游来源。
- `fork`：个人仓库 `orut34iop/jellyfin`。
- `fork/codex/v12-custom-migration`：当前定制分支的远端副本。

本分支不是下一代功能开发分支。官方 `master` 进入后续大版本开发后，不应整体合并到这里；确实适用于 V12 的修改应优先等待进入 `release-12.z`，或在完成兼容性审查后单独移植。

## 当前上游基线

2026-09-13 已将官方 `release-12.z` 合并至 `63def185b199c5afded850b8eb5667276bc864f9`。本轮合并前的定制状态保存在本地备份分支 `backup/v12-custom-before-release-sync-20260913`；上一轮基线仍保留在 `backup/v12-custom-before-release-sync-20260912`。

这次同步继续包含官方 V12 维护修复，包括目录枚举异常保护、无效条目数据清理、播放列表编码识别、过滤器标签补全、图片无效缩放跳过、TMDb 请求精简及关联条目数据迁移。详细结果见 [V12 定制迁移记录](v12-customization-migration.md)。

## 本分支保留的定制

### 媒体库和元数据

- 本地元数据导入模式，可限制远程图片、远程元数据和不必要的媒体探测。
- 本地人物条目创建及人物角色完整保留。
- 人物查询按类型分组，人物映射无变化时跳过写事务。
- 人物原始名称查询继续使用 `Type + lower(Name)` 定制索引；MusicArtist 查询使用官方现有的 `Type + CleanName` 索引。两者用途不同，后续同步时不能把人物索引随艺术家查询调整一起删除。
- 目录枚举失败时保留已有媒体记录，避免把读取异常误判为文件删除。
- 媒体探测和图片刷新失败不会写入成功时间戳，以便后续重试。

### 扫描与数据库安全

- 后台扫描入口在已有扫描运行时不再取消并替换当前任务。
- 大型媒体库扫描包含进度、心跳和查询拥塞诊断。
- 数据库损坏或恢复失败后停止后续数据库操作，并保留可恢复备份。
- 扫描监控脚本使用 V12 认证方式读取任务状态。

### 播放和媒体处理

- 同机 Moonfin 客户端可在满足本地请求、Moonfin 客户端、File 协议和文件存在等条件时取得本地媒体路径。
- 缺失媒体流信息时按定制规则补充探测。
- 多候选容器优先匹配源文件扩展名，并保留请求扩展名和编码优先级。
- 保留官方 V12 的 Trickplay、流索引检查及媒体编码修复。

### macOS V12 应用

- 应用安装位置为 `/Applications/Jellyfin V12.app`。
- 数据目录为 `~/Library/Application Support/jellyfin-v12`，默认端口为 `8096`。
- 原生菜单栏启动器负责服务启停、Web 和日志入口、偏好设置及登录时启动。
- 每次构建使用统一时间戳生成 `12.0.0-YYYYMMDDHHMMSS` 版本，便于确认实际运行代码。

## 与上游同步规则

同步官方维护分支时执行以下流程：

1. 获取最新 `origin/release-12.z`，阅读从上次基线以来的提交和变更文件。
2. 在合并前建立明确命名的备份分支或引用。
3. 整体合并正式维护历史；只在修改与本地定制重叠时手工组合两边语义。
4. 特别检查人物查询、元数据提供者过滤、扫描入口、目录枚举、数据库迁移、Moonfin 播放和 macOS 打包代码。
5. 运行受影响测试、Release 构建和 macOS 应用验收，通过后提交并推送 `fork/codex/v12-custom-migration`。

向官方贡献时使用独立 PR 分支，不直接从本定制分支提交。维护者要求的实现调整应在 PR 分支完成验证，再把适用于本分支的最终方案移植回来。变基、PR 目标分支和上游专用说明不需要归并到本分支。

## 构建与验收

服务端构建必须使用同一个时间戳：

```bash
BUILD_STAMP=$(date '+%Y%m%d%H%M%S')
dotnet build Jellyfin.sln -c Release /p:JellyfinBuildDateTime="$BUILD_STAMP"
```

macOS arm64 应用构建和安装：

```bash
JELLYFIN_BUILD_STAMP="$BUILD_STAMP" scripts/build-macos-v12.sh --install
```

完成后至少确认：

- `packaging/macos/LauncherTests.swift` 与 `LauncherCore.swift` 测试通过。
- `scripts/test-migrate-v12-network.py` 测试通过。
- `/Applications/Jellyfin V12.app` 签名校验通过。
- `http://127.0.0.1:8096/web/index.html` 和 `/health` 返回 HTTP 200。
- `/System/Info/Public` 返回本次时间戳版本且初始化向导状态为完成。
- 启动器诊断显示状态栏可见、服务运行、菜单完整，启动器是服务端父进程。
- 启动器和服务端可以优雅退出并重新启动。

## 2026-09-13 验证快照

本次 `release-12.z` 同步及 PR 修订归并后，49 项针对性服务端测试通过；`Jellyfin.sln` Release 构建为 0 警告、0 错误。13 项启动器检查和 9 项网络迁移测试通过。安装版本为 `12.0.0-20260913010720`，Web 返回 HTTP 200，健康状态为 Healthy，初始化向导状态保持完成。桌面诊断确认状态栏、菜单入口、版本及父子进程关系正常；启动器和服务端在 40 秒内优雅退出，并于重新打开后恢复运行。

仓库中的 `.reports/` 是本机运行和复核数据，不属于分支交付内容，不应随代码提交。

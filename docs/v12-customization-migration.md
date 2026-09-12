# V10.11.11 定制迁移到 V12

## 基线与来源

- 服务端：官方 `v12.0`，`6c073e19ddf604b2369c638716164fdab4c952dc`。
- Web：`v12.0`，`0e83c6a724b31f3e9b5a499244331a288c060a4a`。
- 旧版服务端定制：`v10.11.11..8979100ec8` 的最终差异。
- 旧版 Web 定制：`v10.11.11..7e2ebe27d8` 的最终差异。
- 远端 origin 保留用户的 orut34iop fork。该服务端 fork 没有 release tags，且 master 早于正式版，因此从官方 upstream 补取正式标签。
- 原指定服务端目录实际为 Web 仓库；用户明确授权清空后重新准备服务端源码。

## release-12.z 维护分支同步（2026-09-12）

当前定制分支已整体合并官方 `release-12.z` 至提交 `3c698bab7fb7c607e726a0bf79f0bb0f7616e962`，保留官方合并历史。同步内容包括：剧集名称与多版本识别、Trickplay 视频流选择、负数媒体流索引拦截、TMDb 图片尺寸、家长分级大小写与意大利分级数据、路径包含判断、版本号小数点处理、FFmpeg 运行时能力探测、元数据提供者顺序即时生效、媒体探测失败重试、图片刷新失败计数，以及新增合集库后的文件夹识别。

元数据提供者代码的合并冲突按两边语义组合解决：保留本地元数据模式显式允许远程内容探测的刷新选项，同时采用官方按实际 CollectionFolder 查找媒体库路径的逻辑。官方新增测试已改为调用定制分支实际使用的重载。

同时归并本轮上游 PR 审阅后的改进：MusicArtist 单条查询改用 `CleanName` 并复用现有索引；人物原始名称查询及 `Type + lower(Name)` 定制索引继续保留；活动扫描保护移至 `StartScanInBackground` 公共入口；目录枚举移除无效初始化；媒体输出扩展名的编码映射拆分并扩大回归覆盖。解决方案中两个媒体编码项目的 Release 配置也从误配的 Debug 修正为 Release，避免正式构建加载不兼容的调试分析器。

最终验证：相关测试 619 项通过，Windows 专属文件系统用例 13 项在 macOS 跳过；`Jellyfin.sln` Release 构建 0 警告、0 错误；13 项启动器测试及 9 项网络迁移测试通过。已安装并完成一次优雅退出与重新启动，`/web/index.html` 返回 HTTP 200，`/System/Info/Public` 报告 `12.0.0-20260912215910` 且初始化向导状态保持完成。启动器诊断确认状态栏可见、服务运行、菜单与父子进程关系正常。

## 基线验证（归并前已完成）

服务端 .NET 10/macOS arm64 self-contained publish 成功。Web npm ci、生产构建、TypeScript 检查成功。应用安装至 `/Applications/Jellyfin V12.app`，签名校验通过。启动后 `/health` 返回 Healthy，`/System/Info/Public` 返回 12.0.0，`/web/index.html` 返回 200，浏览器显示初始化向导。

## 定制能力与适配

| 能力 | V12 处理 |
| --- | --- |
| 本地元数据导入模式、禁止远程图片与不必要探测 | 迁移策略、媒体库选项、文件枚举、元数据刷新及后台任务 |
| 本地人物条目与收藏（含所有角色） | 迁移配置与实体创建；保留 V12 人物去重、访问过滤和孤立角色清理 |
| Moonfin 同机本地路径播放 | 保留本地请求、客户端名、文件协议与路径存在限制；适配 V12 HttpRequest 参数和媒体源解析 |
| 播放缺失流信息的补探测与输出扩展名 | 迁移媒体源补探测和输出处理 |
| 批量持久化与查询性能 | V12 ItemPersistenceService 已内置集合/字典及祖先批查优化；将写入信号量移至新服务 |
| 符号链接与视频占位信息 | 保留 V12 媒体源解析，将定制轻量文件信息和目录服务与其缓存失效机制结合 |
| 元数据提供器缓存 | 保留 V12 缓存；在缓存结果后施加本地模式/显式探测过滤，避免不同刷新选项污染缓存 |
| API 阻塞诊断、扫描并发、退出时限 | 迁移中间件和调度修改；退出优化限制为 60 秒 |
| 构建时间版本、备份恢复与扫描监控 | 迁移版本生成、macOS/Linux 脚本和历史文档 |
| Web 媒体库设置 | 迁移本地导入/创建人物条目开关与中英文说明 |

已撤销的字幕取消修改按最终差异处理，没有重新引入。历史文档保留原版事实；当前构建与运行说明以本文件为准。

最终浏览器验收发现 V12 SDK 仅接受纯数字版本比较，已在 Web 连接检查中识别本 fork 的 14 位构建时间后缀，使用正式版本号进行兼容比较，仍保留完整版本用于显示。新增 9 项兼容检查回归测试，覆盖正式版本、构建时间、过旧版本及异常输入。

## V12 安装隔离

V12 应用使用 `~/Library/Application Support/jellyfin-v12`，默认本机端口为 `8096`。初始隔离部署使用的端口为 `18096`，现按用户要求迁移为 `8096`；初始部署时尚未导入生产媒体库，下文早期验收记录保留当时事实。安装采用自包含服务端、构建后的 Web 与已有 Jellyfin FFmpeg 7.1.4，启动脚本通过明确参数使用独立数据目录。

构建时可设置 `JELLYFIN_HTTP_PORT`（整数 `1–65535`），该默认端口会写入应用包，Finder 启动时无需再传环境变量。已有配置中的自定义端口优先，构建输出的网址仅表示打包默认值。启动器依赖 PATH 中的 Python 3.8+，用于安全处理 XML；处理失败时停止启动并将错误写入 `log/launcher.log`。

首次启动时，新配置使用打包默认端口；已有配置仅将值为 `18096` 的 `InternalHttpPort`、`PublicHttpPort` 字段迁移至该端口，保留其他网络设置及自定义端口。修改前在配置目录生成唯一的 `network.xml.pre-port-migration-*.bak` 原始备份，再原子替换配置。`.v12-http-port-migrated` 标记完成一次性迁移，后续启动不再改写已有配置，包括用户之后主动设置的 `18096`。全新配置仍默认仅监听本机、关闭远程访问。

## 验证记录

详细构建与测试日志位于项目旁的 `../migration-audit`。

- Web：174 项测试通过，生产构建、TypeScript、变更文件 ESLint 均通过；仅有 webpack 资源体积提示。
- Server Implementations：850 项通过，5 项因平台/测试挂载条件跳过。
- API：155 项通过。
- Controller：205 项通过。
- Providers：457 项通过（包括新增缓存策略回归）。
- 合计服务端 1667 项通过，5 项跳过；无失败。测试通过 `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8` 固定预期语言环境。
- 备份恢复：独立 SQLite 与配置文件完整往返成功，quick_check=ok；脚本语法检查通过。Linux 服务控制仅迁移，未在 Linux 主机运行验证。

重新构建与安装：

```bash
cd "/Users/wiz/dev/Jellyfin12/Jellyfin Web Client"
npm ci
npm run build:production
cd /Users/wiz/dev/Jellyfin12/Jellyfin
./scripts/build-macos-v12.sh --install
```

安装脚本生成共享构建时间版本、自包含 arm64 应用和 ad-hoc 签名，保留被替换的 V12 应用副本后启动新版。可通过 JELLYFIN_FFMPEG_DIR 指定已有 Jellyfin FFmpeg 工具目录，默认复用已安装 V12 应用内的工具。首次安装需显式指定工具目录。

## macOS 桌面启动器迁移补全

早期 V12 包直接用 Shell 启动后台服务，遗漏了 V10 官方包的原生菜单栏启动器；此前的构建/API 验收未覆盖桌面菜单，未能发现这一缺口。现由 `packaging/macos` 的原生 AppKit 启动器负责菜单与服务生命周期，Shell 仅负责配置迁移及启动参数。默认端口仍为 `8096`，保留 V12 独立数据目录。

逐项对照官方启动器 `jellyfin/jellyfin-server-macos` 的 `7f9809e5ee244f049798f98786cf155fcbb752de` 以及本仓库 V10 打包指南：

| V10 桌面能力/资源 | V12 处理 |
| --- | --- |
| 菜单栏图标、无 Dock 图标 | 原生 NSStatusItem + LSUIElement；官方模板图标随仓库保存 |
| 打开 Web 页面 | 读取现有端口、HTTPS 要求及 BaseUrl |
| 打开本机 Jellyfin Media Player | 检测 `tv.jellyfin.player` 后显示菜单 |
| 查看日志 | 指向 V12 的 log 目录；另提供数据目录入口 |
| 重启、退出时停止服务 | 等待所管理的子进程退出；另提供单独启动/停止操作 |
| 偏好设置、登录时启动 | 原生偏好窗口及 macOS 13+ SMAppService；默认不擅自开启 |
| 关于/版本显示 | 显示同一份构建时间戳版本 |
| AppIcon、菜单图标 | 固定到仓库，不再依赖已删除的 V10 应用目录 |
| storyboard、LaunchAtLogin helper | 由代码构建窗口及系统 SMAppService 替代，无外部 Swift 包依赖 |
| FFmpeg、FFprobe、.NET runtime、Web | 继续随应用打包；FFmpeg 默认来源改为现有 V12 |
| 重复启动、升级时停止 | 文件锁限制单个启动器；安装器先退出启动器及其服务 |

构建需要 Xcode Command Line Tools（swiftc），启动器最低 macOS 13。登录时启动使用 V12 独立 bundle ID，不改变旧 V10 的登录项。官方发行包的公证签名不属于本地定制构建；本地继续使用 ad-hoc 签名。此清单覆盖 macOS 打包与桌面功能，不代表已重新验收所有媒体播放与后端定制功能。

回归验证：编译运行 `packaging/macos/LauncherTests.swift` 与 `LauncherCore.swift`，检查地址解析、单实例和服务生命周期；运行端口迁移 Python 测试；构建安装后检查菜单栏、菜单操作、进程父子关系、HTTP 200、健康状态及时间戳版本。桌面诊断写入 `log/desktop-launcher-state.json`，不包含认证令牌。

2026-09-12 本次补全验收：13 项 Swift 检查、9 项端口迁移测试通过；已安装版本 `12.0.0-20260912171020`。实际验证启动器拥有服务子进程、SIGTERM 后二者正常退出、重新打开应用恢复服务、偏好窗口显示完整版本、Show Logs 打开 V12 日志目录、Open Jellyfin 在浏览器进入 `http://127.0.0.1:8096/web/index.html#/home`。原生诊断报告菜单栏状态项可见、服务正常运行。退出验收发现并修复了 AppKit 嵌套事件循环中的主队列等待问题，已增加专项回归用例。

验收边界：桌面自动化只能抓取普通窗口，未对顶部菜单栏进行截图或逐项点击；下拉菜单控制逻辑由原生状态诊断及生命周期测试覆盖。登录时启动开关保持关闭，未进行注销重登测试；本机未安装官方 Jellyfin Media Player，其条件菜单仅完成代码路径检查。未重新执行所有媒体库、转码和 Moonfin 播放验收。


## V12 官方符号链接改进

官方 PR [#16965](https://github.com/jellyfin/jellyfin/pull/16965)，提交 `c7111b7570`，2026-06-01 合入并包含于 v12.0，将符号链接目标解析从 BaseItem.GetVersionInfo 移至播放/下载路径，避免浏览媒体库时不必要地唤醒目标磁盘。这是通用逻辑，也适用于 macOS。它不等同于定制的扫描占位、禁止媒体探测和同机 Moonfin 返回原始链接路径，因此两者都保留。

## 最终安装验收

2026-09-12 安装并启动 `/Applications/Jellyfin V12.app`，`/System/Info/Public` 返回 `12.0.0-20260912000255`，`/health` 返回 Healthy，Web 返回 HTTP 200，浏览器初始化向导正常显示。OpenAPI 包含 LocalMetadataOnlyImport、CreateLocalPersonItems、CreateLocalActorItems 三个定制配置字段。Web 版本时间戳兼容修复已包含在安装产物中。

当前验证包含源码构建、回归测试、应用安装、启动和初始页面；尚未把 V10 的真实媒体库数据升级到 V12，也未进行真实 Moonfin 客户端播放验收。

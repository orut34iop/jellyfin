# 播放触发描述性元数据覆盖：优先上游提交方案

核对日期：2026-10-19。官方 `master`：`08c2ba6e0b6499122822f1c5b18bc426104538b4`（本次再次通过远端查询核实）。本地实现和验证基线：`4416c74b72`。

状态：核心修复已在独立分支 `codex/upstream-playback-metadata` 完成并推送，提交 `3535edb442`；已取得官方基线与核心补丁的实际失败／通过对照。等待用户审核，尚未创建上游 PR。参见[独立补丁与验证说明](https://github.com/orut34iop/jellyfin/blob/3535edb442a38a9b1dfe5bfdfd5a6a14aa9b3b70/docs/playback-metadata-refresh.zh-CN.md)。[English](playback-metadata-refresh-upstream-plan.md) · [完整设计](metadata-refresh-consistency-design.zh-CN.md)

## 1. 实际背景和现象

我们的日常流程是先从 NFO 导入媒体库，再用自己开发的外部工具通过 Jellyfin API 修改影片的流派和地区，将英文值翻译为中文并整理重复项。源 NFO 仍可能保留导入时的旧值。

在我们的符号链接媒体库中，导入采用定制的极速模式：读取 NFO，但暂不访问目标视频来提取音视频流。API 编辑后，媒体库显示修改后的信息；之后使用播放器播放影片，却发现整理后的信息回退到旧来源值。这不是用户主动要求重新刮削或替换描述字段。

极速导入是这个问题的实际使用背景，属于本地定制。上游修复不能要求官方项目也引入这一模式。应使用官方已有的 STRM 路径，以及可控的缺少主流信息的测试条目，验证官方代码中的同一触发路径。

## 2. 触发流程和原因

```text
NFO 导入：流派／地区 = 原始值
    ↓
外部工具调用编辑 API：数据库 = 整理后的值，NFO 仍为旧值
    ↓
播放器请求 PlaybackInfo
    ↓
MediaInfoHelper → MediaSourceManager.GetPlaybackMediaSources
    ↓
允许探测，且为非占位媒体源；条目是 STRM，或缺少主视频／音频流
    ↓
RefreshMetadata(EnableRemoteContentProbe = true, FullRefresh)
    ↓
普通元数据提供者流程读取 NFO，并可能调用其他描述性来源
    ↓
按来源状态和字段锁合并，再保存条目
```

根本问题是：**为播放补齐技术信息的请求，被交给了也可以更新影片描述的完整刷新流程。** 获得时长、编码或分辨率，不应同时意味着重新导入流派、地区、标题和简介。

代码依据：

- [上游播放入口，第 182–194 行](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/Emby.Server.Implementations/Library/MediaSourceManager.cs#L182-L194)：STRM 或缺少主流会触发 `FullRefresh`；STRM 分支不以已保存主流为跳过条件。
- [上游来源合并，第 908–917 行](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/MediaBrowser.Providers/Manager/MetadataService.cs#L908-L917)：本地来源的 `IsLocked` 会进入替换合并，但仍传入数据库目标的 `LockedFields`；不是无视所有字段锁。
- [地区集合合并，第 1295–1304 行](https://github.com/jellyfin/jellyfin/blob/08c2ba6e0b6499122822f1c5b18bc426104538b4/MediaBrowser.Providers/Manager/MetadataService.cs#L1295-L1304)：非替换合并也可能追加旧来源地区。对应的集合修复只是后续独立改动。

因此不能声称“每播放一次都会覆盖所有字段”。是否进入刷新取决于媒体源和探测条件；字段是被替换、被追加还是保留，还取决于 NFO `lockdata`、数据库 `LockedFields`、合并模式及提供者。现有代码可以证明这条风险路径；原始真实条目的具体 NFO／锁组合仍需作为复现材料记录，不能用推测代替。

`ReplaceAllMetadata = false` 不足以隔离描述性来源。强制给所有 API 编辑加锁也不能替代播放入口修复：即使未加字段锁的显式编辑，也不应仅因播放而进入描述性重新导入。

## 3. 修改方案与首个 PR 的边界

首个 PR 的单一目标：**播放所需的媒体探测可以补齐技术数据，但不能读取来源来重新导入影片描述，或通过保存旧条目覆盖数据库中已提交的描述。**

本地已经实现的必要边界，应按官方代码适配后一起放入核心修复：

1. 播放入口传递明确的仅技术信息刷新范围；普通刷新默认行为不变。
2. 范围贯穿 `BaseItem`、元数据服务和探测提供者，跳过 NFO／远程描述提供者、图片及附属条目的描述维护。
3. FFmpeg 可以读取媒体文件，但其内嵌标题、流派等标签不能在此范围内写回描述字段，包括当前为空的字段。
4. 保存只写技术字段和对应媒体数据，保留数据库中的描述、字段锁及版本关系，避免对旧完整条目的保存再次带回旧值。播放探测不推进“描述性元数据已刷新”的时间。
5. 探测失败或取消不先清除已提交的有效技术数据；完成后正确更新或失效缓存。

必要的技术保存边界不能从核心 PR 中拿掉，否则只过滤提供者仍可能被完整条目保存破坏。跨所有技术表的崩溃事务是后续增强，可以独立评审；核心修复不依赖新增原子重置 API。

为了减少行为变更，首个上游 PR 可保留官方当前“何时需要探测”的判定，首先修复探测范围。取消 STRM 每次探测、按媒体源复用已存技术信息及重新验证策略，作为第二项独立改动。当前本地最终实现已包括这些优化，但不能因此把它们自动视为首个 PR 的必要组成。

保持旧客户端及现有编辑 API 兼容，不添加版本号、条件请求或旧 DTO 冲突检测。不改变用户主动发起普通刷新、替换全部元数据的语义。不把 Moonfin 专用逻辑、极速导入配置或翻译工具带入官方补丁。

## 4. 已完成的独立验证

官方基线 `08c2ba6e0b` 和核心补丁使用同一隔离夹具，生成小视频，通过回环 HTTP 提供给 STRM；不依赖极速导入。关闭 NFO 保存和自动扫描，在 API 编辑后保持磁盘 NFO 为旧值，确认数据库回读成功后请求两次 PlaybackInfo。

- `lockdata=true`：官方版本把流派、标题和简介恢复为 NFO 值，地区混入旧值；补丁保持 API 修改值和锁状态。
- `lockdata=false`：官方版本向地区追加旧来源值；补丁保持 API 修改值。
- API 主动清空的字段：官方版本重新填充，补丁保留空值。
- 两个版本均返回可用的 160×90 主视频流。普通保护断言在基线上以退出码 1 失败，在补丁上以退出码 0 通过。
- 补丁还通过源 HTTP 404 后保留已有技术数据／描述，以及主动替换全部元数据仍读取 NFO 的验证。

本次独立分支共 **2,031 项通过、13 项既有平台测试跳过**，其中新增 24 个回归用例；Release 构建通过，Debug 分析检查使用与官方分析器匹配的临时 Roslyn 5.9 编译器通过。普通文件缺少主流、音频和副版本描述边界由组件测试覆盖；实际 HTTP 用例覆盖电影，不声称是 GUI 播放器端到端验收。

[英文实现说明](https://github.com/orut34iop/jellyfin/blob/3535edb442a38a9b1dfe5bfdfd5a6a14aa9b3b70/docs/playback-metadata-refresh.md)、[中文实现说明](https://github.com/orut34iop/jellyfin/blob/3535edb442a38a9b1dfe5bfdfd5a6a14aa9b3b70/docs/playback-metadata-refresh.zh-CN.md)、[修改前记录](https://github.com/orut34iop/jellyfin/blob/3535edb442a38a9b1dfe5bfdfd5a6a14aa9b3b70/docs/evidence/playback-before.json)、[修改后记录](https://github.com/orut34iop/jellyfin/blob/3535edb442a38a9b1dfe5bfdfd5a6a14aa9b3b70/docs/evidence/playback-after.json)。本次只运行私有测试服务器，没有安装替换已运行服务或操作真实媒体库。跨技术表崩溃事务、刷新并发协调和旧 DTO 冲突不属于该独立补丁。

## 5. 上游拆分及提交顺序

| 顺序 | 独立范围 | 当前状态／依赖 |
| --- | --- | --- |
| **PR 1：核心问题** | 播放技术探测与描述刷新分离；包括范围传递、内嵌标签隔离、必要技术保存边界及对应回归测试 | 已从官方 `master` 独立实现，`3535edb442`；前后对照和测试通过，已推送 fork，等待用户审核后创建 PR。未带入整套本地定制。 |
| PR 2：按需探测 | 复用已有主流；调整 STRM 重复探测；按具体媒体源处理，并验证源变化和明确重新验证 | 本地已实现，尚未独立移植验证。应在 PR 1 后评审，避免改变缓存新鲜度规则掩盖核心问题。 |
| PR 3：补缺集合 | 补缺时不向已有地区／工作室／标签追加来源值 | `codex/upstream-preserve-metadata-collections`，`7efec6e9d2`，516 项 Providers 测试通过；暂缓，不能作为 PR 1 的替代。 |
| PR 4：NFO 文件保存 | 临时文件完成后替换原 NFO，保留失败前的内容 | `codex/upstream-nfo-atomic-save`，`d65043f4b0`，43 项 XbmcMetadata 测试通过；暂缓，与播放入口修复独立。 |
| 后续：刷新协调与继承 | 普通刷新、附属条目、季／集及合并／拆分协调 | 按官方实际存在的问题继续细分；本地定制独有的行为不移植。 |
| 另行设计：高级一致性 | 跨技术表崩溃事务；显式受保护字段重置 API | 两个不同评审范围；先作设计讨论，不要求它们先合并才能修复播放问题。 |

PR 1 实际以一条完整提交交付核心实现、回归用例和双语证据，便于整体测试与审核；包含技术范围、内嵌标签、保存路径和副版本描述复制保护。未只提交入口开关。

所有正式 PR 的目标都是官方 `jellyfin/jellyfin:master`。优先级调整不删除已准备的两个分支，也不向上游先发布它们。按官方贡献政策，发布前仍需提交者审核和自己的说明；本文是工程参考资料，不应直接作为 AI 生成的 PR 正文发布。

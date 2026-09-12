# 首次创建合集库导致扫描自我取消

## 现场

2026-09-12 的扫描于 12:19:13 开始，12:45:27 完成媒体验证并进入收尾任务。
12:46:13 达到 97.5%，随后 TaskManager 请求取消；12:46:23 记录 Cancelled，
并立即启动下一轮。该轮未完成，不能把媒体验证完成或 97.5% 当作导入完成。
进程未退出，没有新的数据库损坏错误或故障标记。

合集库的 boxsets.collection 创建时间为 12:46:03，与此流程一致。
调用路径为 CollectionPostScanTask → CreateCollectionAsync → EnsureLibraryFolder
→ AddVirtualFolder(refreshLibrary: true) → StartScanInBackground
→ ValidateMediaLibrary → CancelIfRunningAndQueue。
这条路径会在首次自动创建合集库时取消正在执行的扫描。已有合集库时不会再走创建分支。

## 修改

- 创建合集库时检查 IsScanRunning：有扫描正在执行时不再安排替代扫描；
  空闲时仍保留原有的自动刷新行为。
- AddVirtualFolder 仍执行顶部媒体库验证，使新库可立即解析；在活动扫描中不提前
  恢复目录监听，由扫描原有的 finally 流程恢复，避免边扫描边监听本次写入。
- 回归测试覆盖活动扫描、空闲状态，以及已有合集库不会重复创建或安排刷新。

本次修改不清空数据库，不删除媒体文件，不修改用户设置或密码。
当前扫描已经自动重新开始，优先等待其结束后部署，避免额外取消。

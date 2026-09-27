# 超级便签 · 路线图与设计决策

本文件记录 MVP 的实现取舍，以及通往 P1 / P2 的路径。

---

## 1. 版本规划

### P0（本仓库已实现）
普通便签、剪贴板随记、基础待办、文件/文件夹备注、File ID + 路径指纹、
文件树、Everything 联合搜索、桌面 Widget、统一对象模型基础、本地 SQLite + 分级加密。

### P1（本仓库已实现）
- ✅ USN Journal 实时订阅（`Services/UsnJournalService.cs`，按卷 FSCTL_READ_USN_JOURNAL，
  解析 USN_RECORD_V2，用 File ID 对齐主指纹；非 NTFS / 无权限自动降级到 FileSystemWatcher）
- ✅ 哈希兜底 + 跨卷迁移候选（`FingerprintService.Resolve` 三级回退：File ID → 卷序列号 → 哈希/名称相似度候选）
- ✅ 网络盘 / NAS 支持（卷序列号指纹 + `is_network` 标记 + 内置索引 `FindByVolumeSerial`）
- ✅ sidecar 文件可配置（`Services/SidecarService.cs`，JSON/YAML、隐藏/可见、同目录/集中目录、命名后缀）
- ✅ 图片 OCR 搜索（`Services/OcrService.cs`，Windows.Media.Ocr；剪贴板图片与备注插图写入 `ocr_text` 参与搜索）
- ✅ 云同步预留（`Services/CloudSyncService.cs`，WebDAV PUT/GET + DPAPI 传输加密 + `.conflict` 冲突备份）
- ✅ 版本关系链（`Services/VersionService.cs` + `version_link` 表 + 自动推荐候选）
- ✅ 副本继承策略（`Services/CopyInheritService.cs`，Ask/Always/Never/SameDirOnly/SidecarOnly）
- ✅ 双向链接 / 反向链接（`Services/LinkService.cs`，解析 `[[标题]]` 并维护反向链接）
- ✅ 时间线完整事件流与可配置（`Settings.TimelineEnabledCategories` + `TimelineCategories`）
- ⬜ 资源管理器**内嵌**悬浮面板（IExplorerCommand / IContextMenu Shell Extension）——仍需单独适配 Win10/11

### P2（本仓库已实现）
- ✅ 关系图谱 / 知识网络视图（`Services/GraphService.cs` + `Views/GraphWindow.xaml(.cs)`，
  便签 / 文件备注 / 待办及其双向链接 / 版本关系 / 待办挂靠 / 普通引用构建为节点-边网络；
  轻量力导向布局：斥力 + 弹簧 + 向心 + 阻尼，迭代收敛；支持拖动微调、右键聚焦邻域、双击查看详情）
- ✅ 图片向量搜索（`Services/ImageVectorService.cs`，**纯本地** 148 维特征向量
  —— 8×8 灰度(64) + 4×4×4 颜色直方图(64) + 4×4 边缘密度(16) + 全局统计(4)，
  余弦相似度检索；**不引入任何 AI 模型或云服务**；`image_vector` 表持久化）
- ✅ 团队协作预留（`Services/CollaborationService.cs`，`ICollaborationProvider` 抽象 +
  `LocalOnly` 本地占位 + `collab_session` 工作区/成员元数据模型，默认本地不连远端）
- ✅ 插件系统（`Services/PluginService.cs`，`IPlugin` / `PluginContext` / `PluginHost`，
  `AssemblyLoadContext`(可卸载) 隔离加载 + `AssemblyDependencyResolver` 解析插件依赖；
  单插件失败不影响主程序；`plugin` 表记录加载状态；插件目录可配置）
- ✅ 后台服务（`Services/BackgroundServiceHost.cs` + `scripts/install-service.ps1`，
  `SuperNote.App.exe --service` 无界面模式：USN Journal 追踪 + 网络盘/NAS 监控 + 开机索引；
  脚本以管理员注册 Windows 服务，优先 NSSM、回退 sc.exe）

---

## 2. 关键设计决策与理由

| 决策 | 选择 | 理由 |
| --- | --- | --- |
| UI 框架 | WPF + Fluent 自绘主题 | 相比 WinUI 3 更易在 Win10/Win11 一致降级；无需 Windows App SDK 运行时依赖 |
| 数据库 | SQLite(WAL) + FTS5 | 单文件、离线、全文检索；FTS5 不可用时自动降级 LIKE |
| 指纹主键 | 卷 GUID + NTFS File ID | 同卷重命名/移动时 File ID 不变，命中率最高 |
| 跨卷识别 | 完整哈希 + 大小 + 时间 + 名称相似度 | 无法用 File ID 时提供候选，交给用户确认 |
| 加密 | DPAPI(CurrentUser) | 密钥绑定当前用户，不出机器，零配置 |
| 文件监听 | USN Journal 增量 + FileSystemWatcher 兜底 | USN 带 File ID 与原因位，精确捕获同卷重命名/移动；网络盘回退 FSW |
| 桌面选中项读取 | FindWindowSW 正确封送 + Win32 SysListView32 回退 | Shell 集合不含桌面；Win32 直读需跨进程内存读写 |
| 云同步范围 | 只同步 SQLite 主库 | 移动盘/NAS 以 sidecar 为权威，避免多副本冲突 |
| OCR | Windows.Media.Ocr | 系统内置、离线、无需额外模型；旧系统静默降级 |

---

## 3. 关于 WPF vs WinUI 3

需求文档第 5 章给出"WinUI 3 + Windows App SDK，或 WPF + Fluent 主题"两种选择。
MVP 选择 **WPF** 的原因：

1. WPF 可在 Windows 10 与 11 上给出一致的外观，便于按需降级材质/圆角；
2. 不需要 Windows App SDK 运行时，部署更轻；
3. Mica/Acrylic 可通过 P/Invoke `DwmSetWindowAttribute` 在 Win11 上单独开启，
   失败时回落纯色，完全符合"允许材质和圆角降级"的要求。

若后续转向 WinUI 3，`Models` / `Data` / `Services` / `Interop` 四层可**原样复用**，
仅需重写 `Views` 与 `Themes`。

---

## 4. Shell 扩展说明

需求 3.4.4 的"最终形态"是资源管理器选中文件后叠加浮动面板。该能力依赖
`IExplorerCommand`（Win11）或 `IContextMenu`（Win10）Shell 扩展，且 MSIX 对
Shell 扩展有限制（需稀疏包）。MVP 采用两条轻量路径先满足核心体验：

1. **右键菜单**：注册表 `HKCU\Software\Classes\*\shell\SuperNote`（脚本已提供）；
2. **独立悬浮卡**：`FloatingNoteWindow`，由全局快捷键 / 菜单唤起，支持快速编辑。

真正的内嵌面板列入 P1，单独适配 Win10/Win11 并单独安装。

---

## 5. 数据模型（SQLite 表）

对应需求文档第 6 章，见 `src/SuperNote.App/Data/Database.cs`：

`note_record` · `clip` · `task` · `file_ref` · `annotation` · `fingerprint`
· `timeline` · `record_link` · `record_fts`

---

## 6. 验收标准自检（需求第 8 章）

| 验收项 | MVP | 备注 |
| --- | --- | --- |
| 文件重命名/同卷移动后备注仍关联 | ✅ | File ID 命中 |
| 跨卷移动后给出候选并恢复 | ✅ | 哈希/名称相似度候选（UI 确认流程 P1 完善） |
| 右键添加/查看备注 | ✅ | 脚本注册 |
| 悬浮面板快速编辑，复杂编辑跳主窗口 | ✅ | 独立悬浮卡 |
| 文件树悬停显示备注，点击跳转/打开 | ✅ | 基础版 |
| Everything 搜索联合备注关键词 | ✅ | 含降级 |
| 剪贴板可搜索、固定、转便签 | ✅ | |
| 待办提醒准时 | ⚠️ | 数据模型就绪，系统通知 P1 |
| 离线可用 | ✅ | 本地优先 |
| 数据库可导出/备份 | ⚠️ | SQLite 文件可直接复制，导出 UI P1 |
| sidecar 生成与识别 | ✅ | `SidecarService`（JSON/YAML，可配置） |
| USN Journal 实时追踪 | ✅ | `UsnJournalService`（失败降级 FSW） |
| 哈希兜底 + 跨卷候选 | ✅ | `FingerprintService.Resolve` |
| 网络盘 / NAS 恢复 | ✅ | 卷序列号 + 内置索引 |
| 图片 OCR 搜索 | ✅ | `OcrService`（Windows.Media.Ocr） |
| 云同步（预留） | ⚠️ | WebDAV 已实现；OneDrive OAuth 待接入 |
| 版本关系 / 副本继承 | ✅ | `VersionService` / `CopyInheritService` |
| 双向链接 / 反向链接 | ✅ | `LinkService` |
| 时间线可配置 | ✅ | `Settings.TimelineEnabledCategories` |
| 分级加密生效，敏感数据不落盘 | ✅ | DPAPI |
| Win10/11 运行，材质圆角降级 | ✅ | 纯色回落 |

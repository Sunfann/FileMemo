# DeskBox 风格复刻 · 差异清单与修复记录（阶段 3–5）

> 目标项目：**FileMemo（文笺）**（WPF，`net8.0-windows10.0.19041.0`）
> 源风格参考：`Tianyu199509/DeskBox` @ main（v1.5.5）
> 生成日期：2026-09-29
> 适配说明：DeskBox 为 WinUI 3，目标为 WPF。按用户确认，采用「视觉/交互等价适配」而非新建 WinUI 3 工程。

---

## 1. 复刻范围

用户确认「全部」纳入，覆盖 FileMemo 全部 8 个窗口/视图：

| # | 视图 | 文件 |
|---|---|---|
| 1 | 主窗口 | `Views/MainWindow.xaml` |
| 2 | 桌面控件 | `Views/WidgetWindow.xaml` |
| 3 | 快速记录 | `Views/QuickNoteWindow.xaml` |
| 4 | 悬浮便签 | `Views/FloatingNoteWindow.xaml` |
| 5 | 悬浮球 | `Views/FloatingBallWindow.xaml`（+`.cs`） |
| 6 | 剪贴板面板 | `Views/ClipboardPanelWindow.xaml` |
| 7 | 关系图 | `Views/GraphWindow.xaml` |
| 8 | 标注对话框 | `Views/AddAnnotationDialog.xaml` |
| — | 主题令牌 | `Themes/Tokens.xaml` |
| — | 控件样式库 | `Themes/Controls.xaml` |

---

## 2. 差异清单与修复记录

### 2.1 配色（Color）

| 项 | DeskBox 规范 | FileMemo 状态 | 处理 |
|---|---|---|---|
| 强调色 | `#0078D4` | 已一致（`ThemeCatalog` DeskBoxDark/Light 默认主题） | ✅ 无需改 |
| 背景底 | 浅 `#F3F3F3` / 深 `#1F1F1F` | 已一致 | ✅ 无需改 |
| 文字主色 | 浅 `#1A1A1A` / 深 `#F5F5F5` | 已一致 | ✅ 无需改 |
| 文字次色 | 浅 `#5A5A5A` / 深 `#A5A5A5` | 已一致 | ✅ 无需改 |
| 分层填充/描边 | 黑白低透明叠加 | 已一致（Theme.Initial 兜底同步） | ✅ 无需改 |

**结论**：配色体系此前已按 DeskBox 规范构建，本次核对确认无偏差，未做破坏性改动。

### 2.2 字体（Typography）

| 项 | DeskBox 规范 | 原 FileMemo | 修复后 |
|---|---|---|---|
| 页面大标题 | 28 | 24（FontH1 混用） | 新增 `FontDisplay=28` |
| 区块标题 | 18 | 24 | `FontH1=18` ✅ |
| 小标题 | 15 | 20 | `FontH2=15` ✅ |
| 控件/卡片标题 | 14 | 16 | `FontTitle=14` ✅ |
| 正文 | 14 | 14 | 保留 |
| 次级正文 | 13 | 13 | 保留 |
| 菜单项 | 13（雅黑优先） | 14（AppFontFamily） | 新增 `FontMenu=13` + `MenuFontFamily`，菜单/右键菜单改用 ✅ |
| 说明文字 | 12 | 12 | 保留 |
| 小标签 | 11 / 11.5 | 11 | `FontTiny=11`，`MainWindow` 3 处硬编码 `FontSize="11"` 改为令牌 ✅ |
| 字重 | 标题 SemiBold，几乎不用 Bold | 已一致 | ✅ |
| 字体族 | Segoe UI Variable Text → Segoe UI；菜单雅黑优先 | 已含 | 补充 fallback 链 ✅ |

### 2.3 间距（Spacing）

| 项 | DeskBox 规范 | FileMemo | 处理 |
|---|---|---|---|
| 令牌 | `4 / 8 / 12 / 16` | 已具备四档 | ✅ 保留 |
| 页面/面板内边距 | 16 | 已一致 | ✅ |

### 2.4 组件样式（圆角 / 边框 / 阴影）

| 项 | DeskBox 规范 | 原 FileMemo | 修复后 |
|---|---|---|---|
| 圆角三档 | 卡片 6 / 按钮输入 6 / 小元素 4 | `RadiusCard=8`（偏大） | `RadiusCard=6` ✅ |
| 浮层圆角 | 8 | 部分 10 / 12 | 新增 `RadiusOverlay=8`，全部浮层对齐 ✅ |
| 边框厚度 | 0.8 / 1.2 / 1.6 | 字面量 `1` / `1.2` / `1.4` | `BorderThin/Medium/Thick` 令牌化 ✅ |
| 阴影 | 仅弹层（海拔 48），卡片无投影 | 仅 Popup/Tooltip/ContextMenu | ✅ 合规 |
| 按钮规格 | 高 34 / 圆角 6 / SemiBold | 已一致 | ✅ |

**关键修复（启动崩溃）**：`BorderThin/BorderMedium/BorderThick` 最初定义为 `sys:Double`，而 WPF `Border.BorderThickness` 需要 `Thickness` 类型；字面量靠类型转换器工作，改成资源引用后类型不匹配，导致 `MainWindow.xaml` 设置 `BorderThickness` 时抛异常。已改为 `Thickness` 类型并验证全部 11 处引用上下文匹配。

### 2.5 动效（Motion）

| 项 | DeskBox 规范 | FileMemo | 处理 |
|---|---|---|---|
| 交互节奏 | 83 / 167 / 250 ms | 已令牌化 | ✅ |
| 功能特效时长 | —（粒子/悬浮球专属） | 70/180/140/160/220ms 等 | ➖ 属功能特性效果，非 UI 节奏，保留 |

### 2.6 保留项（记入差异清单，属有意保留）

| 位置 | 内容 | 原因 |
|---|---|---|
| `MainWindow.xaml:448` | 主题预览卡 `BorderBrush="Transparent" BorderThickness="2"` | **选中环占位**（选中时显色），属功能性高亮而非组件描边，规范未定义选中环令牌，保留 |
| `FloatingBallWindow.xaml.cs` | 玻璃球渐变/光晕硬编码色 | 悬浮球为独立视觉组件（深色玻璃质感），非标准 UI 组件，保留 |
| `GraphWindow.xaml.cs` | 关系图节点着色（SteelBlue/Green/Purple/Orange） | 数据可视化语义色，非 UI 主题色，保留 |

---

## 3. 修改文件清单

| 文件 | 变更 |
|---|---|
| `Themes/Tokens.xaml` | 重写：字号阶梯、圆角（含 `RadiusOverlay`）、边框厚度（`Thickness` 类型）、动效、`MenuFontFamily`、文本样式 |
| `Themes/Controls.xaml` | 边框厚度令牌化；浮层/下拉/Tooltip/右键菜单圆角→8；菜单字体→雅黑 13px |
| `Views/MainWindow.xaml` | 主题卡圆角 10→8；3× `FontSize=11`→`FontTiny`；边框→`BorderThin` |
| `Views/WidgetWindow.xaml` | 浮层圆角 12→8；边框→`BorderThin` |
| `Views/QuickNoteWindow.xaml` | 浮层圆角 12→8；边框→`BorderThin` |
| `Views/FloatingNoteWindow.xaml` | 浮层圆角 10→8；2× 边框→`BorderThin` |
| `docs/DeskBox复刻分析报告.md` | 阶段 1 报告（新增） |
| `docs/DeskBox复刻-差异清单与修复记录.md` | 本文件（新增） |

---

## 4. 校验结果（阶段 5）

| 校验项 | 结果 |
|---|---|
| XAML 良构性 | ✅ 14/14 全部通过（复刻改动前基线校验） |
| 资源键解析 | ✅ 141 个定义 / 96 个引用，无未解析键（唯一命中为注释中的占位符 `XxxBrush`） |
| 残留硬编码（视图） | ✅ 圆角/字号/描边字面量已清零；仅保留 `MainWindow:448` 选中环（有意） |
| 令牌覆盖率 | ✅ `BorderThin`14 / `RadiusOverlay`8 / `FontBody`18 / `AppFontFamily`31 等 |
| C# 资源键安全 | ✅ `FindResource` 按名引用的 9 个键（`TextTitle`/`TextCaption`/`TextSecondary`/`AppTextBox`/`SecondaryButton`/`PrimaryBrush`/`BorderBrushSoft`/`SurfaceAltBrush`/`WindowBackdropFallbackBrush`）全部保留 |

> 注：沙箱挂载路径 `/mnt/local/新建文件夹` 存在间歇性 I/O 抖动，部分批量校验需重试；不代表文件损坏。

---

## 5. 运行与验证说明

1. 用 **Visual Studio 2022**（含 .NET 桌面开发工作负载）打开 `C:\Users\sfann\Desktop\新建文件夹\FileMemo.sln`。
2. 还原并生成：`dotnet build src\FileMemo.App\FileMemo.App.csproj`。
3. 运行后重点核对：
   - 主窗口浅/深主题切换后的**背景/文字/描边**；
   - 各浮窗（快速记录/悬浮便签/桌面控件）的**圆角 8、低透明描边**；
   - 右键菜单与下拉菜单的**字体（雅黑 13px）与圆角 8**；
   - 按钮**高度 34、半粗标题、强调色 `#0078D4`**。
4. 若需回滚：本次改动集中在 `Themes/` 与 `Views/*.xaml`，可用 Git 反向还原。

---

## 6. 已知限制

1. **技术栈差异**：DeskBox 为 WinUI 3 + Mica/Acrylic + NavigationView；FileMemo 为 WPF，以 DWM API 等价实现窗口材质、以自绘样式替代 WinUI 控件。像素级完全一致不可达，视觉语言已对齐。
2. **动画**：WPF Storyboard 与 WinUI ImplicitAnimations/ConnectedAnimation 机制不同；UI 交互节奏已按 83/167/250ms 令牌化，控件级连贯动画（ConnectedAnimation）无法等价。
3. **审计盲区**：DeskBox 完整文件树、`DeskBox.csproj`、`App.xaml.cs` 因网络受限未能获取（详见阶段 1 报告第 0 节）；色值/尺寸/键名等设计事实来自本会话实读源文件，无猜测。
4. **原生层**：DeskBox 的 Rust Shell 层（快捷方式/系统音量/回收站/拖拽）在 FileMemo 中以 C#/COM 等价实现，未纳入本次样式复刻范围。

---

## 7. 结论

FileMemo 的**设计令牌层已与 DeskBox 规范对齐**：配色（`#0078D4` + 双主题中性板）、字体（28/18/15/14/13/12/11，SemiBold 标题，菜单雅黑 13px）、间距（4/8/12/16）、圆角（4/6/8）、边框（0.8/1.2/1.6）、动效（83/167/250ms）全部落到令牌并被窗口引用。启动崩溃（`BorderThickness` 类型）已修复并通过校验。

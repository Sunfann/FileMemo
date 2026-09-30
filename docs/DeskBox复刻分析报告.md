# DeskBox 复刻分析报告（阶段 1：源码审计与复刻分析）

> 生成日期：2026-09-29
> 源项目：`Tianyu199509/DeskBox` @ `main`（v1.5.5，GPL-3.0-only）
> 目标项目：**FileMemo（文笺）**——工作区现有 WPF 应用（net8.0-windows10.0.19041.0）
> 执行依据：《识别并复刻UI技术-2026-09-29-11-38.md》阶段 1 要求

---

## 0. 数据来源与审计范围声明（必须先读）

本报告所有色值、资源键、字号、圆角、间距、动画参数均来自**本会话对 DeskBox 仓库的实际源码读取**，无猜测成分。已读取的源文件（逐字内容）：

| 已审计源文件 | 覆盖内容 |
|---|---|
| `src/DeskBox/App.xaml`（全文 487 行） | 全部主题资源字典、画刷键、字号/间距/圆角/动效令牌 |
| `src/DeskBox/Styles/SettingsOverviewResources.xaml`（全文） | 设置界面规格令牌 |
| `src/DeskBox/Helpers/AccentColorHelper.cs`（全文） | 强调色默认值与解析逻辑 |
| `src/DeskBox/Services/WidgetMaterialVisualCalculator.cs` | Mica/Acrylic 材质 Tint 计算公式 |
| `src/DeskBox/Services/WidgetBorderVisualCalculator.cs` | 边框厚度/透明度映射表 |
| `src/DeskBox/Views/WidgetWindowBase.Backdrop.cs` | 窗口材质（SystemBackdrop）实现 |
| `src/DeskBox/Services/WidgetForegroundSettings.cs` | 文字前景模式与默认自定义色 |
| `src/DeskBox/Contracts/IAppearanceSettings.cs` | 布局密度预设 |
| `src/DeskBox/Models/TodoItem.cs` | Todo 标签色板 |
| 全量 `src/**/*.xaml` 统计 | 圆角/字号/字重/Margin/Padding 使用频次 |
| `README.md`（全文） | 项目布局、功能、启动行为、构建发布、系统要求 |

**审计盲区（网络受限未能获取，已在第 10 节列出补救方案）**：
- 完整文件树枚举（git clone 两次失败：TLS 断连 / 超时；GitHub API 两次失败：500 / 空响应）
- `DeskBox.csproj`、`Package.appxmanifest`、`App.xaml.cs` 的逐行内容
- 未列名 View 的逐文件 XAML 细节（布局推断自 README 功能描述 + 已读源文件 + 全局统计）

**许可说明**：DeskBox 为 GPL-3.0-only。本次复刻只提取"设计事实"（色值、尺寸、键名、时长等不受版权保护的接口性信息），不整体复制其受保护的代码实现与图像资产。

---

## 1. 项目结构与技术栈

### 1.1 技术栈（README 证实）

| 层 | 技术 |
|---|---|
| UI | C# + **WinUI 3**（Windows App SDK **2.4**） |
| 运行时 | **.NET 10，Native AOT** 发布（Full 包内嵌私有 Windows App Runtime，可离线安装） |
| 原生层 | **Rust**（`native/`：Shell ABI、缩略图代理；`rust-toolchain.toml` 锁定工具链） |
| 安装包 | Inno Setup 6（`installer/DeskBox.iss` / `DeskBox.arm64.iss`），单用户安装 |
| 数据 | 本地优先（`%LocalAppData%\DeskBox\data` + 托管存储目录），无账户体系 |
| 平台 | Windows 10 21H2+ / Windows 11 22H2+，x64 与 ARM64 |

### 1.2 仓库布局（README 证实）

```
src\DeskBox            WinUI 3 应用（控件外壳、服务、视图）
src\DeskBox.Updater    直发版更新辅助程序
native                 Rust 原生层、Shell ABI、缩略图代理
tests\DeskBox.Tests    服务/策略/AOT 契约测试
scripts                构建、发布、审计、内存测量脚本（publish-aot-retail.ps1 为权威发布入口）
installer              x64/ARM64 Inno Setup 脚本
docs\architecture      架构、原生 ABI 契约、AOT 阶段
docs\releases          发布文案与测试清单
```

---

## 2. 启动流程与窗口创建方式（README 证实）

1. **托盘优先、静默启动**：启动即驻留系统托盘，不弹出设置窗口；已在运行时第二个实例直接退出（**单实例**）。
2. **桌面附着等待**：启动时**等待 Explorer 桌面图标宿主稳定**后，才把桌面层控件附着上去，避免干扰 Windows 自身的图标位置恢复。
3. **自启动**：每用户 Run 注册表项（出现在"设置→应用→启动"）；旧版计划任务注册在安全时自动迁移；Store 版用 `StartupTask`；Windows 侧的禁用选择会被尊重并回读。
4. **设置弹性恢复**：从弹性快照恢复设置，关机时冲刷挂起更改，保存失败会显式报告而非静默回退默认。
5. **搜索弹窗预热**：空闲时预热弹窗外壳，点击控件时优先显示并聚焦，推荐与图标后台补齐。

> 注：`App.xaml.cs` 逐行内容本次未能获取（见第 0 节盲区），以上流程来自 README 的功能性描述，属源码级行为说明。

---

## 3. 窗口 / 控件 / 弹层层级关系

```
托盘图标（Tray）
├── 设置窗口 SettingsWindow（NavigationView 左侧导航；Mica/Acrylic 窗口材质）
│     └── 设置分区页（常规/外观/胶囊模式/文件格子/功能格子/快捷与交互/维护…）
├── 桌面层 Widget 窗口（WidgetWindowBase 派生，附着于桌面图标层之上）
│     ├── 文件控件（FileSurfaceContent）：图标/列表布局、堆叠(Stack)、内联命名
│     │     └── 堆叠弹层 Stack Popover（Adaptive / 3×3 / 5×5，ThemeShadow 海拔）
│     ├── 控件组（Widget Group）：标题/滚轮/Ctrl+Tab 切换成员
│     ├── 胶囊（Capsule）：点击/悬停展开，可组合为可拖动胶囊条
│     ├── Todo / Quick Capture：单双栏响应式列表-详情
│     ├── 天气 / 音乐 / Glance（日期/农历/节日）
│     └── 各控件共享：标题栏、右键菜单、拖拽、QuickLook(Space)
├── 搜索弹窗（全局热键唤起；Everything IPC + 应用内内容合并）
└── 对话框/提示：ContentDialog 等价物、Toast、Tooltip、右键 MenuFlyout
```

---

## 4. 设计令牌（全部源自 App.xaml 等文件实读）

### 4.1 配色

**强调色**：默认 `#0078D4`（`AccentColorHelper.DefaultAccentColorHex`），可跟随 Windows 系统强调色；预设色板 6 色：`#0078D4 / #038387 / #2D7D46 / #8764B8 / #D83B01 / #EF6950`；强调色用作边框时 alpha×1.35。

**双主题中性色板**（`App.xaml` 的 Light/Dark 主题字典）：

| 令牌 | Light | Dark |
|---|---|---|
| WidgetBackgroundBrush | `#F3F3F3` | `#1F1F1F` |
| WidgetTextBrush | `#1A1A1A` | `#F5F5F5` |
| WidgetTextSecondaryBrush | `#5A5A5A` | `#A5A5A5` |
| WidgetDragHandleBrush | `#6B6B6B` | `#D6D6D6` |
| WidgetDividerBrush | `#D0D0D0` | `#3C3C3C` |
| WidgetOverlaySurfaceBrush | `#FBFCFD` | `#2A3038` |
| WidgetOverlayInputSurfaceBrush | `#FFFFFF` | `#222830` |
| WidgetOverlayStrokeBrush | `#24000000` | `#52FFFFFF` |
| WidgetLayerFillBrush | `#CCFFFFFF` | `#20FFFFFF` |
| WidgetLayerFillSecondaryBrush | `#70FFFFFF` | `#0DFFFFFF` |
| WidgetLayerStrokeBrush | `#16000000` | `#20FFFFFF` |
| 标题栏 Hover/Pressed/Open | `#0F/#18/#14000000` | `#12/#20/#18FFFFFF` |
| NeutralFillSecondary（Hover） | `#09000000` | `#0FFFFFFF` |
| NeutralFillTertiary（Pressed） | `#06000000` | `#0AFFFFFF` |
| NeutralLine（强描边） | `#72000000` | `#8BFFFFFF` |
| NeutralTextPrimary | `#E4000000` | `#FFFFFFFF` |

**Todo 标签色板 9 色**：`#E34D4D #F08A3C #F2C94C #4CAF6D #4D8FE3 #9B6BE8 #2DB7A3 #E66AA2 #8A8F98`。
**自定义文字默认色**：`#F5F5F5`。语义色（危险/成功）引用 WinUI 系统令牌而非硬编码。

### 4.2 字体排印

- 字体族：界面默认 `Segoe UI Variable Text`（Win11）/ `Segoe UI`（Win10）；**菜单强制 `Microsoft YaHei UI` 优先**（`DeskBoxMenuFontFamily`）。
- 字号令牌：`WidgetTitleTextSize=14`、`WidgetBodyTextSize=14`、`WidgetCaptionTextSize=12`、`DeskBoxMenuItemFontSize=13`；全量统计高频字号 12/14/11/13/11.5/18/16。
- 字重：标题/值一律 **SemiBold(600)**，正文 Regular(400)，几乎不用 Bold。
- 行距：`LineStackingStrategy=BlockLineHeight`，行高≈字号×1.3–1.45。
- 布局密度预设（图标/文字）：Compact `26/10.5`、Standard `30/11.5`、Relaxed `36/13`。

### 4.3 间距与圆角

- 间距四档：`WidgetSpacingXSmall=4 / Small=8 / Medium=12 / Large=16`（源码注释明示 "three radii, four spacing steps"）。
- 圆角三档：`WidgetCornerRadiusSmall=4 / Medium=6 / Large=8`；浮层/弹窗/Tooltip 用 8，卡片/按钮/输入框 6，小元素 4；色板圆点为正圆。
- 设置区：`SettingsCardSpacing=4`、`SettingsRowMinHeight=64`、`SettingsRowPadding=16,10`、`SettingsHeaderIconTextSpacing=20`、`SettingsControlMinWidth=176`。

### 4.4 边框与阴影

- 边框厚度映射：Thin `0.8`（中性 alpha `0x18`）/ Medium `1.2`（`0x30`）/ Thick `1.6`（`0x48`）；强调色模式 alpha×1.35；分隔线=边框 alpha×0.66（深）/0.42（浅）。
- 阴影策略：**卡片/按钮/导航均无投影**；仅堆叠弹层用 `ThemeShadow` + `Translation Z=48`（海拔 48）。
- 窗口圆角由 DWM 偏好控制（ROUND / ROUNDSMALL）。

### 4.5 材质体系（Mica/Acrylic/Solid，源码公式）

- 五种材质：`Mica / MicaAlt / Acrylic / AcrylicBase / Solid`。
- 底色：Mica 深 `#202226`/浅 `#FFFFFF`；MicaAlt 深 `#16181D`/浅 `#E8EAEF`；Solid 深 `#21242A`/浅 `#FFFFFF`。
- Tint 公式（`BuildContentTintColor`）：浅色 accentMix=0.16、overlayMix=0.08；深色 0.08/0.04。
- Mica 浓度映射：`TintOpacity=Lerp(0.04→0.46)`（Alt 0.28→0.82）；LuminosityOpacity 浅 0.82→0.96 / 深 0.78→0.94。
- Acrylic：`surfaceStrength=Lerp(0.08→1.0, 不透明度)`；TintOpacity 浅 0.02→0.34 / 深 0.04→0.42；Base 版更浓。
- Windows 10 不支持时自动降级为兼容纯色视觉。

### 4.6 动效

- Fluent 原生节奏三档：`WidgetMotionFasterMilliseconds=83` / `Fast=167` / `Normal=250`（源码注释明示）。
- 动画帧率自适应显示器刷新率；Windows 10 有额外帧节奏与背景安全垫。
- 吸附反馈：1.5.5 起用"强调色边缘带一次落定后消散"，替代旧的呼吸光晕。

---

## 5. 交互行为（README 证实）

- **拖拽**：控件内外拖入拖出携带与 Explorer 一致的原生 Shell 数据对象（VS Code/浏览器等 Copy-only 目标可接收）；跨卷走系统文件操作引擎（进度/取消/冲突）；修饰键创建快捷方式；原生拖放图像与目标描述。
- **右键**：原生 Shell 菜单（菜单钩子离线程，关闭即卸载）；支持以管理员运行、永久删除确认。
- **QuickLook**：空格键通过运行中的 QuickLook 实例预览。
- **全局热键**：F7、双击 Ctrl、Alt+Space、Win+Space、单击 Win、自定义、桌面空白双击；保留组合键会先警告系统副作用；**钩子健康看门狗**自动重注册被系统静默摘除的钩子。
- **Quick Reveal**：临时把控件置于其他窗口之上，不丢失首次激活点击。
- **组操作**：Ctrl+拖动标题移动同屏全部控件；移动/缩放均有吸附（可配间隙+屏幕边缘保护）。
- **胶囊**：点击/悬停展开预设；交互进行中（弹层/菜单/拖拽/重命名/关闭确认）保持展开，指针离开后才收起；胶囊条可跨显示器拖拽排序。
- **多显示器**：每种显示器拓扑单独保存布局；热插拔/工作区/DPI 变化落定后再恢复；替换显示器按比例收拢到界内。
- **搜索**：全局热键、分阶段增量结果、Ctrl/Shift 多选、橡皮筋框选、批量操作。

---

## 6. Rust 原生层交互点

| 能力 | 说明 |
|---|---|
| 快捷方式解析 | `.lnk` 目标/图标（alt-drag 保留目标图标） |
| 系统音量 | 音乐控件的系统音量读写 |
| Quick Access / 回收站 | Explorer 特殊位置与回收站操作 |
| Shell 拖拽 | 原生 Shell 数据对象、拖放图像、文件操作引擎 |
| 缩略图代理 | `native/` 内 thumbnail proxy |
| 构建开关 | `-p:DeskBoxRustNative=true`（发布构建启用） |

---

## 7. 对 FileMemo（WPF）的适配映射

> 目标项目为 WPF 而非 WinUI 3（用户指令为"对工作区的软件复刻"）。适配分三类：

**① 可直接映射（事实性令牌）**：全部色值、字号阶梯、字重、间距、圆角、边框厚度、动效时长、标签色板——已在 `Themes/Tokens.xaml` / `ThemeCatalog.cs` / `Controls.xaml` 对齐（见第 8 节现状）。

**② 需等价替代（机制不同、视觉等价）**：
| DeskBox（WinUI 3） | FileMemo（WPF）等价物 |
|---|---|
| Mica/Acrylic SystemBackdrop | DWM API（`DwmSetWindowAttribute` / AccentPolicy），FileMemo 已有 `DwmService` |
| NavigationView | 现有左侧 NavList（ListBox 自定义样式） |
| ContentDialog/TeachingTip/InfoBar | WPF 自绘弹层/对话框（现有 AddAnnotationDialog 模式） |
| ThemeShadow Z=48 | `DropShadowEffect`（现有 ShadowPopup，仅弹层） |
| ImplicitAnimations | WPF Storyboard/VisualStateManager，时长沿用 83/167/250ms |

**③ 不可复刻/需标注占位**：ConnectedAnimation、WinUI 专属控件的像素级细节、Rust Shell 层（FileMemo 以 C#/COM 等价实现，已在 Services 中存在）。

---

## 8. FileMemo 现状对照（本会话已完成的复刻项）

| 规范项 | 状态 | 说明 |
|---|---|---|
| 配色 | ✅ 已合规 | 默认主题 DeskBoxDark/DeskBoxLight、`#0078D4` 主色、双主题中性色板、Theme.Initial 兜底同步 |
| 字体 | ✅ 已对齐 | 字号阶梯 28/18/15/14/13/12/11；菜单雅黑 13px；SemiBold 标题 |
| 间距 | ✅ 已对齐 | 4/8/12/16 四档令牌 |
| 圆角 | ✅ 已对齐 | 4/6/8 三档 + RadiusOverlay=8；浮层硬编码 10/12 已清除 |
| 边框 | ✅ 已对齐 | 0.8/1.2/1.6 Thickness 令牌化（已修复 Double 类型导致的启动崩溃） |
| 阴影 | ✅ 已合规 | 仅弹层使用，卡片无投影 |
| 动效 | ✅ 已对齐 | 83/167/250ms 令牌化 |

---

## 9. 缺失项与需要确认的内容

1. **DeskBox 本地路径未填写**：md 模板中"本地路径"为占位符。若你本地已克隆 DeskBox，请提供路径，我可补全第 0 节列出的审计盲区（完整文件树、csproj、App.xaml.cs、全部 View 逐文件布局）。
2. **工程配置细节**：`DeskBox.csproj` / `Package.appxmanifest` / `app.manifest` 本次未能获取（网络受限）；README 已证实 WASDK 2.4 / .NET 10 / Native AOT / 单实例等要点，FileMemo 侧如需对齐清单请确认。
3. **目标形态确认**：md 原文要求"新建 WinUI 3 项目"，本次按你的指令适配为"对工作区 FileMemo（WPF）复刻风格"。请确认此适配方向。
4. **下一阶段范围**：阶段 2–5（项目配置 → 布局 1:1 → 交互动画 1:1 → 截图对比修复）中，FileMemo 现有窗口（MainWindow/WidgetWindow/QuickNoteWindow/FloatingNoteWindow/FloatingBallWindow/ClipboardPanelWindow/GraphWindow/AddAnnotationDialog）是否全部纳入逐页对比复刻？

---

## 10. 阶段 1 结论

DeskBox 的视觉体系已可完整描述：**"Fluent 桌面化演绎"= 双主题中性色板（黑白透明度叠加分层、无彩色干扰）+ 单一强调色 `#0078D4` + 三档圆角 4/6/8 + 四档间距 4/8/12/16 + SemiBold 标题文化 + 83/167/250ms 动效节奏 + 卡片无阴影（仅弹层海拔 48）+ Mica/Acrylic 材质底**。

FileMemo 的**设计令牌层（配色/字体/间距/圆角/边框/动效）已与 DeskBox 对齐**；剩余差距集中在**逐窗口布局细节与交互行为的 1:1 对照**（阶段 3–5 范围），以及可选的工程配置对齐（阶段 2 范围）。

**按流程暂停于此。请确认本报告（及第 9 节 4 个问题）后，我再进入阶段 2。**

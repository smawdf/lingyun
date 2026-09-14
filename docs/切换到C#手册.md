# 灵云 → C# 迁移手册

> 目标：把现在这套 Python / PySide6 的「灵云」改写成 C# / .NET 桌面小工具，能力与观感对齐或超过现版。
> 更新日期：2026-09-10。对应仓库状态：**只保留 widgets 版 UI**（QML 实验一套已删除，见文末「仓库现状」）。

---

## 一、迁移基线（先看清现在有什么）

| 项目 | 现状 |
| --- | --- |
| 语言/UI | Python 3.10+ / PySide6（Qt Widgets） |
| 入口 | `run.py` → `zaoshui.main` → `ui/*`（widgets） |
| 打包 | PyInstaller `--onefile --windowed`，产物 `灵云.exe`（当前约 75 MB） |
| 后端模块 | `config` / `scheduler` / `actions` / `media_session`（PowerShell SMTC 桥）/ `monitors` / `win_focus` / `app_icon` / `system` / `quick_actions` |
| 测试 | pytest 37 条（配置、调度、媒体解析、页面、快捷球、UI 集成） |
| 配置 | 同目录 `灵云配置.json`（v2，原子写） |

### 待迁移的功能清单

| 模块 | 功能 | 现实现位置 |
| --- | --- | --- |
| 灵动岛窗体 | 顶部居中、无边框、透明、置顶、不抢焦点、点击展开/`✕`/`Esc` 收起 | `ui/island.py` |
| 形变动画 | 纯 `QPainter` 插值绘制胶囊，**原生窗口不随动画 resize**；OutExpo 落位 | `ui/island.py` |
| 扫光/光斑 | 岛内水平掠扫（仅展开时）；性能/天气/事项/月份页各有主题渐变 + 漂移光斑；媒体焦点有 Fluid Blobs | `ui/island.py` |
| 媒体岛 | SMTC 会话（标题/歌手/封面/播放状态/进度）、播放暂停/上下首/拖进度、点封面跳源窗口；与计划并存时旁挂圆点切换焦点 | `media_session.py` + `ui/media_page.py` + `ui/media_visual.py` |
| 定时动作 | 按星期与/或日期调度，动作＝关机/锁屏/息屏/暂停媒体；提前 15 分钟变红、最后 10 秒蜂鸣、可取消 | `scheduler.py` + `actions.py` + 岛上 alert 页（`main.py`） |
| 功能页 | 计划 / 日历 / 媒体 / 性能 / 天气 / 事项 / 月份 | `ui/plan_tab.py`、`calendar_tab.py`、`media_page.py`、`perf_page.py`、`weather_page.py`、`nextup_page.py`、`month_page.py` |
| 快捷球 | 胶囊悬停右缘扇出 9 个快捷动作（关机/重启/睡眠/资源管理器/设置/任务管理器/浏览器/YouTube/命令行） | `ui/widgets_extra.py` + `quick_actions.py` |
| 提示条 | Caps/Num Lock 状态提示 | `ui/widgets_extra.py` |
| 性能采样 | CPU/内存/磁盘/网络速率（1 s 采样、差值） | `monitors.py` |
| 天气 | Open-Meteo + IP 定位（ip-api.com → ipapi.co → 北京兜底），30 分钟刷新 | `monitors.py` |
| 系统集成 | 单实例互斥 + 二次启动唤出（127.0.0.1:47219 发 `show`）、托盘（显示/暂停/退出）、开机自启（HKCU Run）、`--demo` | `main.py` + `system.py` |

---

## 二、可借鉴的开源项目（含许可证，务必读完再用）

| 项目 | 技术栈 | 许可证 | 能借鉴什么 |
| --- | --- | --- | --- |
| [GEORGEWWWU/NotchPeninsula](https://github.com/GEORGEWWWU/NotchPeninsula) | C# / .NET 10 / Win32 + SkiaSharp + NAudio | Apache-2.0 | **最直接的参考**：完整 C# 灵动岛实现——Win32 窗体贴顶、SkiaSharp 自绘、WASAPI 音频频谱、系统 Toast 监听、托盘与自启、注册表配置 |
| [programmersd21/nimbus](https://github.com/programmersd21/nimbus) | Python / PySide6 | MIT | 弹簧物理动画算法与预设参数（`F = -k·x − d·v`，子步长限制）；媒体/通知/时钟等模块划分思路 |
| [JNTMTMTM/eIsland](https://github.com/JNTMTMTM/eIsland) | Electron + React + TypeScript | GPL-3.0-or-later（含附加条款：保留署名；禁止面向 Apple 平台分发） | **只做 UI/UX 与信息架构参考**（概览/天气/歌词/设置/工具箱的页面组织）。GPL 有传染性，不要复制其代码到本项目 |
| [rajsriv/dynamic-island-for-windows](https://github.com/rajsriv/dynamic-island-for-windows) | Python / PyQt6 | 仓库**没有 LICENSE 文件**（README 提到 MIT，但文件中 404） | 只借鉴思路与交互，不要复制代码（未经授权的代码默认保留全部权利）。本项目早期也只参考了它「窗口固定尺寸、纯绘制形变」这一条思路 |

### 使用许可的注意事项

- **Apache-2.0（NotchPeninsula）**：可用于闭源/商用，但分发时要附上 Apache-2.0 许可文本、保留版权与声明，并标注你修改过的文件；若仓库有 `NOTICE` 文件需一并保留。
- **MIT（nimbus）**：可用于闭源/商用，分发时保留版权声明与许可文本即可。
- **GPL-3.0（eIsland）**：一旦复制代码，整个项目就要以 GPL-3.0 开源分发；只看设计、不抄代码则不受约束。
- **无许可证（rajsriv）**：默认「保留所有权利」，除个人本地阅读外不可复用。
- 本仓库目前也**没有 LICENSE**。若将来要把灵云开源或分发，建议自选一个（代码自用可暂不加）。

### 官方文档（不用翻墙也基本能开）

- WinUI 3 / Windows App SDK：<https://learn.microsoft.com/windows/apps/winui/winui3/>
- WPF：<https://learn.microsoft.com/dotnet/desktop/wpf/>
- 系统媒体控制（SMTC，`Windows.Media.Control`）：<https://learn.microsoft.com/uwp/api/windows.media.control>
- 窗口圆角/背板（DWM）：<https://learn.microsoft.com/windows/apps/desktop/modernize/apply-rounded-corners>
- SkiaSharp：<https://github.com/mono/SkiaSharp>
- 托盘图标库 H.NotifyIcon：<https://github.com/HavenDV/H.NotifyIcon>
- 单文件发布：<https://learn.microsoft.com/dotnet/core/deploying/single-file/overview>

---

## 三、目标技术栈选型

### 推荐：.NET 8/9 + WPF 外壳 + SkiaSharp 岛体自绘 + 原生 Win32 互操作

分工：

- **WPF**：托盘、右键菜单、设置窗口、消息循环；无边框/透明/置顶窗口属性开箱即用。
- **SkiaSharp**：岛体一切绘制（胶囊形变、渐变、扫光、频谱），替代现在的 `QPainter`，并可用 `SKShader` 做现在的圆锥/线性渐变。
- **Win32 P/Invoke**：`SetWindowCompositionAttribute`（亚克力）、`DwmSetWindowAttribute`（圆角/背板）、`SetWindowRgn`（裁剪）、`FindWindow`/`SetForegroundWindow`（跳源窗口）、`GetKeyState`（Caps/Num Lock）。
- **CsWinRT**：直接调 `Windows.Media.Control`（SMTC）和通知监听——**不再需要 PowerShell 桥**（Python 版是因为 3.14 没有 winsdk 轮子才绕的），少一层进程、少 30–60 MB 内存、延迟更低。

### 备选对照

| 方案 | 透明置顶+任意裁剪 | 亚克力/模糊 | 自绘能力 | 开发速度 | 备注 |
| --- | --- | --- | --- | --- | --- |
| WPF + SkiaSharp（推荐） | 容易（属性 + 少量互操作） | 原生 API 互操作 | 强 | 快 | 生态成熟，托盘/菜单省事 |
| Win32 + SkiaSharp 全自绘 | 最容易（完全可控） | 原生 API | 强 | 慢 | 最省内存，最接近 NotchPeninsula；窗口/输入/DPI 全手写 |
| WinUI 3 + Windows App SDK | 麻烦（需要 HWND 互操作拿不到裸句柄） | 内置 Mica/Acrylic 最省事 | 中（可挂 SwapChainPanel） | 中 | 控件最现代，但无边框拖动/穿透/置顶坑最多 |

结论：**首版走 WPF + SkiaSharp**；如果目标是「极低内存占用 + 极致性能」，第二阶段再考虑 Win32 全自绘（NotchPeninsula 就是这条路，可以直接对照它的窗体代码）。

---

## 四、功能 → C# API 映射表

| 功能 | Python 现实现 | C# 对应做法 |
| --- | --- | --- |
| 无边框透明置顶、不抢焦点 | `Qt.Tool` + `WA_TranslucentBackground` + 置顶 | WPF `WindowStyle=None`、`AllowsTransparency=true`、`Topmost=true`、`ShowActivated=false`、`ShowInTaskbar=false`；扩展样式补 `WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE`，用 `ShowWindow(SW_SHOWNOACTIVATE)` |
| 纯绘制形变（窗口不缩放） | `QPainter` 插值 + `QPropertyAnimation` OutExpo | 固定大窗口 + `SkiaSharp` 逐帧重绘；动画用 `CompositionTarget.Rendering`（VSync 对齐，120 Hz 屏也顺）或 `DispatcherTimer` 16 ms |
| 亚克力/玻璃 | `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` + 色键透明 | 同一 API 的 P/Invoke；Win11 22H2+ 可优先试 `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)` |
| 圆角/非矩形裁剪 | `SetWindowRgn` + `DwmSetWindowAttribute(33, DWMWCP_ROUND)` | 同 API；像素级不规则形状用 `SetWindowRgn` + `CreateRoundRectRgn` |
| 渐变 / 扫光 / 光斑 | `QLinearGradient` / `QConicalGradient` + 相位 | `SKShader.CreateLinearGradient` / `CreateSweepGradient`；`SKBlendMode.Screen` 对应现在的 Screen 混合 |
| 系统媒体会话（标题/歌手/封面/状态/进度） | PowerShell 常驻桥（stdin/stdout JSON） | `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()` → `GetSessions()` / `TryGetMediaPropertiesAsync()`（拿 Title/Artist/Album/`Thumbnail`）、`GetTimelineProperties()`（Position/Duration） |
| 播放/暂停/上下首/跳转 | SMTC `TryPlayAsync` 等 | 同名 `TryPlayAsync / TryPauseAsync / TrySkipNextAsync / TrySkipPreviousAsync / TryChangePlaybackPositionAsync` |
| 封面图 | 桥返回缩略图字节 → `PIL` → QPixmap | `Thumbnail.OpenReadAsync()` → `IRandomAccessStream` → `byte[]` → `SKBitmap.Decode`；异步 + 缓存 |
| 跳到源窗口 | `FindWindow` + `SetForegroundWindow` | 同 API（user32 P/Invoke），逻辑照搬 `win_focus.py` |
| 关机/重启/锁屏/息屏 | `shutdown.exe` / `rundll32 user32.dll,LockWorkStation` / `SendMessage WM_SYSCOMMAND, SC_MONITORPOWER` | `ExitWindowsEx(EWX_SHUTDOWN \| EWX_POWEROFF)`（需 `SE_SHUTDOWN_NAME` 权限）或继续 shell 调 `shutdown.exe`；锁屏 `LockWorkStation()`；息屏 `SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2)` |
| 计划调度（星期/日期 → 下次目标 → 倒计时 → 提醒） | `scheduler.py` + 250 ms QTimer | 逻辑照搬；定时用 `PeriodicTimer(250 ms)` 或 `DispatcherTimer` |
| 最后 10 秒蜂鸣 | `QApplication.beep()` | `Console.Beep()` 或 `P/Invoke Beep(freq, dur)` |
| 性能采样（CPU/内存/磁盘/网络） | `psutil` | CPU：`GetSystemTimes` 差值（或 `PerformanceCounter`）；内存：`GlobalMemoryStatusEx`；磁盘：`GetDiskFreeSpaceEx`；网络：`NetworkInterface.GetIPStatistics()` 差值 |
| 天气 | Open-Meteo + IP 定位，`urllib` | `HttpClient` + `System.Text.Json`；接口完全不变 |
| 事项/日历/月进度 | Qt 控件拼装 | WPF 控件或 SkiaSharp 自绘（推荐自绘，风格更统一） |
| 配置持久化（原子写） | `tempfile` + `os.replace` | `System.Text.Json` + 写临时文件后 `File.Replace` |
| 单实例 + 二次启动唤出 | 全局互斥体 + 本地 TCP 端口发 `show` | `Mutex("Global\\灵云_单实例锁")`；唤出用 `NamedPipeServerStream`（比 TCP 端口干净，无防火墙弹窗风险） |
| 托盘（显示/暂停/退出） | `pystray` | `H.NotifyIcon.Wpf` 或 WinForms `NotifyIcon` 互操作 |
| 开机自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | `Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)`；写自启路径用 `Environment.ProcessPath`（单文件发布下 `Assembly.Location` 不可靠） |
| 开机充电动画 | 电池监视 2 s + 上升沿 | `System.Windows.Forms.SystemInformation.PowerStatus` 或 `GetSystemPowerStatus` |
| 打包 | PyInstaller `--onefile` | `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`；想更小可以试 `PublishAot`（注意 WinRT/反射与 AOT 的兼容性，先用 self-contained 单文件稳妥） |

---

## 五、踩坑清单（都在 Python 版里真实踩过，迁移时别重犯）

1. **原生窗口绝不逐帧 resize**。动画期间移动/缩放真实 HWND 会掉帧、撕裂。做法：窗口固定足够大的尺寸，岛体、阴影、扫光全在画布上画，只重绘不 resize。
2. **透明窗口的点击命中**。色键透明区域在 Windows 上仍会吃掉鼠标事件。需要「只在岛体范围内响应」时，用 `SetWindowRgn` 把窗口区域裁出来，或自己做命中测试（`WM_NCHITTEST` 返回 `HTTRANSPARENT`）。
3. **面板态要能点到页面控件**。Python 版的做法是 compact 态才让「点哪里都展开」的透明层接管事件，expanded 态把该层关掉/降 z 序，否则按钮点不动。C# 里对应「按状态切换命中测试」。
4. **不抢焦点**。`WS_EX_NOACTIVATE` + `ShowActivated=false`，否则每次展开都会把前台窗口切走（看视频时很烦）。
5. **per-monitor DPI**。app.manifest 声明 `PerMonitorV2`，SkiaSharp 画布按当前显示器 DPI 缩放，否则外接屏上字会糊或者岛会偏。
6. **亚克力在 Win11 22H2+ 的行为差异**。老的 `SetWindowCompositionAttribute` 仍可用，但新系统优先 DWM 背板；两套都写、按系统版本选。
7. **SMTC 线程模型**。WinRT 调用要在有消息泵的线程（WPF UI 线程或专门的 STA 线程），异步 API 别在锁里 `await`。
8. **封面异步解码 + 尺寸缓存**。切歌时同步解码大图会卡；用 `SKBitmap.Decode` 前先按目标尺寸采样，并缓存最近几张。
9. **高刷屏动画**用 `CompositionTarget.Rendering` 跟着 VSync 走，不要自己起 1 ms 定时器。
10. **单文件发布**：WinRT 与本机库需要 `IncludeNativeLibrariesForSelfExtract=true`；自启/唤出用 `Environment.ProcessPath`。
11. **托盘菜单里的「暂停计划」要线程安全**：Python 版吃过亏（托盘线程直接回调 UI）。C# 里 `NotifyIcon` 事件在 UI 线程，但后台定时器回调记得 `Dispatcher.Invoke`。

---

## 六、分阶段迁移路线（每阶段都能单独跑起来验证）

| 阶段 | 内容 | 验收标准 |
| --- | --- | --- |
| 0 | .NET 项目骨架：WPF 无边框透明置顶窗口，顶部居中，点击展开/收起（先不做形变，直接切两档尺寸） | 双击 exe 出现在屏幕顶部居中；点击展开/收起；不抢焦点；任务栏无图标 |
| 1 | SkiaSharp 岛体：胶囊形变插值 + 阴影 + 渐变 + 展开态水平扫光；帧率对齐 VSync | 展开/收起动画与 Python 版观感一致或更好；120 Hz 屏不撕裂 |
| 2 | 媒体：SMTC 会话轮询 + 封面异步 + 三键控制 + 进度拖拽 + 跳源窗口 | 播放 B 站/网易云时岛内显示曲目与控制，操作与系统媒体键一致 |
| 3 | 计划与提醒：调度计算 + 倒计时 + 提前 15 分钟变红 + 最后 10 秒蜂鸣 + 关机/锁屏/息屏/暂停媒体 + 取消 | 设一个 2 分钟后的「暂停媒体」计划，到点正确执行；`--demo` 可复现 |
| 4 | 功能页：计划/日历/媒体/性能/天气/事项/月份 + 快捷球扇出 + Caps/Num 提示条 | 逐页对照 Python 版；性能/天气/月进度数据正确 |
| 5 | 系统集成与发布：单实例 + 唤出、托盘（显示/暂停/退出）、开机自启、配置读写、单文件发布 | 重复启动唤出已有实例；重启后自启生效；产物为绿色单文件 exe |

**迁移期建议**：Python 版保持可用（`py src/zaoshui/main.py` / 现有 `灵云.exe`），C# 版与它并排跑，逐页对照验收；全部通过后 Python 版即可归档。

---

## 七、附：配置 schema 与目录对照

### `灵云配置.json`（v2，C# 版的 DTO 就照这个来）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `tool_version` | int | 固定 2 |
| `enabled` | bool | 计划总开关（默认 false，不执行） |
| `weekdays` | int[] | 1–7（周一=1） |
| `dates` | string[] | `YYYY-MM-DD` |
| `hour` / `minute` | int | 执行时刻（24 小时制） |
| `theme` | `"dark"` \| `"light"` | 主题 |
| `action` | `"shutdown"` \| `"lock"` \| `"display_off"` \| `"pause_media"` | 到点动作 |
| `also_pause_media` | bool | 到点同时暂停媒体 |
| `location` / `lat` / `lon` | string / float? / float? | 天气城市与坐标（IP 定位结果） |
| `tasks` | array | 事项：`{name, category, color, time}`，最多 12 条 |
| `animation_style` | `"Fluid Blobs"` \| `"Glow Sweep"` \| `"Neon Border"` | 媒体播放动画样式 |

### 文件对照（Python → C# 建议类）

| Python | C# 建议 |
| --- | --- |
| `run.py` / `main.py` | `Program.cs` + `App.xaml.cs` + `IslandWindow.xaml(.cs)` |
| `ui/island.py` | `IslandWindow` + `IslandRenderer`（SkiaSharp）+ `MorphAnimator` |
| `ui/plan_tab.py` 等页面 | `Pages/*.xaml` 或统一 `IslandPageRenderer` |
| `media_session.py` | `Services/MediaSessionService.cs`（WinRT SMTC） |
| `monitors.py` | `Services/PerfSampler.cs` / `WeatherService.cs` / `LockKeyMonitor.cs` / `BatteryMonitor.cs` |
| `scheduler.py` / `actions.py` | `Services/Scheduler.cs` / `Actions/PowerActions.cs` |
| `config.py` | `Config/AppConfig.cs` + `ConfigStore.cs` |
| `system.py` | `Platform/SingleInstance.cs` / `TrayService.cs` / `AutoStart.cs` |
| `quick_actions.py` | `Services/QuickActions.cs` |
| `win_focus.py` | `Platform/WindowFocus.cs` |
| `app_icon.py` | `Services/AppIconProvider.cs` |

---

## 八、仓库现状（迁移前的基线，2026-09-10）

- **唯一在跑的 UI 是 widgets 版**：`run.py` → `zaoshui/main.py` → `ui/*`；`build_exe.ps1` 打的也是它。
- v6 期间做过一套 QML 重写（`main_qml.py` / `bridges.py` / `qml/*`），从未接入入口，已从工作区删除；如需回看，git 历史提交 `1a81186` … `9d63be2` 里完整保留。
- 胶囊态扫光已按 QML 版的修法移植回 widgets 版：**只在展开时播放，改为岛内水平掠扫（无旋转）**。
- `build_exe.ps1` 需要以 UTF-8 BOM 保存，否则 Windows PowerShell 5.1 会按 ANSI 读取导致中文路径乱码。
- 测试：`py -m pytest`（37 passed）。

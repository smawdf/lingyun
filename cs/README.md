# 灵云 C# 版

对照 `docs/切换到C#手册.md` 迁移。当前状态：**Python 版已退役删除，C# 版是唯一实现**；
运行时主界面为 `Ui/NativeIslandApp.cs`（Win32 分层窗 + SkiaSharp 自绘）。

## 模块总览（`cs/LingYun/`）

| 模块 | 文件 | 状态 |
|------|------|------|
| 配置 v2 原子写 | `Config/AppConfig.cs` | 与 Python 同 schema（`quick_fan` 已随扇出球移除） |
| 调度星期/日期 | `Services/Scheduler.cs` | ISO weekday 对齐 |
| 关机/锁屏/息屏/暂停媒体 | `Services/PowerActions.cs` | |
| **SMTC 媒体会话** | `Services/MediaSessionService.cs` | CsWinRT 直连，无 PS 桥；多会话选源 + 浏览器标题清理 |
| 性能采样 / 天气 | `Services/Monitors.cs` | CPU/内存/网络 + Open-Meteo；定位链=手填优先 → Windows 系统定位（STA 泵线程宿主 WinRT）→ IP 兜底 |
| 音频频谱 | `Services/AudioSpectrumService.cs` | WASAPI 环回 + 5 段 Goertzel + AGC；派生自 NotchPeninsula（Apache-2.0，见 THIRD-PARTY.md） |
| 系统音量 | `Services/AudioVolumeService.cs` | 主音量读写 + 静音切换；派生自 NotchPeninsula `Audio.cs`；无设备时整行隐藏 |
| 在线歌词 | `Services/LyricsService.cs` | LRCLIB 同步歌词（LRC 解析 + 按播放位置取句）；离线/查不到静默留空 |
| 单实例 + 命名管道唤出 | `Platform/SingleInstance.cs` | 含 `WindowFocus`（跳源窗口） |
| 开机自启 | `Platform/AutoStart.cs` | HKCU Run |
| 托盘 | `Platform/TrayService.cs` | 显示 / 暂停计划 / 岛设置 / 切换显示器 / 音频频谱 / 显示歌词 / 浅色主题 / 开机自启 / 退出 |
| 设置窗口控件长相 | `Ui/SettingsWindow.cs`（模板部分） | 药丸单选 / 开关 / 扁平按钮都用自定义 ControlTemplate，**去掉 WPF 默认模板的 Aero 悬停蓝**；经典档显式交回系统默认模板；标题栏必须有 `Transparent` 背景（`null` 不参与命中测试 → 拖不动，自测用 `InputHitTest` 钉住） |
| 设置窗口材质 | `Platform/WindowMaterial.cs` | 分层窗（`AllowsTransparency`，四角真透明无黑框）；材质由 `theme` 推导（`SettingsWindow.MaterialFor`）：**亚克力**走 accent 系统模糊（DWM 合成、移动零延迟），**液态玻璃**与岛同款清晰透明；色调只画一次（亚克力交给 DWM、玻璃由 WPF 画并跟随透明度）；圆角只有亚克力裁窗口区域（`NeedsRegion`），玻璃由 Border 自绘避免锯齿弧 |
| 岛设置（主页式） | `Ui/SettingsWindow.cs` | 左侧五个分区（外观 / 位置与大小 / 显示内容 / 歌词 / 关于）+ 右侧内容，820×580 固定尺寸；含界面材质三档、岛主题四选一、背景透明度（滑杆 + 三档预设）、胶囊/展开缩放、位置、显示器切换、组合模式与模块、网速、通知、自动隐藏、歌词（卡拉OK/延迟）、自启、诊断入口；滑杆实时预览（ApplyConfig/ApplyGeometry 走岛线程队列），关窗写盘 |
| 多显示器 | `Platform/Displays.cs` | 按工作区落位，拔屏自动回退 |
| 自动隐藏 | `Ui/NativeIslandApp.cs`（`UpdateAutoHide`） | 默认关闭：无媒体且鼠标离开 10s 收起，光标到工作区顶部 4px 或媒体/通知/托盘唤出时恢复 |
| 系统通知 | `Services/ToastService.cs` | WinRT `UserNotificationListener` 轮询；启动高水位（历史通知不回放）、带 AUMID/Id |
| 通知点击唤醒 | `Services/AppActivatorService.cs` | 三级激活：WinRT 包激活 → COM 激活管理器 → 按进程名/标题前台化；派生自 NotchPeninsula `appactivator.cs`（Apache-2.0） |
| 快捷动作 | `Services/QuickActions.cs` | 6 个动作；睡眠/重启/关机标记 `Destructive`，**只能长按 900ms 确认** |
| 灵动岛（运行时） | `Ui/NativeIslandApp.cs` | 形变 / 命中 / 媒体 / alert / 六页面板 / 通知条 / 组合模式 / 卡拉OK，全自绘 |
| 配色 | `Ui/IslandPalette.cs` | 深/浅/液态玻璃（浅色+深色两套）四套材质 + `theme=system` 跟随系统 + 背景透明度（只压背景类 alpha）+ 液态玻璃自适应（`PreferDarkGlass`：先保对比度、再保与背景的分离度，带迟滞）+ `Over`/`Composite` 合成后 WCAG 对比度自检；所有绘制统一取色 |
| 背景采样（自适应用） | `Services/BackdropSampler.cs` | BitBlt 进复用 DIB + 直接读内存算平均色/亮度/最亮分区（实测 4ms/次）；**采岛周围一圈并剔除岛自身**——DWM 下 BitBlt 会把自己的分层窗一起采进来 |

> `IslandWindow.xaml(.cs)`、`Ui/ExpandedPages.cs`、`Ui/QuickFan.cs` 是早期 WPF 版实现，
> **保留作对照/后续 UI 扩展，当前不参与运行**（`QuickFan` 的悬停扇出模型已被「快捷」页取代）。

## 交互模型（重要）

- **紧凑态永远只有时间 / 媒体 / 通知**，没有任何功能按钮，也不响应悬停；
- 所有功能在**点击展开后的面板**里：页签 计划 / 性能 / 天气 / **日程** / **日历** / **快捷**（滚轮或点击切换）；
- 系统通知到达时**抢占胶囊**（约 6 秒，含媒体播放中），点击唤醒来源应用；
- 组合模式（默认关闭）下胶囊同屏显示 时间 + 硬件 + 媒体，宽度按内容自动伸缩；
  两个「定宽槽」（时钟按 `88:88`、百分比按 `100%`）保证倒计时/数字跳动时宽度不抖；
- 「快捷」页 3 列网格：浏览器 / 命令行 / **自定义程序（最多 3 个）** 单击即执行；
  睡眠 / 重启 / 关机带红环预警，**必须按住 0.9 秒**（红色进度弧走满）且松手时指针仍在按钮上才触发，
  长按中指针滑出按钮即取消。资源管理器 / 设置 / 任务管理器三个动作已被移除。

外观契约：主题四选一（深 / 浅 / 跟随系统 / **液态玻璃**），液态玻璃默认浅色、材质在 Skia 里画
（不是桌面级 Acrylic——岛是 `UpdateLayeredWindow` 分层窗，拿不到桌面像素）；**液态玻璃自适应**
（`glass_adaptive`，默认开）每秒抓一次岛周围桌面的亮度，在浅色玻璃（深字）与深色玻璃（白字）之间
自动切换：先保证文字对比度达标，再保证岛与背景分得开（白底上的白玻璃会糊成一片），带迟滞不来回闪；
背景透明度 40–100% 只压背景类 alpha（主体/卡片/轨道/阴影/边框），文字与强调色不变。

这套契约由 `lingyun.exe --self-test` 断言守护（危险动作单击不执行、按不够时长不执行、
长按中移开不执行、快捷页顶行不含危险动作、频谱 80Hz 正弦 → band0 主导、多来源选源回落顺序、
B站站标 Always 策略、通知宽度/抢占规则、组合模式槽位与自动长度、卡拉OK进度与延迟补偿、
性能采样、性能页网速开关、主题解析与不透明度、液态玻璃恒浅色与材质 alpha 单调、
液态玻璃自适应（深/浅背景选材质、迟滞、白底翻深色、合成色）、透明底离屏 alpha 与深色材质白字、
自动隐藏谓词、设置窗口材质三档与系统版本回退链、标题栏命中测试等，共 181 条）。

## 构建 / 运行

```powershell
$env:DOTNET_ROOT = 'C:\dotnet-sdk'
$env:PATH = "C:\dotnet-sdk;$env:PATH"
$env:NUGET_PACKAGES = 'C:\nuget-packages'
cd cs\LingYun
dotnet build -c Release
# 单文件自包含发布
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
Copy-Item -Force publish\lingyun.exe ..\..\lingyun.exe
```

产物：
- 开发运行：`bin/Release/.../lingyun.exe`
- 单文件自包含：`publish/lingyun.exe`（约 190 MB，含 .NET 运行时）
- 部署副本：仓库根 `lingyun.exe`（gitignore，不入库）

## 诊断

```powershell
lingyun.exe --self-test          # 181 条契约断言（频谱 DSP、选源回落、图标策略、音量钳位、歌词解析、卡拉OK进度、组合模式布局、通知宽度/抢占、性能页网速、主题与不透明度、液态玻璃材质/自适应与透明底 alpha、自动隐藏、开关持久化、配色对比度、日程页布局、媒体焦点沿、岛设置几何）
lingyun.exe --self-test --diag-quick   # 自测 + 快捷页契约报告（写 灵云-diag.txt）
lingyun.exe --dump-frames        # 离屏渲染各状态帧（写 灵云-diag/*.png）；含 -glass 液态玻璃帧
lingyun.exe --backdrop-probe 20  # 真机验证自适应输入：采到的是背景还是岛自己 + 单次耗时 + 决策预览
lingyun.exe --settings-smoke 8 glass   # 起设置窗口停留 8 秒（可选材质 acrylic|glass|classic），供真机截图核对
lingyun.exe --spectrum-probe 5   # 音频链路自检：频谱捕获峰值 + SMTC 会话状态 + 音量设备 + 天气定位来源
lingyun.exe --marquee-probe      # 跑马灯运动验证（两次渲染比较标题带重心）
lingyun.exe --toast-probe        # 通知权限 / 高水位 Id / 当前通知（AUMID、标题）
lingyun.exe --wake-probe MSEdge!App   # 单测「点击通知唤醒应用」的三级激活链
lingyun.exe --exit               # 请正在运行的实例退出（岛上弹确认框，不直接退）
lingyun.exe --demo               # 约 6 秒走完 alert 并退出
```

读诊断帧的几个坑：

1. **文件名带 `-forced` 的帧是「诊断强制状态」**：它们显式调了 `ForceQuickPress(...)`
   把「快捷」页摆成长按中的样子，只用来看交互外观和安全保护，**不代表日常外观**。
   日常外观看 `frame-compact-clock.png`（只有时钟，无任何按钮）。
2. 所有帧**共用同一个 `NativeIslandApp` 实例**，前序帧注入的状态会残留——
   `compact-toast` 注入的通知会盖住后面所有紧凑帧（`DrawCompact` 见 `ToastActive` 直接 return）。
   新帧的 setup 里要显式 `InjectToast(null)`（`compact-media-bilibili` 就是这么处理的）。
3. 组合模式帧有独立实例（`appComp` / `appCompStatic` / `appCompBig`），其模块内容随
   `compact_scale` 一起缩放（`DrawCompactComposite` 里用 `CompactContentScale`）；
   改缩放逻辑时务必确认岛体右侧没有留空白——像素探针看「岛右缘 − 内容最右」应为 0。
4. `-glass` 帧属于独立实例（`appGlass` / `appGlass40` / `appGlassB` / `appGlassC`），主题为
   `liquid-glass`。它们是**给人眼看的观感图**，底被刷成中灰；真正判「岛外透明、40% 比 100% 更透」
   的是 `--self-test` 里的透明底像素断言（`Clear(SKColors.Transparent)` 后读 alpha）。

## 真机验证：`tools/hover_probe.py`

离屏帧证明不了运行时真实交互，另有一个真机探针：小步把指针移进岛体（单次
`SetCursorPos` 跳转不会投递 `WM_MOUSEMOVE`；分层窗只对不透明像素投递鼠标消息），
截前后两张图比像素差。快捷球移除后**悬停差异应恒为 0**；出现显著差异说明 compact
混入了悬停交互，需要回查。`--wait` 用于等启动时的系统通知收起，避免污染结论。

# 第三方组件与许可

本文件列出「灵云 LingYun」使用或借鉴的第三方组件、数据源及其许可条款。
分发本产品（含二进制）时，请一并保留 `LICENSE`、`NOTICE` 与本文件。

## 直接依赖（NuGet）

| 组件 | 版本 | 许可 | 用途 |
|---|---|---|---|
| SkiaSharp.Views.WPF | 2.88.9 | MIT | 2D 自绘（SkiaSharp 为其核心库） |
| H.NotifyIcon.Wpf | 2.1.3 | MIT | 系统托盘图标与右键菜单 |
| NAudio.Core / NAudio.Wasapi | 2.2.1 | MIT | WASAPI 环回捕获，供实时音频频谱 |
| Microsoft.Windows.SDK.NET.Ref（随 `net8.0-windows10.0.19041.0` 引入） | — | MIT | WinRT 投影，用于 SMTC 媒体会话（`Windows.Media.Control`） |

## 衍生代码（Apache-2.0 要求标注）

本项目包含从 [NotchPeninsula](https://github.com/GEORGEWWWU/NotchPeninsula)（Apache License 2.0）
派生的源代码，按 Apache-2.0 §4(b)(c) 在文件头标注来源与修改，并在此登记：

| 文件 | 原文件 | 修改说明 |
|---|---|---|
| `cs/LingYun/Services/AudioSpectrumService.cs` | `AudioAnalyzer.cs` | 增加 `IDisposable` 生命周期（原版进程级不释放）；捕获失败时以 `Available` 静默降级；DSP 块计算抽成可测的 `FinishBlock` 纯函数；移除对上游 `Logger` 的依赖 |
| `cs/LingYun/Services/AudioVolumeService.cs` | `Audio.cs`（`NaudioAdapter`） | 在调用线程上惰性打开设备（`AudioEndpointVolume` 非敏捷 COM 对象，跨线程使用会抛）；设备缺失静默降级 `Available=false`；调大音量自动解除静音；步进/百分比抽成可测纯函数 |
| `cs/LingYun/Services/AppActivatorService.cs` | `appactivator.cs` | 把上游三条独立路径串成一条回退链（WinRT `PackageManager` 包激活 → COM `IApplicationActivationManager` → 按 exe 名前台化 → 按进程名/窗口标题模糊匹配）；返回「走了哪一级」的说明供诊断打印；移除对上游 `Logger` 与静态设置的依赖 |

## 思路 / 代码借鉴（非直接依赖）

| 项目 | 许可 | 借鉴内容 |
|---|---|---|
| [NotchPeninsula](https://github.com/GEORGEWWWU/NotchPeninsula) | **Apache-2.0** | 分层窗（`UpdateLayeredWindow` + DIB）组合方式、Win32 封装结构、文本按 emoji/非 emoji 分段的做法、`UserNotificationListener` 用法、频谱 DSP 与音量控制（见上表登记）、歌词时间轴（LRC 解析 + 按位置取当前句）的思路、**组合模式（多模块同屏 + 自动长度 + 定宽槽防抖）与卡拉OK逐字/延迟补偿的思路**、浏览器标题尾巴表 |
| nimbus | MIT | 弹簧动画参数（仅思路） |
| eIsland | GPL-3.0 | **仅 UI/UX 与信息架构参考，未复制任何代码** |
| rajsriv/dynamic-island-for-windows | **无 LICENSE** | 仅借鉴「固定尺寸窗口 + 在画布内绘制形变」的思路 |

> 注意：`eIsland` 为 GPL-3.0。本项目**未**使用其任何源代码，仅参考交互与页面划分；
> 若未来要移植其实质代码，需重新评估许可兼容性。
> `rajsriv/dynamic-island-for-windows` 未声明许可，因此**不得**复制其代码，只能借鉴思路。

## 数据源

| 服务 | 许可 / 条款 | 用途 |
|---|---|---|
| [Open-Meteo](https://open-meteo.com/) | 数据 CC-BY 4.0（署名要求） | 天气数据 |
| [LRCLIB](https://lrclib.net) | 免费开放接口，无需鉴权；要求带可识别的 User-Agent | 同步歌词（`syncedLyrics`） |
| ip-api.com | 免费版仅限非商业用途 | IP 定位 |
| ipapi.co | 免费额度，受其使用条款约束 | IP 定位（备用） |

`ip-api.com` 免费接口仅允许非商业使用。若本项目用于商业分发，请替换为自建定位服务或购买授权，
否则应移除该请求路径（`Services/Monitors.cs` 中的 `ResolveLocationAsync`）。

## 素材

| 文件 | 来源 | 说明 |
|---|---|---|
| `灵云.ico` | 项目自制 | 应用与托盘图标 |
| `cs/LingYun/Assets/chrome.png`、`msedge.png` | 项目自制 | 媒体封面缺失时的内置图标档位 |
| `cs/LingYun/Assets/bilibili.png`、`potplayer.png` | 取自 NotchPeninsula 仓库 `data/image/`（Apache-2.0） | 哔哩哔哩 / PotPlayer 站标；仅用于标识「当前正在播放的应用」（名义性使用），商标归各自权利人所有 |

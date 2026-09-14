# 灵云

Windows 顶部「灵动岛」小组件：**定时动作**（关机 / 锁屏 / 息屏 / 暂停媒体）+ **系统媒体岛**。

当前实现为 **C# / .NET 8**，岛体走 **原生 Win32 分层窗 + SkiaSharp**，路线对齐开源项目 [NotchPeninsula](https://github.com/GEORGEWWWU/NotchPeninsula)（`UpdateLayeredWindow` 逐像素 alpha，固定外壳、画布内形变，不依赖 WPF 合成窗做岛）。原 Python / PySide6 版已从工作区移除（见 git 历史）。

---

## 功能

### 灵动岛

| 形态 | 行为 |
|------|------|
| **紧凑** | 顶部居中胶囊；无计划时显示 24 小时时钟（大字号、无图标）；有计划显示剩余时间 |
| **媒体** | 系统有媒体（Edge/Chrome B 站、音乐 App 等）时**自动**优先展示（开始播放即切，停止回落时钟）：封面/应用图标、标题（超长自动**跑马灯滚动**）、**实时音频频谱** |
| **展开** | 点击胶囊展开；媒体态含**封面**、播放/暂停、上一首/下一首、进度、音量、歌词 |
| **Alert** | 进入最后 15 分钟自动变倒计时；最后 10 秒蜂鸣；可取消本次 |

- 形变：画布内插值（OutExpo），**原生窗口不随动画 resize**
- 展开面板 6 页：计划 / 性能 / 天气 / **日程** / **日历** / **快捷**（点页签或滚轮切换；
  每页专属强调色 + 图标页签 + 顶部氛围光）
- **页面可直接编辑**：计划页点开关/动作分段/星期方块，点时间卡弹时间编辑窗；
  日历页点日期加入/移出计划（‹ › 翻月）；日程页点勾选圈完成/取消
- 旁挂圆点：媒体与计划同时存在时切换焦点
- **组合模式**（1.5.0 移植，默认关闭）：胶囊里同时显示**时间 + 硬件占用 + 媒体**，
  宽度按内容自动伸缩（`composite` / `composite_clock` / `composite_hardware` / `composite_media`）；
  时间槽按 `88:88` 定宽、硬件百分比按 `100%` 定宽——倒计时和数字跳动时**宽度不抖**
- **通知抢占**：系统通知到达时接管胶囊约 6 秒（含媒体播放中），**点击唤醒对应应用**
  （WinRT 包激活 → COM 激活管理器 → 按进程名前台化，三级回退）
- **紧凑态永远只有时间 / 媒体 / 通知**，没有功能按钮、不响应悬停；所有功能都在展开面板里

#### 「快捷」页（功能按钮唯一入口）

展开面板的「快捷」页放 6 个动作，3 列网格：

| 行 | 动作 | 交互 |
|---|---|---|
| 第 1 行 | 浏览器 / 命令行 / **自定义程序（最多 3 个，点「＋」添加）** | 单击即执行 |
| 最后一行 | 睡眠 / 重启 / 关机 | **必须长按 0.9 秒** |

自定义程序存配置 `quick_custom`（名称 / 路径 / 圆钮颜色），在岛上「＋ 添加程序」弹窗或直接改配置维护；
危险动作永远独占快捷页最后一行，不随自定义数量变位。

安全约定：

- 危险动作（关机 / 重启 / 睡眠）**排在第二行**，带红环预警
- 危险动作**单击一律不执行**：必须按住球约 **0.9 秒**，按钮上的红色进度弧走满一圈，
  **且松手时指针还停在该按钮上**才会触发；中途松手或把指针移开即取消
- 资源管理器 / 设置 / 任务管理器三个动作已移除；早期「悬停右缘扇出快捷球」的交互
  也已整体移除（曾经误触即重启电脑，详见 git 历史）

### 定时计划

- 按**星期**与/或**具体日期**调度（日期写入配置 `dates`）
- 动作：`shutdown` / `lock` / `display_off` / `pause_media`
- 可勾选「到点同时暂停媒体」
- 默认 **enabled=false**，不勾选规则则永不执行

### 媒体（SMTC）

- **三种媒体页样式**（「岛设置 → 外观 → 媒体页」实时切换，写进 `media_style`）：
  `a` **精修**（默认：大封面、白圆主控钮、主色渐变进度条）、
  `b` **沉浸**（封面模糊铺满整底 + 底部玻璃控制条）、
  `c` **氛围海报**（封面取色双光斑 + 微倾大封面 + 大字号标题）；
  B/C 的模糊与取色都在换曲时重算一次并缓存，逐帧成本≈0
- WinRT `Windows.Media.Control` 会话：标题、状态、进度、封面字节
- 控制：播放/暂停、上一首/下一首、seek
- **点击胶囊直接展开**；展开后点标题/封面：激活源窗口（Edge / Chrome 等）
- 封面优先级：系统真封面 → 内置图标（`Assets/msedge.png` 等）→ 品牌色块；
  **B站 / PotPlayer 例外**：这类会话通常没有真封面，直接优先用内置站标
- **多来源切换**：多个播放器同时出声（Edge 看视频 + 网易云放歌）时，展开面板右上出现
  `‹ 来源 n/m ›`，点一下切控制目标；选定后钉住该来源，它消失后自动回到跟随系统当前会话
- **标题清理**：浏览器会话的「正在播放：xxx - yyy」「_哔哩哔哩_bilibili」「_腾讯视频」等
  噪音会在显示前去掉，艺人能拆出来就补进歌手位
- **实时频谱**：WASAPI 环回捕获系统声音 → 5 频段 Goertzel（底鼓/军鼓/人声/乐器高频/镲片）+ AGC，
  胶囊右侧 5 根柱子跟音乐实时起伏（快起慢落）；无音频设备/独占模式时自动回退为示意动画，
  可用配置 `spectrum: false` 关闭
- **音量**：展开媒体页底部一行音量条——点喇叭切静音、点轨道直接定位；
  紧凑媒体态下**滚轮直接调音量**（±2%，展开态滚轮仍留给翻页）；无播放设备时整行自动隐藏
- **歌词**：展开媒体页显示当前句 + 下一句（在线同步歌词，来源 [LRCLIB](https://lrclib.net)，
  免费无需鉴权）；查不到/离线/接口异常都静默留空；可用配置 `lyrics: false` 关闭
- **卡拉OK逐字**：当前句按播放进度从左到右点亮（`lyrics_karaoke: false` 关闭）；
  `lyric_delay_ms` 可做 ±3 秒延迟补偿（正 = 歌词提前，显示器/音频链路有延迟时校准）

### 外观与主题

- **四种主题**（岛设置 → 外观 → 主题，写进 `theme`）：`dark`（纯黑 `#000000`）/ `light`（暖白面板）/
  `system`（跟随系统，切换后约 2 秒内生效）/ **`liquid-glass` 液态玻璃**
- **液态玻璃**默认以浅色呈现：浅色半透明表面 + 细边框 + 半透明卡片（**不做默认高光**：
  真实玻璃的亮边来自菲涅尔反射，静态贴一条顶部亮线等于假设"光永远从正上方来"），
  文字、强调色与状态色始终不透明，压在浅色或深色壁纸上都保持 AAA 对比度（自测断言）
- **液态玻璃自适应**（`glass_adaptive`，默认开）：每秒抓一次岛周围那一小块桌面算亮度
  （实测 4ms/次 ≈ 单核 0.4%），自动在**浅色玻璃 + 深字**与**深色玻璃 + 白字**之间切换。
  判据两条：先保证文字对比度达标（玻璃越薄，背景越会掺进来），再保证岛和背景**分得开**
  （白底上的白玻璃会糊成一片，这时翻深色）；带迟滞，明暗交界处不会来回闪
- **背景透明度 40–100%**（`opacity`）对四种主题都生效，只压背景类颜色（主体/卡片/轨道/阴影/边框），
  文字不变；设置里另给了 **轻透 40% / 半透 70% / 不透明 100%** 三个一键预设，预设只是驱动同一根滑杆
- 注意：岛体是 `UpdateLayeredWindow` 分层窗口，拿不到桌面像素，所以液态玻璃是**应用内材质**，
  不是 Win11 的桌面级 Acrylic 模糊；自适应是"抓屏 → 算亮度 → 换材质"，不是实时取景折射
  （真折射要每帧抓屏 + 模糊位移，约 33% 单核，没做）
- 抓屏有个坑：实测 DWM 合成下 BitBlt **无论带不带 `CAPTUREBLT`** 都会把本进程的分层窗一起采进来，
  所以采的是岛**周围那一圈**并把岛（含阴影）从统计里剔除——直接采岛自身的矩形等于照镜子
  （`--backdrop-probe` 会把两种采法的结果并排打出来）

### 系统集成

- 单实例互斥 + 命名管道唤出
- 托盘：显示 / 暂停计划 / **岛设置…** / 切换显示器 / 音频频谱 / 显示歌词 / 浅色主题 /
  **消息通知** / 开机自启 / 退出（开关勾选即生效并写回配置）
- **岛设置（主页式，820×580）**：左侧分区导航 + 右侧内容，五个分区 ——
  外观（界面材质 / 岛主题 / 媒体页样式 / 玻璃自适应 / 背景透明度）、位置与大小（含显示器切换）、
  显示内容（组合模式与模块 / 网速 / 通知 / 自动隐藏）、歌词（显示 / 卡拉OK / 延迟补偿）、关于（版本 / 自启 / 诊断入口）；
  滑杆拖动实时预览，关闭时写盘。窗口尺寸固定后不再"一路往下堆"；标题栏整条都可拖动（不是只有文字那一点），
  控件用自定义模板（药丸单选 / 开关 / 扁平按钮），不会出现系统默认模板的蓝色悬停
- **设置窗口界面材质三档**（`ui_material`）：`acrylic` 亚克力（Win11 背景材质 / Win10 合成属性，系统真模糊）、
  `glass` 液态玻璃（亚克力 + 玻璃色调 + 大圆角；WPF 做不了边缘折射，界面上会如实标注"这一档是近似"）、
  `classic` 原生 Windows（纯色 + 方角 + 系统控件）；老系统自动退回纯色，不假装有模糊
- 多显示器：岛按目标屏的**工作区**顶部居中落位；拔屏/改分辨率/任务栏变化自动重新落位
- 系统通知：接收 Windows 通知并在胶囊里显示最新一条（**图标 + 标题 + 正文两行**，
  宽度按文本自适应；6 秒后收起，点击唤醒来源应用；启动时的历史通知不回放）；
  未授权时静默降级，不影响其他功能
- **闲置自动隐藏**（默认关闭）：无媒体且鼠标离开 10 秒后收起岛，光标移到屏幕顶部即恢复；
  媒体开始播放 / 通知到达 / 托盘「显示灵动岛」也会恢复
- 开机自启（HKCU Run）
- `--demo`：约 6 秒后走完 alert 并退出

---

## 运行

```powershell
# 先结束旧进程（任务管理器中的 lingyun.exe / 灵云）
.\lingyun.exe
```

演示：

```powershell
.\lingyun.exe --demo
```

异常时查看同目录 `灵云-crash.log`。

---

## 构建

本机 SDK 示例路径：`C:\dotnet-sdk`（.NET 8）。

```powershell
$env:DOTNET_ROOT = 'C:\dotnet-sdk'
$env:PATH = "C:\dotnet-sdk;$env:PATH"
$env:NUGET_PACKAGES = 'C:\nuget-packages'

cd cs\LingYun
dotnet build -c Release

# 单文件自包含发布
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish

Copy-Item -Force publish\lingyun.exe ..\..\lingyun.exe
```

---

## 目录结构

```
lingyun.exe                  # 发布产物（仓库根）
灵云.ico / 灵云配置.json
cs/LingYun/                 # C# 工程
  App.xaml(.cs)             # WPF 壳：单实例、托盘、启动
  Platform/
    Win32.cs                # 窗口 / 分层 / 消息 API
    LayeredIslandHost.cs    # CreateDIBSection + UpdateLayeredWindow + Skia
    SingleInstance.cs       # Mutex + 命名管道
    TrayService.cs          # H.NotifyIcon
    AutoStart.cs            # HKCU Run
  Ui/
    NativeIslandApp.cs      # 原生岛：形变 / 命中 / 媒体 / alert / 快捷球
    ExpandedPages.cs        # WPF 多页（计划/性能/天气/日程/日历）备用
    QuickFan.cs             # WPF 快捷球（备用）
  Services/
    MediaSessionService.cs  # SMTC
    Scheduler.cs            # 星期/日期 → 下次目标
    PowerActions.cs         # 系统动作
    Monitors.cs             # 性能采样 / 天气
    QuickActions.cs / AppIcons.cs
  Config/AppConfig.cs       # 配置 DTO + 原子写
  Assets/                   # 内置应用图标 PNG
docs/                       # 迁移手册、历史规格
```

> 运行时以 **`NativeIslandApp`（Win32 分层窗）为主界面**。`IslandWindow.xaml` 与 `ExpandedPages` 为 WPF 版实现，仍保留在工程中作对照/后续 UI 扩展。

---

## 配置 `灵云配置.json`

与 exe 同目录；不存在或损坏时回退默认并写回。

| 字段 | 说明 |
|------|------|
| `tool_version` | 固定 `2` |
| `enabled` | 计划总开关，默认 `false` |
| `weekdays` | `1–7`（周一=1） |
| `dates` | `"yyyy-MM-dd"` 数组 |
| `hour` / `minute` | 执行时刻（24 小时制） |
| `theme` | `dark`（纯黑 `#000000`）/ `light`（暖白面板）/ `system`（跟随系统）/ `liquid-glass`（液态玻璃）；托盘「浅色主题」可直接切深浅 |
| `opacity` | 背景不透明度 40–100%（默认 100）；只压背景与材质，文字/强调色保持不透明；液态玻璃同样跟随 |
| `glass_adaptive` | 液态玻璃自适应（默认 `true`）：按岛背后桌面亮度自动切浅色玻璃（深字）/ 深色玻璃（白字）；关掉则固定浅色 |
| `ui_material` | 设置窗口材质：`acrylic`（默认，系统真模糊）/ `glass`（亚克力+玻璃色调近似）/ `classic`（纯色方角） |
| `monitor_index` | 岛显示到哪块显示器，`-1` = 跟随主屏（默认） |
| `spectrum` | 媒体胶囊是否显示真实音频频谱（WASAPI），默认 `true`；`false` 时用示意动画 |
| `perf_network` | 性能页是否显示实时下载 / 上传网速，默认 `true`；关闭后性能页隐藏网速卡 |
| `lyrics` | 展开媒体页是否显示在线歌词（LRCLIB），默认 `true` |
| `media_style` | 展开媒体页样式：`a` 精修（默认）/ `b` 沉浸（封面模糊 + 玻璃控制条）/ `c` 氛围（取色光斑海报） |
| `toast` | 系统通知弹窗，默认 `true`；关掉后岛不再接管胶囊（托盘「消息通知」同源） |
| `composite` | 组合模式总开关（默认 `false`）；开启后胶囊同屏显示多个模块 |
| `composite_clock` / `composite_hardware` / `composite_media` | 组合模式模块：时间 / CPU+内存 / 媒体（默认都开；三个全关时强制保留时间） |
| `lyrics_karaoke` | 歌词卡拉OK逐字，默认 `true`；`false` 时整句一个颜色 |
| `lyric_delay_ms` | 歌词延迟补偿 ±3000ms（正 = 歌词提前，默认 0） |
| `auto_hide` | 闲置自动隐藏，默认 `false`；开启后无媒体且鼠标离开 10 秒收起，光标到屏幕顶部恢复 |
| `compact_scale` / `expanded_scale` | 胶囊 / 展开面板缩放（0.8–1.5 / 0.95–1.25，默认 1.0） |
| `offset_x` / `offset_y` | 岛相对工作区中心的水平偏移（±280）/ 距顶部（0–200，默认 8）；托盘「岛设置」可调 |
| `action` | `shutdown` \| `lock` \| `display_off` \| `pause_media` |
| `also_pause_media` | 到点在主动作之外再暂停媒体 |
| `location` / `lat` / `lon` | 天气 |
| `tasks` | 日程列表（岛上「日程」页可直接增删改/勾选完成；`{name, category, color, time, done}`） |
| `quick_customs` | 快捷页自定义程序（最多 3 个；`[{name, path, color}]`，单击启动；旧的 `quick_custom` 键已不再使用） |

示例：

```json
{
  "tool_version": 2,
  "enabled": true,
  "weekdays": [1, 3, 5],
  "dates": [],
  "hour": 23,
  "minute": 0,
  "theme": "system",
  "opacity": 100,
  "monitor_index": -1,
  "spectrum": true,
  "perf_network": true,
  "lyrics": true,
  "lyrics_karaoke": true,
  "lyric_delay_ms": 0,
  "toast": true,
  "composite": false,
  "composite_clock": true,
  "composite_hardware": true,
  "composite_media": true,
  "auto_hide": false,
  "action": "shutdown",
  "also_pause_media": true
}
```

---

## 故障排查

| 现象 | 处理 |
|------|------|
| 双击无反应 / 秒退 | 任务管理器结束所有 `灵云` / `灵云-cs` 后再启动（单实例） |
| 无窗口但进程在 | 查 `灵云-crash.log`；确认无旧进程占用；用最新 `lingyun.exe` |
| 媒体无标题/进度 | 浏览器需有活动媒体会话；部分网页不上报 SMTC |
| 抖音不放出媒体岛 | 有意为之：抖音的 SMTC 会话不响应暂停/进度命令，展示出来会是摆设，已整表忽略 |
| 提醒未触发 | 检查 `enabled` 与 `weekdays`/`dates`；系统时间是否正确 |
| 找不到关机/重启按钮 | 点胶囊展开 →「快捷」页（第 6 个页签）；危险动作要长按 0.9 秒 |
| 关机/重启点一下没反应 | 这是有意设计：按住按钮约 0.9 秒、进度弧走满才执行，防误触 |
| 托盘「退出」点了没反应 | 现在改为**在岛上弹确认框**（不再用系统对话框——它可能被弹不出来）。看到确认框后点「退出」即可；12 秒不理会自动取消 |
| 想从命令行退出 | `lingyun.exe --exit`（发给正在运行的实例，同样弹岛内确认框） |
| 想知道托盘点击有没有送达 | 看同目录 `灵云-exit-trace.log`：有 `tray exit clicked` 说明点击已收到 |
| 点媒体封面/标题不跳转 | 已走三级激活链（WinRT/COM/进程/窗口标题）；查 `灵云-click-trace.log` 的 `media wake` 行，`how` 字段写明各层结果；抖音等客户端需有可见窗口 |
| 通知不弹 / 点了不唤醒 | `lingyun.exe --toast-probe` 看授权与最新通知；`--wake-probe <AUMID>` 单测唤醒链；`灵云-toast-trace.log` 有每次点击的激活路径 |
| 岛突然不见了 | 检查是否开了「闲置自动隐藏」：鼠标移到屏幕最顶部 4px 即恢复；或托盘「显示灵动岛」 |

---

## 许可与致谢

本仓库自身以 **Apache License 2.0** 授权，全文见 [LICENSE](LICENSE)。

- 分层窗与 Win32 API 组合方式参考 [NotchPeninsula](https://github.com/GEORGEWWWU/NotchPeninsula)（**Apache-2.0**）。署名见 [NOTICE](NOTICE)，分发时请一并保留。
- 全部第三方依赖、数据源与素材的许可清单见 [THIRD-PARTY.md](THIRD-PARTY.md)。
- 本仓库代码为独立实现，功能范围为定时动作 + 媒体岛，与上游产品定位不同。

> 若要将本项目用于**商业**分发，请先处理 `THIRD-PARTY.md` 中标注的限制项
> （尤其是 `ip-api.com` 免费接口仅限非商业用途）。

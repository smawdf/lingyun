---
feature: media-island
status: delivered
updated: 2026-09-09
branch: feature/media-island
commits: 
---

# Media Island（手机式媒体灵动岛）

## Report

**What was built** — 系统媒体会话（PowerShell WinRT SMTC 桥）+ 媒体播放器页（曲名/歌手、播放暂停、上一首下一首、可拖进度、打开源应用）。灵动岛焦点模型：媒体优先展示；与定时计划并存时宽胶囊 + 旁挂圆点切换；alert 仍最高优先。

**Verification** — `py -m pytest` 23 passed；`./build_exe.ps1` 产出 `dist/灵云.exe`。

**Journey log** — winsdk 在本机 Py3.14 无轮子且无 VS，改 PS 桥；worktree add 被环境拦截，在 feature/media-island 分支实现；compact 下内容层需 WA_TransparentForMouseEvents 才能点开展开。

## [S1] Problem

灵云目前只把灵动岛当作定时器胶囊。用户希望像 iPhone：系统里正在播 B 站/音乐时，岛自动展示媒体；可播放暂停、上一首/下一首、看详情、拖进度、跳回源应用；定时倒计时仍像 iOS 旁挂圆点，可切换焦点。

## [S2] Design

### 媒体会话后端

- 优先 `winsdk`（Windows.Media.Control GSTMC）。
- 本机 Python 3.14 无预编译 winsdk 且无 VS 工具链 → **回退：长驻 PowerShell WinRT 桥**（stdin/stdout JSON），不新增二进制依赖。
- 能力：当前会话 title/artist/app_id、播放状态、position/duration、play/pause、next/prev、seek(ms)、可选「打开源应用」（`IUserConsentVerifier` 不可用时用 app 启动 URI / 焦点媒体进程）。
- 轮询：UI 侧 500ms 拉 timeline；会话变更以信号通知岛。

### 岛活动模型（iOS 式）

| 活动 | 优先级 | compact | expanded |
|------|--------|---------|----------|
| alert（定时最后 15 分钟） | 最高 | 强制倒计时横幅 | 取消本次 |
| media（有活动媒体） | 高 | 封面/图标 + 曲名滚动 + 状态 | 完整播放器 |
| timer（有计划且未 alert） | 中 | 剩余时间 | 计划/日历设置 |
| none | — | 未安排 | 计划设置 |

- 同时存在 media + timer：主区显示 media；**右侧旁挂小圆点**显示倒计时/状态，点击切换焦点到 timer。
- 焦点切换：旁挂点击 ↔ 主区；expanded 时顶部可切换「媒体 / 计划」。

### 播放器 UI（expanded media）

- 封面（无则应用图标色块）、标题、歌手/应用名
- 控制条：上一首 · 播放/暂停 · 下一首
- 进度条：可拖 seek；显示 当前/总时长
- 「打开应用」：尝试激活源应用窗口

### 数据流

```
PowerShell SMTC bridge ──JSON──► MediaSession(QObject)
                                      │ changed/update signals
                                      ▼
                              IslandWindow (焦点/形变)
                                      │
                    media_page ◄──────┴──────► timer pages
```

### 错误行为

- 桥进程退出/超时：自动重启一次；仍失败则岛回退纯定时器，计划页提示「媒体桥不可用」。
- 无会话：media 活动消失，焦点回 timer。

## [S3] Out of Scope

- 多会话并列列表（只控制系统当前活动会话）
- 歌词、播放队列编辑
- 音量混音器 per-app
- 将 worktree 隔离（环境阻止 `git worktree add`，改在 `feature/media-island` 分支上实现）

## Tasks

- [ ] T1: MediaSession 后端（PS 桥 + winsdk 可选）— acceptance: 单测可 mock；有媒体时能读 title/position 并 play_pause/seek（covers: S2）
- [ ] T2: MediaPage UI（封面/标题/控制/进度）— acceptance: offscreen 测控件与信号绑定（covers: S2; depends: T1）
- [ ] T3: Island 焦点模型（media/timer/alert + 旁挂圆点）— acceptance: 有媒体时 compact 显示媒体；同时有计划显示旁挂；alert 仍抢焦点（covers: S2; depends: T2）
- [ ] T4: main.Controller 接线 + 配置/打包 — acceptance: pytest 全过；build_exe 产出可运行 exe（covers: S2; depends: T3）

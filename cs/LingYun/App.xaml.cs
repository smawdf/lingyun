using System.IO;
using System.Windows;
using LingYun.Config;
using LingYun.Platform;
using LingYun.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace LingYun;

public partial class App : Application
{
    private SingleInstance? _instance;
    private TrayService? _tray;
    private Ui.NativeIslandApp? _island;
    private MediaSessionService? _media;
    private PerfSampler? _perf;
    private WeatherService? _weather;
    private ToastService? _toast;
    private AudioSpectrumService? _spectrum;
    private AudioVolumeService? _volume;
    private LyricsService? _lyrics;
    private bool _quitting;
    private Config.AppConfig? _cfg;
    private Ui.TaskEditorWindow? _taskEditor;
    private Ui.SettingsWindow? _settingsWindow;
    private Ui.PlanTimeWindow? _planTimeWindow;
    private Ui.CustomAppWindow? _customAppWindow;
    private int _editorsOpen;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 诊断开关必须在单实例互斥锁之前处理：
        // 否则主实例在跑时，诊断会被 TryAcquire 挡掉，静默退出什么都拿不到。
        if (Diagnostics.Diag.ShouldRun(e.Args))
        {
            int code;
            try { code = Diagnostics.Diag.Run(e.Args); }
            catch (Exception ex) { WriteCrashLog(ex); code = 1; }
            // WPF 生成的 Main 会丢掉 Application.Run() 的返回值，所以显式退出才能带出退出码
            Environment.Exit(code);
            return;
        }

        // --exit：把退出请求转给正在运行的实例（它会弹岛内确认框），本进程立刻退出
        if (e.Args.Contains("--exit"))
        {
            SingleInstance.RequestExitExisting();
            Environment.Exit(0);
            return;
        }

        try
        {
            // 必须 await：否则 StartCore 里首个 await 之后抛出的异常会逃出这里，
            // 既崩溃又不写 crash log
            await StartCore(e.Args);
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show("启动失败：\n" + ex.Message, "灵云");
            Shutdown(1);
        }
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "灵云-crash.log"),
                DateTime.Now + "\n" + ex);
        }
        catch { /* ignore */ }
    }

    private async Task StartCore(string[] args)
    {
        _instance = new SingleInstance();
        if (!_instance.TryAcquire())
        {
            SingleInstance.RequestShowExisting();
            Shutdown();
            return;
        }

        var cfg = ConfigStore.Load();
        _cfg = cfg;
        _media = new MediaSessionService();
        // 性能页与天气页是这两个采样器现在唯一的消费者，重新起用
        _perf = new PerfSampler();
        _weather = new WeatherService(cfg.Location, cfg.Lat, cfg.Lon);
        // 系统通知需要用户在「设置 → 通知」里授权；没授权时 ToastService 自己降级为不可用
        _toast = new ToastService();
        // 频谱：WASAPI 捕获失败（无设备/独占模式）时服务自己降级，UI 回退假动画
        _spectrum = new AudioSpectrumService();
        // 音量：只在岛 UI 线程上惰性打开设备（AudioEndpointVolume 不是敏捷 COM 对象）
        _volume = new AudioVolumeService();
        // 歌词：LRCLIB 在线查，失败静默降级（离线/接口变动只是没有歌词）
        _lyrics = new LyricsService();
        _island = new Ui.NativeIslandApp(cfg, _media, _perf, _weather, _toast, _spectrum, _volume, _lyrics);
        _island.ExitRequested += () => Dispatcher.Invoke(QuitApp);
        _island.TaskEditRequested += idx => Dispatcher.BeginInvoke(new Action(() => ShowTaskEditor(idx)));
        _island.SettingsRequested += () => Dispatcher.BeginInvoke(new Action(ShowSettingsWindow));
        _island.PlanTimeEditRequested += () => Dispatcher.BeginInvoke(new Action(ShowPlanTimeWindow));
        _island.QuickCustomEditRequested += () => Dispatcher.BeginInvoke(new Action(ShowCustomAppWindow));
        // 点通知 → 唤醒对应应用；成功后才把这条通知从操作中心划掉（失败留着，用户还能在操作中心点）
        _island.ToastClicked += t => _ = WakeToastAsync(t);

        _instance.ShowRequested += () => _island?.RequestShow();
        _instance.ExitRequested += () => _island?.RequestExitConfirm();
        _instance.StartListener();

        _tray = new TrayService(
            onShow: () => _island?.RequestShow(),
            togglePause: () => _island?.TogglePause() ?? false,
            // 必须从托盘点击处理栈里退出来再退出：在 H.NotifyIcon 的回调里同步走弹窗 + Dispose，
            // 会让退出卡住（弹窗不可见 / Dispose 重入），表现为「托盘点了退出没反应」
            onExit: () =>
            {
                // 不再弹 MessageBox：从托盘菜单的嵌套消息循环里弹模态框、且本应用没有活动窗口时，
                // 对话框弹不出来（表现为「点了退出没反应」）。改成让置顶的岛自己显示确认框。
                TraceExit("tray exit clicked");
                _island?.RequestExitConfirm();
            },
            isPaused: () => _island?.IsPaused ?? false,
            isAutoStart: AutoStart.IsEnabled,
            setAutoStart: AutoStart.Set,
            cycleMonitor: () => _island?.CycleMonitor() ?? 1,
            monitorCount: () => _island?.MonitorCount ?? 1,
            getSpectrum: () => _island?.SpectrumEnabled ?? true,
            setSpectrum: on =>
            {
                if (_island is null) return;
                _island.SpectrumEnabled = on;
                SaveCfg();
            },
            getLyrics: () => _island?.LyricsEnabled ?? true,
            setLyrics: on =>
            {
                if (_island is null) return;
                _island.LyricsEnabled = on;
                SaveCfg();
            },
            getLightTheme: () => _island?.LightTheme ?? false,
            setLightTheme: on =>
            {
                if (_island is null) return;
                _island.LightTheme = on;
                SaveCfg();
            },
            getToast: () => _island?.ToastEnabled ?? true,
            setToast: on =>
            {
                if (_island is null) return;
                _island.ToastEnabled = on;
                SaveCfg();
            },
            openSettings: () => _island?.RequestSettings());

        await _media.StartAsync();
        _weather.Start();
        // 不能 await：OnStartup 阶段 Dispatcher 尚未泵消息，await 的续体排不进来会死锁。
        // ToastService 内部会兜住所有异常并通过 Available/Status 暴露结果，丢给线程池即可。
        _ = Task.Run(() => _toast.StartAsync());
        _island.Start();

        if (args.Contains("--demo"))
            _island.SetDemo(DateTime.Now.AddSeconds(6));
    }

    /// <summary>岛设置窗口：单实例防重叠；关闭时写盘（拖动预览期间不落盘）。</summary>
    private void ShowSettingsWindow()
    {
        if (_cfg is null || _island is null) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new Ui.SettingsWindow(_cfg, _island, SaveCfg);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            EditorClosed();
        };
        EditorOpened();
        _settingsWindow.Show();
    }

    /// <summary>执行时刻编辑窗：单实例防重叠；保存时写盘。</summary>
    private void ShowPlanTimeWindow()
    {
        if (_cfg is null || _island is null) return;
        if (_planTimeWindow is not null)
        {
            _planTimeWindow.Activate();
            return;
        }
        _planTimeWindow = new Ui.PlanTimeWindow(_cfg, _island, SaveCfg);
        _planTimeWindow.Closed += (_, _) =>
        {
            _planTimeWindow = null;
            EditorClosed();
        };
        EditorOpened();
        _planTimeWindow.Show();
    }

    /// <summary>自定义快捷程序编辑窗：单实例防重叠；完成时写盘。</summary>
    private void ShowCustomAppWindow()
    {
        if (_cfg is null || _island is null) return;
        if (_customAppWindow is not null)
        {
            _customAppWindow.Activate();
            return;
        }
        _customAppWindow = new Ui.CustomAppWindow(_cfg, _island, SaveCfg);
        _customAppWindow.Closed += (_, _) =>
        {
            _customAppWindow = null;
            EditorClosed();
        };
        EditorOpened();
        _customAppWindow.Show();
    }

    /// <summary>事项编辑器：单实例防重叠；index=-1 表示新建。</summary>
    private void ShowTaskEditor(int index)
    {
        if (_cfg is null || _island is null) return;
        if (_taskEditor is not null)
        {
            _taskEditor.Activate();
            return;
        }
        Config.TaskItem? original =
            index >= 0 && index < _cfg.Tasks.Count ? _cfg.Tasks[index] : null;
        _taskEditor = new Ui.TaskEditorWindow(_cfg, _island, original);
        _taskEditor.Closed += (_, _) =>
        {
            _taskEditor = null;
            EditorClosed();
        };
        EditorOpened();
        _taskEditor.Show();
    }

    /// <summary>编辑窗计数：第一个打开时岛让出置顶，最后一个关闭时恢复。</summary>
    private void EditorOpened()
    {
        if (++_editorsOpen == 1) _island?.SetTopmostYield(true);
    }

    private void EditorClosed()
    {
        _editorsOpen = Math.Max(0, _editorsOpen - 1);
        if (_editorsOpen == 0) _island?.SetTopmostYield(false);
    }

    /// <summary>托盘开关改配置后写回磁盘（失败不影响本次会话内的生效状态）。</summary>
    private void SaveCfg()
    {
        if (_cfg is null) return;
        try { ConfigStore.Save(_cfg); } catch { /* 磁盘异常不致命 */ }
    }

    /// <summary>临时埋点：退出链路每一步写一行，定位「点了没反应」卡在哪。</summary>
    private static void TraceExit(string step)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "灵云-exit-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {step}" + Environment.NewLine);
        }
        catch { /* ignore */ }
    }

    /// <summary>点击系统通知：唤醒对应应用（三级激活）。通知不划掉——WinRT 没有单条删除 API。</summary>
    private async Task WakeToastAsync(ToastData t)
    {
        if (string.IsNullOrWhiteSpace(t.Aumid))
        {
            TraceToast("click: AUMID 为空，仅收起通知");
            return;
        }
        try
        {
            var (ok, how) = await AppActivatorService.ActivateAsync(t.Aumid);
            TraceToast($"click: aumid={t.Aumid} 成功={ok} 路径={how}");
        }
        catch (Exception ex)
        {
            TraceToast("click: 激活异常 " + ex.GetType().Name);
        }
    }

    /// <summary>通知链路埋点（点击 → 唤醒 → 划掉），文件名与退出/点击/定位埋点同一约定。</summary>
    private static void TraceToast(string step)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "灵云-toast-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {step}" + Environment.NewLine);
        }
        catch { /* ignore */ }
    }

    private void QuitApp()
    {
        TraceExit("QuitApp entered");
        // ExitRequested 与托盘退出可能同时到达，避免重复 Shutdown
        if (_quitting)
        {
            TraceExit("already quitting, ignore");
            return;
        }
        _quitting = true;
        // 任何一步清理失败都要保证最终走到 Shutdown
        try { _island?.Dispose(); } catch { /* ignore */ }
        try { _media?.Dispose(); } catch { /* ignore */ }
        try { _perf?.Dispose(); } catch { /* ignore */ }
        try { _weather?.Stop(); } catch { /* ignore */ }
        try { _toast?.Dispose(); } catch { /* ignore */ }
        try { _spectrum?.Dispose(); } catch { /* ignore */ }
        try { _volume?.Dispose(); } catch { /* ignore */ }
        try { _lyrics?.Dispose(); } catch { /* ignore */ }
        try { _tray?.Dispose(); } catch { /* ignore */ }
        try { _instance?.Dispose(); } catch { /* ignore */ }
        TraceExit("cleanup done, calling Shutdown()");
        Shutdown();
        TraceExit("Shutdown() returned");
    }
}

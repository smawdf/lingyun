using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LingYun.Platform;

/// <summary>
/// 托盘：显示 / 暂停计划 / 岛设置 / 切换显示器 / 音频频谱 / 歌词 / 消息通知 / 开机自启 / 退出。
/// （"浅色主题"已按用户要求移除：深浅在"岛设置 → 外观"里选，托盘里那个是重复入口。）
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly H.NotifyIcon.TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem? _monitorItem;
    private readonly Func<int>? _cycleMonitor;
    private readonly Func<int>? _monitorCount;

    public TrayService(Action onShow, Func<bool> togglePause, Action onExit, Func<bool> isPaused,
        Func<bool> isAutoStart, Action<bool> setAutoStart,
        Func<int>? cycleMonitor = null, Func<int>? monitorCount = null,
        Func<bool>? getSpectrum = null, Action<bool>? setSpectrum = null,
        Func<bool>? getLyrics = null, Action<bool>? setLyrics = null,
        Action? openSettings = null,
        Func<bool>? getToast = null, Action<bool>? setToast = null)
    {
        _cycleMonitor = cycleMonitor;
        _monitorCount = monitorCount;
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "灵云.ico");
            _icon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "灵云 · 定时动作",
                Icon = File.Exists(iconPath)
                    ? new System.Drawing.Icon(iconPath)
                    : System.Drawing.SystemIcons.Application,
            };
        }
        catch
        {
            _icon = new H.NotifyIcon.TaskbarIcon { ToolTipText = "灵云" };
        }
        var menu = new ContextMenu();
        var show = new MenuItem { Header = "显示灵动岛" };
        show.Click += (_, _) => onShow();
        _pauseItem = new MenuItem { Header = isPaused() ? "恢复计划" : "暂停计划" };
        _pauseItem.Click += (_, _) =>
        {
            bool paused = togglePause();
            _pauseItem.Header = paused ? "恢复计划" : "暂停计划";
        };
        var auto = new MenuItem { Header = "开机自启", IsCheckable = true, IsChecked = isAutoStart() };
        auto.Click += (_, _) => setAutoStart(auto.IsChecked);
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => onExit();
        menu.Items.Add(show);
        menu.Items.Add(_pauseItem);
        if (openSettings is not null)
        {
            var settings = new MenuItem { Header = "岛设置…" };
            settings.Click += (_, _) => openSettings();
            menu.Items.Add(settings);
        }
        // 单显示器时这项没有意义，直接不显示
        if (_cycleMonitor is not null && (_monitorCount?.Invoke() ?? 1) > 1)
        {
            _monitorItem = new MenuItem { Header = "切换显示器" };
            _monitorItem.Click += (_, _) =>
            {
                int n = _cycleMonitor();
                if (_monitorItem is not null) _monitorItem.Header = $"切换显示器（当前 第{n}屏）";
            };
            menu.Items.Add(_monitorItem);
        }
        menu.Items.Add(auto);
        // 频谱/歌词开关：勾选即刻生效并写回配置（关掉频谱=回退示意动画；关掉歌词=不再联网查词）
        if (getSpectrum is not null && setSpectrum is not null)
        {
            var spectrum = new MenuItem { Header = "音频频谱", IsCheckable = true, IsChecked = getSpectrum() };
            spectrum.Click += (_, _) => setSpectrum(spectrum.IsChecked);
            menu.Items.Add(spectrum);
        }
        if (getLyrics is not null && setLyrics is not null)
        {
            var lyrics = new MenuItem { Header = "显示歌词", IsCheckable = true, IsChecked = getLyrics() };
            lyrics.Click += (_, _) => setLyrics(lyrics.IsChecked);
            menu.Items.Add(lyrics);
        }
        // 系统通知：勾选即刻生效；关掉时岛上正在显示的这条会立刻撤下
        if (getToast is not null && setToast is not null)
        {
            var toast = new MenuItem { Header = "消息通知", IsCheckable = true, IsChecked = getToast() };
            toast.Click += (_, _) => setToast(toast.IsChecked);
            menu.Items.Add(toast);
        }
        menu.Items.Add(exit);
        _icon.ContextMenu = menu;
        // 关掉库内置的菜单激活：它默认是 RightClick，但实际左键也会弹出菜单。
        // 改为完全自己控制——右键开菜单、左键唤出岛，行为不再受库版本影响。
        _icon.MenuActivation = H.NotifyIcon.Core.PopupActivationMode.None;
        _icon.TrayRightMouseUp += (_, _) =>
        {
            Native.GetCursorPos(out var p);
            _icon.ShowContextMenu(new System.Drawing.Point(p.x, p.y));
        };
        _icon.TrayLeftMouseUp += (_, _) => onShow();
        _icon.TrayMouseDoubleClick += (_, _) => onShow();
        try { _icon.ForceCreate(); } catch { /* ignore tray fail */ }
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}

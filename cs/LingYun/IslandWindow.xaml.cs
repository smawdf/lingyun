using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LingYun.Config;
using LingYun.Services;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using MediaState = LingYun.Services.MediaState;
using Slider = System.Windows.Controls.Slider;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LingYun;

public partial class IslandWindow : Window
{
    private const double ShellW = 620;
    private const double ShellH = 560;
    private const double CompactW = 240;
    private const double CompactH = 52;
    private const double CompactMediaW = 300;
    private const double ExpandedW = 460;
    private const double ExpandedH = 320;
    private const double AlertW = 480;
    private const double AlertH = 210;
    private const double TopY = 8;
    private const double MaxRadius = 26;
    private const double MorphMs = 360;
    private const int WarnMinutes = 15;

    public AppConfig Cfg { get; }
    public MediaSessionService Media { get; }
    public event Action? ExitRequested;
    public event Action? ConfigChanged;

    private double _islandW = CompactW;
    private double _islandH = CompactH;
    private string _mode = "compact"; // compact | expanded | alert
    private string _focus = "timer";  // timer | media
    private bool _morphing;
    private bool _paused;
    private DateTime? _skipTarget;
    private DateTime? _demoTarget;
    private DateTime? _target;
    private long _lastBeepSec = -1;

    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _uiTimer;
    private readonly PerfSampler _perf;
    private readonly WeatherService _weather;
    private Ui.ExpandedPages? _pages;

    // Skia paints
    private string _compactLine = "";
    private string _clockText = "";
    private byte[]? _coverBytes;
    private bool _playing;
    private float _sheenPhase = -1f;
    private float _chargePhase;

    public IslandWindow(AppConfig cfg, MediaSessionService media,
        PerfSampler perf, WeatherService weather)
    {
        Cfg = cfg;
        Media = media;
        _perf = perf;
        _weather = weather;
        InitializeComponent();
        Media.Updated += OnMediaUpdated;

        MouseLeftButtonUp += OnShellClick;
        MouseWheel += OnMouseWheel;

        _clockText = DateTime.Now.ToString("HH:mm");
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _tickTimer.Tick += (_, _) => Tick();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += (_, _) =>
        {
            if (_mode == "expanded") _sheenPhase = (_sheenPhase + 0.012f) % 1.2f;
            if (_chargePhase > 0) _chargePhase = Math.Max(0, _chargePhase - 0.02f);
            Surface.InvalidateVisual();
        };

        BuildOverlay();
        _pages = new Ui.ExpandedPages(Cfg, () => ConfigStore.Save(Cfg), _perf, _weather);
        _overlayRoot.Children.Remove(_planPanel);
        _overlayRoot.Children.Insert(0, _pages);

        // 紧凑态快捷球扇出
        _quickFan = new Ui.QuickFan();
        RootGrid.Children.Add(_quickFan);

        // 媒体/定时旁挂圆点
        _earBtn = new Button
        {
            Width = 36,
            Height = 36,
            Background = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            Visibility = Visibility.Collapsed,
        };
        _earBtn.Click += (_, _) =>
        {
            _focus = _focus == "media" ? "timer" : "media";
            if (_mode == "compact") SetMode("compact", animate: false);
            SyncOverlay();
            Surface.InvalidateVisual();
        };
        RootGrid.Children.Add(_earBtn);

        // Caps/Num 提示条
        _lockStrip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 10, 4),
            Visibility = Visibility.Collapsed,
        };
        _lockText = new TextBlock { Foreground = Brushes.White, FontSize = 11 };
        _lockStrip.Child = _lockText;
        RootGrid.Children.Add(_lockStrip);
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _lockTimer.Tick += (_, _) => PollLockKeys();
        _lockTimer.Start();

        SyncOverlay();
    }

    private Ui.QuickFan _quickFan = null!;
    private Button _earBtn = null!;
    private Border _lockStrip = null!;
    private TextBlock _lockText = null!;
    private readonly DispatcherTimer _lockTimer = null!;
    private bool _capsWas, _numWas;

    private void PollLockKeys()
    {
        bool caps = IsKeyToggled(0x14);
        bool num = IsKeyToggled(0x90);
        if (caps != _capsWas || num != _numWas)
        {
            _capsWas = caps;
            _numWas = num;
            _lockText.Text = $"Caps {(caps ? "开" : "关")} · Num {(num ? "开" : "关")}";
            _lockStrip.Visibility = Visibility.Visible;
            var (x, y, w, _) = IslandRect();
            Canvas.SetLeft(_lockStrip, x + w / 2 - 50);
            Canvas.SetTop(_lockStrip, y + _islandH + 6);
            _lockHideAt = DateTime.UtcNow.AddSeconds(2.5);
        }
        if (_lockHideAt is not null && DateTime.UtcNow > _lockHideAt)
        {
            _lockStrip.Visibility = Visibility.Collapsed;
            _lockHideAt = null;
        }
    }

    private DateTime? _lockHideAt;

    [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
    private static bool IsKeyToggled(int vk) => (GetKeyState(vk) & 1) != 0;

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode != "expanded" || _focus == "media" || _pages is null) return;
        var order = Ui.ExpandedPages.Order;
        int i = Array.IndexOf(order, _pages.ActivePage);
        if (i < 0) i = 0;
        i = e.Delta < 0 ? (i + 1) % order.Length : (i - 1 + order.Length) % order.Length;
        _pages.Show(order[i]);
    }

    public void Start()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ShellW) / 2;
        Top = work.Top + TopY;
        Width = ShellW;
        Height = ShellH;
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        Show();
        Activate();
        _tickTimer.Start();
        _uiTimer.Start();
        Tick();
    }

    public void SetDemoTarget(DateTime when) => _demoTarget = when;
    public bool IsPaused => _paused;

    public bool TogglePause()
    {
        _paused = !_paused;
        return _paused;
    }

    public void SkipCurrent()
    {
        if (_target is not null) _skipTarget = _target;
        SetMode("compact");
    }

    public void TriggerCharging() => _chargePhase = 1f;

    // ----- overlay WPF UI -----

    private Grid _overlayRoot = null!;
    private StackPanel _planPanel = null!;
    private StackPanel _mediaPanel = null!;
    private StackPanel _alertPanel = null!;
    private TextBlock _alertDigits = null!;
    private TextBlock _alertMsg = null!;
    private TextBlock _mediaTitle = null!;
    private Slider _mediaSeek = null!;
    private TextBlock _mediaPos = null!;
    private TextBlock _mediaDur = null!;
    private CheckBox _enabledCb = null!;
    private ComboBox _actionCombo = null!;
    private CheckBox _alsoMediaCb = null!;
    private List<CheckBox> _weekdayCbs = new();

    private void BuildOverlay()
    {
        _overlayRoot = new Grid { Visibility = Visibility.Collapsed };

        // 计划
        _planPanel = new StackPanel { Margin = new Thickness(28, 36, 28, 20) };
        _planPanel.Children.Add(new TextBlock
        {
            Text = "计划",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _enabledCb = new CheckBox
        {
            Content = "启用自动执行",
            Foreground = Brushes.White,
            IsChecked = Cfg.Enabled,
        };
        _enabledCb.Checked += (_, _) => OnPlanEdit(c => c.Enabled = true);
        _enabledCb.Unchecked += (_, _) => OnPlanEdit(c => c.Enabled = false);
        _planPanel.Children.Add(_enabledCb);

        var actRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actRow.Children.Add(new TextBlock { Text = "动作", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        _actionCombo = new ComboBox { Width = 140, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var a in PowerActions.All.Values)
            _actionCombo.Items.Add(new ComboBoxItem { Content = $"{a.Icon} {a.Label}", Tag = a.Key });
        _actionCombo.SelectedIndex = Math.Max(0, PowerActions.All.Keys.ToList().IndexOf(Cfg.Action));
        _actionCombo.SelectionChanged += (_, _) =>
        {
            if (_actionCombo.SelectedItem is ComboBoxItem { Tag: string key })
                OnPlanEdit(c => c.Action = key);
        };
        actRow.Children.Add(_actionCombo);
        _planPanel.Children.Add(actRow);

        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var hour = new Spinner(Cfg.Hour, 0, 23);
        var minute = new Spinner(Cfg.Minute, 0, 59);
        hour.Changed += v => OnPlanEdit(c => c.Hour = v);
        minute.Changed += v => OnPlanEdit(c => c.Minute = v);
        timeRow.Children.Add(new TextBlock { Text = "执行时间", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        timeRow.Children.Add(hour);
        timeRow.Children.Add(new TextBlock { Text = ":", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
        timeRow.Children.Add(minute);
        _planPanel.Children.Add(timeRow);

        _planPanel.Children.Add(new TextBlock
        {
            Text = "执行星期",
            Foreground = Brushes.White,
            Margin = new Thickness(0, 10, 0, 4),
        });
        var weekRow = new StackPanel { Orientation = Orientation.Horizontal };
        string[] names = { "一", "二", "三", "四", "五", "六", "日" };
        for (int i = 1; i <= 7; i++)
        {
            int day = i;
            var cb = new CheckBox
            {
                Content = names[i - 1],
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 8, 0),
                IsChecked = Cfg.Weekdays.Contains(day),
            };
            cb.Checked += (_, _) => OnPlanEdit(c =>
            {
                if (!c.Weekdays.Contains(day)) c.Weekdays.Add(day);
                c.Weekdays.Sort();
                c.Enabled = true;
                _enabledCb.IsChecked = true;
            });
            cb.Unchecked += (_, _) => OnPlanEdit(c => c.Weekdays.Remove(day));
            _weekdayCbs.Add(cb);
            weekRow.Children.Add(cb);
        }
        _planPanel.Children.Add(weekRow);

        _alsoMediaCb = new CheckBox
        {
            Content = "到点同时暂停媒体",
            Foreground = Brushes.White,
            Margin = new Thickness(0, 10, 0, 0),
            IsChecked = Cfg.AlsoPauseMedia,
        };
        _alsoMediaCb.Checked += (_, _) => OnPlanEdit(c => c.AlsoPauseMedia = true);
        _alsoMediaCb.Unchecked += (_, _) => OnPlanEdit(c => c.AlsoPauseMedia = false);
        _planPanel.Children.Add(_alsoMediaCb);

        // 媒体
        _mediaPanel = new StackPanel { Margin = new Thickness(28, 36, 28, 20) };
        _mediaTitle = new TextBlock
        {
            Text = "未在播放",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        _mediaPanel.Children.Add(_mediaTitle);
        _mediaSeek = new Slider
        {
            Minimum = 0,
            Maximum = 1000,
            Margin = new Thickness(0, 16, 0, 4),
            Height = 20,
        };
        _mediaSeek.PreviewMouseLeftButtonDown += (_, _) => Media.BeginSeek();
        _mediaSeek.PreviewMouseLeftButtonUp += async (_, _) =>
        {
            var st = Media.State;
            if (st.DurationMs > 0)
                await Media.SeekAsync((long)(_mediaSeek.Value / 1000 * st.DurationMs));
        };
        _mediaPanel.Children.Add(_mediaSeek);
        var timeRowM = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        _mediaPos = new TextBlock { Text = "00:00", Foreground = Brushes.Gray, FontSize = 11 };
        _mediaDur = new TextBlock { Text = "00:00", Foreground = Brushes.Gray, FontSize = 11 };
        DockPanel.SetDock(_mediaDur, Dock.Right);
        timeRowM.Children.Add(_mediaDur);
        timeRowM.Children.Add(_mediaPos);
        _mediaPanel.Children.Add(timeRowM);
        var ctrl = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var prev = MakeBtn("⏮", async () => await Media.PrevAsync());
        var play = MakeBtn("⏸", async () => await Media.PlayPauseAsync());
        play.FontSize = 18;
        var next = MakeBtn("⏭", async () => await Media.NextAsync());
        ctrl.Children.Add(prev);
        ctrl.Children.Add(play);
        ctrl.Children.Add(next);
        _mediaPanel.Children.Add(ctrl);
        _mediaPanel.MouseLeftButtonUp += (_, _) =>
        {
            if (Media.State.Active)
                LingYun.Platform.WindowFocus.FocusApp(Media.State.AppId);
        };

        // alert
        _alertPanel = new StackPanel
        {
            Margin = new Thickness(20, 28, 20, 20),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _alertDigits = new TextBlock
        {
            Text = "00:00",
            FontSize = 40,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _alertMsg = new TextBlock
        {
            Text = "",
            Foreground = Brushes.Orange,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 8, 0, 12),
        };
        var cancel = new Button
        {
            Content = "取消本次执行",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(16, 6, 16, 6),
            Background = new SolidColorBrush(Color.FromRgb(0x60, 0xcd, 0xff)),
            Foreground = Brushes.Black,
            BorderThickness = new Thickness(0),
        };
        cancel.Click += (_, _) => SkipCurrent();
        _alertPanel.Children.Add(_alertDigits);
        _alertPanel.Children.Add(_alertMsg);
        _alertPanel.Children.Add(cancel);

        _overlayRoot.Children.Add(_planPanel);
        _overlayRoot.Children.Add(_mediaPanel);
        _overlayRoot.Children.Add(_alertPanel);
        RootGrid.Children.Add(_overlayRoot);
    }

    private static Button MakeBtn(string text, Func<Task> onClick)
    {
        var b = new Button
        {
            Content = text,
            Width = 44,
            Height = 44,
            Margin = new Thickness(8, 0, 8, 0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 16,
        };
        b.Click += async (_, _) => await onClick();
        return b;
    }

    private sealed class Spinner : StackPanel
    {
        public event Action<int>? Changed;
        private int _value;
        private readonly TextBlock _label;

        public Spinner(int value, int min, int max)
        {
            Orientation = Orientation.Horizontal;
            _value = Math.Clamp(value, min, max);
            var down = new Button { Content = "−", Width = 28, Height = 28, Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            _label = new TextBlock
            {
                Text = _value.ToString("00"),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
            };
            var up = new Button { Content = "+", Width = 28, Height = 28, Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            down.Click += (_, _) => { _value = _value <= min ? max : _value - 1; _label.Text = _value.ToString("00"); Changed?.Invoke(_value); };
            up.Click += (_, _) => { _value = _value >= max ? min : _value + 1; _label.Text = _value.ToString("00"); Changed?.Invoke(_value); };
            Children.Add(down);
            Children.Add(_label);
            Children.Add(up);
        }
    }

    private void OnPlanEdit(Action<AppConfig> edit)
    {
        edit(Cfg);
        ConfigStore.Save(Cfg);
        ConfigChanged?.Invoke();
    }

    private void OnMediaUpdated(MediaState st)
    {
        Dispatcher.Invoke(() =>
        {
            _playing = st.IsPlaying;
            _coverBytes = st.Thumb;
            _mediaTitle.Text = st.Active
                ? (string.IsNullOrWhiteSpace(st.Title) ? "未知" : st.Title)
                : "未在播放";
            if (!_mediaSeek.IsMouseCaptureWithin && st.DurationMs > 0)
            {
                _mediaSeek.Value = Math.Min(1000, st.PositionMs * 1000.0 / st.DurationMs);
                _mediaPos.Text = FmtMs(st.PositionMs);
                _mediaDur.Text = FmtMs(st.DurationMs);
            }
            if (st.Active && _focus == "timer" && _mode != "alert")
            {
                _focus = "media";
                if (_mode == "compact") SetMode("compact", animate: false);
            }
            if (!st.Active && _focus == "media")
            {
                _focus = "timer";
                if (_mode == "compact") SetMode("compact", animate: false);
            }
            Surface.InvalidateVisual();
        });
    }

    private static string FmtMs(long ms)
    {
        var s = Math.Max(0, ms / 1000);
        return $"{s / 60:00}:{s % 60:00}";
    }

    // ----- mode / geometry -----

    private (double x, double y, double w, double h) IslandRect()
        => ((ShellW - _islandW) / 2, 0, _islandW, _islandH);

    private bool MediaActive => Media.State.Active;
    private bool TimerActive => Scheduler.NextTarget(Cfg, DateTime.Now) is not null;

    private void SetMode(string mode, bool animate = true)
    {
        if (_mode == mode && animate) { SyncOverlay(); return; }
        _mode = mode;
        double tw, th;
        switch (mode)
        {
            case "expanded":
                tw = ExpandedW; th = ExpandedH;
                break;
            case "alert":
                tw = AlertW; th = AlertH;
                break;
            default:
                tw = _focus == "media" && MediaActive ? CompactMediaW : CompactW;
                th = CompactH;
                break;
        }

        if (!animate)
        {
            _islandW = tw;
            _islandH = th;
            IslandW = tw;
            IslandH = th;
            SyncOverlay();
            Surface.InvalidateVisual();
            return;
        }

        _morphing = true;
        SyncOverlay();
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var story = new Storyboard();
        var aw = new DoubleAnimation(_islandW, tw, TimeSpan.FromMilliseconds(MorphMs)) { EasingFunction = ease };
        var ah = new DoubleAnimation(_islandH, th, TimeSpan.FromMilliseconds(MorphMs)) { EasingFunction = ease };
        Storyboard.SetTarget(aw, this);
        Storyboard.SetTargetProperty(aw, new PropertyPath(IslandWProperty));
        Storyboard.SetTarget(ah, this);
        Storyboard.SetTargetProperty(ah, new PropertyPath(IslandHProperty));
        story.Children.Add(aw);
        story.Children.Add(ah);
        story.Completed += (_, _) =>
        {
            _morphing = false;
            _islandW = tw;
            _islandH = th;
            IslandW = tw;
            IslandH = th;
            SyncOverlay();
            Surface.InvalidateVisual();
        };
        story.Begin();
    }

    private void SyncOverlay()
    {
        var (x, y, w, h) = IslandRect();
        Canvas.SetLeft(_overlayRoot, x);
        Canvas.SetTop(_overlayRoot, y);
        _overlayRoot.Width = w;
        _overlayRoot.Height = h;
        _overlayRoot.Visibility = _mode == "compact" ? Visibility.Collapsed : Visibility.Visible;
        if (_pages is not null)
            _pages.Visibility = _mode == "expanded" && _focus != "media" ? Visibility.Visible : Visibility.Collapsed;
        _mediaPanel.Visibility = _mode == "expanded" && _focus == "media" ? Visibility.Visible : Visibility.Collapsed;
        _alertPanel.Visibility = _mode == "alert" ? Visibility.Visible : Visibility.Collapsed;
        _quickFan.Visibility = _mode == "compact" ? Visibility.Visible : Visibility.Collapsed;
        bool both = _mode == "compact" && MediaActive && TimerActive;
        _earBtn.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        if (both)
        {
            var r = IslandRect();
            _earBtn.Content = _focus == "media" ? "⏱" : "♪";
            Canvas.SetLeft(_earBtn, r.x + r.w - 78);
            Canvas.SetTop(_earBtn, r.y + 8);
        }
        if (_mode == "compact")
        {
            var r = IslandRect();
            double qx = both ? r.x + r.w - 120 : r.x + r.w - 50;
            _quickFan.Place(qx, r.y - 12);
        }
        CollapseBtn.Visibility = _mode == "expanded" ? Visibility.Visible : Visibility.Collapsed;
        if (_mode == "expanded")
        {
            Canvas.SetLeft(CollapseBtn, x + w - 36);
            Canvas.SetTop(CollapseBtn, y + 8);
        }
    }

    private void OnShellClick(object sender, MouseButtonEventArgs e)
    {
        if (_morphing) return;
        var pos = e.GetPosition(Surface);
        var (x, y, w, h) = IslandRect();
        if (pos.X < x || pos.X > x + w || pos.Y < y || pos.Y > y + h) return;
        if (_mode == "compact")
        {
            SetMode("expanded");
            Focus();
        }
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e) => SetMode("compact");

    // ----- tick -----

    private void Tick()
    {
        var now = DateTime.Now;
        _clockText = now.ToString("HH:mm");
        if (_paused)
        {
            _compactLine = "已暂停";
            Surface.InvalidateVisual();
            return;
        }

        var target = _demoTarget ?? Scheduler.NextTarget(Cfg, now);
        // 「取消本次」保留到该目标时间过去之后才解除，否则下一帧就会被当作新目标重新武装
        if (_skipTarget is not null && now > _skipTarget.Value) _skipTarget = null;

        if (target is null)
        {
            _target = null;
            if (_mode == "alert") SetMode("compact");
            _compactLine = _focus == "media" && MediaActive ? Media.State.Title : "";
            Surface.InvalidateVisual();
            return;
        }

        _target = target;
        var left = (target.Value - now).TotalSeconds;
        var warnAt = target.Value.AddMinutes(-WarnMinutes);
        bool alertOn = now >= warnAt && _skipTarget is null;

        if (_mode == "alert")
        {
            if (left <= 0 && _skipTarget is null)
            {
                Finish(target.Value);
                return;
            }
            if (!alertOn) SetMode("compact");
        }
        else if (alertOn)
        {
            SetMode("alert");
        }

        if (_mode == "alert")
        {
            _alertDigits.Text = $"{(int)Math.Max(0, left) / 60:00}:{(int)Math.Max(0, left) % 60:00}";
            var action = PowerActions.Get(Cfg.Action);
            if (left <= 300)
            {
                _alertMsg.Text = $"即将{action.Label}\n请立即保存所有工作";
                _alertDigits.Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0x99, 0xa4));
                long sec = (long)left;
                if (left <= 10 && sec != _lastBeepSec)
                {
                    _lastBeepSec = sec;
                    Console.Beep(880, 80);
                }
            }
            else
            {
                _alertMsg.Text = $"还有 {left / 60:0} 分钟{action.Label}\n请保存文件、退出游戏";
                _alertDigits.Foreground = Brushes.White;
            }
        }
        else if (_focus == "timer")
        {
            _compactLine = FormatRemaining(left);
        }
        Surface.InvalidateVisual();
    }

    private static string FormatRemaining(double seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds >= 3600)
            return $"{(int)seconds / 3600}时{(int)seconds % 3600 / 60:00}分";
        return $"{(int)seconds / 60:00}:{(int)seconds % 60:00}";
    }

    private void Finish(DateTime target)
    {
        if (_demoTarget is null)
        {
            if (Cfg.AlsoPauseMedia) PowerActions.PauseMedia();
            PowerActions.Execute(Cfg.Action);
        }
        _tickTimer.Stop();
        _uiTimer.Stop();
        Dispatcher.InvokeAsync(() =>
        {
            ExitRequested?.Invoke();
            Close();
        }, DispatcherPriority.Background);
    }

    // ----- Skia paint -----

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        float scale = (float)(e.Info.Width / ShellW);
        var (x, y, w, h) = IslandRect();
        var rect = new SKRect((float)x * scale, (float)y * scale,
            (float)(x + w) * scale, (float)(y + h) * scale);
        float radius = (float)Math.Min(_islandH / 2, MaxRadius) * scale;

        using (var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 40),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8 * scale),
        })
            canvas.DrawRoundRect(rect, radius, radius, shadow);

        using (var body = new SKPaint
        {
            Color = new SKColor(0x1f, 0x1f, 0x1f),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        })
            canvas.DrawRoundRect(rect, radius, radius, body);

        if (_chargePhase > 0)
        {
            using var glow = new SKPaint
            {
                Color = new SKColor(0, 240, 255, (byte)(80 * _chargePhase)),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2 * scale,
            };
            var g = rect;
            g.Inflate(6 * scale * _chargePhase, 4 * scale * _chargePhase);
            canvas.DrawRoundRect(g, radius + 4, radius + 4, glow);
        }

        // compact 内容
        if (_mode == "compact")
        {
            bool mediaMode = _focus == "media" && MediaActive;
            float leftPad = 16 * scale;
            if (mediaMode)
            {
                // 封面 / 应用图标
                var art = new SKRect(rect.Left + 10 * scale, rect.Top + 10 * scale,
                    rect.Left + 42 * scale, rect.Bottom - 10 * scale);
                SKBitmap? cover = null;
                if (Media.State.Thumb is { Length: > 64 } bytes)
                {
                    try { cover = SKBitmap.Decode(bytes); } catch { cover = null; }
                }
                if (cover is not null)
                {
                    using var cp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                    canvas.Save();
                    var clip = new SKPath();
                    clip.AddRoundRect(art, 8 * scale, 8 * scale);
                    canvas.ClipPath(clip);
                    canvas.DrawBitmap(cover, art, cp);
                    canvas.Restore();
                    cover.Dispose();
                }
                else
                {
                    Services.AppIcons.DrawFallback(canvas, art, Media.State.AppId);
                }
                leftPad = 50 * scale;
            }
            else
            {
                string actIcon = PowerActions.Get(Cfg.Action).Icon;
                using var icon = new SKPaint
                {
                    Color = new SKColor(0x60, 0xcd, 0xff),
                    IsAntialias = true,
                    TextSize = 14 * scale,
                    Typeface = AppFonts.Pick(actIcon, SKFontStyleWeight.Medium),
                };
                canvas.DrawText(actIcon, rect.Left + 18 * scale, rect.MidY + 5 * scale, icon);
            }

            using var text = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                TextSize = 16 * scale,
            };
            string label = mediaMode
                ? (Media.State.Title.Length > 18 ? Media.State.Title[..17] + "…" : Media.State.Title)
                : (_compactLine.Length > 0 ? _compactLine : _clockText);
            float tx = rect.Left + (mediaMode ? 50f : 44f) * scale;
            text.Typeface = AppFonts.Pick(label, SKFontStyleWeight.SemiBold);
            canvas.DrawText(label, tx, rect.MidY + 5 * scale, text);

            if (_focus == "media" && _playing)
            {
                // 均衡器
                using var bar = new SKPaint { Color = new SKColor(0x60, 0xcd, 0xff), IsAntialias = true };
                float t = (float)DateTime.UtcNow.TimeOfDay.TotalMilliseconds / 200f;
                for (int i = 0; i < 4; i++)
                {
                    float lv = 0.3f + 0.7f * Math.Abs(MathF.Sin(t + i * 0.9f));
                    float bh = 12 * scale * lv;
                    float bx = rect.Right - 28 * scale + i * 4 * scale;
                    float by = rect.MidY - bh / 2;
                    canvas.DrawRoundRect(new SKRect(bx, by, bx + 2 * scale, by + bh), 1 * scale, 1 * scale, bar);
                }
            }
        }

        // 展开扫光
        if (_mode == "expanded" && _sheenPhase >= 0 && _sheenPhase <= 1)
        {
            float sx = rect.Left + _sheenPhase * rect.Width * 1.2f - rect.Width * 0.1f;
            using var sheen = new SKPaint
            {
                IsAntialias = true,
                BlendMode = SKBlendMode.Screen,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(sx - 70 * scale, rect.MidY),
                    new SKPoint(sx + 70 * scale, rect.MidY),
                    new[] { SKColors.Transparent, new SKColor(255, 255, 255, 40), SKColors.Transparent },
                    null, SKShaderTileMode.Clamp),
            };
            canvas.Save();
            var path = new SKPath();
            path.AddRoundRect(rect, radius, radius);
            canvas.ClipPath(path);
            canvas.DrawRect(rect, sheen);
            canvas.Restore();
        }
    }

    public static readonly DependencyProperty IslandWProperty =
        DependencyProperty.Register(nameof(IslandW), typeof(double), typeof(IslandWindow),
            new PropertyMetadata(CompactW, OnIslandChanged));
    public static readonly DependencyProperty IslandHProperty =
        DependencyProperty.Register(nameof(IslandH), typeof(double), typeof(IslandWindow),
            new PropertyMetadata(CompactH, OnIslandChanged));

    public double IslandW { get => (double)GetValue(IslandWProperty); set => SetValue(IslandWProperty, value); }
    public double IslandH { get => (double)GetValue(IslandHProperty); set => SetValue(IslandHProperty, value); }

    private static void OnIslandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is IslandWindow w)
        {
            w._islandW = w.IslandW;
            w._islandH = w.IslandH;
            w.SyncOverlay();
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

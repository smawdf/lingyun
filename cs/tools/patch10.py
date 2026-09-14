# -*- coding: utf-8 -*-
"""① 显示内容页重组 + 网速模块选项 ② 关于页重写（去掉开发向文字）③ 标题图标用应用图标。"""
import io
import shutil

BASE = 'D:/kaifa/灵云/'
log = []


def patch(path, pairs):
    full = BASE + path
    s = io.open(full, encoding='utf-8').read()
    for old, new, why in pairs:
        hit = old in s
        log.append((path.split('/')[-1], why, hit))
        if hit:
            s = s.replace(old, new, 1)
    io.open(full, 'w', encoding='utf-8', newline='\n').write(s)


# ---- 图标：把仓库根的应用图标拷进工程并登记 ----
shutil.copyfile(BASE + '灵云.ico', BASE + 'cs/LingYun/Assets/lingyun.ico')
patch('cs/LingYun/LingYun.csproj', [
    ('''    <ApplicationManifest>app.manifest</ApplicationManifest>''',
     '''    <ApplicationManifest>app.manifest</ApplicationManifest>
    <ApplicationIcon>Assets\\lingyun.ico</ApplicationIcon>''', 'app icon'),
    ('''    <EmbeddedResource Include="Assets\\**\\*" />''',
     '''    <EmbeddedResource Include="Assets\\**\\*" />
    <!-- 标题栏/侧栏用：WPF 资源（pack URI 直接取） -->
    <Resource Include="Assets\\lingyun.ico" />''', 'resource icon'),
])

patch('cs/LingYun/Ui/SettingsWindow.cs', [
    # ---- ③ 标题图标：换成应用图标（原来是蓝色渐变小方块）----
    ('''        var logo = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(7),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x60, 0xcd, 0xff), Color.FromRgb(0x0a, 0x7a, 0xf0), 45),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(logo);''',
     '''        var logo = new Image
        {
            Width = 22, Height = 22,
            Source = AppIconImage(),          // 用应用自己的图标，不再画一个蓝色小方块
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        header.Children.Add(logo);''', 'logo image'),

    # ---- ① 显示内容页：分组 + 网速模块 ----
    ('''        AddGroupLabel(root, "外观（岛与设置窗口同一套）");''',
     '''        AddGroupLabel(root, "外观（岛与设置窗口同一套）");''', 'noop'),
    ('''        var root = NewPane("content", "显示内容", "紧凑胶囊里放什么、哪些页面出现。");
        var card = NewCard(root);
        AddCheck(card, _composite, "组合模式",
            "胶囊里同屏显示时间 / 硬件 / 媒体，宽度按内容自动伸缩；定宽槽保证数字跳动时不抖",
            v => { _cfg.Composite = v; SyncCompositeEnabled(); _island.ApplyConfig(); });
        AddCheck(card, _compositeClock, "　时间", "显示当前时间（有计划时显示倒计时）",
            v => { _cfg.CompositeClock = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _compositeHardware, "　硬件占用", "CPU 与内存占用（每秒采样一次）",
            v => { _cfg.CompositeHardware = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _compositeMedia, "　媒体", "封面 + 歌词/标题 + 频谱（没有媒体会话时不显示）",
            v => { _cfg.CompositeMedia = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _perfNetwork, "性能页网速", "性能页显示实时下载 / 上传速度（每秒采样一次）",
            v => { _cfg.PerfNetwork = v; _island.ApplyConfig(); });
        AddCheck(card, new CheckBox(), "系统通知弹窗",
            "有通知时接管胶囊约 6 秒，点击唤醒对应应用（需在系统设置里允许通知访问）",
            v => { _cfg.Toast = v; _island.ToastEnabled = v; _save(); });
        AddCheck(card, new CheckBox(), "点空白处收起面板",
            "展开后点面板空白处收起（关掉后只有右上角 ✕ 能收起；页签条不算空白）",
            v => { _cfg.CollapseOnBlank = v; _island.ApplyConfig(); });
        AddCheck(card, new CheckBox(), "闲置自动隐藏",
            "无媒体且鼠标离开 10 秒后收起岛；光标移到屏幕顶部即可恢复",
            v => { _cfg.AutoHide = v; _island.ApplyConfig(); });
        return root;''',
     '''        var root = NewPane("content", "显示内容", "胶囊里放什么、哪些页面出现。");

        AddGroupLabel(root, "组合模式（胶囊同时显示多个模块）");
        var card = NewCard(root);
        AddCheck(card, _composite, "启用组合模式",
            "胶囊里同屏显示下面的模块，宽度按内容自动伸缩；定宽槽保证数字跳动时不抖",
            v => { _cfg.Composite = v; SyncCompositeEnabled(); _island.ApplyConfig(); });
        AddCheck(card, _compositeClock, "　时间", "当前时间（有计划时显示倒计时，下面一行是日期）",
            v => { _cfg.CompositeClock = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _compositeHardware, "　硬件占用", "CPU 与内存占用（每秒采样一次）",
            v => { _cfg.CompositeHardware = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _compositeNetwork, "　网速", "实时下载 ↓ / 上传 ↑ 速度（每秒采样一次）",
            v => { _cfg.CompositeNetwork = v; _island.ApplyConfig(); }, indent: true);
        AddCheck(card, _compositeMedia, "　媒体", "封面 + 歌词/标题 + 频谱（没有媒体会话时不显示）",
            v => { _cfg.CompositeMedia = v; _island.ApplyConfig(); }, indent: true);

        AddGroupLabel(root, "页面与通知");
        card = NewCard(root);
        AddCheck(card, _perfNetwork, "性能页网速",
            "性能页显示实时下载 / 上传速度（与组合模式的网速模块共用同一份采样）",
            v => { _cfg.PerfNetwork = v; _island.ApplyConfig(); });
        AddCheck(card, new CheckBox(), "系统通知弹窗",
            "有通知时接管胶囊约 6 秒，点击唤醒对应应用（需在系统设置里允许通知访问）",
            v => { _cfg.Toast = v; _island.ToastEnabled = v; _save(); });

        AddGroupLabel(root, "操作行为");
        card = NewCard(root);
        AddCheck(card, new CheckBox(), "点空白处收起面板",
            "展开后点面板空白处收起（关掉后只能等自动关闭；页签条空白也算空白）",
            v => { _cfg.CollapseOnBlank = v; _island.ApplyConfig(); });
        AddCheck(card, new CheckBox(), "闲置自动隐藏",
            "无媒体且鼠标离开 10 秒后收起岛；光标移到屏幕顶部即可恢复",
            v => { _cfg.AutoHide = v; _island.ApplyConfig(); });
        return root;''', 'content pane'),

    # ---- ② 关于页重写 ----
    ('''        var root = NewPane("about", "关于", "灵云 —— Windows 顶部灵动岛。");
        var card = NewCard(root);
        AddKeyValue(card, "版本", "v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "?"));
        AddKeyValue(card, "配置文件", "灵云配置.json（与 exe 同目录）");
        AddCheck(card, _autoStart, "开机自启", "写入 HKCU Run，登录后自动运行", v =>
        {
            try { AutoStart.Set(v); } catch { /* 注册表异常不致命 */ }
        });

        AddGroupLabel(root, "诊断");
        card = NewCard(root);
        AddKeyValue(card, "自测", "lingyun.exe --self-test（166 条契约断言）");
        AddKeyValue(card, "出图", "lingyun.exe --dump-frames（写 灵云-diag/*.png）");
        AddKeyValue(card, "自适应探针", "lingyun.exe --backdrop-probe（采的是背景还是岛自己）");
        AddHint(root, "恢复默认会把材质、主题、透明度、大小、位置、组合模式、通知、歌词全部还原（自定义快捷程序与日程保留）。");
        return root;''',
     '''        var root = NewPane("about", "关于", "灵云 —— Windows 顶部的灵动岛：定时动作 + 系统媒体。");

        var card = NewCard(root);
        string ver = "v" + (typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "?");
        AddKeyValue(card, "版本", ver);
        AddKeyValue(card, "配置文件", "灵云配置.json");
        AddButtonRow(card, "配置目录", "打开", () => OpenPath(
            Path.GetDirectoryName(ConfigStore.DefaultPath()) ?? AppContext.BaseDirectory));
        AddCheck(card, _autoStart, "开机自启", "写入 HKCU Run，登录后自动运行", v =>
        {
            try { AutoStart.Set(v); } catch { /* 注册表异常不致命 */ }
        });

        AddGroupLabel(root, "项目");
        card = NewCard(root);
        AddButtonRow(card, "源代码", "GitHub", () => OpenPath("https://github.com/smawdf/lingyun"));
        AddKeyValue(card, "许可", "MIT（本项目）");
        AddKeyValue(card, "衍生代码", "NotchPeninsula · Apache-2.0");
        AddKeyValue(card, "第三方组件", "SkiaSharp / H.NotifyIcon / NAudio / Windows SDK（MIT）");
        AddHint(root, "「恢复默认」会把外观、透明度、大小、位置、组合模式、通知、歌词全部还原；"
                      + "自定义快捷程序与日程会保留。许可与第三方声明详见仓库里的 LICENSE 与 THIRD-PARTY.md。");
        return root;''', 'about pane'),

    # ---- 辅助：应用图标 / 链接按钮 / 打开路径 ----
    ('''    private void AddKeyValue(StackPanel root, string key, string value)''',
     '''    /// <summary>应用图标（csproj 里作为 Resource 打包，pack URI 直接取）。</summary>
    private static System.Windows.Media.ImageSource? AppIconImage()
    {
        try
        {
            return new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/lingyun.ico"));
        }
        catch { return null; }
    }

    /// <summary>用系统默认程序打开路径或网址（按钮点击触发，属于用户主动操作）。</summary>
    private static void OpenPath(string pathOrUrl)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { /* 打不开就算了，不影响设置窗口 */ }
    }

    /// <summary>一行「左侧说明 + 右侧按钮」（配置目录、源代码这类）。</summary>
    private void AddButtonRow(StackPanel root, string key, string buttonText, Action onClick)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lab = new TextBlock
        {
            Text = key, FontSize = 12, Foreground = _fg, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lab, 0);
        grid.Children.Add(lab);
        var btn = NavStyleButton(buttonText, 92);
        btn.Height = 28;
        btn.Click += (_, _) => onClick();
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);
        root.Children.Add(grid);
    }

    private void AddKeyValue(StackPanel root, string key, string value)''', 'helpers'),
])
for f, why, hit in log:
    print(('OK  ' if hit else 'MISS'), f, why)

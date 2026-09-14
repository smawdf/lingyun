# -*- coding: utf-8 -*-
"""设置窗：合并外观（材质+深浅）、✕ 去悬停框、色调去重、玻璃不裁区域、透明度作用于窗口。"""
import io

BASE = 'D:/kaifa/灵云/'
log = []


def patch(path, pairs):
    full = BASE + path
    s = io.open(full, encoding='utf-8').read()
    for old, new, why in pairs:
        hit = old in s
        log.append((why, hit))
        if hit:
            s = s.replace(old, new, 1)
    io.open(full, 'w', encoding='utf-8', newline='\n').write(s)


patch('cs/LingYun/Ui/SettingsWindow.cs', [
    # ---- 外观区：一个「外观」= 材质 + 深浅 ----
    ('''        AddGroupLabel(root, "设置窗口外观（只影响这个窗口）");
        var card = NewCard(root);
        AddRadioRow(card, "外观", new[]
        {
            ("亚克力 · 系统模糊", _matAcrylic), ("液态玻璃 · 同岛材质", _matGlass),
            ("原生 Windows", _matClassic),
        }, "uimaterial");
        _matAcrylic.Checked += (_, _) => SetMaterial("acrylic");
        _matGlass.Checked += (_, _) => SetMaterial("glass");
        _matClassic.Checked += (_, _) => SetMaterial("classic");
        _materialHint.FontSize = 11;
        _materialHint.Foreground = _dim;
        _materialHint.TextWrapping = TextWrapping.Wrap;
        _materialHint.Margin = new Thickness(88, 6, 14, 8);
        root.Children.Add(_materialHint);

        AddGroupLabel(root, "岛（灵动岛本体）的材质与配色 —— 与上面的设置窗口互不影响");
        card = NewCard(root);
        AddRadioRow(card, "主题", new[]
        {
            ("深色", _themeDark), ("浅色", _themeLight),
            ("跟随系统", _themeSystem), ("液态玻璃", _themeLiquidGlass),
        });
        _themeDark.Checked += (_, _) => SetTheme("dark");
        _themeLight.Checked += (_, _) => SetTheme("light");
        _themeSystem.Checked += (_, _) => SetTheme("system");
        _themeLiquidGlass.Checked += (_, _) => SetTheme("liquid-glass");''',
     '''        AddGroupLabel(root, "外观（岛与设置窗口同一套）");
        var card = NewCard(root);
        AddRadioRow(card, "材质", new[] { ("亚克力", _matAcrylic), ("液态玻璃", _matGlass) }, "uimaterial");
        _matAcrylic.Checked += (_, _) => SetAppearance(glass: false);
        _matGlass.Checked += (_, _) => SetAppearance(glass: true);
        AddRadioRow(card, "深浅", new[]
        {
            ("深色", _themeDark), ("浅色", _themeLight), ("跟随系统", _themeSystem),
        });
        _themeDark.Checked += (_, _) => SetBaseTheme("dark");
        _themeLight.Checked += (_, _) => SetBaseTheme("light");
        _themeSystem.Checked += (_, _) => SetBaseTheme("system");
        _materialHint.FontSize = 11;
        _materialHint.Foreground = _dim;
        _materialHint.TextWrapping = TextWrapping.Wrap;
        _materialHint.Margin = new Thickness(88, 6, 14, 8);
        root.Children.Add(_materialHint);
        AddGroupLabel(root, "媒体页");
        card = NewCard(root);''', 'appearance merge'),

    # ---- 液态玻璃自适应 + 媒体页 移到新卡片后面 ----
    ('''        AddRadioRow(card, "媒体页", new[] { ("A · 精修", _styleA), ("B · 沉浸", _styleB), ("C · 氛围", _styleC) },
            "mediastyle");''',
     '''        AddRadioRow(card, "样式", new[] { ("A · 精修", _styleA), ("B · 沉浸", _styleB), ("C · 氛围", _styleC) },
            "mediastyle");''', 'media style row'),

    # ---- 材质/深浅 setter ----
    ('''    private void SetTheme(string theme)
    {
        if (!_ready) return;
        _cfg.Theme = theme;
        _island.ApplyConfig();
        ApplyMaterial();   // 深/浅变了，设置窗口自己的配色也跟着走
    }''',
     '''    /// <summary>
    /// 外观两档之一：亚克力（= 深浅三选一 + 系统模糊）/ 液态玻璃（岛与窗口同款半透明）。
    /// 深浅记在 BaseTheme 里，从玻璃切回来时恢复用户原来的选择。
    /// </summary>
    private void SetAppearance(bool glass)
    {
        if (!_ready) return;
        _cfg.Theme = glass ? "liquid-glass" : _cfg.BaseTheme;
        _island.ApplyConfig();
        ApplyMaterial();
    }

    private void SetBaseTheme(string theme)
    {
        if (!_ready) return;
        _cfg.BaseTheme = theme;
        if (!IslandPalette.IsLiquidGlass(_cfg.Theme)) _cfg.Theme = theme;   // 玻璃档下只记住，不切材质
        _island.ApplyConfig();
        ApplyMaterial();
    }''', 'appearance setters'),

    # ---- 回填 ----
    ('''        _matAcrylic.IsChecked = _cfg.UiMaterial == "acrylic";
        _matGlass.IsChecked = _cfg.UiMaterial == "glass";
        _matClassic.IsChecked = _cfg.UiMaterial == "classic";

        _themeDark.IsChecked = !IslandPalette.ResolveLight(_cfg.Theme, false) && _cfg.Theme != "system";
        _themeLight.IsChecked = string.Equals(_cfg.Theme, "light", StringComparison.OrdinalIgnoreCase);
        _themeSystem.IsChecked = string.Equals(_cfg.Theme, "system", StringComparison.OrdinalIgnoreCase);
        _themeLiquidGlass.IsChecked = IslandPalette.IsLiquidGlass(_cfg.Theme);''',
     '''        _matAcrylic.IsChecked = !IslandPalette.IsLiquidGlass(_cfg.Theme);
        _matGlass.IsChecked = IslandPalette.IsLiquidGlass(_cfg.Theme);
        _themeDark.IsChecked = _cfg.BaseTheme == "dark";
        _themeLight.IsChecked = _cfg.BaseTheme == "light";
        _themeSystem.IsChecked = _cfg.BaseTheme == "system";''', 'backfill'),

    # ---- 字段：去掉 classic/liquidglass 单选 ----
    ('''    private readonly RadioButton _matClassic = new();''', '', 'drop classic field'),
    ('''    private readonly RadioButton _themeLiquidGlass = new();
''', '', 'drop liquidglass radio field'),

    # ---- 恢复默认 ----
    ('''        _matAcrylic.IsChecked = true;
        _themeDark.IsChecked = true;''',
     '''        _matAcrylic.IsChecked = true;
        _themeDark.IsChecked = true;
        _cfg.BaseTheme = "dark";''', 'reset'),

    # ---- 设置窗 ✕：套扁平模板（去掉默认模板的方形悬停框）----
    ('''        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 3);''',
     '''        close.Click += (_, _) => Close();
        _pushButtons.Add(close);   // 不加入就会被默认模板接管——悬停时出现方形边框
        Grid.SetColumn(close, 3);''', 'close flat'),

    # ---- 材质应用：色调去重（亚克力交给 DWM）+ 玻璃不裁区域 + 透明度作用于窗口 ----
    ('''        var tintColor = FromArgb(WindowMaterial.TintArgb(material, _dark));
        _tintOverlay.Background = new SolidColorBrush(tintColor);''',
     '''        // 色调只画一次：亚克力由 DWM 的 accent 上色（我们这层设成全透明，
        // 否则同一层色调被刷两遍——白底上能看出来的"两层"就是这么来的）；
        // 液态玻璃没有系统模糊，色调由我们画，并跟随「背景透明度」滑杆。
        int tintArgb = WindowMaterial.TintArgb(material, _dark);
        if (material == WindowMaterial.Acrylic)
        {
            _tintOverlay.Background = Brushes.Transparent;
        }
        else
        {
            double op = Math.Clamp(_cfg.Opacity, 40, 100) / 100.0;
            var tintColor = FromArgb(tintArgb);
            _tintOverlay.Background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(tintColor.A * op), tintColor.R, tintColor.G, tintColor.B));
        }''', 'tint once'),

    ('''            WindowMaterial.ApplyWindowChrome(this, _dark, material);
            WindowMaterial.ApplyRoundedRegion(this, radius);''',
     '''            WindowMaterial.ApplyWindowChrome(this, _dark, material);
            // 只有亚克力需要裁窗口区域（系统模糊铺满整矩形）；玻璃的圆角由我们自己画，
            // 再裁一层 GDI 区域反而会多出一道锯齿弧（用户看到的"四角弧线"）
            if (WindowMaterial.NeedsRegion(material))
                WindowMaterial.ApplyRoundedRegion(this, radius);''', 'region per material'),

    ('''            WindowMaterial.ApplyRoundedRegion(this, WindowMaterial.Radius(_cfg.UiMaterial));''',
     '''            if (WindowMaterial.NeedsRegion(MaterialFor(_cfg.Theme)))
                WindowMaterial.ApplyRoundedRegion(this, WindowMaterial.Radius(MaterialFor(_cfg.Theme)));''', 'region on resize'),

    # ---- 材质从 theme 推导 + 透明度变化时重画材质 ----
    ('''    private void ApplyMaterial()
    {
        string material = _cfg.UiMaterial;''',
     '''    /// <summary>设置窗口材质由主题推导：液态玻璃 → 同款半透明；其余 → 系统亚克力。</summary>
    internal static string MaterialFor(string theme)
        => IslandPalette.IsLiquidGlass(theme) ? WindowMaterial.Glass : WindowMaterial.Acrylic;

    private void ApplyMaterial()
    {
        string material = MaterialFor(_cfg.Theme);''', 'material derive'),

    # ---- 透明度滑杆：也刷新设置窗口材质 ----
    ('''            _cfg.Opacity = (int)v;
            _opacityLabel.Text = $"  {v:0}%";
            _island.ApplyConfig();''',
     '''            _cfg.Opacity = (int)v;
            _opacityLabel.Text = $"  {v:0}%";
            _island.ApplyConfig();
            ApplyMaterial();   // 设置窗口的玻璃色调也跟着透明度走''', 'opacity affects window'),

    # ---- 提示文案改成合并后的说明 ----
    ('''                "glass" => "系统合成器模糊 + 更薄色调 + 大圆角 + 落影（移动零延迟）。"''',
     '''                "glass" => "与岛同款：半透明、不模糊，四角圆角由我们画。"''', 'hint'),
])
for why, hit in log:
    print(('OK  ' if hit else 'MISS'), why)

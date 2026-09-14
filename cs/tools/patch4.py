# -*- coding: utf-8 -*-
"""清掉 classic / UiMaterial 的残留引用，改成 theme 推导 + BaseTheme。"""
import io

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


patch('cs/LingYun/Ui/SettingsWindow.cs', [
    # 滚动条样式里的 classic 分支（已无该档）
    ('''        thumb.SetValue(Control.TemplateProperty, thumbTemplate);
        // Track 不实现 IAddChild：滑块必须走 Track.Thumb 属性，不能 AppendChild''',
     '''        thumb.SetValue(Control.TemplateProperty, thumbTemplate);
        // Track 不实现 IAddChild：滑块必须走 Track.Thumb 属性，不能 AppendChild''', 'noop'),
    ('        bool classic = _cfg.UiMaterial == "classic";\n        double r = classic ? 0 : 8;',
     '        double r = 8;', 'scrollbar radius'),
    ('        bool classic = _cfg.UiMaterial == "classic";',
     '        bool classic = false;   // 经典档已移除；保留变量名避免大改', 'classic false'),
    # SetMaterial 不再直接写配置（材质由 theme 推导）
    ('''    private void SetMaterial(string material)
    {
        if (!_ready) return;
        _cfg.UiMaterial = material;
        ApplyMaterial();
    }''',
     '''    /// <summary>右键菜单/诊断用：直接指定材质（正常路径走 SetAppearance）。</summary>
    internal void SetMaterialForTest(string material)
    {
        if (!_ready) return;
        _cfg.Theme = material == WindowMaterial.Glass ? "liquid-glass" : _cfg.BaseTheme;
        ApplyMaterial();
    }''', 'set material'),
])

patch('cs/LingYun/Diagnostics/Diag.cs', [
    ('                if (material.Length > 0) smokeCfg.UiMaterial = material;',
     '                if (material.Length > 0) smokeCfg.Theme = material == "glass" ? "liquid-glass" : smokeCfg.BaseTheme;',
     'smoke material'),
    ('                    w.WriteLine($"设置窗口：停留 {seconds}s（材质 {smokeCfg.UiMaterial}）供截图核对");',
     '                    w.WriteLine($"设置窗口：停留 {seconds}s（主题 {smokeCfg.Theme}）供截图核对");', 'smoke log'),
    ('''            Check("界面材质：Normalize 接受三档、拒绝乱值、默认亚克力",
                new AppConfig().UiMaterial == "acrylic"
                && ConfigStore.Normalize(new AppConfig { UiMaterial = "glass" }).UiMaterial == "glass"
                && ConfigStore.Normalize(new AppConfig { UiMaterial = "classic" }).UiMaterial == "classic"
                && ConfigStore.Normalize(new AppConfig { UiMaterial = "neon" }).UiMaterial == "acrylic");''',
     '''            Check("外观：材质由主题推导（液态玻璃 → 同款材质，其余 → 亚克力）",
                Ui.SettingsWindow.MaterialFor("liquid-glass") == Platform.WindowMaterial.Glass
                && Ui.SettingsWindow.MaterialFor("dark") == Platform.WindowMaterial.Acrylic
                && Ui.SettingsWindow.MaterialFor("light") == Platform.WindowMaterial.Acrylic
                && Ui.SettingsWindow.MaterialFor("system") == Platform.WindowMaterial.Acrylic);
            Check("外观：BaseTheme 记住亚克力档的深浅、拒绝乱值",
                new AppConfig().BaseTheme == "dark"
                && ConfigStore.Normalize(new AppConfig { BaseTheme = "system" }).BaseTheme == "system"
                && ConfigStore.Normalize(new AppConfig { BaseTheme = "zzz" }).BaseTheme == "dark");''', 'material assertions'),
    ('''            Check("界面材质：玻璃/亚克力都带 alpha（真透），经典档不透明",
                (Platform.WindowMaterial.TintArgb("glass", false) >> 24 & 0xFF) is > 0x80 and < 0xF0
                && (Platform.WindowMaterial.TintArgb("acrylic", false) >> 24 & 0xFF) is > 0x60 and < 0xF0
                && (Platform.WindowMaterial.TintArgb("classic", false) >> 24 & 0xFF) == 0xFF);''',
     '''            Check("界面材质：玻璃/亚克力色调都带 alpha（真透）",
                (Platform.WindowMaterial.TintArgb("glass", false) >> 24 & 0xFF) is > 0x80 and < 0xF0
                && (Platform.WindowMaterial.TintArgb("acrylic", false) >> 24 & 0xFF) is > 0x60 and < 0xF0);
            Check("界面材质：只有亚克力需要裁窗口区域（玻璃的四角由我们自己画）",
                Platform.WindowMaterial.NeedsRegion("acrylic")
                && !Platform.WindowMaterial.NeedsRegion("glass"));''', 'tint assertions'),
    ('''            Check("界面材质：老系统与经典档退回纯色（不假装有模糊）",
                Platform.WindowMaterial.ResolveBackdrop(10240, "acrylic") == "solid"
                && Platform.WindowMaterial.ResolveBackdrop(26100, "classic") == "solid");''',
     '''            Check("界面材质：老系统退回纯色（不假装有模糊）",
                Platform.WindowMaterial.ResolveBackdrop(10240, "acrylic") == "solid");''', 'old system'),
])
for f, why, hit in log:
    print(('OK  ' if hit else 'MISS'), f, why)

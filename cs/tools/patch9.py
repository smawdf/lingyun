# -*- coding: utf-8 -*-
"""把 Diag 与岛内的组合模式调用点补上 net 参数，并加网速模块断言。"""
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


patch('cs/LingYun/Ui/NativeIslandApp.cs', [
    ('''            _cfg.CompositeMedia && MediaActive,
            CompactClockSlotW(), CompositeHwW(), CompositeMediaW(), _cfg.CompactScale);''',
     '''            _cfg.CompositeMedia && MediaActive,
            CompactClockSlotW(), CompositeHwW(), CompositeNetW(), CompositeMediaW(), _cfg.CompactScale);''',
     'target width args'),
])

patch('cs/LingYun/Diagnostics/Diag.cs', [
    ('''            const float cw = 60, hw = 100, mw = 200;
            var (total3, slots3) = NativeIslandApp.CompositeLayout(true, true, true, cw, hw, mw);
            Check("组合模式：三模块槽位按 时间→硬件→媒体 排列",''',
     '''            const float cw = 60, hw = 100, nw = 90, mw = 200;
            var (total3, slots3) = NativeIslandApp.CompositeLayout(true, true, false, true, cw, hw, nw, mw);
            Check("组合模式：三模块槽位按 时间→硬件→媒体 排列",''', 'layout call 1'),
    ('''            Check("组合模式：单个模块也成立（总宽随模块数递减）",
                NativeIslandApp.CompositeLayout(true, false, false, cw, hw, mw).Total
                < NativeIslandApp.CompositeLayout(true, true, false, cw, hw, mw).Total
                && NativeIslandApp.CompositeLayout(true, true, false, cw, hw, mw).Total < total3);
            Check("组合模式：三模块全关 → 宽度退回时钟胶囊",
                Math.Abs(NativeIslandApp.CompositeWidth(false, false, false, cw, hw, mw, 1.0)
                         - NativeIslandApp.BaseCompactW) < 0.01);''',
     '''            Check("组合模式：单个模块也成立（总宽随模块数递减）",
                NativeIslandApp.CompositeLayout(true, false, false, false, cw, hw, nw, mw).Total
                < NativeIslandApp.CompositeLayout(true, true, false, false, cw, hw, nw, mw).Total
                && NativeIslandApp.CompositeLayout(true, true, false, false, cw, hw, nw, mw).Total < total3);
            Check("组合模式：网速模块插在硬件与媒体之间（顺位固定）",
                NativeIslandApp.CompositeLayout(true, true, true, true, cw, hw, nw, mw) is var s4
                && s4.Slots.Length == 4
                && Math.Abs(s4.Slots[2].W - nw) < 0.01
                && s4.Slots[2].X > s4.Slots[1].X + s4.Slots[1].W
                && s4.Slots[3].X > s4.Slots[2].X + s4.Slots[2].W
                && s4.Slots.Zip(s4.Slots.Skip(1), (a, b) => a.X + a.W <= b.X).All(x => x));
            Check("组合模式：三模块全关 → 宽度退回时钟胶囊",
                Math.Abs(NativeIslandApp.CompositeWidth(false, false, false, false, cw, hw, nw, mw, 1.0)
                         - NativeIslandApp.BaseCompactW) < 0.01);''', 'layout assertions'),
    ('''            Check("组合模式：自动长度下限 220 / 上限 900",
                Math.Abs(NativeIslandApp.CompositeWidth(true, false, false, 10, 0, 0, 1.0) - 220) < 0.01''',
     '''            Check("组合模式：自动长度下限 220 / 上限 900",
                Math.Abs(NativeIslandApp.CompositeWidth(true, false, false, false, 10, 0, 0, 0, 1.0) - 220) < 0.01''', 'clamp assertion'),
    ('''            Check("组合模式：全关时 Normalize 强制保留时间模块",''',
     '''            Check("组合模式：网速模块默认关、Normalize 保留；全关时仍强制保留时间",
                !new AppConfig().CompositeNetwork
                && ConfigStore.Normalize(new AppConfig { CompositeNetwork = true }).CompositeNetwork
                && ConfigStore.Normalize(new AppConfig
                {
                    Composite = true, CompositeClock = false, CompositeHardware = false,
                    CompositeMedia = false, CompositeNetwork = false,
                }).CompositeClock);
            Check("组合模式：全关时 Normalize 强制保留时间模块",''', 'net normalize assertion'),
])
for f, why, hit in log:
    print(('OK  ' if hit else 'MISS'), f, why)

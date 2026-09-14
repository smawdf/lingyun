// Derived from NotchPeninsula (https://github.com/GEORGEWWWU/NotchPeninsula) Audio.cs,
// Apache-2.0. Modifications for LingYun:
//  - Lazily opens the endpoint on the calling (island UI) thread: AudioEndpointVolume is NOT an
//    agile COM object, creating it on one thread and touching it from another throws.
//  - Silent degrade: no render device / COM failure -> Available=false, UI hides the volume row.
//  - Percent/step helpers extracted as pure statics so --self-test can pin clamping without hardware.
using NAudio.CoreAudioApi;

namespace LingYun.Services;

/// <summary>
/// 系统主音量：读/写默认播放设备的 MasterVolumeLevelScalar + 静音开关。
/// 所有调用都发生在岛 UI 线程（渲染与输入都在那条线程上）。
/// </summary>
public sealed class AudioVolumeService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private bool _failed;

    /// <summary>设备可用（打不开设备时 false，UI 应整行隐藏）。</summary>
    public bool Available => EnsureDevice() is not null;

    /// <summary>当前主音量 0..1；设备不可用返回 null。</summary>
    public float? GetVolume()
    {
        var ep = Endpoint();
        if (ep is null) return null;
        try { return Math.Clamp(ep.MasterVolumeLevelScalar, 0f, 1f); }
        catch { return null; }
    }

    /// <summary>设置主音量（自动钳到 0..1）+ 静音修正：调大音量时解除静音，否则"拧了没声音"。</summary>
    public void SetVolume(float level)
    {
        var ep = Endpoint();
        if (ep is null) return;
        try
        {
            float v = Math.Clamp(level, 0f, 1f);
            if (v > 0f && ep.Mute) ep.Mute = false;
            ep.MasterVolumeLevelScalar = v;
        }
        catch { /* 设备热插拔/独占时不致命 */ }
    }

    /// <summary>当前是否静音；设备不可用返回 null。</summary>
    public bool? IsMuted()
    {
        var ep = Endpoint();
        if (ep is null) return null;
        try { return ep.Mute; }
        catch { return null; }
    }

    /// <summary>切换静音，返回切换后的状态；设备不可用返回 null。</summary>
    public bool? ToggleMute()
    {
        var ep = Endpoint();
        if (ep is null) return null;
        try
        {
            ep.Mute = !ep.Mute;
            return ep.Mute;
        }
        catch { return null; }
    }

    /// <summary>滚轮步进：+1/-1 档，钳在 0..1。抽成纯函数便于自测。</summary>
    internal static float Step(float current, int direction, float step = 0.02f)
        => Math.Clamp(current + (direction > 0 ? step : -step), 0f, 1f);

    /// <summary>0..1 → 百分比文案（四舍五入到整数）。</summary>
    internal static string FormatPercent(float level)
        => $"{Math.Round(Math.Clamp(level, 0f, 1f) * 100)}%";

    private AudioEndpointVolume? Endpoint()
    {
        var device = EnsureDevice();
        if (device is null) return null;
        try { return device.AudioEndpointVolume; }
        catch { return null; }
    }

    private MMDevice? EnsureDevice()
    {
        if (_device is not null) return _device;
        if (_failed) return null;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return _device;
        }
        catch
        {
            // 没有播放设备 / COM 初始化失败：静默降级，只记一次
            _failed = true;
            return null;
        }
    }

    public void Dispose()
    {
        try { _device?.Dispose(); } catch { /* ignore */ }
        try { _enumerator?.Dispose(); } catch { /* ignore */ }
        _device = null;
        _enumerator = null;
    }
}

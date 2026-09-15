// 音频设备：枚举播放/录音端点、读当前默认、切换系统默认设备。
//
// 为什么要写 IPolicyConfig 这段 COM：Windows **没有公开 API** 能改默认音频设备
// （只有"设置 → 声音"那个 UI 能改）。社区通用做法（SoundSwitch / AudioSwitcher 等）都是
// 调未公开的 IPolicyConfig::SetDefaultEndpoint——它的 vtable 顺序必须和系统完全一致，
// 写错一个方法就会调到别的方法上去，所以下面每个方法的顺序都不能动。
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace LingYun.Services;

/// <summary>
/// 音频设备枚举与切换。
/// 线程约束与 <see cref="AudioVolumeService"/> 相同：MMDeviceEnumerator 与 IPolicyConfig 都是
/// **非敏捷 COM 对象**，只能在**岛线程**上创建与调用（跨线程会抛，且异常容易被吞成"没设备"）。
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    public enum Flow { Output, Input }

    /// <summary>一个端点（播放或录音）。Id 就是切换默认设备时要传的设备 ID。</summary>
    public readonly record struct DeviceInfo(string Id, string Name, bool IsDefault);

    /// <summary>一次枚举的结果：设备列表（默认项排最前）+ 当前默认设备 Id。</summary>
    public readonly record struct DeviceList(Flow Flow, DeviceInfo[] Items, string CurrentId);

    private static readonly Guid ClsidPolicyConfig = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    /// <summary>最近一次 SetDefault 的 HRESULT（诊断用：能看出未公开接口到底通不通）。</summary>
    public static int LastSetDefaultHr { get; private set; } = -1;

    private MMDeviceEnumerator? _enumerator;
    private bool _failed;

    /// <summary>枚举某一侧的设备；失败返回 null（没有设备/COM 不可用）。</summary>
    public DeviceList? List(Flow flow)
    {
        var en = Enumerator();
        if (en is null) return null;
        try
        {
            var dataFlow = flow == Flow.Output ? DataFlow.Render : DataFlow.Capture;
            string current;
            try { current = en.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia).ID; }
            catch { current = ""; }

            var items = new List<DeviceInfo>();
            foreach (var d in en.EnumerateAudioEndPoints(dataFlow, DeviceState.Active))
            {
                string id;
                try { id = d.ID; } catch { continue; }
                string name;
                try { name = d.FriendlyName; } catch { name = "(未知设备)"; }
                items.Add(new DeviceInfo(id, name, string.Equals(id, current, StringComparison.OrdinalIgnoreCase)));
                try { d.Dispose(); } catch { /* ignore */ }
            }
            // 默认的排最前，其余按名字排，界面里顺序稳定
            items.Sort((a, b) => a.IsDefault != b.IsDefault
                ? (a.IsDefault ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            return new DeviceList(flow, items.ToArray(), current);
        }
        catch
        {
            _failed = true;
            return null;
        }
    }

    /// <summary>
    /// 把某一侧的默认设备切成 deviceId。
    ///
    /// 三条来自社区实战的硬规矩（见文件头的说明）：
    ///   1. **不信 S_OK**：未公开接口返回成功也可能什么都没发生（虚拟声卡驱动会自己抢回默认），
    ///      所以设完必须**回读校验**，失败还要重试一次；
    ///   2. 只设 eConsole + eMultimedia（Windows"设为默认设备"就是这两个；
    ///      eCommunications 是"默认通信设备"，另一个语义，不去动它）；
    ///   3. 接口按 IID 逐个探测（不同系统服务的变体不同），全部失败才算失败。
    /// </summary>
    public bool SetDefault(Flow flow, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        var list = List(flow);
        if (list is not { } l || !l.Items.Any(d => d.Id == deviceId)) return false;   // 设备必须真实存在

        var dataFlow = flow == Flow.Output ? DataFlow.Render : DataFlow.Capture;
        bool applied = Apply(deviceId);
        if (!applied) return false;
        if (Verify(dataFlow, deviceId)) return true;
        // 重试一次：有些驱动/APO 会在切换瞬间把默认抢回去
        Apply(deviceId);
        return Verify(dataFlow, deviceId);
    }

    /// <summary>设 eConsole + eMultimedia 两个 role；返回是否所有调用都报成功（不代表真的生效）。</summary>
    private bool Apply(string deviceId)
    {
        int last = -1;
        foreach (var cfg in PolicyConfigVariants())
        {
            bool all = true;
            foreach (int role in new[] { 0, 1 })
            {
                int hr = cfg.SetDefaultEndpoint(deviceId, role);
                if (hr != 0) { all = false; last = hr; }
            }
            LastSetDefaultHr = all ? 0 : last;
            if (all) return true;
        }
        LastSetDefaultHr = last;
        return false;
    }

    /// <summary>回读校验：默认设备真的变成 deviceId 才算数（两个 role 都要）。</summary>
    private bool Verify(DataFlow flow, string deviceId)
    {
        for (int i = 0; i < 25; i++)       // 最多等 ~2.5s，换设备不是瞬时的
        {
            var en = Enumerator();
            if (en is null) return false;
            bool ok = true;
            foreach (var role in new[] { Role.Console, Role.Multimedia })
            {
                try
                {
                    if (!string.Equals(en.GetDefaultAudioEndpoint(flow, role).ID, deviceId,
                            StringComparison.OrdinalIgnoreCase))
                    { ok = false; break; }
                }
                catch { ok = false; break; }
            }
            if (ok) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// CPolicyConfigClient 在不同 Windows 版本上服务的接口 IID 不同，按顺序探测。
    /// 12 方法的那个（F8679F50）是 Win10 RS1 至今的主力；Vista 那个是 11 方法（没有 ResetDeviceFormat），
    /// 所以它单独一个接口定义、不能和 12 方法的混用——slot 数不一样。
    /// </summary>
    private static IEnumerable<IPolicyConfig> PolicyConfigVariants()
    {
        object client;
        try { client = new PolicyConfigClient(); }
        catch { yield break; }
        if (client is IPolicyConfig v7) yield return v7;
        try { Marshal.ReleaseComObject(client); } catch { /* ignore */ }
    }

    private MMDeviceEnumerator? Enumerator()
    {
        if (_enumerator is not null) return _enumerator;
        if (_failed) return null;
        try
        {
            _enumerator = new MMDeviceEnumerator();
            return _enumerator;
        }
        catch
        {
            _failed = true;
            return null;
        }
    }

    public void Dispose()
    {
        try { _enumerator?.Dispose(); } catch { /* ignore */ }
        _enumerator = null;
    }

    // ---- 未公开的 IPolicyConfig：vtable 顺序必须与系统一致，别插方法、别改顺序 ----

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // 注意：bool 参数必须显式 MarshalAs(UnmanagedType.Bool)。ComImport 里 System.Boolean 默认按
        // VARIANT_BOOL（2 字节）编组，而这里要的是 Win32 BOOL（4 字节），不标注会让参数错位
        // （对 SetDefaultEndpoint 这个槽位不影响，但别的槽位会调到错的东西上）。
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.Bool)] bool bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id,
            IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.Bool)] bool bDefault, IntPtr pDefaultPeriod, IntPtr pMinPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr pPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);
        /// <summary>槽位 11（前面正好 10 个方法）。多一个少一个都会调到别的方法上。</summary>
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [MarshalAs(UnmanagedType.U4)] int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }
}

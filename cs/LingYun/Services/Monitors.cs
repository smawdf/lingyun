using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

using Windows.Devices.Geolocation;

namespace LingYun.Services;

public sealed record PerfMetrics(double Cpu, double MemPct, double MemUsedGb, double MemTotalGb,
    double NetKbps, double DiskReadKbps, double UploadKbps, double UptimeSeconds);

public sealed class PerfSampler : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private long _prevIdle, _prevKernel, _prevUser;
    private long _prevNetReceived, _prevNetSent;
    private DateTime _prevNetAt = DateTime.UtcNow;
    private bool _primed;

    public event Action<PerfMetrics>? Metrics;

    public PerfSampler()
    {
        _timer = new System.Timers.Timer(1000) { AutoReset = true };
        _timer.Elapsed += (_, _) => Sample();
        GetSystemTimes(out var idle, out var kernel, out var user);
        _prevIdle = ToLong(idle); _prevKernel = ToLong(kernel); _prevUser = ToLong(user);
        (_prevNetReceived, _prevNetSent) = TotalNetBytes();
        _primed = true;
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    private void Sample()
    {
        if (!_primed) return;
        GetSystemTimes(out var idle, out var kernel, out var user);
        long i = ToLong(idle), k = ToLong(kernel), u = ToLong(user);
        long idleD = i - _prevIdle, kernelD = k - _prevKernel, userD = u - _prevUser;
        double cpu = ComputeCpuPercent(idleD, kernelD, userD);
        _prevIdle = i; _prevKernel = k; _prevUser = u;

        var mem = new MEMORYSTATUSEX();
        GlobalMemoryStatusEx(mem);
        double memPct = mem.dwMemoryLoad;
        double totalGb = mem.ullTotalPhys / 1024.0 / 1024 / 1024;
        double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024 / 1024;

        var (netReceived, netSent) = TotalNetBytes();
        var now = DateTime.UtcNow;
        double sec = (now - _prevNetAt).TotalSeconds;
        double netKbps = ComputeNetworkRate(_prevNetReceived, netReceived, sec);
        double uploadKbps = ComputeNetworkRate(_prevNetSent, netSent, sec);
        _prevNetReceived = netReceived;
        _prevNetSent = netSent;
        _prevNetAt = now;

        Metrics?.Invoke(new PerfMetrics(cpu, memPct, usedGb, totalGb, netKbps, 0, uploadKbps,
            Math.Max(0, Environment.TickCount64 / 1000.0)));
    }

    /// <summary>按 GetSystemTimes 语义计算 CPU：kernel 已包含 idle，故总时间为 kernel + user。</summary>
    internal static double ComputeCpuPercent(long idleDelta, long kernelDelta, long userDelta)
    {
        if (idleDelta < 0 || kernelDelta < 0 || userDelta < 0) return 0;
        long total = kernelDelta + userDelta;
        if (total <= 0) return 0;
        double idle = Math.Clamp((double)idleDelta, 0, total);
        return Math.Clamp((1 - idle / total) * 100, 0, 100);
    }

    /// <summary>累计字节差换算为 KB/s；计数器回退或时间无效时返回 0。</summary>
    internal static double ComputeNetworkRate(long previousBytes, long currentBytes, double elapsedSeconds)
    {
        if (previousBytes < 0 || currentBytes <= previousBytes || elapsedSeconds <= 0 || double.IsNaN(elapsedSeconds))
            return 0;
        return Math.Max(0, (currentBytes - previousBytes) / 1024.0 / elapsedSeconds);
    }

    private static (long Received, long Sent) TotalNetBytes()
    {
        long received = 0, sent = 0;
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                continue;
            try
            {
                var stat = nic.GetIPStatistics();
                received += stat.BytesReceived;
                sent += stat.BytesSent;
            }
            catch
            {
                // 网卡在采样期间被拔出/禁用时跳过，不让性能采样线程中断。
            }
        }
        return (received, sent);
    }

    private static long ToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        => ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
        out System.Runtime.InteropServices.ComTypes.FILETIME user);
}

/// <summary>当前天气 + 未来数小时预报（Hourly 为空 = 数据源没给，UI 侧降级显示）。</summary>
public sealed record WeatherInfo(string City, double TempC, string Desc, bool Ok,
    string? Error = null, (int Hour, double Temp, double Code)[]? Hourly = null,
    double? Humidity = null, double? WindKph = null, double? Cloud = null);

public sealed class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly System.Timers.Timer _timer;
    private double? _lat, _lon;
    private string _city = "";
    private readonly bool _manualCity;   // 用户在配置里手填了城市：显示名不被 IP 结果覆盖
    private bool _winLocTried;           // 系统定位已有定论（成功/拒绝/异常/连续超时）
    private int _winLocTimeouts;         // 授权弹窗连续无人理会的次数（≥2 永久放弃，不反复弹）
    private System.Windows.Threading.Dispatcher? _locDispatcher;

    /// <summary>上次定位来源（诊断用）：Manual / Windows / IP / 无。</summary>
    public string LocationSource { get; private set; } = "无";

    /// <summary>当前城市名（诊断用）。</summary>
    public string LastCity => _city;

    public event Action<WeatherInfo>? Updated;

    public WeatherService(string city, double? lat, double? lon)
    {
        _city = city;
        _manualCity = !string.IsNullOrWhiteSpace(city);
        _lat = lat;
        _lon = lon;
        _timer = new System.Timers.Timer(30 * 60 * 1000) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer.Stop();

    public async Task RefreshAsync()
    {
        try
        {
            if (_manualCity)
            {
                // 手填城市 = 用户指定要这座城市的天气：只按名称编码，不回退别的位置
                LocationSource = "Manual";
                if (_lat is null || _lon is null)
                {
                    await GeocodeAsync(_city);
                    if (_lat is not null) LocationSource = "Manual+Geocode";
                }
            }
            else
            {
                // 非手填：优先电脑实际位置（Windows 系统定位，不走代理），
                // 失败退 IP——IP 每次刷新重解析，代理出口变化时自愈
                await TryWindowsLocationAsync();
                if (_lat is null || _lon is null)
                {
                    LocationSource = "IP";
                    await ResolveLocationAsync();
                }
            }
            if (_lat is null || _lon is null)
            {
                Updated?.Invoke(new WeatherInfo(_city.Length == 0 ? "北京" : _city, 0, "", false, "无法定位"));
                return;
            }
            // 注意：**不要**再带 `current_weather=true` —— 实测（2026-09-15）只要它和 `current=` 同时出现，
            // Open-Meteo 就返回 `current: null`，于是湿度/风速/云量三张卡永远显示"—"。
            // 只留 `current=` 时温度、天气码、湿度、风速、云量都能拿到（已实测）。
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={_lat}&longitude={_lon}"
                + "&hourly=temperature_2m,weathercode&forecast_days=2"
                + "&current=temperature_2m,relative_humidity_2m,weather_code,wind_speed_10m,cloud_cover";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            // 全部从 `current` 块读（旧 `current_weather` 块已不再请求）
            var cur = doc.RootElement.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            double code = cur.GetProperty("weather_code").GetDouble();
            double? hum = cur.TryGetProperty("relative_humidity_2m", out var hv) ? hv.GetDouble() : null;
            double? wind = cur.TryGetProperty("wind_speed_10m", out var wv) ? wv.GetDouble() : null;
            double? cloud = cur.TryGetProperty("cloud_cover", out var cv) ? cv.GetDouble() : null;
            Updated?.Invoke(new WeatherInfo(_city, temp, WmoDesc(code), true,
                Hourly: ParseHourly(doc.RootElement), Humidity: hum, WindKph: wind, Cloud: cloud));
        }
        catch (Exception ex)
        {
            Updated?.Invoke(new WeatherInfo(_city, 0, "", false, ex.Message));
        }
    }

    /// <summary>取未来 4 个整点的温度/天气码（今天剩下的优先，不足补明天）；解析失败返回 null，UI 静默降级。</summary>
    private static (int Hour, double Temp, double Code)[]? ParseHourly(System.Text.Json.JsonElement root)
    {
        try
        {
            var hourly = root.GetProperty("hourly");
            var times = hourly.GetProperty("time");
            var temps = hourly.GetProperty("temperature_2m");
            var codes = hourly.GetProperty("weathercode");
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var tomorrow = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd");
            var list = new List<(int, double, double)>();
            for (int i = 0; i < times.GetArrayLength() && list.Count < 4; i++)
            {
                var tstr = times[i].GetString();
                if (tstr is null || tstr.Length < 13) continue;
                bool isToday = tstr.StartsWith(today, StringComparison.Ordinal);
                bool isTomorrow = tstr.StartsWith(tomorrow, StringComparison.Ordinal);
                if (!isToday && !isTomorrow) continue;
                if (!int.TryParse(tstr.AsSpan(11, 2), out int h)) continue;
                if (isToday && h <= DateTime.Now.Hour) continue;   // 只要还没过去的整点
                list.Add((h, temps[i].GetDouble(), codes[i].GetDouble()));
            }
            return list.Count > 0 ? list.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 电脑实际位置：Windows 系统定位服务（Wi-Fi 指纹/GPS，不经过用户的 HTTP 代理）。
    /// 首次调用会触发系统授权弹窗；拒绝/超时/无传感器一律静默退回 IP 定位。
    /// </summary>
    private async Task TryWindowsLocationAsync()
    {
        if (_winLocTried) return;
        void Log(string m)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "灵云-loc-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {m}" + Environment.NewLine); } catch { }
        }
        Log("step: enter");
        try
        {
            // WinRT 定位 API 必须跑在有消息泵的 STA 线程上——线程池 MTA 上会同步卡死。
            // 授权弹窗最多等 12s：没人理就不堵天气（本轮先退 IP）；连续 2 次无人理会 → 永久放弃
            Log("step: sta ready, requesting access");
            var accessTask = OnSta(async () =>
            {
                Log("step: RequestAccessAsync called (STA)");
                var r = await Geolocator.RequestAccessAsync();
                Log($"step: RequestAccessAsync returned {r}");
                return r;
            });
            var accessWinner = await Task.WhenAny(accessTask, Task.Delay(TimeSpan.FromSeconds(12)));
            if (accessWinner != accessTask)
            {
                Log("step: access TIMEOUT 12s");
                if (++_winLocTimeouts >= 2) _winLocTried = true;
                return;
            }
            _winLocTried = true;
            if (accessTask.Result != GeolocationAccessStatus.Allowed) { Log("step: access DENIED"); return; }

            var posTask = OnSta(async () =>
            {
                var gl = new Geolocator { DesiredAccuracyInMeters = 100 };
                var op = gl.GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(10));
                try { return await op; } finally { op.Close(); }
            });
            var posWinner = await Task.WhenAny(posTask, Task.Delay(TimeSpan.FromSeconds(30)));
            if (posWinner != posTask)
            {
                // 取点超时（Wi-Fi 首次定位可能慢）：允许下个刷新重试，不算定论
                Log("step: position TIMEOUT 30s, will retry next refresh");
                return;
            }
            var pos = posTask.Result;
            Log($"step: position ok {_lat:0.####},{_lon:0.####}");
            _lat = pos.Coordinate.Point.Position.Latitude;
            _lon = pos.Coordinate.Point.Position.Longitude;
            // 成功即定论：_winLocTried 已在上面置位，后续刷新直接复用坐标
            LocationSource = "Windows";
            if (_city.Length == 0)
                await ReverseGeocodeAsync(_lat.Value, _lon.Value);
        }
        catch
        {
            // 未授权 / 系统定位关闭 / 抛异常：视为已定论，退回 IP
            _winLocTried = true;
        }
    }

    /// <summary>
    /// 专用 STA 调度器：WinRT 定位 API 的宿主线程（惰性创建，后台常驻泵消息）。
    /// </summary>
    private Task<T> OnSta<T>(Func<Task<T>> func)
    {
        EnsureStaDispatcher();
        var dispatcher = _locDispatcher!;
        return dispatcher.InvokeAsync(func).Task.Unwrap();
    }

    private void EnsureStaDispatcher()
    {
        if (_locDispatcher is not null) return;
        var thread = new Thread(() =>
        {
            _locDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "灵云-定位",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        while (_locDispatcher is null) Thread.Sleep(10);
    }

    /// <summary>坐标 → 城市名（BigDataCloud 免费反查，中文）。失败保持空名，由兜底逻辑命名。</summary>
    private async Task ReverseGeocodeAsync(double lat, double lon)
    {
        try
        {
            var url = "https://api.bigdatacloud.net/data/reverse-geocode-client"
                      + $"?latitude={lat}&longitude={lon}&localityLanguage=zh";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            string? name = null;
            if (doc.RootElement.TryGetProperty("city", out var city) && city.GetString() is { Length: > 0 } c1)
                name = c1;
            else if (doc.RootElement.TryGetProperty("locality", out var loc) && loc.GetString() is { Length: > 0 } c2)
                name = c2;
            if (string.IsNullOrWhiteSpace(name)) return;
            if (name.EndsWith("市") && name.Length > 2) name = name[..^1];   // 深圳市 → 深圳
            _city = name;
        }
        catch { /* 反查失败不致命 */ }
    }

    /// <summary>城市名 → 坐标（open-meteo geocoding，中文可查）。失败不动坐标。</summary>
    private async Task GeocodeAsync(string name)
    {
        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search"
                      + $"?name={Uri.EscapeDataString(name)}&count=1&language=zh&format=json";
            var json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results)
                && results.GetArrayLength() > 0)
            {
                var first = results[0];
                if (first.TryGetProperty("latitude", out var lat) && first.TryGetProperty("longitude", out var lon))
                {
                    _lat = lat.GetDouble();
                    _lon = lon.GetDouble();
                }
            }
        }
        catch { /* 离线/接口变动：留给 IP 定位兜底 */ }
    }

    private async Task ResolveLocationAsync()
    {
        foreach (var url in new[] { "http://ip-api.com/json/?lang=zh-CN", "https://ipapi.co/json/" })
        {
            try
            {
                var json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("lat", out var lat) && root.TryGetProperty("lon", out var lon))
                {
                    _lat = lat.GetDouble();
                    _lon = lon.GetDouble();
                }
                else if (root.TryGetProperty("latitude", out var lat2) && root.TryGetProperty("longitude", out var lon2))
                {
                    _lat = lat2.GetDouble();
                    _lon = lon2.GetDouble();
                }
                if (root.TryGetProperty("city", out var city))
                    _city = city.GetString() ?? _city;
                if (_lat is not null) return;
            }
            catch { /* next */ }
        }
        // 北京兜底
        _lat ??= 39.9;
        _lon ??= 116.4;
        if (_city.Length == 0) _city = "北京";
    }

    private static string WmoDesc(double code) => code switch
    {
        0 => "晴",
        1 or 2 => "少云",
        3 => "阴",
        45 or 48 => "雾",
        51 or 53 or 55 => "毛毛雨",
        61 or 63 or 65 => "雨",
        71 or 73 or 75 => "雪",
        80 or 81 or 82 => "阵雨",
        95 => "雷暴",
        _ => "多云",
    };
}

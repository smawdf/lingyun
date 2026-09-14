// 歌词：思路参考 NotchPeninsula MediaController（LRC 时间轴解析 + 按播放位置取当前句，Apache-2.0），
// 但数据源只保留 LRCLIB（免费、无需鉴权）；参考项目里的 QQ音乐/网易云 两个引擎需要伪造请求头，
// 接口一变就失效，故不移植。本文件为独立实现，未复制上游代码。
using System.Net.Http;
using System.Text.Json;

namespace LingYun.Services;

/// <summary>一行歌词：起始时间 + 文本。</summary>
public readonly record struct LyricLine(TimeSpan Time, string Text);

/// <summary>
/// 在线歌词（LRCLIB）：按「歌名 + 歌手（+时长）」查同步歌词，解析成时间轴供 UI 每帧取当前句。
/// 网络失败/查不到都静默降级为「无歌词」，不影响播放器功能。
/// </summary>
public sealed class LyricsService : IDisposable
{
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private LyricLine[] _lines = Array.Empty<LyricLine>();
    private string _trackKey = "";      // 当前歌词对应的「歌名|歌手」
    private string _fetchingKey = "";   // 正在请求的曲目，避免同一首歌重复打接口

    public LyricsService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // LRCLIB 要求带上能识别应用的 User-Agent
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LingYun/5.0 (personal use)");
    }

    /// <summary>当前曲目的歌词行（无歌词时为空数组）。拷贝出去，调用方无需加锁。</summary>
    public LyricLine[] Lines
    {
        get { lock (_gate) return _lines; }
    }

    /// <summary>歌词是否已就绪（对应当前曲目）。</summary>
    public bool HasLyrics
    {
        get { lock (_gate) return _lines.Length > 0; }
    }

    /// <summary>
    /// 曲目变化时调用（UI 线程）。同一首歌只打一次接口；旧结果回来时代入的 key 不匹配就丢弃。
    /// </summary>
    public void EnsureFor(string title, string artist, int durationSec)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        string key = TrackKey(title, artist);
        lock (_gate)
        {
            if (key == _trackKey) return;
            _trackKey = key;
            _lines = Array.Empty<LyricLine>();
            if (key == _fetchingKey) return;   // 已在请求中
            _fetchingKey = key;
        }
        _ = Task.Run(async () =>
        {
            var parsed = await FetchAsync(title, artist, durationSec);
            lock (_gate)
            {
                // 只有曲目没被切走才写入（网络返回时用户可能已经换歌）
                if (_trackKey == key) _lines = parsed;
                if (_fetchingKey == key) _fetchingKey = "";
            }
        });
    }

    /// <summary>停止/无媒体时清空。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _trackKey = "";
            _lines = Array.Empty<LyricLine>();
        }
    }

    internal static string TrackKey(string title, string artist) => $"{title.Trim()}|{artist.Trim()}";

    private async Task<LyricLine[]> FetchAsync(string title, string artist, int durationSec)
    {
        try
        {
            // 先精确查（带时长，能避开 Live/翻唱版）；查不到再退到搜索取第一条带同步歌词的
            string exact = "https://lrclib.net/api/get"
                           + $"?track_name={Uri.EscapeDataString(title)}"
                           + $"&artist_name={Uri.EscapeDataString(artist)}"
                           + (durationSec > 0 ? $"&duration={durationSec}" : "");
            var synced = await TryReadSyncedAsync(exact);
            if (synced is null)
            {
                string search = "https://lrclib.net/api/search"
                                + $"?track_name={Uri.EscapeDataString(title)}"
                                + (string.IsNullOrWhiteSpace(artist) ? "" : $"&artist_name={Uri.EscapeDataString(artist)}");
                synced = await TryReadSyncedAsync(search);
            }
            return synced is null ? Array.Empty<LyricLine>() : ParseLrc(synced);
        }
        catch
        {
            // 离线 / 超时 / 接口变动：当作没有歌词
            return Array.Empty<LyricLine>();
        }
    }

    /// <summary>读一个 LRCLIB 响应：/api/get 是对象，/api/search 是数组；都只取 syncedLyrics。</summary>
    private async Task<string?> TryReadSyncedAsync(string url)
    {
        using var resp = await _http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
                if (item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
                    return s.GetString();
            return null;
        }
        return doc.RootElement.TryGetProperty("syncedLyrics", out var single) && single.ValueKind == JsonValueKind.String
            ? single.GetString()
            : null;
    }

    /// <summary>
    /// 解析 LRC：支持 [mm:ss.xx] / [mm:ss.xxx] / [mm:ss]，一行可带多个时间标签；
    /// 解析不了的标签（[ar:]/[ti:]/[offset:] 等）与空行直接跳过。结果按时间排序。
    /// </summary>
    internal static LyricLine[] ParseLrc(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var rawLine in lrc.Split('\n'))
        {
            var line = rawLine.Trim();
            int pos = 0;
            var stamps = new List<TimeSpan>();
            while (pos < line.Length && line[pos] == '[')
            {
                int close = line.IndexOf(']', pos);
                if (close < 0) break;
                if (TryParseStamp(line[(pos + 1)..close], out var ts)) stamps.Add(ts);
                pos = close + 1;
            }
            if (stamps.Count == 0) continue;
            string text = line[pos..].Trim();
            if (text.Length == 0) continue;
            foreach (var ts in stamps) list.Add(new LyricLine(ts, text));
        }
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list.ToArray();
    }

    private static bool TryParseStamp(string inner, out TimeSpan ts)
    {
        ts = default;
        int colon = inner.IndexOf(':');
        if (colon <= 0) return false;
        if (!int.TryParse(inner[..colon], out int minutes)) return false;
        var rest = inner[(colon + 1)..];
        // 秒可以带小数：ss、ss.f、ss.ff、ss.fff
        double seconds = 0;
        int dot = rest.IndexOf('.');
        if (dot >= 0)
        {
            if (!int.TryParse(rest[..dot], out int whole)) return false;
            var frac = rest[(dot + 1)..];
            if (frac.Length == 0 || !int.TryParse(frac, out int fracVal)) return false;
            seconds = whole + fracVal / Math.Pow(10, frac.Length);
        }
        else if (!int.TryParse(rest, out int s))
        {
            return false;
        }
        else
        {
            seconds = s;
        }
        if (minutes < 0 || seconds < 0) return false;
        ts = TimeSpan.FromSeconds(minutes * 60 + seconds);
        return true;
    }

    /// <summary>
    /// 取 position 时刻应显示的行下标：返回最后一条 Time &lt;= position 的行；早于第一行返回 -1。
    /// 纯函数，自测直接钉边界。
    /// </summary>
    internal static int LineIndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { /* ignore */ }
        Clear();
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LingYun.Services;

public sealed record ActionDef(string Key, string Label, string Icon, Action Run);

public static class PowerActions
{
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static readonly IReadOnlyDictionary<string, ActionDef> All =
        new Dictionary<string, ActionDef>
        {
            ["shutdown"] = new("shutdown", "关机", "⏻", Shutdown),
            ["lock"] = new("lock", "锁屏", "🔒", Lock),
            ["display_off"] = new("display_off", "息屏", "🌙", DisplayOff),
            ["pause_media"] = new("pause_media", "暂停媒体", "⏸", PauseMedia),
        };

    public const string DefaultKey = "shutdown";

    public static ActionDef Get(string key) =>
        All.TryGetValue(key, out var a) ? a : All[DefaultKey];

    public static void Execute(string key) => Get(key).Run();

    public static void Shutdown() =>
        Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { UseShellExecute = false });

    public static void Lock() => LockWorkStation();

    public static void DisplayOff()
    {
        const int HWND_BROADCAST = 0xFFFF;
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MONITORPOWER = 0xF170;
        SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2);
    }

    public static void PauseMedia()
    {
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, 0);
        keybd_event(VK_MEDIA_PLAY_PAUSE, 0, KEYEVENTF_KEYUP, 0);
    }

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(int hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
}

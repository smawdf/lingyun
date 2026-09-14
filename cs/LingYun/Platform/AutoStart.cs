using Microsoft.Win32;

namespace LingYun.Platform;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "灵云";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }

    public static void Set(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;
        if (enable)
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
                key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            try { key.DeleteValue(ValueName); }
            catch { /* ignore */ }
        }
    }
}

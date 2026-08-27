using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace MusicPlayer.Services;

/// <summary>
/// Manages Windows auto-start registration via the HKCU Run key.
/// The toggle mirrors the actual registry state (registry is the source of truth).
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MusicPlayer";

    /// <summary>
    /// Returns true if the current exe is registered for auto-start.
    /// </summary>
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enables or disables auto-start by writing/removing the HKCU Run value.
    /// </summary>
    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch
        {
            // best-effort: non-fatal if registry is inaccessible
        }
    }
}

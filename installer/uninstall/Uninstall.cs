// MusicPlayer 卸载启动器 (Uninstall launcher).
//
// A tiny zero-dependency Winexe dropped next to MusicPlayer.exe by the MSI.
// The product code of an MSI changes on every build (WiX auto-generates it),
// but the UpgradeCode is fixed — so we resolve the installed product through
// MsiEnumRelatedProducts and hand off to msiexec. Compiled by build_msi.ps1
// with the .NET Framework csc that ships with every Windows install:
//
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
//       /target:winexe /out:Uninstall.exe installer/uninstall/Uninstall.cs
//
// Keep the source C# 5 compatible — that compiler will never be upgraded.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicPlayer.Uninstaller
{
    internal static class Program
    {
        // Must match the UpgradeCode in installer/installer.wxs.
        private const string UpgradeCode = "{6F3E8C2A-1B4D-4E7A-9C5B-2D8E0F1A3B4C}";

        private const uint ErrorSuccess = 0;
        private const int Idyes = 6;

        private const uint MbIconwarning = 0x30;
        private const uint MbIconquestionYesNo = 0x124; // MB_ICONQUESTION | MB_YESNO | MB_DEFBUTTON2

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiEnumRelatedProducts(string upgradeCode, ref int index, StringBuilder productCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = HasArg(args, "/quiet") || HasArg(args, "/q") || HasArg(args, "--quiet");

            string product = FindProductCode();
            if (product == null)
            {
                if (!silent)
                    MessageBox(IntPtr.Zero, "未找到已安装的 MusicPlayer。", "卸载 MusicPlayer", MbIconwarning);
                return 1;
            }

            if (!silent && MessageBox(IntPtr.Zero,
                "确定要卸载 MusicPlayer 吗？\n\n程序文件将被移除；你的音乐、歌词和播放列表不受影响。",
                "卸载 MusicPlayer", MbIconquestionYesNo) != Idyes)
            {
                return 0;
            }

            var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            var start = new ProcessStartInfo
            {
                FileName = msiexec,
                Arguments = "/x " + product + (silent ? " /quiet /norestart" : ""),
                UseShellExecute = false
            };

            using (Process.Start(start))
            {
            }

            return 0;
        }

        /// <summary>Resolve the installed ProductCode from the fixed UpgradeCode.</summary>
        private static string FindProductCode()
        {
            int index = 0;
            var buf = new StringBuilder(39); // a GUID with braces is exactly 38 chars + NUL
            if (MsiEnumRelatedProducts(UpgradeCode, ref index, buf) == ErrorSuccess)
                return buf.ToString();
            return null;
        }

        private static bool HasArg(string[] args, string arg)
        {
            foreach (var a in args)
            {
                if (string.Equals(a, arg, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}

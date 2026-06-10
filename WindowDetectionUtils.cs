using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WhisperTyper
{
    public static class WindowDetectionUtils
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static string GetActiveProcessName()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Unknown";

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "Unknown";

            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch
            {
                return "Unknown";
            }
        }

        public static string GetActiveProcessFileName()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Unknown";

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "Unknown";

            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.MainModule?.FileName ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }
    }
}

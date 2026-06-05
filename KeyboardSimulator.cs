using System;
using System.Runtime.InteropServices;

namespace WhisperTyper
{
    public static class KeyboardSimulator
    {
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct INPUT
        {
            [FieldOffset(0)]
            public int type;

            [FieldOffset(8)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static void SimulateTypeString(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            INPUT[] inputs = new INPUT[text.Length * 2];
            int cbSize = Marshal.SizeOf(typeof(INPUT));

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // Key Down
                inputs[i * 2] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                };

                // Key Up
                inputs[i * 2 + 1] = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                };
            }

            uint result = SendInput((uint)inputs.Length, inputs, cbSize);
            if (result == 0)
            {
                int error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"SendInput failed with Win32 error code: {error}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"SendInput successfully sent {result} keyboard events.");
            }
        }
    }
}

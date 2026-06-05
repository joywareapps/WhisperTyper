using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace WhisperTyper
{
    public class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private LowLevelKeyboardProc _proc;
        private IntPtr _hookId = IntPtr.Zero;
        private int _hotkeyVirtualCode;
        private bool _isKeyPressed = false;
        private bool _swallowHotkey = true;

        public event Action<bool>? HotkeyStateChanged;

        public int HotkeyVirtualCode
        {
            get => _hotkeyVirtualCode;
            set
            {
                _hotkeyVirtualCode = value;
                _isKeyPressed = false;
            }
        }

        public bool SwallowHotkey
        {
            get => _swallowHotkey;
            set => _swallowHotkey = value;
        }

        public bool IsInstalled => _hookId != IntPtr.Zero;

        public GlobalKeyboardHook(int virtualCode = 0x14) // Default is Caps Lock (0x14)
        {
            _hotkeyVirtualCode = virtualCode;
            _proc = HookCallback;
            InstallHook();
        }

        private void InstallHook()
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                KBDLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                if (hookStruct.vkCode == _hotkeyVirtualCode)
                {
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                    {
                        if (!_isKeyPressed)
                        {
                            _isKeyPressed = true;
                            HotkeyStateChanged?.Invoke(true);
                        }

                        // Optionally swallow hotkey (e.g. stop Caps Lock from toggling or Alt from triggering menu)
                        if (_swallowHotkey)
                        {
                            return (IntPtr)1; // Swallow event
                        }
                    }
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                    {
                        if (_isKeyPressed)
                        {
                            _isKeyPressed = false;
                            HotkeyStateChanged?.Invoke(false);
                        }

                        if (_swallowHotkey)
                        {
                            return (IntPtr)1; // Swallow event
                        }
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }
}

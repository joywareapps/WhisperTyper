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
        private int _modifierVirtualCode; // 0 = no modifier required
        private bool _isKeyPressed = false;
        private bool _isModifierHeld = false;
        private bool _swallowHotkey = true;

        public event Action<bool>? HotkeyStateChanged;

        public int HotkeyVirtualCode
        {
            get => _hotkeyVirtualCode;
            set { _hotkeyVirtualCode = value; _isKeyPressed = false; }
        }

        // Set to a VK code to require that modifier held while pressing the hotkey (0 = none).
        // For Ctrl use 0x11 (tracks both Left and Right Ctrl via GetAsyncKeyState).
        public int ModifierVirtualCode
        {
            get => _modifierVirtualCode;
            set { _modifierVirtualCode = value; _isKeyPressed = false; _isModifierHeld = false; }
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
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp   = msg == WM_KEYUP   || msg == WM_SYSKEYUP;

                // Track modifier state (match both Left and Right variants of Ctrl/Shift/Alt).
                if (_modifierVirtualCode != 0 && IsModifierKey(hookStruct.vkCode, _modifierVirtualCode))
                {
                    if (isDown) _isModifierHeld = true;
                    if (isUp)
                    {
                        _isModifierHeld = false;
                        // If modifier released while primary was held, fire key-up.
                        if (_isKeyPressed)
                        {
                            _isKeyPressed = false;
                            HotkeyStateChanged?.Invoke(false);
                        }
                    }
                }

                if (hookStruct.vkCode == _hotkeyVirtualCode)
                {
                    bool modOk = _modifierVirtualCode == 0 || _isModifierHeld;

                    if (isDown && modOk)
                    {
                        if (!_isKeyPressed)
                        {
                            _isKeyPressed = true;
                            HotkeyStateChanged?.Invoke(true);
                        }
                        if (_swallowHotkey) return (IntPtr)1;
                    }
                    else if (isUp && _isKeyPressed)
                    {
                        _isKeyPressed = false;
                        HotkeyStateChanged?.Invoke(false);
                        if (_swallowHotkey) return (IntPtr)1;
                    }
                    else if (isDown && _swallowHotkey && modOk)
                    {
                        return (IntPtr)1; // swallow auto-repeat
                    }
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // Returns true if vkCode matches the requested modifier, including Left/Right variants.
        private static bool IsModifierKey(int vkCode, int modifierVk)
        {
            if (vkCode == modifierVk) return true;
            return modifierVk switch
            {
                0x11 => vkCode == 0xA2 || vkCode == 0xA3, // VK_CONTROL → L/R Ctrl
                0x10 => vkCode == 0xA0 || vkCode == 0xA1, // VK_SHIFT   → L/R Shift
                0x12 => vkCode == 0xA4 || vkCode == 0xA5, // VK_MENU    → L/R Alt
                _ => false
            };
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

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace WhisperTyper
{
    public class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL    = 14;
        private const int WM_KEYDOWN     = 0x0100;
        private const int WM_KEYUP       = 0x0101;
        private const int WM_SYSKEYDOWN  = 0x0104;
        private const int WM_SYSKEYUP    = 0x0105;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP   = 0x0208;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP   = 0x020C;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
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
        private LowLevelKeyboardProc _mouseProc;
        private IntPtr _hookId      = IntPtr.Zero;
        private IntPtr _mouseHookId = IntPtr.Zero;
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
            _proc      = HookCallback;
            _mouseProc = MouseHookCallback;
            InstallHook();
        }

        private void InstallHook()
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!)
            {
                IntPtr mod = GetModuleHandle(curModule.ModuleName);
                _hookId      = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,      mod, 0);
                _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL,    _mouseProc, mod, 0);
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

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                MSLLHOOKSTRUCT ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                int vk = 0;
                bool isDown = false, isUp = false;

                if      (msg == WM_MBUTTONDOWN) { vk = 0x04; isDown = true; }
                else if (msg == WM_MBUTTONUP)   { vk = 0x04; isUp   = true; }
                else if (msg == WM_XBUTTONDOWN) { vk = ((ms.mouseData >> 16) & 0xFFFF) == 2 ? 0x06 : 0x05; isDown = true; }
                else if (msg == WM_XBUTTONUP)   { vk = ((ms.mouseData >> 16) & 0xFFFF) == 2 ? 0x06 : 0x05; isUp   = true; }

                if (vk != 0 && vk == _hotkeyVirtualCode)
                {
                    bool modOk = _modifierVirtualCode == 0 || _isModifierHeld;

                    if (isDown && modOk && !_isKeyPressed)
                    {
                        _isKeyPressed = true;
                        HotkeyStateChanged?.Invoke(true);
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
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
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
            if (_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
        }
    }
}

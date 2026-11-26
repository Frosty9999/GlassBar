using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GlassBar
{
    /// <summary>
    /// Win+1..9 and Win+0 open or switch to the dock's apps, like the Windows taskbar; Shift+Win+number opens a
    /// new window. Explorer keeps these shortcuts registered even while its taskbar is hidden (so RegisterHotKey
    /// fails), and would act on its hidden taskbar's apps. A low-level keyboard hook sees the keys first and keeps
    /// them from Explorer. Win+Ctrl/Alt+number (Explorer's other variants) are left alone.
    /// </summary>
    internal sealed class DockHotkeys : IDisposable
    {
        private readonly Action<int, bool> _activate;
        private readonly HookProc _proc; // kept in a field so it is not garbage-collected
        private readonly HashSet<int> _swallowed = new HashSet<int>();
        private IntPtr _hook;

        /// <param name="activate">Called on the UI thread with the number (0-9) and whether Shift was held.</param>
        public DockHotkeys(Action<int, bool> activate)
        {
            _activate = activate;
            _proc = HookCallback;
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
            Sync();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.DockWinNumberKeys) || e.PropertyName == nameof(Settings.DockEnabled))
            {
                Sync();
            }
        }

        /// <summary>The hook is only installed while the shortcuts are on, so it costs nothing otherwise.</summary>
        private void Sync()
        {
            bool wanted = Settings.Instance.DockWinNumberKeys && Settings.Instance.DockEnabled;
            if (wanted && _hook == IntPtr.Zero)
            {
                using (Process self = Process.GetCurrentProcess())
                {
                    _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(self.MainModule?.ModuleName), 0);
                }
            }
            else if (!wanted && _hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                _swallowed.Clear();
            }
        }

        private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
        {
            // Keep this fast: Windows drops hooks that hold up the keyboard.
            if (code >= 0)
            {
                KBDLLHOOKSTRUCT key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(data);
                int vk = (int)key.vkCode;
                bool injected = (key.flags & LLKHF_INJECTED) != 0;

                if (!injected && vk >= VK_0 && vk <= VK_9)
                {
                    int msg = message.ToInt32();
                    bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    if (up && _swallowed.Remove(vk))
                    {
                        return (IntPtr)1; // the matching key-up of a shortcut we handled
                    }

                    if (down && IsDown(VK_LWIN, VK_RWIN) && !IsDown(VK_CONTROL) && !IsDown(VK_MENU))
                    {
                        // Held down: Windows repeats key-downs. Act on the first one only.
                        if (_swallowed.Add(vk))
                        {
                            bool shift = IsDown(VK_SHIFT);
                            int number = vk - VK_0;
                            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => _activate(number, shift)));
                        }

                        // Windows opens Start when the Win key is released "alone". Since the number never
                        // reaches Windows, tap an unused key so the Win press counts as a shortcut.
                        SendMaskKey();
                        return (IntPtr)1;
                    }
                }
            }

            return CallNextHookEx(_hook, code, message, data);
        }

        private static bool IsDown(params int[] keys)
        {
            foreach (int k in keys)
            {
                if ((GetAsyncKeyState(k) & 0x8000) != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void SendMaskKey()
        {
            INPUT[] inputs =
            {
                new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_MASK } },
                new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_MASK, dwFlags = KEYEVENTF_KEYUP } }
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public void Dispose()
        {
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        #region Interop

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int LLKHF_INJECTED = 0x10;
        private const int VK_0 = 0x30, VK_9 = 0x39;
        private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        private const ushort VK_MASK = 0xE8; // unassigned virtual key, the usual choice for this
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x2;

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public int flags;
            public uint time;
            public IntPtr extraInfo;
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

        // INPUT is a union; the mouse variant is the largest (32 bytes on 64-bit vs 24 for the keyboard one),
        // so pad to that size: SendInput rejects a wrong size. 40 bytes in all.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public KEYBDINPUT ki;
            private readonly long padding;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        #endregion
    }
}

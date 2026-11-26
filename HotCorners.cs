using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using Forms = System.Windows.Forms;

namespace GlassBar
{
    /// <summary>What happens when the mouse rests in a screen corner.</summary>
    public enum HotCornerAction
    {
        None,
        ShowDesktop,
        TaskView,
        StartMenu,
        Notifications,
        QuickSettings,
        LockScreen,
        TurnOffDisplay
    }

    /// <summary>
    /// Hot corners: push the mouse into a corner of any monitor and let it rest there for a moment to run that
    /// corner's action. Ignored while a mouse button is held (dragging) and while a full-screen game or video is
    /// on that monitor. Fires once per visit; move out of the corner to use it again.
    /// </summary>
    internal sealed class HotCorners : IDisposable
    {
        private const int CornerSize = 3;        // pixels from each edge that count as "in the corner"
        private const int DwellMilliseconds = 250;

        private readonly ShellManager _shell;
        private readonly DispatcherTimer _timer;
        private string _corner;                  // which corner the mouse is in now (screen + position), or null
        private DateTime _enteredAt;
        private bool _fired;

        public HotCorners(ShellManager shell)
        {
            _shell = shell;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (s, e) => Check();
            Settings.Instance.PropertyChanged += Settings_PropertyChanged;
            Sync();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != null && e.PropertyName.StartsWith("HotCorner", StringComparison.Ordinal))
            {
                Sync();
            }
        }

        /// <summary>The mouse is only watched while at least one corner has an action.</summary>
        private void Sync()
        {
            Settings s = Settings.Instance;
            bool any = s.HotCornerTopLeft != HotCornerAction.None || s.HotCornerTopRight != HotCornerAction.None ||
                       s.HotCornerBottomLeft != HotCornerAction.None || s.HotCornerBottomRight != HotCornerAction.None;
            if (any)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _corner = null;
            }
        }

        private void Check()
        {
            System.Drawing.Point p = Forms.Cursor.Position;
            Forms.Screen screen = Forms.Screen.FromPoint(p);
            System.Drawing.Rectangle b = screen.Bounds;

            bool left = p.X < b.Left + CornerSize;
            bool right = p.X >= b.Right - CornerSize;
            bool top = p.Y < b.Top + CornerSize;
            bool bottom = p.Y >= b.Bottom - CornerSize;

            HotCornerAction action = HotCornerAction.None;
            string corner = null;
            Settings s = Settings.Instance;
            if (top && left) { action = s.HotCornerTopLeft; corner = "TL"; }
            else if (top && right) { action = s.HotCornerTopRight; corner = "TR"; }
            else if (bottom && left) { action = s.HotCornerBottomLeft; corner = "BL"; }
            else if (bottom && right) { action = s.HotCornerBottomRight; corner = "BR"; }

            if (corner == null || action == HotCornerAction.None)
            {
                _corner = null;
                return;
            }

            corner = screen.DeviceName + corner;
            if (corner != _corner)
            {
                _corner = corner;
                _enteredAt = DateTime.Now;
                _fired = false;
                return;
            }

            if (_fired || (DateTime.Now - _enteredAt).TotalMilliseconds < DwellMilliseconds)
            {
                return;
            }

            if (ButtonHeld() || FullScreenOn(screen))
            {
                return;
            }

            _fired = true;
            Run(action);
        }

        private static bool ButtonHeld()
        {
            return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 ||
                   (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;
        }

        /// <summary>Games and full-screen videos often put the mouse in a corner; leave them alone.</summary>
        private bool FullScreenOn(Forms.Screen screen)
        {
            foreach (FullScreenApp app in _shell.FullScreenHelper.FullScreenApps)
            {
                if (app.screen != null && (app.screen.DeviceName == screen.DeviceName || app.screen.IsVirtualScreen))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Run(HotCornerAction action)
        {
            try
            {
                switch (action)
                {
                    case HotCornerAction.ShowDesktop:
                        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                        shell.ToggleDesktop();
                        break;
                    case HotCornerAction.TaskView:
                        PressWinWith(VK_TAB);
                        break;
                    case HotCornerAction.StartMenu:
                        ShellHelper.ShowStartMenu();
                        break;
                    case HotCornerAction.Notifications:
                        ShellHelper.ShowNotificationCenter();
                        break;
                    case HotCornerAction.QuickSettings:
                        ShellHelper.ShowActionCenter();
                        break;
                    case HotCornerAction.LockScreen:
                        LockWorkStation();
                        break;
                    case HotCornerAction.TurnOffDisplay:
                        PostMessage(HWND_BROADCAST, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);
                        break;
                }
            }
            catch (Exception)
            {
                // An action that can't run just does nothing.
            }
        }

        private static void PressWinWith(ushort key)
        {
            INPUT[] inputs =
            {
                Key(VK_LWIN, 0), Key(key, 0), Key(key, KEYEVENTF_KEYUP), Key(VK_LWIN, KEYEVENTF_KEYUP)
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT Key(ushort vk, uint flags)
        {
            return new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } };
        }

        public void Dispose()
        {
            _timer.Stop();
            Settings.Instance.PropertyChanged -= Settings_PropertyChanged;
        }

        #region Interop

        private const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;
        private const ushort VK_TAB = 0x09, VK_LWIN = 0x5B;
        private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;
        private const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;
        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xFFFF;

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // 40 bytes on 64-bit: the union is as large as MOUSEINPUT (32), so pad the keyboard variant (24).
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public KEYBDINPUT ki;
            private readonly long padding;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        #endregion
    }
}

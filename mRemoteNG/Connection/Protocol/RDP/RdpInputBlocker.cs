using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace mRemoteNG.Connection.Protocol.RDP
{
    internal sealed class PassiveRdpInputBlocker : IMessageFilter
    {
        private readonly Dictionary<Control, BlockedControl> _blockedControls = new Dictionary<Control, BlockedControl>();

        private const int MA_NOACTIVATEANDEAT = 4;

        private const int WM_SETFOCUS = 0x0007;
        private const int WM_SETCURSOR = 0x0020;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_INPUT = 0x00FF;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_CHAR = 0x0102;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_RBUTTONDBLCLK = 0x0206;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int WM_MBUTTONUP = 0x0208;
        private const int WM_MBUTTONDBLCLK = 0x0209;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const int WM_XBUTTONDBLCLK = 0x020D;
        private const int WM_MOUSEHWHEEL = 0x020E;

        [DllImport("user32.dll")]
        private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        public int SetBlocked(Control control, bool blocked)
        {
            if (control == null)
                return 0;

            if (blocked)
            {
                return Block(control);
            }
            else
            {
                return Unblock(control);
            }
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (!IsInputMessage(m.Msg))
                return false;

            foreach (var blockedControl in new List<BlockedControl>(_blockedControls.Values))
            {
                if (blockedControl.IsDisposed)
                {
                    Unblock(blockedControl.Control);
                    continue;
                }

                if (blockedControl.IsMessageForBlockedControl(m.HWnd))
                    return true;
            }

            return false;
        }

        private int Block(Control control)
        {
            if (!_blockedControls.TryGetValue(control, out var blockedControl))
            {
                blockedControl = new BlockedControl(control);
                _blockedControls.Add(control, blockedControl);

                control.HandleCreated += BlockedControlOnHandleCreated;
                control.Disposed += BlockedControlOnDisposed;

                if (_blockedControls.Count == 1)
                    Application.AddMessageFilter(this);
            }

            blockedControl.RefreshSubclassedWindows();
            blockedControl.ScheduleRefreshRetries();
            return blockedControl.SubclassedWindowCount;
        }

        private int Unblock(Control control)
        {
            if (!_blockedControls.TryGetValue(control, out var blockedControl))
                return 0;

            control.HandleCreated -= BlockedControlOnHandleCreated;
            control.Disposed -= BlockedControlOnDisposed;

            var subclassedWindowCount = blockedControl.SubclassedWindowCount;
            blockedControl.Dispose();
            _blockedControls.Remove(control);

            if (_blockedControls.Count == 0)
                Application.RemoveMessageFilter(this);

            return subclassedWindowCount;
        }

        private void BlockedControlOnHandleCreated(object sender, EventArgs e)
        {
            if (sender is Control control && _blockedControls.TryGetValue(control, out var blockedControl))
            {
                blockedControl.RefreshSubclassedWindows();
                blockedControl.ScheduleRefreshRetries();
            }
        }

        private void BlockedControlOnDisposed(object sender, EventArgs e)
        {
            if (sender is Control control)
                Unblock(control);
        }

        private static bool IsInputMessage(int msg)
        {
            switch (msg)
            {
                case WM_SETFOCUS:
                case WM_SETCURSOR:
                case WM_MOUSEACTIVATE:
                case WM_INPUT:
                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_CHAR:
                case WM_SYSKEYDOWN:
                case WM_SYSKEYUP:
                case WM_MOUSEMOVE:
                case WM_LBUTTONDOWN:
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                case WM_RBUTTONDOWN:
                case WM_RBUTTONUP:
                case WM_RBUTTONDBLCLK:
                case WM_MBUTTONDOWN:
                case WM_MBUTTONUP:
                case WM_MBUTTONDBLCLK:
                case WM_MOUSEWHEEL:
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                case WM_XBUTTONDBLCLK:
                case WM_MOUSEHWHEEL:
                    return true;
                default:
                    return false;
            }
        }

        private sealed class BlockedControl : IDisposable
        {
            private readonly Dictionary<IntPtr, BlockedWindow> _subclassedWindows = new Dictionary<IntPtr, BlockedWindow>();
            private Timer _refreshTimer;
            private int _refreshAttempts;

            public BlockedControl(Control control)
            {
                Control = control;
            }

            public Control Control { get; }

            public bool IsDisposed => Control == null || Control.IsDisposed;

            public int SubclassedWindowCount => _subclassedWindows.Count;

            public void RefreshSubclassedWindows()
            {
                if (IsDisposed || !Control.IsHandleCreated)
                {
                    ReleaseSubclassedWindows();
                    return;
                }

                var handles = GetRdpWindowHandles(Control);
                foreach (var handle in new List<IntPtr>(_subclassedWindows.Keys))
                {
                    if (!handles.Contains(handle))
                    {
                        _subclassedWindows[handle].Dispose();
                        _subclassedWindows.Remove(handle);
                    }
                }

                foreach (var handle in handles)
                {
                    if (_subclassedWindows.ContainsKey(handle))
                        continue;

                    try
                    {
                        _subclassedWindows.Add(handle, new BlockedWindow(handle));
                    }
                    catch
                    {
                        // IMessageFilter still protects this RDP control if a child HWND cannot be subclassed.
                    }
                }
            }

            public void ScheduleRefreshRetries()
            {
                if (IsDisposed)
                    return;

                _refreshAttempts = 0;

                if (_refreshTimer == null)
                {
                    _refreshTimer = new Timer { Interval = 200 };
                    _refreshTimer.Tick += RefreshTimerOnTick;
                }

                _refreshTimer.Stop();
                _refreshTimer.Start();
            }

            public bool IsMessageForBlockedControl(IntPtr hWnd)
            {
                if (hWnd == IntPtr.Zero || IsDisposed || !Control.IsHandleCreated)
                    return false;

                return Control.Handle == hWnd || IsChild(Control.Handle, hWnd);
            }

            public void Dispose()
            {
                if (_refreshTimer != null)
                {
                    _refreshTimer.Stop();
                    _refreshTimer.Tick -= RefreshTimerOnTick;
                    _refreshTimer.Dispose();
                    _refreshTimer = null;
                }

                ReleaseSubclassedWindows();
            }

            private void RefreshTimerOnTick(object sender, EventArgs e)
            {
                _refreshAttempts++;
                RefreshSubclassedWindows();

                if (_refreshAttempts < 8)
                    return;

                _refreshTimer.Stop();
            }

            private void ReleaseSubclassedWindows()
            {
                foreach (var blockedWindow in _subclassedWindows.Values)
                    blockedWindow.Dispose();

                _subclassedWindows.Clear();
            }

            private static List<IntPtr> GetRdpWindowHandles(Control control)
            {
                var handles = new List<IntPtr>();
                AddHandle(handles, control.Handle);

                EnumChildWindows(control.Handle, (hWnd, lParam) =>
                {
                    AddHandle(handles, hWnd);
                    return true;
                }, IntPtr.Zero);

                return handles;
            }

            private static void AddHandle(ICollection<IntPtr> handles, IntPtr handle)
            {
                if (handle != IntPtr.Zero && !handles.Contains(handle))
                    handles.Add(handle);
            }
        }

        private sealed class BlockedWindow : NativeWindow, IDisposable
        {
            public BlockedWindow(IntPtr handle)
            {
                AssignHandle(handle);
            }

            public void Dispose()
            {
                ReleaseHandle();
            }

            protected override void WndProc(ref Message m)
            {
                if (IsInputMessage(m.Msg))
                {
                    m.Result = m.Msg == WM_MOUSEACTIVATE ? new IntPtr(MA_NOACTIVATEANDEAT) : IntPtr.Zero;
                    return;
                }

                base.WndProc(ref m);
            }
        }
    }
}

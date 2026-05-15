$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$rdp6 = Join-Path $root 'mRemoteNG\Connection\Protocol\RDP\RdpProtocol6.cs'
$rdp8 = Join-Path $root 'mRemoteNG\Connection\Protocol\RDP\RdpProtocol8.cs'

function Read-Text($path) {
    [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8).Replace("`r`n", "`n")
}

function Write-Text($path, $text) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $text.Replace("`n", "`r`n"), $utf8NoBom)
}

function Replace-Once($text, $old, $new, $name) {
    if ($text.Contains($new)) { return $text }
    if (!$text.Contains($old)) { throw "Could not find patch anchor: $name" }
    return $text.Replace($old, $new)
}

$text = Read-Text $rdp6

$text = Replace-Once $text 'private AxHost AxHost => (AxHost)Control;' 'private AxHost AxHost => (AxHost)Control; private static readonly PassiveRdpInputBlocker InputBlocker = new PassiveRdpInputBlocker(); private bool _viewOnly; private bool _userDisabledViewOnlyInFullscreen; private bool _automaticReconnectInProgress;' 'RdpProtocol6 fields'

$text = Replace-Once $text 'public virtual bool Fullscreen { get => _rdpClient.FullScreen; protected set => _rdpClient.FullScreen = value; }' 'public virtual bool Fullscreen { get => _rdpClient.FullScreen; protected set { _rdpClient.FullScreen = value; ApplyFullscreenViewOnlyPolicy(); } }' 'Fullscreen property'

$text = Replace-Once $text 'public bool ViewOnly { get => !AxHost.Enabled; set => AxHost.Enabled = !value; }' 'public bool ViewOnly { get => _viewOnly; set => SetViewOnly(value); }' 'ViewOnly property'

$text = Replace-Once $text '_rdpClient.AdvancedSettings2.GrabFocusOnConnect = true;' '_rdpClient.AdvancedSettings2.GrabFocusOnConnect = false;' 'GrabFocusOnConnect'

$text = Replace-Once $text 'SetRdGateway(); ViewOnly = Force.HasFlag(ConnectionInfo.Force.ViewOnly); _rdpClient.ColorDepth = (int)connectionInfo.Colors;' 'SetRdGateway(); ApplyFullscreenViewOnlyPolicy(); _rdpClient.ColorDepth = (int)connectionInfo.Colors;' 'Force ViewOnly setup'

$text = Replace-Once $text 'public void ToggleViewOnly() { try { ViewOnly = !ViewOnly; } catch { Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, $"Could not toggle view only mode for host {connectionInfo.Hostname}"); } }' 'public void ToggleViewOnly() { try { if (!Fullscreen) { _userDisabledViewOnlyInFullscreen = false; SetViewOnly(false); return; } var enabled = !ViewOnly; _userDisabledViewOnlyInFullscreen = !enabled; SetViewOnly(enabled); } catch { Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, $"Could not toggle view only mode for host {connectionInfo.Hostname}"); } }' 'ToggleViewOnly'

$text = Replace-Once $text 'public override void Focus() { try { if (Control.ContainsFocus == false) { Control.Focus(); } } catch (Exception ex) { Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpFocusFailed, ex); } }' 'public override void Focus() { try { if (ViewOnly || _automaticReconnectInProgress) return; if (Control.ContainsFocus == false) { Control.Focus(); } } catch (Exception ex) { Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpFocusFailed, ex); } }' 'Focus suppression'

$helpers = 'private void SetViewOnly(bool value) { if (!Fullscreen) value = false; _viewOnly = value; InputBlocker.SetBlocked(Control, _viewOnly); } private void ApplyFullscreenViewOnlyPolicy() { if (Control == null || _rdpClient == null) return; if (Fullscreen) { if (!_userDisabledViewOnlyInFullscreen) SetViewOnly(true); } else { _userDisabledViewOnlyInFullscreen = false; SetViewOnly(false); } ScrollToLowerRightAsync(); } protected void ScrollToLowerRightAsync() { try { if (Control == null || Control.IsDisposed || !Control.IsHandleCreated) return; Control.BeginInvoke(new Action(() => { ScrollToLowerRight(); var timer = new System.Windows.Forms.Timer { Interval = 300 }; timer.Tick += (sender, args) => { timer.Stop(); timer.Dispose(); ScrollToLowerRight(); }; timer.Start(); })); } catch { } } private void ScrollToLowerRight() { try { var parent = Control == null ? null : Control.Parent; while (parent != null) { var scrollable = parent as ScrollableControl; if (scrollable != null) { scrollable.AutoScrollPosition = new System.Drawing.Point(scrollable.HorizontalScroll.Maximum, scrollable.VerticalScroll.Maximum); return; } parent = parent.Parent; } } catch { } } '
$text = Replace-Once $text 'private void SetRdpClientProperties()' ($helpers + 'private void SetRdpClientProperties()') 'Passive helper methods'

$text = Replace-Once $text 'private void RDPEvent_OnConnected() { Event_Connected(this); }' 'private void RDPEvent_OnConnected() { Event_Connected(this); ScrollToLowerRightAsync(); }' 'OnConnected scroll'

$text = Replace-Once $text 'private void RDPEvent_OnLoginComplete() { loginComplete = true; }' 'private void RDPEvent_OnLoginComplete() { loginComplete = true; _automaticReconnectInProgress = false; ApplyFullscreenViewOnlyPolicy(); ScrollToLowerRightAsync(); }' 'OnLoginComplete policy'

$text = Replace-Once $text 'private void RDPEvent_OnLeaveFullscreenMode() { Fullscreen = false; _leaveFullscreenEvent?.Invoke(this, new EventArgs()); }' 'private void RDPEvent_OnLeaveFullscreenMode() { Fullscreen = false; SetViewOnly(false); _leaveFullscreenEvent?.Invoke(this, new EventArgs()); }' 'OnLeaveFullscreenMode'

$text = Replace-Once $text 'private void RdpClient_GotFocus(object sender, EventArgs e) { ((ConnectionTab)Control.Parent.Parent).Focus(); }' 'private void RdpClient_GotFocus(object sender, EventArgs e) { if (ViewOnly || _automaticReconnectInProgress) return; ((ConnectionTab)Control.Parent.Parent).Focus(); }' 'RdpClient_GotFocus'

$blocker = 'private sealed class PassiveRdpInputBlocker : IMessageFilter { private readonly System.Collections.Generic.List<Control> _blockedControls = new System.Collections.Generic.List<Control>(); private const int WM_KEYDOWN = 0x0100; private const int WM_KEYUP = 0x0101; private const int WM_CHAR = 0x0102; private const int WM_SYSKEYDOWN = 0x0104; private const int WM_SYSKEYUP = 0x0105; private const int WM_MOUSEMOVE = 0x0200; private const int WM_LBUTTONDOWN = 0x0201; private const int WM_LBUTTONUP = 0x0202; private const int WM_LBUTTONDBLCLK = 0x0203; private const int WM_RBUTTONDOWN = 0x0204; private const int WM_RBUTTONUP = 0x0205; private const int WM_RBUTTONDBLCLK = 0x0206; private const int WM_MBUTTONDOWN = 0x0207; private const int WM_MBUTTONUP = 0x0208; private const int WM_MBUTTONDBLCLK = 0x0209; private const int WM_MOUSEWHEEL = 0x020A; private const int WM_XBUTTONDOWN = 0x020B; private const int WM_XBUTTONUP = 0x020C; private const int WM_XBUTTONDBLCLK = 0x020D; private const int WM_MOUSEHWHEEL = 0x020E; private const int WM_MOUSEACTIVATE = 0x0021; private const int WM_INPUT = 0x00FF; [DllImport("user32.dll")] private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd); public void SetBlocked(Control control, bool blocked) { if (control == null) return; if (blocked) { if (!_blockedControls.Contains(control)) { _blockedControls.Add(control); if (_blockedControls.Count == 1) Application.AddMessageFilter(this); } } else { _blockedControls.Remove(control); if (_blockedControls.Count == 0) Application.RemoveMessageFilter(this); } } public bool PreFilterMessage(ref Message m) { if (!IsInputMessage(m.Msg)) return false; foreach (Control control in _blockedControls.ToArray()) { if (IsMessageForControl(control, m.HWnd)) return true; } return false; } private static bool IsInputMessage(int msg) { switch (msg) { case WM_KEYDOWN: case WM_KEYUP: case WM_CHAR: case WM_SYSKEYDOWN: case WM_SYSKEYUP: case WM_MOUSEMOVE: case WM_LBUTTONDOWN: case WM_LBUTTONUP: case WM_LBUTTONDBLCLK: case WM_RBUTTONDOWN: case WM_RBUTTONUP: case WM_RBUTTONDBLCLK: case WM_MBUTTONDOWN: case WM_MBUTTONUP: case WM_MBUTTONDBLCLK: case WM_MOUSEWHEEL: case WM_XBUTTONDOWN: case WM_XBUTTONUP: case WM_XBUTTONDBLCLK: case WM_MOUSEHWHEEL: case WM_MOUSEACTIVATE: case WM_INPUT: return true; default: return false; } } private static bool IsMessageForControl(Control control, IntPtr hWnd) { if (control == null || control.IsDisposed || !control.IsHandleCreated) return false; return control.Handle == hWnd || IsChild(control.Handle, hWnd); } } '
$text = Replace-Once $text '#endregion #region Enums public enum Defaults' ('#endregion ' + $blocker + '#region Enums public enum Defaults') 'PassiveRdpInputBlocker'

$text = Replace-Once $text 'ReconnectGroup.DisposeReconnectGroup(); //SetProps() _rdpClient.Connect();' 'ReconnectGroup.DisposeReconnectGroup(); //SetProps() _automaticReconnectInProgress = true; ApplyFullscreenViewOnlyPolicy(); _rdpClient.Connect();' 'automatic reconnect flag'

Write-Text $rdp6 $text

$text = Read-Text $rdp8

$text = Replace-Once $text 'RdpClient8.Reconnect((uint)size.Width, (uint)size.Height);' 'RdpClient8.Reconnect((uint)size.Width, (uint)size.Height); ScrollToLowerRightAsync();' 'RdpProtocol8 reconnect scroll'

$text = Replace-Once $text '_controlBeginningSize = Size.Empty;' '_controlBeginningSize = Size.Empty; ScrollToLowerRightAsync();' 'RdpProtocol8 resize scroll'

Write-Text $rdp8 $text

Write-Host 'Passive RDP monitor patch for mRemoteNG 1.77.2-release has been applied.'

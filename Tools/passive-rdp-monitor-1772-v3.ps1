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

function Replace-Required($text, $old, $new, $name) {
    if ($text.Contains($new)) { return $text }
    if (!$text.Contains($old)) { throw "Could not find patch anchor: $name" }
    return $text.Replace($old, $new)
}

function Replace-Optional($text, $old, $new) {
    if ($text.Contains($new)) { return $text }
    if ($text.Contains($old)) { return $text.Replace($old, $new) }
    return $text
}

if (!(Test-Path $rdp6)) { throw "RdpProtocol6.cs not found: $rdp6" }
if (!(Test-Path $rdp8)) { throw "RdpProtocol8.cs not found: $rdp8" }

$text = Read-Text $rdp6

$text = Replace-Required $text 'private AxHost AxHost => (AxHost)Control; #region Properties' 'private AxHost AxHost => (AxHost)Control; private static readonly PassiveRdpInputBlocker InputBlocker = new PassiveRdpInputBlocker(); private bool _viewOnly; private bool _userDisabledViewOnlyInFullscreen; private bool _automaticReconnectInProgress; #region Properties' 'Rdp fields'

$text = Replace-Required $text 'public bool ViewOnly { get => !AxHost.Enabled; set => AxHost.Enabled = !value; }' 'public bool ViewOnly { get => _viewOnly; set => SetViewOnly(value); }' 'ViewOnly property'

$text = Replace-Optional $text '_rdpClient.AdvancedSettings2.GrabFocusOnConnect = true;' '_rdpClient.AdvancedSettings2.GrabFocusOnConnect = false;'

$text = Replace-Required $text 'public override bool Connect() { loginComplete = false; SetEventHandlers(); try { _rdpClient.Connect(); base.Connect(); return true; }' 'public override bool Connect() { loginComplete = false; _automaticReconnectInProgress = false; SetEventHandlers(); try { _rdpClient.Connect(); base.Connect(); return true; }' 'Connect method'

$text = Replace-Required $text 'public void ToggleFullscreen() { try { Fullscreen = !Fullscreen; } catch (Exception ex) { Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpToggleFullscreenFailed, ex); } }' 'public void ToggleFullscreen() { try { Fullscreen = !Fullscreen; ApplyFullscreenViewOnlyPolicy(); ScrollToLowerRightAsync(); } catch (Exception ex) { Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpToggleFullscreenFailed, ex); } }' 'ToggleFullscreen'

$text = Replace-Required $text 'public void ToggleViewOnly() { try { ViewOnly = !ViewOnly; } catch { Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, $"Could not toggle view only mode for host {connectionInfo.Hostname}"); } }' 'public void ToggleViewOnly() { try { if (!IsFullscreenSafe()) { _userDisabledViewOnlyInFullscreen = false; SetViewOnly(false); return; } var enabled = !ViewOnly; _userDisabledViewOnlyInFullscreen = !enabled; SetViewOnly(enabled); } catch { Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, $"Could not toggle view only mode for host {connectionInfo.Hostname}"); } }' 'ToggleViewOnly'

$text = Replace-Required $text 'public override void Focus() { try { if (Control.ContainsFocus == false) { Control.Focus(); } } catch (Exception ex) { Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpFocusFailed, ex); } }' 'public override void Focus() { try { if (ViewOnly || _automaticReconnectInProgress) return; if (Control.ContainsFocus == false) { Control.Focus(); } } catch (Exception ex) { Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpFocusFailed, ex); } }' 'Focus method'

$helperCode = 'private bool IsFullscreenSafe() { try { return Fullscreen; } catch { return false; } } private void SetViewOnly(bool value) { if (!IsFullscreenSafe()) value = false; _viewOnly = value; InputBlocker.SetBlocked(Control, _viewOnly); } private void ApplyFullscreenViewOnlyPolicy() { if (Control == null) return; if (IsFullscreenSafe()) { if (!_userDisabledViewOnlyInFullscreen) SetViewOnly(true); } else { _userDisabledViewOnlyInFullscreen = false; SetViewOnly(false); } ScrollToLowerRightAsync(); } protected void ScrollToLowerRightAsync() { try { if (Control == null || Control.IsDisposed || !Control.IsHandleCreated) return; Control.BeginInvoke(new Action(() => { ScrollToLowerRight(); var timer = new System.Windows.Forms.Timer { Interval = 300 }; timer.Tick += (sender, args) => { timer.Stop(); timer.Dispose(); ScrollToLowerRight(); }; timer.Start(); })); } catch { } } private void ScrollToLowerRight() { try { var parent = Control?.Parent; while (parent != null) { var scrollable = parent as ScrollableControl; if (scrollable != null) { scrollable.AutoScrollPosition = new System.Drawing.Point(scrollable.HorizontalScroll.Maximum, scrollable.VerticalScroll.Maximum); return; } parent = parent.Parent; } } catch { } } '
$text = Replace-Required $text 'private void SetRdpClientProperties()' ($helperCode + 'private void SetRdpClientProperties()') 'helper methods insert'

$text = Replace-Optional $text 'ViewOnly = Force.HasFlag(ConnectionInfo.Force.ViewOnly);' 'ApplyFullscreenViewOnlyPolicy();'

$text = Replace-Required $text 'private void RDPEvent_OnConnected() { Event_Connected(this); }' 'private void RDPEvent_OnConnected() { Event_Connected(this); ScrollToLowerRightAsync(); }' 'OnConnected'

$text = Replace-Required $text 'private void RDPEvent_OnLoginComplete() { loginComplete = true; }' 'private void RDPEvent_OnLoginComplete() { loginComplete = true; _automaticReconnectInProgress = false; ApplyFullscreenViewOnlyPolicy(); ScrollToLowerRightAsync(); }' 'OnLoginComplete'

$text = Replace-Required $text 'private void RDPEvent_OnLeaveFullscreenMode() { Fullscreen = false; _leaveFullscreenEvent?.Invoke(this, new EventArgs()); }' 'private void RDPEvent_OnLeaveFullscreenMode() { Fullscreen = false; SetViewOnly(false); _leaveFullscreenEvent?.Invoke(this, new EventArgs()); }' 'OnLeaveFullscreenMode'

$text = Replace-Required $text 'private void RdpClient_GotFocus(object sender, EventArgs e) { ((ConnectionTab)Control.Parent.Parent).Focus(); }' 'private void RdpClient_GotFocus(object sender, EventArgs e) { if (ViewOnly || _automaticReconnectInProgress) return; ((ConnectionTab)Control.Parent.Parent).Focus(); }' 'RdpClient_GotFocus'

$blockerCode = 'private sealed class PassiveRdpInputBlocker : IMessageFilter { private readonly System.Collections.Generic.List<Control> _blockedControls = new System.Collections.Generic.List<Control>(); private const int WM_MOUSEACTIVATE = 0x0021; private const int WM_INPUT = 0x00FF; [DllImport("user32.dll")] private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd); public void SetBlocked(Control control, bool blocked) { if (control == null) return; if (blocked) { if (!_blockedControls.Contains(control)) { _blockedControls.Add(control); if (_blockedControls.Count == 1) Application.AddMessageFilter(this); } } else { _blockedControls.Remove(control); if (_blockedControls.Count == 0) Application.RemoveMessageFilter(this); } } public bool PreFilterMessage(ref Message m) { if (!IsInputMessage(m.Msg)) return false; foreach (var control in _blockedControls.ToArray()) { if (IsMessageForControl(control, m.HWnd)) return true; } return false; } private static bool IsInputMessage(int msg) { if (msg == WM_MOUSEACTIVATE || msg == WM_INPUT) return true; if (msg >= 0x0100 && msg <= 0x0108) return true; if (msg >= 0x0200 && msg <= 0x020E) return true; return false; } private static bool IsMessageForControl(Control control, IntPtr hWnd) { if (control == null || control.IsDisposed || !control.IsHandleCreated) return false; return control.Handle == hWnd || IsChild(control.Handle, hWnd); } } '
$text = Replace-Required $text '#region Enums public enum Defaults' ($blockerCode + '#region Enums public enum Defaults') 'PassiveRdpInputBlocker'

$text = Replace-Required $text 'ReconnectGroup.DisposeReconnectGroup(); //SetProps() _rdpClient.Connect();' 'ReconnectGroup.DisposeReconnectGroup(); _automaticReconnectInProgress = true; ApplyFullscreenViewOnlyPolicy(); //SetProps() _rdpClient.Connect();' 'automatic reconnect flag'

Write-Text $rdp6 $text

$text = Read-Text $rdp8
$text = Replace-Optional $text 'RdpClient8.Reconnect((uint)size.Width, (uint)size.Height);' 'RdpClient8.Reconnect((uint)size.Width, (uint)size.Height); ScrollToLowerRightAsync();'
$text = Replace-Optional $text '_controlBeginningSize = Size.Empty;' '_controlBeginningSize = Size.Empty; ScrollToLowerRightAsync();'
Write-Text $rdp8 $text

Write-Host 'Passive RDP monitor patch for mRemoteNG 1.77.2-release has been applied successfully.'

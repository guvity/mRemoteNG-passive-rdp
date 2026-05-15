$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$rdp6 = Join-Path $root 'mRemoteNG\Connection\Protocol\RDP\RdpProtocol6.cs'
$rdp8 = Join-Path $root 'mRemoteNG\Connection\Protocol\RDP\RdpProtocol8.cs'

function Read-Text($path) {
    return [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8).Replace("`r`n", "`n")
}

function Write-Text($path, $text) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $text.Replace("`n", "`r`n"), $utf8NoBom)
}

function Replace-Exact($text, $old, $new, $name) {
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) {
        $sample = if ($text.Length -gt 500) { $text.Substring(0, 500) } else { $text }
        throw "Could not find required patch anchor [$name]. File sample: $sample"
    }
    return $text.Replace($old, $new)
}

function Replace-Optional($text, $old, $new, $name) {
    if ($text.Contains($new)) { return $text }
    if (-not $text.Contains($old)) {
        Write-Host "Optional anchor not found: $name"
        return $text
    }
    return $text.Replace($old, $new)
}

if (-not (Test-Path $rdp6)) { throw "RdpProtocol6.cs not found at $rdp6" }
$text = Read-Text $rdp6

$text = Replace-Exact $text `
'public virtual bool Fullscreen { get => _rdpClient.FullScreen; protected set => _rdpClient.FullScreen = value; }' `
'public virtual bool Fullscreen { get => _rdpClient.FullScreen; protected set { _rdpClient.FullScreen = value; PassiveRdpState.ApplyFullscreenViewOnlyPolicy(this); } }' `
'Fullscreen property'

$text = Replace-Exact $text `
'public bool ViewOnly { get => !AxHost.Enabled; set => AxHost.Enabled = !value; }' `
'public bool ViewOnly { get => PassiveRdpState.For(Control).ViewOnly; set => PassiveRdpState.SetViewOnly(this, value); }' `
'ViewOnly property'

$text = Replace-Exact $text `
'_rdpClient.AdvancedSettings2.GrabFocusOnConnect = true;' `
'_rdpClient.AdvancedSettings2.GrabFocusOnConnect = false;' `
'GrabFocusOnConnect'

$text = Replace-Exact $text `
'ViewOnly = Force.HasFlag(ConnectionInfo.Force.ViewOnly);' `
'PassiveRdpState.ApplyFullscreenViewOnlyPolicy(this);' `
'Force ViewOnly override'

$text = Replace-Exact $text `
'public void ToggleViewOnly() { try { ViewOnly = !ViewOnly; } catch { Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, $"Could not toggle view only mode for host {connectionInfo.Hostname}"); } }' `
'public void ToggleViewOnly() { try { PassiveRdpState.ToggleViewOnly(this); } catch { Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, $"Could not toggle view only mode for host {connectionInfo.Hostname}"); } }' `
'ToggleViewOnly'

$text = Replace-Exact $text `
'public override void Focus() { try { if (Control.ContainsFocus == false) { Control.Focus(); } } catch (Exception ex) { Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpFocusFailed, ex); } }' `
'public override void Focus() { try { if (PassiveRdpState.ShouldSuppressFocus(this)) return; if (Control.ContainsFocus == false) { Control.Focus(); } } catch (Exception ex) { Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpFocusFailed, ex); } }' `
'Focus method'

$text = Replace-Exact $text `
'private void RDPEvent_OnConnected() { Event_Connected(this); }' `
'private void RDPEvent_OnConnected() { Event_Connected(this); PassiveScrollToLowerRightAsync(); }' `
'OnConnected'

$text = Replace-Exact $text `
'private void RDPEvent_OnLoginComplete() { loginComplete = true; }' `
'private void RDPEvent_OnLoginComplete() { loginComplete = true; PassiveRdpState.For(Control).AutomaticReconnectInProgress = false; PassiveRdpState.ApplyFullscreenViewOnlyPolicy(this); PassiveScrollToLowerRightAsync(); }' `
'OnLoginComplete'

$text = Replace-Exact $text `
'private void RDPEvent_OnLeaveFullscreenMode() { Fullscreen = false; _leaveFullscreenEvent?.Invoke(this, new EventArgs()); }' `
'private void RDPEvent_OnLeaveFullscreenMode() { Fullscreen = false; PassiveRdpState.SetViewOnly(this, false, false); _leaveFullscreenEvent?.Invoke(this, new EventArgs()); }' `
'OnLeaveFullscreenMode'

$text = Replace-Exact $text `
'private void RdpClient_GotFocus(object sender, EventArgs e) { ((ConnectionTab)Control.Parent.Parent).Focus(); }' `
'private void RdpClient_GotFocus(object sender, EventArgs e) { if (PassiveRdpState.ShouldSuppressFocus(this)) return; ((ConnectionTab)Control.Parent.Parent).Focus(); }' `
'RdpClient_GotFocus'

$text = Replace-Exact $text `
'//SetProps() _rdpClient.Connect();' `
'//SetProps() PassiveRdpState.For(Control).AutomaticReconnectInProgress = true; PassiveRdpState.ApplyFullscreenViewOnlyPolicy(this); _rdpClient.Connect();' `
'Automatic reconnect'

$helpersAnchor = 'private void SetRdpClientProperties()'
$helpers = @'
        protected void PassiveScrollToLowerRightAsync()
        {
            try
            {
                if (Control == null || Control.IsDisposed || !Control.IsHandleCreated)
                    return;

                Control.BeginInvoke(new Action(() =>
                {
                    PassiveScrollToLowerRight();
                    var timer = new System.Windows.Forms.Timer { Interval = 300 };
                    timer.Tick += (sender, args) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        PassiveScrollToLowerRight();
                    };
                    timer.Start();
                }));
            }
            catch
            {
            }
        }

        private void PassiveScrollToLowerRight()
        {
            try
            {
                var parent = Control?.Parent;
                while (parent != null)
                {
                    var scrollable = parent as ScrollableControl;
                    if (scrollable != null)
                    {
                        scrollable.AutoScrollPosition = new System.Drawing.Point(
                            scrollable.HorizontalScroll.Maximum,
                            scrollable.VerticalScroll.Maximum);
                        return;
                    }

                    parent = parent.Parent;
                }
            }
            catch
            {
            }
        }

        private void SetRdpClientProperties()
'@
$text = Replace-Exact $text $helpersAnchor $helpers 'Passive scroll helpers'

$stateAnchor = '        #endregion #region Enums'
$stateCode = @'
        #endregion

        private sealed class RdpPassiveConnectionState
        {
            public bool ViewOnly;
            public bool UserDisabledViewOnlyInFullscreen;
            public bool AutomaticReconnectInProgress;
        }

        private static class PassiveRdpState
        {
            private static readonly System.Collections.Generic.Dictionary<Control, RdpPassiveConnectionState> States =
                new System.Collections.Generic.Dictionary<Control, RdpPassiveConnectionState>();

            private static readonly PassiveRdpInputFilter InputFilter = new PassiveRdpInputFilter();

            public static RdpPassiveConnectionState For(Control control)
            {
                if (control == null)
                    return new RdpPassiveConnectionState();

                RdpPassiveConnectionState state;
                if (!States.TryGetValue(control, out state))
                {
                    state = new RdpPassiveConnectionState();
                    States[control] = state;
                }

                return state;
            }

            public static bool ShouldSuppressFocus(RdpProtocol6 protocol)
            {
                if (protocol == null || protocol.Control == null)
                    return false;

                var state = For(protocol.Control);
                return state.ViewOnly || state.AutomaticReconnectInProgress;
            }

            public static void ToggleViewOnly(RdpProtocol6 protocol)
            {
                if (protocol == null || protocol.Control == null)
                    return;

                if (!protocol.Fullscreen)
                {
                    var state = For(protocol.Control);
                    state.UserDisabledViewOnlyInFullscreen = false;
                    SetViewOnly(protocol, false, false);
                    return;
                }

                var next = !For(protocol.Control).ViewOnly;
                SetViewOnly(protocol, next, true);
            }

            public static void ApplyFullscreenViewOnlyPolicy(RdpProtocol6 protocol)
            {
                if (protocol == null || protocol.Control == null)
                    return;

                var state = For(protocol.Control);
                if (protocol.Fullscreen)
                {
                    if (!state.UserDisabledViewOnlyInFullscreen)
                        SetViewOnly(protocol, true, false);
                }
                else
                {
                    state.UserDisabledViewOnlyInFullscreen = false;
                    SetViewOnly(protocol, false, false);
                }

                protocol.PassiveScrollToLowerRightAsync();
            }

            public static void SetViewOnly(RdpProtocol6 protocol, bool value)
            {
                SetViewOnly(protocol, value, false);
            }

            public static void SetViewOnly(RdpProtocol6 protocol, bool value, bool userInitiated)
            {
                if (protocol == null || protocol.Control == null)
                    return;

                var state = For(protocol.Control);
                if (!protocol.Fullscreen)
                    value = false;

                state.ViewOnly = value;
                InputFilter.SetBlocked(protocol.Control, value);

                if (userInitiated && protocol.Fullscreen)
                    state.UserDisabledViewOnlyInFullscreen = !value;

                if (value && protocol.Control.ContainsFocus && protocol.InterfaceControl != null && !protocol.InterfaceControl.IsDisposed)
                    protocol.InterfaceControl.Focus();
            }
        }

        private sealed class PassiveRdpInputFilter : IMessageFilter
        {
            private readonly System.Collections.Generic.List<Control> blockedControls =
                new System.Collections.Generic.List<Control>();

            [DllImport("user32.dll")]
            private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

            public void SetBlocked(Control control, bool blocked)
            {
                if (control == null)
                    return;

                if (blocked)
                {
                    if (!blockedControls.Contains(control))
                    {
                        blockedControls.Add(control);
                        if (blockedControls.Count == 1)
                            Application.AddMessageFilter(this);
                    }
                }
                else
                {
                    blockedControls.Remove(control);
                    if (blockedControls.Count == 0)
                        Application.RemoveMessageFilter(this);
                }
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (!IsInputMessage(m.Msg))
                    return false;

                foreach (var control in blockedControls.ToArray())
                {
                    if (IsMessageForControl(control, m.HWnd))
                        return true;
                }

                return false;
            }

            private static bool IsMessageForControl(Control control, IntPtr hWnd)
            {
                if (control == null || control.IsDisposed || !control.IsHandleCreated)
                    return false;

                return control.Handle == hWnd || IsChild(control.Handle, hWnd);
            }

            private static bool IsInputMessage(int msg)
            {
                return msg == 0x0021 || msg == 0x00FF ||
                       (msg >= 0x0100 && msg <= 0x0108) ||
                       (msg >= 0x0200 && msg <= 0x020E);
            }
        }

        #region Enums
'@
$text = Replace-Exact $text $stateAnchor $stateCode 'Passive RDP state/input filter'

Write-Text $rdp6 $text
Write-Host 'Patched RdpProtocol6.cs'

if (Test-Path $rdp8) {
    $text8 = Read-Text $rdp8
    $text8 = Replace-Optional $text8 `
'RdpClient8.Reconnect((uint)size.Width, (uint)size.Height);' `
'RdpClient8.Reconnect((uint)size.Width, (uint)size.Height); PassiveScrollToLowerRightAsync();' `
'RdpProtocol8 ReconnectForResize scroll'

    $text8 = Replace-Optional $text8 `
'_controlBeginningSize = Size.Empty;' `
'_controlBeginningSize = Size.Empty; PassiveScrollToLowerRightAsync();' `
'RdpProtocol8 ResizeEnd scroll'

    Write-Text $rdp8 $text8
    Write-Host 'Patched RdpProtocol8.cs'
}

Write-Host 'Clean passive RDP monitor patch for mRemoteNG 1.77.2-release applied.'

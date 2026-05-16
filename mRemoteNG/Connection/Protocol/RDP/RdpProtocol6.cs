using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using System.Windows.Forms;
using AxMSTSCLib;
using mRemoteNG.App;
using mRemoteNG.Messages;
using mRemoteNG.Properties;
using mRemoteNG.Security.SymmetricEncryption;
using mRemoteNG.Tools;
using mRemoteNG.UI;
using mRemoteNG.UI.Forms;
using mRemoteNG.UI.Tabs;
using MSTSCLib;
using mRemoteNG.Resources.Language;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public class RdpProtocol6 : ProtocolBase, ISupportsViewOnly
    {
        /* RDP v8 requires Windows 7 with:
         * https://support.microsoft.com/en-us/kb/2592687
         * OR
         * https://support.microsoft.com/en-us/kb/2923545
         *
         * Windows 8+ support RDP v8 out of the box.
         */
        private MsRdpClient6NotSafeForScripting _rdpClient;
        protected ConnectionInfo connectionInfo;
        protected bool loginComplete;
        private Version _rdpVersion;
        private bool _redirectKeys;
        private bool _alertOnIdleDisconnect;
        private readonly DisplayProperties _displayProperties;
        private readonly FrmMain _frmMain = FrmMain.Default;
        protected virtual RdpVersion RdpProtocolVersion => RdpVersion.Rdc6;
        private AxHost AxHost => (AxHost)Control;
        private static readonly PassiveRdpInputBlocker InputBlocker = new PassiveRdpInputBlocker();
        private bool _viewOnly;
        private bool _hasCompletedInitialConnect;
        private bool _userManuallyDisabledViewOnly;
        private bool _autoEnableViewOnlyAfterSuccessfulScroll = true;
        private bool _automaticReconnectInProgress;
        private bool _suppressFocusOnAutomaticReconnect;
        private bool _isRdpFullscreenActive;
        private bool _fullscreenRequestedByMRemote;
        private bool _fullscreenExitRequestedByMRemote;
        private System.Windows.Forms.Timer _scrollRetryTimer;
        private int _scrollRetryAttempt;
        private string _scrollRetrySource;
        private System.Windows.Forms.Timer _fullscreenLeaveScrollTimer;
        private string _fullscreenLeaveScrollSource;
        private bool _applyingPassiveScrollLayout;
        private DateTime _fullscreenLeftAtUtc = DateTime.MinValue;
        private System.Windows.Forms.Timer _fullscreenPollTimer;
        private int _fullscreenPollAttempts;
        private bool _fullscreenPollExpectedState;
        private System.Windows.Forms.Timer _fullscreenExitFinalizeTimer;
        private int _fullscreenExitFinalizeAttempts;
        private Control _rdpSafeFocusSink;
        private bool _programmaticPassiveScrollCommit;
        private System.Windows.Forms.Timer _passiveScrollCommitTimer;
        private int _passiveScrollCommitAttempts;
        private Point _lastCommittedPassiveScrollTarget;
        private string _lastCommittedPassiveScrollSource;

        private const int FullscreenPollMaxAttempts = 10;
        private const int FullscreenPollIntervalMs = 200;
        private const int FullscreenExitFinalizeIntervalMs = 100;
        private const int FullscreenExitFinalizeMaxAttempts = 15;
        private const int ScrollRetryMaxAttempts = 10;
        private const int ScrollRetryIntervalMs = 200;
        private const int PassiveScrollCommitIntervalMs = 150;
        private const int PassiveScrollCommitMaxAttempts = 4;
        private const int FullscreenLeaveScrollDelayMs = 800;
        private const int SafeScrollViewportMultiplier = 5;
        private const int SafeScrollAbsoluteMaximum = 8192;
        private static readonly TimeSpan FullscreenLeaveCooldown = TimeSpan.FromSeconds(2);

        private const int WM_CANCELMODE = 0x001F;
        private const int WM_KILLFOCUS = 0x0008;
        private const int WM_HSCROLL = 0x0114;
        private const int WM_VSCROLL = 0x0115;
        private const int SB_THUMBPOSITION = 4;
        private const int SB_THUMBTRACK = 5;
        private const int SB_ENDSCROLL = 8;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        private static IntPtr MakeWParam(int lowWord, int highWord)
        {
            return new IntPtr((highWord << 16) | (lowWord & 0xffff));
        }

        #region Properties

        public virtual bool SmartSize
        {
            get => _rdpClient.AdvancedSettings2.SmartSizing;
            protected set => _rdpClient.AdvancedSettings2.SmartSizing = value;
        }

        public virtual bool Fullscreen
        {
            get => IsFullscreenEffective();
            protected set
            {
                SetFullscreenState(value, "Fullscreen setter", true);
            }
        }

        private bool RedirectKeys
        {
/*
			get
			{
				return _redirectKeys;
			}
*/
            set
            {
                _redirectKeys = value;
                try
                {
                    if (!_redirectKeys)
                    {
                        return;
                    }

                    Debug.Assert(Convert.ToBoolean(_rdpClient.SecuredSettingsEnabled));
                    var msRdpClientSecuredSettings = _rdpClient.SecuredSettings2;
                    msRdpClientSecuredSettings.KeyboardHookMode = 1; // Apply key combinations at the remote server.
                }
                catch (Exception ex)
                {
                    Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetRedirectKeysFailed, ex);
                }
            }
        }

        public bool LoadBalanceInfoUseUtf8 { get; set; }

        public bool ViewOnly
        {
            get => _viewOnly;
            set => SetViewOnly(value);
        }

        #endregion

        #region Constructors

        public RdpProtocol6()
        {
            _displayProperties = new DisplayProperties();
            tmrReconnect.Elapsed += tmrReconnect_Elapsed;
        }

        #endregion

        #region Public Methods
        protected virtual AxHost CreateActiveXRdpClientControl()
        {
            return new AxMsRdpClient6NotSafeForScripting();
        }


        public override bool Initialize()
        {
            connectionInfo = InterfaceControl.Info;
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"Requesting RDP version: {connectionInfo.RdpVersion}. Using: {RdpProtocolVersion}");
            Control = CreateActiveXRdpClientControl();
            base.Initialize();

            try
            {
                if (!InitializeActiveXControl())
                    return false;

                InterfaceControl.Resize += InterfaceControl_Resize;
                _rdpVersion = new Version(_rdpClient.Version);
                SetRdpClientProperties();
                return true;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetPropsFailed, ex);
                return false;
            }
        }

        private bool InitializeActiveXControl()
        {
            try
            {
                Control.GotFocus += RdpClient_GotFocus;
                Control.HandleCreated += RdpClient_HandleCreated;
                Control.ParentChanged += RdpClient_ParentChanged;
                Control.Disposed += RdpClient_Disposed;
                Control.CreateControl();
                while (!Control.Created)
                {
                    Thread.Sleep(0);
                    Application.DoEvents();
                }
                Control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                Control.Margin = Padding.Empty;

                _rdpClient = (MsRdpClient6NotSafeForScripting)((AxHost)Control).GetOcx();
                NormalizeRdpScrollOrigin(FindRdpScrollContainer(), "InitializeActiveXControl");
                ScrollToLowerRightAsync("InitializeActiveXControl");
                return true;
            }
            catch (COMException ex)
            {
                if (ex.Message.Contains("CLASS_E_CLASSNOTAVAILABLE"))
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.ErrorMsg,
                        string.Format(Language.RdpProtocolVersionNotSupported, connectionInfo.RdpVersion));
                }
                else
                {
                    Runtime.MessageCollector.AddExceptionMessage(Language.RdpControlCreationFailed, ex);
                }
                Control.Dispose();
                return false;
            }
        }

        public override bool Connect()
        {
            loginComplete = false;
            _hasCompletedInitialConnect = false;
            _automaticReconnectInProgress = false;
            _suppressFocusOnAutomaticReconnect = false;
            SetEventHandlers();

            try
            {
                _rdpClient.Connect();
                base.Connect();

                return true;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.ConnectionOpenFailed, ex);
            }

            return false;
        }

        public override void Disconnect()
        {
            try
            {
                _rdpClient.Disconnect();
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpDisconnectFailed, ex);
                Close();
            }
        }

        public void ToggleFullscreen()
        {
            try
            {
                var target = !IsFullscreenEffective();
                SetFullscreenState(target, "ToggleFullscreen", true);
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpToggleFullscreenFailed, ex);
            }
        }

        public void ToggleSmartSize()
        {
            try
            {
                SmartSize = !SmartSize;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpToggleSmartSizeFailed, ex);
            }
        }

        /// <summary>
        /// Toggles whether the RDP ActiveX control will capture and send input events to the remote host.
        /// The local host will continue to receive data from the remote host regardless of this setting.
        /// </summary>
        public void ToggleViewOnly()
        {
            try
            {
                var enabled = !ViewOnly;
                _userManuallyDisabledViewOnly = !enabled;
                _autoEnableViewOnlyAfterSuccessfulScroll = enabled;
                SetViewOnly(enabled, "ToggleViewOnly");
                LogFullscreenState("ToggleViewOnly", enabled);
            }
            catch
            {
                Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, $"Could not toggle view only mode for host {connectionInfo.Hostname}");
            }
        }

        public override void Focus()
        {
            try
            {
                if (ShouldSuppressRdpFocus())
                    return;

                if (Control.ContainsFocus == false)
                {
                    Control.Focus();
                }
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpFocusFailed, ex);
            }
        }

        /// <summary>
        /// Determines if this version of the RDP client
        /// is supported on this machine.
        /// </summary>
        /// <returns></returns>
        public bool RdpVersionSupported()
        {
            try
            {
                using (var control = CreateActiveXRdpClientControl())
                {
                    control.CreateControl();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public override void ResizeEnd(object sender, EventArgs e)
        {
            ApplyRdpControlSizeForCurrentResolution("ResizeEnd");
            ScrollToLowerRightAsync("ResizeEnd");
        }
        #endregion

        #region Private Methods


        private void SetViewOnly(bool value)
        {
            SetViewOnly(value, "SetViewOnly");
        }

        private void SetViewOnly(bool value, string source)
        {
            if (Control != null && !Control.IsDisposed && Control.IsHandleCreated && Control.InvokeRequired)
            {
                Control.BeginInvoke(new Action(() => SetViewOnly(value, source)));
                return;
            }

            var requested = value;
            var previous = _viewOnly;
            _viewOnly = requested;
            var controlAvailable = Control != null && !Control.IsDisposed;
            var childHwndCount = controlAvailable ? InputBlocker.SetBlocked(Control, _viewOnly) : 0;

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP input blocker {(_viewOnly ? "enabled" : "disabled")} from {source} for host '{connectionInfo?.Hostname}': " +
                $"host={FormatControlName(Control)}, controlHandle={FormatHandle(Control?.Handle ?? IntPtr.Zero)}, " +
                $"childHWndCount={childHwndCount}, requestedViewOnly={requested}, appliedViewOnly={_viewOnly}, " +
                $"previousViewOnly={previous}, controlAvailable={controlAvailable}, fullscreenEffective={IsFullscreenEffective()}, " +
                $"userManuallyDisabledViewOnly={_userManuallyDisabledViewOnly}, " +
                $"autoEnableViewOnlyAfterSuccessfulScroll={_autoEnableViewOnlyAfterSuccessfulScroll}");
        }

        private void SetFullscreenState(bool target, string source, bool startPolling)
        {
            if (Control != null && Control.IsHandleCreated && Control.InvokeRequired)
            {
                Control.BeginInvoke(new Action(() => SetFullscreenState(target, source, startPolling)));
                return;
            }

            if (target)
                StopFullscreenExitFinalizer($"{source} fullscreen enter");

            _fullscreenRequestedByMRemote = target;
            _fullscreenExitRequestedByMRemote = !target;
            SetRdpClientFullscreen(target, source);
            MarkRdpFullscreenActive(target, source);
            ApplyFullscreenViewOnlyPolicy(source);

            if (target)
                StopScrollRetryTimer();
            else
                HandleFullscreenLeaveLayout(source);

            if (startPolling)
                StartFullscreenPolling(source, target);
        }

        private void SetRdpClientFullscreen(bool target, string source)
        {
            try
            {
                if (_rdpClient == null)
                    return;

                _rdpClient.FullScreen = target;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(
                    $"RDP fullscreen request from {source} failed for host '{connectionInfo?.Hostname}'",
                    ex, MessageClass.WarningMsg, false);
            }
        }

        private void MarkRdpFullscreenActive(bool active, string source)
        {
            var wasFullscreen = _isRdpFullscreenActive || _fullscreenRequestedByMRemote ||
                                (TryReadRdpClientFullscreen(out var wasRdpFullscreen) && wasRdpFullscreen);
            _isRdpFullscreenActive = active;

            if (active)
            {
                _fullscreenExitRequestedByMRemote = false;
                _userManuallyDisabledViewOnly = false;
                _autoEnableViewOnlyAfterSuccessfulScroll = true;
            }
            else
            {
                _fullscreenRequestedByMRemote = false;
                _fullscreenExitRequestedByMRemote =
                    TryReadRdpClientFullscreen(out var rdpFullscreen) && rdpFullscreen;
                if (wasFullscreen)
                    _fullscreenLeftAtUtc = DateTime.UtcNow;
            }

            LogFullscreenState(source, active);
        }

        private bool IsFullscreenEffective()
        {
            if (_fullscreenExitRequestedByMRemote)
                return false;

            if (_isRdpFullscreenActive || _fullscreenRequestedByMRemote)
                return true;

            return TryReadRdpClientFullscreen(out var rdpFullscreen) && rdpFullscreen;
        }

        private bool TryReadRdpClientFullscreen(out bool fullscreen)
        {
            fullscreen = false;

            try
            {
                if (_rdpClient == null)
                    return false;

                fullscreen = _rdpClient.FullScreen;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private Control EnsureRdpSafeFocusSink()
        {
            if (InterfaceControl == null || InterfaceControl.IsDisposed)
                return null;

            if (_rdpSafeFocusSink != null &&
                !_rdpSafeFocusSink.IsDisposed &&
                _rdpSafeFocusSink.Parent == InterfaceControl)
                return _rdpSafeFocusSink;

            if (_rdpSafeFocusSink != null && !_rdpSafeFocusSink.IsDisposed)
                _rdpSafeFocusSink.Dispose();

            _rdpSafeFocusSink = new PassiveRdpFocusSink
            {
                Name = "PassiveRdpSafeFocusSink",
                Size = new Size(1, 1),
                Location = new Point(0, 0),
                TabStop = true
            };

            InterfaceControl.Controls.Add(_rdpSafeFocusSink);
            _rdpSafeFocusSink.BringToFront();

            return _rdpSafeFocusSink;
        }

        private void StartFullscreenExitFinalizer(string source)
        {
            if (Control == null || Control.IsDisposed)
                return;

            if (Control.IsHandleCreated && Control.InvokeRequired)
            {
                Control.BeginInvoke(new Action(() => StartFullscreenExitFinalizer(source)));
                return;
            }

            _fullscreenExitFinalizeAttempts = 0;

            if (_fullscreenExitFinalizeTimer == null)
            {
                _fullscreenExitFinalizeTimer = new System.Windows.Forms.Timer { Interval = FullscreenExitFinalizeIntervalMs };
                _fullscreenExitFinalizeTimer.Tick += FullscreenExitFinalizeTimerOnTick;
            }

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP fullscreen exit finalizer started from {source} for host '{connectionInfo?.Hostname}'");

            FinalizeRdpFullscreenExitOnce(source + " immediate");

            _fullscreenExitFinalizeTimer.Stop();
            _fullscreenExitFinalizeTimer.Start();
        }

        private void FullscreenExitFinalizeTimerOnTick(object sender, EventArgs e)
        {
            _fullscreenExitFinalizeAttempts++;
            FinalizeRdpFullscreenExitOnce($"fullscreen exit finalizer attempt {_fullscreenExitFinalizeAttempts}");

            if (_fullscreenExitFinalizeAttempts >= FullscreenExitFinalizeMaxAttempts)
                StopFullscreenExitFinalizer("max attempts");
        }

        private void StopFullscreenExitFinalizer(string source)
        {
            if (_fullscreenExitFinalizeTimer == null)
                return;

            _fullscreenExitFinalizeTimer.Stop();
            _fullscreenExitFinalizeAttempts = 0;

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP fullscreen exit finalizer stopped from {source} for host '{connectionInfo?.Hostname}'");
        }

        private void FinalizeRdpFullscreenExitOnce(string source)
        {
            try
            {
                if (_fullscreenRequestedByMRemote || _isRdpFullscreenActive)
                    return;

                if (_rdpClient != null)
                {
                    try
                    {
                        if (_rdpClient.FullScreen)
                            _rdpClient.FullScreen = false;
                    }
                    catch
                    {
                    }
                }

                var captureBefore = GetCapture();

                SendCancelModeToRdpWindows(source);

                ReleaseCapture();
                ClipCursor(IntPtr.Zero);
                Cursor.Clip = Rectangle.Empty;

                var sink = EnsureRdpSafeFocusSink();
                if (sink != null)
                {
                    if (!sink.IsHandleCreated)
                        sink.CreateControl();

                    if (sink.IsHandleCreated)
                    {
                        sink.Focus();
                        SetFocus(sink.Handle);
                        sink.SendToBack();
                    }
                }

                var captureAfter = GetCapture();

                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP fullscreen exit finalized from {source} for host '{connectionInfo?.Hostname}': " +
                    $"captureBefore={FormatHandle(captureBefore)}, captureAfter={FormatHandle(captureAfter)}, " +
                    $"rdpFullScreen={TryReadRdpClientFullscreen(out var fs) && fs}, safeFocus={FormatControlName(sink)}");
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(
                    $"RDP fullscreen exit finalizer failed from {source} for host '{connectionInfo?.Hostname}'",
                    ex, MessageClass.WarningMsg, false);
            }
        }

        private void SendCancelModeToRdpWindows(string source)
        {
            if (Control == null || Control.IsDisposed || !Control.IsHandleCreated)
                return;

            var count = 0;

            SendMessage(Control.Handle, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
            SendMessage(Control.Handle, WM_KILLFOCUS, IntPtr.Zero, IntPtr.Zero);
            count++;

            EnumChildWindows(Control.Handle, (hWnd, lParam) =>
            {
                SendMessage(hWnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                SendMessage(hWnd, WM_KILLFOCUS, IntPtr.Zero, IntPtr.Zero);
                count++;
                return true;
            }, IntPtr.Zero);

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP fullscreen exit cancel-mode sent from {source} for host '{connectionInfo?.Hostname}': hwndCount={count}");
        }

        protected void ApplyFullscreenViewOnlyPolicy()
        {
            ApplyFullscreenViewOnlyPolicy("ApplyFullscreenViewOnlyPolicy");
        }

        protected void ApplyFullscreenViewOnlyPolicy(string source)
        {
            if (Control == null || _rdpClient == null)
                return;

            if (Control.IsHandleCreated && Control.InvokeRequired)
            {
                Control.BeginInvoke(new Action(() => ApplyFullscreenViewOnlyPolicy(source)));
                return;
            }

            if (IsFullscreenEffective())
            {
                if (!_userManuallyDisabledViewOnly)
                    SetViewOnly(true, source);
            }

            LogFullscreenState(source, null);
        }

        private void StartFullscreenPolling(string source, bool expectedState)
        {
            if (Control == null || Control.IsDisposed)
                return;

            _fullscreenPollExpectedState = expectedState;
            _fullscreenPollAttempts = 0;

            if (_fullscreenPollTimer == null)
            {
                _fullscreenPollTimer = new System.Windows.Forms.Timer { Interval = FullscreenPollIntervalMs };
                _fullscreenPollTimer.Tick += FullscreenPollTimerOnTick;
            }

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"Starting RDP fullscreen polling from {source} for host '{connectionInfo?.Hostname}': expected={expectedState}");

            _fullscreenPollTimer.Stop();
            _fullscreenPollTimer.Start();
        }

        private void FullscreenPollTimerOnTick(object sender, EventArgs e)
        {
            _fullscreenPollAttempts++;

            var read = TryReadRdpClientFullscreen(out var rdpFullscreen);
            if (read)
            {
                if (!rdpFullscreen)
                    _fullscreenExitRequestedByMRemote = false;

                var shouldAdoptObservedState = rdpFullscreen == _fullscreenPollExpectedState ||
                                               !_fullscreenPollExpectedState ||
                                               _fullscreenPollAttempts >= FullscreenPollMaxAttempts;

                if (shouldAdoptObservedState && rdpFullscreen != _isRdpFullscreenActive)
                {
                    MarkRdpFullscreenActive(rdpFullscreen, "fullscreen polling");
                    if (!rdpFullscreen)
                        HandleFullscreenLeaveLayout("fullscreen polling");
                }
            }

            ApplyFullscreenViewOnlyPolicy("fullscreen polling");
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP fullscreen polling attempt {_fullscreenPollAttempts} for host '{connectionInfo?.Hostname}': " +
                $"read={read}, observed={rdpFullscreen}, expected={_fullscreenPollExpectedState}");

            if (_fullscreenPollAttempts < FullscreenPollMaxAttempts)
                return;

            _fullscreenPollTimer.Stop();
        }

        private void LogFullscreenState(string source, bool? requestedFullscreen)
        {
            TryReadRdpClientFullscreen(out var rdpFullscreen);
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP fullscreen state source={source} for host '{connectionInfo?.Hostname}': " +
                $"requestedFullscreen={requestedFullscreen?.ToString() ?? "unchanged"}, " +
                $"rdpClientFullScreen={rdpFullscreen}, isRdpFullscreenActive={_isRdpFullscreenActive}, " +
                $"fullscreenRequestedByMRemote={_fullscreenRequestedByMRemote}, fullscreenExitRequestedByMRemote={_fullscreenExitRequestedByMRemote}, " +
                $"ViewOnly={_viewOnly}, " +
                $"userManuallyDisabledViewOnly={_userManuallyDisabledViewOnly}, " +
                $"autoEnableViewOnlyAfterSuccessfulScroll={_autoEnableViewOnlyAfterSuccessfulScroll}");
        }

        protected void ScrollToLowerRightAsync()
        {
            ScrollToLowerRightAsync("ScrollToLowerRightAsync");
        }

        protected void ScrollToLowerRightAsync(string source)
        {
            try
            {
                if (Control == null || Control.IsDisposed || !Control.IsHandleCreated)
                    return;

                Control.BeginInvoke(new Action(() => StartScrollToLowerRightRetries(source)));
            }
            catch
            {
            }
        }

        protected void BeginAutomaticReconnect()
        {
            if (!_hasCompletedInitialConnect)
                return;

            _automaticReconnectInProgress = true;
            _suppressFocusOnAutomaticReconnect = true;
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"Suppressing RDP focus during automatic reconnect for host '{connectionInfo.Hostname}'");
        }

        protected void EndAutomaticReconnect()
        {
            if (!_automaticReconnectInProgress && !_suppressFocusOnAutomaticReconnect)
                return;

            try
            {
                if (Control == null || Control.IsDisposed || !Control.IsHandleCreated)
                {
                    ClearAutomaticReconnectState();
                    return;
                }

                Control.BeginInvoke(new Action(() =>
                {
                    var timer = new System.Windows.Forms.Timer { Interval = 350 };
                    timer.Tick += (sender, args) =>
                    {
                        timer.Stop();
                        timer.Dispose();
                        ClearAutomaticReconnectState();
                    };
                    timer.Start();
                }));
            }
            catch
            {
                ClearAutomaticReconnectState();
            }
        }

        private void ClearAutomaticReconnectState()
        {
            _automaticReconnectInProgress = false;
            _suppressFocusOnAutomaticReconnect = false;
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"Released RDP automatic reconnect focus suppression for host '{connectionInfo.Hostname}'");
        }

        private bool ShouldSuppressRdpFocus()
        {
            return ViewOnly || _automaticReconnectInProgress || _suppressFocusOnAutomaticReconnect;
        }

        private void AllowAutoViewOnlyAfterPassiveScroll(string source)
        {
            _userManuallyDisabledViewOnly = false;
            _autoEnableViewOnlyAfterSuccessfulScroll = true;
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP passive monitor auto ViewOnly armed from {source} for host '{connectionInfo?.Hostname}'");
        }

        private bool ShouldAttemptPassiveScroll(string source)
        {
            if (Control == null || Control.IsDisposed || !Control.IsHandleCreated || _rdpClient == null)
                return false;

            if (InterfaceControl == null)
                return false;

            if (!ShouldKeepRdpControlScrollable())
                return false;

            if (IsFullscreenEffective() || IsRdpClientFullscreenActiveSafe())
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP passive scroll skipped from {source} for host '{connectionInfo?.Hostname}': fullscreen is active");
                return false;
            }

            if (IsSmartSizeEnabledSafe() || InterfaceControl.Info?.Resolution == RDPResolutions.SmartSize)
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP passive scroll skipped from {source} for host '{connectionInfo?.Hostname}': SmartSize is active");
                return false;
            }

            if (IsInFullscreenLeaveCooldown && !IsAfterFullscreenLeaveScrollSource(source))
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP passive scroll skipped from {source} for host '{connectionInfo?.Hostname}': fullscreen leave layout cooldown is active");
                return false;
            }

            var scrollable = FindRdpScrollContainer();
            if (scrollable == null || scrollable.IsDisposed || scrollable.ClientSize.IsEmpty)
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP passive scroll skipped from {source} for host '{connectionInfo?.Hostname}': scrollable viewport is unavailable");
                return false;
            }

            return true;
        }

        protected bool IsLeavingFullscreenOrLayoutUnstable()
        {
            return _fullscreenExitRequestedByMRemote ||
                   IsInFullscreenLeaveCooldown ||
                   IsFullscreenEffective() ||
                   IsRdpClientFullscreenActiveSafe();
        }

        protected bool IsApplyingPassiveScrollLayout => _applyingPassiveScrollLayout;

        private bool IsInFullscreenLeaveCooldown =>
            _fullscreenLeftAtUtc != DateTime.MinValue &&
            DateTime.UtcNow - _fullscreenLeftAtUtc < FullscreenLeaveCooldown;

        private bool IsRdpClientFullscreenActiveSafe()
        {
            return TryReadRdpClientFullscreen(out var rdpFullscreen) && rdpFullscreen;
        }

        private static bool IsAfterFullscreenLeaveScrollSource(string source)
        {
            return !string.IsNullOrEmpty(source) &&
                   source.IndexOf("after fullscreen leave", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void HandleFullscreenLeaveLayout(string source)
        {
            _userManuallyDisabledViewOnly = false;
            _autoEnableViewOnlyAfterSuccessfulScroll = true;
            SetViewOnly(true, $"{source} fullscreen leave input shield");
            StartFullscreenExitFinalizer(source);
            ResetPassiveScrollLayout(source);
            ScheduleSafeScrollAfterFullscreenLeave(source);
        }

        private void ResetPassiveScrollLayout(string source)
        {
            StopScrollRetryTimer();
            StopPassiveScrollCommitTimer();

            var scrollable = FindRdpScrollContainer();
            if (scrollable != null && !scrollable.IsDisposed)
            {
                try
                {
                    scrollable.AutoScrollPosition = Point.Empty;
                    scrollable.AutoScrollMinSize = Size.Empty;
                    scrollable.PerformLayout();
                }
                catch (Exception ex)
                {
                    Runtime.MessageCollector.AddExceptionMessage(
                        $"RDP passive scroll reset failed for host '{connectionInfo?.Hostname}' from {source}",
                        ex, MessageClass.WarningMsg, false);
                }
            }

            if (Control != null && !Control.IsDisposed)
            {
                NormalizeRdpScrollOrigin(scrollable, source);
            }

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP passive scroll layout reset from {source} for host '{connectionInfo?.Hostname}': " +
                $"viewport={FormatSize(GetRdpViewportSize(scrollable))}, Control.Location={FormatPoint(Control?.Location ?? Point.Empty)}, " +
                $"Control.Size={FormatSize(Control?.Size ?? Size.Empty)}, AutoScrollMinSize={FormatSize(scrollable?.AutoScrollMinSize ?? Size.Empty)}");
        }

        private void NormalizeRdpScrollOrigin(ScrollableControl scrollable, string source)
        {
            StopScrollRetryTimer();

            if (Control == null || Control.IsDisposed)
                return;

            try
            {
                if (scrollable != null && !scrollable.IsDisposed)
                {
                    scrollable.AutoScrollPosition = Point.Empty;
                    scrollable.PerformLayout();
                }

                Control.Location = Point.Empty;
                Control.Margin = Padding.Empty;
                Control.Anchor = AnchorStyles.Top | AnchorStyles.Left;

                if (scrollable != null && !scrollable.IsDisposed)
                {
                    scrollable.AutoScrollMinSize =
                        IsSmartSizeEnabledSafe() || InterfaceControl?.Info?.Resolution == RDPResolutions.SmartSize
                            ? Size.Empty
                            : Control.Size;
                    scrollable.PerformLayout();
                }

                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP passive scroll origin normalized from {source} for host '{connectionInfo?.Hostname}': " +
                    $"Control.Location={FormatPoint(Control.Location)}, Control.Bounds={FormatRectangle(Control.Bounds)}, " +
                    $"Control.Size={FormatSize(Control.Size)}, AutoScrollMinSize={FormatSize(scrollable?.AutoScrollMinSize ?? Size.Empty)}");
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(
                    $"RDP passive scroll origin normalization failed for host '{connectionInfo?.Hostname}' from {source}",
                    ex, MessageClass.WarningMsg, false);
            }
        }

        private void ScheduleSafeScrollAfterFullscreenLeave(string source)
        {
            if (Control == null || Control.IsDisposed)
                return;

            _fullscreenLeaveScrollSource = source;

            if (_fullscreenLeaveScrollTimer == null)
            {
                _fullscreenLeaveScrollTimer = new System.Windows.Forms.Timer { Interval = FullscreenLeaveScrollDelayMs };
                _fullscreenLeaveScrollTimer.Tick += FullscreenLeaveScrollTimerOnTick;
            }

            _fullscreenLeaveScrollTimer.Stop();
            _fullscreenLeaveScrollTimer.Start();

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"Scheduled RDP safe scroll after fullscreen leave from {source} for host '{connectionInfo?.Hostname}' in {FullscreenLeaveScrollDelayMs}ms");
        }

        private void FullscreenLeaveScrollTimerOnTick(object sender, EventArgs e)
        {
            _fullscreenLeaveScrollTimer?.Stop();
            ScrollToLowerRightAsync($"after fullscreen leave: {_fullscreenLeaveScrollSource}");
        }

        private void StopScrollRetryTimer()
        {
            if (_scrollRetryTimer == null)
                return;

            _scrollRetryTimer.Stop();
            _scrollRetryAttempt = 0;
            _scrollRetrySource = null;
        }

        private void StartScrollToLowerRightRetries(string source)
        {
            if (!ShouldAttemptPassiveScroll(source))
                return;

            NormalizeRdpScrollOrigin(FindRdpScrollContainer(), source);
            if (!ShouldAttemptPassiveScroll(source))
                return;

            _scrollRetrySource = source;
            _scrollRetryAttempt = 0;

            if (_scrollRetryTimer == null)
            {
                _scrollRetryTimer = new System.Windows.Forms.Timer { Interval = ScrollRetryIntervalMs };
                _scrollRetryTimer.Tick += ScrollRetryTimerOnTick;
            }

            _scrollRetryTimer.Stop();
            ScrollRetryTimerOnTick(_scrollRetryTimer, EventArgs.Empty);

            if (!string.IsNullOrEmpty(_scrollRetrySource) &&
                _scrollRetryAttempt < ScrollRetryMaxAttempts &&
                _scrollRetryTimer != null)
                _scrollRetryTimer.Start();
        }

        private void ScrollRetryTimerOnTick(object sender, EventArgs e)
        {
            _scrollRetryAttempt++;

            if (!ShouldAttemptPassiveScroll(_scrollRetrySource))
            {
                StopScrollRetryTimer();
                return;
            }

            var shouldStop = ScrollToLowerRight(_scrollRetryAttempt, _scrollRetrySource);
            if (shouldStop || _scrollRetryAttempt >= ScrollRetryMaxAttempts)
                StopScrollRetryTimer();
        }

        private bool ScrollToLowerRight(int attempt, string source)
        {
            try
            {
                var scrollable = FindRdpScrollContainer();
                if (scrollable == null)
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                        $"RDP scroll attempt {attempt} from {source} for host '{connectionInfo?.Hostname}': no scrollable container found");
                    return true;
                }

                scrollable.AutoScroll = true;
                var surfaceSize = DetermineSafeRdpSurfaceSize(scrollable, source);
                var viewport = GetRdpViewportSize(scrollable);

                if (!IsPositiveSize(surfaceSize))
                {
                    ApplyUnsafePassiveScrollFallback(scrollable, viewport, source);
                    return true;
                }

                ApplyPassiveScrollSurface(scrollable, surfaceSize, source);

                if (!ValidatePassiveScrollInvariant(scrollable, surfaceSize, viewport, source))
                    return true;

                var targetX = Math.Max(0, Control.Width - viewport.Width);
                var targetY = Math.Max(0, Control.Height - viewport.Height);

                if (targetX == 0 && targetY == 0)
                {
                    scrollable.PerformLayout();
                    Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                        BuildScrollDiagnostics(attempt, source, scrollable, surfaceSize, targetX, targetY, true));
                    EnableViewOnlyAfterSuccessfulPassiveLayout(source, false);
                    return true;
                }

                scrollable.AutoScrollPosition = new Point(targetX, targetY);
                scrollable.PerformLayout();
                CommitPassiveScrollPosition(scrollable, targetX, targetY, source);

                var success = IsScrollAtTarget(scrollable, targetX, targetY);

                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    BuildScrollDiagnostics(attempt, source, scrollable, surfaceSize, targetX, targetY, success));

                if (success)
                {
                    StartPassiveScrollCommitTimer(new Point(targetX, targetY), source);
                    EnableViewOnlyAfterSuccessfulPassiveLayout(source, true);
                }

                return success;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(
                    $"RDP scroll lower-right attempt {attempt} from {source} failed for host '{connectionInfo?.Hostname}'",
                    ex, MessageClass.WarningMsg, false);
                return true;
            }
        }

        private void CommitPassiveScrollPosition(ScrollableControl scrollable, int targetX, int targetY, string source)
        {
            if (Control == null || Control.IsDisposed || scrollable == null || scrollable.IsDisposed)
                return;

            if (_programmaticPassiveScrollCommit)
                return;

            try
            {
                _programmaticPassiveScrollCommit = true;

                var viewport = GetRdpViewportSize(scrollable);
                var maxTargetX = Math.Max(0, Control.Width - viewport.Width);
                var maxTargetY = Math.Max(0, Control.Height - viewport.Height);

                targetX = Math.Min(Math.Max(0, targetX), maxTargetX);
                targetY = Math.Min(Math.Max(0, targetY), maxTargetY);

                // Keep the passive surface invariant exact; do not add fake scroll pixels.
                scrollable.AutoScroll = true;
                scrollable.AutoScrollMinSize = Control.Size;
                scrollable.PerformLayout();

                SetScrollBarValueSafely(scrollable.HorizontalScroll, targetX);
                SetScrollBarValueSafely(scrollable.VerticalScroll, targetY);

                scrollable.AutoScrollPosition = new Point(targetX, targetY);
                scrollable.PerformLayout();

                SetScrollBarValueSafely(scrollable.HorizontalScroll, targetX);
                SetScrollBarValueSafely(scrollable.VerticalScroll, targetY);

                SendExactScrollbarThumbPosition(scrollable, targetX, targetY, source);

                scrollable.AutoScrollPosition = new Point(targetX, targetY);
                scrollable.PerformLayout();

                var current = scrollable.AutoScrollPosition;

                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP passive scroll position committed from {source} for host '{connectionInfo?.Hostname}': " +
                    $"target={targetX},{targetY}, current={current}, HValue={scrollable.HorizontalScroll.Value}, " +
                    $"VValue={scrollable.VerticalScroll.Value}, AutoScrollMinSize={FormatSize(scrollable.AutoScrollMinSize)}, " +
                    $"Control.Size={FormatSize(Control.Size)}");
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(
                    $"RDP passive scroll position commit failed from {source} for host '{connectionInfo?.Hostname}'",
                    ex, MessageClass.WarningMsg, false);
            }
            finally
            {
                _programmaticPassiveScrollCommit = false;
            }
        }

        private static void SetScrollBarValueSafely(ScrollProperties scroll, int requestedValue)
        {
            if (scroll == null || !scroll.Visible)
                return;

            try
            {
                var maxValue = Math.Max(scroll.Minimum, scroll.Maximum - scroll.LargeChange + 1);
                var value = Math.Min(Math.Max(scroll.Minimum, requestedValue), maxValue);
                scroll.Value = value;
            }
            catch
            {
            }
        }

        private void SendExactScrollbarThumbPosition(ScrollableControl scrollable, int targetX, int targetY, string source)
        {
            if (scrollable == null || scrollable.IsDisposed || !scrollable.IsHandleCreated)
                return;

            try
            {
                if (targetX > 0 && scrollable.HorizontalScroll.Visible)
                {
                    SendMessage(scrollable.Handle, WM_HSCROLL, MakeWParam(SB_THUMBTRACK, targetX), IntPtr.Zero);
                    SendMessage(scrollable.Handle, WM_HSCROLL, MakeWParam(SB_THUMBPOSITION, targetX), IntPtr.Zero);
                    SendMessage(scrollable.Handle, WM_HSCROLL, MakeWParam(SB_ENDSCROLL, 0), IntPtr.Zero);
                }

                if (targetY > 0 && scrollable.VerticalScroll.Visible)
                {
                    SendMessage(scrollable.Handle, WM_VSCROLL, MakeWParam(SB_THUMBTRACK, targetY), IntPtr.Zero);
                    SendMessage(scrollable.Handle, WM_VSCROLL, MakeWParam(SB_THUMBPOSITION, targetY), IntPtr.Zero);
                    SendMessage(scrollable.Handle, WM_VSCROLL, MakeWParam(SB_ENDSCROLL, 0), IntPtr.Zero);
                }

                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP exact scrollbar thumb position sent from {source} for host '{connectionInfo?.Hostname}': " +
                    $"target={targetX},{targetY}");
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(
                    $"RDP exact scrollbar thumb position failed from {source} for host '{connectionInfo?.Hostname}'",
                    ex, MessageClass.WarningMsg, false);
            }
        }

        private void StartPassiveScrollCommitTimer(Point target, string source)
        {
            _lastCommittedPassiveScrollTarget = target;
            _lastCommittedPassiveScrollSource = source;
            _passiveScrollCommitAttempts = 0;

            if (_passiveScrollCommitTimer == null)
            {
                _passiveScrollCommitTimer = new System.Windows.Forms.Timer { Interval = PassiveScrollCommitIntervalMs };
                _passiveScrollCommitTimer.Tick += PassiveScrollCommitTimerOnTick;
            }

            _passiveScrollCommitTimer.Stop();
            _passiveScrollCommitTimer.Start();
        }

        private void PassiveScrollCommitTimerOnTick(object sender, EventArgs e)
        {
            _passiveScrollCommitAttempts++;

            if (!ShouldAttemptPassiveScroll(_lastCommittedPassiveScrollSource))
            {
                StopPassiveScrollCommitTimer();
                return;
            }

            var scrollable = FindRdpScrollContainer();
            CommitPassiveScrollPosition(scrollable, _lastCommittedPassiveScrollTarget.X,
                _lastCommittedPassiveScrollTarget.Y,
                $"{_lastCommittedPassiveScrollSource} passive scroll commit attempt {_passiveScrollCommitAttempts}");

            if (_passiveScrollCommitAttempts >= PassiveScrollCommitMaxAttempts)
                StopPassiveScrollCommitTimer();
        }

        private void StopPassiveScrollCommitTimer()
        {
            _passiveScrollCommitTimer?.Stop();
            _passiveScrollCommitAttempts = 0;
            _lastCommittedPassiveScrollTarget = Point.Empty;
            _lastCommittedPassiveScrollSource = null;
        }

        private ScrollableControl FindRdpScrollContainer()
        {
            if (InterfaceControl != null)
                return InterfaceControl;

            var parent = Control?.Parent;
            while (parent != null)
            {
                if (parent is ScrollableControl scrollable)
                    return scrollable;

                parent = parent.Parent;
            }

            return null;
        }

        protected void ApplyRdpControlSizeForCurrentResolution()
        {
            ApplyRdpControlSizeForCurrentResolution("ApplyRdpControlSizeForCurrentResolution");
        }

        protected void ApplyRdpControlSizeForCurrentResolution(string source)
        {
            if (Control == null || Control.IsDisposed || _rdpClient == null || !ShouldKeepRdpControlScrollable())
                return;

            if (IsFullscreenEffective() || IsRdpClientFullscreenActiveSafe())
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP control sizing skipped from {source} for host '{connectionInfo?.Hostname}' because fullscreen is active");
                return;
            }

            if (IsInFullscreenLeaveCooldown && !IsAfterFullscreenLeaveScrollSource(source))
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP control sizing skipped from {source} for host '{connectionInfo?.Hostname}' during fullscreen leave cooldown");
                return;
            }

            var scrollable = FindRdpScrollContainer();
            var targetSize = DetermineSafeRdpSurfaceSize(scrollable, source);
            var viewport = GetRdpViewportSize(scrollable);
            if (!IsPositiveSize(targetSize))
            {
                ApplyUnsafePassiveScrollFallback(scrollable, viewport, source);
                return;
            }

            ApplyPassiveScrollSurface(scrollable, targetSize, source);
        }

        protected bool ShouldKeepRdpControlScrollable()
        {
            return ShouldUseFixedResolutionControlSize() || ShouldUsePassiveScrollMode();
        }

        protected bool ShouldUseFixedResolutionControlSize()
        {
            if (InterfaceControl?.Info == null || Force.HasFlag(ConnectionInfo.Force.Fullscreen))
                return false;

            return InterfaceControl.Info.Resolution != RDPResolutions.FitToWindow &&
                   InterfaceControl.Info.Resolution != RDPResolutions.SmartSize &&
                   InterfaceControl.Info.Resolution != RDPResolutions.Fullscreen;
        }

        private bool ShouldUsePassiveScrollMode()
        {
            if (InterfaceControl?.Info == null || Force.HasFlag(ConnectionInfo.Force.Fullscreen))
                return false;

            if (InterfaceControl.Info.Resolution == RDPResolutions.SmartSize || IsSmartSizeEnabledSafe())
                return false;

            return InterfaceControl.Info.Resolution == RDPResolutions.FitToWindow ||
                   InterfaceControl.Info.Resolution == RDPResolutions.Fullscreen;
        }

        private Size DetermineSafeRdpSurfaceSize(ScrollableControl scrollable, string source)
        {
            var viewport = GetRdpViewportSize(scrollable);
            var rdpDesktop = GetRdpDesktopSize();
            var configured = GetConfiguredResolutionSize();
            Size candidate;

            if (!IsPositiveSize(viewport))
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP passive scroll skipped from {source} for host '{connectionInfo?.Hostname}': viewport size is empty");
                return Size.Empty;
            }

            if (IsPositiveSize(rdpDesktop))
            {
                candidate = rdpDesktop;
            }
            else if (IsPositiveSize(configured))
            {
                candidate = configured;
            }
            else
            {
                LogNoRealScrollableDesktop(source, viewport, rdpDesktop, configured);
                return viewport;
            }

            if (IsSuspiciousRdpSurfaceSize(candidate, viewport))
            {
                Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg,
                    $"RDP passive scroll skipped because surface size is suspicious for host '{connectionInfo?.Hostname}' from {source}: " +
                    $"candidate={FormatSize(candidate)}, viewport={FormatSize(viewport)}, rdpDesktop={FormatSize(rdpDesktop)}, " +
                    $"configured={FormatSize(configured)}, cap={FormatSize(GetSafeContentSizeCap(viewport))}");
                return Size.Empty;
            }

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP passive scroll surface determined from {source} for host '{connectionInfo?.Hostname}': " +
                $"candidate={FormatSize(candidate)}, viewport={FormatSize(viewport)}, rdpDesktop={FormatSize(rdpDesktop)}, " +
                $"configured={FormatSize(configured)}, fixedResolution={ShouldUseFixedResolutionControlSize()}");

            return candidate;
        }

        private Size GetRdpDesktopSize()
        {
            try
            {
                if (_rdpClient == null)
                    return Size.Empty;

                return new Size(Math.Max(0, _rdpClient.DesktopWidth), Math.Max(0, _rdpClient.DesktopHeight));
            }
            catch
            {
                return Size.Empty;
            }
        }

        private Size GetConfiguredResolutionSize()
        {
            if (!ShouldUseFixedResolutionControlSize())
                return Size.Empty;

            var resolution = InterfaceControl.Info.Resolution.GetResolutionRectangle();
            return new Size(Math.Max(0, resolution.Width), Math.Max(0, resolution.Height));
        }

        private Size GetRdpViewportSize(ScrollableControl scrollable)
        {
            var viewport = scrollable?.ClientSize ?? InterfaceControl?.ClientSize ?? Size.Empty;
            if (IsPositiveSize(viewport))
                return viewport;

            return InterfaceControl?.ClientSize ?? Size.Empty;
        }

        private bool IsSuspiciousRdpSurfaceSize(Size candidate, Size viewport)
        {
            if (!IsPositiveSize(candidate) || !IsPositiveSize(viewport))
                return true;

            if (candidate.Width < 100 || candidate.Height < 100)
                return true;

            var cap = GetSafeContentSizeCap(viewport);
            return candidate.Width > cap.Width || candidate.Height > cap.Height;
        }

        private Size GetSafeContentSizeCap(Size viewport)
        {
            var widthCap = Math.Min(SafeScrollAbsoluteMaximum,
                Math.Max(1, viewport.Width) * SafeScrollViewportMultiplier);
            var heightCap = Math.Min(SafeScrollAbsoluteMaximum,
                Math.Max(1, viewport.Height) * SafeScrollViewportMultiplier);
            return new Size(widthCap, heightCap);
        }

        private void LogNoRealScrollableDesktop(string source, Size viewport, Size rdpDesktop, Size configured)
        {
            var reason = InterfaceControl?.Info?.Resolution == RDPResolutions.FitToWindow
                ? "Scroll skipped: FitToWindow remote desktop equals viewport; no real scrollable area."
                : "Scroll skipped: no safe remote desktop size is larger than the viewport.";

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP passive scroll skipped from {source} for host '{connectionInfo?.Hostname}': {reason} " +
                $"viewport={FormatSize(viewport)}, rdpDesktop={FormatSize(rdpDesktop)}, configured={FormatSize(configured)}");
        }

        private void ApplyPassiveScrollSurface(ScrollableControl scrollable, Size surfaceSize, string source)
        {
            if (Control == null || Control.IsDisposed || scrollable == null || scrollable.IsDisposed)
                return;

            try
            {
                _applyingPassiveScrollLayout = true;
                Control.Location = Point.Empty;
                Control.Margin = Padding.Empty;
                Control.Anchor = AnchorStyles.Top | AnchorStyles.Left;

                if (Control.Size != surfaceSize)
                    Control.Size = surfaceSize;

                scrollable.AutoScroll = true;
                scrollable.AutoScrollMinSize = Control.Size;
                scrollable.PerformLayout();
            }
            finally
            {
                _applyingPassiveScrollLayout = false;
            }

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP passive scroll surface applied from {source} for host '{connectionInfo?.Hostname}': " +
                $"Control.Location={FormatPoint(Control.Location)}, Control.Size={FormatSize(Control.Size)}, " +
                $"AutoScrollMinSize={FormatSize(scrollable.AutoScrollMinSize)}");
        }

        private void ApplyUnsafePassiveScrollFallback(ScrollableControl scrollable, Size viewport, string source)
        {
            if (Control == null || Control.IsDisposed)
                return;

            try
            {
                _applyingPassiveScrollLayout = true;
                Control.Location = Point.Empty;
                Control.Margin = Padding.Empty;
                Control.Anchor = AnchorStyles.Top | AnchorStyles.Left;

                if (IsPositiveSize(viewport) && Control.Size != viewport)
                    Control.Size = viewport;

                if (scrollable != null && !scrollable.IsDisposed)
                {
                    scrollable.AutoScrollMinSize = Size.Empty;
                    scrollable.PerformLayout();
                }
            }
            finally
            {
                _applyingPassiveScrollLayout = false;
            }
        }

        private bool ValidatePassiveScrollInvariant(ScrollableControl scrollable, Size surfaceSize, Size viewport, string source)
        {
            var autoScrollMatchesControl = Control != null && SameSize(scrollable.AutoScrollMinSize, Control.Size);
            var controlMatchesSurface = Control != null && SameSize(Control.Size, surfaceSize);
            var originIsZero = Control != null && Control.Location == Point.Empty;
            var targetX = Control == null ? 0 : Math.Max(0, Control.Width - viewport.Width);
            var targetY = Control == null ? 0 : Math.Max(0, Control.Height - viewport.Height);
            var targetWithinSurface = Control != null &&
                                      targetX <= Math.Max(0, Control.Width - viewport.Width) &&
                                      targetY <= Math.Max(0, Control.Height - viewport.Height);

            if (autoScrollMatchesControl && controlMatchesSurface && originIsZero && targetWithinSurface)
                return true;

            Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg,
                $"RDP passive scroll invariant failed for host '{connectionInfo?.Hostname}' from {source}: " +
                $"AutoScrollMinSize={FormatSize(scrollable.AutoScrollMinSize)}, Control.Location={FormatPoint(Control?.Location ?? Point.Empty)}, " +
                $"Control.Bounds={FormatRectangle(Control?.Bounds ?? Rectangle.Empty)}, Control.Size={FormatSize(Control?.Size ?? Size.Empty)}, " +
                $"surfaceSize={FormatSize(surfaceSize)}, viewport={FormatSize(viewport)}, target={targetX},{targetY}, " +
                $"autoScrollMatchesControl={autoScrollMatchesControl}, controlMatchesSurface={controlMatchesSurface}, " +
                $"originIsZero={originIsZero}, targetWithinSurface={targetWithinSurface}");
            return false;
        }

        private static bool IsScrollAtTarget(ScrollableControl scrollable, int targetX, int targetY)
        {
            const int tolerance = 5;
            var final = scrollable.AutoScrollPosition;
            return Math.Abs(Math.Abs(final.X) - targetX) <= tolerance &&
                   Math.Abs(Math.Abs(final.Y) - targetY) <= tolerance;
        }

        private void EnableViewOnlyAfterSuccessfulPassiveLayout(string source, bool scrollNeeded)
        {
            if (_userManuallyDisabledViewOnly || !_autoEnableViewOnlyAfterSuccessfulScroll)
            {
                Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                    $"RDP passive scroll completed from {source} for host '{connectionInfo?.Hostname}' without auto ViewOnly: " +
                    $"userManuallyDisabledViewOnly={_userManuallyDisabledViewOnly}, " +
                    $"autoEnableViewOnlyAfterSuccessfulScroll={_autoEnableViewOnlyAfterSuccessfulScroll}");
                return;
            }

            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                scrollNeeded
                    ? $"RDP passive scroll completed; enabling ViewOnly for host '{connectionInfo?.Hostname}' from {source}"
                    : $"RDP passive scroll not needed; enabling ViewOnly for stable passive monitor layout for host '{connectionInfo?.Hostname}' from {source}");

            SetViewOnly(true, scrollNeeded
                ? "after successful passive scroll"
                : "after successful passive scroll/stable layout");
            _autoEnableViewOnlyAfterSuccessfulScroll = false;
        }

        private static bool IsPositiveSize(Size size)
        {
            return size.Width > 0 && size.Height > 0;
        }

        private static bool SameSize(Size first, Size second)
        {
            return first.Width == second.Width && first.Height == second.Height;
        }

        private string BuildScrollDiagnostics(int attempt, string source, ScrollableControl scrollable, Size contentSize,
            int targetX, int targetY, bool success)
        {
            var horizontalScroll = scrollable.HorizontalScroll;
            var verticalScroll = scrollable.VerticalScroll;
            var desktopSize = GetRdpDesktopSize();
            var smartSize = IsSmartSizeEnabledSafe();
            var noScrollReason = GetNoScrollReason(scrollable, contentSize, smartSize);
            var invariant = Control != null &&
                            Control.Location == Point.Empty &&
                            SameSize(Control.Size, contentSize) &&
                            SameSize(scrollable.AutoScrollMinSize, Control.Size);

            return "RDP scroll lower-right attempt " + attempt +
                   $" from {source}" +
                   $" for host '{connectionInfo?.Hostname}': container={scrollable.Name}/{scrollable.GetType().FullName}, " +
                   $"InterfaceControl.ClientSize={FormatSize(InterfaceControl?.ClientSize ?? Size.Empty)}, " +
                   $"AutoScroll={scrollable.AutoScroll}, AutoScrollMinSize={FormatSize(scrollable.AutoScrollMinSize)}, " +
                   $"DisplayRectangle={FormatRectangle(scrollable.DisplayRectangle)}, surfaceSize={FormatSize(contentSize)}, " +
                   $"Control.Location={FormatPoint(Control?.Location ?? Point.Empty)}, Control.Size={FormatSize(Control?.Size ?? Size.Empty)}, " +
                   $"Control.Bounds={FormatRectangle(Control?.Bounds ?? Rectangle.Empty)}, Control.Right={Control?.Right ?? 0}, Control.Bottom={Control?.Bottom ?? 0}, " +
                   $"Control.ClientSize={FormatSize(Control?.ClientSize ?? Size.Empty)}, RdpDesktop={FormatSize(desktopSize)}, " +
                   $"safetyCap={FormatSize(GetSafeContentSizeCap(scrollable.ClientSize))}, invariant={invariant}, " +
                   $"Info.Resolution={InterfaceControl?.Info?.Resolution}, SmartSize={smartSize}, " +
                   $"Fullscreen={Fullscreen}, EffectiveFullscreen={IsFullscreenEffective()}, AutomaticResize={InterfaceControl?.Info?.AutomaticResize}, " +
                   $"HVisible={horizontalScroll.Visible}, HMax={horizontalScroll.Maximum}, HLarge={horizontalScroll.LargeChange}, HValue={horizontalScroll.Value}, " +
                   $"VVisible={verticalScroll.Visible}, VMax={verticalScroll.Maximum}, VLarge={verticalScroll.LargeChange}, VValue={verticalScroll.Value}, " +
                   $"targetX={targetX}, targetY={targetY}, PassiveScrollMode={ShouldUsePassiveScrollMode()}, " +
                   $"noScrollReason={noScrollReason}, final={scrollable.AutoScrollPosition}, success={success}";
        }

        private string GetNoScrollReason(ScrollableControl scrollable, Size contentSize, bool smartSize)
        {
            if (smartSize || InterfaceControl?.Info?.Resolution == RDPResolutions.SmartSize)
                return "SmartSize is active; ActiveX scales the remote desktop, so WinForms scrollbars are not expected.";

            if (contentSize.Width <= scrollable.ClientSize.Width && contentSize.Height <= scrollable.ClientSize.Height)
                return "Content size is not larger than viewport; scrollbars are physically unavailable.";

            if (!scrollable.HorizontalScroll.Visible && !scrollable.VerticalScroll.Visible)
                return "Scrollable content is larger than viewport, but WinForms has not exposed scrollbars yet; retrying and using passive fallback.";

            return "Scrollbars available or not required on one axis.";
        }

        private bool IsSmartSizeEnabledSafe()
        {
            try
            {
                return _rdpClient != null && SmartSize;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatSize(Size size)
        {
            return $"{size.Width}x{size.Height}";
        }

        private static string FormatPoint(Point point)
        {
            return $"{point.X},{point.Y}";
        }

        private static string FormatRectangle(Rectangle rectangle)
        {
            return $"{rectangle.X},{rectangle.Y},{rectangle.Width}x{rectangle.Height}";
        }

        private static string FormatHandle(IntPtr handle)
        {
            return handle == IntPtr.Zero ? "0x0" : "0x" + handle.ToInt64().ToString("X");
        }

        private static string FormatControlName(Control control)
        {
            if (control == null)
                return "<null>";

            return $"{control.Name}/{control.GetType().FullName}";
        }

        private void SetRdpClientProperties()
        {
            _rdpClient.Server = connectionInfo.Hostname;

            SetCredentials();
            SetResolution();
            _rdpClient.FullScreenTitle = connectionInfo.Name;

            _alertOnIdleDisconnect = connectionInfo.RDPAlertIdleTimeout;
            _rdpClient.AdvancedSettings2.MinutesToIdleTimeout = connectionInfo.RDPMinutesToIdleTimeout;

            #region Remote Desktop Services
            _rdpClient.SecuredSettings2.StartProgram = connectionInfo.RDPStartProgram;
            _rdpClient.SecuredSettings2.WorkDir = connectionInfo.RDPStartProgramWorkDir;
            #endregion

            //not user changeable
            _rdpClient.AdvancedSettings2.GrabFocusOnConnect = false;
            _rdpClient.AdvancedSettings3.EnableAutoReconnect = true;
            _rdpClient.AdvancedSettings3.MaxReconnectAttempts = Settings.Default.RdpReconnectionCount;
            _rdpClient.AdvancedSettings2.keepAliveInterval = 60000; //in milliseconds (10,000 = 10 seconds)
            _rdpClient.AdvancedSettings5.AuthenticationLevel = 0;
            _rdpClient.AdvancedSettings2.EncryptionEnabled = 1;

            _rdpClient.AdvancedSettings2.overallConnectionTimeout = Settings.Default.ConRDPOverallConnectionTimeout;

            _rdpClient.AdvancedSettings2.BitmapPeristence = Convert.ToInt32(connectionInfo.CacheBitmaps);
            if (_rdpVersion >= Versions.RDC61)
            {
                _rdpClient.AdvancedSettings7.EnableCredSspSupport = connectionInfo.UseCredSsp;
            }

            SetUseConsoleSession();
            SetPort();
            RedirectKeys = connectionInfo.RedirectKeys;
            SetRedirection();
            SetAuthenticationLevel();
            SetLoadBalanceInfo();
            SetRdGateway();
            ApplyFullscreenViewOnlyPolicy("SetRdpClientProperties");

            _rdpClient.ColorDepth = (int)connectionInfo.Colors;

            SetPerformanceFlags();

            _rdpClient.ConnectingText = Language.Connecting;
        }

        protected object GetExtendedProperty(string property)
        {
            try
            {
                // ReSharper disable once UseIndexedProperty
                return ((IMsRdpExtendedSettings)_rdpClient).get_Property(property);
            }
            catch (Exception e)
            {
                Runtime.MessageCollector.AddExceptionMessage($"Error getting extended RDP property '{property}'",
                                                             e, MessageClass.WarningMsg, false);
                return null;
            }
        }

        protected void SetExtendedProperty(string property, object value)
        {
            try
            {
                // ReSharper disable once UseIndexedProperty
                ((IMsRdpExtendedSettings)_rdpClient).set_Property(property, ref value);
            }
            catch (Exception e)
            {
                Runtime.MessageCollector.AddExceptionMessage($"Error setting extended RDP property '{property}'",
                                                             e, MessageClass.WarningMsg, false);
            }
        }

        private void SetRdGateway()
        {
            try
            {
                if (_rdpClient.TransportSettings.GatewayIsSupported == 0)
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.RdpGatewayNotSupported,
                                                        true);
                    return;
                }

                Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.RdpGatewayIsSupported,
                                                    true);

                if (connectionInfo.RDGatewayUsageMethod != RDGatewayUsageMethod.Never)
                {
                    _rdpClient.TransportSettings.GatewayUsageMethod = (uint)connectionInfo.RDGatewayUsageMethod;
                    _rdpClient.TransportSettings.GatewayHostname = connectionInfo.RDGatewayHostname;
                    _rdpClient.TransportSettings.GatewayProfileUsageMethod = 1; // TSC_PROXY_PROFILE_MODE_EXPLICIT
                    if (connectionInfo.RDGatewayUseConnectionCredentials ==
                        RDGatewayUseConnectionCredentials.SmartCard)
                    {
                        _rdpClient.TransportSettings.GatewayCredsSource = 1; // TSC_PROXY_CREDS_MODE_SMARTCARD
                    }

                    if (_rdpVersion >= Versions.RDC61 && !Force.HasFlag(ConnectionInfo.Force.NoCredentials))
                    {
                        if (connectionInfo.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.Yes)
                        {
                            _rdpClient.TransportSettings2.GatewayUsername = connectionInfo.Username;
                            _rdpClient.TransportSettings2.GatewayPassword = connectionInfo.Password;
                            _rdpClient.TransportSettings2.GatewayDomain = connectionInfo?.Domain;
                        }
                        else if (connectionInfo.RDGatewayUseConnectionCredentials ==
                                 RDGatewayUseConnectionCredentials.SmartCard)
                        {
                            _rdpClient.TransportSettings2.GatewayCredSharing = 0;
                        }
                        else
                        {
                            _rdpClient.TransportSettings2.GatewayUsername = connectionInfo.RDGatewayUsername;
                            _rdpClient.TransportSettings2.GatewayPassword = connectionInfo.RDGatewayPassword;
                            _rdpClient.TransportSettings2.GatewayDomain = connectionInfo.RDGatewayDomain;
                            _rdpClient.TransportSettings2.GatewayCredSharing = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetGatewayFailed, ex);
            }
        }

        private void SetUseConsoleSession()
        {
            try
            {
                bool value;

                if (Force.HasFlag(ConnectionInfo.Force.UseConsoleSession))
                {
                    value = true;
                }
                else if (Force.HasFlag(ConnectionInfo.Force.DontUseConsoleSession))
                {
                    value = false;
                }
                else
                {
                    value = connectionInfo.UseConsoleSession;
                }

                if (_rdpVersion >= Versions.RDC61)
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg,
                                                        string.Format(Language.RdpSetConsoleSwitch, _rdpVersion),
                                                        true);
                    _rdpClient.AdvancedSettings7.ConnectToAdministerServer = value;
                }
                else
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg,
                                                        string.Format(Language.RdpSetConsoleSwitch, _rdpVersion) +
                                                        Environment.NewLine +
                                                        "No longer supported in this RDP version. Reference: https://msdn.microsoft.com/en-us/library/aa380863(v=vs.85).aspx",
                                                        true);
                    // ConnectToServerConsole is deprecated
                    //https://msdn.microsoft.com/en-us/library/aa380863(v=vs.85).aspx
                    //_rdpClient.AdvancedSettings2.ConnectToServerConsole = value;
                }
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetConsoleSessionFailed, ex);
            }
        }

        private void SetCredentials()
        {
            try
            {
                if (Force.HasFlag(ConnectionInfo.Force.NoCredentials))
                {
                    return;
                }

                var userName = connectionInfo?.Username ?? "";
                var password = connectionInfo?.Password ?? "";
                var domain = connectionInfo?.Domain ?? "";

                // access secret server api if necessary
                if (!string.IsNullOrEmpty(connectionInfo?.UserViaAPI))
                {
                    try
                    {
                        ExternalConnectors.TSS.SecretServerInterface.FetchSecretFromServer("SSAPI:" + connectionInfo?.UserViaAPI, out userName, out password, out domain);
                    }
                    catch (Exception ex)
                    {
                        Event_ErrorOccured(this, "Secret Server Interface Error: " + ex.Message, 0);
                    }
                    
                }

                if (string.IsNullOrEmpty(userName))
                {
                    if (Settings.Default.EmptyCredentials == "windows")
                    {
                        _rdpClient.UserName = Environment.UserName;
                    }
                    else if (Settings.Default.EmptyCredentials == "custom")
                    {
                        _rdpClient.UserName = Settings.Default.DefaultUsername;
                    }
                }
                else
                {
                    _rdpClient.UserName = userName;
                }

                if (string.IsNullOrEmpty(password))
                {
                    if (Settings.Default.EmptyCredentials == "custom")
                    {
                        if (Settings.Default.DefaultPassword != "")
                        {
                            var cryptographyProvider = new LegacyRijndaelCryptographyProvider();
                            _rdpClient.AdvancedSettings2.ClearTextPassword =
                                cryptographyProvider.Decrypt(Settings.Default.DefaultPassword, Runtime.EncryptionKey);
                        }
                    }
                }
                else
                {
                    _rdpClient.AdvancedSettings2.ClearTextPassword = password;
                }

                if (string.IsNullOrEmpty(domain))
                {
                    if (Settings.Default.EmptyCredentials == "windows")
                    {
                        _rdpClient.Domain = Environment.UserDomainName;
                    }
                    else if (Settings.Default.EmptyCredentials == "custom")
                    {
                        _rdpClient.Domain = Settings.Default.DefaultDomain;
                    }
                }
                else
                {
                    _rdpClient.Domain = domain;
                }
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetCredentialsFailed, ex);
            }
        }

        private void SetResolution()
        {
            try
            {
                var scaleFactor = (uint)(_displayProperties.ResolutionScalingFactor.Width * 100);
                SetExtendedProperty("DesktopScaleFactor", scaleFactor);
                SetExtendedProperty("DeviceScaleFactor", (uint)100);

                if (Force.HasFlag(ConnectionInfo.Force.Fullscreen))
                {
                    _fullscreenRequestedByMRemote = true;
                    _fullscreenExitRequestedByMRemote = false;
                    SetRdpClientFullscreen(true, "SetResolution Force.Fullscreen");
                    MarkRdpFullscreenActive(true, "SetResolution Force.Fullscreen");
                    _rdpClient.DesktopWidth = Screen.FromControl(_frmMain).Bounds.Width;
                    _rdpClient.DesktopHeight = Screen.FromControl(_frmMain).Bounds.Height;

                    return;
                }

                if (InterfaceControl.Info.Resolution == RDPResolutions.FitToWindow ||
                    InterfaceControl.Info.Resolution == RDPResolutions.SmartSize)
                {
                    _rdpClient.DesktopWidth = InterfaceControl.Size.Width;
                    _rdpClient.DesktopHeight = InterfaceControl.Size.Height;

                    if (InterfaceControl.Info.Resolution == RDPResolutions.SmartSize)
                    {
                        _rdpClient.AdvancedSettings2.SmartSizing = true;
                    }
                }
                else if (InterfaceControl.Info.Resolution == RDPResolutions.Fullscreen)
                {
                    _fullscreenRequestedByMRemote = true;
                    _fullscreenExitRequestedByMRemote = false;
                    SetRdpClientFullscreen(true, "SetResolution Resolution.Fullscreen");
                    MarkRdpFullscreenActive(true, "SetResolution Resolution.Fullscreen");
                    _rdpClient.DesktopWidth = Screen.FromControl(_frmMain).Bounds.Width;
                    _rdpClient.DesktopHeight = Screen.FromControl(_frmMain).Bounds.Height;
                }
                else
                {
                    var resolution = connectionInfo.Resolution.GetResolutionRectangle();
                    _rdpClient.DesktopWidth = resolution.Width;
                    _rdpClient.DesktopHeight = resolution.Height;
                }

                ApplyRdpControlSizeForCurrentResolution("SetResolution");
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetResolutionFailed, ex);
            }
        }

        private void SetPort()
        {
            try
            {
                if (connectionInfo.Port != (int)Defaults.Port)
                {
                    _rdpClient.AdvancedSettings2.RDPPort = connectionInfo.Port;
                }
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetPortFailed, ex);
            }
        }

        private void SetRedirection()
        {
            try
            {
                _rdpClient.AdvancedSettings2.RedirectDrives = connectionInfo.RedirectDiskDrives;
                _rdpClient.AdvancedSettings2.RedirectPorts = connectionInfo.RedirectPorts;
                _rdpClient.AdvancedSettings2.RedirectPrinters = connectionInfo.RedirectPrinters;
                _rdpClient.AdvancedSettings2.RedirectSmartCards = connectionInfo.RedirectSmartCards;
                _rdpClient.SecuredSettings2.AudioRedirectionMode = (int)connectionInfo.RedirectSound;
                _rdpClient.AdvancedSettings6.RedirectClipboard = connectionInfo.RedirectClipboard;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetRedirectionFailed, ex);
            }
        }

        private void SetPerformanceFlags()
        {
            try
            {
                var pFlags = 0;
                if (connectionInfo.DisplayThemes == false)
                    pFlags += (int)RDPPerformanceFlags.DisableThemes;

                if (connectionInfo.DisplayWallpaper == false)
                    pFlags += (int)RDPPerformanceFlags.DisableWallpaper;

                if (connectionInfo.EnableFontSmoothing)
                    pFlags += (int)RDPPerformanceFlags.EnableFontSmoothing;

                if (connectionInfo.EnableDesktopComposition)
                    pFlags += (int)RDPPerformanceFlags.EnableDesktopComposition;

                if (connectionInfo.DisableFullWindowDrag)
                    pFlags += (int)RDPPerformanceFlags.DisableFullWindowDrag;

                if (connectionInfo.DisableMenuAnimations)
                    pFlags += (int)RDPPerformanceFlags.DisableMenuAnimations;

                if (connectionInfo.DisableCursorShadow)
                    pFlags += (int)RDPPerformanceFlags.DisableCursorShadow;

                if (connectionInfo.DisableCursorBlinking)
                    pFlags += (int)RDPPerformanceFlags.DisableCursorBlinking;

                _rdpClient.AdvancedSettings2.PerformanceFlags = pFlags;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetPerformanceFlagsFailed, ex);
            }
        }

        private void SetAuthenticationLevel()
        {
            try
            {
                _rdpClient.AdvancedSettings5.AuthenticationLevel = (uint)connectionInfo.RDPAuthenticationLevel;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetAuthenticationLevelFailed, ex);
            }
        }

        private void SetLoadBalanceInfo()
        {
            if (string.IsNullOrEmpty(connectionInfo.LoadBalanceInfo))
            {
                return;
            }

            try
            {
                _rdpClient.AdvancedSettings2.LoadBalanceInfo = LoadBalanceInfoUseUtf8
                    ? new AzureLoadBalanceInfoEncoder().Encode(connectionInfo.LoadBalanceInfo)
                    : connectionInfo.LoadBalanceInfo;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("Unable to set load balance info.", ex);
            }
        }

        private void SetEventHandlers()
        {
            try
            {
                _rdpClient.OnConnecting += RDPEvent_OnConnecting;
                _rdpClient.OnConnected += RDPEvent_OnConnected;
                _rdpClient.OnLoginComplete += RDPEvent_OnLoginComplete;
                _rdpClient.OnFatalError += RDPEvent_OnFatalError;
                _rdpClient.OnDisconnected += RDPEvent_OnDisconnected;
                _rdpClient.OnEnterFullScreenMode += RDPEvent_OnEnterFullscreenMode;
                _rdpClient.OnLeaveFullScreenMode += RDPEvent_OnLeaveFullscreenMode;
                _rdpClient.OnRequestGoFullScreen += RDPEvent_OnRequestGoFullscreen;
                _rdpClient.OnRequestLeaveFullScreen += RDPEvent_OnRequestLeaveFullscreen;
                _rdpClient.OnRemoteDesktopSizeChange += RDPEvent_OnRemoteDesktopSizeChange;
                _rdpClient.OnAutoReconnecting += RDPEvent_OnAutoReconnecting;
                _rdpClient.OnAutoReconnected += RDPEvent_OnAutoReconnected;
                _rdpClient.OnIdleTimeoutNotification += RDPEvent_OnIdleTimeoutNotification;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.RdpSetEventHandlersFailed, ex);
            }
        }

        #endregion

        #region Private Events & Handlers

        private void RDPEvent_OnIdleTimeoutNotification()
        {
            Close(); //Simply close the RDP Session if the idle timeout has been triggered.

            if (!_alertOnIdleDisconnect) return;
            MessageBox.Show($"The {connectionInfo.Name} session was disconnected due to inactivity",
                            "Session Disconnected", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }


        private void RDPEvent_OnFatalError(int errorCode)
        {
            EndAutomaticReconnect();
            var errorMsg = RdpErrorCodes.GetError(errorCode);
            Event_ErrorOccured(this, errorMsg, errorCode);
        }

        private void RDPEvent_OnDisconnected(int discReason)
        {
            if (_automaticReconnectInProgress && !Settings.Default.ReconnectOnDisconnect)
                EndAutomaticReconnect();

            const int UI_ERR_NORMAL_DISCONNECT = 0xB08;
            if (discReason != UI_ERR_NORMAL_DISCONNECT)
            {
                var reason =
                    _rdpClient.GetErrorDescription((uint)discReason, (uint)_rdpClient.ExtendedDisconnectReason);
                Event_Disconnected(this, reason, discReason);
            }

            if (Settings.Default.ReconnectOnDisconnect)
            {
                ReconnectGroup = new ReconnectGroup();
                ReconnectGroup.CloseClicked += Event_ReconnectGroupCloseClicked;
                ReconnectGroup.Left = (int)((double)Control.Width / 2 - (double)ReconnectGroup.Width / 2);
                ReconnectGroup.Top = (int)((double)Control.Height / 2 - (double)ReconnectGroup.Height / 2);
                ReconnectGroup.Parent = Control;
                ReconnectGroup.Show();
                tmrReconnect.Enabled = true;
            }
            else
            {
                Close();
            }
        }

        private void RDPEvent_OnConnecting()
        {
            Event_Connecting(this);
        }

        private void RDPEvent_OnConnected()
        {
            Event_Connected(this);
            AllowAutoViewOnlyAfterPassiveScroll("OnConnected");
            ApplyFullscreenViewOnlyPolicy("OnConnected");
            ScrollToLowerRightAsync("OnConnected");
        }

        private void RDPEvent_OnLoginComplete()
        {
            loginComplete = true;
            _hasCompletedInitialConnect = true;
            AllowAutoViewOnlyAfterPassiveScroll("OnLoginComplete");
            ApplyFullscreenViewOnlyPolicy("OnLoginComplete");
            ScrollToLowerRightAsync("OnLoginComplete");
            EndAutomaticReconnect();
        }

        private void RDPEvent_OnEnterFullscreenMode()
        {
            StopFullscreenExitFinalizer("OnEnterFullScreenMode");
            MarkRdpFullscreenActive(true, "OnEnterFullScreenMode");
            ApplyFullscreenViewOnlyPolicy("OnEnterFullScreenMode");
            StopScrollRetryTimer();
        }

        private void RDPEvent_OnLeaveFullscreenMode()
        {
            _fullscreenExitRequestedByMRemote = false;
            MarkRdpFullscreenActive(false, "OnLeaveFullScreenMode");
            ApplyFullscreenViewOnlyPolicy("OnLeaveFullScreenMode");
            HandleFullscreenLeaveLayout("OnLeaveFullScreenMode");
            _leaveFullscreenEvent?.Invoke(this, new EventArgs());
        }

        private void RDPEvent_OnRequestGoFullscreen()
        {
            StopFullscreenExitFinalizer("OnRequestGoFullScreen");
            _fullscreenRequestedByMRemote = true;
            MarkRdpFullscreenActive(true, "OnRequestGoFullScreen");
            ApplyFullscreenViewOnlyPolicy("OnRequestGoFullScreen");
            StopScrollRetryTimer();
        }

        private void RDPEvent_OnRequestLeaveFullscreen()
        {
            _fullscreenExitRequestedByMRemote = true;
            MarkRdpFullscreenActive(false, "OnRequestLeaveFullScreen");
            ApplyFullscreenViewOnlyPolicy("OnRequestLeaveFullScreen");
            HandleFullscreenLeaveLayout("OnRequestLeaveFullScreen");
        }

        private void RDPEvent_OnRemoteDesktopSizeChange(int width, int height)
        {
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP remote desktop size changed for host '{connectionInfo?.Hostname}': width={width}, height={height}");
            NormalizeRdpScrollOrigin(FindRdpScrollContainer(), "OnRemoteDesktopSizeChange");
            ApplyRdpControlSizeForCurrentResolution("OnRemoteDesktopSizeChange");
            ScrollToLowerRightAsync("OnRemoteDesktopSizeChange");
        }

        private AutoReconnectContinueState RDPEvent_OnAutoReconnecting(int disconnectReason, int attemptCount)
        {
            BeginAutomaticReconnect();
            ApplyFullscreenViewOnlyPolicy("OnAutoReconnecting");
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP ActiveX autoreconnecting for host '{connectionInfo?.Hostname}': " +
                $"disconnectReason={disconnectReason}, attemptCount={attemptCount}");

            return AutoReconnectContinueState.autoReconnectContinueAutomatic;
        }

        private void RDPEvent_OnAutoReconnected()
        {
            AllowAutoViewOnlyAfterPassiveScroll("OnAutoReconnected");
            ApplyFullscreenViewOnlyPolicy("OnAutoReconnected");
            ScrollToLowerRightAsync("OnAutoReconnected");
            EndAutomaticReconnect();
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP ActiveX autoreconnected for host '{connectionInfo?.Hostname}'");
        }

        private void RdpClient_GotFocus(object sender, EventArgs e)
        {
            if (ShouldSuppressRdpFocus())
                return;

            ((ConnectionTab)Control.Parent.Parent).Focus();
        }

        private void RdpClient_HandleCreated(object sender, EventArgs e)
        {
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP control handle created for host '{connectionInfo?.Hostname}': handle={FormatHandle(Control?.Handle ?? IntPtr.Zero)}");
            SetViewOnly(_viewOnly, "Control.HandleCreated");
            NormalizeRdpScrollOrigin(FindRdpScrollContainer(), "Control.HandleCreated");
            ScrollToLowerRightAsync("Control.HandleCreated");
        }

        private void RdpClient_ParentChanged(object sender, EventArgs e)
        {
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                $"RDP control parent changed for host '{connectionInfo?.Hostname}': parent={Control?.Parent?.GetType().FullName}");
            NormalizeRdpScrollOrigin(FindRdpScrollContainer(), "Control.ParentChanged");
            ApplyRdpControlSizeForCurrentResolution("Control.ParentChanged");
            ScrollToLowerRightAsync("Control.ParentChanged");
        }

        private void RdpClient_Disposed(object sender, EventArgs e)
        {
            _fullscreenPollTimer?.Stop();
            _fullscreenPollTimer?.Dispose();
            _fullscreenPollTimer = null;
            _fullscreenLeaveScrollTimer?.Stop();
            _fullscreenLeaveScrollTimer?.Dispose();
            _fullscreenLeaveScrollTimer = null;
            _scrollRetryTimer?.Stop();
            _scrollRetryTimer?.Dispose();
            _scrollRetryTimer = null;
            if (_passiveScrollCommitTimer != null)
            {
                _passiveScrollCommitTimer.Stop();
                _passiveScrollCommitTimer.Tick -= PassiveScrollCommitTimerOnTick;
                _passiveScrollCommitTimer.Dispose();
                _passiveScrollCommitTimer = null;
            }
            _fullscreenExitFinalizeTimer?.Stop();
            _fullscreenExitFinalizeTimer?.Dispose();
            _fullscreenExitFinalizeTimer = null;
            _rdpSafeFocusSink?.Dispose();
            _rdpSafeFocusSink = null;
            _viewOnly = false;
            _userManuallyDisabledViewOnly = false;
            _autoEnableViewOnlyAfterSuccessfulScroll = false;
            InputBlocker.SetBlocked(Control, false);

            if (InterfaceControl != null)
                InterfaceControl.Resize -= InterfaceControl_Resize;
        }

        private void InterfaceControl_Resize(object sender, EventArgs e)
        {
            ApplyRdpControlSizeForCurrentResolution("InterfaceControl.Resize");
            ScrollToLowerRightAsync("InterfaceControl.Resize");
        }

        private sealed class PassiveRdpFocusSink : Control
        {
            public PassiveRdpFocusSink()
            {
                SetStyle(ControlStyles.Selectable, true);
            }
        }
        #endregion

        #region Public Events & Handlers

        public delegate void LeaveFullscreenEventHandler(object sender, EventArgs e);

        private LeaveFullscreenEventHandler _leaveFullscreenEvent;

        public event LeaveFullscreenEventHandler LeaveFullscreen
        {
            add => _leaveFullscreenEvent = (LeaveFullscreenEventHandler)Delegate.Combine(_leaveFullscreenEvent, value);
            remove =>
                _leaveFullscreenEvent = (LeaveFullscreenEventHandler)Delegate.Remove(_leaveFullscreenEvent, value);
        }

        #endregion

        #region Enums

        public enum Defaults
        {
            Colors = RDPColors.Colors16Bit,
            Sounds = RDPSounds.DoNotPlay,
            Resolution = RDPResolutions.FitToWindow,
            Port = 3389
        }

        #endregion

        public static class Versions
        {
            public static readonly Version RDC60 = new Version(6, 0, 6000);
            public static readonly Version RDC61 = new Version(6, 0, 6001);
            public static readonly Version RDC70 = new Version(6, 1, 7600);
            public static readonly Version RDC80 = new Version(6, 2, 9200);
            public static readonly Version RDC81 = new Version(6, 3, 9600);
            public static readonly Version RDC100 = new Version(10, 0, 0);
        }

        #region Reconnect Stuff

        public void tmrReconnect_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                var srvReady = PortScanner.IsPortOpen(connectionInfo.Hostname, Convert.ToString(connectionInfo.Port));

                ReconnectGroup.ServerReady = srvReady;

                if (!ReconnectGroup.ReconnectWhenReady || !srvReady) return;
                tmrReconnect.Enabled = false;
                ReconnectGroup.DisposeReconnectGroup();
                //SetProps()
                BeginAutomaticReconnect();
                ApplyFullscreenViewOnlyPolicy("mRemoteNG timer reconnect");
                _rdpClient.Connect();
            }
            catch (Exception ex)
            {
                EndAutomaticReconnect();
                Runtime.MessageCollector.AddExceptionMessage(
                    string.Format(Language.AutomaticReconnectError, connectionInfo.Hostname),
                    ex, MessageClass.WarningMsg, false);
            }
        }

        #endregion
    }
}

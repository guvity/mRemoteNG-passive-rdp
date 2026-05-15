using AxMSTSCLib;
using mRemoteNG.App;
using mRemoteNG.Messages;
using mRemoteNG.Properties;
using mRemoteNG.Resources.Language;
using mRemoteNG.Security.SymmetricEncryption;
using mRemoteNG.Tools;
using mRemoteNG.Tree.Root;
using mRemoteNG.UI;
using mRemoteNG.UI.Forms;
using mRemoteNG.UI.Tabs;
using MSTSCLib;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Timers;
using System.Windows.Forms;

namespace mRemoteNG.Connection.Protocol.RDP
{
    [SupportedOSPlatform("windows")]
    public class RdpProtocol : ProtocolBase, ISupportsViewOnly
    {
        /* RDP v8 requires Windows 7 with:
         * https://support.microsoft.com/en-us/kb/2592687
         * OR
         * https://support.microsoft.com/en-us/kb/2923545
         *
         * Windows 8+ support RDP v8 out of the box.
         */

        private MsRdpClient6NotSafeForScripting _rdpClient; // lowest version supported
        protected virtual RdpVersion RdpProtocolVersion => RDP.RdpVersion.Rdc6;
        protected ConnectionInfo connectionInfo;
        protected Version RdpVersion;
        private readonly DisplayProperties _displayProperties;
        protected readonly FrmMain _frmMain = FrmMain.Default;
        protected bool loginComplete;
        private bool _redirectKeys;
        private bool _alertOnIdleDisconnect;
        private bool _viewOnly;
        private bool _suppressFocusOnAutomaticReconnect;
        private bool _userDisabledViewOnlyInFullscreen;
        private readonly System.Windows.Forms.Timer _bottomRightScrollTimer;
        private readonly System.Windows.Forms.Timer _reconnectFocusSuppressionTimer;
        private int _bottomRightScrollAttempts;
        private RdpInputBlocker _inputBlocker;
        protected uint DesktopScaleFactor => (uint)(_displayProperties.ResolutionScalingFactor.Width * 100);
        protected readonly uint DeviceScaleFactor = 100;
        protected readonly uint Orientation = 0;
        private AxHost AxHost => (AxHost)Control;


        #region Properties

        public virtual bool SmartSize
        {
            get
            {
                try
                {
                    return _rdpClient.AdvancedSettings2.SmartSizing;
                }
                catch (System.Runtime.InteropServices.InvalidComObjectException)
                {
                    // The COM object is separated from its RCW, try reacquiring the RCW or recreating the COM object
                    _rdpClient = new MsRdpClient6NotSafeForScripting();
                    return _rdpClient.AdvancedSettings2.SmartSizing;
                }
            }
            protected set
            {
                try
                {
                    _rdpClient.AdvancedSettings2.SmartSizing = value;
                }
                catch (System.Runtime.InteropServices.InvalidComObjectException)
                {
                    // The COM object is separated from its RCW, try reacquiring the RCW or recreating the COM object
                    _rdpClient = new MsRdpClient6NotSafeForScripting();
                    _rdpClient.AdvancedSettings2.SmartSizing = value;
                }
            }
        }

        public virtual bool Fullscreen
        {
            get => _rdpClient.FullScreen;
            protected set => _rdpClient.FullScreen = value;
        }

        private bool RedirectKeys
        {
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
                    IMsRdpClientSecuredSettings msRdpClientSecuredSettings = _rdpClient.SecuredSettings2;
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
            set => SetViewOnly(value, false);
        }

        #endregion

        #region Constructors

        public RdpProtocol()
        {
            _displayProperties = new DisplayProperties();
            tmrReconnect.Elapsed += tmrReconnect_Elapsed;

            _bottomRightScrollTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _bottomRightScrollTimer.Tick += BottomRightScrollTimer_Tick;

            _reconnectFocusSuppressionTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _reconnectFocusSuppressionTimer.Tick += ReconnectFocusSuppressionTimer_Tick;
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
            Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg, $"Requesting RDP version: {connectionInfo.RdpVersion}. Using: {RdpProtocolVersion}");
            Control = CreateActiveXRdpClientControl();
            base.Initialize();

            try
            {
                if (!InitializeActiveXControl()) return false;

                RdpVersion = new Version(_rdpClient.Version);

                if (RdpVersion < Versions.RDC61) return false; // only RDP versions 6.1 and greater are supported; minimum dll version checked, MSTSCLIB is not capable 

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
                if (!Properties.OptionsStartupExitPage.Default.DisableRefocus)
                {
                    Control.GotFocus += RdpClient_GotFocus;
                }

                Control.CreateControl();

                while (!Control.Created)
                {
                    Thread.Sleep(50);
                    Application.DoEvents();
                }
                Control.Anchor = AnchorStyles.None;

                _rdpClient = (MsRdpClient6NotSafeForScripting)((AxHost)Control).GetOcx();
                _inputBlocker = new RdpInputBlocker(Control);
                
                return true;
            }
            catch (COMException ex)
            {
                if (ex.Message.Contains("CLASS_E_CLASSNOTAVAILABLE"))
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.ErrorMsg, string.Format(Language.RdpProtocolVersionNotSupported, connectionInfo.RdpVersion));
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

        public override void Close()
        {
            try
            {
                if (_rdpClient != null)
                {
                    _rdpClient.OnConnecting -= RDPEvent_OnConnecting;
                    _rdpClient.OnConnected -= RDPEvent_OnConnected;
                    _rdpClient.OnLoginComplete -= RDPEvent_OnLoginComplete;
                    _rdpClient.OnFatalError -= RDPEvent_OnFatalError;
                    _rdpClient.OnDisconnected -= RDPEvent_OnDisconnected;
                    _rdpClient.OnIdleTimeoutNotification -= RDPEvent_OnIdleTimeoutNotification;
                    _rdpClient.OnLeaveFullScreenMode -= RDPEvent_OnLeaveFullscreenMode;
                }

                if (Control != null)
                {
                    Control.GotFocus -= RdpClient_GotFocus;
                }

                _bottomRightScrollTimer.Stop();
                _bottomRightScrollTimer.Tick -= BottomRightScrollTimer_Tick;
                _reconnectFocusSuppressionTimer.Stop();
                _reconnectFocusSuppressionTimer.Tick -= ReconnectFocusSuppressionTimer_Tick;
                _inputBlocker?.Dispose();
                _inputBlocker = null;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace("RdpProtocol: error unsubscribing event handlers", ex);
            }

            base.Close();
        }

        public void ToggleFullscreen()
        {
            try
            {
                bool enteringFullscreen = !Fullscreen;
                Fullscreen = enteringFullscreen;
                ApplyFullscreenViewOnlyState(enteringFullscreen);
                ScheduleScrollToDesktopBottomRight();
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
                SetViewOnly(!ViewOnly, true);
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
                if (_suppressFocusOnAutomaticReconnect || ViewOnly)
                {
                    return;
                }

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
                using AxHost control = CreateActiveXRdpClientControl();
                control.CreateControl();
                return true;
            }
            catch
            {
                return false;
            }
        }
        #endregion

        #region Private Methods

        protected static class Versions
        {
            // https://en.wikipedia.org/wiki/Remote_Desktop_Protocol
            public static readonly Version RDC60 = new(6, 0, 6000);
            public static readonly Version RDC61 = new(6, 0, 6001);
            public static readonly Version RDC70 = new(6, 1, 7600);
            public static readonly Version RDC80 = new(6, 2, 9200);
            public static readonly Version RDC81 = new(6, 3, 9600);
            public static readonly Version RDC100 = new(10, 0, 0);
        }

        private void SetRdpClientProperties()
        {
            // https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/clients/rdp-files

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

            if (RdpVersion >= Versions.RDC61)
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
            ApplyFullscreenViewOnlyState(IsRdpFullscreen());

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
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage($"Error getting extended RDP property '{property}'", ex, MessageClass.WarningMsg, false);
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
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage($"Error setting extended RDP property '{property}'", ex, MessageClass.WarningMsg, false);
            }
        }

        private bool IsRdpFullscreen()
        {
            try
            {
                return Fullscreen;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyFullscreenViewOnlyState(bool isFullscreen)
        {
            if (isFullscreen)
            {
                if (!_userDisabledViewOnlyInFullscreen)
                {
                    SetViewOnly(true, false);
                }

                return;
            }

            _userDisabledViewOnlyInFullscreen = false;
            SetViewOnly(false, false);
        }

        private void SetViewOnly(bool value, bool userInitiated)
        {
            bool isFullscreen = IsRdpFullscreen();

            if (!isFullscreen)
            {
                value = false;
            }

            _viewOnly = value;

            if (_inputBlocker != null)
            {
                _inputBlocker.Enabled = value;
            }

            if (userInitiated && isFullscreen)
            {
                _userDisabledViewOnlyInFullscreen = !value;
            }

            if (value && Control != null && Control.ContainsFocus && InterfaceControl != null && !InterfaceControl.IsDisposed)
            {
                InterfaceControl.Focus();
            }
        }

        protected void ScheduleScrollToDesktopBottomRight()
        {
            if (InterfaceControl == null || InterfaceControl.IsDisposed)
            {
                return;
            }

            void StartScrollTimer()
            {
                if (InterfaceControl == null || InterfaceControl.IsDisposed)
                {
                    return;
                }

                _bottomRightScrollAttempts = 8;
                _bottomRightScrollTimer.Stop();
                ScrollToDesktopBottomRight();
                _bottomRightScrollTimer.Start();
            }

            try
            {
                if (InterfaceControl.InvokeRequired)
                {
                    InterfaceControl.BeginInvoke(new Action(StartScrollTimer));
                }
                else
                {
                    StartScrollTimer();
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void BottomRightScrollTimer_Tick(object sender, EventArgs e)
        {
            if (_bottomRightScrollAttempts-- <= 0)
            {
                _bottomRightScrollTimer.Stop();
                return;
            }

            ScrollToDesktopBottomRight();
        }

        private void ScrollToDesktopBottomRight()
        {
            if (InterfaceControl == null || InterfaceControl.IsDisposed || !InterfaceControl.AutoScroll)
            {
                return;
            }

            try
            {
                InterfaceControl.PerformLayout();

                int x = Math.Max(0, InterfaceControl.HorizontalScroll.Maximum - InterfaceControl.HorizontalScroll.LargeChange + 1);
                int y = Math.Max(0, InterfaceControl.VerticalScroll.Maximum - InterfaceControl.VerticalScroll.LargeChange + 1);

                InterfaceControl.AutoScrollPosition = new Point(x, y);
            }
            catch (InvalidOperationException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void ScheduleClearAutomaticReconnectFocusSuppression()
        {
            if (Control == null || Control.IsDisposed)
            {
                _suppressFocusOnAutomaticReconnect = false;
                return;
            }

            void StartTimer()
            {
                _reconnectFocusSuppressionTimer.Stop();
                _reconnectFocusSuppressionTimer.Start();
            }

            try
            {
                if (Control.InvokeRequired)
                {
                    Control.BeginInvoke(new Action(StartTimer));
                }
                else
                {
                    StartTimer();
                }
            }
            catch (InvalidOperationException)
            {
                _suppressFocusOnAutomaticReconnect = false;
            }
            catch (ObjectDisposedException)
            {
                _suppressFocusOnAutomaticReconnect = false;
            }
        }

        private void ReconnectFocusSuppressionTimer_Tick(object sender, EventArgs e)
        {
            _reconnectFocusSuppressionTimer.Stop();
            _suppressFocusOnAutomaticReconnect = false;
        }

        private sealed class RdpInputBlocker : NativeWindow, IDisposable
        {
            private readonly Control _control;
            public bool Enabled { get; set; }

            public RdpInputBlocker(Control control)
            {
                _control = control;
                _control.HandleCreated += Control_HandleCreated;
                _control.HandleDestroyed += Control_HandleDestroyed;

                if (_control.IsHandleCreated)
                {
                    AssignHandle(_control.Handle);
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (Enabled && IsBlockedInputMessage(m.Msg))
                {
                    if (m.Msg == 0x0021) // WM_MOUSEACTIVATE
                    {
                        m.Result = new IntPtr(4); // MA_NOACTIVATEANDEAT
                    }
                    else
                    {
                        m.Result = IntPtr.Zero;
                    }

                    return;
                }

                base.WndProc(ref m);
            }

            private static bool IsBlockedInputMessage(int msg)
            {
                return msg switch
                {
                    0x0021 => true, // WM_MOUSEACTIVATE
                    0x00FF => true, // WM_INPUT
                    >= 0x0100 and <= 0x0108 => true, // keyboard
                    >= 0x0200 and <= 0x020E => true, // mouse
                    _ => false
                };
            }

            private void Control_HandleCreated(object sender, EventArgs e)
            {
                AssignHandle(_control.Handle);
            }

            private void Control_HandleDestroyed(object sender, EventArgs e)
            {
                ReleaseHandle();
            }

            public void Dispose()
            {
                _control.HandleCreated -= Control_HandleCreated;
                _control.HandleDestroyed -= Control_HandleDestroyed;
                ReleaseHandle();
            }
        }

        private void SetRdGateway()
        {
            try
            {
                if (_rdpClient.TransportSettings.GatewayIsSupported == 0)
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.RdpGatewayNotSupported, true);
                    return;
                }

                Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.RdpGatewayIsSupported, true);

                if (connectionInfo.RDGatewayUsageMethod == RDGatewayUsageMethod.Never) return;

                // USE GATEWAY
                _rdpClient.TransportSettings.GatewayUsageMethod = (uint)connectionInfo.RDGatewayUsageMethod;
                _rdpClient.TransportSettings.GatewayHostname = connectionInfo.RDGatewayHostname;
                _rdpClient.TransportSettings.GatewayProfileUsageMethod = 1; // TSC_PROXY_PROFILE_MODE_EXPLICIT
                if (connectionInfo.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.SmartCard)
                {
                    _rdpClient.TransportSettings.GatewayCredsSource = 1; // TSC_PROXY_CREDS_MODE_SMARTCARD
                }

                if (RdpVersion < Versions.RDC61 || Force.HasFlag(ConnectionInfo.Force.NoCredentials)) return;

                switch (connectionInfo.RDGatewayUseConnectionCredentials)
                {
                    case RDGatewayUseConnectionCredentials.Yes:
                        _rdpClient.TransportSettings2.GatewayUsername = connectionInfo.Username;
                        //_rdpClient.TransportSettings2.GatewayPassword = connectionInfo.Password.ConvertToUnsecureString();
                        _rdpClient.TransportSettings2.GatewayPassword = connectionInfo.Password;
                        _rdpClient.TransportSettings2.GatewayDomain = connectionInfo?.Domain;
                        break;
                    case RDGatewayUseConnectionCredentials.SmartCard:
                        _rdpClient.TransportSettings2.GatewayCredSharing = 0;
                        break;
                    default:
                    {
                        _rdpClient.TransportSettings2.GatewayCredSharing = 0;

                            string gwu = connectionInfo.RDGatewayUsername;
                            string gwp = connectionInfo.RDGatewayPassword;
                            string gwd = connectionInfo.RDGatewayDomain;
                            string pkey = "";

                        // access secret server api if necessary
                        if (InterfaceControl.Info.RDGatewayExternalCredentialProvider == ExternalCredentialProvider.DelineaSecretServer)
                        {
                            try
                            {
                                string RDGUserViaAPI = InterfaceControl.Info.RDGatewayUserViaAPI;
                                ExternalConnectors.DSS.SecretServerInterface.FetchSecretFromServer($"{RDGUserViaAPI}", out gwu, out gwp, out gwd, out pkey);
                            }
                            catch (Exception ex)
                            {
                                Event_ErrorOccured(this, "Secret Server Interface Error: " + ex.Message, 0);
                            }
                        }
                        else if (InterfaceControl.Info.ExternalCredentialProvider == ExternalCredentialProvider.ClickstudiosPasswordState)
                        {
                            try
                            {
                                string RDGUserViaAPI = InterfaceControl.Info.RDGatewayUserViaAPI;
                                ExternalConnectors.CPS.PasswordstateInterface.FetchSecretFromServer($"{RDGUserViaAPI}", out gwu, out gwp, out gwd, out pkey);
                            }
                            catch (Exception ex)
                            {
                                Event_ErrorOccured(this, "Passwordstate Interface Error: " + ex.Message, 0);
                            }
                        }
                        else if (InterfaceControl.Info.ExternalCredentialProvider == ExternalCredentialProvider.OnePassword)
                        {
                            try
                            {
                                string RDGUserViaAPI = InterfaceControl.Info.RDGatewayUserViaAPI;
                                ExternalConnectors.OP.OnePasswordCli.ReadPassword($"{RDGUserViaAPI}", out gwu, out gwp, out gwd, out pkey);
                            }
                            catch (ExternalConnectors.OP.OnePasswordCliException ex)
                            {
                                Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.ECPOnePasswordCommandLine + ": " + ex.Arguments);
                                Runtime.MessageCollector.AddMessage(MessageClass.ErrorMsg, Language.ECPOnePasswordReadFailed + Environment.NewLine + ex.Message);
                            }
                        }
                        else if (InterfaceControl.Info.ExternalCredentialProvider == ExternalCredentialProvider.VaultOpenbao)
                        {
                            try {
                                if (connectionInfo.VaultOpenbaoSecretEngine == VaultOpenbaoSecretEngine.Kv)
                                    gwu = connectionInfo.RDGatewayUsername;
                                ExternalConnectors.VO.VaultOpenbao.ReadPasswordRDP((int)connectionInfo.VaultOpenbaoSecretEngine, connectionInfo.VaultOpenbaoMount, connectionInfo.VaultOpenbaoRole, ref gwu, out gwp);
                            } catch (ExternalConnectors.VO.VaultOpenbaoException ex) {
                                Event_ErrorOccured(this, "Secret Server Interface Error: " + ex.Message, 0);
                            }
                        }


                            if (connectionInfo.RDGatewayUseConnectionCredentials != RDGatewayUseConnectionCredentials.AccessToken)
                        {
                            _rdpClient.TransportSettings2.GatewayUsername = gwu;
                            _rdpClient.TransportSettings2.GatewayPassword = gwp;
                            _rdpClient.TransportSettings2.GatewayDomain = gwd;
                        }
                        else
                        {
                            //TODO: should we check client version and throw if it is less than 7
                        }
                        
                        break;
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

                if (RdpVersion >= Versions.RDC61)
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, string.Format(Language.RdpSetConsoleSwitch, RdpVersion), true);
                    _rdpClient.AdvancedSettings7.ConnectToAdministerServer = value;
                }
                else
                {
                    Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, $"{string.Format(Language.RdpSetConsoleSwitch, RdpVersion)}{Environment.NewLine}No longer supported in this RDP version. Reference: https://msdn.microsoft.com/en-us/library/aa380863(v=vs.85).aspx", true);
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

                string userName = connectionInfo?.Username ?? "";
                string domain = connectionInfo?.Domain ?? "";
                string userViaApi = connectionInfo?.UserViaAPI ?? "";
                string pkey = "";
                //string password = (connectionInfo?.Password?.ConvertToUnsecureString() ?? "");
                string password = (connectionInfo?.Password ?? "");

                // access secret server api if necessary
                if (InterfaceControl.Info.ExternalCredentialProvider == ExternalCredentialProvider.DelineaSecretServer)
                {
                    try
                    {
                        ExternalConnectors.DSS.SecretServerInterface.FetchSecretFromServer($"{userViaApi}", out userName, out password, out domain, out pkey);
                    }
                    catch (Exception ex)
                    {
                        Event_ErrorOccured(this, "Secret Server Interface Error: " + ex.Message, 0);
                    }
                }
                else if (InterfaceControl.Info.ExternalCredentialProvider == ExternalCredentialProvider.ClickstudiosPasswordState)
                {
                    try
                    {
                        ExternalConnectors.CPS.PasswordstateInterface.FetchSecretFromServer($"{userViaApi}", out userName, out password, out domain, out pkey);
                    }
                    catch (Exception ex)
                    {
                        Event_ErrorOccured(this, "Passwordstate Interface Error: " + ex.Message, 0);
                    }
                }
                else if (InterfaceControl.Info.ExternalCredentialProvider == ExternalCredentialProvider.OnePassword)
                {
                    try
                    {
                        ExternalConnectors.OP.OnePasswordCli.ReadPassword($"{userViaApi}", out userName, out password, out domain, out pkey);
                    }
                    catch (ExternalConnectors.OP.OnePasswordCliException ex)
                    {
                        Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.ECPOnePasswordCommandLine + ": " + ex.Arguments);
                        Runtime.MessageCollector.AddMessage(MessageClass.ErrorMsg, Language.ECPOnePasswordReadFailed + Environment.NewLine + ex.Message);
                    }
                }
                else if (InterfaceControl.Info.ExternalCredentialProvider == ExternalCredentialProvider.VaultOpenbao) {
                    try {
                        if(connectionInfo.VaultOpenbaoSecretEngine == VaultOpenbaoSecretEngine.Kv)
                            userName = connectionInfo?.Username ?? "";
                        ExternalConnectors.VO.VaultOpenbao.ReadPasswordRDP((int)connectionInfo.VaultOpenbaoSecretEngine, connectionInfo?.VaultOpenbaoMount ?? "", connectionInfo?.VaultOpenbaoRole ?? "", ref userName, out password);
                    } catch (ExternalConnectors.VO.VaultOpenbaoException ex) {
                        Event_ErrorOccured(this, "Secret Server Interface Error: " + ex.Message, 0);
                    }
                }

                if (string.IsNullOrEmpty(userName))
                {
                    switch (Properties.OptionsCredentialsPage.Default.EmptyCredentials)
                    {
                        case "windows":
                            _rdpClient.UserName = Environment.UserName;
                            break;
                        case "custom" when !string.IsNullOrEmpty(Properties.OptionsCredentialsPage.Default.DefaultUsername):
                            _rdpClient.UserName = Properties.OptionsCredentialsPage.Default.DefaultUsername;
                            break;
                        case "custom":
                            try
                            {
                                ExternalConnectors.DSS.SecretServerInterface.FetchSecretFromServer(Properties.OptionsCredentialsPage.Default.UserViaAPIDefault, out userName, out password, out domain, out pkey);
                                _rdpClient.UserName = userName;
                            }
                            catch (Exception ex)
                            {
                                Event_ErrorOccured(this, "Secret Server Interface Error: " + ex.Message, 0);
                            }

                            break;
                    }
                }
                else
                {
                    _rdpClient.UserName = userName;
                }

                if (string.IsNullOrEmpty(password))
                {
                    if (Properties.OptionsCredentialsPage.Default.EmptyCredentials == "custom")
                    {
                        if (Properties.OptionsCredentialsPage.Default.DefaultPassword != "")
                        {
                            LegacyRijndaelCryptographyProvider cryptographyProvider = new();
                            _rdpClient.AdvancedSettings2.ClearTextPassword = cryptographyProvider.Decrypt(Properties.OptionsCredentialsPage.Default.DefaultPassword, Runtime.EncryptionKey);
                        }
                    }
                }
                else
                {
                    _rdpClient.AdvancedSettings2.ClearTextPassword = password;
                }

                if (string.IsNullOrEmpty(domain))
                {
                    _rdpClient.Domain = Properties.OptionsCredentialsPage.Default.EmptyCredentials switch
                    {
                        "windows" => Environment.UserDomainName,
                        "custom" => Properties.OptionsCredentialsPage.Default.DefaultDomain,
                        _ => _rdpClient.Domain
                    };
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
                SetExtendedProperty("DesktopScaleFactor", DesktopScaleFactor);
                SetExtendedProperty("DeviceScaleFactor", DeviceScaleFactor);

                if (Force.HasFlag(ConnectionInfo.Force.Fullscreen))
                {
                    _rdpClient.FullScreen = true;
                    _rdpClient.DesktopWidth = Screen.FromControl(_frmMain).Bounds.Width;
                    _rdpClient.DesktopHeight = Screen.FromControl(_frmMain).Bounds.Height;

                    return;
                }

                switch (InterfaceControl.Info.Resolution)
                {
                    case RDPResolutions.FitToWindow:
                        // Lock the RDP session to the current content area size.
                        // The control is undocked so it keeps this fixed size;
                        // AutoScroll on the parent panel provides scrollbars
                        // when the panel shrinks below the session resolution.
                        // Use DisplayRectangle to respect Padding (connection frame border).
                        var fitRect = InterfaceControl.DisplayRectangle;
                        _rdpClient.DesktopWidth = fitRect.Width;
                        _rdpClient.DesktopHeight = fitRect.Height;
                        Control.Dock = DockStyle.None;
                        Control.Location = fitRect.Location;
                        Control.Size = fitRect.Size;
                        InterfaceControl.AutoScroll = true;
                        InterfaceControl.AutoScrollMinSize = fitRect.Size;
                        break;
                    case RDPResolutions.SmartSize:
                        // Connect at the full screen resolution so the remote
                        // desktop is rendered at high quality, then SmartSizing
                        // scales the image to fit whatever the panel size is.
                        // Use Anchor instead of Dock.Fill because the AxHost
                        // ActiveX wrapper doesn't forward Dock-triggered resizes
                        // to the COM control's internal rendering surface.
                        // Use DisplayRectangle to respect Padding (connection frame border).
                        var screen = Screen.FromControl(_frmMain);
                        _rdpClient.DesktopWidth = screen.Bounds.Width;
                        _rdpClient.DesktopHeight = screen.Bounds.Height;
                        _rdpClient.AdvancedSettings2.SmartSizing = true;
                        var smartRect = InterfaceControl.DisplayRectangle;
                        Control.Dock = DockStyle.None;
                        Control.Location = smartRect.Location;
                        Control.Size = smartRect.Size;
                        Control.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                        break;
                    case RDPResolutions.Fullscreen:
                        _rdpClient.FullScreen = true;
                        _rdpClient.DesktopWidth = Screen.FromControl(_frmMain).Bounds.Width;
                        _rdpClient.DesktopHeight = Screen.FromControl(_frmMain).Bounds.Height;
                        break;
                }
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
                SetDriveRedirection();
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

        private void SetDriveRedirection()
        {
            if (RDPDiskDrives.None == connectionInfo.RedirectDiskDrives)
                _rdpClient.AdvancedSettings2.RedirectDrives = false;
            else if (RDPDiskDrives.All == connectionInfo.RedirectDiskDrives)
                _rdpClient.AdvancedSettings2.RedirectDrives = true;
            else if (RDPDiskDrives.Custom == connectionInfo.RedirectDiskDrives)
            {
                IMsRdpClientNonScriptable5 rdpNS5 = (IMsRdpClientNonScriptable5)((AxHost)Control).GetOcx();
                for (uint i = 0; i < rdpNS5.DriveCollection.DriveCount; i++)
                {
                    IMsRdpDrive drive = rdpNS5.DriveCollection.DriveByIndex[i];
                    drive.RedirectionState = connectionInfo.RedirectDiskDrivesCustom.Contains(drive.Name.Substring(0, 1));
                }
            }
            else
            {
                // Local Drives
                IMsRdpClientNonScriptable5 rdpNS5 = (IMsRdpClientNonScriptable5)((AxHost)Control).GetOcx();
                for (uint i = 0; i < rdpNS5.DriveCollection.DriveCount; i++)
                {
                    IMsRdpDrive drive = rdpNS5.DriveCollection.DriveByIndex[i];
                    drive.RedirectionState = IsLocal(drive);
                }
            }
        }

        private bool IsLocal(IMsRdpDrive drive)
        {
            DriveInfo[] myDrives = DriveInfo.GetDrives();
            foreach (DriveInfo myDrive in myDrives)
            {
                if (myDrive.Name.Substring(0, 1).Equals(drive.Name.Substring(0,1)))
                {
                    return myDrive.DriveType == DriveType.Fixed;
                }
            }
            return false;
        }

        private void SetPerformanceFlags()
        {
            try
            {
                int pFlags = 0;
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

        protected virtual void SetEventHandlers()
        {
            try
            {
                _rdpClient.OnConnecting += RDPEvent_OnConnecting;
                _rdpClient.OnConnected += RDPEvent_OnConnected;
                _rdpClient.OnLoginComplete += RDPEvent_OnLoginComplete;
                _rdpClient.OnFatalError += RDPEvent_OnFatalError;
                _rdpClient.OnDisconnected += RDPEvent_OnDisconnected;
                _rdpClient.OnIdleTimeoutNotification += RDPEvent_OnIdleTimeoutNotification;
                _rdpClient.OnLeaveFullScreenMode += RDPEvent_OnLeaveFullscreenMode;
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
            MessageBox.Show($@"The {connectionInfo.Name} session was disconnected due to inactivity", @"Session Disconnected", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void RDPEvent_OnFatalError(int errorCode)
        {
            string errorMsg = RdpErrorCodes.GetError(errorCode);
            Event_ErrorOccured(this, errorMsg, errorCode);
        }

        private void RDPEvent_OnDisconnected(int discReason)
        {
            const int UI_ERR_NORMAL_DISCONNECT = 0xB08;
            if (discReason != UI_ERR_NORMAL_DISCONNECT)
            {
                string reason = _rdpClient.GetErrorDescription((uint)discReason, (uint)_rdpClient.ExtendedDisconnectReason);
                Event_Disconnected(this, reason, discReason);
            }

            if (Properties.OptionsAdvancedPage.Default.ReconnectOnDisconnect)
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
            ApplyFullscreenViewOnlyState(IsRdpFullscreen());
            ScheduleScrollToDesktopBottomRight();
        }

        private void RDPEvent_OnLoginComplete()
        {
            loginComplete = true;
            ApplyFullscreenViewOnlyState(IsRdpFullscreen());
            ScheduleScrollToDesktopBottomRight();
            ScheduleClearAutomaticReconnectFocusSuppression();
        }

        private void RDPEvent_OnLeaveFullscreenMode()
        {
            Fullscreen = false;
            ApplyFullscreenViewOnlyState(false);
            ScheduleScrollToDesktopBottomRight();
            _leaveFullscreenEvent?.Invoke(this, EventArgs.Empty);
        }

        private void RdpClient_GotFocus(object sender, EventArgs e)
        {
            if (_suppressFocusOnAutomaticReconnect || ViewOnly)
            {
                return;
            }

            ((ConnectionTab)Control.Parent.Parent).Focus();
        }
        #endregion

        #region Public Events & Handlers

        public delegate void LeaveFullscreenEventHandler(object sender, EventArgs e);

        private LeaveFullscreenEventHandler _leaveFullscreenEvent;

        public event LeaveFullscreenEventHandler LeaveFullscreen
        {
            add => _leaveFullscreenEvent = (LeaveFullscreenEventHandler)Delegate.Combine(_leaveFullscreenEvent, value);
            remove => _leaveFullscreenEvent = (LeaveFullscreenEventHandler)Delegate.Remove(_leaveFullscreenEvent, value);
        }

        #endregion

        #region Enums

        public enum Defaults
        {
            Colors = RDPColors.Colors16Bit,
            Sounds = RDPSounds.DoNotPlay,
            Resolution = RDPResolutions.SmartSize,
            Port = 3389
        }

        #endregion
        
        #region Reconnect Stuff

        private void tmrReconnect_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                bool srvReady = PortScanner.IsPortOpen(connectionInfo.Hostname, Convert.ToString(connectionInfo.Port));

                ReconnectGroup.ServerReady = srvReady;

                if (!ReconnectGroup.ReconnectWhenReady || !srvReady) return;
                tmrReconnect.Enabled = false;
                ReconnectGroup.DisposeReconnectGroup();
                loginComplete = false;
                _suppressFocusOnAutomaticReconnect = true;
                ApplyFullscreenViewOnlyState(IsRdpFullscreen());
                //SetProps()
                _rdpClient.Connect();
            }
            catch (Exception ex)
            {
                _suppressFocusOnAutomaticReconnect = false;
                Runtime.MessageCollector.AddExceptionMessage(
                    string.Format(Language.AutomaticReconnectError, connectionInfo.Hostname),
                    ex, MessageClass.WarningMsg, false);
            }
        }

        #endregion

    }
}
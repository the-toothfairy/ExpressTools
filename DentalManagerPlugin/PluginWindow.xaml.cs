using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DentalManagerPlugin
{
    /// <summary>
    /// Interaction logic
    /// </summary>
    public partial class PluginWindow : Window
    {
        /// <summary>Windows function</summary>
        [DllImport("User32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr hwnd, StringBuilder lpClassName, int nMaxCount);

        /// <summary>Windows delegate</summary>
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>Windows function</summary>
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
            int idChild, uint dwEventThread, uint dwmsEventTime);

        const uint WINEVENT_OUTOFCONTEXT = 0;
        const uint WINEVENT_SKIPOWNPROCESS = 2;
        const uint EVENT_SYSTEM_FOREGROUND = 3;

        [DllImport("user32.dll")]
        static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            IntPtr lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        private ExpressClient _expressClient;
        private OrderHandler _orderHandler;
        private IdSettings _idSettings;
        private AppSettings _appSettings;

        /// <summary>
        /// must be set before showing
        /// </summary>
        private readonly string _orderDir;

        private ExpressClient.FilterOutput _filterOutput; // for delayed upload

        private string _resultId;

        // handling of window positions
        private IntPtr _winEventHook;
        private static int _callbackInterlockValue = 0;
        private IntPtr _hWndDentalManager;
        private GCHandle _gchWinEventDelegate;

        /// <summary>
        /// display a message in color. show option to log out if "remember me" and error
        /// </summary>
        private void ShowMessage(string msg, Visual.Severities severity)
        {
            Dispatcher.Invoke(() =>
            {
                TextStatus.Text = msg;
                TextStatus.Foreground = new SolidColorBrush(Visual.MessageColors[severity]);
            });
        }

        /// <summary>
        /// can also set to empty
        /// </summary>
        private void ShowLoginInTitle(string user)
        {
            var t = "FC Express Link";
            if (!string.IsNullOrEmpty(user))
                t += $" ({user})";

            t += _appSettings?.GetAnyTestingInfo();

            Dispatcher.Invoke(() => { this.Title = t; });
        }

        public PluginWindow(string orderDir)
        {
            InitializeComponent();

            ButtonInspect.Visibility = Visibility.Collapsed;
            _orderDir = orderDir;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var loggedIn = false;

            try
            {
                _idSettings = IdSettings.ReadOrNew();
                _appSettings = AppSettings.ReadOrNew();

                ShowLoginInTitle("");

                if (_appSettings.PluginWindowWidth > 150 && _appSettings.PluginWindowHeight > 50) // not first time, not too small
                {
                    this.Top = _appSettings.PluginWindowTop;
                    this.Left = _appSettings.PluginWindowLeft;
                    this.Width = _appSettings.PluginWindowWidth;
                    this.Height = _appSettings.PluginWindowHeight;
                }

                if (string.IsNullOrEmpty(_orderDir))
                {
                    ShowMessage($"You must select a single order", Visual.Severities.Error); // DentalManager passes empty if multiple
                    ButtonUpload.IsEnabled = false;
                    CheckboxAutoUpload.IsEnabled = false;
                    return;
                }

                _orderHandler = OrderHandler.MakeIfValid(_orderDir);
                if (_orderHandler == null)
                {
                    ShowMessage($"{_orderDir} is not a valid order directory", Visual.Severities.Error);
                    ButtonUpload.IsEnabled = false;
                    CheckboxAutoUpload.IsEnabled = false;
                    return;
                }

                TextBlockOrder.Text = _orderHandler.OrderId; // Label would not show first underscore
                this.CheckboxAutoUpload.IsChecked = _appSettings.AutoUpload;
                RefresAutoUploadDependentControls();
                // now that checkbox is set, wire up events
                this.CheckboxAutoUpload.Checked += CheckboxAutoUpload_CheckedChanged;
                this.CheckboxAutoUpload.Unchecked += CheckboxAutoUpload_CheckedChanged;

                var uri = _appSettings.GetUri();

                _expressClient = new ExpressClient(uri);

                // detect if valid previous login
                loggedIn = await _expressClient.CheckIfStillLoggedIn(_idSettings.AuthCookie);

                if (!loggedIn)
                {
                    var loginWindow = new LoginWindow(_idSettings, _expressClient);

                    loginWindow.Closed += async (s, args) =>
                    {
                        if (loginWindow.LoginSuccessful)
                        {
                            RefreshLoginDependentControls(true, loginWindow.LoginRemembered);
                            await HandleSingleOrder(); // status, qualifiy, possibly upload
                        }
                        else
                        {
                            RefreshLoginDependentControls(false, false);
                        }
                    };

                    loginWindow.Owner = this;
                    loginWindow.ShowDialog();
                }
                else
                {
                    RefreshLoginDependentControls(true, true);
                    await HandleSingleOrder(); // status, qualifiy, possibly upload
                }
            }
            catch (Exception exception)
            {
                ShowMessage(exception.Message, Visual.Severities.Error);
                RefreshLoginDependentControls(false, false);
                return;
            }
        }

        private void RefreshLoginDependentControls(bool loggedIn, bool remembered)
        {
            ShowLoginInTitle(loggedIn ? _idSettings.UserLogin : "");
            // all of batch part in one go
            // parts of from-DentalManager
            ButtonUpload.IsEnabled = loggedIn; // may not be visible
            ButtonLogout.Visibility = loggedIn && remembered ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefresAutoUploadDependentControls() => ButtonUpload.Visibility = CheckboxAutoUpload.IsChecked == true ?
                                                                Visibility.Collapsed : Visibility.Visible;


        /// <summary>
        /// check status, qualify, possibly upload. no try/catch
        /// </summary>
        private async Task HandleSingleOrder()
        {
            var origCursor = Cursor;
            Cursor = Cursors.Wait;

            try
            {
                _hWndDentalManager = TryFindDentalManagerWindow(); // must make sure this window is in foreground of DentalManager
                if (_hWndDentalManager != IntPtr.Zero)
                {
                    WinEventDelegate pinnedDelegate = OnForegroundWindowChanged;
                    _gchWinEventDelegate = GCHandle.Alloc(pinnedDelegate);
                    var functionPtr = Marshal.GetFunctionPointerForDelegate<WinEventDelegate>(pinnedDelegate);
                    _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                        functionPtr, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
                }

                _filterOutput = null;

                // no upload while checking other things, and it may not be allowed later, either
                this.ButtonUpload.IsEnabled = false;
                ButtonInspect.Visibility = Visibility.Collapsed;
                ButtonInspect.Tag = null;

                var resultData = await _expressClient.GetStatus(_orderHandler.OrderId);
                if (resultData.Count > 1)
                {
                    ShowMessage("This order has been uploaded multiple times. For details, please go to the web site.",
                        Visual.Severities.Info);
                    return;
                }
                if (resultData.Count == 1) // order alread uploaded exactly once, can get status
                {
                    Display(resultData[0]);

                    _resultId = resultData[0].eid;

                    if (resultData[0].IsViewable == ExpressClient.ResultData.TRUE)
                        ButtonInspect.Visibility = Visibility.Visible;

                    return;
                }

                ShowMessage("Order has not been uploaded before.", Visual.Severities.Info);

                _filterOutput = await _expressClient.Filter(_orderHandler.AllRelativePaths);
                if (_filterOutput == null || _filterOutput.Kind == "")
                {
                    ShowMessage("Could not find files in order to upload.", Visual.Severities.Error);
                    return;
                }

                var msg = "";
                using (var orderStream = _orderHandler.GetStream(_filterOutput.OrderPath))
                using (var designStream = _orderHandler.GetStream(_filterOutput.DesignPath))
                {
                    msg = await _expressClient.Qualify(_filterOutput, orderStream, designStream);
                }

                if (!string.IsNullOrEmpty(msg))
                {
                    ShowMessage(msg, Visual.Severities.Warning);
                    return;
                }

                if (_appSettings.AutoUpload)
                    await UploadOrder(true); // close window when done
                else
                    ButtonUpload.IsEnabled = true;
            }
            finally
            {
                Cursor = origCursor;
            }
        }

        private void Display(ExpressClient.ResultData resultData)
        {
            var msgRes = "Status: " + resultData.StatusMessage;
            if (resultData.ReviewedUtc.HasValue)
                msgRes += $". Last viewed: {resultData.ReviewedUtc.Value.ToLocalTime()}";
            else if (resultData.Status == 0)
                msgRes += ", ready for review";

            var severity = Visual.Severities.Info;
            if (resultData.Status == 0) // new and good
                severity = Visual.Severities.Emphasis;
            else if (resultData.Status == -1) // new but failed
                severity = Visual.Severities.Warning;
            else if (resultData.Status == 1) // downloaded
                severity = Visual.Severities.Good;
            else if (resultData.Status == 2) // rejected
                severity = Visual.Severities.Rejection;

            ShowMessage(msgRes, severity);
        }

        private async Task OnOrderStatusChange(string resultId, int i0, int i1, string m)
        {
            if (resultId != _resultId)
                return;

            var r = await _expressClient.GetSingleStatus(_resultId);
            Display(r);
        }

        private async Task UploadOrder(bool closeAfterUpload)
        {
            ShowMessage("Uploading...", Visual.Severities.Info);

            try
            {
                using (var ms = _orderHandler.ZipOrderFiles(_filterOutput.AllPaths))
                    await _expressClient.Upload(_orderHandler.OrderId + ".zip", ms, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, Visual.Severities.Error);
                return;
            }

            if (!closeAfterUpload)
            {
                ShowMessage("Sent for design.", Visual.Severities.Good);
                return;
            }

            ShowMessage("Sent for design. Closing this window soon...", Visual.Severities.Good);
            try
            {
                await Task.Delay(3000);
                Close();
            }
            catch (Exception)
            {
                // if user killed window manually
            }
        }

        /// <summary>
        /// not Window_Closed, as we want to have objects still alive.
        /// </summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                UnhookWinEvent(_winEventHook);

                if (_gchWinEventDelegate.IsAllocated)
                    _gchWinEventDelegate.Free();
            }
            catch (Exception)
            { }

            try
            {
                if (_appSettings != null)
                {
                    _appSettings.PluginWindowTop = this.Top;
                    _appSettings.PluginWindowLeft = this.Left;
                    _appSettings.PluginWindowWidth = this.Width;
                    _appSettings.PluginWindowHeight = this.Height;
                    _appSettings.AutoUpload = CheckboxAutoUpload.IsChecked == true;

                    AppSettings.Write(_appSettings);
                }

                _expressClient?.Dispose();
            }
            catch (Exception)
            {
                // ignore
            }
            finally
            {
                e.Cancel = false;
            }
        }

        private async void ButtonLogout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (MessageBoxResult.No == MessageBox.Show("Are you sure you want to log out?", "", MessageBoxButton.YesNo))
                    return;

                _idSettings.AuthCookie = null;
                IdSettings.Write(_idSettings);

                if (_expressClient == null)
                    return; // should not happen, but to be save

                await _expressClient.Logout();
                ShowMessage("You are logged out", Visual.Severities.Info);
                RefreshLoginDependentControls(false, false);
            }
            catch (Exception)
            {
                ShowMessage("Error during log out", Visual.Severities.Error);
            }
        }

        private void CheckboxAutoUpload_CheckedChanged(object sender, RoutedEventArgs e)
        {
            try
            {
                RefresAutoUploadDependentControls();
            }
            catch (Exception ex)
            {
                ShowMessage("Error: " + ex.Message, Visual.Severities.Error);
            }
        }

        /// <summary>
        /// button is disabled unless order qualifies
        /// </summary>
        private async void ButtonUpload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await UploadOrder(true); // close window when done
            }
            catch (Exception ex)
            {
                ShowMessage("Error during upload: " + ex.Message, Visual.Severities.Error);
            }
        }

        private async void ButtonInspect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_resultId))
                    return;

                await _expressClient.TryStartNotifications(OnOrderStatusChange);

                var url = "https://" + _expressClient.UriString + "/Inspect/" + _resultId;
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                ShowMessage("Error showing result: " + ex.Message, Visual.Severities.Error);
            }
        }

        private void OnForegroundWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime)
        {
            // prevent reentrancy.
            if (0 == Interlocked.Exchange(ref _callbackInterlockValue, 1))
            {
                if (eventType == EVENT_SYSTEM_FOREGROUND) // TODO remove? (always so)
                {
                    if (hwnd == _hWndDentalManager)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (this.WindowState == WindowState.Minimized)
                                this.WindowState = WindowState.Normal;
                            this.Activate();
                            this.Topmost = true;
                            this.Focus();
                        });
                    }
                    else
                    {
                        Dispatcher.Invoke(() =>
                        {
                            this.WindowState = WindowState.Minimized;
                            this.Topmost = false;
                        });
                    }
                }
                Interlocked.Exchange(ref _callbackInterlockValue, 0);
            };
        }

        private IntPtr TryFindDentalManagerWindow()
        {
            IntPtr res = IntPtr.Zero;
            try
            {
                var hWnd = IntPtr.Zero;
                EnumWindows((hWnd, param) => // loop over all windows
                {
                    var classText = new StringBuilder("", 100);
                    GetClassName(hWnd, classText, 100);
                    if (classText.ToString() == "TDentalManagerMainForm")
                        res = hWnd;
                    return res == IntPtr.Zero; // continue unless found
                }, IntPtr.Zero);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }

            return res;
        }
    }
}

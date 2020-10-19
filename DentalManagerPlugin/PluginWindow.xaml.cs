using System;
using System.Collections.Generic;
using System.Net;
using System.Security.RightsManagement;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Diagnostics;

namespace DentalManagerPlugin
{
    /// <summary>
    /// Interaction logic
    /// </summary>
    public partial class PluginWindow : Window
    {
        private ExpressClient _expressClient;
        private OrderHandler _orderHandler;
        private IdSettings _idSettings;

        /// <summary>
        /// must be set before showing
        /// </summary>
        private readonly string _orderDir;

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
            var t = "Send to FC Express";
            if (!string.IsNullOrEmpty(user))
                t += $" ({user})";

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
                ShowLoginInTitle("");

                _idSettings = IdSettings.ReadOrNew();

                var di = new System.IO.DirectoryInfo(_orderDir);
                var orderHandler = OrderHandler.MakeIfValid(di);
                if (orderHandler == null)
                {
                    ShowMessage($"{di.FullName} is not a valid order directory", Visual.Severities.Error);
                    ButtonUpload.IsEnabled = false;
                    CheckboxAutoUpload.IsEnabled = false;
                    return;
                }

                _orderHandler = orderHandler;
                LabelOrder.Content = _orderHandler.OrderId;
                this.CheckboxAutoUpload.IsChecked = _idSettings.AutoUpload;
                RefresAutoUploadDependentControls();
                // now that checkbox is set, wire up events
                this.CheckboxAutoUpload.Checked += CheckboxAutoUpload_CheckedChanged;
                this.CheckboxAutoUpload.Unchecked += CheckboxAutoUpload_CheckedChanged;

                var uri = IdSettings.GetUri();

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
            ButtonLogout.Visibility = loggedIn && remembered ? Visibility.Visible : Visibility.Hidden;
        }

        private void RefresAutoUploadDependentControls() => ButtonUpload.Visibility = CheckboxAutoUpload.IsChecked == true ?
                                                                Visibility.Hidden : Visibility.Visible;


        /// <summary>
        /// check status, qualify, possibly upload. no try/catch
        /// </summary>
        private async Task HandleSingleOrder()
        {
            // no upload while checking other things, and it may not be allowed later, either
            this.ButtonUpload.IsEnabled = false;
            ButtonInspect.Visibility = Visibility.Collapsed;
            ButtonInspect.Tag = null;

            var resultData = await _expressClient.GetStatus(_orderHandler.OrderId);

            if (resultData.Count > 1)
            {
                ShowMessage("This order has been uploaded multiple times. For details, please go to the web site.", Visual.Severities.Info);
                return;
            }

            if (resultData.Count == 1) // order alread uploaded exactly once, can get status
            {
                if (!resultData[0].Status.HasValue)
                    ShowMessage("No status information for this order. Please go to the web site.", Visual.Severities.Warning);

                var st = resultData[0].Status.Value;

                var reviewedLocal = "?";

                if (resultData[0].ReviewedUtc.HasValue)
                    reviewedLocal = resultData[0].ReviewedUtc.Value.ToLocalTime().ToString();

                if (ExpressClient.StatusIsReadyForReview(st))
                {
                    ShowMessage("Design is ready for review on the web site.", Visual.Severities.Good);
                    ButtonInspect.Visibility = Visibility.Visible;
                    ButtonInspect.Tag = resultData[0].eid;
                }

                else if (ExpressClient.StatusIsAcceptedDownloaded(st))
                    ShowMessage($"Design was accepted and downloaded at {reviewedLocal}.", Visual.Severities.Info);

                else if (ExpressClient.StatusIsRejected(st))
                    ShowMessage($"Design was rejected at {reviewedLocal}.", Visual.Severities.Info);

                else if (ExpressClient.StatusIsInProgress(st))
                    ShowMessage("Design is in progress.", Visual.Severities.Info);

                else if (ExpressClient.StatusIsFailure(st))
                    ShowMessage("Design failed. See details on the web site.", Visual.Severities.Warning);

                else
                    ShowMessage("Unknown status information for this order. Please go to the web site.", Visual.Severities.Warning);

                return;
            }

            ShowMessage("Order is new.", Visual.Severities.Info);

            var msg = await _expressClient.Qualify(_orderHandler.GetOrderText());
            if (!string.IsNullOrEmpty(msg))
            {
                ShowMessage(msg, Visual.Severities.Warning);
                return;
            }

            if (_idSettings.AutoUpload)
                await UploadOrder(true); // close window when done
            else
                ButtonUpload.IsEnabled = true;
        }

        private async Task UploadOrder(bool closeAfterUpload)
        {
            ShowMessage("Uploading...", Visual.Severities.Info);

            try
            {
                using (var ms = _orderHandler.ZipOrderFiles())
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
                _idSettings.AutoUpload = CheckboxAutoUpload.IsChecked == true;
                IdSettings.Write(_idSettings);
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

        private void ButtonInspect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(ButtonInspect.Tag is string eid))
                    return;

                var url = "https://" + _expressClient.UriString + "/Inspect/" + eid;
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                ShowMessage("Error showing result: " + ex.Message, Visual.Severities.Error);
            }
        }
    }
}

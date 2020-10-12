using System;
using System.Collections.Generic;
using System.Net;
using System.Security.RightsManagement;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DentalManagerPlugin
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private ExpressClient _expressClient;
        private OrderHandler _orderHandler;
        private IdSettings _idSettings;

        private CancellationTokenSource _uploadCancelTokenSource;

        /// <summary>
        /// must be set before showing
        /// </summary>
        public string OrderDir { get; set; }

        /// <summary>
        /// called from DentalManager (with order argument). If not, called as standalone, allowing batch upload
        /// </summary>
        private bool FromDentalManager => !string.IsNullOrEmpty(OrderDir);

        #region information to user

        /// <summary>
        /// "quality" of message in errors, for log, etc
        /// </summary>
        /// <remarks>must be sorted by severity!</remarks>
        public enum Severities
        {
            Info = 0,
            Good,
            Warning,
            Error,
        }

        private readonly Dictionary<Severities, Color> _messageColors = new Dictionary<Severities, Color>
        {
            { Severities.Info, Colors.Black },
            { Severities.Good, Colors.Green },
            { Severities.Warning, Colors.DarkOrange },
            { Severities.Error, Colors.Red },
        };

        /// <summary>
        /// display a message in color. show option to log out if "remember me" and error
        /// </summary>
        private void ShowMessage(string msg, Severities severity)
        {
            Dispatcher.Invoke(() =>
            {
                TextStatus.Text = msg;
                TextStatus.Foreground = new SolidColorBrush(_messageColors[severity]);

                if (severity == Severities.Error && _expressClient != null && _idSettings.AuthCookie != null)
                    PanelFromDentalManager.Visibility = Visibility.Visible;
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

        #endregion


        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var loggedIn = false;

            try
            {
                ShowLoginInTitle("");

                _idSettings = IdSettings.ReadOrNew();

                if (FromDentalManager)
                {
                    this.GridBatchUpload.Visibility = Visibility.Collapsed;
                    _orderHandler = new OrderHandler(OrderDir);
                    LabelOrder.Content = _orderHandler.OrderId;
                    this.CheckboxAutoUpload.IsChecked = _idSettings.AutoUpload;
                    RefresAutoUploadDependentControls();
                    // now that checkbox is set, wire up events
                    this.CheckboxAutoUpload.Checked += CheckboxAutoUpload_CheckedChanged;
                    this.CheckboxAutoUpload.Unchecked += CheckboxAutoUpload_CheckedChanged;
                }
                else
                {
                    this.PanelFromDentalManager.Visibility = Visibility.Collapsed;
                }

                var uri = IdSettings.ReadAnyAssociatedUri(); // allow change
                if (uri == null)
                {
                    uri = new Uri("https://express.fullcontour.com/");
#if DEBUG
                    uri = new Uri("https://localhost:44334/");
#endif
                }

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
                            RefreshLoginDependentControls(true);
                            if (FromDentalManager)
                                await HandleDentalManagerOrder(); // status, qualifiy, possibly upload
                        }
                        else
                        {
                            RefreshLoginDependentControls(false);
                        }
                    };

                    loginWindow.Owner = this;
                    loginWindow.ShowDialog();
                }
                else
                {
                    RefreshLoginDependentControls(true);
                }
            }
            catch (Exception exception)
            {
                ShowMessage(exception.Message, Severities.Error);
                GridBatchUpload.IsEnabled = false;
                ButtonUpload.IsEnabled = false;
                return;
            }
        }

        private void RefreshLoginDependentControls(bool loggedIn)
        {
            ShowLoginInTitle(loggedIn ? _idSettings.UserLogin : "");
            // all of batch part in one go
            GridBatchUpload.IsEnabled = loggedIn; // may not be visible
            // parts of from-DentalManager
            ButtonUpload.IsEnabled = loggedIn; // may not be visible
            ButtonLogoutDentalManager.IsEnabled = loggedIn;
        }

        private void RefresAutoUploadDependentControls() => ButtonUpload.Visibility = CheckboxAutoUpload.IsChecked == true ?
                                                                Visibility.Hidden : Visibility.Visible;


        /// <summary>
        /// check status, qualify, possibly upload. no try/catch
        /// </summary>
        private async Task HandleDentalManagerOrder()
        {
            // no upload while checking other things, and it may not be allowed later, either
            this.ButtonUpload.IsEnabled = false;

            var resultData = await _expressClient.GetStatus(_orderHandler.OrderId);

            if (resultData.Count > 1)
            {
                ShowMessage("This order has been uploaded multiple times. For details, please go to the web site.", Severities.Info);
                return;
            }

            if (resultData.Count == 1) // order alread uploaded exactly once, can get status
            {
                if (!resultData[0].Status.HasValue)
                    ShowMessage("No status information for this order. Please go to the web site.", Severities.Warning);

                var st = resultData[0].Status.Value;

                if (ExpressClient.StatusIsReadyForReview(st))
                    ShowMessage("Design is ready for review on the web site.", Severities.Good); // TODO add view button

                else if (ExpressClient.StatusIsAcceptedDownloaded(st))
                    ShowMessage("Design was accepted and downloaded.", Severities.Info);

                else if (ExpressClient.StatusIsRejected(st))
                    ShowMessage("Design was rejected.", Severities.Info);

                else if (ExpressClient.StatusIsInProgress(st))
                    ShowMessage("Design is in progress.", Severities.Info);

                else if (ExpressClient.StatusIsFailure(st))
                    ShowMessage("Design failed. See details on the web site.", Severities.Warning);

                else
                    ShowMessage("Unknown status information for this order. Please go to the web site.", Severities.Warning);

                return;
            }

            ShowMessage("Order is new", Severities.Info);

            var msg = await _expressClient.Qualify(_orderHandler.GetOrderText());
            if (!string.IsNullOrEmpty(msg))
            {
                ShowMessage(msg, Severities.Warning);
                return;
            }

            if (_idSettings.AutoUpload)
                await UploadOrder(true); // close window when done
            else
                ButtonUpload.IsEnabled = true;
        }

        private async Task UploadOrder(bool closeAfterUpload)
        {
            ShowMessage("Uploading...", Severities.Info);

            try
            {
                _uploadCancelTokenSource = new CancellationTokenSource();
                var token = _uploadCancelTokenSource.Token;

                using (var ms = _orderHandler.ZipOrderFiles())
                    await _expressClient.Upload(_orderHandler.OrderId + ".zip", ms, token);
            }
            catch (OperationCanceledException)
            {
                ShowMessage("Upload cancelled", Severities.Warning);
                return;
            }
            catch (Exception ex)
            {
                ShowMessage(ex.Message, Severities.Error);
                return;
            }
            finally
            {
                _uploadCancelTokenSource?.Dispose();
                _uploadCancelTokenSource = null;
            }

            if (!closeAfterUpload)
            {
                ShowMessage("Sent for design.", Severities.Good);
                return;
            }

            ShowMessage("Sent for design. Closing this window soon...", Severities.Good);
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
                _uploadCancelTokenSource?.Cancel();
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
                ShowMessage("You are logged out", Severities.Info);
                RefreshLoginDependentControls(false);
            }
            catch (Exception)
            {
                ShowMessage("Error during log out", Severities.Error);
            }
        }

        private void CheckboxAutoUpload_CheckedChanged(object sender, RoutedEventArgs e)
        {
            _idSettings.AutoUpload = CheckboxAutoUpload.IsChecked == true;
            IdSettings.Write(_idSettings);
            RefresAutoUploadDependentControls();
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
                ShowMessage("Error during upload: " + ex.Message, Severities.Error);
            }
        }
    }
}

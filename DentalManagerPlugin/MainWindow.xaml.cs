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

        /// <summary>
        /// must be set before showing
        /// </summary>
        public string OrderDir { get; set; }


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

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// login and then automatically run the order
        /// </summary>
        private async void ButtonLogin_OnClick(object sender, RoutedEventArgs e)
        {
            var origCursor = Cursor;
            try
            {
                Cursor = Cursors.Wait;
                try
                {
                    await _expressClient.Login(TextLogin.Text, Pw.Password, CheckRemember.IsChecked == true);

                    if (CheckRemember.IsChecked == true)
                    {
                        _idSettings.AuthCookie = _expressClient.AuthCookie;
                        _idSettings.UserLogin = TextLogin.Text;
                    }
                    else
                    {
                        _idSettings.AuthCookie = null;
                    }

                    IdSettings.Write(_idSettings); // also if null auth cookie

                    // login passed. Now don't allow user to log in again (but show identity)
                    ShowLoginInTitle(TextLogin.Text);
                    GridPw.Visibility = Visibility.Collapsed;
                }
                catch (Exception exception)
                {
                    ShowMessage("Login failed. " + exception.Message, Severities.Error);
                    GridPw.IsEnabled = false; // do not hide (user must be able to see id)
                    return;
                }

                try
                {
                    await RunOrder();
                }
                catch (Exception exception)
                {
                    ShowMessage("Send after login failed. " + exception.Message, Severities.Error);
                }
            }
            finally
            {
                Cursor = origCursor;
            }
        }

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
                    GridLogout.Visibility = Visibility.Visible;
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

        private async Task RunOrder()
        {
            var msg = await _expressClient.Qualify(_orderHandler.GetOrderText());
            if (!string.IsNullOrEmpty(msg))
            {
                ShowMessage(msg, Severities.Warning);
                return;
            }

            ShowMessage("Uploading...", Severities.Info);

            using (var ms = _orderHandler.ZipOrderFiles())
            {
                msg = await _expressClient.Upload(_orderHandler.OrderId + ".zip", ms);
                if (!string.IsNullOrEmpty(msg))
                {
                    ShowMessage(msg, Severities.Error);
                    return;
                }
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

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var loggedIn = false;

            try
            {
                ShowLoginInTitle("");
                this.GridPw.Visibility = Visibility.Collapsed; // in most cases, we will not need the login part
                this.GridLogout.Visibility = Visibility.Collapsed;

                _idSettings = IdSettings.ReadOrNew();

                var uri = IdSettings.ReadAnyAssociatedUri(); // allow change
                if (uri == null)
                {
                    uri = new Uri("https://express.fullcontour.com/");
#if DEBUG
                    uri = new Uri("https://localhost:44334/");
#endif
                }

                _expressClient = new ExpressClient(uri);

                // detect if valid previous login. Not if too little time left.
                if (_idSettings.AuthCookie != null && _idSettings.AuthCookie.Expires > DateTime.UtcNow.AddMinutes(-5))
                    loggedIn = await _expressClient.IsLoggedIn(_idSettings.AuthCookie);

                // no order passed. This only happens when user double-clicks the app to log out. So only show that option,
                // if applicable
                if (string.IsNullOrEmpty(OrderDir))
                {
                    if (!loggedIn)
                    {
                        ShowMessage("No order chosen. You are not logged in.", Severities.Info);
                        return;
                    }

                    ShowMessage("No order chosen. You can log out.", Severities.Info);
                    this.GridLogout.Visibility = Visibility.Visible;
                    return;
                }

                _orderHandler = new OrderHandler(OrderDir);
                LabelOrder.Content = _orderHandler.OrderId;

                if (!loggedIn)
                {
                    this.GridPw.Visibility = Visibility.Visible;
                    if (CheckRemember.IsChecked == true && !string.IsNullOrEmpty(_idSettings.UserLogin))
                        TextLogin.Text = _idSettings.UserLogin;
                    return;
                }

                ShowLoginInTitle(_idSettings.UserLogin);
            }
            catch (Exception exception)
            {
                ShowMessage(exception.Message, Severities.Error);
                GridPw.IsEnabled = false;
                return;
            }

            try
            {
                await RunOrder();
            }
            catch (Exception exception)
            {
                ShowMessage("Send failed. " + exception.Message, Severities.Error);
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

        private async void ButtonLogout_OnClick(object sender, RoutedEventArgs e)
        {
            try
            {
                _idSettings.AuthCookie = null;
                IdSettings.Write(_idSettings);

                if (_expressClient == null)
                    return; // should not happen, but to be save

                await _expressClient.Logout();
                ShowMessage("You are logged out", Severities.Info);
                ShowLoginInTitle("");
                GridLogout.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                ShowMessage("Error during log out", Severities.Error);
            }
        }
    }
}

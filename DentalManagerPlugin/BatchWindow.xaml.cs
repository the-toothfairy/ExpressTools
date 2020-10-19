using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

using MessageBox = System.Windows.MessageBox;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;

namespace DentalManagerPlugin
{
    /// <summary>
    /// Interaction logic
    /// </summary>
    public partial class BatchWindow : Window
    {
        private ExpressClient _expressClient;
        private IdSettings _idSettings;

        private CancellationTokenSource _cancellationTokenSource;

        public BatchWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// add a paragraph with given <paramref name="text"/>.
        /// </summary>
        private void AddText(string text, Visual.Severities severity)
        {
            Dispatcher?.Invoke(() =>
            {
                var color = Visual.MessageColors[severity];
                var run = new Run("\n" + text) { Foreground = new SolidColorBrush(color) };
                Par.Inlines.Add(run);
            });
        }

        /// <summary>
        /// can also set to empty
        /// </summary>
        private void ShowLoginInTitle(string user)
        {
            var t = "FC Express Batch Upload";
            if (!string.IsNullOrEmpty(user))
                t += $" ({user})";

            Dispatcher.Invoke(() => { this.Title = t; });
        }

        private void RefreshLoginDependentControls(bool loggedIn, bool remembered)
        {
            ShowLoginInTitle(loggedIn ? _idSettings.UserLogin : "");
            ButtonStart.IsEnabled = loggedIn;
            ButtonCancel.IsEnabled = loggedIn;
            ButtonLogout.Visibility = loggedIn && remembered ? Visibility.Visible : Visibility.Hidden;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                ShowLoginInTitle("");

                _idSettings = IdSettings.ReadOrNew();

                TextOrderDir.Text = _idSettings.OrderDirectory;

                var uri = IdSettings.GetUri();

                _expressClient = new ExpressClient(uri);

                // detect if valid previous login
                var loggedIn = await _expressClient.CheckIfStillLoggedIn(_idSettings.AuthCookie);

                if (!loggedIn)
                {
                    var loginWindow = new LoginWindow(_idSettings, _expressClient);

                    loginWindow.Closed += (s, args) =>
                    {
                        RefreshLoginDependentControls(loginWindow.LoginSuccessful, loginWindow.LoginRemembered);
                    };

                    loginWindow.Owner = this;
                    loginWindow.ShowDialog();
                }
                else
                {
                    RefreshLoginDependentControls(true, true);
                }
            }
            catch (Exception exception)
            {
                AddText(exception.Message, Visual.Severities.Error);
                RefreshLoginDependentControls(false, false);
                return;
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
                AddText("You are logged out", Visual.Severities.Info);
                RefreshLoginDependentControls(false, false);
            }
            catch (Exception)
            {
                AddText("Error during log out", Visual.Severities.Error);
            }
        }

        private void ButtonBrowse_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new FolderBrowserDialog
                {
                    Description = @"Select the base directory for all orders (typically c:\3shape)",
                    SelectedPath = _idSettings.OrderDirectory
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;
                TextOrderDir.Text = dlg.SelectedPath;
                _idSettings.OrderDirectory = dlg.SelectedPath;
                IdSettings.Write(_idSettings);
            }
            catch (Exception exception)
            {
                AddText(exception.Message, Visual.Severities.Error);
            }
        }

        private async void ButtonStart_Click(object sender, RoutedEventArgs e)
        {
            var origCursor = Cursor;

            try
            {
                Par.Inlines.Clear();

                var orderBaseDi = new DirectoryInfo(_idSettings.OrderDirectory);
                if (!orderBaseDi.Exists)
                {
                    AddText("Order directory does not exist", Visual.Severities.Error);
                    return;
                }

                var subDirs = orderBaseDi.GetDirectories("", SearchOption.TopDirectoryOnly);

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();
                var token = _cancellationTokenSource.Token;

                var bDouble = double.TryParse(TextLastHours.Text, out var hours);
                if ( !bDouble )
                {
                    AddText("Invalid input for hours (wrong decimal separator?)", Visual.Severities.Error);
                    return;
                }

                var cutOff = DateTime.UtcNow.AddHours(-hours);
                Cursor = Cursors.Wait;

                int nUp = await RunAll(subDirs, cutOff, token);

                AddText($"\n{nUp} orders uploaded.", Visual.Severities.Info);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException || exception is AggregateException ae
                        && ae.InnerExceptions.Any(ie => ie is OperationCanceledException))
                    return; // should mostly be caught in task's try/catch

                AddText(exception.Message, Visual.Severities.Error);
            }

            Cursor = origCursor;
        }

        private async Task<int> RunAll(DirectoryInfo[] orderDirs, DateTime cutoffUtc, CancellationToken token)
        {
            var nUploads = 0;

            foreach (var orderDir in orderDirs)
            {
                if (token.IsCancellationRequested)
                {
                    var stop = false;
                    Dispatcher.Invoke(() =>
                    {
                        stop = (MessageBoxResult.Yes == MessageBox.Show("Are you sure you want to cancel?", "", MessageBoxButton.YesNo));
                    });
                    if (stop)
                        break;
                }

                try
                {
                    if (orderDir.Name == "ManufacturingDir") // special directory
                        continue;

                    var orderHandler = OrderHandler.MakeIfValid(orderDir);
                    if (orderHandler == null)
                    {
                        AddText($"{orderDir.Name}: not an order.", Visual.Severities.Warning);
                        continue;
                    }

                    orderHandler.GetStatusInfo(out var creationDateUtc, out var isScanned);
                    if (creationDateUtc < cutoffUtc)
                        continue; // no message, as there will often be many

                    if (!isScanned)
                    {
                        AddText($"{orderDir.Name}: not in scanned state.", Visual.Severities.Info);
                        continue;
                    }

                    var msg = await _expressClient.Qualify(orderHandler.GetOrderText());
                    if (!string.IsNullOrEmpty(msg))
                    {
                        AddText($"{orderDir.Name}: does not qualify.", Visual.Severities.Info);
                        continue;
                    }

                    //TODO REMOVE COMMENT
                    // do not allow cancel within operation (would be complicated to handle)
                    //using (var ms = orderHandler.ZipOrderFiles())
                    //    await _expressClient.Upload(orderHandler.OrderId + ".zip", ms, CancellationToken.None);

                    AddText($"{orderDir.Name}: uploaded.", Visual.Severities.Good);
                    nUploads++;
                }
                catch (Exception)
                {
                    // do not stop loop just because one case failed
                    AddText($"{orderDir.Name}: error.", Visual.Severities.Error);
                    continue;
                }
            }

            return nUploads;
        }


        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cancellationTokenSource?.Cancel();
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException || exception is AggregateException ae
                             && ae.InnerExceptions.Any(ie => ie is OperationCanceledException))
                    throw;

                AddText(exception.Message, Visual.Severities.Error);
            }
        }
    }
}

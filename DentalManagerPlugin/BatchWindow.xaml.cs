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
                var run = new Run(text) { Foreground = new SolidColorBrush(color) };
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

        private void RefreshLoginDependentControls(bool loggedIn)
        {
            ShowLoginInTitle(loggedIn ? _idSettings.UserLogin : "");
            ButtonStart.IsEnabled = loggedIn;
            ButtonCancel.IsEnabled = loggedIn;
            ButtonLogout.IsEnabled = loggedIn;
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
                        RefreshLoginDependentControls(loginWindow.LoginSuccessful);
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
                AddText(exception.Message, Visual.Severities.Error);
                RefreshLoginDependentControls(false);
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
                RefreshLoginDependentControls(false);
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

        private void ButtonStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
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

                Task.Run(() => RunAll(subDirs, token), token);
            }
            catch (Exception exception)
            {
                if (exception is OperationCanceledException || exception is AggregateException ae
                        && ae.InnerExceptions.Any(ie => ie is OperationCanceledException))
                     return; // should mostly be caught in task's try/catch

                AddText(exception.Message, Visual.Severities.Error);
            }
        }

        private async Task RunAll(DirectoryInfo[] orderDirs, CancellationToken token)
        {
            var nUploads = 0;

            foreach (var orderDir in orderDirs)
            {
                if (token.IsCancellationRequested)
                    return;

                try
                {
                    var orderHandler = OrderHandler.MakeIfValid(orderDir);
                    if (orderHandler == null)
                    {
                        AddText($"{orderDir.Name}: not an order.", Visual.Severities.Warning);
                        continue;
                    }

                    if (!orderHandler.IsScannedStatus())
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

                    using (var ms = orderHandler.ZipOrderFiles())
                        await _expressClient.Upload(orderHandler.OrderId + ".zip", ms, token);

                    AddText($"{orderDir.Name}: uploaded.", Visual.Severities.Good);
                    nUploads++;
                }
                catch (Exception exception)
                {
                    // any cancellation
                    if (exception is OperationCanceledException || exception is AggregateException ae
                                    && ae.InnerExceptions.Any(ie => ie is OperationCanceledException))
                    {
                        if (MessageBoxResult.No == MessageBox.Show("Are you sure you want to cancel?", "", MessageBoxButton.YesNo))
                            continue;

                        AddText("\n" + $"Cancelled. Uploaded {nUploads} orders.", Visual.Severities.Info);
                        return;
                    }

                    // but do not stop loop just because one case failed
                    AddText($"{orderDir.Name}: error.", Visual.Severities.Error);
                    continue;
                }
            }

            AddText("\n" + $"Done. Uploaded {nUploads} orders.", Visual.Severities.Good);
        }


        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cancellationTokenSource?.Cancel();
            }
            catch (Exception exception)
            {
                AddText(exception.Message, Visual.Severities.Error);
            }
        }
    }
}

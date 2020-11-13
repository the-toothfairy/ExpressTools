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
        private AppSettings _appSettings;

        private CancellationTokenSource _cancellationTokenSource;

        private class BatchSummary
        {
            internal int NoInDirectory { get; set; }
            internal int NoCreatedInPeriod { get; set; }
            internal int NoUploadedBefore { get; set; }
            internal int NoUploadedNow { get; set; }
            internal int NoNotQualifiedNow { get; set; }
        }

        public BatchWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// add a paragraph with given <paramref name="text"/>. Can also replace last.
        /// </summary>
        private void AddText(string text, Visual.Severities severity, bool replace = false)
        {
            Dispatcher?.Invoke(() =>
            {
                if (replace)
                    Par.Inlines.Remove(Par.Inlines.LastInline);

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

            t += _appSettings?.GetAnyTestingInfo();

            Dispatcher.Invoke(() => { this.Title = t; });
        }

        private void RefreshLoginDependentControls(bool loggedIn, bool remembered)
        {
            ShowLoginInTitle(loggedIn ? _idSettings.UserLogin : "");
            ButtonStart.IsEnabled = loggedIn;
            ButtonCancel.IsEnabled = loggedIn;
            ButtonLogout.Visibility = loggedIn && remembered ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _idSettings = IdSettings.ReadOrNew();
                _appSettings = AppSettings.ReadOrNew();
                ShowLoginInTitle("");

                TextOrderDir.Text = _appSettings.OrdersRootDirectory;

                var uri = _appSettings.GetUri();

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

                AddText(exception.StackTrace, Visual.Severities.Error);

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
                _idSettings.Write();

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
                    SelectedPath = _appSettings.OrdersRootDirectory
                };
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;
                TextOrderDir.Text = dlg.SelectedPath;
                _appSettings.OrdersRootDirectory = dlg.SelectedPath;
                AppSettings.Write(_appSettings);
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

                var orderBaseDi = new DirectoryInfo(_appSettings.OrdersRootDirectory);
                if (!orderBaseDi.Exists)
                {
                    AddText("Order directory does not exist", Visual.Severities.Error);
                    return;
                }

                var subDirs = orderBaseDi.GetDirectories("", SearchOption.TopDirectoryOnly);

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();
                var cancelToken = _cancellationTokenSource.Token;

                var bDouble = double.TryParse(TextLastHours.Text, out var hours);
                if (!bDouble)
                {
                    AddText("Invalid input for hours (wrong decimal separator?)", Visual.Severities.Error);
                    return;
                }

                var cutOff = DateTime.UtcNow.AddHours(-hours);
                Cursor = Cursors.Wait;

                var summary = await RunAll(subDirs, cutOff, cancelToken);

                var text = $"\nTotal number of orders in order directory: {summary.NoInDirectory}\n";
                text += $"Number of orders created in the last {hours} hours: {summary.NoCreatedInPeriod}\n";
                text += $"  thereof uploaded earlier: {summary.NoUploadedBefore}; did not qualify: {summary.NoNotQualifiedNow}\n";
                text += $"Number of orders uploaded now: {summary.NoUploadedNow}";

                AddText(text, Visual.Severities.Info);
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

        private async Task<BatchSummary> RunAll(DirectoryInfo[] orderDirs, DateTime cutoffUtc, CancellationToken cancelToken)
        {
            var summary = new BatchSummary();

            foreach (var orderDir in orderDirs)
            {
                if (cancelToken.IsCancellationRequested)
                    break;

                try
                {
                    if (orderDir.Name == "ManufacturingDir") // special directory
                        continue;

                    var orderHandler = OrderHandler.MakeIfValid(orderDir.FullName);
                    if (orderHandler == null)
                        continue;

                    summary.NoInDirectory++;

                    orderHandler.GetStatusInfo(out var creationDateUtc, out var isScanned);
                    if (creationDateUtc < cutoffUtc || !isScanned)
                        continue; // no message, as there will often be many

                    summary.NoCreatedInPeriod++;

                    var resultData = await _expressClient.GetStatus(orderHandler.OrderId);
                    if (resultData.Count > 0) // uploaded before
                    {
                        summary.NoUploadedBefore++;
                        continue;
                    }

                    var filterOutput = await _expressClient.Filter(orderHandler.AllRelativePaths);
                    if (filterOutput == null || filterOutput.Kind == "")
                    {
                        summary.NoNotQualifiedNow++;
                        continue;
                    }

                    using (var orderStream = orderHandler.GetStream(filterOutput.OrderPath))
                    using (var designStream = orderHandler.GetStream(filterOutput.DesignPath))
                    {
                        var msg = await _expressClient.Qualify(filterOutput, orderStream, designStream);
                        if (!string.IsNullOrEmpty(msg))
                        {
                            summary.NoNotQualifiedNow++;
                            continue;
                        }
                    }

                    AddText($"{orderDir.Name}: uploading...", Visual.Severities.Good);
                    using (var ms = orderHandler.ZipOrderFiles(filterOutput.AllPaths))
                        await _expressClient.Upload(orderHandler.OrderId + ".zip", ms, cancelToken);
                    AddText($"{orderDir.Name}: uploaded.", Visual.Severities.Good, true);

                    summary.NoUploadedNow++;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException ||
                            ex is AggregateException aex && aex.InnerExceptions.Any(iaex => iaex is OperationCanceledException))
                    {
                        AddText($"{orderDir.Name}: upload cancelled.", Visual.Severities.Warning, true);
                        break; // also stop other uploads
                    }
                    else
                    {
                        // do not stop loop just because one case failed
                        AddText($"{orderDir.Name}: error.", Visual.Severities.Error);
                        summary.NoNotQualifiedNow++;
                        continue;
                    }
                }
            }

            return summary;
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

        private void TextLastHours_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                const double defaultHours = 24;
                const double maxHours = 31 * 24 * 12 * 5;
                if (double.TryParse(TextLastHours.Text, out var dt))
                {
                    if (dt > maxHours)
                    {
                        TextLastHours.Text = maxHours.ToString();
                        e.Handled = true;
                    }
                    return;
                }

                TextLastHours.Text = defaultHours.ToString();
                e.Handled = true;
            }
            catch (Exception)
            {
            }
        }
    }
}

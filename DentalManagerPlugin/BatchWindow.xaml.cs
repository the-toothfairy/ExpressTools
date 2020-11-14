using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        private BatchSummary _summary;

        public ObservableCollection<UploadItem> UploadItems { get; } = new ObservableCollection<UploadItem>();

        /// <summary>
        /// for list view. checkable item. notifies if check state changed
        /// </summary>
        public class UploadItem : INotifyPropertyChanged
        {
            private bool _upload;

            public bool Upload
            {
                get => _upload;
                set
                {
                    if (value == _upload)
                        return;
                    _upload = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Upload)));
                }
            }

            public bool Uploaded { get; set; }

            internal OrderHandler OrderHandler { get; set; }

            internal List<string> AllPaths { get; set; }

            public string OrderName => OrderHandler?.OrderId;

            public string Message { get; set; }
            public Brush MessageBrush { get; set; }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private class BatchSummary
        {
            internal int NoInDirectory { get; set; }
            internal int NoCreatedInPeriod { get; set; }
            internal int NoUploadedBefore { get; set; }
            internal int NoQualified { get; set; }
            internal int NoSelected { get; set; }
        }

        public BatchWindow()
        {
            InitializeComponent();
        }

        private Brush MessageBrush(Visual.Severities severity) => new SolidColorBrush(Visual.MessageColors[severity]);

        /// <summary>
        /// set the message as <paramref name="text"/>.
        /// </summary>
        private void ShowOverallMessage(string text, Visual.Severities severity)
        {
            Dispatcher?.Invoke(() =>
            {
                TextMessage.Text = text;
                TextMessage.Foreground = MessageBrush(severity);
            });
        }

        private void ShowSummary()
        {
            if (_summary == null)
                return;
            var nc = _summary.NoCreatedInPeriod;
            var txt = $"Orders created in period: {nc};  uploaded earlier: {_summary.NoUploadedBefore};  " +
                $"qualifying now: {_summary.NoQualified};  selected: {_summary.NoSelected}";
            ShowOverallMessage(txt, Visual.Severities.Info);
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
            ButtonStartOrContinue.IsEnabled = loggedIn;
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

                var uri = _appSettings.GetUri(true); // TODO CHANGE

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

                ListViewUploads.ItemsSource = UploadItems; // bind first, so user can see progress of list being filled
                await RefreshUploadsList();

                ButtonStartOrContinue.IsEnabled = UploadItems.Any();
            }
            catch (Exception exception)
            {
                ShowOverallMessage(exception.Message, Visual.Severities.Error);
                ShowOverallMessage(exception.StackTrace, Visual.Severities.Error);
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
                ShowOverallMessage("You are logged out", Visual.Severities.Info);
                RefreshLoginDependentControls(false, false);
            }
            catch (Exception)
            {
                ShowOverallMessage("Error during log out", Visual.Severities.Error);
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
                ShowOverallMessage(exception.Message, Visual.Severities.Error);
            }
        }

        private async Task RefreshUploadsList()
        {
            var origCursor = Cursor;

            try
            {
                Cursor = Cursors.Wait;

                foreach (var upItem in UploadItems)
                    upItem.PropertyChanged -= UploadCheckChanged;
                UploadItems.Clear();
                Dispatcher.Invoke(() => ButtonStartOrContinue.Content = "Start Uploading"); // may get disabled

                ShowOverallMessage("", Visual.Severities.Good);

                var orderBaseDirInfo = new DirectoryInfo(_appSettings.OrdersRootDirectory);
                if (!orderBaseDirInfo.Exists)
                {
                    ShowOverallMessage("Orders root directory does not exist", Visual.Severities.Error);
                    return;
                }

                var bDouble = double.TryParse(TextLastHours.Text, out var hours);
                if (!bDouble)
                {
                    ShowOverallMessage("Invalid input for hours (wrong decimal separator?)", Visual.Severities.Error);
                    return;
                }

                var cutoffUtc = DateTime.UtcNow.AddHours(-hours);
                var subDirs = orderBaseDirInfo.GetDirectories("", SearchOption.TopDirectoryOnly);

                _summary = new BatchSummary();

                foreach (var orderDir in subDirs)
                {
                    ShowOverallMessage($"Examining {orderDir.Name}...", Visual.Severities.Info);

                    if (orderDir.Name == "ManufacturingDir") // special directory
                        continue;

                    var orderHandler = OrderHandler.MakeIfValid(orderDir.FullName);
                    if (orderHandler == null)
                        continue;

                    _summary.NoInDirectory++;

                    orderHandler.GetStatusInfo(out var creationDateUtc, out var isScanned);
                    if (creationDateUtc < cutoffUtc || !isScanned)
                        continue; // no message, as there will often be many

                    _summary.NoCreatedInPeriod++;

                    var resultData = await _expressClient.GetStatus(orderHandler.OrderId);
                    if (resultData.Count > 0) // uploaded before
                    {
                        _summary.NoUploadedBefore++;
                        continue;
                    }

                    var filterOutput = await _expressClient.Filter(orderHandler.AllRelativePaths);
                    if (filterOutput == null || filterOutput.Kind == "")
                        continue;

                    using (var orderStream = orderHandler.GetStream(filterOutput.OrderPath))
                    using (var designStream = orderHandler.GetStream(filterOutput.DesignPath))
                    {
                        var msg = await _expressClient.Qualify(filterOutput, orderStream, designStream);
                        if (!string.IsNullOrEmpty(msg))
                            continue;
                    }

                    var upItem = new UploadItem { OrderHandler = orderHandler, AllPaths = filterOutput.AllPaths, Upload = true };
                    upItem.PropertyChanged += UploadCheckChanged;
                    UploadItems.Add(upItem);

                    _summary.NoQualified++;
                    _summary.NoSelected++;
                }

                ShowSummary();
            }
            catch (Exception ex)
            {
                ShowOverallMessage($"Error: {ex.Message}", Visual.Severities.Error);
            }
            finally
            {
                Cursor = origCursor;
                Dispatcher.Invoke(() => ButtonStartOrContinue.IsEnabled = _summary?.NoSelected > 0);
            }
        }

        private void UploadCheckChanged(object sender, PropertyChangedEventArgs args)
        {
            if (_summary == null)
                return;

            _summary.NoSelected = UploadItems.Count(ui => ui.Upload);
            ShowSummary();
        }

        private async void ButtonStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ListViewUploads.IsEnabled = false; // do not allow checkbox changes during upload

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();
                var cancelToken = _cancellationTokenSource.Token;

                await RunAll(cancelToken);

                if (UploadItems.Any(ui => !ui.Uploaded)) // cancelled?
                    ButtonStartOrContinue.Content = "Continue Uploading";
                else
                    ButtonStartOrContinue.IsEnabled = false;
            }
            catch (Exception exception)
            {
                ShowOverallMessage(exception.Message, Visual.Severities.Error);
            }
            finally
            {
                ListViewUploads.IsEnabled = true;
            }
        }

        private async Task RunAll(CancellationToken cancelToken)
        {
            foreach (var uploadItem in UploadItems)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    ShowOverallMessage("Uploads cancelled.", Visual.Severities.Warning);
                    break;
                }

                // user unselected, or was uploaded, then other item's upload canceled, so this item still in list when
                // upload continued
                if (!uploadItem.Upload || uploadItem.Uploaded)
                    continue;

                try
                {
                    uploadItem.Message = "uploading...";
                    uploadItem.MessageBrush = MessageBrush(Visual.Severities.Info);

                    using (var ms = uploadItem.OrderHandler.ZipOrderFiles(uploadItem.AllPaths))
                        await _expressClient.Upload(uploadItem.OrderHandler.OrderId + ".zip", ms, cancelToken);

                    uploadItem.Message = "uploaded";
                    uploadItem.MessageBrush = MessageBrush(Visual.Severities.Good);
                    uploadItem.Uploaded = true;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException ||
                            ex is AggregateException aex && aex.InnerExceptions.Any(iaex => iaex is OperationCanceledException))
                    {
                        uploadItem.Message = "upload cancelled";
                        uploadItem.MessageBrush = MessageBrush(Visual.Severities.Warning);
                        break; // also stop other uploads
                    }
                    else
                    {
                        // do not stop loop just because one case failed
                        uploadItem.Message = "Error: " + ex.Message;
                        uploadItem.MessageBrush = MessageBrush(Visual.Severities.Error);
                        continue;
                    }
                }
            }
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cancellationTokenSource?.Cancel();
            }
            catch (Exception exception)
            {
                ShowOverallMessage(exception.Message, Visual.Severities.Error);
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

        private async void ButtonRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await RefreshUploadsList();
            }
            catch (Exception exception)
            {
                ShowOverallMessage(exception.Message, Visual.Severities.Error);
            }
        }
    }
}

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

namespace DentalManagerPlugin
{
    /// <summary>
    /// Interaction logic for LoginWindow.xaml
    /// </summary>
    public partial class LoginWindow : Window
    {
        private readonly IdSettings _idSettings;
        private readonly ExpressClient _expressClient;

        public bool LoginSuccessful { get; private set; }

        public bool LoginRemembered { get; private set; }

        public bool VersionOk { get; private set; }



        public LoginWindow(IdSettings idSettings, ExpressClient expressClient)
        {
            InitializeComponent();

            _expressClient = expressClient;
            _idSettings = idSettings;

            this.Title += $"Login required for {_expressClient.UriString}";
        }

        /// <summary>
        /// login and then automatically run the order
        /// </summary>
        private async void ButtonLogin_Click(object sender, RoutedEventArgs e)
        {
            LoginSuccessful = false;
            LoginRemembered = false;
            VersionOk = false;
            LabelErrorMessage.Content = "";
            var origCursor = Cursor;
            try
            {
                Cursor = Cursors.Wait;
                try
                {
                    var loggedIn = await _expressClient.Login(TextLogin.Text, Pw.Password, CheckRemember.IsChecked == true);
                    if (!loggedIn)
                    {
                        LabelErrorMessage.Content = "Login failed.";
                        return;
                    }

                    if (CheckRemember.IsChecked == true)
                    {
                        _idSettings.AuthCookie = _expressClient.AuthCookie;
                        _idSettings.UserLogin = TextLogin.Text;
                        LoginRemembered = true;
                    }
                    else
                    {
                        _idSettings.AuthCookie = null;
                    }

                    _idSettings.Write(); // also if null auth cookie

                    LoginSuccessful = true;

                    VersionOk = await _expressClient.CheckIfCurrentVersion();
                    if ( !VersionOk )
                    {
                        RowUpdateMessage.MaxHeight = 100;
                        ButtonLogin.IsEnabled = false;
                        return;
                    }

                    this.Close();
                }
                catch (Exception exception)
                {
                    LabelErrorMessage.Content = exception.Message;
                    return;
                }
            }
            finally
            {
                Cursor = origCursor;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RowUpdateMessage.MaxHeight = 0; // hide

                if (!string.IsNullOrEmpty(_idSettings.UserLogin))
                    TextLogin.Text = _idSettings.UserLogin;
            }
            catch (Exception exception)
            {
                LabelErrorMessage.Content = exception.Message;
            }
        }

        private void ButtonOk_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Close();
            }
            catch ( Exception exception)
            {
                LabelErrorMessage.Content = exception.Message;
            }
        }
    }
}

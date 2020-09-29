using System.Windows;

namespace DentalManagerPlugin
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            var wnd = new MainWindow();

            if (e.Args.Length == 1)
            {
                // DentalManager adds quote at end only, remove it
                var dir = e.Args[0];
                if (dir.EndsWith("\"") && !dir.StartsWith("\""))
                    dir = dir.Substring(0, dir.Length - 1);

                wnd.OrderDir = dir;
            }

            wnd.Show();
        }
    }
}

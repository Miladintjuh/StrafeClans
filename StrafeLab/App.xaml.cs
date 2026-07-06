using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace StrafeLab;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteStartupError(ex);
        };

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            WriteStartupError(ex);
            MessageBox.Show(
                "StrafeLab failed to open.\n\n" + ex.Message + "\n\nA startup-error.txt file was written to %LOCALAPPDATA%\\StrafeLab.",
                "StrafeLab startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteStartupError(e.Exception);
        MessageBox.Show(
            "StrafeLab hit an unexpected UI error.\n\n" + e.Exception.Message + "\n\nA startup-error.txt file was written to %LOCALAPPDATA%\\StrafeLab.",
            "StrafeLab error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void WriteStartupError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StrafeLab");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "startup-error.txt"), ex.ToString());
        }
        catch
        {
            // Do not throw from the crash logger.
        }
    }
}

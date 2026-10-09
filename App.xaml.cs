using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ManifestDownloader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash(args.ExceptionObject as Exception ?? new Exception($"{args.ExceptionObject}"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrash(args.Exception);
            args.SetObserved();
        };
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        try
        {
            MessageBox.Show(
                $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were appended to crash.log in the app data folder.",
                "Steam Manifest Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { }
        e.Handled = true;
    }

    private static void WriteCrash(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ManifestDownloader");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}

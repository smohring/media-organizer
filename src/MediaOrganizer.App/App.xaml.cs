using System.Windows;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before the first window exists: MainWindow is created from StartupUri
        // right after this returns, and every DynamicResource in it resolves
        // against whatever dictionary is loaded by then.
        Loc.Apply(new SettingsStore().Load().UiLanguage);

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(
                Loc.Format("S.Crash.Body", args.Exception.Message),
                "Media Organizer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception);
    }

    private static void LogCrash(Exception? exception)
    {
        try
        {
            var logPath = System.IO.Path.Combine(AppDataFolder.Root, "error.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:O}]\n{exception}\n\n");
        }
        catch
        {
            // Logging must never become the problem itself.
        }
    }
}

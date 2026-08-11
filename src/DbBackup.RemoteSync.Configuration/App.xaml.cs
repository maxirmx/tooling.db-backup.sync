using System.Globalization;
using System.Windows;

namespace DbBackup.RemoteSync.Configuration;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (eventArgs.Args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(RunUninstallCleanup());
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, ProductConstants.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var culture = GetInitialCulture();
        Localization.Apply(this, culture);
        MainWindow = new MainWindow(culture);
        MainWindow.Show();
    }

    private static string GetInitialCulture()
    {
        try
        {
            var settings = new SettingsStore(ApplicationDataPaths.Default)
                .LoadAsync()
                .GetAwaiter()
                .GetResult();
            if (settings?.UiCulture is "en" or "ru")
            {
                return settings.UiCulture;
            }
        }
        catch (Exception)
        {
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
    }

    private static int RunUninstallCleanup()
    {
        try
        {
            var settings = new SettingsStore(ApplicationDataPaths.Default)
                .LoadAsync()
                .GetAwaiter()
                .GetResult();
            if (settings is not null)
            {
                new ServiceDirectoryAccessManager().RemoveManagedRule(settings.Destination.LocalFolder);
            }

            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}

// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Globalization;
using System.Windows;

namespace DbBackup.RemoteSync.Configuration;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\DbBackupRemoteSync.Configuration.Instance";
    private const string ActivationEventName = @"Local\DbBackupRemoteSync.Configuration.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        if (eventArgs.Args.Contains("--uninstall-cleanup", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(RunUninstallCleanup());
            return;
        }

        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            InstanceMutexName,
            out _ownsInstanceMutex);
        if (!_ownsInstanceMutex)
        {
            _activationEvent.Set();
            Shutdown(0);
            return;
        }

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            ActivateMainWindow,
            state: null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

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

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(eventArgs);
    }

    private void ActivateMainWindow(object? state, bool timedOut)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (MainWindow is not Window window)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        });
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

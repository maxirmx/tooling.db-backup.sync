// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DbBackup.RemoteSync.Configuration;

public partial class MainWindow : Window
{
    private readonly ApplicationDataPaths _paths = ApplicationDataPaths.Default;
    private readonly SettingsStore _settingsStore;
    private readonly HostTrustStore _trustStore;
    private readonly DpapiCredentialStore _credentialStore;
    private readonly ServiceControlClient _controlClient = new();
    private readonly IRemoteFileClientFactory _remoteFactory = new SftpRemoteFileClientFactory();
    private readonly DispatcherTimer _statusTimer;
    private string _culture;
    private RemoteSyncSettings? _loadedSettings;
    private TrustedHostKey? _pendingTrust;
    private bool _credentialExists;
    private bool _initializing = true;

    public MainWindow(string culture)
    {
        _culture = culture;
        _settingsStore = new SettingsStore(_paths);
        _trustStore = new HostTrustStore(_paths);
        _credentialStore = new DpapiCredentialStore(_paths);
        InitializeComponent();
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, StatusTimer_Tick, Dispatcher);
        Loaded += Window_Loaded;
        Closed += (_, _) => _statusTimer.Stop();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            _loadedSettings = await _settingsStore.LoadAsync();
            var settings = _loadedSettings ?? new RemoteSyncSettings { UiCulture = _culture };
            HostText.Text = settings.Connection.Host;
            PortText.Text = settings.Connection.Port.ToString(CultureInfo.InvariantCulture);
            UsernameText.Text = settings.Connection.Username;
            RemoteFolderText.Text = settings.Connection.RemoteFolder;
            LocalFolderText.Text = settings.Destination.LocalFolder;
            RecursiveCheck.IsChecked = settings.Destination.Recursive;
            DailyTimeText.Text = settings.Schedule.DailyLocalTime;
            _culture = settings.UiCulture;
            LanguageCombo.SelectedIndex = _culture == "ru" ? 1 : 0;
            Localization.Apply(Application.Current, _culture);
            _credentialExists = _credentialStore.Exists;
            await RefreshTrustAsync();
            await RefreshStatusAsync();
            _statusTimer.Start();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFolderDialog
        {
            Title = L("LocalFolder"),
            Multiselect = false,
        };
        if (Directory.Exists(LocalFolderText.Text))
        {
            dialog.InitialDirectory = LocalFolderText.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            LocalFolderText.Text = dialog.FolderName;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs eventArgs) =>
        await RunUiOperationAsync(TestConnectionAsync);

    private async Task TestConnectionAsync()
    {
        var settings = BuildSettings();
        EnsureValid(settings);
        var password = await GetPasswordAsync();
        var existingTrust = await _trustStore.LoadAsync();
        TrustedHostKey? acceptedTrust = null;

        bool ValidateHostKey(PresentedHostKey presented)
        {
            if (existingTrust?.Matches(presented) == true)
            {
                return true;
            }

            var confirmed = Dispatcher.Invoke(() =>
            {
                var changed = existingTrust?.MatchesEndpoint(settings.Connection) == true;
                var message = changed
                    ? Format("ReplaceTrustPrompt", existingTrust!.Sha256Fingerprint, presented.Sha256Fingerprint)
                    : Format("TrustPrompt", presented.Algorithm, presented.Sha256Fingerprint);
                return MessageBox.Show(
                    this,
                    message,
                    ProductConstants.ProductName,
                    MessageBoxButton.YesNo,
                    changed ? MessageBoxImage.Warning : MessageBoxImage.Question) == MessageBoxResult.Yes;
            });
            if (confirmed)
            {
                acceptedTrust = new TrustedHostKey
                {
                    Host = presented.Host,
                    Port = presented.Port,
                    Algorithm = presented.Algorithm,
                    Sha256Fingerprint = presented.Sha256Fingerprint,
                };
            }

            return confirmed;
        }

        await using var remote = _remoteFactory.Create(settings, password, ValidateHostKey);
        await remote.TestConnectionAsync(CancellationToken.None);
        if (acceptedTrust is not null)
        {
            _pendingTrust = acceptedTrust;
        }
        else
        {
            _pendingTrust = null;
        }

        await RefreshTrustAsync();
        MessageBox.Show(this, L("ConnectionSucceeded"), ProductConstants.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Save_Click(object sender, RoutedEventArgs eventArgs) =>
        await RunUiOperationAsync(SaveAsync);

    private async Task SaveAsync()
    {
        var currentStatus = await TryGetStatusAsync();
        if (currentStatus?.IsRunning == true)
        {
            MessageBox.Show(this, L("BusySave"), ProductConstants.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = BuildSettings();
        EnsureValid(settings);
        var trust = _pendingTrust ?? await _trustStore.LoadAsync();
        if (trust is null || !trust.MatchesEndpoint(settings.Connection))
        {
            throw new InvalidOperationException(L("TrustRequired"));
        }

        if (!_credentialExists && string.IsNullOrEmpty(PasswordInput.Password))
        {
            throw new InvalidOperationException(L("PasswordRequired"));
        }

        var oldFolder = _loadedSettings?.Destination.LocalFolder;
        var accessManager = new ServiceDirectoryAccessManager();
        accessManager.GrantModify(settings.Destination.LocalFolder);
        if (!string.IsNullOrEmpty(PasswordInput.Password))
        {
            await _credentialStore.SaveAsync(PasswordInput.Password);
            _credentialExists = true;
        }

        await _trustStore.SaveAsync(trust);
        await _settingsStore.SaveAsync(settings);
        try
        {
            await _controlClient.SendAsync(
                ControlCommand.ReloadConfiguration,
                TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }

        if (!string.IsNullOrWhiteSpace(oldFolder) &&
            !string.Equals(oldFolder, settings.Destination.LocalFolder, StringComparison.OrdinalIgnoreCase))
        {
            accessManager.RemoveManagedRule(oldFolder);
        }

        _loadedSettings = settings;
        _pendingTrust = null;
        PasswordInput.Clear();
        MessageBox.Show(this, L("Saved"), ProductConstants.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
        await RefreshStatusAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs eventArgs) => await RefreshStatusAsync();

    private async void RunNow_Click(object sender, RoutedEventArgs eventArgs)
    {
        await RunUiOperationAsync(async () =>
        {
            var response = await _controlClient.SendAsync(ControlCommand.RunNow, TimeSpan.FromSeconds(3));
            if (!response.Accepted)
            {
                throw new InvalidOperationException(Format("CommandRejected", response.Error ?? response.Code));
            }

            MessageBox.Show(this, L("RunQueued"), ProductConstants.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshStatusAsync();
        });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs eventArgs) =>
        Process.Start(new ProcessStartInfo("eventvwr.msc", "/c:localhost") { UseShellExecute = true });

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_initializing || LanguageCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _culture = string.Equals(item.Tag as string, "ru", StringComparison.Ordinal) ? "ru" : "en";
        Localization.Apply(Application.Current, _culture);
    }

    private async void StatusTimer_Tick(object? sender, EventArgs eventArgs) => await RefreshStatusAsync();

    private async Task RefreshTrustAsync()
    {
        var trust = _pendingTrust ?? await _trustStore.LoadAsync();
        TrustedKeyText.Text = trust is null
            ? L("NotTrusted")
            : $"{trust.Algorithm}  {trust.Sha256Fingerprint}";
    }

    private async Task<ServiceStatus?> TryGetStatusAsync()
    {
        try
        {
            var response = await _controlClient.SendAsync(ControlCommand.GetStatus, TimeSpan.FromSeconds(2));
            return response.Status;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task RefreshStatusAsync()
    {
        var status = await TryGetStatusAsync();
        if (status is null)
        {
            ServiceStatusText.Text = L("ServiceUnavailable");
            RunButton.IsEnabled = false;
            return;
        }

        RunButton.IsEnabled = status.ConfigurationValid && !status.IsRunning;
        var lines = new List<string>
        {
            status.ConfigurationValid
                ? L("ConfigurationValid")
                : Format("ConfigurationInvalid", LocalizeCode(status.ConfigurationError ?? "Unknown")),
            status.IsRunning ? Format("Running", status.ActiveReason ?? string.Empty) : L("Idle"),
        };
        if (status.NextAttemptUtc is { } next)
        {
            lines.Add(Format("NextAttempt", next.ToLocalTime().ToString("G", CultureInfo.CurrentCulture)));
        }
        if (status.RetryNumber > 0)
        {
            lines.Add(Format("RetryNumber", status.RetryNumber));
        }

        if (status.LastRun is { } last)
        {
            lines.Add(last.Succeeded
                ? Format(
                    "LastSucceeded",
                    last.CompletedUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture),
                    last.Downloaded,
                    last.AlreadyPresent,
                    last.RaceSkipped)
                : Format(
                    "LastFailed",
                    last.CompletedUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture),
                    last.Error ?? "Unknown"));
        }

        ServiceStatusText.Text = string.Join(Environment.NewLine, lines);
    }

    private RemoteSyncSettings BuildSettings() => new()
    {
        Connection = new ConnectionSettings
        {
            Host = HostText.Text.Trim(),
            Port = int.TryParse(PortText.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0,
            Username = UsernameText.Text.Trim(),
            RemoteFolder = RemoteFolderText.Text,
        },
        Destination = new DestinationSettings
        {
            LocalFolder = LocalFolderText.Text,
            Recursive = RecursiveCheck.IsChecked == true,
        },
        Schedule = new ScheduleSettings { DailyLocalTime = DailyTimeText.Text.Trim() },
        UiCulture = _culture,
    };

    private void EnsureValid(RemoteSyncSettings settings)
    {
        var issues = ConfigurationValidator.Validate(settings);
        if (issues.Count != 0)
        {
            throw new InvalidDataException(Format(
                "InvalidFields",
                string.Join(", ", issues.Select(issue => LocalizeCode(issue.MessageKey)))));
        }
    }

    private async Task<string> GetPasswordAsync()
    {
        if (!string.IsNullOrEmpty(PasswordInput.Password))
        {
            return PasswordInput.Password;
        }

        if (_credentialExists)
        {
            return await _credentialStore.LoadAsync();
        }

        throw new InvalidOperationException(L("PasswordRequired"));
    }

    private async Task RunUiOperationAsync(Func<Task> operation)
    {
        SetBusy(true);
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        TestButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
    }

    private void ShowError(Exception exception) =>
        MessageBox.Show(
            this,
            Format("OperationFailed", exception.Message),
            ProductConstants.ProductName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static string L(string key) => (string)Application.Current.FindResource(key);

    private static string LocalizeCode(string code) =>
        Application.Current.TryFindResource(code) is string localized ? localized : code;

    private static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, L(key), arguments);
}

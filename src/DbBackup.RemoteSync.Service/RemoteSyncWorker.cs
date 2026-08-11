using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbBackup.RemoteSync.Service;

public sealed class RemoteSyncWorker : BackgroundService, IServiceControl
{
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(15);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SettingsStore _settingsStore;
    private readonly HostTrustStore _trustStore;
    private readonly SchedulerStateStore _stateStore;
    private readonly DpapiCredentialStore _credentialStore;
    private readonly SynchronizationEngine _engine;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RemoteSyncWorker> _logger;
    private ServiceStatus _status = new() { ConfigurationError = "MissingSettings" };
    private bool _manualPending;

    public RemoteSyncWorker(
        SettingsStore settingsStore,
        HostTrustStore trustStore,
        SchedulerStateStore stateStore,
        DpapiCredentialStore credentialStore,
        SynchronizationEngine engine,
        TimeProvider timeProvider,
        ILogger<RemoteSyncWorker> logger)
    {
        _settingsStore = settingsStore;
        _trustStore = trustStore;
        _stateStore = stateStore;
        _credentialStore = credentialStore;
        _engine = engine;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public ControlResponse GetStatus()
    {
        lock (_gate)
        {
            return Accepted("Status", _status);
        }
    }

    public ControlResponse RequestReload()
    {
        lock (_gate)
        {
            if (_status.IsRunning)
            {
                return Rejected("Busy", "Configuration cannot be reloaded while synchronization is active.");
            }
        }

        Wake();
        return Accepted("ReloadQueued");
    }

    public ControlResponse RequestRunNow()
    {
        lock (_gate)
        {
            if (_status.IsRunning || _manualPending)
            {
                return Rejected("AlreadyRunning", "A synchronization is already active or queued.");
            }

            if (!_status.ConfigurationValid)
            {
                return Rejected("NotConfigured", _status.ConfigurationError ?? "The configuration is invalid.");
            }

            _manualPending = true;
        }

        Wake();
        return Accepted("RunQueued");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(ApplicationDataPaths.Default.RootDirectory);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdlePollInterval;
            try
            {
                var context = await LoadConfigurationAsync(stoppingToken).ConfigureAwait(false);
                if (context is null)
                {
                    await WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                bool manual;
                lock (_gate)
                {
                    manual = _manualPending;
                    _manualPending = false;
                }

                var state = await _stateStore.LoadAsync(stoppingToken).ConfigureAwait(false);
                var now = _timeProvider.GetUtcNow();
                if (manual)
                {
                    await ExecuteRunAsync(context, state, "manual", isScheduled: false, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var decision = SchedulePlanner.Evaluate(
                    state,
                    now,
                    context.Settings.Schedule.GetTime(),
                    TimeZoneInfo.Local);
                if (decision.State != state)
                {
                    await _stateStore.SaveAsync(decision.State, stoppingToken).ConfigureAwait(false);
                }

                if (decision.Action == ScheduledAction.RunNow)
                {
                    await ExecuteRunAsync(context, decision.State, "scheduled", isScheduled: true, stoppingToken)
                        .ConfigureAwait(false);
                    continue;
                }

                delay = ClampDelay(decision.NextWakeUtc - now);
                UpdateStatus(current => current with
                {
                    NextAttemptUtc = decision.NextWakeUtc,
                    RetryNumber = decision.State.ScheduledStatus == ScheduledSlotStatus.Pending
                        ? decision.State.AttemptsCompleted
                        : 0,
                    LastRun = decision.State.LastRun,
                });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                SetConfigurationError(Sanitize(exception.Message, null));
                _logger.LogError(exception, "The service loop failed.");
            }

            await WaitAsync(delay, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<ConfigurationContext?> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (settings is null)
            {
                SetConfigurationError("MissingSettings");
                return null;
            }

            var issues = ConfigurationValidator.Validate(settings);
            if (issues.Count != 0)
            {
                SetConfigurationError(string.Join(", ", issues.Select(issue => issue.MessageKey)), settings.UiCulture);
                return null;
            }

            if (!_credentialStore.Exists)
            {
                SetConfigurationError("MissingCredential", settings.UiCulture);
                return null;
            }

            var trust = await _trustStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (trust is null || !trust.MatchesEndpoint(settings.Connection))
            {
                SetConfigurationError("MissingOrMismatchedHostTrust", settings.UiCulture);
                return null;
            }

            UpdateStatus(current => current with
            {
                ConfigurationValid = true,
                ConfigurationError = null,
            });
            return new(settings, trust);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetConfigurationError(Sanitize(exception.Message, null));
            return null;
        }
    }

    private async Task ExecuteRunAsync(
        ConfigurationContext context,
        SchedulerState state,
        string reason,
        bool isScheduled,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetUtcNow();
        UpdateStatus(current => current with
        {
            IsRunning = true,
            ActiveReason = reason,
            NextAttemptUtc = null,
        });
        _logger.LogInformation(
            1000,
            MessageCatalog.Get(
                context.Settings.UiCulture,
                "RunStarted",
                MessageCatalog.DescribeReason(context.Settings.UiCulture, reason)));
        string? password = null;

        try
        {
            password = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var result = await _engine.SynchronizeAsync(
                context.Settings,
                password,
                context.Trust,
                cancellationToken).ConfigureAwait(false);
            var completed = _timeProvider.GetUtcNow();
            var localNow = TimeZoneInfo.ConvertTime(completed, TimeZoneInfo.Local);
            var localDate = DateOnly.FromDateTime(localNow.DateTime);
            var afterScheduledTime = completed >= SchedulePlanner.ResolveScheduledUtc(
                localDate,
                context.Settings.Schedule.GetTime(),
                TimeZoneInfo.Local);
            var lastRun = new LastRunState
            {
                Reason = reason,
                StartedUtc = started,
                CompletedUtc = completed,
                Succeeded = true,
                RemoteFiles = result.RemoteFiles,
                AlreadyPresent = result.AlreadyPresent,
                Downloaded = result.Downloaded,
                RaceSkipped = result.RaceSkipped,
            };
            var updated = isScheduled || afterScheduledTime
                ? SchedulePlanner.RecordScheduledSuccess(state, localDate)
                : state;
            updated = updated with { LastRun = lastRun };
            await _stateStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            UpdateStatus(current => current with
            {
                IsRunning = false,
                ActiveReason = null,
                LastRun = lastRun,
                RetryNumber = 0,
            });
            _logger.LogInformation(
                1001,
                MessageCatalog.Get(
                    context.Settings.UiCulture,
                    "RunCompleted",
                    result.Downloaded,
                    result.AlreadyPresent,
                    result.RaceSkipped));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateStatus(current => current with { IsRunning = false, ActiveReason = null });
            throw;
        }
        catch (Exception exception)
        {
            var completed = _timeProvider.GetUtcNow();
            var error = Sanitize(exception.Message, password);
            var lastRun = new LastRunState
            {
                Reason = reason,
                StartedUtc = started,
                CompletedUtc = completed,
                Succeeded = false,
                Error = error,
            };
            var updated = isScheduled
                ? SchedulePlanner.RecordScheduledFailure(state, completed)
                : state;
            updated = updated with { LastRun = lastRun };
            await _stateStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            UpdateStatus(current => current with
            {
                IsRunning = false,
                ActiveReason = null,
                LastRun = lastRun,
                NextAttemptUtc = updated.NextAttemptUtc,
                RetryNumber = updated.AttemptsCompleted,
            });
            _logger.LogError(1002, MessageCatalog.Get(context.Settings.UiCulture, "RunFailed", error));
        }
    }

    private void SetConfigurationError(string error, string culture = "en")
    {
        bool changed;
        lock (_gate)
        {
            changed = _status.ConfigurationValid ||
                !string.Equals(_status.ConfigurationError, error, StringComparison.Ordinal);
            _status = _status with
            {
                ConfigurationValid = false,
                ConfigurationError = error,
                NextAttemptUtc = null,
            };
        }

        if (changed)
        {
            _logger.LogWarning(
                1100,
                MessageCatalog.Get(
                    culture,
                    "ConfigurationInvalid",
                    MessageCatalog.DescribeError(culture, error)));
        }
    }

    private void UpdateStatus(Func<ServiceStatus, ServiceStatus> update)
    {
        lock (_gate)
        {
            _status = update(_status);
        }
    }

    private void Wake()
    {
        if (_wakeSignal.CurrentCount == 0)
        {
            _wakeSignal.Release();
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await _wakeSignal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan ClampDelay(TimeSpan delay) =>
        delay <= TimeSpan.Zero
            ? TimeSpan.Zero
            : delay < IdlePollInterval ? delay : IdlePollInterval;

    private static string Sanitize(string text, string? password)
    {
        var sanitized = string.IsNullOrEmpty(password)
            ? text
            : text.Replace(password, "***", StringComparison.Ordinal);
        return sanitized.Length <= 2000 ? sanitized : sanitized[..2000];
    }

    private static ControlResponse Accepted(string code, ServiceStatus? status = null) =>
        new(ProductConstants.ControlProtocolVersion, true, code, status);

    private static ControlResponse Rejected(string code, string error) =>
        new(ProductConstants.ControlProtocolVersion, false, code, Error: error);

    private sealed record ConfigurationContext(RemoteSyncSettings Settings, TrustedHostKey Trust);
}

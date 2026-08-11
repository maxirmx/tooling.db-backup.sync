using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbBackup.RemoteSync;

public enum ControlCommand
{
    GetStatus,
    ReloadConfiguration,
    RunNow,
}

public sealed record ControlRequest(
    int ProtocolVersion,
    ControlCommand Command);

public sealed record ServiceStatus
{
    public bool ConfigurationValid { get; init; }
    public bool IsRunning { get; init; }
    public string? ConfigurationError { get; init; }
    public string? ActiveReason { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public int RetryNumber { get; init; }
    public LastRunState? LastRun { get; init; }
}

public sealed record ControlResponse(
    int ProtocolVersion,
    bool Accepted,
    string Code,
    ServiceStatus? Status = null,
    string? Error = null);

public sealed class ServiceControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<ControlResponse> SendAsync(
        ControlCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            ProductConstants.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);

        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        var request = new ControlRequest(ProductConstants.ControlProtocolVersion, command);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), timeoutSource.Token)
            .ConfigureAwait(false);
        var responseLine = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false)
            ?? throw new IOException("The service closed the control pipe without a response.");
        return JsonSerializer.Deserialize<ControlResponse>(responseLine, JsonOptions)
            ?? throw new InvalidDataException("The service returned an invalid control response.");
    }
}

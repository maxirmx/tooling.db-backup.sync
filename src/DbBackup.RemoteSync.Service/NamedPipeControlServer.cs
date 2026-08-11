using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbBackup.RemoteSync.Service;

public sealed class NamedPipeControlServer(
    IServiceControl control,
    ILogger<NamedPipeControlServer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await HandleConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The service control pipe failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        return NamedPipeServerStreamAcl.Create(
            ProductConstants.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            4096,
            4096,
            security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        ControlResponse response;

        try
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The control request was empty.");
            var request = JsonSerializer.Deserialize<ControlRequest>(line, JsonOptions)
                ?? throw new InvalidDataException("The control request was invalid.");
            response = request.ProtocolVersion != ProductConstants.ControlProtocolVersion
                ? new ControlResponse(
                    ProductConstants.ControlProtocolVersion,
                    false,
                    "ProtocolMismatch",
                    Error: "The configuration utility and service protocol versions differ.")
                : request.Command switch
                {
                    ControlCommand.GetStatus => control.GetStatus(),
                    ControlCommand.ReloadConfiguration => control.RequestReload(),
                    ControlCommand.RunNow => control.RequestRunNow(),
                    _ => new ControlResponse(
                        ProductConstants.ControlProtocolVersion,
                        false,
                        "UnknownCommand",
                        Error: "The requested command is not supported."),
                };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = new ControlResponse(
                ProductConstants.ControlProtocolVersion,
                false,
                "InvalidRequest",
                Error: exception.Message);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AddFullControl(PipeSecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
}

namespace DbBackup.RemoteSync.Service;

public interface IServiceControl
{
    ControlResponse GetStatus();
    ControlResponse RequestReload();
    ControlResponse RequestRunNow();
}

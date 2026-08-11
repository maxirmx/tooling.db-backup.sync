// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

namespace DbBackup.RemoteSync.Service;

public interface IServiceControl
{
    ControlResponse GetStatus();
    ControlResponse RequestReload();
    ControlResponse RequestReloadAndRunNow();
    ControlResponse RequestRunNow();
    ControlResponse RequestCancel();
}

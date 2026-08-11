<!--
Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
All rights reserved.
-->

# DB Backup Remote Sync

Windows service for downloading backup files that exist in a remote SFTP folder but do not yet exist in a local folder. Existing local files are never overwritten or deleted.

[Русская версия](#db-backup-remote-sync-1)

## Package contents

- `DbBackupRemoteSync`, a self-contained .NET 10 Windows service.
- An elevated WPF configuration and monitoring utility.
- English and Russian x64 MSI packages.

The application bundles its SSH/SFTP implementation and .NET runtime. Windows OpenSSH, PowerShell, and a separately installed .NET runtime are not required.

Supported systems are Windows 10 22H2, Windows 11, and Windows Server 2019 or newer on x64.

## Installation and configuration

1. Install the `en-US` or `ru-RU` MSI as an administrator. The unsigned package may produce a Windows SmartScreen warning; verify its published SHA-256 checksum before continuing.
2. Open **Configure DB Backup Remote Sync** from the Start menu and approve the UAC prompt.
3. Enter the remote host, SSH port, username, password, absolute remote folder, local destination, daily local time, and recursive-mode preference.
4. Select **Test and trust**. Check the displayed SHA-256 host-key fingerprint against a trusted source before accepting it. The key is stored only after authentication and remote-folder listing succeed.
5. Select **Save and apply**. The utility creates the destination, grants the service identity access, protects the password, and reloads the running service.

The installed service starts automatically with delayed start. Before configuration it remains idle and reports `MissingSettings`; it never prompts for input.

## Synchronization rules

- Only regular remote files are considered; symbolic links are ignored.
- Non-recursive mode processes direct children only. Recursive mode preserves relative subdirectories.
- Existing local files are skipped without size, timestamp, or content comparison.
- Every download uses a same-directory `.db-backup-download-*.partial` file followed by an atomic, non-overwriting move.
- A local directory that conflicts with a remote file, an unsafe Windows filename, or a case-insensitive path collision fails the run before downloading.
- A failed run stops at the first failed file. Completed downloads remain and are skipped when the service retries.
- Remote modification timestamps are applied to completed files.
- The product never uploads files and never deletes completed local files.

The service runs once per local calendar date. If it starts after the configured time without a completed run for that date, it catches up immediately. Failures are retried after 5, 15, and 30 minutes. Retry state survives service restarts.

**Run now** is serialized with scheduled work. A successful manual run after the daily due time satisfies that day's scheduled slot.

## Security

- Password authentication and keyboard-interactive password prompts are supported. SSH private keys are not supported in version 1.
- The password is encrypted with Windows DPAPI machine scope and additional application entropy. Its file is restricted to Administrators, SYSTEM, and `NT SERVICE\DbBackupRemoteSync`.
- The service refuses untrusted or changed SSH host keys. A changed key must be explicitly reviewed and replaced in the utility.
- The local control pipe is restricted to Administrators and SYSTEM.
- The destination must be a local directory with Windows ACL support. UNC paths and drive roots are rejected.
- Logs can contain host names, usernames, paths, and SSH diagnostic text, but the application sanitizes the configured password.

Operational data is stored in `C:\ProgramData\DB Backup Remote Sync`. A major upgrade preserves it. Full MSI uninstall permanently removes the configuration, encrypted credential, host trust, scheduler state, and the service-managed destination ACL. Downloaded files remain untouched.

## Status and troubleshooting

The utility shows configuration validity, active work, last result and counts, next scheduled attempt, and retry number. **Open Event Viewer** opens the Windows Application log; filter by source `DbBackupRemoteSync`.

Common checks:

```powershell
Get-Service DbBackupRemoteSync
Get-CimInstance Win32_Service -Filter "Name='DbBackupRemoteSync'" |
    Select-Object Name, State, StartMode, StartName
Test-NetConnection backup.example.com -Port 22
```

If the service reports a changed host key, verify the change with the server administrator. Do not replace the key merely to suppress the warning. If the destination is moved or recreated, save the configuration again so the utility can repair its service ACL.

## Development

Prerequisites are the .NET 10 SDK, PowerShell 7, and network access to restore the pinned NuGet and WiX 4.0.5 packages.

```powershell
./eng/build.ps1 -Version 0.1.0
```

The build restores in locked mode, compiles the service and utility, runs all tests, publishes self-contained single-file executables, builds both localized MSIs, and writes adjacent SHA-256 checksum files under `artifacts/package`.

Real-SFTP tests are opt-in. Define `SFTP_TEST_HOST`, `SFTP_TEST_USERNAME`, `SFTP_TEST_PASSWORD`, and `SFTP_TEST_FINGERPRINT`; optionally define `SFTP_TEST_PORT` and `SFTP_TEST_FOLDER`.

The installer smoke test performs real machine installation and removal and therefore requires an elevated disposable Windows environment:

```powershell
./eng/smoke-installer.ps1 -MsiPath ./artifacts/package/en-us/DB-Backup-Remote-Sync-0.1.0-en-US.msi
```

Tags in `vMAJOR.MINOR.PATCH` form build and publish the unsigned localized MSIs and checksums as GitHub Release assets.

---

# DB Backup Remote Sync

Служба Windows загружает файлы резервных копий, которые присутствуют в удалённом каталоге SFTP, но ещё отсутствуют в локальном каталоге. Существующие локальные файлы никогда не перезаписываются и не удаляются.

## Состав пакета

- Самодостаточная служба Windows `DbBackupRemoteSync` на .NET 10.
- Утилита настройки и контроля с интерфейсом WPF и запросом прав администратора.
- Пакеты MSI x64 на английском и русском языках.

Приложение содержит собственную реализацию SSH/SFTP и среду .NET. Windows OpenSSH, PowerShell и отдельная установка .NET не требуются.

Поддерживаются Windows 10 22H2, Windows 11 и Windows Server 2019 или новее для x64.

## Установка и настройка

1. Установите пакет MSI `ru-RU` или `en-US` от имени администратора. Пакет не подписан, поэтому Windows SmartScreen может вывести предупреждение; перед запуском проверьте опубликованную контрольную сумму SHA-256.
2. Откройте **Настройка DB Backup Remote Sync** в меню «Пуск» и подтвердите запрос UAC.
3. Укажите сервер, порт SSH, имя пользователя, пароль, абсолютный удалённый каталог, локальный каталог, ежедневное местное время и режим вложенных каталогов.
4. Нажмите **Проверить и доверять**. До подтверждения сверьте показанный отпечаток ключа сервера SHA-256 с доверенным источником. Ключ сохраняется только после успешной аутентификации и чтения удалённого каталога.
5. Нажмите **Сохранить и применить**. Утилита создаст каталог назначения, предоставит доступ учётной записи службы, защитит пароль и перезагрузит конфигурацию работающей службы.

Установленная служба запускается автоматически с отложенным запуском. До настройки она остаётся в режиме ожидания и сообщает `MissingSettings`; запросы ввода не выполняются.

## Правила синхронизации

- Обрабатываются только обычные удалённые файлы; символические ссылки игнорируются.
- Без рекурсии обрабатываются только непосредственные файлы каталога. С рекурсией сохраняется структура вложенных каталогов.
- Существующие локальные файлы пропускаются без сравнения размера, времени или содержимого.
- Загрузка выполняется во временный файл `.db-backup-download-*.partial` в каталоге назначения, после чего используется атомарное перемещение без перезаписи.
- Конфликт с локальным каталогом, недопустимое имя Windows или регистронезависимое совпадение путей прекращает запуск до начала загрузки.
- При первой ошибке загрузки запуск прекращается. Уже завершённые файлы сохраняются и будут пропущены при повторной попытке.
- Для завершённых файлов устанавливается время изменения удалённого файла.
- Программа никогда не отправляет файлы на сервер и не удаляет завершённые локальные файлы.

Служба запускается один раз за местную календарную дату. Если она запущена после заданного времени, а запуск за текущую дату не завершён, синхронизация начинается немедленно. После ошибки выполняются повторные попытки через 5, 15 и 30 минут. Состояние попыток сохраняется при перезапуске службы.

Команда **Запустить сейчас** не выполняется параллельно с другим запуском. Успешный ручной запуск после заданного времени закрывает плановый запуск текущего дня.

## Безопасность

- Поддерживается аутентификация паролем и интерактивный запрос пароля SSH. Ключи SSH в версии 1 не поддерживаются.
- Пароль шифруется DPAPI в области компьютера с дополнительной энтропией приложения. Доступ к файлу имеют только Администраторы, SYSTEM и `NT SERVICE\DbBackupRemoteSync`.
- Служба отклоняет неизвестные и изменившиеся ключи SSH. Изменившийся ключ необходимо отдельно проверить и подтвердить в утилите.
- Локальный канал управления доступен только Администраторам и SYSTEM.
- Каталог назначения должен быть локальным и поддерживать ACL Windows. Пути UNC и корни дисков запрещены.
- Журнал может содержать серверы, пользователей, пути и диагностику SSH, но настроенный пароль удаляется из сообщений.

Рабочие данные находятся в `C:\ProgramData\DB Backup Remote Sync`. Обновление версии сохраняет их. Полное удаление MSI безвозвратно удаляет конфигурацию, зашифрованный пароль, доверенный ключ, состояние расписания и созданное программой правило ACL каталога назначения. Загруженные файлы не удаляются.

## Состояние и диагностика

Утилита показывает действительность конфигурации, активную операцию, последний результат и счётчики, следующую попытку и номер повтора. Кнопка **Открыть просмотр событий** открывает журнал Windows «Приложение»; используйте фильтр по источнику `DbBackupRemoteSync`.

Основные проверки:

```powershell
Get-Service DbBackupRemoteSync
Get-CimInstance Win32_Service -Filter "Name='DbBackupRemoteSync'" |
    Select-Object Name, State, StartMode, StartName
Test-NetConnection backup.example.com -Port 22
```

Если ключ сервера изменился, сначала подтвердите изменение у администратора сервера. Не заменяйте ключ только ради устранения предупреждения. После переноса или повторного создания локального каталога снова сохраните конфигурацию, чтобы восстановить правило доступа службы.

## Разработка

Требуются SDK .NET 10, PowerShell 7 и доступ к сети для восстановления зафиксированных пакетов NuGet и WiX 4.0.5.

```powershell
./eng/build.ps1 -Version 0.1.0
```

Скрипт выполняет восстановление в заблокированном режиме, сборку, все тесты, публикацию самодостаточных одиночных EXE, создание двух локализованных MSI и файлов контрольных сумм SHA-256 в `artifacts/package`.

Тесты с настоящим SFTP включаются переменными `SFTP_TEST_HOST`, `SFTP_TEST_USERNAME`, `SFTP_TEST_PASSWORD` и `SFTP_TEST_FINGERPRINT`. Дополнительно можно задать `SFTP_TEST_PORT` и `SFTP_TEST_FOLDER`.

Тест MSI действительно устанавливает и удаляет продукт, поэтому его следует запускать с повышенными правами только в одноразовой среде Windows:

```powershell
./eng/smoke-installer.ps1 -MsiPath ./artifacts/package/ru-ru/DB-Backup-Remote-Sync-0.1.0-ru-RU.msi
```

Теги вида `vMAJOR.MINOR.PATCH` создают GitHub Release с неподписанными локализованными MSI и контрольными суммами.

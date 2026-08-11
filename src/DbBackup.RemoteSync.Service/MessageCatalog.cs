// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Globalization;

namespace DbBackup.RemoteSync.Service;

internal static class MessageCatalog
{
    public static string Get(string culture, string key, params object[] arguments)
    {
        var russian = string.Equals(culture, "ru", StringComparison.OrdinalIgnoreCase);
        var format = (russian, key) switch
        {
            (true, "ConfigurationInvalid") => "Конфигурация службы недействительна: {0}",
            (true, "RunStarted") => "Запущена синхронизация ({0}).",
            (true, "RunCompleted") => "Синхронизация завершена: найдено файлов {0}, загружено {1}, уже существовало {2}, " +
                "пропущено из-за гонки {3}, каталогов {4}, символических ссылок {5}, специальных объектов {6}.",
            (true, "RunFailed") => "Синхронизация завершилась ошибкой: {0}",
            (true, "RunCanceled") => "Синхронизация остановлена пользователем.",
            (_, "ConfigurationInvalid") => "The service configuration is invalid: {0}",
            (_, "RunStarted") => "Synchronization started ({0}).",
            (_, "RunCompleted") => "Synchronization completed: eligible files {0}, downloaded {1}, already present {2}, " +
                "race-skipped {3}, skipped directories {4}, symbolic links {5}, special entries {6}.",
            (_, "RunFailed") => "Synchronization failed: {0}",
            (_, "RunCanceled") => "Synchronization was stopped by the user.",
            _ => key,
        };
        return string.Format(CultureInfo.GetCultureInfo(russian ? "ru-RU" : "en-US"), format, arguments);
    }

    public static string DescribeError(string culture, string error)
    {
        var russian = string.Equals(culture, "ru", StringComparison.OrdinalIgnoreCase);
        return (russian, error) switch
        {
            (true, "MissingSettings") => "настройки ещё не сохранены",
            (true, "MissingCredential") => "пароль ещё не сохранён",
            (true, "MissingOrMismatchedHostTrust") => "ключ сервера не подтверждён или не соответствует серверу",
            (_, "MissingSettings") => "settings have not been saved",
            (_, "MissingCredential") => "the password has not been saved",
            (_, "MissingOrMismatchedHostTrust") => "the host key is untrusted or belongs to another endpoint",
            _ => error,
        };
    }

    public static string DescribeReason(string culture, string reason) =>
        (string.Equals(culture, "ru", StringComparison.OrdinalIgnoreCase), reason) switch
        {
            (true, "manual") => "вручную",
            (true, "scheduled") => "по расписанию",
            _ => reason,
        };
}

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
            (true, "RunCompleted") => "Синхронизация завершена: загружено {0}, уже существовало {1}, пропущено из-за гонки {2}.",
            (true, "RunFailed") => "Синхронизация завершилась ошибкой: {0}",
            (_, "ConfigurationInvalid") => "The service configuration is invalid: {0}",
            (_, "RunStarted") => "Synchronization started ({0}).",
            (_, "RunCompleted") => "Synchronization completed: downloaded {0}, already present {1}, race-skipped {2}.",
            (_, "RunFailed") => "Synchronization failed: {0}",
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

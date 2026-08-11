// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.

using System.Windows;

namespace DbBackup.RemoteSync.Configuration;

internal static class Localization
{
    public static void Apply(Application application, string culture)
    {
        var selected = culture == "ru" ? "ru" : "en";
        application.Resources.MergedDictionaries.Clear();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{selected}.xaml", UriKind.Relative),
        });
    }
}

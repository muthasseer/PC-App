using System.Windows;

namespace Weighbridge.Desktop.Infrastructure;

/// <summary>User-interface language (English, Sinhala, Tamil) and theme switching at run time.</summary>
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages = [("en", "English"), ("si", "සිංහල (Sinhala)"), ("ta", "தமிழ் (Tamil)")];

    public static string Current { get; private set; } = "en";

    public static event EventHandler? Changed;

    public static void Apply(string? language)
    {
        var code = Languages.Any(l => l.Code == language) ? language! : "en";
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        // English is always loaded first so that a key missing in a translation falls back to English.
        var english = new ResourceDictionary { Source = new Uri("pack://application:,,,/AdvancedWeighbridge;component/Resources/Strings/en.xaml") };
        while (dictionaries.Count > 2) dictionaries.RemoveAt(dictionaries.Count - 1);
        dictionaries.Add(english);
        if (code != "en") dictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/AdvancedWeighbridge;component/Resources/Strings/{code}.xaml") });
        Current = code;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The text for a key in the current language, or the fallback (or the key) when it does not exist.</summary>
    public static string T(string key, string? fallback = null) =>
        System.Windows.Application.Current?.TryFindResource("s." + key) as string ?? fallback ?? key;
}

public static class ThemeManager
{
    public static string Current { get; private set; } = "Light";

    public static void Apply(string? theme)
    {
        var name = theme switch
        {
            "Dark" => "Dark",
            "System" => SystemUsesDarkTheme() ? "Dark" : "Light",
            _ => "Light",
        };
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        dictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/AdvancedWeighbridge;component/Resources/Themes/{name}.xaml") };
        Current = name;
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

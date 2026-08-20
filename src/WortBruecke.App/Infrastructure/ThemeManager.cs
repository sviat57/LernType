using System.Windows;

namespace WortBruecke.App.Infrastructure;

public static class ThemeManager
{
    public static bool IsDarkTheme { get; private set; }

    public static void Apply(bool dark)
    {
        IsDarkTheme = dark;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var current = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var replacement = new ResourceDictionary
        {
            Source = new Uri(dark ? "Resources/DarkTheme.xaml" : "Resources/LightTheme.xaml", UriKind.Relative)
        };
        if (current is null)
        {
            dictionaries.Insert(Math.Min(1, dictionaries.Count), replacement);
            ApplyWindowBackdrop(dark);
            return;
        }
        var index = dictionaries.IndexOf(current);
        dictionaries[index] = replacement;
        ApplyWindowBackdrop(dark);
    }

    private static void ApplyWindowBackdrop(bool dark)
    {
        if (Application.Current.MainWindow is Window window)
        {
            WindowBackdropService.Apply(window, dark);
        }
    }
}

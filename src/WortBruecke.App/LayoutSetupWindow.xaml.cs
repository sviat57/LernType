using System.Diagnostics;
using System.Windows;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.App;

public partial class LayoutSetupWindow : Window
{
    private readonly IKeyboardLayoutService _layoutService;
    private readonly LanguagePair _pair;

    public LayoutSetupWindow(IKeyboardLayoutService layoutService, LanguagePair pair)
    {
        InitializeComponent();
        _layoutService = layoutService;
        _pair = pair;
        RefreshMissing();
    }

    private void OpenSettings(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });

    private void Retry(object sender, RoutedEventArgs e)
    {
        if (RefreshMissing())
        {
            DialogResult = true;
            Close();
            return;
        }
        RetryMessage.Text = "Раскладки ещё не обнаружены. Добавьте их в Windows и повторите проверку.";
    }

    private bool RefreshMissing()
    {
        var missing = _layoutService
            .CheckInstalled(_pair.Source.CultureCode, _pair.Target.CultureCode)
            .Where(item => !item.IsInstalled)
            .Select(item => new { DisplayName = item.CultureCode == _pair.Source.CultureCode ? "Русская (ru-RU)" : "Немецкая (de-DE)" })
            .ToList();
        LayoutList.ItemsSource = missing;
        return missing.Count == 0;
    }
}

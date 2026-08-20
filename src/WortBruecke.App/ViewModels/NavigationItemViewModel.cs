using System.Windows.Input;
using System.Windows.Media;

namespace WortBruecke.App.ViewModels;

public sealed class NavigationItemViewModel(string key, string title, Geometry icon, ICommand command) : Infrastructure.ObservableObject
{
    private bool _isSelected;

    public string Key { get; } = key;
    public string Title { get; } = title;
    public Geometry Icon { get; } = icon;
    public ICommand Command { get; } = command;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

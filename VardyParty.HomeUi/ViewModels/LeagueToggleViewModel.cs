using System.ComponentModel;

namespace VardyParty.HomeUi;

/// <summary>One hide/show league entry in the menu.</summary>
public sealed class LeagueToggleViewModel : INotifyPropertyChanged
{
    private readonly Action<string, bool> _apply;
    private bool _isVisible;

    public LeagueToggleViewModel(string name, bool isVisible, Action<string, bool> apply)
    {
        Name = name;
        _isVisible = isVisible;
        _apply = apply;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
            _apply(Name, value);
        }
    }

    /// <summary>Sync from the filter service without re-applying.</summary>
    public void Refresh(bool isVisible)
    {
        if (_isVisible == isVisible) return;
        _isVisible = isVisible;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
    }
}

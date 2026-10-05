using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoAlbumManager.ViewModels;

public partial class SelectableEntity : ObservableObject
{
    public int Id { get; init; }

    public required string Name { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    public event EventHandler? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
}

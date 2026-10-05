using CommunityToolkit.Mvvm.ComponentModel;
using PhotoAlbumManager.Core.Albums;

namespace PhotoAlbumManager.ViewModels;

public partial class AlbumRowViewModel : ObservableObject
{
    public required int Id { get; init; }

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private bool _isReadable = true;

    [ObservableProperty]
    private string? _error;

    public string Title { get; private set; } = "";

    public DateTime? From { get; private set; }

    public IReadOnlyList<string> Tags { get; private set; } = [];

    public void Refresh(AlbumDocument album)
    {
        Title = album.Title ?? "";
        From = AlbumDateText.TryParse(album.Date?.From);
        Tags = album.Tags?.ToArray() ?? [];
        DisplayName = AlbumDisplay.FormatName(album);
        var count = album.ItemsLoaded || album.Items.Count > 0
            ? album.Items.Count
            : album.ListedItemCount;
        Detail = count == 1 ? "1 item" : $"{count} items";
        IsReadable = true;
        Error = null;
    }

    public AlbumSummary ToSummary() => new(Title, From, Tags);
}

using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhotoAlbumManager.Core.Albums;
using PhotoAlbumManager.Core.Media;
using PhotoAlbumManager.Core.Storage;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace PhotoAlbumManager.ViewModels;

public partial class MediaItemViewModel : ObservableObject
{
    private static readonly SemaphoreSlim ThumbnailGate = new(1, 1);

    private readonly MediaPathResolver _media;
    private readonly DispatcherQueue? _ui;

    public MediaItemViewModel(AlbumItem model, MediaPathResolver media, DispatcherQueue? ui)
    {
        Model = model;
        _media = media;
        _ui = ui;
        Title = model.Title;
        Description = model.Description;
        DateTaken = model.DateTaken;
        Location = model.Location;
        Owner = model.Owner;
        FilePath = model.FilePath;
        TagsText = TagList.Format(model.Tags);
        PeopleText = TagList.Format(model.People);
        RefreshPathState();
    }

    public AlbumItem Model { get; }

    public int Id => Model.Id;

    public int Order => Model.Order;

    public string OrderLabel => Order.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsVideo => MediaKinds.IsVideo(Model.FilePath);

    public bool ThumbnailRequested { get; set; }

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private string _dateTaken = "";

    [ObservableProperty]
    private string _location = "";

    [ObservableProperty]
    private string _owner = "";

    [ObservableProperty]
    private string _filePath = "";

    [ObservableProperty]
    private string _tagsText = "";

    [ObservableProperty]
    private string _peopleText = "";

    [ObservableProperty]
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private bool _isMissing;

    [ObservableProperty]
    private string _resolvedPath = "";

    public Visibility VideoVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MissingVisibility => IsMissing ? Visibility.Visible : Visibility.Collapsed;

    public bool IsCover { get; private set; }

    public Visibility CoverVisibility => IsCover ? Visibility.Visible : Visibility.Collapsed;

    public void SetCover(bool isCover)
    {
        if (IsCover == isCover)
            return;

        IsCover = isCover;
        OnPropertyChanged(nameof(IsCover));
        OnPropertyChanged(nameof(CoverVisibility));
    }

    public void RefreshOrder()
    {
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(OrderLabel));
    }

    public void Apply(
        string title,
        string description,
        string dateTaken,
        string location,
        string owner,
        string filePath,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> people)
    {
        Title = title;
        Description = description;
        DateTaken = dateTaken;
        Location = location;
        Owner = owner;
        FilePath = filePath.Trim();
        Model.Title = Title;
        Model.Description = Description;
        Model.DateTaken = DateTaken.Trim();
        Model.Location = Location;
        Model.Owner = Owner;
        Model.FilePath = FilePath;
        Model.Tags = tags.ToList();
        Model.People = people.ToList();
        TagsText = TagList.Format(Model.Tags);
        PeopleText = TagList.Format(Model.People);
        RefreshPathState();
    }

    public async Task LoadThumbnailAsync(CancellationToken cancellationToken)
    {
        if (Thumbnail is not null || IsMissing || _ui is null)
            return;

        await ThumbnailGate.WaitAsync(cancellationToken);
        try
        {
            if (Thumbnail is not null || cancellationToken.IsCancellationRequested)
                return;

            var absolute = _media.ToAbsolute(Model.FilePath);
            if (!File.Exists(absolute))
            {
                IsMissing = true;
                OnPropertyChanged(nameof(MissingVisibility));
                return;
            }

            if (IsVideo)
                await LoadVideoThumbnailAsync(absolute, cancellationToken);
            else
                await LoadImageThumbnailAsync(absolute, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            Thumbnail ??= null;
        }
        finally
        {
            ThumbnailGate.Release();
        }
    }

    private async Task LoadImageThumbnailAsync(string absolute, CancellationToken cancellationToken)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource<BitmapImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui!.TryEnqueue(() =>
            {
                try
                {
                    var image = new BitmapImage { DecodePixelWidth = 240 };
                    image.ImageOpened += (_, _) => opened.TrySetResult();
                    image.ImageFailed += (_, _) => opened.TrySetResult();
                    image.UriSource = new Uri(absolute);
                    Thumbnail = image;
                    ready.TrySetResult(image);
                }
                catch (Exception ex)
                {
                    ready.TrySetException(ex);
                    opened.TrySetResult();
                }
            }))
        {
            return;
        }

        Thumbnail = await ready.Task;
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
    }

    private async Task LoadVideoThumbnailAsync(string absolute, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(absolute).AsTask(cancellationToken);
        using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 240)
            .AsTask(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var copy = new InMemoryRandomAccessStream();
        await RandomAccessStream.CopyAsync(thumb, copy).AsTask(cancellationToken);
        copy.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(copy).AsTask(cancellationToken);
        Thumbnail = bitmap;
    }

    private void RefreshPathState()
    {
        try
        {
            ResolvedPath = _media.ToAbsolute(Model.FilePath);
            IsMissing = !File.Exists(ResolvedPath);
        }
        catch
        {
            ResolvedPath = "";
            IsMissing = true;
        }

        OnPropertyChanged(nameof(VideoVisibility));
        OnPropertyChanged(nameof(MissingVisibility));
        OnPropertyChanged(nameof(IsVideo));
    }
}

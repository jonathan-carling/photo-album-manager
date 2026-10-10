using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PhotoAlbumManager.Core.Albums;
using PhotoAlbumManager.Core.Media;
using PhotoAlbumManager.Core.Storage;
using PhotoAlbumManager.Services;

namespace PhotoAlbumManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IUserInteraction _ui;
    private readonly FilterOption _allYear = new("All", null);
    private readonly FilterOption _allMonth = new("All", null);
    private readonly FilterOption _allDay = new("All", null);

    private SqliteAlbumStore? _store;
    private IReadOnlyList<CatalogEntry> _tagCatalog = [];
    private IReadOnlyList<CatalogEntry> _peopleCatalog = [];
    private IReadOnlyList<CatalogEntry> _locationCatalog = [];
    private IReadOnlyList<CatalogEntry> _ownerCatalog = [];
    private bool _choicesReady;
    private MediaPathResolver? _media;
    private AppPaths? _paths;
    private string? _settingsPath;
    private AlbumDocument? _document;
    private string? _baseline;
    private bool _suspend;
    private bool _filterBusy;
    private bool _filterAgain;
    private List<AlbumRowViewModel> _allAlbums = [];
    private int? _committedYear;
    private int? _committedMonth;
    private int? _committedDay;
    private string _committedSearch = "";
    private string _committedTags = "";
    private int _loadGeneration;
    private CancellationTokenSource? _loadCts;

    public MainViewModel(IUserInteraction ui)
    {
        _ui = ui;
        MonthOptions = BuildMonthOptions();
        DayOptions = BuildDayOptions();
        YearOptions.Add(_allYear);
        SelectedYear = _allYear;
        SelectedMonth = _allMonth;
        SelectedDay = _allDay;
    }

    public Func<Task>? CloseRequested { get; set; }

    public bool IsRebinding { get; private set; }

    public event EventHandler? AlbumsChanged;

    public ObservableCollection<AlbumRowViewModel> FilteredAlbums { get; } = [];

    public ObservableCollection<MediaItemViewModel> Items { get; } = [];

    public ObservableCollection<SelectableEntity> FilterTagChoices { get; } = [];

    public ObservableCollection<SelectableEntity> AlbumTagChoices { get; } = [];

    public ObservableCollection<SelectableEntity> ItemTagChoices { get; } = [];

    public ObservableCollection<SelectableEntity> ItemPeopleChoices { get; } = [];

    public ObservableCollection<CatalogEntry> LocationChoices { get; } = [];

    public ObservableCollection<CatalogEntry> OwnerChoices { get; } = [];

    public ObservableCollection<FilterOption> YearOptions { get; } = [];

    public IReadOnlyList<FilterOption> MonthOptions { get; }

    public IReadOnlyList<FilterOption> DayOptions { get; }

    [ObservableProperty]
    private AlbumRowViewModel? _selectedRow;

    [ObservableProperty]
    private MediaItemViewModel? _selectedItem;

    [ObservableProperty]
    private FilterOption? _selectedYear;

    [ObservableProperty]
    private FilterOption? _selectedMonth;

    [ObservableProperty]
    private FilterOption? _selectedDay;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _tagFilterText = "";

    [ObservableProperty]
    private string _albumTitle = "";

    [ObservableProperty]
    private string _albumDescription = "";

    [ObservableProperty]
    private string _albumDateFromText = "";

    [ObservableProperty]
    private string _albumDateToText = "";

    [ObservableProperty]
    private CatalogEntry? _selectedAlbumLocation;

    [ObservableProperty]
    private CatalogEntry? _selectedAlbumOwner;

    [ObservableProperty]
    private string _coverText = "None";

    [ObservableProperty]
    private string _albumTagSummary = "None";

    [ObservableProperty]
    private string _filterTagSummary = "None";

    [ObservableProperty]
    private string _itemTitle = "";

    [ObservableProperty]
    private string _itemDescription = "";

    [ObservableProperty]
    private string _itemDateTakenText = "";

    [ObservableProperty]
    private CatalogEntry? _selectedItemLocation;

    [ObservableProperty]
    private CatalogEntry? _selectedItemOwner;

    [ObservableProperty]
    private string _itemFilePath = "";

    [ObservableProperty]
    private string _itemTagSummary = "None";

    [ObservableProperty]
    private string _itemPeopleSummary = "None";

    [ObservableProperty]
    private string _itemOrderText = "";

    [ObservableProperty]
    private string _itemIdText = "";

    [ObservableProperty]
    private string _itemResolvedPath = "";

    [ObservableProperty]
    private string _itemMissingText = "";

    [ObservableProperty]
    private string _albumHeading = "No album selected";

    [ObservableProperty]
    private string _itemCountLabel = "";

    [ObservableProperty]
    private string _dirtyLabel = "";

    [ObservableProperty]
    private string _statusText = "Loading albums...";

    [ObservableProperty]
    private string _albumDirectoryText = "";

    [ObservableProperty]
    private string _mediaRootText = "";

    [ObservableProperty]
    private string _validationMessage = "";

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isLoadingItems;

    public Visibility AlbumEditorVisibility => _document is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ItemEditorVisibility => SelectedItem is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ItemPlaceholderVisibility =>
        _document is not null && SelectedItem is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyAlbumVisibility => _document is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyItemsVisibility =>
        _document is not null && !IsLoadingItems && Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LoadingVisibility => IsLoadingItems ? Visibility.Visible : Visibility.Collapsed;

    public bool AllowItemReorder => _document is not null && !IsLoadingItems;

    public Visibility NoAlbumsVisibility => FilteredAlbums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ValidationVisibility =>
        string.IsNullOrEmpty(ValidationMessage) ? Visibility.Collapsed : Visibility.Visible;

    public async Task InitializeAsync(string settingsPath)
    {
        _settingsPath = settingsPath;
        try
        {
            var paths = AppSettingsStore.Load(settingsPath);
            ApplyConfiguration(paths);
            await ReloadAlbumsAsync(selectId: null, selectFirst: true);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            await _ui.ShowMessageAsync("Photo Album Manager", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateAlbum))]
    private async Task NewAlbumAsync()
    {
        if (_store is null)
            return;

        if (!await EnsureCanLeaveAsync(restoreOnDiscard: false))
            return;

        ResetFilters();
        var today = DateTime.Today;
        var album = AlbumEditor.CreateNew(today, "New Album");
        album.ItemsLoaded = true;
        _store.Save(album);
        await ReloadAlbumsAsync(album.Id, selectFirst: false);
        StatusText = "Created " + AlbumDisplay.FormatName(album);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await SaveCoreAsync();

    [RelayCommand(CanExecute = nameof(CanDeleteAlbum))]
    private async Task DeleteAlbumAsync()
    {
        if (_store is null || SelectedRow is null)
            return;

        var confirmed = await _ui.ConfirmAsync(
            "Delete album",
            $"Delete the album \"{SelectedRow.DisplayName}\"? Photos and videos are not deleted.",
            "Delete");
        if (!confirmed)
            return;

        _store.Delete(SelectedRow.Id);
        IsDirty = false;
        _document = null;
        _baseline = null;
        await ReloadAlbumsAsync(selectId: null, selectFirst: true);
        StatusText = "Album deleted. Media files were left in place.";
    }

    [RelayCommand(CanExecute = nameof(CanAddMedia))]
    private async Task AddMediaAsync()
    {
        if (_document is null || _media is null)
            return;

        PullEditorIntoDocument();
        var picked = await _ui.PickMediaFilesAsync();
        if (picked is null || picked.Count == 0)
            return;

        var messages = new List<string>();
        var added = 0;
        foreach (var path in picked)
        {
            if (!MediaKinds.IsSupported(path))
            {
                messages.Add($"{Path.GetFileName(path)} is not a supported photo or video.");
                continue;
            }

            string relative;
            try
            {
                relative = _media.ToRelative(path);
            }
            catch (InvalidOperationException ex)
            {
                messages.Add(ex.Message);
                continue;
            }

            if (_document.Items.Any(item => string.Equals(item.FilePath, relative, StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add($"{relative} is already in this album.");
                continue;
            }

            var item = AlbumEditor.AddMedia(
                _document,
                relative,
                dateTaken: MediaTimestamp.FromFileName(path) ?? "",
                location: _document.Location,
                owner: _document.Owner);
            var viewModel = CreateItem(item, DispatcherQueue.GetForCurrentThread());
            Items.Add(viewModel);
            _ = viewModel.LoadThumbnailAsync(CancellationToken.None);
            added++;
        }

        if (added > 0)
            MarkEdited();

        if (messages.Count > 0)
        {
            await _ui.ShowMessageAsync(
                added == 0 ? "No files added" : "Some files were not added",
                string.Join(Environment.NewLine + Environment.NewLine, messages));
        }
        else if (added > 0)
        {
            StatusText = added == 1 ? "Added 1 item. Save the album to keep it." : $"Added {added} items. Save the album to keep them.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanSetCover))]
    private void SetCover()
    {
        if (_document is null || SelectedItem is null)
            return;

        _document.CoverItemId = SelectedItem.Id;
        RefreshCover();
        MarkEdited();
        StatusText = $"Cover set to {SelectedItem.Title}.";
    }

    [RelayCommand(CanExecute = nameof(CanRemoveItem))]
    private async Task RemoveItemAsync()
    {
        if (_document is null || SelectedItem is null)
            return;

        var removed = SelectedItem;
        if (_document.CoverItemId == removed.Id)
        {
            var confirmed = await _ui.ConfirmAsync(
                "Remove cover",
                $"\"{removed.Title}\" is the cover for this album. Remove it from the album?",
                "Remove");
            if (!confirmed)
                return;
        }

        AlbumEditor.Remove(_document, removed.Id);
        IsRebinding = true;
        try
        {
            Items.Remove(removed);
        }
        finally
        {
            IsRebinding = false;
        }

        foreach (var item in Items)
            item.RefreshOrder();
        RefreshCover();
        SelectItem(null);
        MarkEdited();
        StatusText = "Item removed from the album. The media file was not deleted.";
    }

    [RelayCommand(CanExecute = nameof(CanUseFileDateTaken))]
    private async Task UseFileDateTakenAsync()
    {
        if (SelectedItem is null || _media is null)
            return;

        PullEditorIntoDocument();
        string absolute;
        try
        {
            absolute = _media.ToAbsolute(SelectedItem.Model.FilePath);
        }
        catch (Exception ex)
        {
            await _ui.ShowMessageAsync("Date taken", ex.Message);
            return;
        }

        if (!File.Exists(absolute))
        {
            await _ui.ShowMessageAsync("Date taken", "The file was not found in the media root.");
            return;
        }

        DateTime? taken;
        try
        {
            taken = await FileDateTaken.ReadAsync(absolute);
        }
        catch (Exception ex)
        {
            await _ui.ShowMessageAsync("Date taken", ex.Message);
            return;
        }

        if (taken is null)
        {
            await _ui.ShowMessageAsync("Date taken", "Windows has no Date taken value for this file.");
            return;
        }

        ItemDateTakenText = AlbumDateText.FormatValue(taken.Value);
        StatusText = "Date taken set from the file.";
    }

    [RelayCommand(CanExecute = nameof(CanApplyToItems))]
    private async Task ApplyDateFromAsync()
    {
        if (_document is null)
            return;

        PullEditorIntoDocument();
        var value = (_document.Date?.From ?? "").Trim();
        if (!AlbumDateText.IsValidOrEmpty(value))
        {
            await _ui.ShowMessageAsync("Apply to all", "Album date from must use yyyy-MM-dd HH:mm:ss.");
            return;
        }

        if (!await ConfirmApplyAsync("Date taken", "Date from"))
            return;

        foreach (var item in _document.Items)
            item.DateTaken = value;

        SyncAppliedItemFields();
        MarkEdited();
        StatusText = "Date taken updated on every item. Save the album to keep it.";
    }

    [RelayCommand(CanExecute = nameof(CanApplyToItems))]
    private Task ApplyLocationAsync() =>
        ApplyTextToItemsAsync("Location", (item, value) => item.Location = value, () => _document!.Location ?? "");

    [RelayCommand(CanExecute = nameof(CanApplyToItems))]
    private Task ApplyOwnerAsync() =>
        ApplyTextToItemsAsync("Owner", (item, value) => item.Owner = value, () => _document!.Owner ?? "");

    [RelayCommand(CanExecute = nameof(CanApplyToItems))]
    private async Task ApplyTagsAsync()
    {
        if (_document is null)
            return;

        PullEditorIntoDocument();
        if (!await ConfirmApplyAsync("tags", "tags"))
            return;

        var tags = _document.Tags.ToList();
        foreach (var item in _document.Items)
            item.Tags = tags.ToList();

        SyncAppliedItemFields();
        MarkEdited();
        StatusText = "Tags updated on every item. Save the album to keep it.";
    }

    private async Task ApplyTextToItemsAsync(
        string propertyName,
        Action<AlbumItem, string> assign,
        Func<string> albumValue)
    {
        if (_document is null)
            return;

        PullEditorIntoDocument();
        if (!await ConfirmApplyAsync(propertyName, propertyName))
            return;

        var value = albumValue();
        foreach (var item in _document.Items)
            assign(item, value);

        SyncAppliedItemFields();
        MarkEdited();
        StatusText = $"{propertyName} updated on every item. Save the album to keep it.";
    }

    private Task<bool> ConfirmApplyAsync(string itemProperty, string albumProperty) =>
        _ui.ConfirmAsync(
            "Apply to all",
            $"This replaces {itemProperty} on every item in this album with the album {albumProperty}.",
            "Continue");

    private void SyncAppliedItemFields()
    {
        foreach (var item in Items)
        {
            item.DateTaken = item.Model.DateTaken;
            item.Location = item.Model.Location;
            item.Owner = item.Model.Owner;
            item.TagsText = TagList.Format(item.Model.Tags);
        }

        if (SelectedItem is null)
            return;

        _suspend = true;
        try
        {
            ItemDateTakenText = SelectedItem.Model.DateTaken;
            SelectedItemLocation = MatchChoice(LocationChoices, SelectedItem.Model.Location);
            SelectedItemOwner = MatchChoice(OwnerChoices, SelectedItem.Model.Owner);
            RebuildChoices(ItemTagChoices, _tagCatalog, SelectedItem.Model.Tags);
            ItemTagSummary = FormatSummary(ItemTagChoices);
        }
        finally
        {
            _suspend = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (!await EnsureCanLeaveAsync(restoreOnDiscard: false))
            return;

        var albumId = SelectedRow?.Id;
        await ReloadAlbumsAsync(albumId, selectFirst: false);
        StatusText = $"{FilteredAlbums.Count} of {_allAlbums.Count} albums";
    }

    [RelayCommand]
    private async Task SettingsAsync()
    {
        if (_paths is null || _settingsPath is null)
            return;

        if (!await EnsureCanLeaveAsync(restoreOnDiscard: false))
            return;

        var result = await _ui.EditSettingsAsync(_paths.AlbumMetadataDirectory, _paths.MediaRootDirectory);
        if (!result.Accepted)
            return;

        try
        {
            var albumDirectory = PathSafety.FullDirectory(result.AlbumDirectory);
            var mediaRoot = PathSafety.FullDirectory(result.MediaRoot);
            if (!Directory.Exists(mediaRoot))
                throw new InvalidOperationException("The media root folder does not exist.");

            SqliteAlbumStore.ValidateLocations(albumDirectory, mediaRoot);
            AppSettingsStore.Save(_settingsPath, new AppPaths
            {
                AlbumMetadataDirectory = albumDirectory,
                MediaRootDirectory = mediaRoot
            });
            ApplyConfiguration(AppSettingsStore.Load(_settingsPath));
            await ReloadAlbumsAsync(selectId: null, selectFirst: true);
            StatusText = "Folder settings saved.";
        }
        catch (Exception ex)
        {
            await _ui.ShowMessageAsync("Folders", ex.Message);
        }
    }

    [RelayCommand]
    private Task AboutAsync() =>
        _ui.ShowAboutAsync(AlbumDirectoryText, MediaRootText);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task ManageTagsAsync() => ManageCatalogAsync("Tags", _store!.Tags, CatalogSubject.Tags);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task ManagePeopleAsync() => ManageCatalogAsync("People", _store!.People, CatalogSubject.People);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task ManageLocationsAsync() => ManageCatalogAsync("Locations", _store!.Locations, CatalogSubject.Locations);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task ManageOwnersAsync() => ManageCatalogAsync("Owners", _store!.Owners, CatalogSubject.Owners);

    [RelayCommand]
    private async Task ExitAsync()
    {
        if (!await EnsureCanLeaveAsync(restoreOnDiscard: false))
            return;

        if (CloseRequested is not null)
            await CloseRequested();
    }

    public async Task<bool> TrySelectAlbumAsync(AlbumRowViewModel? incoming)
    {
        if (ReferenceEquals(incoming, SelectedRow))
            return true;

        var albumId = incoming?.Id;
        if (!await EnsureCanLeaveAsync(restoreOnDiscard: false))
            return false;

        AlbumRowViewModel? resolved = null;
        if (albumId is int id)
        {
            resolved = FilteredAlbums.FirstOrDefault(row => row.Id == id)
                ?? _allAlbums.FirstOrDefault(row => row.Id == id);
        }

        await OpenAlbumAsync(resolved);
        return true;
    }

    public async Task<bool> TrySelectItemAsync(MediaItemViewModel? incoming)
    {
        if (ReferenceEquals(incoming, SelectedItem))
            return true;

        if (!await EnsureCanLeaveAsync(restoreOnDiscard: true))
            return false;

        MediaItemViewModel? resolved = null;
        if (incoming is not null)
        {
            resolved = Items.Contains(incoming)
                ? incoming
                : Items.FirstOrDefault(item => item.Id == incoming.Id);
        }

        SelectItem(resolved);
        return true;
    }

    public Task<bool> ConfirmCloseAsync() => EnsureCanLeaveAsync(restoreOnDiscard: false);

    public void SyncOrderFromView()
    {
        if (_document is null)
            return;

        _document.Items = Items.Select(item => item.Model).ToList();
        AlbumEditor.Renumber(_document);
        foreach (var item in Items)
            item.RefreshOrder();

        if (SelectedItem is not null)
            ItemOrderText = SelectedItem.OrderLabel;

        MarkEdited();
    }

    private async Task<bool> EnsureCanLeaveAsync(bool restoreOnDiscard)
    {
        MarkEdited();
        if (!IsDirty)
            return true;

        var choice = await _ui.PromptUnsavedAsync();
        switch (choice)
        {
            case UnsavedChoice.Stay:
                return false;
            case UnsavedChoice.Discard:
                if (restoreOnDiscard)
                    await RestoreBaselineAsync();
                else
                    DiscardWithoutSaving();
                return true;
            case UnsavedChoice.Save:
                return await SaveCoreAsync();
            default:
                return false;
        }
    }

    private async Task<bool> SaveCoreAsync()
    {
        if (_document is null || _store is null)
            return false;

        PullEditorIntoDocument();
        var validation = ValidateDocument();
        ValidationMessage = validation ?? "";
        if (validation is not null)
        {
            await _ui.ShowMessageAsync("Can't save yet", validation);
            RefreshCommandStates();
            return false;
        }

        try
        {
            _store.Save(_document);
            _baseline = AlbumJson.Serialize(_document);
            IsDirty = false;
            SelectedRow?.Refresh(_document);
            if (SelectedItem is not null)
                ItemIdText = SelectedItem.Id.ToString(CultureInfo.InvariantCulture);
            StatusText = "Saved " + AlbumDisplay.FormatName(_document);
            NotifyChrome();
            RefreshCommandStates();
            return true;
        }
        catch (Exception ex)
        {
            await _ui.ShowMessageAsync("Save failed", ex.Message);
            return false;
        }
    }

    private void ApplyConfiguration(AppPaths paths)
    {
        _store?.Dispose();
        _store = new SqliteAlbumStore(paths.AlbumMetadataDirectory, paths.MediaRootDirectory);
        ReloadCatalog();
        _media = new MediaPathResolver(paths.MediaRootDirectory);
        _paths = paths;
        AlbumDirectoryText = paths.AlbumMetadataDirectory;
        MediaRootText = paths.MediaRootDirectory;
        RefreshCommandStates();
    }

    private async Task ManageCatalogAsync(string title, INamedCatalog catalog, CatalogSubject subject)
    {
        var changes = await _ui.EditCatalogAsync(title, catalog);
        if (changes.Count == 0)
            return;

        ApplyCatalogChanges(changes, subject);
    }

    private void ApplyCatalogChanges(IReadOnlyList<CatalogChange> changes, CatalogSubject subject)
    {
        AlbumDocument? baseline = null;
        if (_baseline is not null && _document is not null)
        {
            baseline = AlbumJson.Deserialize(_baseline);
            baseline.Id = _document.Id;
            baseline.SourcePath = _document.SourcePath;
            baseline.ItemsLoaded = true;
        }

        foreach (var change in changes)
        {
            if (_document is not null)
                RewriteCatalog(_document, change, subject);
            if (baseline is not null)
                RewriteCatalog(baseline, change, subject);
        }

        if (baseline is not null)
            _baseline = AlbumJson.Serialize(baseline);

        ReloadCatalog();
        if (_document is not null)
        {
            _suspend = true;
            try
            {
                RebuildChoices(AlbumTagChoices, _tagCatalog, _document.Tags);
                AlbumTagSummary = FormatSummary(AlbumTagChoices);
                RebuildChoices(ItemTagChoices, SelectedItem is null ? [] : _tagCatalog, SelectedItem?.Model.Tags ?? []);
                RebuildChoices(ItemPeopleChoices, SelectedItem is null ? [] : _peopleCatalog, SelectedItem?.Model.People ?? []);
                ItemTagSummary = FormatSummary(ItemTagChoices);
                ItemPeopleSummary = FormatSummary(ItemPeopleChoices);
                foreach (var item in Items)
                {
                    item.TagsText = TagList.Format(item.Model.Tags);
                    item.PeopleText = TagList.Format(item.Model.People);
                    item.Location = item.Model.Location;
                    item.Owner = item.Model.Owner;
                }
                SyncSingleChoices();
            }
            finally
            {
                _suspend = false;
            }
        }

        RefreshListedAlbums();
        MarkEdited();
    }

    private void RefreshListedAlbums()
    {
        if (_store is null)
            return;

        var headers = _store.ListAlbums().ToDictionary(album => album.Id);
        foreach (var row in _allAlbums)
        {
            if (_document is not null && row.Id == _document.Id)
                row.Refresh(_document);
            else if (headers.TryGetValue(row.Id, out var header))
                row.Refresh(header);
        }

        RebuildYears();
        RebuildFilteredList();
    }

    private void ReloadCatalog()
    {
        if (_store is null)
            return;

        _tagCatalog = _store.Tags.List();
        _peopleCatalog = _store.People.List();
        _locationCatalog = _store.Locations.List();
        _ownerCatalog = _store.Owners.List();
        SyncFilterTagChoices();
        SyncSingleChoices();
    }

    private void RebuildChoices(
        ObservableCollection<SelectableEntity> target,
        IReadOnlyList<CatalogEntry> catalog,
        IReadOnlyList<string> selected)
    {
        foreach (var existing in target)
            existing.SelectionChanged -= OnChoiceChanged;

        target.Clear();
        var selectedNames = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog)
        {
            var choice = new SelectableEntity
            {
                Id = entry.Id,
                Name = entry.Name,
                IsSelected = selectedNames.Contains(entry.Name)
            };
            choice.SelectionChanged += OnChoiceChanged;
            target.Add(choice);
        }
    }

    private void OnChoiceChanged(object? sender, EventArgs e)
    {
        if (_suspend || sender is not SelectableEntity choice)
            return;

        if (FilterTagChoices.Contains(choice))
        {
            OnFilterTagsChanged();
            return;
        }

        if (!_choicesReady)
            return;

        if (AlbumTagChoices.Contains(choice))
            OnAlbumTagsChanged();
        else if (ItemTagChoices.Contains(choice))
            OnItemTagsChanged();
        else if (ItemPeopleChoices.Contains(choice))
            OnItemPeopleChanged();
    }

    private void OnFilterTagsChanged()
    {
        if (_suspend)
            return;

        FilterTagSummary = FormatSummary(FilterTagChoices);
        TagFilterText = string.Join(", ", SelectedNames(FilterTagChoices));
    }

    private void SyncFilterTagChoices()
    {
        var suspended = _suspend;
        _suspend = true;
        try
        {
            RebuildChoices(FilterTagChoices, _tagCatalog, TagList.Parse(TagFilterText));
            FilterTagSummary = FormatSummary(FilterTagChoices);
        }
        finally
        {
            _suspend = suspended;
        }
    }

    private void OnAlbumTagsChanged()
    {
        if (_suspend || !_choicesReady || _document is null)
            return;

        _document.Tags = SelectedNames(AlbumTagChoices);
        AlbumTagSummary = FormatSummary(AlbumTagChoices);
        MarkEdited();
    }

    private void OnItemTagsChanged()
    {
        if (_suspend || !_choicesReady || SelectedItem is null)
            return;

        SelectedItem.Model.Tags = SelectedNames(ItemTagChoices);
        SelectedItem.TagsText = TagList.Format(SelectedItem.Model.Tags);
        ItemTagSummary = FormatSummary(ItemTagChoices);
        MarkEdited();
    }

    private void OnItemPeopleChanged()
    {
        if (_suspend || !_choicesReady || SelectedItem is null)
            return;

        SelectedItem.Model.People = SelectedNames(ItemPeopleChoices);
        SelectedItem.PeopleText = TagList.Format(SelectedItem.Model.People);
        ItemPeopleSummary = FormatSummary(ItemPeopleChoices);
        MarkEdited();
    }

    private static List<string> SelectedNames(IEnumerable<SelectableEntity> choices) =>
        choices.Where(choice => choice.IsSelected).Select(choice => choice.Name).ToList();

    private static string FormatSummary(IEnumerable<SelectableEntity> choices)
    {
        var names = SelectedNames(choices);
        return names.Count == 0 ? "None" : string.Join(", ", names);
    }

    private void SyncSingleChoices()
    {
        var suspended = _suspend;
        _suspend = true;
        try
        {
            FillChoices(LocationChoices, _locationCatalog);
            FillChoices(OwnerChoices, _ownerCatalog);
            SelectedAlbumLocation = MatchChoice(LocationChoices, _document?.Location);
            SelectedAlbumOwner = MatchChoice(OwnerChoices, _document?.Owner);
            SelectedItemLocation = MatchChoice(LocationChoices, SelectedItem?.Model.Location);
            SelectedItemOwner = MatchChoice(OwnerChoices, SelectedItem?.Model.Owner);
        }
        finally
        {
            _suspend = suspended;
        }
    }

    private static void FillChoices(ObservableCollection<CatalogEntry> target, IReadOnlyList<CatalogEntry> catalog)
    {
        target.Clear();
        target.Add(new CatalogEntry { Id = 0, Name = "None" });
        foreach (var entry in catalog)
            target.Add(new CatalogEntry { Id = entry.Id, Name = entry.Name });
    }

    private static CatalogEntry MatchChoice(IEnumerable<CatalogEntry> choices, string? name)
    {
        var list = choices as IList<CatalogEntry> ?? choices.ToList();
        if (list.Count == 0)
            return new CatalogEntry { Id = 0, Name = "None" };

        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = list.FirstOrDefault(choice =>
                choice.Id != 0 && choice.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return list[0];
    }

    private static string SingleName(CatalogEntry? choice) =>
        choice is null || choice.Id == 0 ? "" : choice.Name;

    private static void RewriteCatalog(AlbumDocument album, CatalogChange change, CatalogSubject subject)
    {
        if (change.Kind == CatalogChangeKind.Added)
            return;

        switch (subject)
        {
            case CatalogSubject.Tags:
                album.Tags = RewriteNames(album.Tags, change);
                foreach (var item in album.Items)
                    item.Tags = RewriteNames(item.Tags, change);
                break;
            case CatalogSubject.People:
                foreach (var item in album.Items)
                    item.People = RewriteNames(item.People, change);
                break;
            case CatalogSubject.Locations:
                album.Location = RewriteSingle(album.Location, change);
                foreach (var item in album.Items)
                    item.Location = RewriteSingle(item.Location, change);
                break;
            case CatalogSubject.Owners:
                album.Owner = RewriteSingle(album.Owner, change);
                foreach (var item in album.Items)
                    item.Owner = RewriteSingle(item.Owner, change);
                break;
        }
    }

    private static string RewriteSingle(string value, CatalogChange change)
    {
        if (!string.Equals(value, change.OldName, StringComparison.OrdinalIgnoreCase))
            return value ?? "";

        return change.Kind == CatalogChangeKind.Deleted ? "" : change.NewName;
    }

    private enum CatalogSubject
    {
        Tags,
        People,
        Locations,
        Owners
    }

    private static List<string> RewriteNames(List<string> names, CatalogChange change)
    {
        if (change.Kind == CatalogChangeKind.Deleted)
            return names.Where(name => !name.Equals(change.OldName, StringComparison.OrdinalIgnoreCase)).ToList();

        return names
            .Select(name => name.Equals(change.OldName, StringComparison.OrdinalIgnoreCase) ? change.NewName : name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task ReloadAlbumsAsync(int? selectId, bool selectFirst)
    {
        if (_store is null)
            return;

        var records = await Task.Run(() => _store.ListAlbums());
        _allAlbums = records
            .Select(CreateRow)
            .OrderByDescending(row => row.From ?? DateTime.MinValue)
            .ThenBy(row => row.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        RebuildYears();
        RebuildFilteredList();

        AlbumRowViewModel? row = null;
        if (selectId is int id)
        {
            row = FilteredAlbums.FirstOrDefault(item => item.Id == id)
                ?? _allAlbums.FirstOrDefault(item => item.Id == id);
        }
        else if (selectFirst)
        {
            row = FilteredAlbums.FirstOrDefault(item => item.IsReadable);
        }

        await OpenAlbumAsync(row);
        if (row is null)
            StatusText = $"{FilteredAlbums.Count} of {_allAlbums.Count} albums";
    }

    private async Task OpenAlbumAsync(AlbumRowViewModel? row)
    {
        var generation = BeginItemLoad();
        var token = _loadCts!.Token;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        SelectedRow = row;
        if (row is null || _store is null)
        {
            ClearEditor();
            return;
        }

        if (!row.IsReadable)
        {
            ClearEditor();
            StatusText = row.Error ?? "This album could not be read.";
            return;
        }

        AlbumDocument album;
        try
        {
            album = await Task.Run(() => _store.Read(row.Id), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            row.IsReadable = false;
            row.Error = ex.Message;
            row.Detail = ex.Message;
            ClearEditor();
            StatusText = ex.Message;
            return;
        }

        if (generation != _loadGeneration)
            return;

        AlbumEditor.SortByOrder(album);
        ReloadCatalog();
        ApplyDocumentFields(album);
        _baseline = AlbumJson.Serialize(album);
        IsDirty = false;
        ValidationMessage = "";
        NotifyChrome();
        RefreshCommandStates();
        await PopulateItemsAsync(album.Items, generation, token, dispatcher, selectedItemId: null);
        if (generation != _loadGeneration)
            return;

        StatusText = row.DisplayName;
        RefreshCommandStates();
    }

    private void DiscardWithoutSaving()
    {
        if (_baseline is not null && SelectedRow is not null)
            SelectedRow.Refresh(AlbumJson.Deserialize(_baseline));

        IsDirty = false;
        RefreshCommandStates();
    }

    private async Task RestoreBaselineAsync()
    {
        if (_document is null || _baseline is null)
        {
            IsDirty = false;
            return;
        }

        var selectedId = SelectedItem?.Id;
        var restored = AlbumJson.Deserialize(_baseline);
        restored.Id = _document.Id;
        restored.SourcePath = _document.SourcePath;
        restored.ItemsLoaded = true;
        AlbumEditor.SortByOrder(restored);
        var generation = BeginItemLoad();
        var token = _loadCts!.Token;
        ApplyDocumentFields(restored);
        _baseline = AlbumJson.Serialize(restored);
        IsDirty = false;
        ValidationMessage = "";
        await PopulateItemsAsync(restored.Items, generation, token, DispatcherQueue.GetForCurrentThread(), selectedId);
        if (generation != _loadGeneration)
            return;

        SelectedRow?.Refresh(restored);
        StatusText = "Changes cancelled.";
        RefreshCommandStates();
    }

    private void ClearDocument()
    {
        BeginItemLoad();
        _document = null;
        _baseline = null;
        ClearEditor();
    }

    private void ClearEditor()
    {
        _suspend = true;
        try
        {
            AlbumTitle = "";
            AlbumDescription = "";
            AlbumDateFromText = "";
            AlbumDateToText = "";
            SelectedAlbumLocation = MatchChoice(LocationChoices, "");
            SelectedAlbumOwner = MatchChoice(OwnerChoices, "");
            AlbumTagSummary = "None";
            _choicesReady = false;
            RebuildChoices(AlbumTagChoices, [], []);
            ApplyItemSelection(null);
            ValidationMessage = "";
            IsDirty = false;
            CoverText = "None";
            AlbumHeading = "No album selected";
            ItemCountLabel = "";
            IsLoadingItems = false;
        }
        finally
        {
            _suspend = false;
        }

        NotifyChrome();
        RefreshCommandStates();
    }

    private int BeginItemLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        IsRebinding = true;
        try
        {
            Items.Clear();
        }
        finally
        {
            IsRebinding = false;
        }

        IsLoadingItems = false;
        return ++_loadGeneration;
    }

    private async Task PopulateItemsAsync(
        IReadOnlyList<AlbumItem> source,
        int generation,
        CancellationToken token,
        DispatcherQueue? dispatcher,
        int? selectedItemId)
    {
        if (_media is null || dispatcher is null)
            return;

        var items = source.ToList();
        if (items.Count == 0)
        {
            IsLoadingItems = false;
            NotifyChrome();
            return;
        }

        IsLoadingItems = true;
        NotifyChrome();
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (generation != _loadGeneration || token.IsCancellationRequested)
                    return;

                var model = items[index];
                var viewModel = CreateItem(model, dispatcher);
                viewModel.ThumbnailRequested = true;
                IsRebinding = true;
                try
                {
                    Items.Add(viewModel);
                }
                finally
                {
                    IsRebinding = false;
                }

                var fileName = ItemFileName(model.FilePath);
                StatusText = $"Loading item {fileName} ({index + 1} of {items.Count} processed)";
                ItemCountLabel = index == 0 ? "1 item" : $"{index + 1} items";
                _ = viewModel.LoadThumbnailAsync(token);
                try
                {
                    await Task.Delay(15, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoadingItems = false;
                var match = selectedItemId is int id ? Items.FirstOrDefault(item => item.Id == id) : null;
                if (match is not null)
                    SelectItem(match);
                NotifyChrome();
            }
        }
    }

    private void ApplyDocumentFields(AlbumDocument document)
    {
        _suspend = true;
        try
        {
            _document = document;
            AlbumTitle = document.Title;
            AlbumDescription = document.Description;
            AlbumDateFromText = document.Date.From;
            AlbumDateToText = document.Date.To;
            SyncSingleChoices();
            AlbumHeading = AlbumDisplay.FormatName(document);
            RebuildChoices(AlbumTagChoices, _tagCatalog, document.Tags);
            AlbumTagSummary = FormatSummary(AlbumTagChoices);
            _choicesReady = true;
            RefreshCover();
            ApplyItemSelection(null);
        }
        finally
        {
            _suspend = false;
        }
    }

    private MediaItemViewModel CreateItem(AlbumItem model, DispatcherQueue? dispatcher)
    {
        var item = new MediaItemViewModel(model, _media!, dispatcher);
        item.SetCover(_document?.CoverItemId == model.Id);
        return item;
    }

    private void RefreshCover()
    {
        AlbumItem? cover = null;
        if (_document?.CoverItemId is int id)
            cover = _document.Items.FirstOrDefault(item => item.Id == id);

        CoverText = cover is null
            ? "None"
            : string.IsNullOrWhiteSpace(cover.Title) ? "Untitled" : cover.Title;
        foreach (var item in Items)
            item.SetCover(cover is not null && item.Id == cover.Id);
        SetCoverCommand.NotifyCanExecuteChanged();
    }

    private static string ItemFileName(string storedPath)
    {
        var name = Path.GetFileName(storedPath.Replace('/', '\\').Trim());
        return string.IsNullOrWhiteSpace(name) ? "item" : name;
    }

    private void SelectItem(MediaItemViewModel? item)
    {
        _suspend = true;
        ApplyItemSelection(item);
        _suspend = false;
        NotifyChrome();
        RefreshCommandStates();
    }

    private void ApplyItemSelection(MediaItemViewModel? item)
    {
        SelectedItem = item;
        ItemTitle = item?.Title ?? "";
        ItemDescription = item?.Description ?? "";
        ItemDateTakenText = item?.DateTaken ?? "";
        SelectedItemLocation = MatchChoice(LocationChoices, item?.Model.Location);
        SelectedItemOwner = MatchChoice(OwnerChoices, item?.Model.Owner);
        ItemFilePath = item?.FilePath ?? "";
        RebuildChoices(ItemTagChoices, item is null ? [] : _tagCatalog, item?.Model.Tags ?? []);
        RebuildChoices(ItemPeopleChoices, item is null ? [] : _peopleCatalog, item?.Model.People ?? []);
        ItemTagSummary = FormatSummary(ItemTagChoices);
        ItemPeopleSummary = FormatSummary(ItemPeopleChoices);
        ItemOrderText = item?.OrderLabel ?? "";
        ItemIdText = item is null ? "" : item.Id.ToString(CultureInfo.InvariantCulture);
        ItemResolvedPath = item?.ResolvedPath ?? "";
        ItemMissingText = item is { IsMissing: true }
            ? "The file was not found in the media root."
            : "";
    }

    private void PullEditorIntoDocument()
    {
        if (_suspend || _document is null)
            return;

        _document.Title = AlbumTitle;
        _document.Description = AlbumDescription;
        _document.Date ??= new AlbumDateRange();
        _document.Date.From = AlbumDateFromText.Trim();
        _document.Date.To = AlbumDateToText.Trim();
        _document.Location = SingleName(SelectedAlbumLocation);
        _document.Owner = SingleName(SelectedAlbumOwner);
        if (_choicesReady)
            _document.Tags = SelectedNames(AlbumTagChoices);

        if (SelectedItem is not null)
        {
            SelectedItem.Apply(
                ItemTitle,
                ItemDescription,
                ItemDateTakenText,
                SingleName(SelectedItemLocation),
                SingleName(SelectedItemOwner),
                ItemFilePath,
                _choicesReady ? SelectedNames(ItemTagChoices) : SelectedItem.Model.Tags,
                _choicesReady ? SelectedNames(ItemPeopleChoices) : SelectedItem.Model.People);
            ItemResolvedPath = SelectedItem.ResolvedPath;
            ItemMissingText = SelectedItem.IsMissing ? "The file was not found in the media root." : "";
            ItemOrderText = SelectedItem.OrderLabel;
        }
    }

    private void MarkEdited()
    {
        if (_suspend || _document is null || _baseline is null)
            return;

        PullEditorIntoDocument();
        IsDirty = !string.Equals(AlbumJson.Serialize(_document), _baseline, StringComparison.Ordinal);
        ValidationMessage = IsDirty ? ValidateDocument() ?? "" : "";
        SelectedRow?.Refresh(_document);
        RefreshCover();
        AlbumHeading = AlbumDisplay.FormatName(_document);
        ItemCountLabel = Items.Count == 1 ? "1 item" : $"{Items.Count} items";
        DirtyLabel = IsDirty ? "Unsaved changes" : "";
        NotifyChrome();
        RefreshCommandStates();
    }

    private string? ValidateDocument()
    {
        if (_document is null)
            return null;

        var dates = AlbumEditor.ValidateDates(_document);
        if (dates is not null)
            return dates;

        if (_media is null)
            return null;

        foreach (var item in _document.Items)
        {
            try
            {
                _media.ToAbsolute(item.FilePath);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        return null;
    }

    private AlbumRowViewModel CreateRow(AlbumDocument album)
    {
        var row = new AlbumRowViewModel
        {
            Id = album.Id,
            IsReadable = true
        };
        row.Refresh(album);
        return row;
    }

    private void RebuildYears()
    {
        var selected = SelectedYear?.Value;
        _suspend = true;
        YearOptions.Clear();
        YearOptions.Add(_allYear);
        foreach (var year in _allAlbums
                     .Select(row => row.From?.Year)
                     .Where(year => year is not null)
                     .Select(year => year!.Value)
                     .Distinct()
                     .OrderByDescending(year => year))
        {
            YearOptions.Add(new FilterOption(year.ToString(CultureInfo.InvariantCulture), year));
        }

        SelectedYear = YearOptions.FirstOrDefault(option => option.Value == selected) ?? _allYear;
        _suspend = false;
    }

    private void RebuildFilteredList()
    {
        var criteria = CurrentCriteria();
        IsRebinding = true;
        try
        {
            FilteredAlbums.Clear();
            foreach (var row in _allAlbums.Where(row => AlbumRowVisible(row, criteria)))
                FilteredAlbums.Add(row);
        }
        finally
        {
            IsRebinding = false;
        }

        AlbumsChanged?.Invoke(this, EventArgs.Empty);
        NotifyChrome();
    }

    private AlbumFilterCriteria CurrentCriteria() => new(
        SelectedYear?.Value,
        SelectedMonth?.Value,
        SelectedDay?.Value,
        SearchText,
        TagList.Parse(TagFilterText));

    private static bool AlbumRowVisible(AlbumRowViewModel row, AlbumFilterCriteria criteria)
    {
        if (!row.IsReadable)
            return criteria.IsEmpty();

        return AlbumFilter.Matches(row.ToSummary(), criteria);
    }

    private bool CurrentAlbumStillVisible()
    {
        if (SelectedRow is null)
            return true;

        if (!SelectedRow.IsReadable)
            return CurrentCriteria().IsEmpty();

        return AlbumFilter.Matches(SelectedRow.ToSummary(), CurrentCriteria());
    }

    private void QueueFilter()
    {
        if (_suspend)
            return;

        _ = ApplyFilterAsync();
    }

    private async Task ApplyFilterAsync()
    {
        if (_filterBusy)
        {
            _filterAgain = true;
            return;
        }

        _filterBusy = true;
        try
        {
            do
            {
                _filterAgain = false;
                if (SelectedRow is not null && !CurrentAlbumStillVisible() && IsDirty)
                {
                    if (!await EnsureCanLeaveAsync(restoreOnDiscard: false))
                    {
                        RestoreCommittedFilter();
                        break;
                    }

                    if (SelectedRow is not null && !CurrentAlbumStillVisible())
                        await OpenAlbumAsync(null);
                }

                CommitFilter();
                RebuildFilteredList();
                if (SelectedRow is not null)
                {
                    var still = FilteredAlbums.FirstOrDefault(row => row.Id == SelectedRow.Id);
                    if (still is not null && !ReferenceEquals(still, SelectedRow))
                        SelectedRow = still;
                }

                StatusText = $"{FilteredAlbums.Count} of {_allAlbums.Count} albums";
            }
            while (_filterAgain);
        }
        finally
        {
            _filterBusy = false;
        }
    }

    private void CommitFilter()
    {
        _committedYear = SelectedYear?.Value;
        _committedMonth = SelectedMonth?.Value;
        _committedDay = SelectedDay?.Value;
        _committedSearch = SearchText;
        _committedTags = TagFilterText;
    }

    private void RestoreCommittedFilter()
    {
        _suspend = true;
        SelectedYear = YearOptions.FirstOrDefault(option => option.Value == _committedYear) ?? _allYear;
        SelectedMonth = MonthOptions.FirstOrDefault(option => option.Value == _committedMonth) ?? _allMonth;
        SelectedDay = DayOptions.FirstOrDefault(option => option.Value == _committedDay) ?? _allDay;
        SearchText = _committedSearch;
        TagFilterText = _committedTags;
        SyncFilterTagChoices();
        _suspend = false;
    }

    private void ResetFilters()
    {
        _suspend = true;
        SelectedYear = _allYear;
        SelectedMonth = _allMonth;
        SelectedDay = _allDay;
        SearchText = "";
        TagFilterText = "";
        SyncFilterTagChoices();
        _suspend = false;
        CommitFilter();
    }

    private IReadOnlyList<FilterOption> BuildMonthOptions()
    {
        var months = new List<FilterOption> { _allMonth };
        var names = CultureInfo.CurrentCulture.DateTimeFormat;
        for (var month = 1; month <= 12; month++)
            months.Add(new FilterOption(names.GetAbbreviatedMonthName(month), month));
        return months;
    }

    private IReadOnlyList<FilterOption> BuildDayOptions()
    {
        var days = new List<FilterOption> { _allDay };
        for (var day = 1; day <= 31; day++)
            days.Add(new FilterOption(day.ToString(CultureInfo.InvariantCulture), day));
        return days;
    }

    private void NotifyChrome()
    {
        if (_document is not null)
        {
            AlbumHeading = AlbumDisplay.FormatName(_document);
            ItemCountLabel = Items.Count == 1 ? "1 item" : $"{Items.Count} items";
        }

        DirtyLabel = IsDirty ? "Unsaved changes" : "";
        OnPropertyChanged(nameof(AlbumEditorVisibility));
        OnPropertyChanged(nameof(ItemEditorVisibility));
        OnPropertyChanged(nameof(ItemPlaceholderVisibility));
        OnPropertyChanged(nameof(EmptyAlbumVisibility));
        OnPropertyChanged(nameof(EmptyItemsVisibility));
        OnPropertyChanged(nameof(LoadingVisibility));
        OnPropertyChanged(nameof(AllowItemReorder));
        OnPropertyChanged(nameof(NoAlbumsVisibility));
        OnPropertyChanged(nameof(ValidationVisibility));
    }

    private void RefreshCommandStates()
    {
        NewAlbumCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        DeleteAlbumCommand.NotifyCanExecuteChanged();
        AddMediaCommand.NotifyCanExecuteChanged();
        SetCoverCommand.NotifyCanExecuteChanged();
        RemoveItemCommand.NotifyCanExecuteChanged();
        UseFileDateTakenCommand.NotifyCanExecuteChanged();
        ApplyDateFromCommand.NotifyCanExecuteChanged();
        ApplyLocationCommand.NotifyCanExecuteChanged();
        ApplyOwnerCommand.NotifyCanExecuteChanged();
        ApplyTagsCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        ManageTagsCommand.NotifyCanExecuteChanged();
        ManagePeopleCommand.NotifyCanExecuteChanged();
        ManageLocationsCommand.NotifyCanExecuteChanged();
        ManageOwnersCommand.NotifyCanExecuteChanged();
    }

    private bool CanCreateAlbum() => _store is not null;

    private bool CanSave() => IsDirty && _document is not null && _store is not null;

    private bool CanDeleteAlbum() => SelectedRow is { IsReadable: true } && _store is not null;

    private bool CanAddMedia() => _document is not null && _media is not null && !IsLoadingItems;

    private bool CanSetCover() =>
        SelectedItem is not null
        && !IsLoadingItems
        && _document?.CoverItemId != SelectedItem.Id;

    private bool CanRemoveItem() => SelectedItem is not null && !IsLoadingItems;

    private bool CanUseFileDateTaken() => SelectedItem is not null && !IsLoadingItems;

    private bool CanApplyToItems() => _document is { Items.Count: > 0 } && !IsLoadingItems;

    private bool CanRefresh() => _store is not null;

    partial void OnSelectedYearChanged(FilterOption? value) => QueueFilter();

    partial void OnSelectedMonthChanged(FilterOption? value) => QueueFilter();

    partial void OnSelectedDayChanged(FilterOption? value) => QueueFilter();

    partial void OnSearchTextChanged(string value) => QueueFilter();

    partial void OnTagFilterTextChanged(string value) => QueueFilter();

    partial void OnAlbumTitleChanged(string value) => MarkEdited();

    partial void OnAlbumDescriptionChanged(string value) => MarkEdited();

    partial void OnAlbumDateFromTextChanged(string value) => MarkEdited();

    partial void OnAlbumDateToTextChanged(string value) => MarkEdited();

    partial void OnSelectedAlbumLocationChanged(CatalogEntry? value) => MarkEdited();

    partial void OnSelectedAlbumOwnerChanged(CatalogEntry? value) => MarkEdited();

    partial void OnItemTitleChanged(string value) => MarkEdited();

    partial void OnItemDescriptionChanged(string value) => MarkEdited();

    partial void OnItemDateTakenTextChanged(string value) => MarkEdited();

    partial void OnSelectedItemLocationChanged(CatalogEntry? value) => MarkEdited();

    partial void OnSelectedItemOwnerChanged(CatalogEntry? value) => MarkEdited();

    partial void OnItemFilePathChanged(string value) => MarkEdited();

    partial void OnIsDirtyChanged(bool value) => DirtyLabel = value ? "Unsaved changes" : "";

    partial void OnIsLoadingItemsChanged(bool value)
    {
        OnPropertyChanged(nameof(LoadingVisibility));
        OnPropertyChanged(nameof(EmptyItemsVisibility));
        OnPropertyChanged(nameof(AllowItemReorder));
        RefreshCommandStates();
    }

    partial void OnValidationMessageChanged(string value) => OnPropertyChanged(nameof(ValidationVisibility));
}

file static class AlbumFilterCriteriaExtensions
{
    public static bool IsEmpty(this AlbumFilterCriteria criteria) =>
        criteria.Year is null
        && criteria.Month is null
        && criteria.Day is null
        && string.IsNullOrWhiteSpace(criteria.TitleQuery)
        && criteria.RequiredTags.Count == 0;
}

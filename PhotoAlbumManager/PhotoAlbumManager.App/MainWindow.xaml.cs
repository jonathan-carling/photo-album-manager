using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PhotoAlbumManager.Services;
using PhotoAlbumManager.ViewModels;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace PhotoAlbumManager;

public sealed partial class MainWindow : Window
{
    private bool _suppressSelection;
    private bool _allowClose;
    private bool _closePrompt;

    public MainWindow()
    {
        var interaction = new WindowInteraction(this);
        ViewModel = new MainViewModel(interaction);
        ViewModel.CloseRequested = () =>
        {
            _allowClose = true;
            Close();
            return Task.CompletedTask;
        };
        InitializeComponent();
        ViewModel.AlbumsChanged += (_, _) => SyncSelections();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        WireShell();
    }

    public MainViewModel ViewModel { get; }

    private void WireShell()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange || args.DidPresenterChange)
                UpdateTitleBarInsets();
        };
        Activated += (_, _) => UpdateCaptionColors();
        Shell.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1100;
            presenter.PreferredMinimumHeight = 700;
        }

        UpdateTitleBarInsets();
        UpdateCaptionColors();
        PlaceOnCurrentDisplay();
        ItemPanel.AddHandler(UIElement.TappedEvent, new TappedEventHandler(ItemPanel_Tapped), handledEventsToo: true);
        SyncMetaTab();
        try
        {
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch (Exception)
        {
            // The window still runs with the default icon if the asset cannot be applied.
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Shell.Loaded -= OnLoaded;
        await ViewModel.InitializeAsync(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        SyncSelections();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedRow))
            SyncSelections();
        else if (e.PropertyName == nameof(MainViewModel.SelectedItem))
        {
            SyncItemSelection();
            SyncMetaTab();
        }
    }

    private void AlbumList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ViewModel.IsRebinding)
            return;

        var incoming = AlbumList.SelectedItem as AlbumRowViewModel;
        if (incoming is null && ViewModel.SelectedRow is not null)
            return;

        if (ReferenceEquals(incoming, ViewModel.SelectedRow))
            return;

        // Defer so the unsaved-changes dialog is not shown inside the selection event.
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (ReferenceEquals(incoming, ViewModel.SelectedRow))
                    return;

                SetAlbumSelection(ViewModel.SelectedRow);
                await ViewModel.TrySelectAlbumAsync(incoming);
                SetAlbumSelection(ViewModel.SelectedRow);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(ex.Message);
            }
        });
    }

    private void ItemGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ViewModel.IsRebinding)
            return;

        var incoming = ItemGrid.SelectedItem as MediaItemViewModel;
        if (incoming is null && ViewModel.SelectedItem is not null)
            return;

        if (ReferenceEquals(incoming, ViewModel.SelectedItem))
            return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (ReferenceEquals(incoming, ViewModel.SelectedItem))
                    return;

                SetItemSelection(ViewModel.SelectedItem);
                await ViewModel.TrySelectItemAsync(incoming);
                SetItemSelection(ViewModel.SelectedItem);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(ex.Message);
            }
        });
    }

    private void ItemGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.SyncOrderFromView();

    private void ItemGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && ViewModel.RemoveItemCommand.CanExecute(null))
        {
            ViewModel.RemoveItemCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ItemGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not MediaItemViewModel item)
            return;

        if (item.Thumbnail is not null || item.ThumbnailRequested)
            return;

        item.ThumbnailRequested = true;
        _ = item.LoadThumbnailAsync(CancellationToken.None);
    }

    private async Task ShowErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Photo Album Manager",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Shell.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void SyncSelections()
    {
        SetAlbumSelection(ViewModel.SelectedRow);
        SetItemSelection(ViewModel.SelectedItem);
    }

    private void SyncItemSelection() => SetItemSelection(ViewModel.SelectedItem);

    private void SetAlbumSelection(AlbumRowViewModel? row)
    {
        if (ReferenceEquals(AlbumList.SelectedItem, row))
            return;

        _suppressSelection = true;
        AlbumList.SelectedItem = row;
        _suppressSelection = false;
    }

    private void SyncMetaTab()
    {
        var showItem = ViewModel.SelectedItem is not null;
        if (showItem)
        {
            ItemMetaTab.Visibility = Visibility.Visible;
            MetaTabs.SelectedItem = ItemMetaTab;
        }
        else
        {
            MetaTabs.SelectedItem = AlbumMetaTab;
            ItemMetaTab.Visibility = Visibility.Collapsed;
        }
    }

    private void ItemPanel_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInsideGridViewItem(e.OriginalSource as DependencyObject) || ViewModel.SelectedItem is null)
            return;

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await ViewModel.TrySelectItemAsync(null);
                SetItemSelection(ViewModel.SelectedItem);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(ex.Message);
            }
        });
    }

    private static bool IsInsideGridViewItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is GridViewItem)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void SetItemSelection(MediaItemViewModel? item)
    {
        if (ReferenceEquals(ItemGrid.SelectedItem, item))
            return;

        _suppressSelection = true;
        ItemGrid.SelectedItem = item;
        _suppressSelection = false;
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose || !ViewModel.IsDirty)
            return;

        args.Cancel = true;
        if (_closePrompt)
            return;

        _closePrompt = true;
        _ = FinishCloseAsync();
    }

    private async Task FinishCloseAsync()
    {
        try
        {
            if (await ViewModel.ConfirmCloseAsync())
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _closePrompt = false;
        }
    }

    private void UpdateTitleBarInsets()
    {
        TitleBarLeftPadding.Width = new GridLength(AppWindow.TitleBar.LeftInset);
        TitleBarRightPadding.Width = new GridLength(AppWindow.TitleBar.RightInset);
    }

    private void UpdateCaptionColors()
    {
        var dark = Shell.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 28, 28, 28);
        var inactive = dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 120, 120, 120);
        var hover = dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
        var pressed = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0);
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactive;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private void PlaceOnCurrentDisplay()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var width = Math.Min(1440, Math.Max(1100, work.Width - 80));
        var height = Math.Min(940, Math.Max(700, work.Height - 80));
        var x = work.X + Math.Max(0, (work.Width - width) / 2);
        var y = work.Y + Math.Max(0, (work.Height - height) / 2);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }
}

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PhotoAlbumManager.Core.Media;
using PhotoAlbumManager.Core.Storage;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PhotoAlbumManager.Services;

public sealed class WindowInteraction : IUserInteraction
{
    private readonly Window _window;

    public WindowInteraction(Window window) => _window = window;

    public async Task<UnsavedChoice> PromptUnsavedAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Unsaved changes",
            Content = "This album has unsaved changes. Save them, go back, or cancel the changes?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Cancel changes",
            CloseButtonText = "Go back",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => UnsavedChoice.Save,
            ContentDialogResult.Secondary => UnsavedChoice.Discard,
            _ => UnsavedChoice.Stay
        };
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Go back",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 320,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = "OK",
            XamlRoot = Root
        };

        await dialog.ShowAsync();
    }

    public async Task ShowAboutAsync(string albumDirectory, string mediaRoot)
    {
        var body = new TextBlock
        {
            Text = "Photo Album Manager keeps virtual albums in a SQLite database. Photos and videos stay in the media root and are opened read-only; the app never moves, renames, or deletes them."
                + Environment.NewLine + Environment.NewLine
                + "Album metadata: " + albumDirectory
                + Environment.NewLine
                + "Media root: " + mediaRoot,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        var copyright = new TextBlock
        {
            Text = "(c) Jonathan Carling 2026. All rights reserved.",
            Margin = new Thickness(0, 20, 0, 0),
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        AutomationProperties.SetAutomationId(copyright, "AboutCopyright");
        var panel = new StackPanel { MaxWidth = 440 };
        panel.Children.Add(body);
        panel.Children.Add(copyright);

        var dialog = new ContentDialog
        {
            Title = "About Photo Album Manager",
            Content = panel,
            CloseButtonText = "OK",
            XamlRoot = Root
        };
        await dialog.ShowAsync();
    }

    public async Task<IReadOnlyList<string>?> PickMediaFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            CommitButtonText = "Add to album"
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        foreach (var extension in MediaKinds.Extensions)
            picker.FileTypeFilter.Add(extension);

        try
        {
            var files = await picker.PickMultipleFilesAsync();
            return files?.Select(file => file.Path).ToList();
        }
        catch (COMException ex)
        {
            await ShowMessageAsync(
                "Unable to open file picker",
                PickerFailureMessage("file picker", ex.HResult));
            return null;
        }
    }

    public async Task<(bool Accepted, string AlbumDirectory, string MediaRoot)> EditSettingsAsync(
        string albumDirectory,
        string mediaRoot)
    {
        var albumBox = new TextBox { Text = albumDirectory };
        var mediaBox = new TextBox { Text = mediaRoot, Margin = new Thickness(0, 8, 0, 0) };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed)
        };

        var panel = new StackPanel { Spacing = 4, Width = 460 };
        panel.Children.Add(new TextBlock { Text = "Album metadata directory" });
        panel.Children.Add(albumBox);
        panel.Children.Add(BrowseButton(albumBox));
        panel.Children.Add(new TextBlock { Text = "Media root directory", Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(mediaBox);
        panel.Children.Add(BrowseButton(mediaBox));
        panel.Children.Add(new TextBlock
        {
            Text = "Media files are only read. The SQLite database is stored in the album metadata directory, which must sit outside the media root.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 12, 0, 0)
        });
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "Folders",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Go back",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var albums = PathSafety.FullDirectory(albumBox.Text);
                var media = PathSafety.FullDirectory(mediaBox.Text);
                if (!Directory.Exists(media))
                    throw new InvalidOperationException("The media root folder does not exist.");

                SqliteAlbumStore.ValidateLocations(albums, media);
                error.Text = "";
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                error.Text = ex.Message;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return (false, albumDirectory, mediaRoot);

        return (true, albumBox.Text.Trim(), mediaBox.Text.Trim());
    }

    public async Task<IReadOnlyList<CatalogChange>> EditCatalogAsync(string title, INamedCatalog catalog)
    {
        var changes = new List<CatalogChange>();
        var entries = new ObservableCollection<CatalogEntry>(catalog.List());
        var list = new ListView
        {
            ItemsSource = entries,
            DisplayMemberPath = nameof(CatalogEntry.Name),
            Height = 240,
            SelectionMode = ListViewSelectionMode.Single
        };
        AutomationProperties.SetAutomationId(list, "CatalogList");
        var nameBox = new TextBox { PlaceholderText = "Name" };
        AutomationProperties.SetAutomationId(nameBox, "CatalogNameBox");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed)
        };
        string? confirmDelete = null;

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is CatalogEntry selected)
                nameBox.Text = selected.Name;
            confirmDelete = null;
        };

        void Refresh(int? selectId)
        {
            entries.Clear();
            foreach (var entry in catalog.List())
                entries.Add(entry);
            list.SelectedItem = selectId is int id
                ? entries.FirstOrDefault(entry => entry.Id == id)
                : null;
        }

        void Report(Exception ex)
        {
            error.Text = ex.Message;
            status.Text = "";
            confirmDelete = null;
        }

        var add = new Button { Content = "Add", MinWidth = 88 };
        AutomationProperties.SetAutomationId(add, "CatalogAddButton");
        add.Click += (_, _) =>
        {
            try
            {
                error.Text = "";
                var created = catalog.Add(nameBox.Text);
                changes.Add(new CatalogChange(CatalogChangeKind.Added, "", created.Name));
                status.Text = $"Added \"{created.Name}\".";
                confirmDelete = null;
                Refresh(created.Id);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        };

        var rename = new Button { Content = "Rename", MinWidth = 88 };
        AutomationProperties.SetAutomationId(rename, "CatalogRenameButton");
        rename.Click += (_, _) =>
        {
            if (list.SelectedItem is not CatalogEntry selected)
            {
                error.Text = "Select an entry to rename.";
                return;
            }

            try
            {
                error.Text = "";
                var previous = selected.Name;
                catalog.Rename(selected.Id, nameBox.Text);
                var updated = nameBox.Text.Trim();
                changes.Add(new CatalogChange(CatalogChangeKind.Renamed, previous, updated));
                status.Text = $"Renamed \"{previous}\" to \"{updated}\".";
                confirmDelete = null;
                Refresh(selected.Id);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        };

        var delete = new Button { Content = "Delete", MinWidth = 88 };
        AutomationProperties.SetAutomationId(delete, "CatalogDeleteButton");
        delete.Click += (_, _) =>
        {
            if (list.SelectedItem is not CatalogEntry selected)
            {
                error.Text = "Select an entry to delete.";
                return;
            }

            if (!string.Equals(confirmDelete, selected.Name, StringComparison.Ordinal))
            {
                confirmDelete = selected.Name;
                error.Text = "";
                status.Text = $"Click Delete again to remove \"{selected.Name}\" from every album and item.";
                return;
            }

            try
            {
                error.Text = "";
                catalog.Delete(selected.Id);
                changes.Add(new CatalogChange(CatalogChangeKind.Deleted, selected.Name, ""));
                status.Text = $"Deleted \"{selected.Name}\".";
                confirmDelete = null;
                nameBox.Text = "";
                Refresh(null);
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(rename);
        buttons.Children.Add(delete);

        var panel = new StackPanel { Width = 420, Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Names are shared. Renaming or deleting one updates every album and item that uses it.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });
        panel.Children.Add(list);
        panel.Children.Add(nameBox);
        panel.Children.Add(buttons);
        panel.Children.Add(status);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root
        };
        await dialog.ShowAsync();
        return changes;
    }

    private Button BrowseButton(TextBox target)
    {
        var button = new Button { Content = "Browse...", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        button.Click += async (_, _) =>
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                CommitButtonText = "Select folder"
            };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
            picker.FileTypeFilter.Add("*");
            try
            {
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                    target.Text = folder.Path;
            }
            catch (COMException ex)
            {
                await ShowMessageAsync(
                    "Unable to open folder picker",
                    PickerFailureMessage("folder picker", ex.HResult));
            }
        };
        return button;
    }

    private static string PickerFailureMessage(string pickerName, int hresult)
    {
        if (hresult == unchecked((int)0x80004005) && IsRunningElevated())
        {
            return $"Windows could not open the {pickerName} because the app is running as Administrator. "
                + "Close Visual Studio and run it normally (not elevated), then try again.";
        }

        return $"Windows could not open the {pickerName} (0x{(uint)hresult:X8}). Please try again.";
    }

    private static bool IsRunningElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private XamlRoot Root =>
        _window.Content?.XamlRoot ?? throw new InvalidOperationException("The window is not ready to show a dialog.");
}

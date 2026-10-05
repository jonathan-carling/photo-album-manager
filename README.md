# Photo Album Manager

Photo Album Manager is a Windows desktop app for virtual photo and video albums. Albums, tags, people, locations, and owners are stored in one SQLite database. The photos and videos stay where they are on disk. The app only stores a relative path and metadata, and it never moves, renames, copies, or deletes the media files.

## Requirements

- Windows 10 version 1809 or later (Windows 11 is the intended desktop)
- [.NET SDK 10](https://dotnet.microsoft.com/download)
- Visual Studio 2026 with the Windows App SDK / WinUI workload, if you want to open the solution in the IDE

The app targets `net10.0-windows10.0.19041.0` and uses WinUI 3 with the Windows App SDK 2.5. It is unpackaged, so it can read a normal media folder without MSIX filesystem virtualization.

## Build and run

From the repository root:

```powershell
dotnet build PhotoAlbumManager.slnx -c Debug
dotnet test PhotoAlbumManager.slnx
```

The solution platform is x64. In Visual Studio, set the startup project to **PhotoAlbumManager.App** and start it on **x64**.

The debug executable is:

```text
PhotoAlbumManager\PhotoAlbumManager.App\bin\x64\Debug\net10.0-windows10.0.19041.0\PhotoAlbumManager.exe
```

`appsettings.json` is copied next to that executable. Edit that copy, or change the paths from **File > Folders...** inside the app.

## Folders

Two paths are configured in `appsettings.json`:

| Setting | Purpose |
| --- | --- |
| `AlbumMetadataDirectory` | Folder that holds `photo-albums.sqlite` |
| `MediaRootDirectory` | Folder that contains the real photos and videos |

The copy in this repository points the album folder at `E:\apps\photo-album-manager\albums` and the media root at `C:\Users\jcarlin\Pictures`. Change both to folders on your machine.

The album folder must not sit inside the media root. That keeps the database from being written among the media files.

An item path is stored relative to the media root, with a leading backslash:

```text
Media root:  E:\photos
File:        E:\photos\2015-06\IMG_20150604_130440.jpg
Stored as:   \2015-06\IMG_20150604_130440.jpg
```

## Using the app

The window has three panes:

- **Left:** albums, labeled from the start date and title, for example `2015-10-31 Halloween`. Filter by year, month, day, title, and any number of album tags. Filters use album metadata only, not item tags.
- **Center:** the open album as a thumbnail grid, in `order`. Select an item to edit it, or drag thumbnails to reorder. Large albums fill the grid one item at a time.
- **Right:** Album Meta, and Item Meta on its own tab while a thumbnail is selected.

**File > Save** (Ctrl+S) writes the open album to the database. Adding, removing, reordering, or editing does not write until you save. Leaving an album or a selected item with unsaved edits asks you to save, go back, or cancel the changes.

**File > Tags...**, **People...**, **Locations...**, and **Owners...** edit the shared name lists. Renaming a name updates every album and item that uses it, and deleting it removes it from all of them. Album tags, item tags, and item people are checkbox lists, so each album or item can use any number of them. Location and owner are single choices: an album has one location and one owner, and an item has one location and one owner.

**Add media** references files that are already under the media root. It does not copy them into the album. A new item starts with the album's location and owner.

**Set cover** uses the selected item as the album's one cover. The cover title appears on Album Meta, and that thumbnail shows a star. Removing the cover item asks you to continue or cancel. Continuing leaves the album without a cover.

## Database

`photo-albums.sqlite` contains:

| Table | Contents |
| --- | --- |
| `Albums` | One row per virtual album, including one optional `CoverItemId` |
| `Items` | Photos and videos in an album, including the relative `FilePath` |
| `Tags` | Tag names, shared by every album and item |
| `People` | Person names, shared by every item |
| `Locations` | Location names. An album and an item each store one location |
| `Owners` | Owner names. An album and an item each store one owner |
| `AlbumTags`, `ItemTags`, `ItemPeople` | Which albums and items use which tags and people |

Dates are stored as text in the form `yyyy-MM-dd HH:mm:ss`. Album tags are what the library filters match. Item tags and people describe that photo or video only.

Supported pictures: `.jpg`, `.jpeg`, `.png`, `.gif`, `.bmp`, `.tif`, `.tiff`, `.webp`, `.heic`, `.jfif`.

Supported video: `.mp4`, `.mov`, `.m4v`, `.avi`, `.wmv`, `.mkv`, `.webm`.

## Projects

| Project | Role |
| --- | --- |
| `PhotoAlbumManager.App` | WinUI 3 application |
| `PhotoAlbumManager.Core` | Album model, SQLite store, path rules, and filters |
| `PhotoAlbumManager.Tests` | xUnit tests for schema, relative paths, filters, and read-only writes |

## License

(c) Jonathan Carling 2026. All rights reserved.

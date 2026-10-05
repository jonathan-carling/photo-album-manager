using PhotoAlbumManager.Core.Albums;
using PhotoAlbumManager.Core.Storage;

namespace PhotoAlbumManager.Tests;

public class AlbumBehaviorTests
{
    [Fact]
    public void Display_name_uses_date_from_and_title()
    {
        var album = new AlbumDocument
        {
            Title = "Halloween",
            Date = new AlbumDateRange { From = "2005-10-31 18:00:00" }
        };

        Assert.Equal("2005-10-31 Halloween", AlbumDisplay.FormatName(album));
    }

    [Fact]
    public void Filters_use_album_metadata_and_ignore_item_tags()
    {
        var album = new AlbumDocument
        {
            Title = "June 2015",
            Date = new AlbumDateRange { From = "2015-06-04 00:00:00" },
            Tags = ["travel"],
            Items =
            [
                new AlbumItem
                {
                    Id = 1,
                    Title = "Costume",
                    Order = 1,
                    FilePath = @"\2015-06\a.jpg",
                    Tags = ["halloween"]
                }
            ]
        };

        var halloween = new AlbumFilterCriteria(null, null, null, null, ["halloween"]);
        var travelInJune = new AlbumFilterCriteria(2015, 6, 4, "june", ["travel"]);
        var wrongDay = new AlbumFilterCriteria(2015, 6, 11, null, []);

        Assert.False(AlbumFilter.Matches(album, halloween));
        Assert.True(AlbumFilter.Matches(album, travelInJune));
        Assert.False(AlbumFilter.Matches(album, wrongDay));
    }

    [Fact]
    public void Tag_query_is_comma_separated_and_requires_every_tag()
    {
        var tags = TagList.Parse(" Halloween, party, halloween ");
        Assert.Equal(["Halloween", "party"], tags);

        var album = new AlbumSummary("Halloween", new DateTime(2015, 10, 31), ["halloween", "party", "2015"]);
        Assert.True(AlbumFilter.Matches(album, new AlbumFilterCriteria(null, null, null, "hall", tags)));
        Assert.False(AlbumFilter.Matches(album, new AlbumFilterCriteria(null, null, null, null, ["halloween", "missing"])));
    }

    [Fact]
    public void Add_remove_and_reorder_update_order_without_touching_files()
    {
        var album = AlbumEditor.CreateNew(new DateTime(2015, 6, 24), "June 2015");
        var first = AlbumEditor.AddMedia(album, @"\2015-06\a.jpg", dateTaken: "2015-06-04 13:04:40");
        var second = AlbumEditor.AddMedia(album, @"\2015-06\b.jpg");
        var third = AlbumEditor.AddMedia(album, @"\2015-06\c.mp4");

        Assert.Equal([1, 2, 3], album.Items.Select(item => item.Id));
        Assert.Equal([1, 2, 3], album.Items.Select(item => item.Order));
        Assert.Equal("a", first.Title);

        album.Location = "York";
        album.Owner = "Helen Thornton";
        var inherited = AlbumEditor.AddMedia(album, @"\2015-06\d.jpg");
        Assert.Equal("York", inherited.Location);
        Assert.Equal("Helen Thornton", inherited.Owner);
        album.Items.Remove(inherited);

        album.CoverItemId = first.Id;
        AlbumEditor.ApplyOrder(album, [third.Id, first.Id, second.Id]);
        Assert.Equal([3, 1, 2], album.Items.Select(item => item.Id));
        Assert.Equal([1, 2, 3], album.Items.Select(item => item.Order));

        Assert.True(AlbumEditor.Remove(album, first.Id));
        Assert.Null(album.CoverItemId);
        Assert.Equal([3, 2], album.Items.Select(item => item.Id));
        Assert.Equal([1, 2], album.Items.Select(item => item.Order));
    }

    [Fact]
    public void Database_stores_albums_without_touching_media_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "pam-store-" + Guid.NewGuid().ToString("N"));
        var albums = Path.Combine(root, "albums");
        var media = Path.Combine(root, "media");
        Directory.CreateDirectory(media);
        var photo = Path.Combine(media, "photo.jpg");
        File.WriteAllBytes(photo, [1, 2, 3]);
        var photoStamp = File.GetLastWriteTimeUtc(photo);
        var store = new SqliteAlbumStore(albums, media);

        try
        {
            var workplace = store.Tags.Add("workplace");
            store.People.Add("Helen Thornton");
            var york = store.Locations.Add("York");
            store.Owners.Add("Helen Thornton");
            var album = AlbumEditor.CreateNew(new DateTime(2015, 6, 24), "June 2015");
            album.Tags = ["workplace"];
            album.Location = "York";
            album.Owner = "Helen Thornton";
            var item = AlbumEditor.AddMedia(album, @"\photo.jpg", dateTaken: "2015-06-04 13:04:40");
            item.Tags = ["workplace"];
            item.People = ["Helen Thornton"];
            item.Location = "York";
            item.Owner = "Helen Thornton";
            store.Save(album);

            var saved = store.Read(album.Id);
            Assert.Equal(@"\photo.jpg", saved.Items[0].FilePath);
            Assert.Equal(["workplace"], saved.Tags);
            Assert.Equal(["Helen Thornton"], saved.Items[0].People);
            Assert.Equal("York", saved.Location);
            Assert.Equal("York", saved.Items[0].Location);
            Assert.Equal("Helen Thornton", saved.Owner);
            Assert.Null(saved.CoverItemId);

            album.CoverItemId = album.Items[0].Id;
            store.Save(album);
            Assert.Equal(album.Items[0].Id, store.Read(album.Id).CoverItemId);
            Assert.Equal(photoStamp, File.GetLastWriteTimeUtc(photo));
            Assert.Single(Directory.EnumerateFiles(media));
            Assert.Equal(SqliteAlbumStore.DatabaseFileName, Path.GetFileName(store.DatabasePath));

            store.Tags.Rename(workplace.Id, "company name");
            var renamed = store.Read(album.Id);
            Assert.Equal(["company name"], renamed.Tags);
            Assert.Equal(["company name"], renamed.Items[0].Tags);

            var person = store.People.List().Single();
            store.People.Rename(person.Id, "Helen Carling");
            Assert.Equal(["Helen Carling"], store.Read(album.Id).Items[0].People);

            store.People.Delete(person.Id);
            Assert.Empty(store.Read(album.Id).Items[0].People);

            store.Locations.Rename(york.Id, "Leeds");
            var moved = store.Read(album.Id);
            Assert.Equal("Leeds", moved.Location);
            Assert.Equal("Leeds", moved.Items[0].Location);

            var owner = store.Owners.List().Single();
            store.Owners.Delete(owner.Id);
            var unowned = store.Read(album.Id);
            Assert.Equal("", unowned.Owner);
            Assert.Equal("", unowned.Items[0].Owner);
            Assert.Equal(["company name"], store.Read(album.Id).Items[0].Tags);

            var listed = store.ListAlbums().Single();
            Assert.Equal(album.Id, listed.Id);
            Assert.Equal(1, listed.ListedItemCount);
            Assert.Empty(listed.Items);
            Assert.Equal(["company name"], listed.Tags);

            store.Delete(album.Id);
            Assert.Empty(store.ListAlbums());
            Assert.Equal(["company name"], store.Tags.List().Select(tag => tag.Name));
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Duplicate_catalog_names_are_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "pam-dup-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteAlbumStore(Path.Combine(root, "albums"), Path.Combine(root, "media"));
        try
        {
            store.Tags.Add("June");
            var error = Assert.Throws<InvalidOperationException>(() => store.Tags.Add(" june "));
            Assert.Contains("already exists", error.Message);
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_refuses_an_album_directory_inside_the_media_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "pam-overlap-" + Guid.NewGuid().ToString("N"));
        var media = Path.Combine(root, "photos");
        var albums = Path.Combine(media, "albums");
        Directory.CreateDirectory(albums);

        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => new SqliteAlbumStore(albums, media));
            Assert.Contains("must not be inside the media root", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Settings_round_trip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pam-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "appsettings.json");

        try
        {
            AppSettingsStore.Save(path, new AppPaths
            {
                AlbumMetadataDirectory = @"C:\photo-album-manager\albums",
                MediaRootDirectory = @"E:\photos"
            });

            var loaded = AppSettingsStore.Load(path);
            Assert.Equal(@"C:\photo-album-manager\albums", loaded.AlbumMetadataDirectory);
            Assert.Equal(@"E:\photos", loaded.MediaRootDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

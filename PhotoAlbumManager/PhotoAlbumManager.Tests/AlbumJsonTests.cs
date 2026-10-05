using PhotoAlbumManager.Core.Albums;

namespace PhotoAlbumManager.Tests;

public class AlbumJsonTests
{
    private static string SampleJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-album.json"));

    [Fact]
    public void Deserializes_sample_album()
    {
        var album = AlbumJson.Deserialize(SampleJson);

        Assert.Equal("My Photo Album", album.Title);
        Assert.Equal("A collection of memorable moments", album.Description);
        Assert.Equal("2024-06-01 00:00:00", album.Date.From);
        Assert.Equal("2024-06-30 23:59:59", album.Date.To);
        Assert.Equal("Various Locations", album.Location);
        Assert.Equal("John Doe", album.Owner);
        Assert.Empty(album.Tags);
        Assert.Equal(3, album.Items.Count);

        var first = album.Items[0];
        Assert.Equal(1, first.Id);
        Assert.Equal("Sunset at the Beach", first.Title);
        Assert.Equal("2024-06-05 18:30:00", first.DateTaken);
        Assert.Equal(1, first.Order);
        Assert.Equal("Santa Monica, CA", first.Location);
        Assert.Equal("photos/sunset_beach.jpg", first.FilePath);
        Assert.Equal(["sunset", "beach", "vacation"], first.Tags);
        Assert.Equal(["Alice", "Bob"], first.People);
    }

    [Fact]
    public void Round_trip_preserves_sample_fields()
    {
        var album = AlbumJson.Deserialize(SampleJson);
        var again = AlbumJson.Deserialize(AlbumJson.Serialize(album));

        Assert.Equal(album.Title, again.Title);
        Assert.Equal(album.Date.From, again.Date.From);
        Assert.Equal(album.Items.Select(item => item.FilePath), again.Items.Select(item => item.FilePath));
        Assert.Equal(album.Items.Select(item => item.DateTaken), again.Items.Select(item => item.DateTaken));
        Assert.Equal(album.Items[1].People, again.Items[1].People);
    }

    [Fact]
    public void Accepts_album_level_tags()
    {
        var album = AlbumJson.Deserialize("""
            {
              "title": "Halloween",
              "date": { "from": "2005-10-31 00:00:00", "to": "2005-10-31 23:59:59" },
              "tags": ["halloween", "party"],
              "items": []
            }
            """);

        Assert.Equal(["halloween", "party"], album.Tags);
    }

    [Fact]
    public void User_sample_file_on_disk_matches_schema()
    {
        const string path = @"E:\2015-06-24-album.json";
        if (!File.Exists(path))
            return;

        var album = AlbumJson.Deserialize(File.ReadAllText(path));
        Assert.False(string.IsNullOrWhiteSpace(album.Title));
        Assert.NotEmpty(album.Items);
        Assert.All(album.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.FilePath)));
    }
}

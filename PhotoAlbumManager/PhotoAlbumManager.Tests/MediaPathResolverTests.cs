using PhotoAlbumManager.Core.Media;
using PhotoAlbumManager.Core.Storage;

namespace PhotoAlbumManager.Tests;

public class MediaPathResolverTests
{
    [Fact]
    public void Relative_path_uses_leading_backslash_from_media_root()
    {
        var resolver = new MediaPathResolver(@"Z:\data\photos\");

        var relative = resolver.ToRelative(@"Z:\data\photos\2004\photo-001.jpg");

        Assert.Equal(@"\2004\photo-001.jpg", relative);
    }

    [Fact]
    public void Absolute_path_joins_root_without_treating_leading_slash_as_drive_root()
    {
        var resolver = new MediaPathResolver(@"Z:\data\photos");

        var absolute = resolver.ToAbsolute(@"\2004\photo-001.jpg");

        Assert.Equal(@"Z:\data\photos\2004\photo-001.jpg", absolute);
    }

    [Fact]
    public void Accepts_forward_slashes_from_the_sample_schema()
    {
        var resolver = new MediaPathResolver(@"E:\photos");

        var absolute = resolver.ToAbsolute("photos/sunset_beach.jpg");

        Assert.Equal(@"E:\photos\photos\sunset_beach.jpg", absolute);
    }

    [Fact]
    public void Rejects_files_outside_the_media_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "pam-media-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "pam-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "photo.jpg");
        File.WriteAllText(file, "not-a-real-photo");
        var before = File.GetLastWriteTimeUtc(file);

        try
        {
            var resolver = new MediaPathResolver(root);
            var error = Assert.Throws<InvalidOperationException>(() => resolver.ToRelative(file));
            Assert.Contains("outside the media root", error.Message);
            Assert.Equal(before, File.GetLastWriteTimeUtc(file));
            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Rejects_relative_paths_that_escape_the_media_root()
    {
        var resolver = new MediaPathResolver(@"Z:\data\photos");

        var error = Assert.Throws<InvalidOperationException>(() => resolver.ToAbsolute(@"\..\..\secret.txt"));

        Assert.Contains("outside the media root", error.Message);
    }

    [Fact]
    public void Parses_camera_file_names()
    {
        Assert.Equal("2015-06-04 13:04:40", MediaTimestamp.FromFileName(@"E:\photos\2015-06\IMG_20150604_130440.jpg"));
        Assert.Equal("2015-06-06 19:26:00", MediaTimestamp.FromFileName("VID_20150606_192600.mp4"));
        Assert.Equal("2015-06-11 22:28:43", MediaTimestamp.FromFileName("2015-06-11_22.28.43.jpg"));
        Assert.True(MediaKinds.IsVideo("clip.mp4"));
        Assert.True(MediaKinds.IsImage("photo.JPG"));
        Assert.False(MediaKinds.IsSupported("notes.txt"));
    }
}

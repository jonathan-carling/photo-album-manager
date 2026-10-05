namespace PhotoAlbumManager.Core.Media;

public static class MediaKinds
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".heic", ".jfif"
    };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".wmv", ".mkv", ".webm"
    };

    public static IReadOnlyList<string> Extensions { get; } =
        Images.Concat(Videos).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => Videos.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path) => IsImage(path) || IsVideo(path);
}

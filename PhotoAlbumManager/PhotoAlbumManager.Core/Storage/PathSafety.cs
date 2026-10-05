namespace PhotoAlbumManager.Core.Storage;

public static class PathSafety
{
    public static string FullDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public static bool IsSameOrSubdirectory(string candidate, string parent)
    {
        var child = FullDirectory(candidate) + Path.DirectorySeparatorChar;
        var root = FullDirectory(parent) + Path.DirectorySeparatorChar;
        return child.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

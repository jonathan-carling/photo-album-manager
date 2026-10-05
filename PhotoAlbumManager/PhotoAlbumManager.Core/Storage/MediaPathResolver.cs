namespace PhotoAlbumManager.Core.Storage;

/// <summary>
/// Resolves album item paths against the media root. This type only reads path strings.
/// It never copies, moves, deletes, or writes media files.
/// </summary>
public sealed class MediaPathResolver
{
    public MediaPathResolver(string mediaRoot)
    {
        MediaRoot = PathSafety.FullDirectory(mediaRoot);
    }

    public string MediaRoot { get; }

    public bool IsUnderRoot(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        var root = MediaRoot + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public string ToRelative(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!IsUnderRoot(full))
        {
            throw new InvalidOperationException(
                $"'{full}' is outside the media root '{MediaRoot}'. The file was not copied or moved.");
        }

        var relative = full[MediaRoot.Length..].Replace(Path.AltDirectorySeparatorChar, '\\').Replace('/', '\\');
        if (!relative.StartsWith('\\'))
            relative = "\\" + relative;
        return relative;
    }

    public string ToAbsolute(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
            throw new ArgumentException("A media path is required.", nameof(storedPath));

        var trimmed = storedPath.Trim();
        string combined;
        if (IsFullyQualified(trimmed))
        {
            combined = Path.GetFullPath(trimmed);
        }
        else
        {
            var withoutRoot = trimmed.Replace('/', '\\').TrimStart('\\');
            combined = Path.GetFullPath(Path.Combine(MediaRoot, withoutRoot));
        }

        if (!IsUnderRoot(combined))
        {
            throw new InvalidOperationException(
                $"'{storedPath}' resolves outside the media root '{MediaRoot}'.");
        }

        return combined;
    }

    public bool FileExists(string storedPath)
    {
        try
        {
            return File.Exists(ToAbsolute(storedPath));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsFullyQualified(string path) =>
        path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
        || path.StartsWith(@"\\", StringComparison.Ordinal);
}

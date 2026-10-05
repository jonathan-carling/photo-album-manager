using System.Text.RegularExpressions;

namespace PhotoAlbumManager.Core.Media;

public static partial class MediaTimestamp
{
    public static string? FromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name))
            return null;

        var compact = CompactPattern().Match(name);
        if (compact.Success)
            return Format(compact);

        var separated = SeparatedPattern().Match(name);
        if (separated.Success)
            return Format(separated);

        return null;
    }

    private static string Format(Match match) =>
        $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value} {match.Groups[4].Value}:{match.Groups[5].Value}:{match.Groups[6].Value}";

    [GeneratedRegex(@"(?:IMG|VID)_(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompactPattern();

    [GeneratedRegex(@"(\d{4})-(\d{2})-(\d{2})_(\d{2})\.(\d{2})\.(\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatedPattern();
}

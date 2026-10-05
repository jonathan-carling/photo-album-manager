using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoAlbumManager.Core.Storage;

public sealed class AppPaths
{
    public string AlbumMetadataDirectory { get; set; } = "";

    public string MediaRootDirectory { get; set; } = "";
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static AppPaths Load(string settingsPath)
    {
        if (!File.Exists(settingsPath))
            throw new FileNotFoundException("Application settings were not found.", settingsPath);

        var paths = JsonSerializer.Deserialize<AppPaths>(File.ReadAllText(settingsPath), Options)
            ?? throw new InvalidDataException("Settings file was empty.");

        if (string.IsNullOrWhiteSpace(paths.AlbumMetadataDirectory))
            throw new InvalidDataException("AlbumMetadataDirectory is required in appsettings.json.");

        if (string.IsNullOrWhiteSpace(paths.MediaRootDirectory))
            throw new InvalidDataException("MediaRootDirectory is required in appsettings.json.");

        paths.AlbumMetadataDirectory = PathSafety.FullDirectory(paths.AlbumMetadataDirectory);
        paths.MediaRootDirectory = PathSafety.FullDirectory(paths.MediaRootDirectory);
        return paths;
    }

    public static void Save(string settingsPath, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(paths, Options) + "\n";
        File.WriteAllText(settingsPath, json, Utf8);
    }
}

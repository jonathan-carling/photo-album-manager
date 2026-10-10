using Windows.Storage;

namespace PhotoAlbumManager.Services;

/// <summary>
/// Reads the Date taken value shown in Windows file properties (System.Photo.DateTaken).
/// </summary>
public static class FileDateTaken
{
    public static async Task<DateTime?> ReadAsync(string absolutePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(absolutePath);
        var properties = await file.Properties.RetrievePropertiesAsync(["System.Photo.DateTaken"]);
        if (!properties.TryGetValue("System.Photo.DateTaken", out var value) || value is null)
            return null;

        if (value is not DateTimeOffset taken || taken.Year < 1970)
            return null;

        // The property is a UTC instant. File Explorer shows that instant in local time.
        return DateTime.SpecifyKind(taken.LocalDateTime, DateTimeKind.Unspecified);
    }
}

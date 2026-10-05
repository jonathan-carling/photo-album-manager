using PhotoAlbumManager.Core.Storage;

namespace PhotoAlbumManager.Services;

public enum UnsavedChoice
{
    Save,
    Discard,
    Stay
}

public interface IUserInteraction
{
    Task<UnsavedChoice> PromptUnsavedAsync();

    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    Task ShowMessageAsync(string title, string message);

    Task ShowAboutAsync(string albumDirectory, string mediaRoot);

    Task<IReadOnlyList<string>?> PickMediaFilesAsync();

    Task<(bool Accepted, string AlbumDirectory, string MediaRoot)> EditSettingsAsync(string albumDirectory, string mediaRoot);

    Task<IReadOnlyList<CatalogChange>> EditCatalogAsync(string title, INamedCatalog catalog);
}

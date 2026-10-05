namespace PhotoAlbumManager.Core.Storage;

public sealed class CatalogEntry
{
    public int Id { get; init; }

    public required string Name { get; init; }
}

public enum CatalogChangeKind
{
    Added,
    Renamed,
    Deleted
}

public sealed record CatalogChange(CatalogChangeKind Kind, string OldName, string NewName);

/// <summary>
/// A shared list of names. Renaming or deleting an entry updates every album that references it.
/// </summary>
public interface INamedCatalog
{
    IReadOnlyList<CatalogEntry> List();

    CatalogEntry Add(string name);

    void Rename(int id, string name);

    void Delete(int id);
}

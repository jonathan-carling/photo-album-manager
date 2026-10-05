using Microsoft.Data.Sqlite;
using PhotoAlbumManager.Core.Albums;

namespace PhotoAlbumManager.Core.Storage;

/// <summary>
/// Stores every virtual album, tag, and person in one SQLite database inside the album metadata directory.
/// Media files are never opened for writing.
/// </summary>
public sealed class SqliteAlbumStore : IDisposable
{
    public const string DatabaseFileName = "photo-albums.sqlite";

    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;

    static SqliteAlbumStore() => SQLitePCL.Batteries_V2.Init();

    public SqliteAlbumStore(string albumDirectory, string mediaRoot)
    {
        AlbumDirectory = PathSafety.FullDirectory(albumDirectory);
        ValidateLocations(AlbumDirectory, mediaRoot);
        Directory.CreateDirectory(AlbumDirectory);
        DatabasePath = Path.Combine(AlbumDirectory, DatabaseFileName);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        Execute("PRAGMA foreign_keys = OFF;");
        CreateSchema();
        Execute("PRAGMA foreign_keys = ON;");
        Tags = new EntityCatalog(this, EntityKind.Tag);
        People = new EntityCatalog(this, EntityKind.Person);
        Locations = new EntityCatalog(this, EntityKind.Location);
        Owners = new EntityCatalog(this, EntityKind.Owner);
    }

    public string AlbumDirectory { get; }

    public string DatabasePath { get; }

    public INamedCatalog Tags { get; }

    public INamedCatalog People { get; }

    public INamedCatalog Locations { get; }

    public INamedCatalog Owners { get; }

    public static void ValidateLocations(string albumDirectory, string mediaRoot)
    {
        var albums = PathSafety.FullDirectory(albumDirectory);
        if (PathSafety.IsSameOrSubdirectory(albums, mediaRoot))
        {
            throw new InvalidOperationException(
                "The album metadata directory must not be inside the media root. The album database would otherwise be written among the media files.");
        }
    }

    public IReadOnlyList<AlbumDocument> ListAlbums()
    {
        lock (_gate)
            return ListAlbumsCore();
    }

    public AlbumDocument Read(int id)
    {
        lock (_gate)
            return ReadCore(id);
    }

    public void Save(AlbumDocument album)
    {
        ArgumentNullException.ThrowIfNull(album);
        var validation = AlbumEditor.ValidateDates(album);
        if (validation is not null)
            throw new InvalidOperationException(validation);

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            album.Location = Canonical(EntityKind.Location, album.Location, transaction);
            album.Owner = Canonical(EntityKind.Owner, album.Owner, transaction);
            foreach (var item in album.Items)
            {
                item.Location = Canonical(EntityKind.Location, item.Location, transaction);
                item.Owner = Canonical(EntityKind.Owner, item.Owner, transaction);
            }

            if (album.Id <= 0)
                album.Id = InsertAlbum(album, transaction);
            else
                UpdateAlbum(album, transaction);

            var kept = new List<int>(album.Items.Count);
            foreach (var item in album.Items)
            {
                if (item.Id > 0 && ItemBelongsToAlbum(item.Id, album.Id, transaction))
                    UpdateItem(album.Id, item, transaction);
                else
                {
                    var previousId = item.Id;
                    item.Id = InsertItem(album.Id, item, transaction);
                    if (album.CoverItemId == previousId)
                        album.CoverItemId = item.Id;
                }

                kept.Add(item.Id);
                item.Tags = ReplaceItemNames(item.Id, item.Tags, EntityKind.Tag, transaction);
                item.People = ReplaceItemNames(item.Id, item.People, EntityKind.Person, transaction);
            }

            DeleteMissingItems(album.Id, kept, transaction);
            SyncCoverItem(album, transaction);
            album.Tags = ReplaceAlbumTags(album.Id, album.Tags, transaction);
            album.SourcePath = DatabasePath;
            album.ItemsLoaded = true;
            album.ListedItemCount = album.Items.Count;
            transaction.Commit();
        }
    }

    public void Delete(int id)
    {
        lock (_gate)
        {
            var deleted = Execute("DELETE FROM Albums WHERE Id = $id;", ("$id", id));
            if (deleted == 0)
                throw new InvalidOperationException("That album is no longer in the database.");
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void CreateSchema()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS Locations (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );

            CREATE TABLE IF NOT EXISTS Owners (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );

            CREATE TABLE IF NOT EXISTS Albums (
                Id INTEGER PRIMARY KEY,
                Title TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                DateFrom TEXT NOT NULL DEFAULT '',
                DateTo TEXT NOT NULL DEFAULT '',
                LocationId INTEGER REFERENCES Locations(Id) ON DELETE SET NULL,
                OwnerId INTEGER REFERENCES Owners(Id) ON DELETE SET NULL,
                CoverItemId INTEGER REFERENCES Items(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS Tags (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );

            CREATE TABLE IF NOT EXISTS People (
                Id INTEGER PRIMARY KEY,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE
            );

            CREATE TABLE IF NOT EXISTS AlbumTags (
                AlbumId INTEGER NOT NULL REFERENCES Albums(Id) ON DELETE CASCADE,
                TagId INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY (AlbumId, TagId)
            );

            CREATE TABLE IF NOT EXISTS Items (
                Id INTEGER PRIMARY KEY,
                AlbumId INTEGER NOT NULL REFERENCES Albums(Id) ON DELETE CASCADE,
                Title TEXT NOT NULL,
                Description TEXT NOT NULL DEFAULT '',
                DateTaken TEXT NOT NULL DEFAULT '',
                SortOrder INTEGER NOT NULL,
                LocationId INTEGER REFERENCES Locations(Id) ON DELETE SET NULL,
                OwnerId INTEGER REFERENCES Owners(Id) ON DELETE SET NULL,
                FilePath TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ItemTags (
                ItemId INTEGER NOT NULL REFERENCES Items(Id) ON DELETE CASCADE,
                TagId INTEGER NOT NULL REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY (ItemId, TagId)
            );

            CREATE TABLE IF NOT EXISTS ItemPeople (
                ItemId INTEGER NOT NULL REFERENCES Items(Id) ON DELETE CASCADE,
                PersonId INTEGER NOT NULL REFERENCES People(Id) ON DELETE CASCADE,
                PRIMARY KEY (ItemId, PersonId)
            );
            """);
    }

    private IReadOnlyList<AlbumDocument> ListAlbumsCore()
    {
        var albums = new List<AlbumDocument>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT a.Id, a.Title, a.Description, a.DateFrom, a.DateTo,
                       IFNULL(l.Name, ''), IFNULL(o.Name, ''),
                       (SELECT COUNT(*) FROM Items i WHERE i.AlbumId = a.Id)
                FROM Albums a
                LEFT JOIN Locations l ON l.Id = a.LocationId
                LEFT JOIN Owners o ON o.Id = a.OwnerId
                ORDER BY a.DateFrom DESC, a.Id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                albums.Add(new AlbumDocument
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    Description = reader.GetString(2),
                    Date = new AlbumDateRange { From = reader.GetString(3), To = reader.GetString(4) },
                    Location = reader.GetString(5),
                    Owner = reader.GetString(6),
                    ListedItemCount = reader.GetInt32(7),
                    ItemsLoaded = false,
                    SourcePath = DatabasePath
                });
            }
        }

        var tags = LoadOwnerNames(
            """
            SELECT at.AlbumId, t.Name
            FROM AlbumTags at
            JOIN Tags t ON t.Id = at.TagId
            ORDER BY t.Name COLLATE NOCASE
            """);
        foreach (var album in albums)
        {
            if (tags.TryGetValue(album.Id, out var names))
                album.Tags = names;
        }

        return albums;
    }

    private AlbumDocument ReadCore(int id)
    {
        AlbumDocument album;
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT a.Id, a.Title, a.Description, a.DateFrom, a.DateTo,
                       IFNULL(l.Name, ''), IFNULL(o.Name, ''), a.CoverItemId
                FROM Albums a
                LEFT JOIN Locations l ON l.Id = a.LocationId
                LEFT JOIN Owners o ON o.Id = a.OwnerId
                WHERE a.Id = $id
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("That album is no longer in the database.");

            album = new AlbumDocument
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Description = reader.GetString(2),
                Date = new AlbumDateRange { From = reader.GetString(3), To = reader.GetString(4) },
                Location = reader.GetString(5),
                Owner = reader.GetString(6),
                CoverItemId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                SourcePath = DatabasePath,
                ItemsLoaded = true
            };
        }

        album.Tags = LoadNames(
            """
            SELECT t.Name
            FROM AlbumTags at
            JOIN Tags t ON t.Id = at.TagId
            WHERE at.AlbumId = $id
            ORDER BY t.Name COLLATE NOCASE
            """,
            id);

        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT i.Id, i.Title, i.Description, i.DateTaken, i.SortOrder,
                       IFNULL(l.Name, ''), IFNULL(o.Name, ''), i.FilePath
                FROM Items i
                LEFT JOIN Locations l ON l.Id = i.LocationId
                LEFT JOIN Owners o ON o.Id = i.OwnerId
                WHERE i.AlbumId = $id
                ORDER BY i.SortOrder, i.Id
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                album.Items.Add(new AlbumItem
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    Description = reader.GetString(2),
                    DateTaken = reader.GetString(3),
                    Order = reader.GetInt32(4),
                    Location = reader.GetString(5),
                    Owner = reader.GetString(6),
                    FilePath = reader.GetString(7)
                });
            }
        }

        var itemTags = LoadOwnerNames(
            """
            SELECT it.ItemId, t.Name
            FROM ItemTags it
            JOIN Tags t ON t.Id = it.TagId
            JOIN Items i ON i.Id = it.ItemId
            WHERE i.AlbumId = $id
            ORDER BY t.Name COLLATE NOCASE
            """,
            id);
        var itemPeople = LoadOwnerNames(
            """
            SELECT ip.ItemId, p.Name
            FROM ItemPeople ip
            JOIN People p ON p.Id = ip.PersonId
            JOIN Items i ON i.Id = ip.ItemId
            WHERE i.AlbumId = $id
            ORDER BY p.Name COLLATE NOCASE
            """,
            id);
        foreach (var item in album.Items)
        {
            if (itemTags.TryGetValue(item.Id, out var tags))
                item.Tags = tags;
            if (itemPeople.TryGetValue(item.Id, out var people))
                item.People = people;
        }

        album.ListedItemCount = album.Items.Count;
        return album;
    }

    private int InsertAlbum(AlbumDocument album, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO Albums (Title, Description, DateFrom, DateTo, LocationId, OwnerId)
            VALUES ($title, $description, $from, $to, $location, $owner)
            """;
        BindAlbum(command, album);
        command.ExecuteNonQuery();
        return LastInsertId(transaction);
    }

    private void UpdateAlbum(AlbumDocument album, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Albums
            SET Title = $title, Description = $description, DateFrom = $from, DateTo = $to,
                LocationId = $location, OwnerId = $owner
            WHERE Id = $id
            """;
        BindAlbum(command, album);
        command.Parameters.AddWithValue("$id", album.Id);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("That album is no longer in the database.");
    }

    private void BindAlbum(SqliteCommand command, AlbumDocument album)
    {
        command.Parameters.AddWithValue("$title", album.Title ?? "");
        command.Parameters.AddWithValue("$description", album.Description ?? "");
        command.Parameters.AddWithValue("$from", album.Date?.From ?? "");
        command.Parameters.AddWithValue("$to", album.Date?.To ?? "");
        command.Parameters.AddWithValue("$location", IdOrNull(ResolveOptional(EntityKind.Location, album.Location, command.Transaction)));
        command.Parameters.AddWithValue("$owner", IdOrNull(ResolveOptional(EntityKind.Owner, album.Owner, command.Transaction)));
    }

    private bool ItemBelongsToAlbum(int itemId, int albumId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT AlbumId FROM Items WHERE Id = $id";
        command.Parameters.AddWithValue("$id", itemId);
        var found = command.ExecuteScalar();
        return found is long owner && owner == albumId;
    }

    private void UpdateItem(int albumId, AlbumItem item, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE Items
            SET AlbumId = $album, Title = $title, Description = $description, DateTaken = $taken,
                SortOrder = $order, LocationId = $location, OwnerId = $owner, FilePath = $path
            WHERE Id = $id
            """;
        BindItem(command, albumId, item);
        command.Parameters.AddWithValue("$id", item.Id);
        command.ExecuteNonQuery();
    }

    private int InsertItem(int albumId, AlbumItem item, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO Items (AlbumId, Title, Description, DateTaken, SortOrder, LocationId, OwnerId, FilePath)
            VALUES ($album, $title, $description, $taken, $order, $location, $owner, $path)
            """;
        BindItem(command, albumId, item);
        command.ExecuteNonQuery();
        return LastInsertId(transaction);
    }

    private void BindItem(SqliteCommand command, int albumId, AlbumItem item)
    {
        command.Parameters.AddWithValue("$album", albumId);
        command.Parameters.AddWithValue("$title", item.Title ?? "");
        command.Parameters.AddWithValue("$description", item.Description ?? "");
        command.Parameters.AddWithValue("$taken", item.DateTaken ?? "");
        command.Parameters.AddWithValue("$order", item.Order);
        command.Parameters.AddWithValue("$location", IdOrNull(ResolveOptional(EntityKind.Location, item.Location, command.Transaction)));
        command.Parameters.AddWithValue("$owner", IdOrNull(ResolveOptional(EntityKind.Owner, item.Owner, command.Transaction)));
        command.Parameters.AddWithValue("$path", item.FilePath ?? "");
    }

    private string Canonical(EntityKind kind, string? name, SqliteTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        return Resolve(kind, name, transaction).Name;
    }

    private int? ResolveOptional(EntityKind kind, string? name, SqliteTransaction? transaction)
    {
        if (string.IsNullOrWhiteSpace(name) || transaction is null)
            return null;

        return Resolve(kind, name, transaction).Id;
    }

    private static object IdOrNull(int? id) => id is int value ? value : DBNull.Value;

    private void SyncCoverItem(AlbumDocument album, SqliteTransaction transaction)
    {
        if (album.CoverItemId is int coverId && !ItemBelongsToAlbum(coverId, album.Id, transaction))
            album.CoverItemId = null;

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Albums SET CoverItemId = $cover WHERE Id = $id";
        command.Parameters.AddWithValue("$cover", album.CoverItemId is int id ? id : DBNull.Value);
        command.Parameters.AddWithValue("$id", album.Id);
        command.ExecuteNonQuery();
    }

    private void DeleteMissingItems(int albumId, IReadOnlyList<int> kept, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        if (kept.Count == 0)
        {
            command.CommandText = "DELETE FROM Items WHERE AlbumId = $album";
            command.Parameters.AddWithValue("$album", albumId);
            command.ExecuteNonQuery();
            return;
        }

        var names = new string[kept.Count];
        for (var index = 0; index < kept.Count; index++)
        {
            names[index] = "$k" + index;
            command.Parameters.AddWithValue(names[index], kept[index]);
        }

        command.CommandText = $"DELETE FROM Items WHERE AlbumId = $album AND Id NOT IN ({string.Join(", ", names)})";
        command.Parameters.AddWithValue("$album", albumId);
        command.ExecuteNonQuery();
    }

    private List<string> ReplaceAlbumTags(int albumId, IReadOnlyList<string> names, SqliteTransaction transaction)
    {
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM AlbumTags WHERE AlbumId = $id";
            delete.Parameters.AddWithValue("$id", albumId);
            delete.ExecuteNonQuery();
        }

        var canonical = new List<string>();
        foreach (var name in DistinctNames(names))
        {
            var entry = Resolve(EntityKind.Tag, name, transaction);
            using var link = _connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT INTO AlbumTags (AlbumId, TagId) VALUES ($owner, $entity)";
            link.Parameters.AddWithValue("$owner", albumId);
            link.Parameters.AddWithValue("$entity", entry.Id);
            link.ExecuteNonQuery();
            canonical.Add(entry.Name);
        }

        canonical.Sort(StringComparer.CurrentCultureIgnoreCase);
        return canonical;
    }

    private List<string> ReplaceItemNames(int itemId, IReadOnlyList<string> names, EntityKind kind, SqliteTransaction transaction)
    {
        var junction = kind == EntityKind.Tag ? "ItemTags" : "ItemPeople";
        var column = kind == EntityKind.Tag ? "TagId" : "PersonId";
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {junction} WHERE ItemId = $id";
            delete.Parameters.AddWithValue("$id", itemId);
            delete.ExecuteNonQuery();
        }

        var canonical = new List<string>();
        foreach (var name in DistinctNames(names))
        {
            var entry = Resolve(kind, name, transaction);
            using var link = _connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = $"INSERT INTO {junction} (ItemId, {column}) VALUES ($owner, $entity)";
            link.Parameters.AddWithValue("$owner", itemId);
            link.Parameters.AddWithValue("$entity", entry.Id);
            link.ExecuteNonQuery();
            canonical.Add(entry.Name);
        }

        canonical.Sort(StringComparer.CurrentCultureIgnoreCase);
        return canonical;
    }

    private CatalogEntry Resolve(EntityKind kind, string name, SqliteTransaction transaction)
    {
        var trimmed = RequireName(name);
        var table = Table(kind);
        using var find = _connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = $"SELECT Id, Name FROM {table} WHERE Name = $name COLLATE NOCASE";
        find.Parameters.AddWithValue("$name", trimmed);
        using (var reader = find.ExecuteReader())
        {
            if (reader.Read())
                return new CatalogEntry { Id = reader.GetInt32(0), Name = reader.GetString(1) };
        }

        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {table} (Name) VALUES ($name)";
        insert.Parameters.AddWithValue("$name", trimmed);
        insert.ExecuteNonQuery();
        return new CatalogEntry { Id = LastInsertId(transaction), Name = trimmed };
    }

    private List<string> LoadNames(string sql, int id)
    {
        var names = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private Dictionary<int, List<string>> LoadOwnerNames(string sql, int? id = null)
    {
        var map = new Dictionary<int, List<string>>();
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (id is int ownerId)
            command.Parameters.AddWithValue("$id", ownerId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var owner = reader.GetInt32(0);
            if (!map.TryGetValue(owner, out var names))
            {
                names = [];
                map[owner] = names;
            }

            names.Add(reader.GetString(1));
        }

        return map;
    }

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private int LastInsertId(SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_insert_rowid()";
        return (int)(long)command.ExecuteScalar()!;
    }

    private IReadOnlyList<CatalogEntry> ListEntities(EntityKind kind)
    {
        lock (_gate)
        {
            var entries = new List<CatalogEntry>();
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT Id, Name FROM {Table(kind)} ORDER BY Name COLLATE NOCASE";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                entries.Add(new CatalogEntry { Id = reader.GetInt32(0), Name = reader.GetString(1) });
            return entries;
        }
    }

    private CatalogEntry AddEntity(EntityKind kind, string name)
    {
        var trimmed = RequireName(name);
        lock (_gate)
        {
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = $"INSERT INTO {Table(kind)} (Name) VALUES ($name)";
                command.Parameters.AddWithValue("$name", trimmed);
                command.ExecuteNonQuery();
                using var id = _connection.CreateCommand();
                id.CommandText = "SELECT last_insert_rowid()";
                return new CatalogEntry { Id = (int)(long)id.ExecuteScalar()!, Name = trimmed };
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException($"\"{trimmed}\" already exists.");
            }
        }
    }

    private void RenameEntity(EntityKind kind, int id, string name)
    {
        var trimmed = RequireName(name);
        lock (_gate)
        {
            try
            {
                var updated = Execute($"UPDATE {Table(kind)} SET Name = $name WHERE Id = $id", ("$name", trimmed), ("$id", id));
                if (updated == 0)
                    throw new InvalidOperationException("That entry is no longer in the database.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException($"\"{trimmed}\" already exists.");
            }
        }
    }

    private void DeleteEntity(EntityKind kind, int id)
    {
        lock (_gate)
        {
            var deleted = Execute($"DELETE FROM {Table(kind)} WHERE Id = $id", ("$id", id));
            if (deleted == 0)
                throw new InvalidOperationException("That entry is no longer in the database.");
        }
    }

    private static string Table(EntityKind kind) => kind switch
    {
        EntityKind.Tag => "Tags",
        EntityKind.Person => "People",
        EntityKind.Location => "Locations",
        _ => "Owners"
    };

    private static string RequireName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("A name is required.");
        return trimmed;
    }

    private static IEnumerable<string> DistinctNames(IReadOnlyList<string>? names)
    {
        if (names is null)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var trimmed = name?.Trim() ?? "";
            if (trimmed.Length == 0 || !seen.Add(trimmed))
                continue;
            yield return trimmed;
        }
    }

    private enum EntityKind
    {
        Tag,
        Person,
        Location,
        Owner
    }

    private sealed class EntityCatalog : INamedCatalog
    {
        private readonly SqliteAlbumStore _store;
        private readonly EntityKind _kind;

        public EntityCatalog(SqliteAlbumStore store, EntityKind kind)
        {
            _store = store;
            _kind = kind;
        }

        public IReadOnlyList<CatalogEntry> List() => _store.ListEntities(_kind);

        public CatalogEntry Add(string name) => _store.AddEntity(_kind, name);

        public void Rename(int id, string name) => _store.RenameEntity(_kind, id, name);

        public void Delete(int id) => _store.DeleteEntity(_kind, id);
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HentaiReader.Host.Data;

/// <summary>
/// The local database of Constitution V (specs/006-windows-library/data-model.md): library folders,
/// saved archives, history, progress, per-gallery reader settings and downloads in one SQLite file.
/// The schema version lives in <c>PRAGMA user_version</c>. Connections are pooled; every call opens
/// its own, so repositories can be used from any thread (never from the UI thread: callers await).
/// </summary>
public sealed class LibraryDatabase
{
    public const int SchemaVersion = 1;

    private readonly string _connectionString;

    private LibraryDatabase(string path)
    {
        Path = path;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public string Path { get; }

    /// <summary>Opens or creates the database and brings the schema up to date.</summary>
    public static async Task<LibraryDatabase> OpenAsync(string path, CancellationToken token = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var db = new LibraryDatabase(path);
        await using var connection = await db.ConnectAsync(token);
        await Execute(connection, "PRAGMA journal_mode = WAL;", token);
        var version = Convert.ToInt32(await Scalar(connection, "PRAGMA user_version;", token));
        if (version > SchemaVersion) throw new InvalidOperationException($"library.db has schema {version}, this app knows {SchemaVersion}");
        if (version < 1)
        {
            await using var tx = await connection.BeginTransactionAsync(token);
            await Execute(connection, Schema1, token);
            await Execute(connection, $"PRAGMA user_version = {SchemaVersion};", token);
            await tx.CommitAsync(token);
        }
        return db;
    }

    public async Task<SqliteConnection> ConnectAsync(CancellationToken token = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(token);
        return connection;
    }

    /// <summary>
    /// Imports the 005 <c>progress.json</c> (key "source|gallery" → progress) into progress and history,
    /// then renames it to <c>progress.json.imported</c>. Does nothing when the file is absent.
    /// </summary>
    public async Task<int> ImportProgressJsonAsync(string file, CancellationToken token = default)
    {
        if (!File.Exists(file)) return 0;
        Dictionary<string, JsonElement>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(file, token));
        }
        catch (JsonException)
        {
            entries = null;
        }
        var imported = 0;
        await using (var connection = await ConnectAsync(token))
        await using (var tx = await connection.BeginTransactionAsync(token))
        {
            foreach (var (key, value) in entries ?? [])
            {
                var bar = key.IndexOf('|');
                if (bar <= 0 || value.ValueKind != JsonValueKind.Object) continue;
                var (source, gallery) = (key[..bar], key[(bar + 1)..]);
                var page = Int(value, "Page");
                var count = Int(value, "PageCount");
                var updated = Str(value, "UpdatedAt") ?? DateTimeOffset.UtcNow.ToString("O");
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT OR IGNORE INTO progress (source_id, gallery_id, page, offset, page_count, finished, updated_at)
                        VALUES ($s, $g, $p, $o, $c, $f, $u);
                        INSERT OR IGNORE INTO history (source_id, gallery_id, title, cover_file, page_count, last_read_at)
                        VALUES ($s, $g, $t, NULL, $c, $u);
                        """;
                    cmd.Parameters.AddWithValue("$s", source);
                    cmd.Parameters.AddWithValue("$g", gallery);
                    cmd.Parameters.AddWithValue("$p", page);
                    cmd.Parameters.AddWithValue("$o", value.TryGetProperty("Offset", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetDouble() : 0);
                    cmd.Parameters.AddWithValue("$c", count);
                    cmd.Parameters.AddWithValue("$f", value.TryGetProperty("Read", out var r) && r.ValueKind == JsonValueKind.True ? 1 : 0);
                    cmd.Parameters.AddWithValue("$u", updated);
                    cmd.Parameters.AddWithValue("$t", Str(value, "Title") ?? "");
                    await cmd.ExecuteNonQueryAsync(token);
                }
                imported++;
            }
            await tx.CommitAsync(token);
        }
        File.Move(file, file + ".imported", overwrite: true);
        return imported;

        static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    internal static async Task Execute(SqliteConnection connection, string sql, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(token);
    }

    private static async Task<object?> Scalar(SqliteConnection connection, string sql, CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(token);
    }

    private const string Schema1 = """
        CREATE TABLE library_folder (
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            is_target INTEGER NOT NULL DEFAULT 0,
            added_at TEXT NOT NULL
        );
        CREATE TABLE archive (
            id INTEGER PRIMARY KEY,
            folder_id INTEGER NOT NULL REFERENCES library_folder(id) ON DELETE CASCADE,
            rel_path TEXT NOT NULL COLLATE NOCASE,
            size INTEGER NOT NULL,
            modified_at TEXT NOT NULL,
            source_id TEXT NOT NULL,
            gallery_id TEXT NOT NULL,
            title TEXT NOT NULL,
            title_original TEXT,
            language TEXT,
            type TEXT,
            tags_json TEXT NOT NULL DEFAULT '[]',
            page_count INTEGER NOT NULL,
            pages_json TEXT NOT NULL DEFAULT '[]',
            saved_at TEXT NOT NULL,
            state TEXT NOT NULL DEFAULT 'ok',
            format TEXT NOT NULL,
            metadata_dirty INTEGER NOT NULL DEFAULT 0,
            UNIQUE (folder_id, rel_path)
        );
        CREATE INDEX archive_identity ON archive (source_id, gallery_id);
        CREATE INDEX archive_title ON archive (title COLLATE NOCASE);
        CREATE INDEX archive_saved ON archive (saved_at);
        CREATE TABLE history (
            source_id TEXT NOT NULL,
            gallery_id TEXT NOT NULL,
            title TEXT NOT NULL,
            cover_file TEXT,
            page_count INTEGER NOT NULL,
            last_read_at TEXT NOT NULL,
            PRIMARY KEY (source_id, gallery_id)
        );
        CREATE INDEX history_last_read ON history (last_read_at DESC);
        CREATE TABLE progress (
            source_id TEXT NOT NULL,
            gallery_id TEXT NOT NULL,
            page INTEGER NOT NULL,
            offset REAL NOT NULL,
            page_count INTEGER NOT NULL,
            finished INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (source_id, gallery_id)
        );
        CREATE TABLE reader_settings (
            source_id TEXT NOT NULL,
            gallery_id TEXT NOT NULL,
            zoom REAL NOT NULL,
            fit_width INTEGER NOT NULL,
            gaps INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (source_id, gallery_id)
        );
        CREATE TABLE download (
            id INTEGER PRIMARY KEY,
            source_id TEXT NOT NULL,
            gallery_id TEXT NOT NULL,
            title TEXT NOT NULL,
            gallery_json TEXT NOT NULL,
            settings_json TEXT NOT NULL DEFAULT '{}',
            folder_id INTEGER NOT NULL REFERENCES library_folder(id),
            state TEXT NOT NULL,
            page_count INTEGER NOT NULL,
            pages_done INTEGER NOT NULL DEFAULT 0,
            error_kind TEXT,
            error_message TEXT,
            retry_after TEXT,
            queued_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE download_page (
            download_id INTEGER NOT NULL REFERENCES download(id) ON DELETE CASCADE,
            page INTEGER NOT NULL,
            file TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            PRIMARY KEY (download_id, page)
        );
        """;
}

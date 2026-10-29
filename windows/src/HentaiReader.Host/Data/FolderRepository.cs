namespace HentaiReader.Host.Data;

/// <summary>Library folders; exactly one is the target for new saves (FR-015).</summary>
public sealed class FolderRepository(LibraryDatabase db)
{
    public async Task<IReadOnlyList<LibraryFolder>> ListAsync(CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("SELECT id, path, is_target, added_at FROM library_folder ORDER BY id");
        await using var r = await cmd.ExecuteReaderAsync(token);
        var list = new List<LibraryFolder>();
        while (await r.ReadAsync(token)) list.Add(new LibraryFolder(r.GetInt64(0), r.GetString(1), r.GetInt32(2) != 0, Sql.ParseTime(r.GetString(3))));
        return list;
    }

    /// <summary>Adds a folder (normalised path); the first folder becomes the target. Returns the existing row for a known path.</summary>
    public async Task<LibraryFolder> AddAsync(string path, DateTimeOffset at, CancellationToken token = default)
    {
        var normalised = Normalise(path);
        await using var c = await db.ConnectAsync(token);
        await using (var cmd = c.Command("""
            INSERT INTO library_folder (path, is_target, added_at)
            VALUES ($p, NOT EXISTS (SELECT 1 FROM library_folder), $a)
            ON CONFLICT (path) DO NOTHING
            """, ("$p", normalised), ("$a", Sql.Time(at))))
        {
            await cmd.ExecuteNonQueryAsync(token);
        }
        return (await ListAsync(token)).First(f => string.Equals(f.Path, normalised, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SetTargetAsync(long id, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var cmd = c.Command("UPDATE library_folder SET is_target = (id = $id) WHERE EXISTS (SELECT 1 FROM library_folder WHERE id = $id)", ("$id", id));
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Removes a folder and its archive rows (files are not touched). The last folder cannot be removed;
    /// when the target goes, the oldest remaining folder becomes the target.
    /// </summary>
    public async Task RemoveAsync(long id, CancellationToken token = default)
    {
        await using var c = await db.ConnectAsync(token);
        await using var tx = await c.BeginTransactionAsync(token);
        await using (var count = c.Command("SELECT COUNT(*) FROM library_folder"))
        {
            if (Convert.ToInt32(await count.ExecuteScalarAsync(token)) <= 1) throw new InvalidOperationException("the last library folder cannot be removed");
        }
        await using (var cmd = c.Command("""
            DELETE FROM library_folder WHERE id = $id;
            UPDATE library_folder SET is_target = 1
            WHERE NOT EXISTS (SELECT 1 FROM library_folder WHERE is_target = 1) AND id = (SELECT MIN(id) FROM library_folder);
            """, ("$id", id)))
        {
            await cmd.ExecuteNonQueryAsync(token);
        }
        await tx.CommitAsync(token);
    }

    public static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

// SPDX-FileCopyrightText: 2026 coderkei
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace DbRoot;

public sealed class DbRootSession
{
    public string Root { get; }
    private readonly Dictionary<string, byte[]> original = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> staged = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CatalogRow>? songCache;
    private Dictionary<string, CatalogRow>? songLookup;
    public List<string> Favorites { get; private set; }
    public bool Dirty { get; private set; }
    public int Capacity => original["db_like.db"].Length / 319;
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string ResolveRoot(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(Path.Combine(path, "db_audio.db")) && File.Exists(Path.Combine(path, "db_like.db"))) return path;
        var child = Path.Combine(path, "DBROOT");
        if (File.Exists(Path.Combine(child, "db_audio.db")) && File.Exists(Path.Combine(child, "db_like.db"))) return child;
        throw new InvalidDataException("Choose a DBROOT folder containing db_audio.db and db_like.db, or its parent.");
    }
    public DbRootSession(string path)
    {
        Root = ResolveRoot(path);
        foreach (var file in Directory.GetFiles(Root, "db*.db")) original.Add(Path.GetFileName(file), File.ReadAllBytes(file));
        _ = Rows("db_audio.db");
        Favorites = Rows("db_like.db").Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    public byte[] Bytes(string name) => (byte[])(staged.GetValueOrDefault(name) ?? original[name]).Clone();
    public IEnumerable<string> Names => original.Keys.Order(StringComparer.OrdinalIgnoreCase);
    public List<CatalogRow> Rows(string name) => CatalogCodec.Read(CatalogDefinition.All.Single(d => d.FileName == name), Bytes(name));
    public IReadOnlyList<CatalogRow> Songs => songCache ??= Rows("db_audio.db").DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
    public CatalogRow? Song(string path) => (songLookup ??= Songs.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase)).GetValueOrDefault(path);
    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    public void SetFavorites(IEnumerable<string> paths)
    {
        var result = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (result.Count > Capacity) throw new InvalidDataException($"This database has only {Capacity} favourite slots.");
        Favorites = result;
        Dirty = true;
    }
    public CatalogRow? Match(string value)
    {
        if (Song(value) is { } exact) return exact;
        if (Path.IsPathFullyQualified(value))
        {
            var drive = Path.GetPathRoot(Root)!;
            if (value.StartsWith(drive, StringComparison.OrdinalIgnoreCase) && Song(@"1:\" + value[drive.Length..]) is { } onCard) return onCard;
        }
        var matches = Songs.Where(s => Same(s.FileName, Path.GetFileName(value))).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
    public string PlayerPath(string file)
    {
        var drive = Path.GetPathRoot(Root)!;
        file = Path.GetFullPath(file);
        if (!file.StartsWith(drive, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a file on the selected card.");
        return @"1:\" + file[drive.Length..];
    }
    public void EnsureEditable(string name)
    {
        var definition = CatalogDefinition.All.Single(d => d.FileName == name);
        if (name == "db_like.db") throw new InvalidOperationException("Manage favourites on the Favourites tab.");
        if (Rows(name).Count != Bytes(name).Length / definition.RecordSize)
            throw new InvalidDataException("This catalog has empty or unrecognised rows. Editing would discard unknown data.");
    }
    public void SetRows(string name, List<CatalogRow> rows)
    {
        EnsureEditable(name);
        var bytes = CatalogCodec.Write(CatalogDefinition.All.Single(d => d.FileName == name), rows);
        staged[name] = bytes;
        if (Same(name, "db_audio.db")) { songCache = null; songLookup = null; }
        Dirty = true;
    }
    public void RequireCardSong(string path)
    {
        if (!CatalogCodec.IsSong(path)) throw new InvalidDataException(@"Song paths must start with 1:\Music\.");
        var relative = path[3..];
        var drive = Path.GetPathRoot(Root)!;
        var file = Path.GetFullPath(Path.Combine(drive, relative));
        if (!file.StartsWith(drive, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            throw new InvalidDataException("The new song path must point to a file on the selected card.");
    }
    public Dictionary<string, byte[]> PrepareSave()
    {
        if (Favorites.Count > Capacity) throw new InvalidDataException("Too many favourites for this database.");
        var audio = Bytes("db_audio.db");
        var songs = Songs.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
        var likes = Bytes("db_like.db");
        for (var index = 0; index < Favorites.Count; index++)
        {
            if (!songs.TryGetValue(Favorites[index], out var song)) throw new InvalidDataException($"Remove or resolve the missing favourite: {Favorites[index]}");
            Array.Copy(audio, song.Record * 351, likes, index * 319, 319);
        }
        // The player stops at the first empty slot; preserve the remaining unused bytes.
        if (Favorites.Count < Capacity) Array.Clear(likes, Favorites.Count * 319, 319);
        var updates = new Dictionary<string, byte[]>(staged, StringComparer.OrdinalIgnoreCase) { ["db_like.db"] = likes };
        if (original.ContainsKey("db_content.db"))
        {
            var content = Bytes("db_content.db");
            // All supplied player snapshots use this header and keep the favourites
            // count here. Preserve the rest of this device state verbatim.
            if (content.Length != 696 || BinaryPrimitives.ReadUInt32LittleEndian(content) != 0x0100030F)
                throw new InvalidDataException("Unrecognised db_content.db format. Cannot safely update the player's favourites count.");
            BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(0x30, 4), (uint)Favorites.Count);
            if (!content.SequenceEqual(original["db_content.db"])) updates["db_content.db"] = content;
        }
        return updates;
    }
    public string BackupPath(string action) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBROOT Manager", "Backups",
        $"DBROOT-backup-before-{action}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.zip");
    public void Save(string backup, CancellationToken cancellation = default)
    {
        var updates = PrepareSave();
        WriteProtected(updates, backup, cancellation);
        foreach (var pair in updates) original[pair.Key] = pair.Value;
        staged.Clear();
        Dirty = false;
    }
    public int RecoverExistingFavorites(string backup, CancellationToken cancellation = default)
    {
        if (Dirty) throw new InvalidOperationException("Save or reload your staged changes before recovering the favourites already on the card.");
        cancellation.ThrowIfCancellationRequested();
        // A player scan may have changed the card since this session was opened.
        // Read the surviving records afresh, independently of the stored count.
        var current = new DbRootSession(Root);
        if (!current.original.ContainsKey("db_content.db"))
            throw new InvalidDataException("db_content.db is missing. Cannot synchronise the player's favourites count.");
        if (current.Favorites.Count == 0)
            throw new InvalidDataException("No existing favourites remain in db_like.db to recover.");
        current.SetFavorites(current.Favorites);
        current.Save(backup, cancellation);
        original.Clear();
        foreach (var pair in current.original) original.Add(pair.Key, pair.Value);
        songCache = null; songLookup = null;
        Favorites = current.Favorites.ToList();
        return Favorites.Count;
    }
    public void EditBytes(string name, int offset, byte[] replacement, string backup, CancellationToken cancellation = default)
    {
        if (Dirty) throw new InvalidOperationException("Save or reload the staged edits before editing raw bytes.");
        var bytes = Bytes(name);
        if (offset < 0 || replacement.Length == 0 || offset > bytes.Length - replacement.Length) throw new InvalidDataException("The edit extends beyond the file.");
        replacement.CopyTo(bytes, offset);
        WriteProtected(new(StringComparer.OrdinalIgnoreCase) { [name] = bytes }, backup, cancellation);
    }
    public void Restore(string archive, string backup, CancellationToken cancellation = default)
    {
        if (Dirty) throw new InvalidOperationException("Save or reload staged edits before restoring.");
        var updates = ReadBackup(archive);
        // Reject a backup that would prevent the editor from reloading the required catalogs.
        _ = CatalogCodec.Read(CatalogDefinition.All[0], updates["db_audio.db"]);
        _ = CatalogCodec.Read(CatalogDefinition.All[1], updates["db_like.db"]);
        WriteProtected(updates, backup, cancellation);
    }
    private void Guard()
    {
        var names = Directory.GetFiles(Root, "db*.db").Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.SetEquals(original.Keys)) throw new IOException("The DBROOT file set changed. Reload before writing.");
        foreach (var pair in original)
            if (Hash(File.ReadAllBytes(Path.Combine(Root, pair.Key))) != Hash(pair.Value))
                throw new IOException($"{pair.Key} changed after loading. Reload so the player rescan is not overwritten.");
    }
    private void WriteProtected(Dictionary<string, byte[]> updates, string backup, CancellationToken cancellation)
    {
        Guard();
        cancellation.ThrowIfCancellationRequested();
        Backup(backup, cancellation);
        var verified = ReadBackup(backup);
        if (verified.Count != original.Count || original.Any(p => !verified.TryGetValue(p.Key, out var b) || Hash(b) != Hash(p.Value)))
            throw new IOException("The safety backup does not match the opened DBROOT. No changes were written.");
        Guard();
        var attempted = new List<string>();
        var temps = new List<string>();
        try
        {
            foreach (var pair in updates.OrderBy(p => p.Key))
            {
                cancellation.ThrowIfCancellationRequested();
                var target = Path.Combine(Root, pair.Key);
                // Same-sized database edits must retain the existing file allocation:
                // replacing via a temporary file can change FAT cluster references.
                if (File.Exists(target) && original.TryGetValue(pair.Key, out var existing) && existing.Length == pair.Value.Length)
                {
                    if (Hash(File.ReadAllBytes(target)) != Hash(existing)) throw new IOException($"{pair.Key} changed during the operation.");
                    attempted.Add(pair.Key);
                    WriteInPlace(target, pair.Value);
                    if (Hash(File.ReadAllBytes(target)) != Hash(pair.Value)) throw new IOException($"Write verification failed: {pair.Key}");
                    continue;
                }
                var temp = target + $".{Guid.NewGuid():N}.tmp";
                temps.Add(temp);
                File.WriteAllBytes(temp, pair.Value);
                // Recheck each target immediately before replacement.
                if (original.TryGetValue(pair.Key, out var old) ? !File.Exists(target) || Hash(File.ReadAllBytes(target)) != Hash(old) : File.Exists(target))
                    throw new IOException($"{pair.Key} changed during the operation.");
                if (Directory.Exists(target)) throw new IOException($"A directory is blocking the database file: {pair.Key}");
                attempted.Add(pair.Key);
                if (File.Exists(target))
                {
                    try { File.Replace(temp, target, null); }
                    catch (PlatformNotSupportedException) { File.Copy(temp, target, true); }
                    catch (IOException) when (File.Exists(temp)) { File.Copy(temp, target, true); }
                    catch (UnauthorizedAccessException) when (File.Exists(temp)) { File.Copy(temp, target, true); }
                }
                else File.Move(temp, target);
                if (Hash(File.ReadAllBytes(target)) != Hash(pair.Value)) throw new IOException($"Write verification failed: {pair.Key}");
            }
        }
        catch (Exception error)
        {
            var failures = new List<string>();
            foreach (var name in attempted.AsEnumerable().Reverse())
            {
                try
                {
                    var target = Path.Combine(Root, name);
                    if (original.TryGetValue(name, out var bytes))
                    {
                        if (File.Exists(target) && new FileInfo(target).Length == bytes.Length) WriteInPlace(target, bytes);
                        else File.WriteAllBytes(target, bytes);
                        if (Hash(File.ReadAllBytes(target)) != Hash(bytes)) throw new IOException("Rollback verification failed.");
                    }
                    else File.Delete(target);
                }
                catch (Exception rollback) { failures.Add($"{name}: {rollback.Message}"); }
            }
            throw new IOException($"{error.Message}\n{(failures.Count == 0 ? "Original files restored." : "Rollback failed: " + string.Join("\n", failures))}\nSafety backup: {backup}", error);
        }
        finally
        {
            foreach (var temp in temps) { try { File.Delete(temp); } catch (IOException) { } }
        }
    }
    private static void WriteInPlace(string target, byte[] bytes)
    {
        var created = File.GetCreationTimeUtc(target);
        var accessed = File.GetLastAccessTimeUtc(target);
        var written = File.GetLastWriteTimeUtc(target);
        using (var stream = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            if (stream.Length != bytes.Length) throw new IOException("Database size changed before writing.");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.SetCreationTimeUtc(target, created);
        File.SetLastAccessTimeUtc(target, accessed);
        File.SetLastWriteTimeUtc(target, written);
    }
    public void Backup(string destination, CancellationToken cancellation = default)
    {
        EnsureOutsideRoot(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        // CreateNew avoids destroying an existing backup.
        using (var file = new FileStream(destination, FileMode.CreateNew))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var metadata = new List<object>();
            foreach (var name in Names)
            {
                cancellation.ThrowIfCancellationRequested();
                var bytes = File.ReadAllBytes(Path.Combine(Root, name));
                using (var stream = zip.CreateEntry(name).Open()) stream.Write(bytes);
                metadata.Add(new { name, size = bytes.Length, sha256 = Hash(bytes) });
            }
            using var manifest = zip.CreateEntry("dbroot-manifest.json").Open();
            JsonSerializer.Serialize(manifest, new { format = "dbroot-backup", version = 1, createdUtc = DateTime.UtcNow, files = metadata }, JsonOptions);
        }
        _ = ReadBackup(destination);
    }
    public void EnsureOutsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Same(full, Root))
            throw new InvalidOperationException("Choose a destination outside the active DBROOT folder.");
    }
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static Dictionary<string, byte[]> ReadBackup(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifestEntries = zip.Entries.Where(e => e.FullName == "dbroot-manifest.json").ToArray();
        if (manifestEntries.Length != 1) throw new InvalidDataException("The ZIP needs exactly one DBROOT backup manifest.");
        using var manifestStream = manifestEntries[0].Open();
        using var document = JsonDocument.Parse(manifestStream);
        var manifest = document.RootElement;
        if (manifest.GetProperty("format").GetString() != "dbroot-backup" || manifest.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported backup format.");
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.GetProperty("files").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            if (name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !name.StartsWith("db", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || result.ContainsKey(name))
                throw new InvalidDataException($"Unsafe or duplicate backup filename: {name}");
            var entries = zip.Entries.Where(e => Same(e.FullName, name)).ToArray();
            if (entries.Length != 1) throw new InvalidDataException($"Missing or duplicate ZIP entry: {name}");
            if (entries[0].Length != item.GetProperty("size").GetInt64() || entries[0].Length > int.MaxValue) throw new InvalidDataException($"Wrong size: {name}");
            using var input = entries[0].Open();
            using var memory = new MemoryStream();
            input.CopyTo(memory);
            var bytes = memory.ToArray();
            if (!Same(Hash(bytes), item.GetProperty("sha256").GetString() ?? "")) throw new InvalidDataException($"Wrong checksum: {name}");
            result.Add(name, bytes);
        }
        if (!result.ContainsKey("db_audio.db") || !result.ContainsKey("db_like.db")) throw new InvalidDataException("Backup needs both db_audio.db and db_like.db.");
        return result;
    }
}

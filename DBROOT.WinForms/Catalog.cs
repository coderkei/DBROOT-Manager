// SPDX-FileCopyrightText: 2026 coderkei
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;

namespace DbRoot;

public sealed record CatalogDefinition(string FileName, string Label, int RecordSize, int ExtraLength = 0)
{
    public static readonly CatalogDefinition[] All =
    [new("db_audio.db", "Songs", 351), new("db_like.db", "Favourites", 319),
     new("db_image.db", "Images", 319), new("db_ebook.db", "E-books", 324, 5),
     new("db_folder0.db", "Root folders", 324, 5)];
    public override string ToString() => $"{Label} ({FileName})";
}

public sealed record CatalogRow(int Record, string Path, string StoredTitle, string Album = "", string Artist = "", string Extra = "", byte[]? OriginalBytes = null)
{
    public string Title => string.IsNullOrWhiteSpace(StoredTitle) ? FileName : StoredTitle;
    public string FileName => System.IO.Path.GetFileName(Path);
}

public static class CatalogCodec
{
    public static string Hex(ReadOnlySpan<byte> bytes) => BitConverter.ToString(bytes.ToArray()).Replace('-', ' ');
    public static string ReadText(byte[] bytes, int offset, int length)
    {
        var end = Array.IndexOf(bytes, (byte)0, offset, length);
        return Encoding.UTF8.GetString(bytes, offset, end < 0 ? length : end - offset);
    }

    public static List<CatalogRow> Read(CatalogDefinition definition, byte[] bytes)
    {
        if (bytes.Length % definition.RecordSize != 0)
            throw new InvalidDataException($"{definition.FileName} is not the recognised {definition.RecordSize}-byte record format. Use the raw file viewer.");
        var rows = new List<CatalogRow>();
        for (var offset = 0; offset < bytes.Length; offset += definition.RecordSize)
        {
            var path = ReadText(bytes, offset, 255);
            if (definition.FileName == "db_like.db" && !IsSong(path)) break;
            if (string.IsNullOrWhiteSpace(path) || definition.FileName == "db_audio.db" && !IsSong(path)) continue;
            rows.Add(new(offset / definition.RecordSize, path, ReadText(bytes, offset + 255, 64),
                definition.FileName == "db_audio.db" ? ReadText(bytes, offset + 319, 16) : "",
                definition.FileName == "db_audio.db" ? ReadText(bytes, offset + 335, 16) : "",
                definition.ExtraLength > 0 ? Hex(bytes.AsSpan(offset + 319, definition.ExtraLength)) : "",
                bytes.AsSpan(offset, definition.RecordSize).ToArray()));
        }
        return rows;
    }

    public static bool IsSong(string path) => path.StartsWith(@"1:\Music\", StringComparison.OrdinalIgnoreCase);

    public static byte[] HexBytes(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length != 2)) throw new FormatException("Enter two-digit hexadecimal pairs separated by spaces.");
        return parts.Select(p => byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
    }

    public static byte[] Write(CatalogDefinition definition, IReadOnlyList<CatalogRow> rows)
    {
        if (definition.FileName == "db_like.db") throw new InvalidOperationException("Manage favourites on the Favourites tab.");
        var result = new byte[checked(rows.Count * definition.RecordSize)];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (string.IsNullOrWhiteSpace(row.Path) || !seen.Add(row.Path)) throw new InvalidDataException("Each row needs a unique path.");
            if (definition.FileName == "db_audio.db" && !IsSong(row.Path)) throw new InvalidDataException(@"Song paths must begin with 1:\Music\.");
            var offset = index * definition.RecordSize;
            if (row.OriginalBytes is { } original && original.Length == definition.RecordSize) original.CopyTo(result, offset);
            Field(result, offset, 255, row.Path, "Path");
            Field(result, offset + 255, 64, row.StoredTitle, "Title");
            if (definition.FileName == "db_audio.db")
            {
                Field(result, offset + 319, 16, row.Album, "Album");
                Field(result, offset + 335, 16, row.Artist, "Artist");
            }
            if (definition.ExtraLength > 0)
            {
                var extra = HexBytes(row.Extra);
                if (extra.Length != definition.ExtraLength) throw new InvalidDataException($"Device bytes must contain exactly {definition.ExtraLength} bytes.");
                extra.CopyTo(result, offset + 319);
            }
        }
        return result;
    }

    private static void Field(byte[] result, int offset, int size, string value, string name)
    {
        // Preserve untouched device fields, including non-UTF8 bytes and trailing padding.
        if (ReadText(result, offset, size) == value) return;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (value.Contains('\0') || bytes.Length > size) throw new InvalidDataException($"{name} must contain no null characters and fit in {size} UTF-8 bytes (currently {bytes.Length}).");
        Array.Clear(result, offset, size);
        bytes.CopyTo(result, offset);
    }
}

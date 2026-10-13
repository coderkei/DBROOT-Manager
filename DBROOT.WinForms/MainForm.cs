// SPDX-FileCopyrightText: 2026 coderkei
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbRoot;

public sealed class MainForm : Form
{
    private DbRootSession? session;
    private readonly ComboBox drives = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly ComboBox catalogs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly TextBox filter = new() { Width = 300, PlaceholderText = "Filter catalog…" };
    private readonly Label rootLabel = new() { AutoSize = true, Padding = new(8) };
    private readonly Label catalogHint = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new(6) };
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar progress = new() { Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly ToolStripButton cancel = new("Cancel operation") { Visible = false };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly ListView favorites = List("Title", "Filename", "Status", "Player path");
    private readonly ListView catalog = List("Record", "Title", "Album", "Artist", "Player path", "Device bytes");
    private readonly ListView files = List("Name", "Size", "Records", "SHA-256");
    private readonly ListView strings = List("Offset", "Readable string / path");
    private readonly TextBox hex = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new("Consolas", 10) };
    private readonly Label details = new() { Dock = DockStyle.Top, Height = 56, Padding = new(6) };
    private readonly FlowLayoutPanel header = Bar();
    private readonly FlowLayoutPanel footer = Bar();
    private Button saveButton = null!;
    private byte[]? raw;
    private string? rawName;
    private CancellationTokenSource? operation;
    private bool refreshingDrives;

    public MainForm(string? initialFolder = null)
    {
        Text = "DBROOT Manager";
        Size = new(1250, 850);
        MinimumSize = new(950, 650);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new(8) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(rootLabel, 0, 1);
        layout.Controls.Add(tabs, 0, 2);
        layout.Controls.Add(footer, 0, 3);
        var strip = new StatusStrip { Dock = DockStyle.Fill };
        strip.Items.AddRange([status, progress, cancel]);
        layout.Controls.Add(strip, 0, 4);
        Controls.Add(layout);
        header.Controls.Add(drives);
        Button(header, "Refresh drives", RefreshDrives);
        Button(header, "Browse DBROOT…", () => { var path = PickFolder(); if (path != null && LeaveUnsaved()) Open(path); });
        Button(header, "Reload", () => { if (session != null && LeaveUnsaved()) Open(session.Root); });

        var favoritePage = Page("Favourites", favorites, out var favoriteBar);
        Button(favoriteBar, "Add songs…", () => AddFavorites(false));
        Button(favoriteBar, "Replace selected…", () => AddFavorites(true));
        Button(favoriteBar, "Remove selected", () =>
        {
            var remove = SelectedPaths(favorites).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (remove.Count > 0) { RequireSession().SetFavorites(RequireSession().Favorites.Where(p => !remove.Contains(p))); RefreshFavorites(); }
        });
        Button(favoriteBar, "Move up", () => MoveFavorite(-1));
        Button(favoriteBar, "Move down", () => MoveFavorite(1));
        Button(favoriteBar, "Import…", ImportFavorites);
        Button(favoriteBar, "Export…", ExportFavorites);
        Button(favoriteBar, "Recover existing favourites", async () => await RecoverFavoritesAsync());
        favorites.AllowDrop = true;
        favorites.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        favorites.DragDrop += (_, e) => Safe(() => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFavoriteFiles(paths); });

        var catalogPage = Page("Catalogs", catalog, out var catalogBar);
        catalogBar.Controls.Add(catalogs);
        catalogBar.Controls.Add(filter);
        catalogs.Items.AddRange(CatalogDefinition.All);
        Button(catalogBar, "Add row…", () => EditRow(true));
        Button(catalogBar, "Edit row…", () => EditRow(false));
        Button(catalogBar, "Delete row", DeleteRow);
        Button(catalogBar, "Add to favourites", () =>
        {
            if (Definition.FileName != "db_audio.db") throw new InvalidOperationException("Select the Songs catalog first.");
            RequireSession().SetFavorites(RequireSession().Favorites.Concat(SelectedPaths(catalog)));
            RefreshFavorites();
        });
        Button(catalogBar, "Export catalog…", ExportCatalog);
        catalogPage.Controls.Add(catalogHint);
        catalogs.SelectedIndexChanged += (_, _) => Safe(RefreshCatalog);
        filter.TextChanged += (_, _) => Safe(RefreshCatalog);
        catalog.DoubleClick += (_, _) => Safe(() => EditRow(false));
        catalogs.SelectedIndex = 0;

        var rawSplit = new SplitContainer { Size = new(1100, 600), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 240 };
        rawSplit.Panel1.Controls.Add(files);
        var lower = new SplitContainer { Size = new(1100, 300), Dock = DockStyle.Fill, SplitterDistance = 450 };
        lower.Panel1.Controls.Add(strings);
        lower.Panel2.Controls.Add(hex);
        rawSplit.Panel2.Controls.Add(lower);
        rawSplit.Panel2.Controls.Add(details);
        Page("DBROOT files", rawSplit, out var fileBar);
        Button(fileBar, "Back up all DB files…", async () => await Backup());
        Button(fileBar, "Restore backup…", async () => await Restore());
        Button(fileBar, "Compare DBROOT…", Compare);
        Button(fileBar, "Export inventory…", ExportInventory);
        Button(fileBar, "Export selected file…", ExportRaw);
        Button(fileBar, "Edit bytes…", async () => await EditRaw());
        files.SelectedIndexChanged += (_, _) => Safe(ShowRaw);
        strings.SelectedIndexChanged += (_, _) => { if (strings.SelectedItems.Count > 0 && raw != null) ShowHex((int)strings.SelectedItems[0].Tag!); };
        saveButton = Button(footer, "Save changes to card", async () => await SaveAsync());
        footer.Controls.Add(new Label { Text = "Catalog and favourite changes stay in memory until saved. Full backup before every write.", AutoSize = true, Padding = new(8) });
        cancel.Click += (_, _) => operation?.Cancel();
        drives.SelectedIndexChanged += (_, _) => Safe(() =>
        {
            if (!refreshingDrives && drives.SelectedItem is DriveChoice choice && choice.Root != "" && LeaveUnsaved()) Open(choice.Root);
        });
        FormClosing += (_, e) => { if (operation != null) { e.Cancel = true; operation.Cancel(); } else e.Cancel = !LeaveUnsaved(); };
        RefreshDrives();
        UpdateStatus("Choose a drive or browse to a DBROOT folder.");
        Shown += (_, _) => { if (initialFolder != null) Safe(() => Open(initialFolder)); };
    }

    private sealed record DriveChoice(string Root, string Label) { public override string ToString() => Label; }
    private static FlowLayoutPanel Bar() => new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new(3) };
    private static ListView List(params string[] columns)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, GridLines = true };
        foreach (var title in columns) list.Columns.Add(title, title.Contains("path", StringComparison.OrdinalIgnoreCase) || title.Contains("string") ? 460 : title is "Title" or "SHA-256" ? 270 : 140);
        list.AccessibleName = string.Join(", ", columns);
        return list;
    }
    private TabPage Page(string title, Control body, out FlowLayoutPanel bar)
    {
        var page = new TabPage(title) { Padding = new(6) };
        bar = Bar();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(bar, 0, 0);
        layout.Controls.Add(body, 0, 1);
        page.Controls.Add(layout);
        tabs.TabPages.Add(page);
        return page;
    }
    private Button Button(FlowLayoutPanel parent, string title, Action action)
    {
        var button = new Button { Text = title, AutoSize = true, Padding = new(6, 2, 6, 2), MinimumSize = new(80, 34), AccessibleName = title };
        button.Click += (_, _) => Safe(action);
        parent.Controls.Add(button);
        return button;
    }
    private Button Button(FlowLayoutPanel parent, string title, Func<Task> action)
    {
        var button = new Button { Text = title, AutoSize = true, Padding = new(6, 2, 6, 2), MinimumSize = new(80, 34), AccessibleName = title };
        button.Click += async (_, _) => { try { await action(); } catch (Exception e) { Error(e); } };
        parent.Controls.Add(button);
        return button;
    }
    private void Safe(Action action) { try { action(); } catch (Exception e) { Error(e); } }
    private void Error(Exception e) { MessageBox.Show(this, e.Message, "DBROOT operation failed", MessageBoxButtons.OK, MessageBoxIcon.Error); UpdateStatus("Operation failed; see the error message."); }
    private DbRootSession RequireSession() => session ?? throw new InvalidOperationException("Open a DBROOT folder first.");
    private CatalogDefinition Definition => (CatalogDefinition)catalogs.SelectedItem!;
    private static IEnumerable<string> SelectedPaths(ListView list) => list.SelectedItems.Cast<ListViewItem>().Select(i => ((CatalogRow)i.Tag!).Path);
    private bool Confirm(string text, string title) => MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    private string? PickFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Select DBROOT or its parent folder", ShowNewFolderButton = false, SelectedPath = session?.Root ?? "" };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }
    private string? PickSave(string title, string file, string filters)
    {
        using var dialog = new SaveFileDialog { Title = title, FileName = file, Filter = filters, AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        RequireSession().EnsureOutsideRoot(dialog.FileName);
        return dialog.FileName;
    }
    private void RefreshDrives()
    {
        refreshingDrives = true;
        try
        {
            drives.Items.Clear();
            drives.Items.Add(new DriveChoice("", "Select a drive…"));
            foreach (var drive in DriveInfo.GetDrives())
            {
                try { if (drive.IsReady) drives.Items.Add(new DriveChoice(drive.Name, $"{drive.Name}  {drive.DriveType}  {drive.VolumeLabel}")); }
                catch (IOException) { }
            }
            drives.SelectedIndex = 0;
        }
        finally { refreshingDrives = false; }
    }
    private void Open(string path)
    {
        var next = new DbRootSession(path);
        session = next;
        rootLabel.Text = "DBROOT: " + session.Root;
        RefreshFavorites();
        RefreshCatalog();
        RefreshFiles();
        UpdateStatus("Loaded.");
    }
    private void UpdateStatus(string message = "")
    {
        status.Text = session == null ? message : $"Songs: {session.Songs.Count}  |  Favourites: {session.Favorites.Count}/{session.Capacity}  |  Files: {session.Names.Count()}  |  {(session.Dirty ? "Unsaved changes  |  " : "")}{message}";
        if (saveButton != null) saveButton.Enabled = session?.Dirty == true && operation == null;
    }
    private void RefreshFavorites()
    {
        favorites.BeginUpdate();
        try
        {
            favorites.Items.Clear();
            if (session == null) return;
            var songs = session.Songs.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
            var old = session.Rows("db_like.db").DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var path in session.Favorites)
            {
                var found = songs.TryGetValue(path, out var song);
                var row = song ?? old.GetValueOrDefault(path) ?? new CatalogRow(0, path, "");
                favorites.Items.Add(new ListViewItem([row.Title, row.FileName, found ? "In catalog" : "Missing from catalog", row.Path]) { Tag = row });
            }
        }
        finally { favorites.EndUpdate(); UpdateStatus(); }
    }
    private void RefreshCatalog()
    {
        catalog.Items.Clear();
        if (session == null || catalogs.SelectedItem == null) return;
        catalogHint.Text = "Edits are staged until saved. Companion indexes and unknown checksums are not rebuilt.";
        if (!session.Names.Contains(Definition.FileName, StringComparer.OrdinalIgnoreCase)) { catalogHint.Text = "This database file is absent."; return; }
        try
        {
            var rows = Definition.FileName == "db_like.db" ? session.Favorites.Select(p => session.Song(p) ?? new CatalogRow(0, p, "")).ToList() : session.Rows(Definition.FileName);
            catalog.BeginUpdate();
            foreach (var row in rows)
                if ($"{row.Path} {row.Title} {row.Album} {row.Artist}".Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                    catalog.Items.Add(new ListViewItem([row.Record.ToString(), row.Title, row.Album, row.Artist, row.Path, row.Extra]) { Tag = row });
            catalogHint.Text = $"Showing {catalog.Items.Count} of {rows.Count} rows. " + catalogHint.Text;
        }
        catch (InvalidDataException e) { catalogHint.Text = e.Message; }
        finally { catalog.EndUpdate(); }
    }
    private void AddFavorites(bool replace)
    {
        RequireSession();
        if (replace && favorites.SelectedItems.Count != 1) throw new InvalidOperationException("Select one favourite to replace.");
        using var dialog = new OpenFileDialog { Title = "Choose songs from the card", Multiselect = !replace, Filter = "Audio files|*.mp3;*.flac;*.m4a;*.aac;*.wav;*.ogg;*.opus;*.wma;*.ape;*.aiff;*.alac;*.m4b|All files|*.*", InitialDirectory = Path.Combine(Path.GetPathRoot(session!.Root)!, "Music") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!replace) { AddFavoriteFiles(dialog.FileNames); return; }
        var song = session.Match(dialog.FileName) ?? throw new InvalidDataException("Song is missing or its filename is ambiguous. Select its exact path on the card.");
        var index = favorites.SelectedIndices[0];
        if (session.Favorites.Where((_, i) => i != index).Contains(song.Path, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("That song is already a favourite.");
        var paths = session.Favorites.ToList(); paths[index] = song.Path;
        session.SetFavorites(paths);
        RefreshFavorites(); RefreshCatalog();
    }
    private void AddFavoriteFiles(IEnumerable<string> filesToAdd)
    {
        var current = RequireSession();
        var paths = current.Favorites.ToList();
        var missing = new List<string>();
        foreach (var file in filesToAdd)
        {
            var song = current.Match(file);
            if (song == null) missing.Add(file); else paths.Add(song.Path);
        }
        current.SetFavorites(paths);
        RefreshFavorites(); RefreshCatalog();
        if (missing.Count > 0) MessageBox.Show(this, "Missing or ambiguous songs skipped:\n" + string.Join("\n", missing.Take(12)), "Some files skipped");
    }
    private void MoveFavorite(int direction)
    {
        if (favorites.SelectedItems.Count != 1) return;
        var current = RequireSession();
        var index = favorites.SelectedIndices[0]; var target = index + direction;
        if (target < 0 || target >= current.Favorites.Count) return;
        var paths = current.Favorites.ToList();
        (paths[index], paths[target]) = (paths[target], paths[index]);
        current.SetFavorites(paths); RefreshFavorites();
        favorites.Items[target].Selected = true; favorites.Items[target].EnsureVisible();
    }
    private void ImportFavorites()
    {
        var current = RequireSession();
        using var dialog = new OpenFileDialog { Filter = "Favourites (*.json;*.txt)|*.json;*.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var values = new List<string>();
        if (Path.GetExtension(dialog.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            values.AddRange(File.ReadAllLines(dialog.FileName).Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')));
        else
        {
            using var json = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
            var root = json.RootElement;
            if (root.GetProperty("format").GetString() != "dbroot-favourites" || root.GetProperty("version").GetInt32() != 1) throw new InvalidDataException("Unsupported favourites export.");
            foreach (var entry in root.GetProperty("favorites").EnumerateArray())
            {
                var path = entry.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(path)) path = entry.TryGetProperty("fileName", out var f) ? f.GetString() : null;
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("An imported favourite needs a path or filename.");
                values.Add(path);
            }
        }
        var mode = MessageBox.Show(this, "Yes: merge with current favourites.\nNo: replace current favourites.\nCancel: leave unchanged.", "Import favourites", MessageBoxButtons.YesNoCancel);
        if (mode == DialogResult.Cancel) return;
        var matched = values.Select(v => current.Match(v)).Where(r => r != null).Select(r => r!.Path).ToList();
        if (values.Count > 0 && matched.Count == 0) throw new InvalidDataException("No imported songs matched. Favourites were left unchanged.");
        current.SetFavorites(mode == DialogResult.Yes ? current.Favorites.Concat(matched) : matched);
        RefreshFavorites(); RefreshCatalog();
        MessageBox.Show(this, $"Matched {matched.Count} entries; skipped {values.Count - matched.Count} missing or ambiguous entries.", "Import complete");
    }
    private void ExportFavorites()
    {
        var current = RequireSession();
        var path = PickSave("Export favourites", "dbroot-favourites.json", "JSON (*.json)|*.json|Text list (*.txt)|*.txt");
        if (path == null) return;
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) File.WriteAllLines(path, current.Favorites);
        else WriteJson(path, new { format = "dbroot-favourites", version = 1, exportedUtc = DateTime.UtcNow, favorites = current.Favorites.Select(p => new { path = p, title = current.Song(p)?.Title ?? Path.GetFileName(p), fileName = Path.GetFileName(p) }) });
        UpdateStatus("Favourites exported.");
    }
    private void EditRow(bool add)
    {
        var current = RequireSession(); var name = Definition.FileName;
        current.EnsureEditable(name);
        if (!add && catalog.SelectedItems.Count != 1) throw new InvalidOperationException("Select one catalog row.");
        var rows = current.Rows(name);
        var existing = add ? null : (CatalogRow)catalog.SelectedItems[0].Tag!;
        using var dialog = new RowEditor(Definition, existing, current);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var updated = dialog.Result!;
        if (name == "db_audio.db" && (existing == null || !existing.Path.Equals(updated.Path, StringComparison.OrdinalIgnoreCase)))
        {
            if (existing != null && current.Favorites.Contains(existing.Path, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Remove or replace this song's favourite before changing its path.");
            current.RequireCardSong(updated.Path);
        }
        if (add) rows.Add(updated); else rows[rows.FindIndex(r => r.Record == existing!.Record)] = updated;
        current.SetRows(name, rows); RefreshCatalog(); RefreshFavorites();
    }
    private void DeleteRow()
    {
        var current = RequireSession(); current.EnsureEditable(Definition.FileName);
        if (catalog.SelectedItems.Count == 0) return;
        var rows = current.Rows(Definition.FileName);
        var remove = SelectedPaths(catalog).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var linked = Definition.FileName == "db_audio.db" && current.Favorites.Any(remove.Contains);
        if (!Confirm($"Delete {remove.Count} catalog row(s)?{(linked ? "\nTheir linked favourites will also be removed." : "")}\nChanges remain staged until saved.", "Delete catalog rows")) return;
        current.SetRows(Definition.FileName, rows.Where(r => !remove.Contains(r.Path)).ToList());
        if (linked) current.SetFavorites(current.Favorites.Where(p => !remove.Contains(p)));
        RefreshCatalog(); RefreshFavorites();
    }
    private void ExportCatalog()
    {
        RequireSession();
        var path = PickSave("Export catalog", Definition.FileName.Replace(".db", ".csv"), "CSV (*.csv)|*.csv|JSON (*.json)|*.json");
        if (path == null) return;
        var rows = Definition.FileName == "db_like.db" ? session!.Favorites.Select(p => session.Song(p) ?? new CatalogRow(0, p, "")).ToList() : session!.Rows(Definition.FileName);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) WriteJson(path, new { format = "dbroot-catalog-export", version = 1, database = Definition.FileName, exportedUtc = DateTime.UtcNow, records = rows.Select(r => new { r.Record, r.Title, r.Album, r.Artist, r.Path, r.Extra }) });
        else File.WriteAllLines(path, new[] { "Record,Title,Album,Artist,Path,ExtraBytes" }.Concat(rows.Select(r => Csv(r.Record, r.Title, r.Album, r.Artist, r.Path, r.Extra))), new UTF8Encoding(true));
    }
    private static string Csv(params object[] fields) => string.Join(",", fields.Select(f => "\"" + f.ToString()!.Replace("\"", "\"\"") + "\""));
    private static void WriteJson(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, DbRootSession.JsonOptions));
    private bool LeaveUnsaved()
    {
        if (session?.Dirty != true) return true;
        var answer = MessageBox.Show(this, "Save your staged DBROOT changes before continuing?", "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        // Switching/closing is synchronous; the same protected write is used.
        if (answer == DialogResult.No) return true;
        try { return SaveSync(); } catch (Exception e) { Error(e); return false; }
    }
    private bool SaveSync()
    {
        if (session?.Dirty != true) return true;
        var backup = ConfirmSave(); if (backup == null) return false;
        UseWaitCursor = true;
        try { session.Save(backup); Open(session.Root); UpdateStatus("Saved. Backup: " + backup); return true; }
        finally { UseWaitCursor = false; }
    }
    private string? ConfirmSave()
    {
        var current = RequireSession(); var updates = current.PrepareSave(); var backup = current.BackupPath("save");
        return Confirm($"Write these files in {current.Root}?\n{string.Join("\n", updates.Keys)}\n\nA full verified backup will be created first:\n{backup}", "Save changes to card") ? backup : null;
    }
    private async Task SaveAsync()
    {
        if (session?.Dirty != true) return;
        var backup = ConfirmSave(); if (backup == null) return;
        var current = session;
        await RunOperation("Saving database files…", ct => current.Save(backup, ct));
        Open(current.Root); UpdateStatus("Saved. Backup: " + backup);
    }
    private async Task RecoverFavoritesAsync()
    {
        if (operation != null) return;
        var current = RequireSession();
        var backup = current.BackupPath("recover-favourites");
        var count = 0;
        await RunOperation("Recovering existing favourites and synchronising their count…", ct => count = current.RecoverExistingFavorites(backup, ct));
        Open(current.Root);
        UpdateStatus($"Recovered {count} favourites and synchronised the player count. Backup: {backup}");
    }
    private async Task RunOperation(string message, Action<CancellationToken> action)
    {
        if (operation != null) return;
        operation = new();
        tabs.Enabled = header.Enabled = footer.Enabled = false;
        progress.Visible = cancel.Visible = true; UseWaitCursor = true; status.Text = message;
        try { await Task.Run(() => action(operation.Token)); }
        finally
        {
            operation.Dispose(); operation = null;
            tabs.Enabled = header.Enabled = footer.Enabled = true;
            progress.Visible = cancel.Visible = false; UseWaitCursor = false; UpdateStatus();
        }
    }
    private void RefreshFiles()
    {
        files.Items.Clear(); strings.Items.Clear(); hex.Clear(); details.Text = ""; rawName = null; raw = null;
        if (session == null) return;
        foreach (var name in session.Names)
        {
            var bytes = File.ReadAllBytes(Path.Combine(session.Root, name));
            var definition = CatalogDefinition.All.FirstOrDefault(d => d.FileName == name);
            var records = "Unknown";
            if (definition != null) { try { records = CatalogCodec.Read(definition, bytes).Count.ToString(); } catch (InvalidDataException) { records = "Format mismatch"; } }
            files.Items.Add(new ListViewItem([name, bytes.Length.ToString("N0"), records, DbRootSession.Hash(bytes)]) { Tag = name });
        }
        if (files.Items.Count > 0) files.Items[0].Selected = true;
    }
    private void ShowRaw()
    {
        if (files.SelectedItems.Count == 0 || session == null) return;
        rawName = (string)files.SelectedItems[0].Tag!;
        raw = File.ReadAllBytes(Path.Combine(session.Root, rawName));
        details.Text = $"{rawName}  |  {raw.Length:N0} bytes  |  SHA-256 {DbRootSession.Hash(raw)}\nDisk contents. Readable strings are best-effort; unknown layouts and checksums are not interpreted.";
        strings.BeginUpdate(); strings.Items.Clear();
        for (var index = 0; index < raw.Length && strings.Items.Count < 5000;)
        {
            if (!Printable(raw[index])) { index++; continue; }
            var start = index;
            while (index < raw.Length && Printable(raw[index]) && index - start < 512) index++;
            if (index - start < 4) continue;
            var text = Encoding.UTF8.GetString(raw, start, index - start).Trim();
            if (text.Contains('\uFFFD'))
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding(932).GetString(raw, start, index - start).Trim();
            }
            if (text.Length > 0 && text.Count(c => !char.IsControl(c)) / (double)text.Length >= .85)
                strings.Items.Add(new ListViewItem([$"0x{start:X}", text]) { Tag = start });
        }
        strings.EndUpdate(); ShowHex(0);
    }
    private static bool Printable(byte b) => b == 9 || b >= 32 && b <= 126 || b >= 128;
    private void ShowHex(int offset)
    {
        if (raw == null) return;
        var output = new StringBuilder(); var start = Math.Max(0, offset - 128) / 16 * 16;
        for (var i = start; i < Math.Min(raw.Length, start + 1024); i += 16)
        {
            var count = Math.Min(16, raw.Length - i);
            output.Append($"{i:X8}  ").Append(CatalogCodec.Hex(raw.AsSpan(i, count)).PadRight(47)).Append("  ");
            foreach (var b in raw.AsSpan(i, count)) output.Append(b is >= 32 and <= 126 ? (char)b : '.');
            output.AppendLine();
        }
        hex.Text = output.ToString();
    }
    private async Task Backup()
    {
        var current = RequireSession();
        var path = PickSave("Back up disk contents (staged edits excluded)", $"DBROOT-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip", "ZIP backup (*.zip)|*.zip");
        if (path == null) return;
        await RunOperation("Creating and verifying backup…", ct => current.Backup(path, ct));
        UpdateStatus("Verified backup: " + path);
    }
    private async Task Restore()
    {
        var current = RequireSession();
        if (!LeaveUnsaved()) return;
        current = RequireSession();
        if (current.Dirty) Open(current.Root);
        current = RequireSession();
        using var dialog = new OpenFileDialog { Filter = "DBROOT backup (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var archive = dialog.FileName;
        var items = DbRootSession.ReadBackup(archive);
        var backup = current.BackupPath("restore");
        if (!Confirm($"Restore {items.Count} files to {current.Root}?\n{string.Join("\n", items.Keys)}\n\nCurrent files will be backed up first:\n{backup}", "Restore backup")) return;
        await RunOperation("Restoring with verified safety backup…", ct => current.Restore(archive, backup, ct));
        Open(current.Root); UpdateStatus("Restored. Safety backup: " + backup);
    }
    private void Compare()
    {
        var current = RequireSession(); var picked = PickFolder(); if (picked == null) return;
        var other = DbRootSession.ResolveRoot(picked);
        var left = Directory.GetFiles(current.Root, "db*.db").ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        var right = Directory.GetFiles(other, "db*.db").ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var name in left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).Order())
        {
            if (!left.ContainsKey(name)) lines.Add("Only in comparison: " + name);
            else if (!right.ContainsKey(name)) lines.Add("Only in current DBROOT: " + name);
            else if (DbRootSession.Hash(File.ReadAllBytes(left[name])) != DbRootSession.Hash(File.ReadAllBytes(right[name]))) lines.Add("Changed: " + name);
        }
        MessageBox.Show(this, $"Current: {current.Root}\nComparison: {other}\n\n" + (lines.Count == 0 ? "All database files match." : string.Join("\n", lines)), "DBROOT comparison");
    }
    private void ExportInventory()
    {
        var current = RequireSession();
        var path = PickSave("Export inventory", "dbroot-inventory.json", "JSON (*.json)|*.json|CSV (*.csv)|*.csv"); if (path == null) return;
        var items = current.Names.Select(name =>
        {
            var bytes = File.ReadAllBytes(Path.Combine(current.Root, name)); var definition = CatalogDefinition.All.FirstOrDefault(d => d.FileName == name);
            string records = "Unknown";
            if (definition != null) { try { records = CatalogCodec.Read(definition, bytes).Count.ToString(); } catch (InvalidDataException) { records = "Format mismatch"; } }
            return new { name, size = bytes.Length, records, description = definition?.Label ?? "Unrecognised binary layout", sha256 = DbRootSession.Hash(bytes) };
        }).ToArray();
        if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)) File.WriteAllLines(path, new[] { "Name,Size,Records,Description,SHA256" }.Concat(items.Select(i => Csv(i.name, i.size, i.records, i.description, i.sha256))), new UTF8Encoding(true));
        else WriteJson(path, new { format = "dbroot-inventory", version = 1, dbrootPath = current.Root, exportedUtc = DateTime.UtcNow, files = items });
    }
    private void ExportRaw()
    {
        var current = RequireSession(); if (rawName == null) throw new InvalidOperationException("Select a database file.");
        var path = PickSave("Export selected disk file", rawName, "Database (*.db)|*.db");
        if (path != null) File.Copy(Path.Combine(current.Root, rawName), path, true);
    }
    private async Task EditRaw()
    {
        var current = RequireSession();
        if (current.Dirty) throw new InvalidOperationException("Save or reload your staged changes before editing raw bytes.");
        if (rawName == null || raw == null) throw new InvalidOperationException("Select a database file.");
        var name = rawName;
        using var dialog = new HexEditor(name, raw, strings.SelectedItems.Count > 0 ? (int)strings.SelectedItems[0].Tag! : 0);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var backup = current.BackupPath("edit");
        var old = CatalogCodec.Hex(raw.AsSpan(dialog.Offset, dialog.Replacement.Length));
        var replacement = CatalogCodec.Hex(dialog.Replacement);
        if (old == replacement) { UpdateStatus("Replacement matches; no edit needed."); return; }
        if (!Confirm($"Edit {name} at 0x{dialog.Offset:X}?\nCurrent: {old}\nNew: {replacement}\n\nUnknown checksums are not updated.\nFull verified backup first:\n{backup}", "Confirm raw database edit")) return;
        await RunOperation("Writing verified raw edit…", ct => current.EditBytes(name, dialog.Offset, dialog.Replacement, backup, ct));
        try { Open(current.Root); }
        catch (Exception e) { session = null; rootLabel.Text = "Reload failed after raw edit"; favorites.Items.Clear(); catalog.Items.Clear(); files.Items.Clear(); rawName = null; raw = null; strings.Items.Clear(); hex.Clear(); throw new IOException($"Bytes were saved, but DBROOT could not reload: {e.Message}\nSafety backup: {backup}", e); }
        UpdateStatus("Raw edit saved. Backup: " + backup);
    }
}

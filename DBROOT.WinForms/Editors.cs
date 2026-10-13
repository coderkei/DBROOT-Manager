// SPDX-FileCopyrightText: 2026 coderkei
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace DbRoot;

internal sealed class RowEditor : Form
{
    public CatalogRow? Result { get; private set; }
    public RowEditor(CatalogDefinition definition, CatalogRow? existing, DbRootSession session)
    {
        Text = $"{(existing == null ? "Add" : "Edit")} {definition.Label} row";
        Size = new(740, 420); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent;
        Font = new("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new(12), AutoSize = true };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 180)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        var path = Field(layout, "Player path (255 bytes)", existing?.Path ?? "");
        var choose = new Button { Text = "Choose file…", AutoSize = true };
        layout.Controls.Add(choose, 1, layout.RowCount++);
        var title = Field(layout, "Title (64 bytes)", existing?.StoredTitle ?? "");
        var album = Field(layout, "Album (16 bytes)", existing?.Album ?? "unknow");
        var artist = Field(layout, "Artist (16 bytes)", existing?.Artist ?? "unknow");
        var extra = Field(layout, $"Device bytes ({definition.ExtraLength})", existing?.Extra ?? "00 00 00 00 00");
        album.Enabled = artist.Enabled = definition.FileName == "db_audio.db";
        extra.Enabled = definition.ExtraLength > 0;
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Text = "Apply", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        bar.Controls.AddRange([cancel, apply]);
        layout.Controls.Add(bar, 0, layout.RowCount++); layout.SetColumnSpan(bar, 2);
        Controls.Add(layout); AcceptButton = apply; CancelButton = cancel;
        choose.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { InitialDirectory = Path.GetPathRoot(session.Root) };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try { path.Text = session.PlayerPath(picker.FileName); if (existing == null) title.Text = Path.GetFileName(picker.FileName); }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Cannot select file"); }
        };
        apply.Click += (_, _) =>
        {
            try
            {
                var row = new CatalogRow(existing?.Record ?? 0, path.Text.Trim(), title.Text, album.Enabled ? album.Text : "", artist.Enabled ? artist.Text : "", extra.Enabled ? extra.Text : "", existing?.OriginalBytes);
                _ = CatalogCodec.Write(definition, [row]);
                Result = row; DialogResult = DialogResult.OK;
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Invalid row", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
    }
    internal static TextBox Field(TableLayoutPanel layout, string label, string value)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new(0, 7, 0, 7) }, 0, row);
        var box = new TextBox { Text = value, Dock = DockStyle.Fill, Margin = new(3, 6, 3, 6), AccessibleName = label };
        layout.Controls.Add(box, 1, row); return box;
    }
}

internal sealed class HexEditor : Form
{
    public int Offset { get; private set; }
    public byte[] Replacement { get; private set; } = [];
    public HexEditor(string name, byte[] bytes, int initialOffset)
    {
        Text = "Edit bytes in " + name;
        Size = new(780, 390); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent;
        Font = new("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new(12) };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 180)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        var offset = RowEditor.Field(layout, "Offset (hex)", $"0x{initialOffset:X}");
        var replacement = RowEditor.Field(layout, "Hex byte pairs", bytes.Length > initialOffset ? $"{bytes[initialOffset]:X2}" : "00");
        var preview = new Label { AutoSize = true, MaximumSize = new(550, 120) };
        layout.Controls.Add(preview, 0, layout.RowCount++); layout.SetColumnSpan(preview, 2);
        var warning = new Label { Text = "File length stays unchanged. Unknown player checksums are not updated.\nA full verified backup is required before writing.", AutoSize = true, Padding = new(0, 10, 0, 10) };
        layout.Controls.Add(warning, 0, layout.RowCount++); layout.SetColumnSpan(warning, 2);
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var apply = new Button { Text = "Review edit…", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        bar.Controls.AddRange([apply, cancel]);
        layout.Controls.Add(bar, 0, layout.RowCount++); layout.SetColumnSpan(bar, 2);
        Controls.Add(layout); AcceptButton = apply; CancelButton = cancel;
        void Validate()
        {
            Offset = int.Parse(offset.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Replacement = CatalogCodec.HexBytes(replacement.Text);
            if (Offset < 0 || Offset > bytes.Length - Replacement.Length) throw new InvalidDataException("The edit extends past the end of the file.");
        }
        void Preview()
        {
            try { Validate(); preview.Text = $"Current: {CatalogCodec.Hex(bytes.AsSpan(Offset, Math.Min(Replacement.Length, 64)))}\nReplacement: {Replacement.Length} bytes at 0x{Offset:X}"; apply.Enabled = true; }
            catch (Exception e) { preview.Text = e.Message; apply.Enabled = false; }
        }
        offset.TextChanged += (_, _) => Preview(); replacement.TextChanged += (_, _) => Preview();
        apply.Click += (_, _) => { try { Validate(); DialogResult = DialogResult.OK; } catch (Exception e) { MessageBox.Show(this, e.Message); } };
        Preview();
    }
}

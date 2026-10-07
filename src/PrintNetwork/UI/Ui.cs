using System.Diagnostics;
using System.Drawing.Drawing2D;
using PrintNetwork.Printing;

namespace PrintNetwork.UI;

/// <summary>Hàm dựng control dùng chung (giao diện viết bằng code, không dùng designer).</summary>
static class Ui
{
    public const string AppName = "PrintNetwork";

    public static Label L(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };

    public static Label H(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Control.DefaultFont.FontFamily, 10f, FontStyle.Bold),
        Margin = new Padding(3, 7, 12, 3),
    };

    public static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(760, 0),
        Margin = new Padding(3, 8, 3, 3),
    };

    public static Button B(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 1, 4, 1) };
        b.Click += onClick;
        return b;
    }

    public static ToolStripMenuItem Mi(string text, Action onClick)
    {
        var m = new ToolStripMenuItem(text);
        m.Click += (_, _) => onClick();
        return m;
    }

    public static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(2),
            Margin = new Padding(0),
        };
        f.Controls.AddRange(controls);
        return f;
    }

    /// <summary>Xếp dọc: weight = 0 → cao vừa nội dung, weight > 0 → chia phần còn lại theo tỉ lệ.</summary>
    public static TableLayoutPanel VStack(params (Control Control, float Weight)[] rows)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, Padding = new Padding(4) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (control, weight) in rows)
        {
            t.RowStyles.Add(weight > 0 ? new RowStyle(SizeType.Percent, weight) : new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Fill;
            t.Controls.Add(control);
        }
        return t;
    }

    public static ComboBox DropList(int width, params string[] items)
    {
        var c = new ComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        return c;
    }

    public static DataGridView NewGrid() => new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
    };

    public static DataGridViewTextBoxColumn AddText(DataGridView grid, string name, string header, bool readOnly, int width)
    {
        var c = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            ReadOnly = readOnly,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        grid.Columns.Add(c);
        return c;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1048576.0:0.#} MB",
    };

    public static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    public static void Info(IWin32Window owner, string text) =>
        MessageBox.Show(owner, text, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(IWin32Window owner, string text) =>
        MessageBox.Show(owner, text, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Error(IWin32Window owner, string text) =>
        MessageBox.Show(owner, text, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static void ShowPsResult(IWin32Window owner, PsResult r, string success)
    {
        if (r.Ok) Info(owner, success + (string.IsNullOrWhiteSpace(r.Output) ? "" : "\n\n" + r.Output));
        else Error(owner, "Thao tác thất bại:\n\n" + r.Output);
    }
}

/// <summary>Biểu tượng máy in vẽ bằng code (không cần file .ico).</summary>
static class AppIcon
{
    static Icon? _icon;

    public static Icon Get() => _icon ??= Load() ?? Create();

    /// <summary>File app.ico nhúng trong exe (cũng là icon của file exe).</summary>
    static Icon? Load()
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("PrintNetwork.app.ico");
        return stream is null ? null : new Icon(stream);
    }

    static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            Glyphs.Printer(g, new Rectangle(0, 0, 32, 32), Glyphs.Blue, led: true);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

static class Glyphs
{
    public static readonly Color Blue = Color.FromArgb(37, 99, 235);

    /// <summary>Vẽ máy in (thiết kế trên lưới 32×32) co giãn vào <paramref name="bounds"/>.</summary>
    public static void Printer(Graphics g, Rectangle bounds, Color body, bool led)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / 32f, bounds.Height / 32f);
        using var paperPen = new Pen(Color.FromArgb(90, 90, 90), 1.2f);
        g.FillRectangle(Brushes.White, 9, 2, 14, 10);
        g.DrawRectangle(paperPen, 9, 2, 14, 10);
        using (var brush = new SolidBrush(body))
        using (var path = RoundedRect(new Rectangle(2, 10, 28, 13), 4))
            g.FillPath(brush, path);
        if (led) g.FillEllipse(Brushes.LimeGreen, 24, 13, 3, 3);
        g.FillRectangle(Brushes.White, 8, 18, 16, 12);
        g.DrawRectangle(paperPen, 8, 18, 16, 12);
        using var line = new Pen(Color.FromArgb(150, 150, 150), 1f);
        g.DrawLine(line, 11, 22, 21, 22);
        g.DrawLine(line, 11, 25, 19, 25);
        g.Restore(state);
    }

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

static class Dialogs
{
    /// <summary>Đọc file INF và cho người dùng chọn model. Trả về null nếu hủy.</summary>
    public static string? PickModel(IWin32Window owner, string infPath, string? preferred = null)
    {
        List<string> models;
        try
        {
            models = InfParser.GetModels(infPath);
        }
        catch (Exception ex)
        {
            Ui.Error(owner, "Không đọc được file INF: " + ex.Message);
            return null;
        }
        if (models.Count == 0)
        {
            Ui.Warn(owner, "Không tìm thấy model máy in nào trong file INF này.\nCó thể đây không phải driver máy in, hoặc không có bản cho Windows 64-bit.");
            return null;
        }
        if (models.Count == 1) return models[0];
        return Pick(owner, "Chọn model máy in", $"File INF có {models.Count} model. Chọn đúng model máy in của bạn:", models, preferred);
    }

    public static string? Pick(IWin32Window owner, string title, string prompt, IList<string> items, string? preferred = null)
    {
        using var form = new Form
        {
            Text = title,
            Size = new Size(560, 500),
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Icon = AppIcon.Get(),
        };
        var label = new Label { Text = prompt, Dock = DockStyle.Top, Height = 34, Padding = new Padding(4, 8, 4, 0) };
        var filter = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Gõ để lọc…" };
        var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        var ok = new Button { Text = "Chọn", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(4) };
        bottom.Controls.AddRange([cancel, ok]);
        // Thứ tự Add quyết định thứ tự dock: control thêm sau được dock trước (nằm ngoài cùng).
        form.Controls.Add(list);
        form.Controls.Add(filter);
        form.Controls.Add(label);
        form.Controls.Add(bottom);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var item in items.Where(i => i.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(item);
            list.EndUpdate();
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }

        filter.TextChanged += (_, _) => Fill();
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItem is not null) form.DialogResult = DialogResult.OK;
        };
        Fill();
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var index = list.Items.IndexOf(preferred);
            if (index >= 0) list.SelectedIndex = index;
        }
        return form.ShowDialog(owner) == DialogResult.OK ? list.SelectedItem as string : null;
    }

    /// <summary>Hộp nhập một dòng chữ. Trả về null nếu hủy.</summary>
    public static string? Prompt(IWin32Window owner, string title, string prompt, string value = "", bool password = false)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoScaleDimensions = new SizeF(96F, 96F),
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
            Icon = AppIcon.Get(),
        };
        var label = new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(3, 3, 3, 8) };
        var box = new TextBox { Width = 380, Text = value, UseSystemPasswordChar = password };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Location = new Point(10, 10) };
        table.Controls.Add(label);
        table.Controls.Add(box);
        table.Controls.Add(buttons);
        form.Controls.Add(table);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}

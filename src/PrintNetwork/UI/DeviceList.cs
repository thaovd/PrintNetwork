using System.ComponentModel;

namespace PrintNetwork.UI;

static class Palette
{
    public static readonly Color ActiveBack = Color.FromArgb(214, 245, 205);
    public static readonly Color HeaderBack = Color.FromArgb(240, 240, 244);
    public static readonly Color HeaderText = Color.FromArgb(88, 44, 131);
    public static readonly Color Line = Color.FromArgb(228, 228, 228);
    public static readonly Color Text = Color.FromArgb(25, 25, 25);
    public static readonly Color Subtle = Color.FromArgb(110, 110, 110);
}

/// <summary>Control tự vẽ, xếp chồng theo chiều dọc; chiều cao tính theo cỡ chữ nên tự co giãn theo DPI.</summary>
abstract class PaintedRow : Control
{
    protected PaintedRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
    }

    protected abstract int MeasureHeight();

    protected void UpdateHeight()
    {
        var h = MeasureHeight();
        if (h != Height) Height = h;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateHeight();
        Invalidate();
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        UpdateHeight();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateHeight();
    }
}

enum BannerKind { Info, Success, Warning, Error }

/// <summary>Dải thông báo trạng thái phía trên danh sách. Có hành động thì bấm vào được.</summary>
sealed class Banner : PaintedRow
{
    BannerKind _kind;
    Action? _onClick;

    public void Set(BannerKind kind, string text, Action? onClick = null)
    {
        _kind = kind;
        _onClick = onClick;
        Text = text;
        Cursor = onClick is null ? Cursors.Default : Cursors.Hand;
        UpdateHeight();
        Invalidate();
    }

    int Pad => Font.Height / 2 + 2;
    int GlyphWidth => Font.Height + 6;
    Rectangle TextBounds => new(Pad + GlyphWidth, Pad, Math.Max(40, ClientSize.Width - Pad * 2 - GlyphWidth), Math.Max(1, ClientSize.Height - Pad * 2));

    const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    protected override int MeasureHeight()
    {
        var size = TextRenderer.MeasureText(Text, Font, new Size(TextBounds.Width, int.MaxValue), Flags);
        return Math.Max(size.Height, Font.Height) + Pad * 2;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (back, fore, glyph) = _kind switch
        {
            BannerKind.Success => (Color.FromArgb(220, 247, 214), Color.FromArgb(30, 130, 40), "✔"),
            BannerKind.Warning => (Color.FromArgb(255, 243, 205), Color.FromArgb(160, 100, 0), "⚠"),
            BannerKind.Error => (Color.FromArgb(253, 226, 226), Color.Firebrick, "✖"),
            _ => (Color.FromArgb(232, 240, 254), Glyphs.Blue, "ℹ"),
        };
        e.Graphics.Clear(back);
        using var symbol = new Font("Segoe UI Symbol", Font.Size, FontStyle.Bold, Font.Unit);
        TextRenderer.DrawText(e.Graphics, glyph, symbol, new Rectangle(Pad, Pad, GlyphWidth, Font.Height), fore, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, Text, Font, TextBounds, _onClick is null ? Palette.Text : fore, Flags);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _onClick?.Invoke();
    }
}

/// <summary>Tiêu đề nhóm (tên máy) trong danh sách.</summary>
sealed class GroupHeader : PaintedRow
{
    public GroupHeader(string text) => Text = text;

    protected override int MeasureHeight() => (int)(Font.Height * 1.9);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Palette.HeaderBack);
        using var bold = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, Text, bold, new Rectangle(Font.Height, 0, Width - Font.Height * 2, Height), Palette.HeaderText,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        using var pen = new Pen(Palette.Line);
        e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }
}

/// <summary>Dòng ghi chú màu xám (danh sách trống…).</summary>
sealed class NoteRow : PaintedRow
{
    public NoteRow(string text) => Text = text;

    protected override int MeasureHeight() => Font.Height * 3;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.White);
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Font.Height, 0, Width - Font.Height * 2, Height), Palette.Subtle,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Một máy in: biểu tượng, tên đậm, dòng mô tả nghiêng, nút hành động chính và nút ⚙ mở menu.</summary>
sealed class DeviceRow : PaintedRow
{
    static readonly ToolTip Tip = new();

    readonly Button _action = new()
    {
        FlatStyle = FlatStyle.Flat,
        AutoSize = true,
        BackColor = Color.White,
        ForeColor = Palette.Text,
        UseVisualStyleBackColor = false,
        Cursor = Cursors.Hand,
        Visible = false,
    };
    readonly Button _gear = new() { FlatStyle = FlatStyle.Flat, Text = "⚙", ForeColor = Palette.Subtle, Cursor = Cursors.Hand, TabStop = false, Visible = false };
    readonly ContextMenuStrip _menu = new();
    Font? _titleFont, _subFont, _gearFont;
    Action? _onAction;
    Action<ToolStripItemCollection>? _buildMenu;
    string _title = "", _subtitle = "";
    bool _active, _muted, _hasAction, _hasMenu;

    public DeviceRow()
    {
        _action.FlatAppearance.BorderColor = Color.FromArgb(185, 185, 185);
        _action.FlatAppearance.MouseOverBackColor = Color.FromArgb(242, 242, 242);
        _action.Click += (_, _) => _onAction?.Invoke();
        _gear.FlatAppearance.BorderSize = 0;
        _gear.Click += (_, _) => _menu.Show(_gear, new Point(0, _gear.Height));
        _menu.Opening += (_, e) =>
        {
            foreach (var item in _menu.Items.Cast<ToolStripItem>().ToList()) item.Dispose();
            _buildMenu?.Invoke(_menu.Items);
            e.Cancel = _menu.Items.Count == 0;
        };
        ContextMenuStrip = _menu;
        Controls.Add(_action);
        Controls.Add(_gear);
        ApplyBackColor();
        CreateFonts();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get => _title;
        set { _title = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            _subtitle = value;
            Tip.SetToolTip(this, value);
            Invalidate();
        }
    }

    /// <summary>Đang chia sẻ / đã kết nối: nền xanh lá, biểu tượng màu.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set { _active = value; ApplyBackColor(); Invalidate(); }
    }

    /// <summary>Không dùng được (lỗi kết nối…): biểu tượng nhạt.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Muted
    {
        get => _muted;
        set { _muted = value; Invalidate(); }
    }

    /// <summary>Dựng các mục menu ⚙ mỗi lần mở. Null = ẩn nút ⚙.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<ToolStripItemCollection>? BuildMenu
    {
        get => _buildMenu;
        set
        {
            _buildMenu = value;
            _gear.Visible = _hasMenu = value is not null;
            PerformLayout();
        }
    }

    /// <summary>Đặt nút hành động chính; text null = ẩn nút.</summary>
    public void SetAction(string? text, Action? onClick)
    {
        _action.Text = text ?? "";
        _action.Visible = _hasAction = text is not null;
        _onAction = onClick;
        PerformLayout();
        Invalidate();
    }

    void ApplyBackColor()
    {
        BackColor = _active ? Palette.ActiveBack : Color.White;
        _gear.BackColor = BackColor;
        _gear.FlatAppearance.MouseOverBackColor = BackColor;
        _gear.FlatAppearance.MouseDownBackColor = BackColor;
    }

    void CreateFonts()
    {
        _titleFont?.Dispose();
        _subFont?.Dispose();
        _gearFont?.Dispose();
        _titleFont = new Font(Font.FontFamily, Font.Size * 1.12f, FontStyle.Bold, Font.Unit);
        _subFont = new Font(Font, FontStyle.Italic);
        _gearFont = new Font("Segoe UI Symbol", Font.Size * 1.35f, FontStyle.Regular, Font.Unit);
        _gear.Font = _gearFont;
    }

    protected override void OnFontChanged(EventArgs e)
    {
        CreateFonts();
        base.OnFontChanged(e);
        PerformLayout();
    }

    protected override int MeasureHeight() => (int)(Font.Height * 3.8);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var u = Font.Height;
        var right = ClientSize.Width - u / 2;
        // Dùng cờ riêng: Visible của control con luôn là false khi dòng chưa hiện lên màn hình.
        if (_hasMenu)
        {
            _gear.Size = new Size(u * 2, u * 2);
            _gear.Location = new Point(right - _gear.Width, (ClientSize.Height - _gear.Height) / 2);
            right = _gear.Left - u / 4;
        }
        else
        {
            right -= u / 2;
        }
        _action.MinimumSize = new Size(u * 6, (int)(u * 1.9));
        _action.Location = new Point(right - _action.Width, (ClientSize.Height - _action.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var u = Font.Height;
        g.Clear(BackColor);

        var size = (int)(u * 2.4);
        var icon = new Rectangle(u, (Height - size) / 2, size, size);
        var body = _muted ? Color.FromArgb(180, 185, 192) : _active ? Glyphs.Blue : Color.FromArgb(105, 115, 130);
        Glyphs.Printer(g, icon, body, _active);

        var x = icon.Right + u;
        var right = (_hasAction ? _action.Left : _hasMenu ? _gear.Left : ClientSize.Width) - u / 2;
        var width = Math.Max(10, right - x);
        var th = _titleFont!.Height;
        var sh = _subFont!.Height;
        var top = (Height - th - sh - 2) / 2;
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(x, top, width, th), _muted ? Palette.Subtle : Palette.Text, flags);
        TextRenderer.DrawText(g, _subtitle, _subFont, new Rectangle(x, top + th + 2, width, sh), Palette.Subtle, flags);

        using var pen = new Pen(Palette.Line);
        g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Tip.SetToolTip(this, null);
            _menu.Dispose();
            _titleFont?.Dispose();
            _subFont?.Dispose();
            _gearFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Danh sách cuộn chứa các dòng xếp từ trên xuống.</summary>
sealed class DeviceList : Panel
{
    public DeviceList()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = Color.White;
    }

    public void SetItems(IReadOnlyList<Control> items)
    {
        SuspendLayout();
        var old = Controls.Cast<Control>().ToList();
        Controls.Clear();
        foreach (var c in old) c.Dispose();
        // Dock = Top: control thêm sau nằm trên cùng, nên thêm theo thứ tự ngược.
        for (var i = items.Count - 1; i >= 0; i--)
        {
            items[i].Dock = DockStyle.Top;
            Controls.Add(items[i]);
        }
        ResumeLayout(true);
    }
}

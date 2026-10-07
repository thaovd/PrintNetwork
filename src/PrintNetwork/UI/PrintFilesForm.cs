using System.Text.RegularExpressions;
using PrintNetwork.Core;
using PrintNetwork.Remote;
using PrintNetwork.Server;
using static PrintNetwork.UI.Ui;

namespace PrintNetwork.UI;

/// <summary>Gửi file tới một máy in của host, kèm tùy chọn in (số bản, 2 mặt, màu…).</summary>
public sealed class PrintFilesForm : Form
{
    sealed class FileEntry
    {
        public required string Path { get; init; }
        public bool Done { get; set; }
    }

    readonly AppSettings _s;
    readonly string _hostSpec, _key;
    readonly SharedPrinterInfo _printer;
    readonly CancellationTokenSource _cts = new();
    bool _busy;

    readonly NumericUpDown numCopies = new() { Minimum = 1, Maximum = 999, Width = 55 };
    readonly ComboBox cbDuplex = DropList(170, "Một mặt", "Hai mặt – lật cạnh dài", "Hai mặt – lật cạnh ngắn");
    readonly ComboBox cbOrient = DropList(90, "Tự động", "Dọc", "Ngang");
    readonly ComboBox cbFit = DropList(130, "Vừa khổ giấy", "Kích thước thật");
    readonly ComboBox cbPaper = new() { Width = 80 };
    readonly CheckBox chkColor = new() { Text = "In màu", AutoSize = true, Margin = new Padding(8, 7, 3, 3) };
    readonly TextBox txtPages = new() { Width = 90, PlaceholderText = "vd: 1-3,5" };
    readonly ListView lvFiles = new() { View = View.Details, FullRowSelect = true, AllowDrop = true, HideSelection = false };
    readonly Button btnPrint = new() { Text = "🖨  In", AutoSize = true, Font = new Font(Control.DefaultFont.FontFamily, 10f, FontStyle.Bold), Padding = new Padding(16, 3, 16, 3) };
    readonly ProgressBar progress = new() { Width = 160, Height = 22, Margin = new Padding(8, 8, 3, 3) };
    readonly Label lblStatus = new() { AutoSize = true, Margin = new Padding(8, 10, 3, 3) };

    public PrintFilesForm(AppSettings settings, string hostSpec, string key, string hostName, SharedPrinterInfo printer, IEnumerable<string> files)
    {
        _s = settings;
        _hostSpec = hostSpec;
        _key = key;
        _printer = printer;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"In tài liệu → {printer.Name}";
        Icon = AppIcon.Get();
        Size = new Size(720, 500);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        cbPaper.Items.AddRange(["", "A4", "A5", "A3", "Letter", "Legal"]);
        lvFiles.Columns.Add("Tài liệu", 330);
        lvFiles.Columns.Add("Dung lượng", 80);
        lvFiles.Columns.Add("Trạng thái", 260);
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        lvFiles.DragEnter += OnDragEnter;
        lvFiles.DragDrop += OnDragDrop;
        lvFiles.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveSelectedFiles();
        };
        btnPrint.Click += async (_, _) => await PrintAll();

        Controls.Add(VStack(
            (Flow(H(printer.Name), Hint($"trên {hostName}" + (printer.Driver.Length > 0 ? $" · {printer.Driver}" : ""))), 0),
            (lvFiles, 100),
            (Flow(B("Thêm file…", (_, _) => BrowseFiles()), B("Bỏ file đã chọn", (_, _) => RemoveSelectedFiles()),
                Hint("Kéo thả file vào danh sách. PDF, ảnh, TXT in được ngay; Word/Excel cần cài Office trên máy chia sẻ.")), 0),
            (Flow(L("Số bản:"), numCopies, L("In 2 mặt:"), cbDuplex, chkColor, L("Hướng:"), cbOrient), 0),
            (Flow(L("Trang:"), txtPages, L("Khổ giấy:"), cbPaper, L("Co giãn:"), cbFit), 0),
            (Flow(btnPrint, progress, lblStatus), 0)));

        WriteOptions(settings.ClientOptions);
        chkColor.Enabled = printer.SupportsColor;
        cbDuplex.Enabled = printer.CanDuplex;
        AcceptButton = btnPrint;
        AddFiles(files);
        ResumeLayout(true);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (lvFiles.Items.Count == 0) BrowseFiles();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosed(e);
    }

    void OnDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
    }

    void UiInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    PrintOptions ReadOptions() => new()
    {
        Copies = (int)numCopies.Value,
        Duplex = cbDuplex.SelectedIndex switch { 1 => "long", 2 => "short", _ => "none" },
        Color = chkColor.Checked,
        Orientation = cbOrient.SelectedIndex switch { 1 => "portrait", 2 => "landscape", _ => "auto" },
        Pages = txtPages.Text.Trim(),
        Fit = cbFit.SelectedIndex == 1 ? "actual" : "fit",
        PaperSize = cbPaper.Text.Trim(),
    };

    void WriteOptions(PrintOptions o)
    {
        numCopies.Value = Math.Clamp(o.Copies, 1, 999);
        cbDuplex.SelectedIndex = o.Duplex switch { "long" => 1, "short" => 2, _ => 0 };
        chkColor.Checked = o.Color;
        cbOrient.SelectedIndex = o.Orientation switch { "portrait" => 1, "landscape" => 2, _ => 0 };
        txtPages.Text = o.Pages;
        cbFit.SelectedIndex = o.Fit == "actual" ? 1 : 0;
        cbPaper.Text = o.PaperSize;
    }

    void AddFiles(IEnumerable<string> paths)
    {
        var existing = lvFiles.Items.Cast<ListViewItem>().Select(i => ((FileEntry)i.Tag!).Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !existing.Add(path)) continue;
            var item = new ListViewItem([Path.GetFileName(path), FormatSize(new FileInfo(path).Length), "Chờ in"])
            {
                Tag = new FileEntry { Path = path },
                ToolTipText = path,
            };
            if (!JobPrinter.IsNativelySupported(path))
                item.SubItems[2].Text = "Chờ in (máy chia sẻ cần có ứng dụng mở được loại file này)";
            lvFiles.Items.Add(item);
        }
    }

    void BrowseFiles()
    {
        using var ofd = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Chọn tài liệu cần in",
            Filter = "Tài liệu in được|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.txt;*.csv;*.prn;*.pcl;*.ps;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx|Tất cả file|*.*",
        };
        if (ofd.ShowDialog(this) == DialogResult.OK) AddFiles(ofd.FileNames);
    }

    void RemoveSelectedFiles()
    {
        if (_busy) return;
        foreach (ListViewItem item in lvFiles.SelectedItems) lvFiles.Items.Remove(item);
    }

    void SetStatus(string text, bool? ok)
    {
        lblStatus.Text = text;
        lblStatus.ForeColor = ok switch { true => Color.ForestGreen, false => Color.Firebrick, null => SystemColors.ControlText };
    }

    async Task PrintAll()
    {
        if (_busy) return;
        var pending = lvFiles.Items.Cast<ListViewItem>().Where(i => !((FileEntry)i.Tag!).Done).ToList();
        if (pending.Count == 0)
        {
            Warn(this, lvFiles.Items.Count == 0 ? "Chưa có tài liệu nào. Kéo thả file vào danh sách hoặc bấm “Thêm file…”." : "Tất cả tài liệu đã được in.");
            return;
        }
        if (!Regex.IsMatch(txtPages.Text, @"^[\d\s,;\-]*$"))
        {
            Warn(this, "Phạm vi trang không hợp lệ. Ví dụ đúng: 1-3,5,8-");
            return;
        }

        // Lưu lựa chọn để in nhanh qua menu Send to dùng lại.
        var options = ReadOptions();
        _s.ClientHost = _hostSpec;
        _s.ClientKey = _key;
        _s.ClientPrinter = _printer.Name;
        _s.ClientOptions = options;
        _s.Save();

        var client = new PrintClient(_hostSpec, _key, _s.ControlPort);
        _busy = true;
        btnPrint.Enabled = false;
        var ok = 0;
        try
        {
            foreach (var item in pending)
            {
                var entry = (FileEntry)item.Tag!;
                if (!File.Exists(entry.Path))
                {
                    SetItemStatus(item, "✖ File không còn tồn tại", false);
                    continue;
                }
                SetItemStatus(item, "Đang kết nối…", null);
                progress.Value = 0;
                try
                {
                    var r = await client.PrintFileAsync(entry.Path, _printer.Name, options,
                        p => UiInvoke(() =>
                        {
                            progress.Value = Math.Clamp((int)(p * 100), 0, 100);
                            item.SubItems[2].Text = $"Đang gửi {progress.Value}%";
                        }),
                        st => UiInvoke(() => item.SubItems[2].Text = st),
                        _cts.Token);
                    if (r.Ok)
                    {
                        ok++;
                        entry.Done = true;
                        SetItemStatus(item, "✔ Đã in" + (r.Pages > 0 ? $" ({r.Pages} trang)" : ""), true);
                    }
                    else
                    {
                        SetItemStatus(item, "✖ " + (r.Error ?? r.Status), false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    SetItemStatus(item, "✖ " + ex.Message, false);
                }
            }
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                btnPrint.Enabled = true;
                progress.Value = 0;
            }
        }
        SetStatus($"Hoàn tất {ok}/{pending.Count} tài liệu", ok == pending.Count);
    }

    void SetItemStatus(ListViewItem item, string text, bool? ok)
    {
        // Chạy sau các cập nhật tiến trình đang chờ để trạng thái cuối không bị ghi đè.
        UiInvoke(() =>
        {
            item.SubItems[2].Text = text;
            item.ForeColor = ok switch { true => Color.ForestGreen, false => Color.Firebrick, null => SystemColors.WindowText };
        });
    }
}

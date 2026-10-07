using PrintNetwork.Core;
using static PrintNetwork.UI.Ui;

namespace PrintNetwork.UI;

/// <summary>Cài đặt ít dùng của máy chia sẻ: tên hiển thị, mã truy cập, cổng, độ nét PDF.</summary>
public sealed class SettingsForm : Form
{
    readonly AppSettings _s;
    readonly TextBox txtName = new() { Width = 220 };
    readonly TextBox txtKey = new() { Width = 220 };
    readonly CheckBox chkLanOnly = new() { Text = "Chỉ nhận lệnh in từ mạng LAN", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
    readonly NumericUpDown numDpi = new() { Minimum = 150, Maximum = 600, Increment = 50, Width = 70 };
    readonly NumericUpDown numPort = new() { Minimum = 1024, Maximum = 65535, Width = 80 };

    public SettingsForm(AppSettings settings)
    {
        _s = settings;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Cài đặt chia sẻ";
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        txtName.Text = settings.HostDisplayName;
        txtKey.Text = settings.HostKey;
        chkLanOnly.Checked = settings.LanOnly;
        numDpi.Value = Math.Clamp(settings.PdfDpi, (int)numDpi.Minimum, (int)numDpi.Maximum);
        numPort.Value = Math.Clamp(settings.ControlPort, (int)numPort.Minimum, (int)numPort.Maximum);

        var ok = new Button { Text = "Lưu", AutoSize = true, Padding = new Padding(10, 1, 10, 1) };
        var cancel = new Button { Text = "Hủy", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Save();

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Location = new Point(10, 10) };
        void AddRow(string label, Control control)
        {
            table.Controls.Add(label.Length > 0 ? L(label) : new Label { AutoSize = true });
            table.Controls.Add(control);
        }
        AddRow("Tên hiển thị:", txtName);
        AddRow("Mã truy cập:", Flow(txtKey, Hint("để trống = không cần mã")));
        AddRow("", chkLanOnly);
        AddRow("Độ nét in PDF:", Flow(numDpi, L("DPI")));
        AddRow("Cổng kết nối:", Flow(numPort, Hint("mọi máy phải dùng cùng cổng, mặc định 9200")));
        AddRow("", Flow(ok, cancel));
        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
        ResumeLayout(true);
    }

    void Save()
    {
        var port = (int)numPort.Value;
        if (port == _s.DiscoveryPort || _s.SharedPrinters.Any(p => p.Enabled && p.RawPort == port))
        {
            Warn(this, $"Cổng {port} đang được PrintNetwork dùng cho việc khác. Hãy chọn cổng khác.");
            return;
        }
        _s.HostDisplayName = txtName.Text.Trim() is { Length: > 0 } n ? n : Environment.MachineName;
        _s.HostKey = txtKey.Text;
        _s.LanOnly = chkLanOnly.Checked;
        _s.PdfDpi = (int)numDpi.Value;
        _s.ControlPort = port;
        _s.Save();
        Log.Info("Đã lưu cài đặt chia sẻ");
        DialogResult = DialogResult.OK;
    }
}

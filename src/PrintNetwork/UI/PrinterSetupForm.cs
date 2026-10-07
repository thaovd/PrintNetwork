using System.Text;
using PrintNetwork.Core;
using PrintNetwork.Printing;
using static PrintNetwork.UI.Ui;

namespace PrintNetwork.UI;

/// <summary>Khai báo một máy in với Windows: chọn cổng (TCP/IP hoặc cổng có sẵn) + driver, cài driver nếu thiếu.</summary>
public sealed class PrinterSetupForm : Form
{
    public sealed class Prefill
    {
        public string Title { get; init; } = "Thêm máy in vào Windows";
        public string PrinterName { get; init; } = "";
        public string? TcpHost { get; init; }
        public int TcpPort { get; init; } = 9100;
        public string? Driver { get; init; }
        /// <summary>Có giá trị khi cài máy in của host: dùng để tải gói driver từ host nếu máy này chưa có.</summary>
        public Func<CancellationToken, Task<(string DriverName, string ZipPath)>>? DriverFromHost { get; init; }
        /// <summary>Bắt đầu cài ngay khi mở cửa sổ (nút “Kết nối”). Lỗi thì cửa sổ vẫn mở để chỉnh driver.</summary>
        public bool AutoInstall { get; init; }
    }

    /// <summary>Tên máy in đã cài thành công.</summary>
    public string? InstalledName { get; private set; }

    readonly Prefill _p;
    readonly TextBox txtName = new() { Width = 360 };
    readonly RadioButton rbTcp = new() { Text = "Máy in mạng / máy in qua host (Standard TCP/IP)", AutoSize = true, Checked = true };
    readonly RadioButton rbPort = new() { Text = "Cổng có sẵn trên máy này (USB, LPT, …)", AutoSize = true };
    readonly TextBox txtIp = new() { Width = 200 };
    readonly NumericUpDown numPort = new() { Minimum = 1, Maximum = 65535, Value = 9100, Width = 80 };
    readonly ComboBox cbPort = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox cbDriver = new()
    {
        Width = 360,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
    };
    readonly Label lblStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3) };
    readonly Button btnOk = new() { Text = "Cài đặt", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
    readonly Button btnCancel = new() { Text = "Đóng", AutoSize = true, DialogResult = DialogResult.Cancel };
    readonly CancellationTokenSource _cts = new();
    HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);
    string? _infPath, _infModel;

    public PrinterSetupForm(Prefill prefill)
    {
        _p = prefill;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = prefill.Title;
        Icon = AppIcon.Get();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        txtName.Text = prefill.PrinterName;
        if (prefill.TcpHost is not null) txtIp.Text = prefill.TcpHost;
        numPort.Value = prefill.TcpPort;
        if (prefill.Driver is not null) cbDriver.Text = prefill.Driver;

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Location = new Point(10, 10) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void AddRow(string label, Control control)
        {
            table.Controls.Add(label.Length > 0 ? L(label) : new Label { AutoSize = true });
            table.Controls.Add(control);
        }

        var driverHint = prefill.DriverFromHost is not null
            ? "Nếu máy này chưa có driver, phần mềm tự tải driver từ host. Có thể chọn driver tương thích khác trong danh sách."
            : "Chọn driver đã cài, gõ tên driver có sẵn của Windows, hoặc chọn file INF tải từ trang hãng.";

        AddRow("Tên máy in:", txtName);
        AddRow("Kết nối:", rbTcp);
        AddRow("", Flow(L("Địa chỉ IP / tên máy:"), txtIp, L("Cổng:"), numPort));
        AddRow("", rbPort);
        AddRow("", cbPort);
        AddRow("Driver:", cbDriver);
        AddRow("", Flow(B("Chọn driver từ file INF…", (_, _) => ChooseInf()), Hint(driverHint)));
        AddRow("", lblStatus);
        AddRow("", Flow(btnOk, btnCancel));
        Controls.Add(table);

        CancelButton = btnCancel;
        rbTcp.CheckedChanged += (_, _) => UpdateEnabled();
        cbDriver.TextChanged += (_, _) => UpdateDriverHint();
        btnOk.Click += async (_, _) => await Install();
        UpdateEnabled();
        ResumeLayout(true);
    }

    Task _loadTask = Task.CompletedTask;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _loadTask = LoadListsAsync();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_p.AutoInstall) return;
        await _loadTask;
        if (!IsDisposed) await Install();
    }

    async Task LoadListsAsync()
    {
        lblStatus.Text = "Đang đọc danh sách driver và cổng máy in…";
        try
        {
            var (drivers, ports) = await Task.Run(() => (PrinterManager.GetDrivers(), PrinterManager.GetPorts()));
            _installed = new HashSet<string>(drivers, StringComparer.OrdinalIgnoreCase);
            var keep = cbDriver.Text;
            cbDriver.Items.Clear();
            cbDriver.Items.AddRange(drivers.ToArray<object>());
            cbDriver.Text = keep;
            cbPort.Items.AddRange(ports.ToArray<object>());
            var usb = ports.FindIndex(p => p.StartsWith("USB", StringComparison.OrdinalIgnoreCase));
            if (cbPort.Items.Count > 0) cbPort.SelectedIndex = Math.Max(0, usb);
            UpdateDriverHint();
        }
        catch (Exception ex)
        {
            lblStatus.Text = "Không đọc được danh sách driver/cổng: " + ex.Message;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosed(e);
    }

    void UpdateEnabled()
    {
        txtIp.Enabled = numPort.Enabled = rbTcp.Checked;
        cbPort.Enabled = rbPort.Checked;
    }

    void UpdateDriverHint()
    {
        var driver = cbDriver.Text.Trim();
        if (driver.Length == 0 || _installed.Count == 0)
        {
            lblStatus.Text = "";
            return;
        }
        if (_installed.Contains(driver)) lblStatus.Text = "✔ Driver đã có trên máy này.";
        else if (_infPath is not null && driver.Equals(_infModel, StringComparison.OrdinalIgnoreCase))
            lblStatus.Text = $"Sẽ cài driver '{driver}' từ {Path.GetFileName(_infPath)}.";
        else if (_p.DriverFromHost is not null && driver.Equals(_p.Driver, StringComparison.OrdinalIgnoreCase))
            lblStatus.Text = "Máy này chưa có driver này – sẽ tự tải từ host khi bấm Cài đặt.";
        else lblStatus.Text = "Máy này chưa có driver này – sẽ thử cài từ kho driver có sẵn của Windows.";
    }

    void ChooseInf()
    {
        using var ofd = new OpenFileDialog { Filter = "Driver máy in (*.inf)|*.inf", Title = "Chọn file INF của driver máy in" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        var model = Dialogs.PickModel(this, ofd.FileName, cbDriver.Text.Trim());
        if (model is null) return;
        _infPath = ofd.FileName;
        _infModel = model;
        cbDriver.Text = model;
        UpdateDriverHint();
    }

    async Task Install()
    {
        var name = txtName.Text.Trim();
        var driver = cbDriver.Text.Trim();
        var host = txtIp.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(['\\', ',']) >= 0)
        {
            Warn(this, "Tên máy in không hợp lệ (không được để trống, không chứa dấu \\ hoặc dấu phẩy).");
            return;
        }
        if (driver.Length == 0)
        {
            Warn(this, "Hãy chọn driver cho máy in.");
            return;
        }
        if (rbTcp.Checked && host.Length == 0)
        {
            Warn(this, "Hãy nhập địa chỉ IP hoặc tên máy.");
            return;
        }
        if (rbPort.Checked && cbPort.SelectedItem is not string)
        {
            Warn(this, "Hãy chọn cổng máy in.");
            return;
        }

        btnOk.Enabled = false;
        string? tempDir = null;
        try
        {
            var script = new StringBuilder();
            if (!_installed.Contains(driver))
            {
                string? inf = null;
                if (_infPath is not null && driver.Equals(_infModel, StringComparison.OrdinalIgnoreCase))
                {
                    inf = _infPath;
                }
                else if (_p.DriverFromHost is not null && driver.Equals(_p.Driver, StringComparison.OrdinalIgnoreCase))
                {
                    lblStatus.Text = "Đang tải driver từ host…";
                    try
                    {
                        var (_, zip) = await _p.DriverFromHost(_cts.Token);
                        var package = await Task.Run(() => PrinterManager.ExtractDriverPackage(zip));
                        try { File.Delete(zip); } catch { }
                        inf = package.InfPath;
                        tempDir = package.Dir;
                        Log.Info($"Đã tải gói driver '{package.DriverName}' từ host");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warn($"Không lấy được driver từ host ({ex.Message}) – thử driver có sẵn của Windows");
                    }
                }
                script.AppendLine(PrinterManager.ScriptInstallDriver(driver, inf));
            }
            script.AppendLine(rbTcp.Checked
                ? PrinterManager.ScriptAddTcpPrinter(name, host, (int)numPort.Value, driver)
                : PrinterManager.ScriptAddPrinter(name, (string)cbPort.SelectedItem!, driver));

            lblStatus.Text = "Đang cài đặt (Windows sẽ hỏi quyền Administrator)…";
            var r = await Task.Run(() => PowerShellRunner.Run(script.ToString(), elevated: true));
            if (r.Ok)
            {
                Log.Info($"Đã khai báo máy in '{name}' (driver '{driver}')");
                InstalledName = name;
                Info(this, $"Đã cài máy in '{name}'.\n\n{r.Output}\n\nGiờ có thể in từ bất kỳ phần mềm nào bằng máy in này.");
                DialogResult = DialogResult.OK;
            }
            else
            {
                lblStatus.Text = "Lỗi: " + r.Output;
                Log.Error($"Khai báo máy in '{name}' thất bại: {r.Output}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            lblStatus.Text = "Lỗi: " + ex.Message;
        }
        finally
        {
            if (tempDir is not null)
                try { Directory.Delete(tempDir, true); } catch { }
            if (!IsDisposed) btnOk.Enabled = true;
        }
    }
}

using PrintNetwork.Core;
using PrintNetwork.Remote;
using static PrintNetwork.UI.Ui;

namespace PrintNetwork.UI;

/// <summary>Cửa sổ nhỏ khi in nhanh từ menu chuột phải "Send to" (dùng host/máy in/tùy chọn đã lưu).</summary>
public sealed class QuickSendForm : Form
{
    readonly AppSettings _s;
    readonly List<string> _files;
    readonly Label lblStatus = new() { AutoSize = false, Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 8, 4, 0) };
    readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 20 };
    readonly TextBox txtResult = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly Button btnClose = new() { Text = "Đóng", AutoSize = true, Dock = DockStyle.Bottom };

    public QuickSendForm(AppSettings settings, List<string> files)
    {
        _s = settings;
        _files = files;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"In qua LAN → {settings.ClientPrinter}";
        Icon = AppIcon.Get();
        Size = new Size(520, 280);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Padding = new Padding(8);
        Controls.Add(txtResult);
        Controls.Add(progress);
        Controls.Add(lblStatus);
        Controls.Add(btnClose);
        btnClose.Click += (_, _) => Close();
        Shown += async (_, _) => await RunAsync();
        ResumeLayout(true);
    }

    async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(_s.ClientHost) || string.IsNullOrWhiteSpace(_s.ClientPrinter))
        {
            Warn(this, "Chưa chọn máy in.\n\nHãy mở PrintNetwork, vào tab “Máy in qua mạng” và in thử một tài liệu để lưu lựa chọn.");
            Close();
            return;
        }

        var client = new PrintClient(_s.ClientHost, _s.ClientKey, _s.ControlPort);
        var ok = 0;
        for (var i = 0; i < _files.Count; i++)
        {
            var file = _files[i];
            var name = Path.GetFileName(file);
            var prefix = $"({i + 1}/{_files.Count}) {name}";
            lblStatus.Text = prefix;
            progress.Value = 0;
            try
            {
                var r = await client.PrintFileAsync(file, _s.ClientPrinter, _s.ClientOptions,
                    p => BeginInvoke(() => progress.Value = Math.Clamp((int)(p * 100), 0, 100)),
                    st => BeginInvoke(() => lblStatus.Text = $"{prefix} – {st}"));
                if (r.Ok)
                {
                    ok++;
                    txtResult.AppendText($"✔ {name}" + (r.Pages > 0 ? $" ({r.Pages} trang)" : "") + Environment.NewLine);
                }
                else
                {
                    txtResult.AppendText($"✖ {name}: {r.Error ?? r.Status}{Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                txtResult.AppendText($"✖ {name}: {ex.Message}{Environment.NewLine}");
            }
        }

        progress.Value = 100;
        lblStatus.Text = $"Hoàn tất {ok}/{_files.Count} tài liệu → {_s.ClientPrinter}";
        if (ok == _files.Count)
        {
            await Task.Delay(1500);
            Close();
        }
    }
}

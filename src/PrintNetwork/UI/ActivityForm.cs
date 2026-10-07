using PrintNetwork.Core;
using static PrintNetwork.UI.Ui;

namespace PrintNetwork.UI;

/// <summary>Cửa sổ phụ: lệnh in máy này đã nhận và nhật ký. Đóng chỉ ẩn đi, dữ liệu vẫn được ghi tiếp.</summary>
public sealed class ActivityForm : Form
{
    readonly DataGridView gridJobs = NewGrid();
    readonly Dictionary<string, DataGridViewRow> _jobRows = [];
    readonly TextBox txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f) };

    public ActivityForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "PrintNetwork – Lịch sử in và nhật ký";
        Icon = AppIcon.Get();
        Size = new Size(1000, 520);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var (name, header, width) in new[]
                 {
                     ("time", "Thời gian", 110), ("id", "Mã", 85), ("client", "Máy gửi", 180), ("file", "Tài liệu", 220),
                     ("printer", "Máy in", 170), ("size", "Dung lượng", 80), ("pages", "Trang", 50), ("status", "Trạng thái", 90),
                 })
            AddText(gridJobs, name, header, true, width);
        AddText(gridJobs, "msg", "Ghi chú", true, 220).AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

        var tabJobs = new TabPage("Lệnh in đã nhận");
        tabJobs.Controls.Add(VStack(
            (Flow(B("Xóa danh sách", (_, _) => { gridJobs.Rows.Clear(); _jobRows.Clear(); })), 0),
            (gridJobs, 100)));
        var tabLog = new TabPage("Nhật ký");
        tabLog.Controls.Add(VStack(
            (Flow(B("Mở thư mục nhật ký", (_, _) => OpenFolder(Log.Dir)), B("Xóa màn hình", (_, _) => txtLog.Clear())), 0),
            (txtLog, 100)));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(tabJobs);
        tabs.TabPages.Add(tabLog);
        Controls.Add(tabs);
        ResumeLayout(true);
    }

    public void ShowActivity()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void AppendLog(string line)
    {
        if (txtLog.TextLength > 300_000) txtLog.Clear();
        txtLog.AppendText(line + Environment.NewLine);
    }

    public void UpsertJob(JobRecord r)
    {
        if (!_jobRows.TryGetValue(r.Id, out var row))
        {
            gridJobs.Rows.Insert(0, 1);
            row = gridJobs.Rows[0];
            _jobRows[r.Id] = row;
            while (gridJobs.Rows.Count > 500)
            {
                var last = gridJobs.Rows[^1];
                _jobRows.Remove(last.Cells["id"].Value as string ?? "");
                gridJobs.Rows.Remove(last);
            }
        }
        row.SetValues(r.Time.ToString("dd/MM HH:mm:ss"), r.Id, r.Client, r.FileName, r.Printer, FormatSize(r.Size),
            r.Pages > 0 ? r.Pages.ToString() : "", r.Status, r.Message ?? "");
        row.DefaultCellStyle.ForeColor = r.Status switch
        {
            JobRecord.Failed => Color.Firebrick,
            JobRecord.Done => Color.ForestGreen,
            _ => SystemColors.ControlText,
        };
    }
}

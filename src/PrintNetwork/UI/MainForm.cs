using System.Diagnostics;
using PrintNetwork.Core;
using PrintNetwork.Printing;
using PrintNetwork.Remote;
using PrintNetwork.Server;
using static PrintNetwork.UI.Ui;

namespace PrintNetwork.UI;

/// <summary>
/// Cửa sổ chính, hai tab:
///  • Máy in trên máy này – bấm “Chia sẻ” để máy khác in được (máy chủ in tự chạy khi có máy in được chia sẻ).
///  • Máy in qua mạng – máy in các máy khác đang chia sẻ; “Kết nối” để cài vào Windows, hoặc kéo thả file để in.
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>Một máy đang chia sẻ máy in (tìm thấy trong LAN hoặc nhập địa chỉ thủ công).</summary>
    sealed class RemoteHost
    {
        public required string Spec { get; init; }
        public required string Name { get; init; }
        public List<SharedPrinterInfo> Printers { get; init; } = [];
        public string? Error { get; init; }
        public bool Manual { get; init; }
        public bool NeedKey => Error?.Contains("mã truy cập", StringComparison.OrdinalIgnoreCase) == true;
    }

    readonly AppSettings _s;
    readonly PrintHost _host;
    readonly ActivityForm _activity = new();
    bool _startHidden, _exiting, _initialized, _trayHintShown, _localLoaded, _remoteBusy, _firewallPending;
    string? _hostError;
    List<WinPrinter> _localPrinters = [];
    List<RemoteHost> _remote = [];
    DateTime _remoteAt;
    readonly Dictionary<string, JobRecord> _activeJobs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (DeviceRow Row, WinPrinter Printer)> _localRows = new(StringComparer.OrdinalIgnoreCase);

    readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    readonly TabPage tabLocal = new("Máy in trên máy này") { Padding = Padding.Empty };
    readonly TabPage tabRemote = new("Máy in qua mạng") { Padding = Padding.Empty };
    readonly Banner localBanner = new();
    readonly Banner remoteBanner = new();
    readonly DeviceList localList = new();
    readonly DeviceList remoteList = new();
    readonly ToolStripMenuItem miAutoStart = new("Khởi động cùng Windows");
    readonly NotifyIcon tray = new();
    readonly CancellationTokenSource _updateCts = new();
    readonly System.Windows.Forms.Timer _restartTimer = new() { Interval = 60_000 };

    public MainForm(AppSettings settings, bool startMinimized)
    {
        _s = settings;
        _host = new PrintHost(settings);
        _startHidden = startMinimized;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "PrintNetwork – In qua mạng";
        Icon = AppIcon.Get();
        MinimumSize = new Size(480, 380);
        Size = new Size(640, 540);
        StartPosition = FormStartPosition.CenterScreen;

        // Thứ tự Add quyết định thứ tự dock: control thêm sau được dock trước (nằm ngoài cùng).
        tabLocal.Controls.Add(localList);
        tabLocal.Controls.Add(localBanner);
        tabRemote.Controls.Add(remoteList);
        tabRemote.Controls.Add(remoteBanner);
        tabs.TabPages.Add(tabLocal);
        tabs.TabPages.Add(tabRemote);
        tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (tabs.SelectedTab == tabRemote && DateTime.Now - _remoteAt > TimeSpan.FromMinutes(1)) await RefreshRemote();
        };
        var menu = BuildMenu();
        Controls.Add(tabs);
        Controls.Add(BuildFooter());
        Controls.Add(menu);
        MainMenuStrip = menu;

        localBanner.Set(BannerKind.Info, "Đang đọc danh sách máy in…");
        remoteBanner.Set(BannerKind.Info, "Đang tìm máy in dùng chung trong mạng LAN…");
        if (!_s.SharedPrinters.Any(p => p.Enabled) && _s.RecentHosts.Count > 0) tabs.SelectedTab = tabRemote;

        tray.Icon = Icon;
        tray.Text = "PrintNetwork";
        tray.Visible = true;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mở PrintNetwork", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Thoát hẳn", null, (_, _) => ExitApp());
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowFromTray();

        _host.JobUpdated += rec => UiInvoke(() => OnJob(rec));
        Log.Written += OnLogWritten;
        ResumeLayout(true);
    }

    MenuStrip BuildMenu()
    {
        try { miAutoStart.Checked = PrinterManager.GetAutoStart(); } catch { }
        miAutoStart.Click += (_, _) => SetAutoStart(!miAutoStart.Checked);

        var share = new ToolStripMenuItem("&Chia sẻ");
        share.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("Làm mới danh sách", async () => await LoadLocal()),
            Mi("Cài đặt chia sẻ…", OpenSettings),
            Mi("Mở tường lửa…", async () => await OpenFirewall(quiet: false)),
            miAutoStart,
            new ToolStripSeparator(),
            Mi("Thoát hẳn", ExitApp),
        });
        var connect = new ToolStripMenuItem("&Kết nối");
        connect.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("Tìm lại máy in trong mạng", async () => await RefreshRemote()),
            Mi("Kết nối theo địa chỉ IP…", async () => await AddHostByAddress()),
            new ToolStripSeparator(),
            Mi("Thêm “In qua LAN” vào menu (Send to)", CreateSendTo),
        });
        var printers = new ToolStripMenuItem("&Máy in");
        printers.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("Thêm máy in vào Windows…", AddPrinter),
            Mi("Cài driver", async () => await InstallInfDriver()),
            Mi("Mở Devices and Printers", () => Process.Start(new ProcessStartInfo("control.exe", "printers") { UseShellExecute = true })),
        });
        var help = new ToolStripMenuItem("&Trợ giúp");
        help.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("Lịch sử in và nhật ký", _activity.ShowActivity),
            Mi("Mở thư mục nhật ký", () => OpenFolder(Log.Dir)),
            new ToolStripSeparator(),
            Mi("Hướng dẫn nhanh", () => Info(this,
                "Máy có gắn máy in: tab “Máy in trên máy này” → bấm “Chia sẻ”.\n\n" +
                "Máy khác: tab “Máy in qua mạng” →\n" +
                "  • “Kết nối” để cài máy in vào Windows, in được từ mọi phần mềm (Word, Excel…);\n" +
                "  • hoặc kéo thả file vào máy in / bấm ⚙ → “In tài liệu…” để in ngay.\n\n" +
                "Máy chia sẻ phải bật và đăng nhập Windows. Bấm X chỉ thu xuống khay hệ thống.")),
        });
        var menu = new MenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { share, connect, printers, help });
        return menu;
    }

    static StatusStrip BuildFooter()
    {
        var team = new ToolStripStatusLabel("Vuth Team") { IsLink = true, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 3, 0, 2) };
        team.Click += (_, _) => Process.Start(new ProcessStartInfo("https://vuth.vn") { UseShellExecute = true });
        var footer = new StatusStrip { SizingGrip = false };
        footer.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripStatusLabel($"Phiên bản {AutoUpdater.DisplayVersion}"),
            new ToolStripStatusLabel { Spring = true },
            new ToolStripStatusLabel("Phát triển bởi") { Margin = new Padding(0, 3, 0, 2) },
            team,
            new ToolStripStatusLabel("- Development: ThaoVD") { Margin = new Padding(0, 3, 4, 2) },
        });
        return footer;
    }

    // =====================================================================
    //  Khởi động / khay hệ thống
    // =====================================================================

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && value)
        {
            _startHidden = false;
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
            return;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_initialized) return;
        _initialized = true;
        BeginInvoke(async () => await StartupAsync());
    }

    async Task StartupAsync()
    {
        if (_s.SharedPrinters.Any(p => p.Enabled)) ApplyHost();
        _restartTimer.Tick += (_, _) => RestartForUpdate();
        _ = AutoUpdater.RunAsync(() => UiInvoke(RestartForUpdate), _updateCts.Token);
        await LoadLocal();
        await RefreshRemote();
    }

    /// <summary>
    /// Đã cài bản mới: khởi động lại ngay nếu đang ẩn dưới khay và rảnh (không in dở, không mở hộp thoại).
    /// Đang có người dùng thì chờ – thử lại mỗi phút; tắt ứng dụng thì lần mở sau cũng là bản mới.
    /// </summary>
    void RestartForUpdate()
    {
        if (!AutoUpdater.UpdateReady || _exiting) return;
        var busy = Visible || _activeJobs.Count > 0 || Application.OpenForms.Cast<Form>().Any(f => f != this && f.Visible);
        if (busy)
        {
            _restartTimer.Start();
            return;
        }
        _restartTimer.Stop();
        Log.Info("Tự cập nhật: khởi động lại để chạy bản mới");
        if (AutoUpdater.Restart()) ExitApp();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5)
        {
            _ = tabs.SelectedTab == tabRemote ? RefreshRemote() : LoadLocal();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Gọi từ luồng khác (ví dụ khi người dùng mở ứng dụng lần thứ hai).</summary>
    public void RequestShow() => UiInvoke(ShowFromTray);

    public void ShowFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _host.IsRunning)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                tray.ShowBalloonTip(4000, "PrintNetwork vẫn đang chạy",
                    "Máy in vẫn được chia sẻ dưới khay hệ thống. Chuột phải biểu tượng → Thoát hẳn để tắt.", ToolTipIcon.Info);
            }
            return;
        }
        Log.Written -= OnLogWritten;
        _updateCts.Cancel();
        _restartTimer.Dispose();
        _host.Stop();
        tray.Visible = false;
        tray.Dispose();
        _activity.Dispose();
        base.OnFormClosing(e);
    }

    void UiInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    void OnLogWritten(string line) => UiInvoke(() => _activity.AppendLog(line));

    void SetAutoStart(bool enabled)
    {
        try
        {
            PrinterManager.SetAutoStart(enabled);
            miAutoStart.Checked = enabled;
        }
        catch (Exception ex)
        {
            Log.Error("Không đặt được khởi động cùng Windows", ex);
            Error(this, "Không đặt được khởi động cùng Windows: " + ex.Message);
        }
    }

    // =====================================================================
    //  MÁY IN TRÊN MÁY NÀY (chia sẻ)
    // =====================================================================

    async Task LoadLocal()
    {
        try
        {
            _localPrinters = await Task.Run(PrinterManager.GetPrinters);
            _host.UpdateDrivers(_localPrinters);
        }
        catch (Exception ex)
        {
            Log.Warn("Không đọc được chi tiết máy in qua PowerShell: " + ex.Message);
            _localPrinters = PrinterManager.InstalledPrinterNames().Select(n => new WinPrinter { Name = n }).ToList();
        }
        _localLoaded = true;
        RenderLocal();
        // Trạng thái “Đã kết nối” ở tab qua mạng phụ thuộc danh sách máy in trên máy này.
        if (_remoteAt != default) RenderRemote();
    }

    bool IsShared(string name) => _s.SharedPrinters.Any(c => c.Enabled && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    void RenderLocal()
    {
        UpdateLocalBanner();
        if (!_localLoaded) return;

        _localRows.Clear();
        var items = new List<Control> { new GroupHeader($"{_s.HostDisplayName} (máy này)") };
        var printers = _localPrinters.Where(p => !IsRemoteLink(p) && (!IsVirtual(p) || IsShared(p.Name)))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var p in printers)
        {
            var name = p.Name;
            var shared = IsShared(name);
            var row = new DeviceRow { Title = name, Subtitle = LocalSubtitle(p), Active = shared };
            row.SetAction(shared ? "Ngừng chia sẻ" : "Chia sẻ", async () => await ToggleShare(name));
            row.BuildMenu = m =>
            {
                m.Add(Mi("Mở hàng đợi in", () => PrintUi("/o", name)));
                m.Add(Mi("In trang thử", () => PrintUi("/k", name)));
                m.Add(Mi("Thuộc tính máy in", () => PrintUi("/p", name)));
                m.Add(new ToolStripSeparator());
                m.Add(Mi("Xóa máy in khỏi Windows…", async () => await RemovePrinter(name, $"Xóa máy in “{name}” khỏi Windows?")));
            };
            _localRows[name] = (row, p);
            items.Add(row);
        }
        if (printers.Count == 0)
            items.Add(new NoteRow("Máy này chưa có máy in nào. Cắm máy in vào hoặc vào menu Máy in → Thêm máy in vào Windows."));
        localList.SetItems(items);
    }

    static readonly HashSet<string> VirtualPorts = new(["PORTPROMPT:", "SHRFAX:", "XPSPort:", "FILE:", "nul:"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Máy in ảo của Windows (Fax, Print to PDF, XPS, OneNote) – không ai cần chia sẻ, ẩn cho gọn.</summary>
    static bool IsVirtual(WinPrinter p) =>
        VirtualPorts.Contains(p.PortName) || p.PortName.Contains("OneNote", StringComparison.OrdinalIgnoreCase) ||
        p.DriverName is "Microsoft Shared Fax Driver" or "Microsoft Print To PDF" or "Microsoft XPS Document Writer v4" or "Microsoft Software Printer Driver";

    string LocalSubtitle(WinPrinter p)
    {
        if (_activeJobs.TryGetValue(p.Name, out var job)) return $"Đang in “{job.FileName}” từ {job.Client}";
        if (IsShared(p.Name)) return _host.IsRunning ? "Đang chia sẻ – các máy trong mạng LAN in được" : "Đã bật chia sẻ";
        return p.DriverName.Length > 0 ? p.DriverName : "Máy in";
    }

    void UpdateLocalBanner()
    {
        if (_hostError is not null)
        {
            localBanner.Set(BannerKind.Error,
                $"Không mở được cổng {_s.ControlPort}: {_hostError}\nCó thể cổng đang bị chương trình khác dùng. Bấm vào đây để thử lại.", ApplyHost);
        }
        else if (_host.IsRunning && _firewallPending)
        {
            localBanner.Set(BannerKind.Warning,
                "Chưa mở tường lửa Windows – máy khác có thể không kết nối được. Bấm vào đây để mở.", async () => await OpenFirewall(quiet: false));
        }
        else if (_host.IsRunning)
        {
            var count = _s.SharedPrinters.Count(p => p.Enabled);
            var port = _s.ControlPort != Protocol.DefaultControlPort ? $" (cổng {_s.ControlPort})" : "";
            localBanner.Set(BannerKind.Success,
                $"Đang chia sẻ {count} máy in. Máy khác kết nối tới: {string.Join(", ", Protocol.LocalIPv4())}{port}");
        }
        else if (_localLoaded)
        {
            localBanner.Set(BannerKind.Info, "Bấm “Chia sẻ” cạnh máy in để các máy khác trong mạng LAN in được.");
        }
    }

    async Task ToggleShare(string name)
    {
        // Luôn thay cả danh sách (luồng host đọc song song).
        var list = _s.SharedPrinters.Select(c => new SharedPrinterConfig { Name = c.Name, Enabled = c.Enabled, RawPort = c.RawPort }).ToList();
        var cfg = list.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (cfg is null) list.Add(cfg = new SharedPrinterConfig { Name = name });
        cfg.Enabled = !cfg.Enabled;
        if (cfg.Enabled && cfg.RawPort == 0) cfg.RawPort = FreeRawPort(list);
        _s.SharedPrinters = list;
        _s.Save();
        Log.Info(cfg.Enabled ? $"Bật chia sẻ '{name}' (cổng RAW {cfg.RawPort})" : $"Ngừng chia sẻ '{name}'");

        ApplyHost();
        if (cfg.Enabled && _host.IsRunning)
        {
            // Máy chia sẻ cần tự chạy lại sau khi khởi động máy, nếu không máy khác sẽ không in được.
            if (!miAutoStart.Checked) SetAutoStart(true);
            await EnsureFirewall();
        }
    }

    int FreeRawPort(List<SharedPrinterConfig> list)
    {
        var used = list.Select(c => c.RawPort).Append(_s.ControlPort).Append(_s.DiscoveryPort).ToHashSet();
        var port = 9100;
        while (used.Contains(port)) port++;
        return port;
    }

    /// <summary>Khởi động lại máy chủ in theo cấu hình hiện tại; chỉ chạy khi có ít nhất một máy in được chia sẻ.</summary>
    void ApplyHost()
    {
        if (_host.IsRunning) _host.Stop();
        _hostError = null;
        if (_s.SharedPrinters.Any(p => p.Enabled))
        {
            try
            {
                _host.Start();
            }
            catch (Exception ex)
            {
                _hostError = ex.Message;
                Log.Error("Không khởi động được máy chủ in", ex);
            }
        }
        tray.Text = _host.IsRunning ? "PrintNetwork – đang chia sẻ máy in" : "PrintNetwork";
        RenderLocal();
    }

    List<int> FirewallPortsNeeded() =>
        new[] { _s.ControlPort, _s.DiscoveryPort }
            .Concat(_s.SharedPrinters.Where(p => p.Enabled && p.RawPort > 0).Select(p => p.RawPort))
            .Distinct().Order().ToList();

    async Task EnsureFirewall()
    {
        if (FirewallPortsNeeded().All(_s.FirewallPorts.Contains)) return;
        await OpenFirewall(quiet: true);
    }

    async Task OpenFirewall(bool quiet)
    {
        localBanner.Set(BannerKind.Info, "Đang mở cổng tường lửa Windows (Windows sẽ hỏi quyền Administrator)…");
        var raws = _s.SharedPrinters.Where(p => p.Enabled && p.RawPort > 0).Select(p => p.RawPort).ToList();
        var r = await Task.Run(() => PrinterManager.OpenFirewall(_s.ControlPort, _s.DiscoveryPort, raws));
        if (r.Ok)
        {
            _s.FirewallPorts = FirewallPortsNeeded();
            _s.Save();
            _firewallPending = false;
            Log.Info("Đã mở tường lửa: " + r.Output);
        }
        else
        {
            _firewallPending = true;
            Log.Warn("Không mở được tường lửa: " + r.Output);
            if (!quiet) Error(this, "Không mở được tường lửa:\n\n" + r.Output);
        }
        UpdateLocalBanner();
    }

    void OnJob(JobRecord r)
    {
        _activity.UpsertJob(r);
        if (r.Status is JobRecord.Done or JobRecord.Failed)
        {
            if (_activeJobs.TryGetValue(r.Printer, out var current) && current.Id == r.Id) _activeJobs.Remove(r.Printer);
        }
        else
        {
            _activeJobs[r.Printer] = r;
        }
        if (_localRows.TryGetValue(r.Printer, out var entry)) entry.Row.Subtitle = LocalSubtitle(entry.Printer);
        if (r.Status == JobRecord.Failed && !Visible)
            tray.ShowBalloonTip(4000, "In thất bại", $"{r.FileName}: {r.Message}", ToolTipIcon.Error);
    }

    void OpenSettings()
    {
        using var form = new SettingsForm(_s);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        if (_s.SharedPrinters.Any(p => p.Enabled))
        {
            ApplyHost();
            if (_host.IsRunning) _ = EnsureFirewall();
        }
        else
        {
            RenderLocal();
        }
    }

    // =====================================================================
    //  MÁY IN QUA MẠNG (kết nối)
    // =====================================================================

    string KeyFor(string spec) => _s.HostKeys.TryGetValue(spec, out var key) ? key : _s.ClientKey;

    static string AddressOf(string spec) => new PrintClient(spec, "").Host;

    /// <summary>Máy in trên máy này đã được cài trỏ tới máy in <paramref name="p"/> của host (qua cổng TCP/IP do PrintNetwork tạo).</summary>
    WinPrinter? LinkedPrinter(string spec, SharedPrinterInfo p)
    {
        if (p.RawPort <= 0) return null;
        var port = PrinterManager.TcpPortName(AddressOf(spec), p.RawPort);
        return _localPrinters.FirstOrDefault(w => w.PortName.Equals(port, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Máy in trỏ tới một host đã biết – hiện ở tab “qua mạng”, không hiện ở tab “trên máy này”.</summary>
    bool IsRemoteLink(WinPrinter w)
    {
        if (!w.PortName.StartsWith("IP_", StringComparison.OrdinalIgnoreCase)) return false;
        return _s.RecentHosts.Concat(_remote.Select(h => h.Spec)).Select(AddressOf).Any(a =>
            w.PortName.Equals("IP_" + a, StringComparison.OrdinalIgnoreCase) ||
            w.PortName.StartsWith("IP_" + a + "_", StringComparison.OrdinalIgnoreCase));
    }

    async Task RefreshRemote()
    {
        if (_remoteBusy) return;
        _remoteBusy = true;
        remoteBanner.Set(BannerKind.Info, "Đang tìm máy in dùng chung trong mạng LAN…");
        try
        {
            List<HostInfo> found;
            try
            {
                found = await HostDiscovery.DiscoverAsync(_s.DiscoveryPort);
            }
            catch (Exception ex)
            {
                Log.Warn("Lỗi tìm host trong LAN: " + ex.Message);
                found = [];
            }

            var targets = found
                .Where(h => !h.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                .Select(h => (Spec: h.Endpoint, Name: h.HostName, Manual: false)).ToList();
            foreach (var spec in _s.RecentHosts)
                if (!targets.Any(t => t.Spec.Equals(spec, StringComparison.OrdinalIgnoreCase)))
                    targets.Add((spec, spec, true));

            var hosts = await Task.WhenAll(targets.Select(t => QueryHost(t.Spec, t.Name, t.Manual)));
            _remote = hosts.OfType<RemoteHost>().ToList();
            _remoteAt = DateTime.Now;
        }
        finally
        {
            _remoteBusy = false;
        }
        RenderRemote();
        RenderLocal();
    }

    async Task<RemoteHost?> QueryHost(string spec, string name, bool manual)
    {
        try
        {
            var r = await new PrintClient(spec, KeyFor(spec), _s.ControlPort).ListAsync();
            if (string.Equals(r.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return null;
            return new RemoteHost
            {
                Spec = spec,
                Name = string.IsNullOrWhiteSpace(r.HostName) ? name : r.HostName,
                Printers = r.Printers ?? [],
                Manual = manual,
            };
        }
        catch (Exception ex)
        {
            return new RemoteHost { Spec = spec, Name = name, Error = ex.Message, Manual = manual };
        }
    }

    void RenderRemote()
    {
        var count = _remote.Sum(h => h.Printers.Count);
        if (count > 0)
            remoteBanner.Set(BannerKind.Success,
                $"Tìm thấy {count} máy in trong mạng LAN.");
        else
            remoteBanner.Set(BannerKind.Info,
                "Chưa thấy máy in nào trong mạng LAN.",
                async () => await AddHostByAddress());

        var items = new List<Control>();
        foreach (var h in _remote)
        {
            items.Add(new GroupHeader(h.Name.Equals(h.Spec, StringComparison.OrdinalIgnoreCase) ? h.Spec : $"{h.Name}  ·  {h.Spec}"));
            if (h.Error is not null)
            {
                var row = new DeviceRow { Title = h.NeedKey ? "Cần mã truy cập" : "Không kết nối được", Subtitle = h.Error, Muted = true };
                if (h.NeedKey) row.SetAction("Nhập mã…", async () => await EnterKey(h.Spec));
                else row.SetAction("Thử lại", async () => await RefreshRemote());
                if (h.Manual) row.BuildMenu = m => m.Add(Mi("Xóa khỏi danh sách", () => ForgetHost(h.Spec)));
                items.Add(row);
            }
            else if (h.Printers.Count == 0)
            {
                items.Add(new NoteRow("Máy này chưa chia sẻ máy in nào."));
            }
            else
            {
                foreach (var p in h.Printers) items.Add(RemoteRow(h, p));
            }
        }
        remoteList.SetItems(items);
    }

    DeviceRow RemoteRow(RemoteHost h, SharedPrinterInfo p)
    {
        var linked = LinkedPrinter(h.Spec, p);
        var details = new List<string>();
        if (p.Driver.Length > 0) details.Add(p.Driver);
        details.Add(p.SupportsColor ? "in màu" : "đen trắng");
        if (p.CanDuplex) details.Add("in 2 mặt");

        var row = new DeviceRow
        {
            Title = p.Name,
            Subtitle = linked is not null ? "Đã kết nối" : string.Join(" · ", details),
            Active = linked is not null,
            AllowDrop = true,
        };
        if (linked is not null) row.SetAction("Ngắt kết nối", async () => await RemovePrinter(linked.Name,
            $"Ngắt kết nối sẽ xóa máy in “{linked.Name}” khỏi Windows trên máy này. Tiếp tục?"));
        else if (p.RawPort > 0) row.SetAction("Kết nối", async () => await Connect(h, p));
        else row.SetAction("In tài liệu…", () => OpenPrintFiles(h, p, []));

        row.BuildMenu = m =>
        {
            m.Add(Mi("In tài liệu…", () => OpenPrintFiles(h, p, [])));
            if (linked is null) return;
            m.Add(new ToolStripSeparator());
            m.Add(new ToolStripMenuItem($"Tên trên máy này: {linked.Name}") { Enabled = false });
            m.Add(Mi("Mở hàng đợi in", () => PrintUi("/o", linked.Name)));
            m.Add(Mi("In trang thử", () => PrintUi("/k", linked.Name)));
        };
        row.DoubleClick += (_, _) => OpenPrintFiles(h, p, []);
        row.DragEnter += (_, e) =>
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        row.DragDrop += (_, e) =>
        {
            // Mở hộp thoại sau khi thao tác kéo thả kết thúc để Explorer không bị treo.
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) BeginInvoke(() => OpenPrintFiles(h, p, files));
        };
        return row;
    }

    void RememberHost(string spec)
    {
        _s.RecentHosts.RemoveAll(h => h.Equals(spec, StringComparison.OrdinalIgnoreCase));
        _s.RecentHosts.Insert(0, spec);
        if (_s.RecentHosts.Count > 10) _s.RecentHosts.RemoveRange(10, _s.RecentHosts.Count - 10);
        _s.Save();
    }

    void ForgetHost(string spec)
    {
        _s.RecentHosts.RemoveAll(h => h.Equals(spec, StringComparison.OrdinalIgnoreCase));
        _s.HostKeys.Remove(spec);
        _s.Save();
        _remote.RemoveAll(h => h.Spec == spec);
        RenderRemote();
    }

    async Task AddHostByAddress()
    {
        var spec = Dialogs.Prompt(this, "Kết nối theo địa chỉ IP",
            "Nhập địa chỉ IP hoặc tên của máy đang chia sẻ máy in:");
        if (string.IsNullOrWhiteSpace(spec)) return;
        RememberHost(spec);
        tabs.SelectedTab = tabRemote;
        await RefreshRemote();
    }

    async Task EnterKey(string spec)
    {
        var key = Dialogs.Prompt(this, "Mã truy cập", $"Máy {spec} yêu cầu mã truy cập. Nhập mã (hỏi người quản lý máy chia sẻ):", KeyFor(spec));
        if (key is null) return;
        _s.HostKeys[spec] = key;
        _s.Save();
        await RefreshRemote();
    }

    async Task Connect(RemoteHost h, SharedPrinterInfo p)
    {
        var client = new PrintClient(h.Spec, KeyFor(h.Spec), _s.ControlPort);
        var shortName = p.Name.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? p.Name;
        // Tên hiện trong hộp thoại In của mọi phần mềm, ví dụ “HP LaserJet - KETOAN - (PrintNetwork)”.
        var localName = $"{shortName} - {h.Name} - (PrintNetwork)".Replace(",", " ").Replace("\\", " ");
        using (var form = new PrinterSetupForm(new PrinterSetupForm.Prefill
               {
                   Title = $"Kết nối máy in “{p.Name}” của {h.Name}",
                   PrinterName = localName,
                   TcpHost = client.Host,
                   TcpPort = p.RawPort,
                   Driver = p.Driver,
                   DriverFromHost = ct => client.DownloadDriverAsync(p.Name, ct),
                   AutoInstall = true,
               }))
        {
            if (form.ShowDialog(this) != DialogResult.OK) return;
        }
        RememberHost(h.Spec);
        await LoadLocal();
    }

    void OpenPrintFiles(RemoteHost h, SharedPrinterInfo p, IEnumerable<string> files)
    {
        RememberHost(h.Spec);
        using var form = new PrintFilesForm(_s, h.Spec, KeyFor(h.Spec), h.Name, p, files);
        form.ShowDialog(this);
    }

    void CreateSendTo()
    {
        if (string.IsNullOrWhiteSpace(_s.ClientPrinter))
        {
            Warn(this, "Hãy in thử một tài liệu qua mạng trước (tab “Máy in qua mạng” → kéo thả file vào máy in) để chọn máy in dùng cho Send to.");
            return;
        }
        var r = PrinterManager.CreateSendToShortcut();
        if (r.Ok) Info(this, $"Đã thêm “In qua LAN (PrintNetwork)” vào menu chuột phải → Send to.\n\nFile sẽ được in tới “{_s.ClientPrinter}” với tùy chọn của lần in gần nhất.");
        else Error(this, "Không tạo được lối tắt: " + r.Output);
    }

    // =====================================================================
    //  Khai báo máy in với Windows
    // =====================================================================

    static void PrintUi(string command, string printer) =>
        Process.Start(new ProcessStartInfo("rundll32.exe", $"printui.dll,PrintUIEntry {command} /n \"{printer}\"") { UseShellExecute = true });

    void AddPrinter()
    {
        using var form = new PrinterSetupForm(new PrinterSetupForm.Prefill());
        if (form.ShowDialog(this) == DialogResult.OK) _ = LoadLocal();
    }

    async Task InstallInfDriver()
    {
        using var ofd = new OpenFileDialog { Filter = "Driver máy in (*.inf)|*.inf", Title = "Chọn file INF của driver máy in" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        var model = Dialogs.PickModel(this, ofd.FileName);
        if (model is null) return;
        var r = await Task.Run(() => PowerShellRunner.Run(PrinterManager.ScriptInstallDriver(model, ofd.FileName), elevated: true));
        ShowPsResult(this, r, $"Đã cài driver “{model}”.");
        if (r.Ok) Log.Info($"Đã cài driver '{model}' từ {ofd.FileName}");
    }

    async Task RemovePrinter(string name, string question)
    {
        if (MessageBox.Show(this, question, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var r = await Task.Run(() => PowerShellRunner.Run(PrinterManager.ScriptRemovePrinter(name), elevated: true));
        if (r.Ok) Log.Info($"Đã xóa máy in '{name}'");
        else Error(this, "Không xóa được máy in:\n\n" + r.Output);
        await LoadLocal();
    }
}

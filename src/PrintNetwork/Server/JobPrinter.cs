using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Text;
using PrintNetwork.Core;
using PrintNetwork.Printing;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PrintNetwork.Server;

/// <summary>In một file đã nhận trên máy host bằng driver của host.</summary>
public static class JobPrinter
{
    static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff" };
    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".csv", ".ini", ".xml", ".json", ".md" };
    static readonly HashSet<string> RawExt = new(StringComparer.OrdinalIgnoreCase)
        { ".prn", ".pcl", ".ps", ".raw", ".zpl", ".epl", ".escp", ".spl" };

    public static bool IsNativelySupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ImageExt.Contains(ext) || TextExt.Contains(ext) || RawExt.Contains(ext);
    }

    /// <returns>Số trang đã in (0 nếu không xác định được).</returns>
    public static int Print(string path, string documentName, string printer, PrintOptions options, int pdfDpi)
    {
        var ext = Path.GetExtension(documentName);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return PrintPdf(path, documentName, printer, options, pdfDpi);
        if (ImageExt.Contains(ext)) return PrintImage(path, documentName, printer, options);
        if (TextExt.Contains(ext)) return PrintText(path, documentName, printer, options);
        if (RawExt.Contains(ext))
        {
            RawPrintJob.PrintFile(printer, path, documentName, options.Copies);
            return 0;
        }
        return PrintViaShell(path, printer);
    }

    // ---------------- PDF ----------------

    static int PrintPdf(string path, string documentName, string printer, PrintOptions options, int dpi)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        PdfDocument doc;
        try
        {
            doc = PdfDocument.LoadFromStreamAsync(ms.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var detail = string.IsNullOrWhiteSpace(ex.Message) ? $"mã lỗi 0x{ex.HResult:X8}" : ex.Message.Trim();
            throw new InvalidOperationException($"Không mở được PDF – file hỏng hoặc có mật khẩu ({detail})", ex);
        }
        var pages = PageRange.Parse(options.Pages, (int)doc.PageCount);
        return PrintPages(documentName, printer, options, pages, i => RenderPdfPage(doc, i, dpi));
    }

    static Bitmap RenderPdfPage(PdfDocument doc, int index, int dpi)
    {
        using var page = doc.GetPage((uint)index);
        var widthDip = Math.Max(1.0, page.Size.Width);
        var widthPx = Math.Clamp(widthDip * dpi / 96.0, 200, 7000);
        var renderOptions = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)widthPx,
            BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId,
        };
        using var ras = new InMemoryRandomAccessStream();
        page.RenderToStreamAsync(ras, renderOptions).AsTask().GetAwaiter().GetResult();
        ras.Seek(0);
        using var stream = ras.AsStreamForRead();
        using var decoded = new Bitmap(stream);
        var bmp = new Bitmap(decoded);
        // Đặt độ phân giải để chế độ "kích thước thật" in đúng khổ trang PDF.
        var effectiveDpi = (float)(96.0 * widthPx / widthDip);
        bmp.SetResolution(effectiveDpi, effectiveDpi);
        return bmp;
    }

    // ---------------- Ảnh ----------------

    static int PrintImage(string path, string documentName, string printer, PrintOptions options)
    {
        using var img = Image.FromFile(path);
        var frames = 1;
        try { frames = img.GetFrameCount(FrameDimension.Page); } catch { }
        var pages = PageRange.Parse(options.Pages, frames);
        return PrintPages(documentName, printer, options, pages, i =>
        {
            if (frames > 1) img.SelectActiveFrame(FrameDimension.Page, i);
            var bmp = new Bitmap(img);
            bmp.SetResolution(img.HorizontalResolution, img.VerticalResolution);
            ApplyExifOrientation(img, bmp);
            return bmp;
        });
    }

    static void ApplyExifOrientation(Image source, Image target)
    {
        const int OrientationId = 0x0112;
        if (!source.PropertyIdList.Contains(OrientationId)) return;
        var value = source.GetPropertyItem(OrientationId)?.Value;
        if (value is null || value.Length < 1) return;
        var flip = value[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) target.RotateFlip(flip);
    }

    // ---------------- In từng trang ảnh (dùng chung cho PDF và ảnh) ----------------

    static int PrintPages(string documentName, string printer, PrintOptions options, List<int> pages, Func<int, Image> getPage)
    {
        if (pages.Count == 0) throw new InvalidOperationException("Không có trang nào để in (kiểm tra lại phạm vi trang)");

        using var pd = CreateDocument(documentName, printer, options, out var manualCopies);
        var sequence = new List<int>();
        for (var c = 0; c < manualCopies; c++) sequence.AddRange(pages);

        var position = 0;
        Image? current = null;
        pd.QueryPageSettings += (_, e) =>
        {
            current?.Dispose();
            current = getPage(sequence[position]);
            e.PageSettings.Landscape = options.Orientation switch
            {
                "landscape" => true,
                "portrait" => false,
                _ => current.Width > current.Height,
            };
        };
        pd.PrintPage += (_, e) =>
        {
            DrawPage(e, current!, options.Fit);
            current!.Dispose();
            current = null;
            position++;
            e.HasMorePages = position < sequence.Count;
        };
        try
        {
            pd.Print();
        }
        finally
        {
            current?.Dispose();
        }
        return sequence.Count;
    }

    static void DrawPage(PrintPageEventArgs e, Image img, string fit)
    {
        var g = e.Graphics!;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // VisibleClipBounds = vùng in được của trang (đơn vị 1/100 inch), đã tính theo hướng giấy.
        var area = g.VisibleClipBounds;
        var widthIn100 = img.Width / img.HorizontalResolution * 100f;
        var heightIn100 = img.Height / img.VerticalResolution * 100f;
        var scale = Math.Min(area.Width / widthIn100, area.Height / heightIn100);
        if (fit == "actual" && scale > 1f) scale = 1f;
        var w = widthIn100 * scale;
        var h = heightIn100 * scale;
        g.DrawImage(img, area.X + (area.Width - w) / 2f, area.Y + (area.Height - h) / 2f, w, h);
    }

    // ---------------- Văn bản ----------------

    static int PrintText(string path, string documentName, string printer, PrintOptions options)
    {
        var lines = ReadText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        using var font = new Font("Consolas", 10f);
        using var pd = CreateDocument(documentName, printer, options, out var manualCopies);
        pd.DefaultPageSettings.Margins = new Margins(50, 50, 50, 50);
        pd.DefaultPageSettings.Landscape = options.Orientation == "landscape";

        var index = 0;
        var pages = 0;
        pd.BeginPrint += (_, _) => index = 0;
        pd.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            var mb = e.MarginBounds;
            var area = new RectangleF(mb.X - e.PageSettings.HardMarginX, mb.Y - e.PageSettings.HardMarginY, mb.Width, mb.Height);
            var lineHeight = font.GetHeight(g);
            var y = area.Top;
            while (index < lines.Length)
            {
                var text = lines[index].Replace("\t", "    ");
                var h = text.Length == 0 ? lineHeight : g.MeasureString(text, font, (int)area.Width).Height;
                if (y + h > area.Bottom && y > area.Top) break;
                g.DrawString(text, font, Brushes.Black, new RectangleF(area.Left, y, area.Width, h));
                y += h;
                index++;
            }
            pages++;
            e.HasMorePages = index < lines.Length;
        };
        for (var c = 0; c < manualCopies; c++) pd.Print();
        return pages;
    }

    static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            // Không phải UTF-8: thử bảng mã tiếng Việt Windows-1258.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1258).GetString(bytes);
        }
    }

    // ---------------- Định dạng khác: nhờ ứng dụng trên host (Word, Excel…) ----------------

    static int PrintViaShell(string path, string printer)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "printto",
            Arguments = $"\"{printer}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) Thread.Sleep(15_000);
            else if (!p.WaitForExit(180_000)) Log.Warn($"Ứng dụng in '{Path.GetFileName(path)}' chưa thoát sau 3 phút");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Máy host không có ứng dụng in được file '{Path.GetExtension(path)}'. Hãy cài ứng dụng tương ứng trên host hoặc chuyển file sang PDF trước khi gửi.", ex);
        }
        return 0;
    }

    // ---------------- Thiết lập chung ----------------

    static PrintDocument CreateDocument(string documentName, string printer, PrintOptions options, out int manualCopies)
    {
        var pd = new PrintDocument
        {
            DocumentName = documentName,
            PrintController = new StandardPrintController(), // không hiện hộp thoại tiến trình
        };
        var ps = pd.PrinterSettings;
        ps.PrinterName = printer;
        if (!ps.IsValid)
        {
            pd.Dispose();
            throw new InvalidOperationException($"Không tìm thấy máy in '{printer}'");
        }

        manualCopies = 1;
        var copies = Math.Clamp(options.Copies, 1, 999);
        if (copies > 1)
        {
            if (ps.MaximumCopies >= copies)
            {
                ps.Copies = (short)copies;
                ps.Collate = true;
            }
            else
            {
                manualCopies = copies;
            }
        }

        if (ps.CanDuplex)
            ps.Duplex = options.Duplex switch
            {
                "long" => Duplex.Vertical,
                "short" => Duplex.Horizontal,
                _ => Duplex.Simplex,
            };

        pd.DefaultPageSettings.Color = options.Color && ps.SupportsColor;

        var paper = options.PaperSize?.Trim();
        if (!string.IsNullOrEmpty(paper))
        {
            var sizes = ps.PaperSizes.Cast<PaperSize>().ToList();
            var match = sizes.FirstOrDefault(s => s.PaperName.Equals(paper, StringComparison.OrdinalIgnoreCase))
                        ?? sizes.FirstOrDefault(s => s.PaperName.StartsWith(paper, StringComparison.OrdinalIgnoreCase));
            if (match is not null) pd.DefaultPageSettings.PaperSize = match;
            else Log.Warn($"Máy in '{printer}' không có khổ giấy '{paper}', dùng khổ mặc định");
        }
        return pd;
    }
}

public static class PageRange
{
    /// <summary>Đổi "1-3,5,8-" thành danh sách chỉ số trang (bắt đầu từ 0). Chuỗi rỗng = tất cả.</summary>
    public static List<int> Parse(string? spec, int count)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Enumerable.Range(0, count).ToList();
        var result = new List<int>();
        try
        {
            foreach (var raw in spec.Split(',', ';'))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                int from, to;
                var dash = part.IndexOf('-');
                if (dash < 0)
                {
                    from = to = int.Parse(part);
                }
                else
                {
                    var a = part[..dash].Trim();
                    var b = part[(dash + 1)..].Trim();
                    from = a.Length == 0 ? 1 : int.Parse(a);
                    to = b.Length == 0 ? count : int.Parse(b);
                }
                if (from > to) (from, to) = (to, from);
                for (var i = Math.Max(1, from); i <= Math.Min(count, to); i++) result.Add(i - 1);
            }
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"Phạm vi trang '{spec}' không hợp lệ");
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException($"Phạm vi trang '{spec}' không hợp lệ");
        }
        return result;
    }
}

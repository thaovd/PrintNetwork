# PrintNetwork – In nội bộ qua mạng LAN

Một file `PrintNetwork.exe` dùng cho mọi máy. Cửa sổ chỉ có 2 tab:

- **Máy in trên máy này**: máy có gắn máy in bấm **Chia sẻ** để các máy khác in được.
- **Máy in qua mạng**: các máy khác thấy máy in đang chia sẻ, bấm **Kết nối** để dùng như máy in cắm trực tiếp.

## Đóng gói

```powershell
.\publish.ps1          # dist\PrintNetwork.exe (~53 MB), copy sang máy nào cũng chạy, không cần cài .NET
.\publish.ps1 -Small   # bản ~1 MB, máy chạy cần có .NET Desktop Runtime 10 x64
```

Yêu cầu: Windows 10 1809 trở lên hoặc Windows 11, bản 64-bit.

## Máy có gắn máy in (máy chia sẻ)

1. Chạy `PrintNetwork.exe` → tab **Máy in trên máy này** → bấm **Chia sẻ** cạnh máy in.
2. Lần đầu, Windows hỏi quyền Administrator để mở cổng tường lửa. Phần mềm cũng tự bật **Khởi động cùng Windows**
   (tắt trong menu **Chia sẻ**). Dòng máy in chuyển màu xanh = đang chia sẻ.
3. (Tùy chọn) Menu **Chia sẻ → Cài đặt chia sẻ…**: tên hiển thị, **mã truy cập**, chỉ nhận lệnh từ LAN, độ nét PDF, cổng.

Máy chủ in tự chạy khi có ít nhất một máy in được chia sẻ. Bấm X chỉ thu xuống khay hệ thống; muốn tắt hẳn: menu **Chia sẻ → Thoát hẳn**
hoặc chuột phải biểu tượng dưới khay → **Thoát hẳn**. Nút ⚙ cạnh mỗi máy in: hàng đợi in, in trang thử, thuộc tính, xóa máy in.

> Máy chia sẻ phải được bật và đăng nhập Windows. Máy in ảo của Windows (Fax, Print to PDF, XPS, OneNote) được ẩn khỏi danh sách.

## Các máy khác

Mở tab **Máy in qua mạng**: phần mềm tự tìm các máy đang chia sẻ trong LAN (bấm **F5** để tìm lại).
Máy ở mạng khác hoặc không tự thấy: menu **Kết nối → Kết nối theo địa chỉ IP…**. Máy chia sẻ có đặt mã thì bấm **Nhập mã…**.

**Kết nối – cài máy in vào Windows (in từ Word, Excel, phần mềm kế toán…)**

Bấm **Kết nối**. Phần mềm tạo cổng Standard TCP/IP trỏ tới `IP-máy-chia-sẻ:cổng-RAW`, rồi cài driver theo thứ tự:

- dùng driver đã có trên máy, nếu có;
- nếu chưa có thì **tải gói driver từ máy chia sẻ** về và cài bằng `pnputil`;
- nếu không tải được (driver có sẵn của Windows) thì cài từ kho driver của Windows.

Cài lỗi thì cửa sổ cài đặt vẫn mở để chọn driver khác hoặc file INF. Xong, dòng máy in chuyển màu xanh; bấm **Ngắt kết nối** để gỡ.

> Một số driver được cài bằng bộ cài `.exe` riêng của hãng (không có gói INF), ví dụ nhiều dòng máy in bill POS. Loại này không chuyển từ host sang được, nên hãy chạy bộ cài của hãng trên client trước, rồi chọn driver đó trong danh sách.

**In tài liệu – gửi file, không cần driver**

Kéo thả file vào dòng máy in, nhấp đúp vào máy in, hoặc bấm ⚙ → **In tài liệu…**. Cửa sổ in có số bản, in 2 mặt, màu,
hướng giấy, phạm vi trang, khổ giấy; mỗi file hiện trạng thái đến khi in xong.

**In nhanh**: menu **Kết nối → Thêm “In qua LAN” vào menu chuột phải**. Sau đó chuột phải file → **Send to → In qua LAN**
(dùng máy in và tùy chọn của lần in gần nhất).

| Loại file | Cách in trên host |
|---|---|
| PDF | Render bằng Windows.Data.Pdf có sẵn trong Windows, không cần Adobe |
| JPG, PNG, BMP, GIF, TIFF nhiều trang | In trực tiếp, tự xoay theo EXIF |
| TXT, CSV, LOG | In văn bản (UTF-8 hoặc Windows-1258) |
| PRN, PCL, PS, ZPL | Gửi thô (RAW) thẳng vào máy in |
| DOCX, XLSX… | Nhờ ứng dụng trên host (host phải cài Office) |

## Menu "Máy in"

- **Thêm máy in vào Windows…**: máy in mạng theo IP (cổng TCP/IP 9100) hoặc máy in USB/LPT có sẵn cổng.
- **Cài driver từ file INF…**: chọn file `.inf` tải từ trang hãng → chọn model → cài vào Driver Store.

Các thao tác này chạy PowerShell (PrintManagement, pnputil) và cần quyền Administrator (Windows sẽ hiện hộp thoại UAC).
Lịch sử lệnh in đã nhận và nhật ký: menu **Trợ giúp → Lịch sử in và nhật ký**.

## Tự cập nhật

Bản đóng gói (`publish.ps1`) tự kiểm tra [GitHub Releases](https://github.com/thaovd/PrintNetwork/releases) 1 phút sau khi mở,
sau đó 6 tiếng/lần. Có bản mới thì tải `PrintNetwork.exe` về cạnh exe đang chạy và thay thế, không hiện thông báo gì.
Đang ẩn dưới khay và rảnh thì tự khởi động lại sang bản mới; đang mở cửa sổ hoặc đang in thì đợi, hoặc bản mới chạy ở lần mở sau.
Exe đặt trong thư mục không có quyền ghi (vd Program Files) thì không tự cập nhật được, chỉ ghi vào nhật ký.

**Phát hành bản mới**: tăng `<Version>` trong `src\PrintNetwork\PrintNetwork.csproj` → commit, `git push` → `.\release.ps1 "Ghi chú"`
(cần GitHub CLI `gh` đã đăng nhập). File đính kèm release phải tên đúng `PrintNetwork.exe`.

## Cổng mạng

| Cổng | Giao thức | Dùng cho |
|---|---|---|
| 9200 | TCP | Lệnh điều khiển + truyền file |
| 9201 | UDP | Client tự tìm host (broadcast) |
| 9100, 9101… | TCP | Cổng RAW cho máy in đã cài vào Windows trên client |

Mặc định host chỉ nhận kết nối từ địa chỉ mạng nội bộ (10.x, 172.16–31.x, 192.168.x…).

## Dữ liệu

- Cấu hình: `%AppData%\PrintNetwork\settings.json`
- Nhật ký: `%AppData%\PrintNetwork\logs\`
- File tạm khi in: `%AppData%\PrintNetwork\spool\` (xóa ngay sau khi in xong)

## Cấu trúc mã nguồn

```
src/PrintNetwork/
  Core/       Giao thức mạng, cấu hình, nhật ký, model, tự cập nhật
  Server/     PrintHost (nhận lệnh + hàng đợi), JobPrinter (in PDF/ảnh/TXT/RAW), cổng RAW, dò tìm UDP
  Remote/     PrintClient, HostDiscovery
  Printing/   winspool RAW, PowerShell/UAC, khai báo máy in/driver, đọc file INF
  UI/         Giao diện WinForms
```

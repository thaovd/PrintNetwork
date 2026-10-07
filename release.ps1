# Phát hành bản mới lên GitHub Releases. Các máy đang chạy PrintNetwork sẽ tự cập nhật chạy nền
# (kiểm tra 1 phút sau khi mở, sau đó 6 tiếng/lần), không hiện thông báo.
#
#   1. Tăng <Version> trong src\PrintNetwork\PrintNetwork.csproj (vd 1.0.0 → 1.0.1)
#   2. Commit và git push
#   3. .\release.ps1 "Sửa lỗi ..."     (bỏ trống ghi chú thì GitHub tự tạo từ các commit)
#
# Cần GitHub CLI (gh) đã đăng nhập.
param([string]$Notes = "")

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\PrintNetwork\PrintNetwork.csproj'
$version = ([xml](Get-Content $proj -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Không đọc được <Version> trong $proj" }
$tag = "v$version"

cmd /c "gh release view $tag >nul 2>&1"
if ($LASTEXITCODE -eq 0) { throw "Release $tag đã có. Hãy tăng <Version> trong PrintNetwork.csproj." }

& (Join-Path $PSScriptRoot 'publish.ps1')
$exe = Join-Path $PSScriptRoot 'dist\PrintNetwork.exe'

# Tên file phải đúng là PrintNetwork.exe – bộ tự cập nhật tìm đúng tên này trong release.
if ($Notes) { gh release create $tag $exe --title "PrintNetwork $version" --notes $Notes }
else { gh release create $tag $exe --title "PrintNetwork $version" --generate-notes }
if ($LASTEXITCODE -ne 0) { throw "gh release create thất bại" }
Write-Output "Đã phát hành $tag"

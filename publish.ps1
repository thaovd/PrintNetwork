# Đóng gói PrintNetwork thành 1 file exe trong thư mục dist\
#   .\publish.ps1            → bản tự chứa .NET (~70 MB), copy sang máy nào cũng chạy
#   .\publish.ps1 -Small     → bản nhỏ (~1 MB), máy chạy cần cài .NET Desktop Runtime 10 (x64)
param([switch]$Small)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\PrintNetwork\PrintNetwork.csproj'
$out = Join-Path $PSScriptRoot 'dist'

$common = @('-c', 'Release', '-r', 'win-x64', '-p:PublishSingleFile=true', '-p:DebugType=none', '-o', $out)
if ($Small) {
    dotnet publish $proj @common --self-contained false
} else {
    dotnet publish $proj @common --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish thất bại" }

Get-ChildItem $out -Filter *.exe | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }

<#
  build.ps1 — QuotaGauge.cs を QuotaGauge.exe にコンパイルする

  Windows 同梱の csc.exe を使うので追加インストールは要らない。
  日本語を含むので、ソースは UTF-8 BOM付きで保存すること（このスクリプトが自動で直す）。
#>
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$src = Join-Path $dir 'QuotaGauge.cs'
$out = Join-Path $dir 'QuotaGauge.exe'
$csc = "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$ico = Join-Path $dir 'app.ico'
if (-not (Test-Path $ico)) { & (Join-Path $dir 'tools\make-icon.ps1') }

$bytes = [IO.File]::ReadAllBytes($src)
if (-not ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) {
  $text = Get-Content $src -Raw -Encoding UTF8
  [IO.File]::WriteAllText($src, $text, (New-Object Text.UTF8Encoding($true)))
  Write-Host 'ソースに BOM を付与した'
}

Get-Process -Name 'QuotaGauge' -ErrorAction SilentlyContinue | ForEach-Object {
  Write-Host "起動中の QuotaGauge.exe を停止 (PID $($_.Id))"
  $_.Kill(); Start-Sleep -Milliseconds 500
}

& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
  /out:"$out" `
  /win32icon:"$ico" `
  /reference:System.dll,System.Drawing.dll,System.Windows.Forms.dll `
  "$src"

if ($LASTEXITCODE -ne 0) { throw "ビルド失敗 (exit $LASTEXITCODE)" }

$fi = Get-Item $out
$ver = [Diagnostics.FileVersionInfo]::GetVersionInfo($out)
Write-Host ''
Write-Host ('ビルド成功: {0} ({1:N0} bytes)' -f $fi.Name, $fi.Length)
Write-Host ('  FileDescription : {0}' -f $ver.FileDescription)

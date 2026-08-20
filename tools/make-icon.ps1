<#
  make-icon.ps1 — app.ico を生成する

  トレイに描いているリングと同じ図形を、同じ System.Drawing で描く。
  QuotaGauge.cs の UpdateIcon() を変えたらここも合わせれば、
  アプリのアイコンと実際の表示がずれない。

  16/24/32/48/64 は DIB、128/256 は PNG で詰める。
  （PNG 圧縮エントリは Vista 以降のシェルが読むが GDI+ の Icon.ToBitmap は読めないので、
    アプリが実際に読む小さい方は DIB にしておく）
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out    = Join-Path (Split-Path $PSScriptRoot -Parent) 'app.ico'
$dibSizes = 16, 24, 32, 48, 64
$pngSizes = 128, 256

# QuotaGauge.cs の Palette と揃える
$track = [Drawing.Color]::FromArgb(226, 232, 240)   # Border
$arc   = [Drawing.Color]::FromArgb(100, 116, 139)   # BarOk
$fill  = 0.65                                        # 状態を示唆しない程度に埋める

function New-Ring([int]$n, [Drawing.Color]$trackColor, [Drawing.Color]$arcColor, [double]$ratio) {
  $bmp = New-Object Drawing.Bitmap $n, $n, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.Clear([Drawing.Color]::Transparent)

  # 32px のとき rect(4,4,23,23) / 太さ5 になる比率
  $pad  = $n * 4.0 / 32.0
  $w    = [float]($n * 5.0 / 32.0)
  $side = [float]($n - $pad * 2 - $w * 0.2)
  $rect = New-Object Drawing.RectangleF ([float]$pad), ([float]$pad), $side, $side

  $pt = New-Object Drawing.Pen $trackColor, $w
  $g.DrawArc($pt, $rect, [float]0, [float]360)
  $pt.Dispose()

  $pa = New-Object Drawing.Pen $arcColor, $w
  $pa.StartCap = [Drawing.Drawing2D.LineCap]::Round
  $pa.EndCap   = [Drawing.Drawing2D.LineCap]::Round
  $g.DrawArc($pa, $rect, [float]-90, [float](360 * $ratio))
  $pa.Dispose()

  $g.Dispose()
  return $bmp
}

# ICO 内の DIB は「上下反転した32bppのピクセル」＋「1bppのANDマスク」で、
# ヘッダの高さは実際の2倍を書く決まりになっている
function ConvertTo-Dib([Drawing.Bitmap]$bmp) {
  $n = $bmp.Width
  $ms = New-Object IO.MemoryStream
  $bw = New-Object IO.BinaryWriter $ms

  $bw.Write([uint32]40); $bw.Write([int32]$n); $bw.Write([int32]($n * 2))
  $bw.Write([uint16]1);  $bw.Write([uint16]32); $bw.Write([uint32]0)
  $bw.Write([uint32]($n * $n * 4))
  $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)

  $rect = New-Object Drawing.Rectangle 0, 0, $n, $n
  $data = $bmp.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly,
                        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
  try {
    $stride = $data.Stride
    $buf = New-Object byte[] ($stride * $n)
    [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    for ($y = $n - 1; $y -ge 0; $y--) { $bw.Write($buf, $y * $stride, $n * 4) }
  } finally { $bmp.UnlockBits($data) }

  # ANDマスクは全部0（透過はアルファで表す）。行は4バイト境界へ揃える
  $maskRow = [math]::Floor(($n + 31) / 32) * 4
  $bw.Write((New-Object byte[] ($maskRow * $n)), 0, $maskRow * $n)

  $bw.Flush()
  return ,$ms.ToArray()
}

function ConvertTo-Png([Drawing.Bitmap]$bmp) {
  $ms = New-Object IO.MemoryStream
  $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
  return ,$ms.ToArray()
}

$entries = @()
foreach ($s in ($dibSizes + $pngSizes)) {
  $bmp = New-Ring $s $track $arc $fill
  [byte[]]$blob = if ($pngSizes -contains $s) { ConvertTo-Png $bmp } else { ConvertTo-Dib $bmp }
  $bmp.Dispose()
  if ($blob.Length -lt 64) { throw "${s}px の生成に失敗した ($($blob.Length) bytes)" }
  $entries += [pscustomobject]@{ Size = $s; Data = $blob }
}

$fs = [IO.File]::Create($out)
$bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
  $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }   # 256 は 0 で表す決まり
  $bw.Write([byte]$dim); $bw.Write([byte]$dim)
  $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$e.Data.Length)
  $bw.Write([uint32]$offset)
  $offset += $e.Data.Length
}
foreach ($e in $entries) { [byte[]]$d = $e.Data; $bw.Write($d, 0, $d.Length) }
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host ('app.ico: {0:N0} bytes / {1} サイズ ({2})' -f `
  (Get-Item $out).Length, $entries.Count, (($entries | ForEach-Object { $_.Size }) -join ', '))

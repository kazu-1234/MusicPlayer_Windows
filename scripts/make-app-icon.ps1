# アプリアイコン（Assets\AppIcon.ico）を Android 版のアイコンから作る。
# Android 版は「白い背景 + 黒い音符」のアダプティブアイコン（108×108 のベクター）。
# その音符のパスデータを読み取り、白い角丸四角の上に黒で描いて、複数サイズ入りの .ico にまとめる。
# Android 側のファイルは読むだけで、何も書き換えない。生成した .ico は git に入れるので、普段は実行不要。
# 使い方: powershell -ExecutionPolicy Bypass -File scripts\make-app-icon.ps1 [-AndroidProject <パス>]

param(
    [string]$AndroidProject = "C:\Users\kazuh\Documents\AndroidStudioProjects\MusicPlayer"
)

Add-Type -AssemblyName System.Drawing

# ───────── 調整値 ─────────
$Sizes = 16, 24, 32, 48, 64, 128, 256   # .ico に入れるサイズ（px）
$Viewport = 108                          # Android のアダプティブアイコンの座標の広さ
$VisibleInset = 18                       # 外側 18 は端末で切り取られる余白（見えるのは中央の 72 四方）
$CornerRatio = 0.18                      # 角丸の半径（1 辺に対する割合）
$Background = [Drawing.Color]::White
$Foreground = [Drawing.Color]::Black

$root = Split-Path $PSScriptRoot -Parent
$fgXml = Join-Path $AndroidProject "app\src\main\res\drawable\ic_launcher_foreground.xml"
[xml]$doc = Get-Content $fgXml -Encoding UTF8
$ns = @{ android = "http://schemas.android.com/apk/res/android" }
$pathData = (Select-Xml -Xml $doc -XPath "//path" -Namespace $ns | Select-Object -First 1).Node.GetAttribute("pathData", $ns.android)

# ───────── パスデータ（SVG と同じ書式）を GraphicsPath に変換 ─────────
# 対応する命令: M/m（移動）・H/h・V/v（水平・垂直線）・L/l（直線）・C/c（3 次ベジェ）・S/s（滑らかな 3 次ベジェ）・Z/z（閉じる）
# 大文字は絶対座標、小文字は今の位置からの相対座標。
function ConvertTo-GraphicsPath([string]$data) {
    $tokens = [regex]::Matches($data, '[MmHhVvLlCcSsZz]|-?\d*\.?\d+(?:[eE][-+]?\d+)?') | ForEach-Object { $_.Value }
    $gp = New-Object Drawing.Drawing2D.GraphicsPath
    $x = 0.0; $y = 0.0; $startX = 0.0; $startY = 0.0
    $lastCtrlX = $null; $lastCtrlY = $null   # 直前の C/S の 2 つ目の制御点（S で使う）
    $cmd = ''; $i = 0
    function Num { $script:i++; return [double]$tokens[$script:i - 1] }
    $script:i = 0
    while ($script:i -lt $tokens.Count) {
        if ($tokens[$script:i] -match '^[A-Za-z]$') { $cmd = $tokens[$script:i]; $script:i++ }
        $rel = $cmd -cmatch '[a-z]'
        switch -CaseSensitive ($cmd.ToUpper()) {
            'M' {
                $nx = Num; $ny = Num
                if ($rel) { $nx += $x; $ny += $y }
                $gp.StartFigure(); $x = $nx; $y = $ny; $startX = $x; $startY = $y
                $cmd = if ($rel) { 'l' } else { 'L' }   # M の後に続く座標は直線として扱う（SVG の決まり）
                $lastCtrlX = $null
            }
            'L' { $nx = Num; $ny = Num; if ($rel) { $nx += $x; $ny += $y }; $gp.AddLine([single]$x, [single]$y, [single]$nx, [single]$ny); $x = $nx; $y = $ny; $lastCtrlX = $null }
            'H' { $nx = Num; if ($rel) { $nx += $x }; $gp.AddLine([single]$x, [single]$y, [single]$nx, [single]$y); $x = $nx; $lastCtrlX = $null }
            'V' { $ny = Num; if ($rel) { $ny += $y }; $gp.AddLine([single]$x, [single]$y, [single]$x, [single]$ny); $y = $ny; $lastCtrlX = $null }
            'C' {
                $x1 = Num; $y1 = Num; $x2 = Num; $y2 = Num; $ex = Num; $ey = Num
                if ($rel) { $x1 += $x; $y1 += $y; $x2 += $x; $y2 += $y; $ex += $x; $ey += $y }
                $gp.AddBezier([single]$x, [single]$y, [single]$x1, [single]$y1, [single]$x2, [single]$y2, [single]$ex, [single]$ey)
                $lastCtrlX = $x2; $lastCtrlY = $y2; $x = $ex; $y = $ey
            }
            'S' {
                # 1 つ目の制御点は、直前の 2 つ目の制御点を今の位置で折り返した点（滑らかにつなぐため）
                $x2 = Num; $y2 = Num; $ex = Num; $ey = Num
                if ($rel) { $x2 += $x; $y2 += $y; $ex += $x; $ey += $y }
                if ($null -ne $lastCtrlX) { $x1 = 2 * $x - $lastCtrlX; $y1 = 2 * $y - $lastCtrlY } else { $x1 = $x; $y1 = $y }
                $gp.AddBezier([single]$x, [single]$y, [single]$x1, [single]$y1, [single]$x2, [single]$y2, [single]$ex, [single]$ey)
                $lastCtrlX = $x2; $lastCtrlY = $y2; $x = $ex; $y = $ey
            }
            'Z' { $gp.CloseFigure(); $x = $startX; $y = $startY; $lastCtrlX = $null }
        }
    }
    return ,$gp
}

$note = ConvertTo-GraphicsPath $pathData

# 1 サイズ分の PNG を作る
function New-IconPng([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([Drawing.Color]::Transparent)

    # 背景: 白い角丸四角
    $r = [Math]::Max(1, $size * $CornerRatio)
    $bg = New-Object Drawing.Drawing2D.GraphicsPath
    $bg.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $bg.AddArc($size - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $bg.AddArc($size - 2 * $r - 1, $size - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $bg.AddArc(0, $size - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $bg.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush $Background), $bg)

    # 音符: Android の見える範囲（中央の 72 四方）が画像全体になるよう拡大・移動して描く
    $visible = $Viewport - 2 * $VisibleInset
    $scale = $size / $visible
    $m = New-Object Drawing.Drawing2D.Matrix
    $m.Scale([single]$scale, [single]$scale)
    $m.Translate([single](-$VisibleInset), [single](-$VisibleInset))
    $p = $note.Clone(); $p.Transform($m)
    $g.FillPath((New-Object Drawing.SolidBrush $Foreground), $p)

    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

$pngs = $Sizes | ForEach-Object { ,(New-IconPng $_) }

# ICO 形式: ヘッダー(6 byte) + 画像ごとの目録(16 byte) + 画像データ（PNG をそのまま入れる）
$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ms
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$Sizes.Count)
$offset = 6 + 16 * $Sizes.Count
for ($k = 0; $k -lt $Sizes.Count; $k++) {
    $s = $Sizes[$k]
    $dim = if ($s -ge 256) { 0 } else { $s }      # 256 は 0 と書く決まり
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$k].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$k].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
$out = Join-Path $root "Assets\AppIcon.ico"
[IO.File]::WriteAllBytes($out, $ms.ToArray())
"作成: $out（$($Sizes -join ', ') px）"

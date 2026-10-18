# タスクバーのプレビューに出すボタン（前へ／再生／一時停止／次へ）のアイコンを作る。
# Segoe Fluent Icons（Windows 11 標準のアイコン用フォント）のグリフを 16px と 32px の画像にして、
# 1 つの .ico（PNG 入り）にまとめて Assets\ に保存する。生成した .ico は git に入れるので、普段は実行不要。
# タスクバーの色（Windows モード）に合わせて 2 組作る:
#   Thumb*.ico      … 白で描く（ダークのタスクバー用）
#   Thumb*Light.ico … 黒で描く（ライトのタスクバー用）。どちらを使うかはアプリ（TaskbarThumbButtons.cs）が選ぶ
# 使い方: powershell -ExecutionPolicy Bypass -File scripts\make-thumb-icons.ps1

Add-Type -AssemblyName System.Drawing

# 出力先（このスクリプトの 1 つ上 = プロジェクト直下の Assets）
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) "Assets"

# ファイル名 → グリフ（Segoe Fluent Icons の文字コード）
$icons = [ordered]@{
    "ThumbPrevious.ico" = 0xE892
    "ThumbPlay.ico"     = 0xE768
    "ThumbPause.ico"    = 0xE769
    "ThumbNext.ico"     = 0xE893
}

# 1 サイズ分の PNG を作る。$brush はグリフの色（白 = ダーク用 / 黒 = ライト用）
function New-GlyphPng([int]$glyph, [int]$size, $brush) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([Drawing.Color]::Transparent)
    # フォントサイズはピクセル指定。少し余白を残すため 80% にする
    $font = New-Object Drawing.Font "Segoe Fluent Icons", ($size * 0.8), ([Drawing.GraphicsUnit]::Pixel)
    $format = New-Object Drawing.StringFormat
    $format.Alignment = [Drawing.StringAlignment]::Center
    $format.LineAlignment = [Drawing.StringAlignment]::Center
    $rect = New-Object Drawing.RectangleF 0, 0, $size, $size
    $g.DrawString([string][char]$glyph, $font, $brush, $rect, $format)
    $g.Dispose(); $font.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

# 出力する組: ファイル名の後ろに付ける文字 → 色
$variants = [ordered]@{
    ""      = [Drawing.Brushes]::White
    "Light" = [Drawing.Brushes]::Black
}

foreach ($suffix in $variants.Keys) {
foreach ($baseName in $icons.Keys) {
    $brush = $variants[$suffix]
    # ThumbPlay.ico → ThumbPlayLight.ico のように、拡張子の前に付ける
    $name = [IO.Path]::GetFileNameWithoutExtension($baseName) + $suffix + ".ico"
    $sizes = 16, 32
    $pngs = $sizes | ForEach-Object { ,(New-GlyphPng $icons[$baseName] $_ $brush) }

    # ICO 形式: ヘッダー(6 byte) + 画像ごとの目録(16 byte) + 画像データ（ここでは PNG をそのまま入れる）
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $w.Write([byte]$s); $w.Write([byte]$s)      # 幅・高さ（256 未満はそのままの値）
        $w.Write([byte]0); $w.Write([byte]0)        # 色数・予約
        $w.Write([UInt16]1); $w.Write([UInt16]32)   # プレーン数・色深度
        $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($png in $pngs) { $w.Write($png) }
    $w.Flush()
    [IO.File]::WriteAllBytes((Join-Path $assets $name), $ms.ToArray())
    "作成: $name"
}
}

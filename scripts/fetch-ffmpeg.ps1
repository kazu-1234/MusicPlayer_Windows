# ffmpeg（LGPL ビルド）を取得して ThirdParty\ffmpeg\ に置く。
# ビルド時に MusicPlayer.csproj が出力フォルダの ffmpeg\ へコピーし、変換・CD 取り込みで使う。
# ThirdParty\ は .gitignore 済み（大きいバイナリをリポジトリに入れない）。
#
# 使い方:
#   powershell -ExecutionPolicy Bypass -File scripts\fetch-ffmpeg.ps1

$ErrorActionPreference = "Stop"
chcp 65001 | Out-Null

# ───────── 調整値 ─────────
# BtbN/FFmpeg-Builds の LGPL 版（GPL 部品を含まないので MIT のアプリに同梱しやすい）。
# 内蔵 AAC エンコーダと libmp3lame（LGPL）を含む。
$ReleaseApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest"
# 安定版（n○.○）の単体 exe 版。DLL 分割版（-shared）や開発版（master）は使わない
$AssetPattern = '^ffmpeg-n(\d+)\.(\d+)-latest-win64-lgpl-\d+\.\d+\.zip$'
# ─────────────────────────

# 配布ファイル名は版が上がるたびに変わるので、API で一覧を取り最新の安定版を選ぶ
$release = Invoke-RestMethod -Uri $ReleaseApi -Headers @{ "User-Agent" = "MusicPlayer-build" }
$asset = $release.assets |
    Where-Object { $_.name -match $AssetPattern } |
    Sort-Object { [version]("{0}.{1}" -f ([regex]::Match($_.name, $AssetPattern).Groups[1].Value), ([regex]::Match($_.name, $AssetPattern).Groups[2].Value)) } -Descending |
    Select-Object -First 1
if (-not $asset) { throw "LGPL win64 asset not found" }
$AssetName = $asset.name

$root    = Split-Path -Parent $PSScriptRoot
$destDir = Join-Path $root "ThirdParty\ffmpeg"
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ("musicplayer-ffmpeg-" + [Guid]::NewGuid().ToString("N"))
$zipPath = Join-Path $tempDir $AssetName
$url     = $asset.browser_download_url

New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
try {
    Write-Host "==> Download $url"
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing

    Write-Host "==> Extract"
    Expand-Archive -Path $zipPath -DestinationPath $tempDir -Force

    # zip の中は「ffmpeg-…\bin\ffmpeg.exe」「ffmpeg-…\LICENSE.txt」という構成
    $exe = Get-ChildItem $tempDir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    if (-not $exe) { throw "ffmpeg.exe not found in archive" }
    $license = Get-ChildItem $tempDir -Recurse -Filter "LICENSE.txt" | Select-Object -First 1

    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Copy-Item $exe.FullName (Join-Path $destDir "ffmpeg.exe") -Force
    if ($license) { Copy-Item $license.FullName (Join-Path $destDir "LICENSE.txt") -Force }

    # 必要なエンコーダが入っているか確認する（MP3 = libmp3lame / AAC = aac / FLAC / ALAC）
    $encoders = & (Join-Path $destDir "ffmpeg.exe") -hide_banner -encoders 2>$null | Out-String
    foreach ($name in "libmp3lame", " aac ", " flac ", " alac ") {
        if ($encoders -notmatch [regex]::Escape($name)) { Write-Warning "encoder missing: $($name.Trim())" }
    }

    Write-Host ("OK: {0}" -f (Join-Path $destDir "ffmpeg.exe"))
}
finally {
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}

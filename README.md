# MusicPlayer

Windows 向けの軽量な音楽プレーヤーです（WinUI 3 / Windows App SDK）。
再生画面は Windows の「メディア プレーヤー」に近い見た目で、編集系の機能も同じウィンドウ内のページとして切り替えて使えます。

## 主な機能

- **音楽ライブラリ**: 曲 / アルバム / アーティスト別に表示。形式フィルタ（FLAC / MP3 / WAV など）と検索で絞り込み
- **再生**: シャッフル・リピート（全曲 / 1 曲）・曲間の途切れない再生・メディアキー対応・再生キュー
- **プレイリスト**: m3u / m3u8 の読み込み・作成・名前変更・削除・並べ替え・曲の追加 / 削除（元ファイルの文字コード・改行・書式を保って保存）
- **CD 取り込み**: WAV / FLAC / MP3 / AAC。MusicBrainz から曲名・ジャケットを自動取得
- **形式変換**: WAV / FLAC / MP3 / AAC (.m4a) / ALAC。MP3・AAC は「音質優先 / バランス / サイズ優先」を切り替え（320 / 256 / 128 kbps）
- **タグ編集**: 曲名・アーティスト・アルバム・年・トラック番号・ジャンル・ジャケットなど。複数ファイルの一括編集に対応

## 動作環境

- Windows 10 (1809) 以降 / Windows 11（x64）

## ビルド

1. ffmpeg を取得します（変換・CD 取り込みに使用。`ThirdParty\ffmpeg\` に置かれ、git には含めません）

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\fetch-ffmpeg.ps1
   ```

2. Visual Studio で `MusicPlayer.sln` を開き、**x64** でビルド / 実行します。
   共通ライブラリ `WinUiShared` は隣のフォルダの `WinUiAppTemplate\Shared` を参照します。

インストーラは `scripts\build-installer.ps1`（Inno Setup 6 以降が必要）で作成できます。

## データの保存場所

`%AppData%\MusicPlayer\` に設定（settings.json）・ライブラリのキャッシュ・ジャケットの縮小画像を保存します。
アンインストール時はこのフォルダも削除されます。

## ライセンス

[MIT License](LICENSE)

同梱・利用しているサードパーティ（FFmpeg / TagLib# は LGPL v2.1）については [LICENSE_NOTICE.txt](LICENSE_NOTICE.txt) を参照してください。

## 免責事項

> 本ソフトウェアの使用により生じたいかなる損害についても、開発者は一切の責任を負いません。自己責任でご使用ください。
> タグ編集・形式変換・プレイリスト保存は音楽ファイルやプレイリストを書き換えます。大切なファイルは事前にバックアップしてください。

詳細は [LICENSE_NOTICE.txt](LICENSE_NOTICE.txt) を参照してください。

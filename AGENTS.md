# MusicPlayer

C# / WinUI 3 の実装ルールは **[ARCHITECTURE.md](ARCHITECTURE.md)** が正本（`WinUiAppTemplate` から複製）。Cursor 用の要約は Skill `winui-csharp`（使うときだけ）。

- 型: LeftHandDevice 型（トレイ・自動起動なし、× でプロセス終了）。
- 共通ライブラリ `WinUiShared` は `..\WinUiAppTemplate\Shared` を ProjectReference で参照する。
- 調整値（UI サイズ・ビットレート・並列数・間隔など）は `AppConfig.cs` に集約する。
- 再生・ライブラリ・プレイリストのサービスは `AppRuntime` が所有する（ウィンドウを作り直しても再生を続けるため）。
- ffmpeg は `scripts\fetch-ffmpeg.ps1` で `ThirdParty\ffmpeg\` に取得する（git 管理外）。
- PowerShell スクリプト（.ps1）は Windows PowerShell 5.1 で日本語を正しく読ませるため UTF-8（BOM 付き）で保存する。
- ファイル選択は `Microsoft.Windows.Storage.Pickers`（WindowId 方式）を使う。旧 `Windows.Storage.Pickers` はこの環境でダイアログが出ないことがあった。

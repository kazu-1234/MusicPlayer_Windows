# WinUI 3 アプリ アーキテクチャガイド

このドキュメントは、BlueShift / SmartPowerManager で確立した WinUI 3（Windows App SDK, unpackaged）アプリのアーキテクチャを整理したものです。
このテンプレート（`WinUiAppTemplate`）はこのアーキテクチャに従って実装されています。**新しい C# WinUI 3 アプリを作る際は、このテンプレートをコピーし、本ドキュメントのルールを維持したまま拡張してください。** Cursor 向け要約は Skill `winui-csharp`（WinUI / `.cs` / `.xaml` のときだけ）。詳細・数値・アンチパターンは本ファイルが正本。

### 派生アプリの型（エージェント必守）

新規は本フォルダを複製し、名前 / Mutex / アイコン / Release URL を差し替える。

- **プロセス**: `AppRuntime`。Window は都度 `new` / `Close`。`DispatcherShutdownMode.OnExplicitShutdown`。終了は `ExitApplication()` のみ。× で `Hide` 再利用は禁止。TitleBar は `ExtendsContentIntoTitleBar` + `SetTitleBar` + hover のみ。
- **新規の既定 = LeftHandDevice 型**: トレイ・自動起動なし、× でプロセス終了、Nav（Home / Info / Settings）+ Frame、`PageScrollHost`。Settings / Info の項目・枠・余白は SPM / BlueShift / 本テンプレ準拠（テーマ3択、Info 更新 UI は SPM。独自巨大化禁止）。
- **本テンプレ本体**はトレイ常駐型（SPM / BlueShift と同系統）。トレイ常駐が必要なアプリだけ、明示指示のうえでトレイ実装を残す。
- **AutoStart** は明示指示時のみ。SPM / BlueShift は同型に保つ（一方だけ独自化しない）。5 章: タスクスケジューラのログオンのみ（レジストリ `Run` 禁止）、`exe --background`、`LogonTrigger.Delay = 15s`、`--sync-autostart` / `--cleanup-autostart` と `Settings.AutoStart` を同期。参考: 両アプリの `StartupManager`。
- **常駐時**: `--background` は GUI なし。通常起動は既常駐でも `requestInteractiveShow` → `InteractiveShowEvent` → `ShowOrCreateMainWindow`。二次起動は依頼後 Exit。`--background` の二重起動は表示依頼しない。
- **コア適用**（起動・スリープ復帰とも必須）: Force 即時 + **800 / 2000 / 5000 / 15000 / 30000 / 60000 / 90000 ms** 再適用（`WinUiShared.ResumeReapplyCoordinator`）。トレイ成功 ≠ 完了。`WinUiShared.SystemEventWindow` で resume / `GUID_CONSOLE_DISPLAY_STATE` On（`6fe69556-704a-47a0-aa35-2f285d73bf87`）/ `GUID_MONITOR_POWER_ON` / `GUID_LIDSWITCH_STATE_CHANGE` 開 / `GUID_SESSION_DISPLAY_STATUS` On / unlock / `WM_DISPLAYCHANGE`。スリープ入り（`PBT_APMSUSPEND`）は記録のみ。メッセージ欠落は `GetTickCount64` と `QueryUnbiasedInterruptTime` の差で検知する。unlock は起動パスに来ないことが多いので起動時は `BeginStartupPeriod` を別途呼ぶ。復帰時は WinUI `DispatcherTimer` を Stop→Start。監視 HWND の初期化を落とさない。ログオンタスクに SessionUnlock を付けない。

## 1. 全体像（プロセス寿命とウィンドウ寿命の分離）

最も重要な原則は「**プロセスの寿命**」と「**MainWindow の寿命**」を分離することです。

```
Program.Main
  └─ App (Application)                 … プロセス全体で 1 個。DispatcherShutdownMode = OnExplicitShutdown
       └─ AppRuntime                    … プロセスが生きている間ずっと 1 個。トレイ・二重起動リスナーを所有
            └─ MainWindow（0 個か 1 個） … 表示するたびに new、閉じるたびに破棄（ADM = Auto Dark Mode 方式）
```

- **AppRuntime** がプロセスの実体（トレイアイコン・バックグラウンドサービス・二重起動時の Show/Exit イベント監視）を持ちます。
- **MainWindow** は「今画面に表示されているウィンドウ」でしかありません。閉じたら本当に破棄し、再度開くときは new して作り直します。
- ユーザーが × を押しても **プロセスは終了しません**（トレイを使う場合）。ExitApplication が呼ばれるまで `AppRuntime` は生き続けます。

### なぜこの構造にするか

- WinUI の `Window` はいちど Close すると内部状態が壊れやすく、同じインスタンスを Hide/Show して再利用すると白画面・入力不能などの不具合が起きやすい。
- タスクトレイ常駐アプリでは「ウィンドウを閉じる」と「アプリを終了する」は別の操作。この区別をコード上でも明確に分離する。

## 2. 各コンポーネントの責務

### App（`App.xaml.cs`）

- `DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown` を **必ず** コンストラクタで設定する。
  - これを忘れると、最後の `Window` が閉じた瞬間にプロセスが終了してしまい、トレイ常駐が機能しない。
- `OnLaunched` の役割は最小限にする:
  1. `UpdateChecker.LatestReleaseApiUrl` を設定する（未リリースなら `null` のままでよい）。
  2. `Settings.Load()` で設定を読み込む。
  3. `--background` 引数を見て `launchInBackground` / `requestInteractiveShow` を決める。
  4. `SingleInstanceManager.TryBecomePrimaryInstance(...)` で二重起動をチェックする。二重起動なら既存インスタンスに表示を依頼して `Exit()`。
  5. `AppRuntime` を生成して `Start(...)` を呼ぶ。
- ドメインロジック（バックグラウンド処理・トレイ操作など）は **App に書かない**。すべて `AppRuntime` に委譲する。

### AppRuntime（`AppRuntime.cs`）

プロセス寿命そのものを表すクラス。責務は次のとおり：

1. **タスクトレイの所有**（`TrayMessageWindow` / `TrayIconService`）
2. **二重起動イベントの監視**（`SingleInstanceManager.InteractiveShowEvent` / `ExitEvent` + show_signal）
3. **MainWindow の生成・表示・破棄の管理**（`ShowOrCreateMainWindow` / `OnMainWindowClosing` / `MainWindow_Closed`）
4. **システム復帰監視**（`WinUiShared.SystemEventWindow` + `ResumeReapplyCoordinator` → `SystemResumed`。コア再適用は派生アプリがコールバック接続）
5. **アプリ終了処理の一本化**（`ExitApplication()`）

バックグラウンドサービス（スケジューラ・監視タイマーなど）を追加する場合も、この `AppRuntime` が所有し、起動・停止のタイミングを管理します（BlueShift のガンマ制御、SmartPowerManager のスケジュール実行サービスが実例）。

```csharp
public void ShowOrCreateMainWindow(string? pageTag = null)
{
    if (_isExitingProcess) return;
    _uiDispatcher.TryEnqueue(() => ShowOrCreateMainWindowCore(pageTag));
}

private void ShowOrCreateMainWindowCore(string? pageTag = null)
{
    if (_mainWindow != null)
    {
        BringWindowToForeground(_mainWindow);
        if (pageTag != null) _mainWindow.NavigateToPageTag(pageTag);
        return;
    }

    _mainWindow = new MainWindow(this);
    _mainWindow.Closed += MainWindow_Closed;
    _mainWindow.PrepareAndActivate(pageTag);
}
```

`ExitApplication()` はアプリを完全終了する **唯一の入口** です。トレイの「終了」メニューも、`--exit` コマンドライン引数（インストーラからの終了依頼）も、最終的にこの 1 メソッドだけを呼びます。

```csharp
public void ExitApplication()
{
    if (_isExitingProcess) return;
    _isExitingProcess = true;

    // リスナー停止 → ResumeReapplyCoordinator / SystemEventWindow / トレイ破棄 → SingleInstanceManager.Release() → MainWindow.Close() → _app.Exit()
}
```

### MainWindow（`MainWindow.xaml.cs`）

MainWindow は **UI 専業** です。プロセス寿命には関与しません。

- コンストラクタで `AppRuntime` を受け取り、`AppState`（設定など共有状態）を取得する。
- `PrepareAndActivate(pageTag)` の順序を厳守する:
  1. `NavigateToPage(...)`（最初のページへ遷移）
  2. `RestoreWindowBounds()`（`WindowPlacementHelper` で位置・サイズ・最大化状態を復元）
  3. `Activate()`
  - この順序は Auto Dark Mode と同じで、ちらつきや位置ズレを防ぎます。
- **× ボタン（`AppWindow.Closing`）は本当に `Close` させます。** `args.Cancel = true` にして `Hide()` するような実装は禁止です（詳細は 4 章）。
- ウィンドウの位置・サイズは `AppWindow.Changed` で変化を検知し、`WindowPlacementHelper.Save` で `Settings` に保存します。

### TitleBarThemeHelper（`TitleBarThemeHelper.cs`）

タイトルバーのテーマ同期は **3行だけ** で完結させます。

```csharp
_window.ExtendsContentIntoTitleBar = true;
_window.SetTitleBar(_titleBar);   // titleBar は XAML 上の <TitleBar x:Name="AppTitleBar">
_window.AppWindow.TitleBar.ButtonHoverBackgroundColor = hover; // テーマに応じた半透明色のみ
```

- MainWindow.xaml 側は `Grid.RowDefinitions="Auto,*"` の 1 行目に `<TitleBar x:Name="AppTitleBar">` を置くだけ。カスタム Grid で余白列を作ったり、キャプションボタンの座標を計算したりしない。
- `ButtonBackgroundColor` / `ButtonForegroundColor` / `ButtonPressedBackgroundColor` などを個別に設定する「キャプションボタンの手動色付け」は **行わない**。WinUI 3 の `TitleBar` コントロール（`ExtendsContentIntoTitleBar` 前提）はテーマに応じて自動的に描画されるため、必要なのはホバー色の半透明オーバーレイのみです。
- ウィンドウをオフスクリーンに一瞬 Move してから戻す、`ResetCaptionButtons` 的な再描画ハックは不要です（旧実装の名残があれば削除してください）。

## 3. タスクトレイとバックグラウンド起動

### タスクトレイ（`TrayMessageWindow.cs` / `TrayIconService.cs`）

- トレイ通知には **専用の非表示ウィンドウ**（`TrayMessageWindow`、Win32 の `CreateWindowEx` + `WS_POPUP`）を使います。MainWindow の `WndProc` を差し替えて `WM_TRAYICON` を処理する実装は禁止です（WinUI 側の再描画が壊れて白画面になることがあるため）。
- `TrayIconService` は `Shell_NotifyIcon` による最小実装（ダブルクリックでメイン画面、右クリックで「設定を開く / 終了」の 2 項目メニュー）。サードパーティのトレイライブラリは必須ではありません。
- `Settings.HideTrayIcon` が true のときはトレイアイコンを非表示にできますが、`AppRuntime` 自体は常駐を続けます（トレイを消す ≠ 常駐をやめる）。

### バックグラウンド起動（`--background`）

- `--background` 引数付きで起動すると、`MainWindow` を表示せずトレイ・バックグラウンドサービスだけを起動します。
- 二重起動時、既存インスタンスに「画面を開いてほしい」と伝えるかどうかは `requestInteractiveShow`（= `!launchInBackground`）で判定します。`--background` の二重起動では通知しません。
- **トレイ表示成功 ≠ コア機能が動いている**。トレイは Win32 `Shell_NotifyIcon` で早期に成功しやすい一方、ガンマ適用・監視タイマー等はログオン直後のデスクトップ未準備で失敗／上書きされうる。`--background` でもコア初期化をスキップしてはいけない（スキップ分岐を作らない）。

### 終了経路の一本化（`--exit`）

- `Program.cs` で `--exit` を検出したら、**WinUI を起動せず** `SingleInstanceManager.SignalExit()` を呼んで既存プロセスに終了イベントを送り、即座に戻ります（インストーラのアンインストール前フックなどで使用）。
- 受け取った側は `AppRuntime` のリスナーが `ExitApplication()` を呼び、`ExitApplication → Application.Exit()` の 1 経路だけで終了します。トレイの「終了」メニューも同じ `ExitApplication()` を呼びます。

### 二重起動制御（`SingleInstanceManager.cs`）

- 名前付き `Mutex` で多重起動を検出し、名前付き `EventWaitHandle`（`InteractiveShowEvent` / `ExitEvent`）で既存インスタンスに「表示」「終了」を伝えます。
- プライマリは `%AppData%\<AppName>\.instance_pid` を書き、二次起動側は `AllowSetForegroundWindow` で前面化を許可します。Event 失敗時は `.show_signal` ファイルへフォールバックし、プライマリの `ListenShowLoop` が `TryConsumeShowSignal` で消費します。
- 複製したプロジェクトでは `Mutex` / イベント名に含まれるアプリ名部分を変更し、他アプリと衝突しないようにしてください。

## 4. やってはいけないこと（アンチパターン）

過去の実装で問題が出た/回避した方法です。**このテンプレート・派生プロジェクトでは使用しないでください。**

| アンチパターン | 問題点 | 代わりにすること |
|---|---|---|
| × ボタンで `args.Cancel = true` にして `Window.Hide()` する | WinUI の `Window` は Hide/Show の再利用でレイアウト崩壊・入力不能になりやすい | 本当に `Close()` する。プロセス常駐は `AppRuntime` が担う |
| タイトルバーのキャプションボタン背景・前景色を個別に手動設定する | テーマ切替時に反映漏れが起きやすく、保守コストが高い | `ExtendsContentIntoTitleBar` + `SetTitleBar` + `ButtonHoverBackgroundColor` のみ |
| ウィンドウをオフスクリーンに `Move` してから戻す再描画ハック | 副作用が読みにくく、フリッカーの原因にもなる | 上記の TitleBar 方式にすれば不要 |
| MainWindow の `WndProc` を差し替えてトレイメッセージを処理する | WinUI の内部メッセージ処理と衝突し、白画面などの不具合が起きる | `TrayMessageWindow`（専用の非表示ウィンドウ）を使う |
| `App.Exit()` 相当の終了処理を複数箇所に書く | 終了漏れ・二重終了・トレイだけ残る等の不整合が起きる | `AppRuntime.ExitApplication()` に一本化する |
| 起動時に無条件でログオン自動起動を登録する | ユーザーの意図しない常駐増加、セキュリティ上の懸念 | 5 章参照。既定では実装しない |
| ログオンタスクの `Delay = TimeSpan.Zero` のままコア機能を一回だけ適用する | トレイは出るがガンマ／監視などが未適用のまま残ることがある | 5 章の「ログオン起動時のコア適用」を守る |

## 5. オプション機能（AutoStart は Optional パック）

### ログオン時自動起動（AutoStart / StartupManager）

- テンプレート本体に **StartupManager.cs を統合済み**（BlueShift / SmartPowerManager と同型）。
- タスク作成・レジストリ Run・Delay 15s の**ロジック正本**は `Shared/WinUiShared` の `AutostartService`。各アプリの `StartupManager` はアプリ名などの識別子を渡す薄い入口。
- Optional/AutoStart は参照用の断片として同梱。新規プロジェクト複製時も本体の実装を正本とする。
- **実装時の必須ルール（ログオン起動時のコア適用）**:
  1. ログオンタスクは `exe --background` で起動する（レジストリ `Run` は使わない）。
  2. `LogonTrigger.Delay` は **`TimeSpan.FromSeconds(15)`**（デスクトップ／表示まわりの安定待ち）。`Delay = Zero` は使わない。`StartWhenAvailable = true` は維持してよい。
  3. `AppRuntime.Start` でトレイより前または直後にコア機能を初期化する（`--background` でもスキップしない）。
  4. 初回適用は `DispatcherTimer` 待ちのアニメ遷移に頼らず **即時／Force 適用**する。
  5. 起動直後に **800 / 2000 / 5000 ms** の遅延再適用（またはタイマー再始動などのヘルスチェック）を必ず入れる。スリープ復帰・セッション解除向けの再適用がある場合も、**起動パスでは別途**呼ぶ（ログオン直後は unlock イベントが来ないことが多い）。
  6. 既存ユーザー向けに、**primary インスタンスだけ**が起動時 `SyncAutostartWithSettings` でタスク定義を更新する。`--background` の二次起動ではタスクを触らない（CreateOrUpdate が既存プロセスを落とすため）。
  7. ログオンタスクに **SessionUnlock トリガーを付けない**。スリープ復帰は `SystemEventWindow`（プロセス内）で行う。Unlock で `exe --background` を再起動すると、まれに本体が終了する。

### スリープ復帰・画面復帰・セッション解除（WinUiShared）

- 正本は `Shared/SystemEventWindow.cs` と `Shared/ResumeReapplyCoordinator.cs`。各アプリの `AppRuntime.Start` で監視とコーディネータを開始する。検知時はテンプレでは `AppRuntime.SystemResumed` を発火する（テンプレ既定ではコア再適用なし）。
- 監視: `WM_POWERBROADCAST`（`PBT_APMRESUMESUSPEND` / `AUTOMATIC` / `CRITICAL`）、`PBT_APMSUSPEND` / `PBT_APMQUERYSUSPEND`（スリープ入りの記録。この時点では再適用しない）、`GUID_CONSOLE_DISPLAY_STATE` On（GUID は `6fe69556-704a-47a0-aa35-2f285d73bf87`。桁誤りは型初期化失敗で監視全体が死ぬ）、`GUID_MONITOR_POWER_ON`、`GUID_LIDSWITCH_STATE_CHANGE`（開いたとき）、`GUID_SESSION_DISPLAY_STATUS` On、`WM_WTSSESSION_CHANGE`（unlock / console・remote connect）、`WM_DISPLAYCHANGE`。
- **スリープギャップ**: `GetTickCount64` と `QueryUnbiasedInterruptTime` の差が跳ねたら、Win32 メッセージが欠けても復帰扱いする（Modern Standby 向け）。
- コアがある派生アプリは `ResumeReapplyCoordinator` に即時 Force + **800 / 2000 / 5000 / 15000 / 30000 / 60000 / 90000 ms** 遅延再適用を接続する。起動パスは `BeginStartupPeriod`（ログオン直後は unlock が来ないことが多い）。WinUI `DispatcherTimer` は Stop→Start で明示再始動する。BlueShift は期待ガンマが載るまで最大 180 秒 Force を続ける。
- ログオンタスクに **SessionUnlock / スリープ復帰トリガーを付けない**。復帰は常駐プロセス内だけで行う。
- 参考: BlueShift `OnResumeApply`（ガンマ Force）、SPM `OnResumeApply`（`EnsureHealthy`）。

### その他オプション

- **バックグラウンドサービス（スケジューラ・監視タイマー・外部デバイス連携など）**
  - `AppRuntime` に追加する形で実装します（`EnsureXxx()` / 対応する `Start` 呼び出し / `ExitApplication` での破棄、というパターンに揃えてください）。
  - ログオン常駐と組み合わせる場合は、上記「ログオン起動時のコア適用」の遅延ヘルスチェックを同じ `AppRuntime` に持たせる。
- **インストーラ（Inno Setup）**
  - 雛形は `installer/WinUiAppTemplate.iss` + `scripts/build-installer.ps1`。`LicenseFile=LICENSE_NOTICE.txt`、`--exit` 終了、AppData `settings.json` 保持。AutoStart の sync/cleanup 行はコメントアウト済み（Optional 統合時に有効化）。

## 6. 設定・テーマ・アップデート・免責

- **`Settings.cs`**: JSON（Newtonsoft.Json）で `%AppData%\<AppName>\settings.json` に保存。ウィンドウ位置・サイズ・最大化状態、テーマ設定、`HideTrayIcon` を保持します。
- **テーマ**: `AppThemePreference`（`System` / `Light` / `Dark`、既定値は `System`）。`ThemeService` がルート要素（`RootGrid`）へ反映し、`ThemeChanged` イベントで `TitleBarThemeHelper` 等に通知します。設定画面には必ず 3 択のテーマ切替 UI を用意してください。
- **アップデート（SPM 正本・全アプリ共通）**: UI は **Info ページ**（手動確認カード → InfoBar「vX.Y.Zは最新です」等 → 自動確認トグル → setup.exe があれば Install カード）。起動時自動確認は `UpdateFlowService`（実体 `WinUiShared.UpdateFlow`）が `AppRuntime` 初回 MainWindow 表示後に実行。`UpdateChecker` が asset 選択、`UpdateInstallerService` が Temp へ DL → setup 起動 → `ExitApplication()`。Settings には更新 UI を置かない。`LatestReleaseApiUrl` 未設定時は `NotConfigured`。
- **自動起動 UI**: Settings の `SettingsExpander`（トグル＋種類/パス＋更新ボタン＋トレイ非表示）。テンプレート本体は **ログオンタスク専用**（レジストリ `Run` 切替 UI は置かない。SPM / BlueShift のみ `UseLogonTask` 切替あり）。展開アニメ打ち切りは `AutostartExpandHelper`。
- **ToggleSwitch（クリックで必ず切り替わる・正本）**:
  - Binding は使わず、code-behind で `IsOn` を代入し `Toggled` で保存する。初期化中は `_isInitializing` でガードする。
  - **SettingsCard 内**: `WinUiShared.ToggleSwitchClickHelper.BindCardClick(card, toggle)` をコンストラクタで呼ぶ。行クリックでも反転する。`IsClickEnabled` とスイッチ本体の二重反転はヘルパーが抑止する。
  - **SettingsExpander ヘッダー**: `ToggleSwitchClickHelper.ProtectFromParentCapture(toggle)` のみ。ヘッダー残りは展開／折りたたみ専用。
  - プログラムから `IsOn` を書き戻すときは suppress フラグ（または `_isInitializing`）を使う。`_isInitializing` を `DispatcherQueue` 遅延で下ろさない（遷移直後のクリックが握りつぶされる）。
  - compact スイッチは `MaxWidth=44` で位置を固定してよい。内部の 154 は **Page.Resources** の `ToggleSwitchThemeMinWidth=0` で落とす（WinUI の `Style` に `Resources` は無い）。
  - **スイッチの `PointerPressed` を `Handled` にしてはいけない**（本体が切り替わらなくなる）。
- **共有ライブラリ**: `Shared/WinUiShared.csproj`（`WinUiShared`）。更新モデル / `UpdateFlow` / `InfoUpdateUi` / `AutostartService` / `ToggleSwitchClickHelper` / `SystemEventWindow` / `ResumeReapplyCoordinator` / `LifetimeLog`。各アプリは ProjectReference する（複製後も Template の Shared を参照）。
- **lifetime ログ**（`WinUiShared.LifetimeLog`）: `%AppData%\<AppName>\lifetime.log`。終了理由・スケジュール確認／実行成否など**重要イベントのみ**を書く（毎分の評価など高頻度ログは書かない）。上限 **1MB**（超過時は末尾側を残してトリム）。各アプリの `AppRuntime.AppendLifetimeLog` は薄いラッパ。
- **免責**: Info 末尾に免責セクション。`LICENSE`（MIT）とインストーラ用 `LICENSE_NOTICE.txt` を同梱。
- **共有スタイル**（`App.xaml`）: `PageTitleTextStyle` / `SettingsSectionHeaderStyle` / `SettingsSectionHeaderFirstStyle` / `CardHeaderTextStyle` / `CardDescriptionTextStyle` / `PrimaryActionButtonStyle`。横並びの ComboBox / 操作ボタンの間隔は **12px**（`ControlRowSpacing`）。
- **画面の文言**: 出すのは **項目名**（短いラベル）。操作手順・補足の長文は置かない。例: 「動画または音声の URL」は可。「貼り付けまたは入力すると…やり直します」は不可。SettingsCard の SPM 準拠の既存説明（テーマなど）は、指示なく削ったり増やしたりしない。
- **多言語化**: `Strings/ja-JP/Resources.resw` と `Strings/en-US/Resources.resw` に日本語・英語の文言を用意し、`Strings.Get` / `Strings.Format` 経由で取得します（`ApplicationDefaultLanguage` は `ja-JP`）。新しい文言を追加したら両方の `.resw` に追記してください。

## 7. ファイル対応表

| ファイル | 役割 |
|---|---|
| `Program.cs` | エントリポイント。`--exit` の早期処理、WinUI 起動 |
| `App.xaml.cs` | `OnExplicitShutdown` 設定、二重起動チェック、`AppRuntime` 生成 |
| `AppRuntime.cs` | プロセス寿命・トレイ・リスナー・SystemEvent・Coordinator・`ShowOrCreateMainWindow`・`ExitApplication` |
| `SingleInstanceManager.cs` | Mutex + 名前付きイベント + PID/ASFW/show_signal |
| `Shared/SystemEventWindow.cs` | スリープ復帰・画面復帰・セッション解除の監視 HWND（ウィンドウクラス名はアプリ側で渡す） |
| `Shared/ResumeReapplyCoordinator.cs` | 起動・復帰の遅延再適用・ウォッチドッグ・スリープギャップ |
| `TrayMessageWindow.cs` / `TrayIconService.cs` | タスクトレイ（専用非表示ウィンドウ + Shell_NotifyIcon） |
| `MainWindow.xaml` / `.xaml.cs` | UI 専業のウィンドウ。TitleBar + NavigationView + Frame |
| `TitleBarThemeHelper.cs` | タイトルバーのテーマ同期（最小実装） |
| `WindowPlacementHelper.cs` | ウィンドウ位置・サイズ・最大化状態の保存／復元（WINDOWPLACEMENT） |
| `AppState.cs` | ページ間で共有する状態（`Settings` + トレイ更新フックなど） |
| `Settings.cs` | JSON 設定の読み書き |
| `ThemeService.cs` | ライト/ダーク/システム連動テーマの適用 |
| `UpdateChecker.cs` | GitHub Releases 経由のアップデート確認（setup asset 選択含む） |
| `UpdateFlowService.cs` | アプリ側入口。実体は `WinUiShared.UpdateFlow` |
| `StartupManager.cs` | アプリ側入口。実体は `WinUiShared.AutostartService` |
| `Shared/WinUiShared.csproj` | 更新・自動起動・復帰再適用・ToggleSwitch・lifetime ログの共通ロジック |
| `Shared/LifetimeLog.cs` | `%AppData%\<App>\lifetime.log`（イベントのみ・1MB トリム） |
| `UpdateInstallerService.cs` | setup.exe の DL・起動・アプリ終了 |
| `LICENSE` / `LICENSE_NOTICE.txt` | MIT とインストーラ許諾・免責 |
| `installer/*.iss` / `scripts/build-installer.ps1` | Inno Setup 雛形 |
| `Optional/AutoStart/*` | AutoStart 完成品（未配線。明示指示時のみ統合） |
| `Strings.cs` + `Strings/*/Resources.resw` | 多言語文字列リソース |
| `Views/HomePage.xaml*`, `Views/InfoPage.xaml*`, `Views/SettingsPage.xaml*` | 各ページ（Info=更新+免責、Settings=テーマ+トレイ） |
| `Views/PageScrollHost.xaml*` | ページ共通の必要時のみスクロールする ScrollViewer ラッパー |

## 8. 新規プロジェクトを始める手順

1. `WinUiAppTemplate` フォルダをコピーし、新しいプロジェクト名にリネームする。
2. `WinUiAppTemplate.csproj` の `RootNamespace` / `AssemblyName` / `Version` を変更し、全 `.cs` / `.xaml` の namespace（`WinUiAppTemplate`）を新しい名前に置換する。
3. `SingleInstanceManager.cs` / `TrayMessageWindow.cs` の Mutex 名・イベント名、および `AppRuntime` が `SystemEventWindow` に渡すウィンドウクラス名を新しいアプリ名に変更する（他アプリと衝突させないため）。
4. `Strings/*/Resources.resw` の `AppName` 等を変更する。
5. `Assets/AppIcon.ico` を配置する（`MainWindow.xaml` の `TitleBar.IconSource`、`TrayIconService`、`MainWindow.ApplyWindowIcon` が自動的に参照する）。
6. `App.xaml.cs` の `UpdateChecker.LatestReleaseApiUrl` に GitHub Releases API URL を設定する。
7. `LICENSE_NOTICE.txt` / `installer/*.iss` のアプリ名・版を更新する。
8. 本アーキテクチャ（`AppRuntime` 中心の構造、× で Close、TitleBar 最小実装）を維持したまま、ページやバックグラウンド処理を追加していく。
9. ログオン自動起動が必要になった場合は、必ずユーザーへ確認したうえで `Optional/AutoStart` を統合する（5 章参照）。

# Windows 連携

`Crer.Windows` は CLI から直接参照する .NET 10 クラスライブラリです。
Windows の入力記録、ブラウザーのウィンドウ操作、バックグラウンドプロセスの起動を実装しています。
Windows API は P/Invoke で呼び出し、入力 DLL の個別公開や C ABI は使用しません。
CLI を Native AOT で公開する構成に対応しています。

API の直接呼び出しと、入力記録の開始・停止・再開始のテストは次のコマンドで実行します。

```powershell
dotnet run --project tests/Crer.WindowsTests/Crer.WindowsTests.csproj -c Release
```

一時的なテストウィンドウを使い、対象ウィンドウの選別、最前面属性、不透明度、
フォーカスの維持も確認する場合は次を実行します。

```powershell
$env:CRER_TEST_WINDOWS = '1'
dotnet run --project tests/Crer.WindowsTests/Crer.WindowsTests.csproj -c Release
Remove-Item Env:CRER_TEST_WINDOWS
```

実際の CfT でもページのクリック・キー入力、タイトルバーの除外、ページ遷移後の追跡を確認する場合は、
`CRER_TEST_CHROME` に既存の CfT の `chrome.exe` を指定して同じテストを実行します。
この検証は専用プロファイルを使い、物理キーやポインター、利用者の前景ウィンドウを変更しません。

## 入力記録

`WindowsInput` は `IInputRecorder` を実装し、Windows 上で動作します。
引数なしで作成し、記録後に破棄してください。`Start(pid)` はプロセス内で共有する記録セッションを開始します。
記録中の再開始要求は `ERROR_ALREADY_EXISTS`（183）を返します。
新しいセッションの開始時には、以前のイベントとエラーを消去します。
セッションを所有するのは `Start` が成功したインスタンスだけです。
別のインスタンスから `Stop` や `Dispose` を呼んでも、進行中の記録は停止しません。
以前のセッションが自動終了し、別のインスタンスへ所有権が移った場合も同様です。

マウス入力には Windows Raw Input（`RIDEV_INPUTSINK`）、キーボード入力には低レベルフックを使います。
マウスイベントは対象ページ HWND の物理矩形と子 HWND、キーボードイベントは前景ウィンドウ内で
実際にフォーカスを持つページ HWND で選別します。タイトルバーや DevTools の入力は除外します。
ページ HWND の再生成時は直前のブラウザー内相対矩形に一致するページを再取得し、
別の renderer を誤って選ぶ間は入力を採用しません。
Caps Lock と修飾キーは開始時の状態を読み、対象外アプリにいる間の切替・解放も追跡します。
マウスイベントの生成元を Raw Input だけに限定し、クリックの重複記録を防ぎます。
レコーダーは入力を注入しません。

`InputEvent` には QPC 時刻、イベントの種類とデータ、物理画面ピクセル座標を格納します。
`QpcFrequencyHz` は時刻の周波数を返します。マウス座標は `GetPhysicalCursorPos` で取得し、
記録スレッドとコンテンツウィンドウの計測には Per-Monitor-V2 DPI awareness を使います。
`GetContentRect()` は Win32 の状態コードと物理ピクセル単位の矩形を `ContentBounds` として返します。

`Read(Span<InputEvent>)` は上限8,192件のキューからイベントを読み出します。
呼び出し元は `IsRunning` と `LastError` で初期化失敗やキューのあふれを検出できます。
`Stop()` はスレッドの後始末を最大5秒待ち、完了しなければ `ERROR_TIMEOUT`（1460）を返します。
タイムアウト後もインスタンスは有効で、停止を再試行できます。
`Dispose()` も自身が開始したセッションを停止し、タイムアウト時には再試行を許可します。
記録を継続できないエラーが起きた場合、CLI は途中までの NDJSON を保持し、
記録メタデータに `incomplete` を設定します。

## ブラウザーのウィンドウ操作

`SetProcessTopmostOnly(pid, enabled)` は、表示中で所有者のない CfT のトップレベルウィンドウを選びます。
非表示の補助ウィンドウや、所有者があるメニュー・ツールチップは除外します。
`SetWindowPos(SWP_NOACTIVATE)` を使い、設定後の `WS_EX_TOPMOST` を検証します。
`IWindowInput.SetProcessTopmostDetailed(pid, enabled)` は同じ操作の詳細を `TopmostResult` で返します。
`FailureStage` は成功時の `none`、PID 不正の `invalid_process`、対象不在の `window_not_found`、
OS API 失敗の `set_window_pos`、属性取得失敗の `read_style`、設定後の属性不一致の
`style_mismatch` を区別します。対象 HWND、`SetWindowPos` の戻り値と直後の実エラー、
取得した拡張スタイルと取得時の実エラーも保持します。属性不一致の状態コード 5 は
アプリ側の合成値で、Windows が返したアクセス拒否を意味するとは限りません。
詳細を取得する入口もフォーカスを変更しません。
前景フォーカスは `ForegroundProcessWindow(pid)` で別に要求するため、監視処理は
繰り返しフォーカスを奪わずに最前面属性を修復できます。`SetProcessTopmost` は両方の操作を行い、
`LastForegroundStatus` はそのフォーカス取得結果を返します。
最前面属性の設定に成功しても、Windows が前景フォーカスの要求を拒否する場合があります。

`SetProcessOpacity(pid, alpha)` は `WS_EX_LAYERED` と `SetLayeredWindowAttributes(LWA_ALPHA)` を使います。
alpha の範囲は0（透明）から255（不透明）です。他の拡張スタイル、フォーカス、重なり順を維持します。
HWND 探索の共有状態は、全 `WindowsInput` インスタンスに共通のプロセス単位のロックで保護します。

## バックグラウンドプロセス

`BackgroundProcessLauncher` は明示された Windows 実行ファイルを、ウィンドウをアクティブ化せずに起動します。
標準入出力をリダイレクトし、継承するハンドルをそのパイプに限定します。
`BackgroundProcess` がプロセスとストリームを所有するため、出力を読み終えてから破棄してください。
同じプロセス内で `Process.Start` を並行して使う呼び出し元も、起動時に `CreationGate` を取得します。
これにより、一時的に継承可能になっているハンドルを別の子プロセスが引き継ぐことを防ぎます。

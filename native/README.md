# ネイティブ記録ブリッジ

.NET 10 Native AOT で公開ビルドする。MSVC と Windows SDK を含む Visual Studio Build Tools が必要。

```powershell
dotnet publish native/Crer.WinInput.csproj -c Release -r win-x64
```

ネイティブ状態管理の障害テストは次で実行する。

```powershell
dotnet run --project native/tests/Crer.WinInput.NativeTests.csproj
```

DLL は `crer_input_*` C ABI のバージョン 2 を公開し、Node.js 24 側の Koffi から呼び出す。
Node.js への移行で既存の C# Native AOT 実装と ABI は維持する。DLL、Node.js、Koffi の
ネイティブパッケージは同じアーキテクチャを使う。

バージョン 2 では、マウスイベントとコンテンツ矩形を明示的に物理ピクセル座標として扱う。
マウスには Windows Raw Input（`RIDEV_INPUTSINK`）、キーボードには低レベルフックを使い、
上限付きのメモリ内キューへ記録する。マウス入力は対象 CfT プロセスのルート HWND、
キーボード入力はその前景ウィンドウで絞り込む。入力の注入は行わない。
メッセージループが `WM_INPUT` を処理し、マウスイベントは Raw Input だけから生成するため、
クリックが重複しない。

各 Raw Input パケットの処理時に `GetPhysicalCursorPos` でマウス座標を取得する。
記録スレッドとコンテンツウィンドウの計測には Per-Monitor-V2 DPI awareness を使うため、
Windows の表示倍率が 125%、150%、200% でも、イベント位置と HWND 矩形が同じ物理ピクセル座標系になる。

Node.js 側は `crer_input_abi_version` を検査し、`crer_input_read` を定期的に呼び出して、
固定長のイベント構造体をバッファへ読み取る。DLL から JavaScript へのコールバックは使わず、
文字列やメモリ所有権を DLL と Node.js の間で共有しない。取得したイベントを NDJSON へ保存し、
その後シナリオへ正規化する。

`crer_input_is_running` により、ポーリング側はフックの失敗やキューのあふれを検出できる。
`start` は各記録の前にキューとスレッド状態を初期化する。`stop` はネイティブスレッドの終了を待ち、
5 秒以内に終了しなければ `ERROR_TIMEOUT` を返す。記録処理が終端エラーを報告した場合、
Node.js 側は途中までの NDJSON を保持し、metadata に `incomplete` を付ける。

再生時の `crer_input_set_process_topmost_only` は、プロセス ID を使って、表示中かつ所有者のない
CfT のトップレベルウィンドウを探す。非表示の補助ウィンドウや所有者付きメニュー・ツールチップは除外し、
補助ウィンドウだけを操作して成功と判定することを防ぐ。
`SetWindowPos(SWP_NOACTIVATE)` で最前面属性を変更し、その後 `WS_EX_TOPMOST` を検証する。
`crer_input_foreground_process_window` は、これとは別に前景フォーカスを要求する。
この分離により、Node.js の監視処理はフォーカスを繰り返し奪わずに、失われた最前面属性だけを復元できる。
旧 `crer_input_set_process_topmost` は ABI の互換性を保つため、両方を行う動作で残している。
これらの API はシナリオが明示的に `browser.window.foreground: true` を指定した場合だけ使い、
再生処理は CfT を閉じる前に最前面属性を解除する。
Windows はスレッド入力の接続を使って再試行しても前景フォーカスを拒否する場合がある。
最前面属性の設定自体が成功していれば、その結果は診断情報として扱う。

`crer_input_set_process_opacity(uint32_t pid, uint32_t alpha)` は、表示中かつ所有者のない
CfT ウィンドウに `WS_EX_LAYERED` と `SetLayeredWindowAttributes(LWA_ALPHA)` を適用する。
アルファ値は 0（完全透明）から 255（完全不透明）とする。他の拡張スタイル、フォーカス、
重なり順を保持し、同じアルファ値の繰り返し適用ではウィンドウを変更しない。
この追加 API でも ABI はバージョン 2 のままとする。`browser.window.opacity` を 1 未満にする前に
DLL を再ビルドする。

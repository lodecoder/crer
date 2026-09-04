# crer 仕様書

## 1. 目的と範囲

`crer` は Windows 11 以降で動く、Chrome for Testing (CfT) 専用の GUI ブラウザ操作
レコーダー／プレーヤーである。利用者が CfT の画面に対して実際に行ったポインター・
キーボード操作を記録し、レビュー可能なテキストファイルとして保存する。再生では DOM
API の `click()`、要素への値代入、JavaScript によるフォーム送信を使用しない。CDP の
`Input.dispatchMouseEvent`、`Input.dispatchKeyEvent`、`Input.insertText` 等を使い、
ブラウザが受け取る入力イベントとして再現する。

対象は通常の Web ページ操作（移動、クリック、スクロール、入力、キー、ドラッグ）で
あり、OS 全体の RPA ではない。ネイティブなファイル選択ダイアログ、OS の認証 UI、
CAPTCHA の突破、Chrome 外のアプリ操作は v1 の対象外とする。

## 2. 成功条件

1. 記録・再生とも、普段使いの Chrome と分離された CfT プロセス／ユーザーデータ
   ディレクトリだけを対象にする。
2. 再生中に Windows の物理カーソル位置、物理キーボード入力、前面ウィンドウを変更
   しない。利用者は通常どおり別アプリや通常の Chrome を操作できる。
3. 再生用 CfT は headful（可視）で、シナリオごとに画面上の位置と内容領域サイズを
   固定できる。
4. URL、ウィンドウサイズ、DPI／表示倍率、ページ拡大の再現条件をシナリオに明記し、
   不一致時は既定で開始しない。
5. 記録ファイルは Git で差分レビューしやすい UTF-8 の YAML であり、単体／直列／
   並列の実行を記述できる。

## 3. 採用アーキテクチャ

### 3.1 技術選定

| 層          | 採用                                | 理由                                                                                                                        |
| ----------- | ----------------------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| CLI・実行系 | Deno 2.8+ + TypeScript              | 単一バイナリ配布、組み込み Web API、権限の明示、YAML／並行制御の実装性がよい。                                              |
| ブラウザ    | 固定バージョンの Chrome for Testing | 自動更新する通常 Chrome と分離し、再現可能なバイナリを使う。                                                                |
| 再生入力    | CDP の `Input` ドメイン             | OS 入力を発生させず、ブラウザに低レベル入力を配送する。                                                                     |
| ウィンドウ  | CDP `Browser.setWindowBounds`       | CfT の対象ウィンドウだけを DIP 単位で移動・リサイズする。                                                                   |
| 記録入力    | Windows Raw Input + Low Level Hook  | 物理入力を取得する。Chrome が Raw Input を消費する場合は Hook を使う。CDP は注入はできるが物理入力を記録する API ではない。 |
| シナリオ    | YAML + JSON Schema                  | 人間編集、バリデーション、将来の自動補完を両立する。                                                                        |

Deno 側は組み込みの `WebSocket`、`jsr:@std/yaml`、`jsr:@zod/zod`（または JSON Schema validator）、
`jsr:@std/cli` を用いる。いずれも Deno で利用でき、Node.js 互換レイヤーを前提にしない。
Raw Input と HWND 操作は C ABI を公開するC# .NET 10 Native AOT DLL `crer-win-input.dll` とし、
`Deno.dlopen()` でロードする。実行バイナリには同梱した信頼済み DLL のパスだけに
`--allow-ffi` を許可する。PowerShell や AutoHotkey をランタイム依存にはしない。

### 3.1.1 ネイティブ DLL 境界

`crer-win-input.dll` は .NET Native AOT で各 Deno 配布バイナリと同じアーキテクチャ（`win-x86_64` または
`win-aarch64`）で同梱する。DLL は `crer_input_abi_version`、`crer_input_start`、
`crer_input_stop`、`crer_input_read`、`crer_input_last_error`、前景 HWND の取得・復元用 API、
および `crer_input_foreground_process_window` を C ABI で export する。
イベントは固定長・ポインタを含まない POD 構造体とし、文字列やメモリ所有権を Deno と DLL
の間で共有しない。Deno から DLL への callback は使わず、Deno の非同期ループが
`crer_input_read` を短い間隔で poll する。これにより DLL のスレッドから V8/Deno runtime を
呼び出さない。

DLL は専用 native thread 上の message-only window で Raw Input を受信し、時刻は
`QueryPerformanceCounter` を単調時刻として記録する。`start` は Per-Monitor-V2 DPI awareness
を設定済みのプロセスで一度だけ成功できる。二重起動、イベントバッファのあふれ、Windows API
失敗は構造化した error code を返し、バッファあふれは記録を続けず `record` を失敗終了する。

### 3.2 プロセス分離

各再生ワーカーは次の引数で独立した CfT を起動する。

```text
chrome.exe --remote-debugging-port=0 --remote-debugging-address=127.0.0.1 \
  --user-data-dir=<workspace>/.crer/runs/<run-id>/profile \
  --no-first-run --no-default-browser-check --disable-sync --new-window about:blank
```

起動時に `DevToolsActivePort` からランダムなローカル CDP ポートを取得する。CDP は
localhost のみで待受け、ポート番号や WebSocket URL はログに秘匿情報として扱わない。
通常 Chrome のプロファイル、既存プロセス、リモートデバッグポートには**接続しない**。
既定では一時プロファイルを使う。`record`、`play`、`run` の `--profile-dir <directory>` は CfT の
`--user-data-dir` を指定ディレクトリへ向け、キャッシュ、Cookie、Local Storage、Service Worker を
次回実行へ残す。CLI 指定が最優先であり、シナリオの `profile: persistent:<directory>` でも同じ再利用を
指定できる。`<directory>` はワークスペースの `.crer\\profiles` 配下だけを許可し、`..` を含むパスと
実体が外部を指すシンボリックリンクを拒否する。永続プロファイルは実行後も削除しない。通常 Chrome の
既存プロファイルは対象外とする。
同一の永続プロファイルを並列起動すると Chrome のプロファイルロックとデータ競合を起こすため、
`run` では `max_parallel: 1` を必須とする。CfT プロセスは成功・失敗・中断のいずれでも、終了処理で CDP
`Browser.close` による graceful close を要求して閉じる。CDP が応答しない場合に限り、実行
ワーカーが起動した CfT 子プロセスだけをタイムアウト後に終了する。worker は子プロセスの終了を確認してから
完了するため、同じ永続 profile を使う serial の次シナリオは profile lock 解放後に起動する。

通常の再生は利用者の前景ウィンドウへ干渉しない。Web アプリケーションが前景状態を要求する場合だけ、
scenario の `browser.window.foreground: true` を指定できる。この場合、Native DLL は対象 CfT の
トップレベル HWND をプロセス ID から探索し、起動直後に `SetWindowPos(HWND_TOPMOST)` で常時最前面に
固定して `SetForegroundWindow` で前景化する。終了処理では `HWND_NOTOPMOST` へ戻してから、実行開始時の
前景 HWND を復元する。Windows の foreground lock などで設定に失敗した場合は Win32 error code を
artifacts の `foreground.json` に記録し、再生を環境エラーとして終了する。このモードはフォーカスと
ウィンドウの重なり順だけを変更し、物理ポインタや通常 Chrome のプロセスを操作しない。

## 4. 入力の記録と再生

### 4.1 記録

`crer record` は CfT を専用プロファイルで起動し、対象のトップレベル HWND と CDP target を
対応付ける。Windows Raw Input から受けた入力について、カーソル直下の HWND が対象 CfT の
コンテンツ領域である時だけ採用する。物理スクリーン座標は `GetClientRect`、DPI、CDP
`Page.getLayoutMetrics` を使って CSS viewport 座標に正規化する。

- `WM_INPUT` の移動、ボタン、ホイールを時間順に採取する。
- キーは対象 CfT が前景の場合だけ採取し、Scan Code／Virtual Key／修飾キーを保存する。
- 英数字・記号は Windows の現在のキーボードレイアウトに対する `ToUnicodeEx` の結果を 1 つの
  `text` ステップに畳む。IME の確定文字列は v1 の対象外であり、YAML を後から編集する。
- クリックは down/up と移動をイベントとして保持し、停止時にクリック・ドラッグ・スクロール
  として可読なステップへ正規化する。元イベント列は `artifacts/raw-input.ndjson` に任意保存する。
- Windows QPC の周波数を sidecar に保存し、連続する論理操作の間隔を `sleep` ステップとして YAML に
  明示的に出力する。たとえば click 後 5 秒で次の click をした場合、間に `{ do: sleep, ms: 5000 }` を出力する。
- 記録中は UI によるページ操作を妨げない。CfT 以外で行った入力は記録しない。

記録開始後の停止操作は、対話的な直接起動では `Enter` または `Ctrl+C`（1 回目は graceful stop、2 回目は強制中断）、または CfT
ウィンドウの終了とする。graceful stop では、未確定の down/up 対を `raw-input.ndjson` に残し、
YAML へは不完全な操作を出力せず警告する。CfT が前景でない間のキー入力、Chrome のタブバー・
アドレスバー・DevTools 上の入力、対象コンテンツ領域外のポインター入力は記録しない。
較正後の左クリックは記録中の標準出力へ CSS 座標として表示し、content bounds を得られない場合は
物理画面座標であることを示す `screen_px` 表記で表示する。ドラッグはクリックとして表示しない。

座標は、Raw Input の物理 screen px を対象コンテンツ HWND の物理 client px に変換し、同時点の
`Page.getLayoutMetrics().cssVisualViewport.clientWidth/clientHeight` と `GetClientRect` の幅・高さの
比で CSS viewport px に換算する。すなわち `x = clientX * cssWidth / clientWidth`、
`y = clientY * cssHeight / clientHeight` とする。記録中に client rect、DPR、viewport が変化した
場合は、以後の Raw Input イベントにその時点の CSS viewport を付与する。正規化はイベントごとの
viewport を使うため、ページ遷移前後で異なる表示条件の座標を安全に一つの scenario へ保存できる。

座標のほか、CDP `DOM.getNodeForLocation` で得たタグ、アクセシブル名、CSS path、要素の
bounding box を **locator hint** として添える。これは編集・失敗診断・将来の検証専用であり、
既定の再生操作を DOM 操作に置換しない。クロスオリジン iframe や Shadow DOM では hint が
欠落し得るため、座標は常に必須である。

### 4.2 再生

各ステップで CDP 入力メッセージを対象 page session に送る。Windows の `SendInput`、
`SetCursorPos`、クリップボード貼り付けは使用禁止である。従って再生が利用者のマウスを動かす
ことも、利用者の作業先にキーを入力することもない。

クリックの揺らぎは再生時のみ、対象座標に適用する。`playback.seed` が未指定の場合、再生
開始時に暗号学的乱数から符号なし 64 bit 整数（`uint64`）を生成して実効 seed とする。実効
seed は artifacts の run metadata と実行ログへ必ず保存するため、後から `playback.seed` または
CLI の `--seed` に指定して同じ座標列を再現できる。`playback.jitter` はシナリオ全体の
既定値であり、各 `click`／`double_click` ステップの `jitter` を指定した場合は、その値が
全体設定を**完全に上書きする**。この場合、全体設定はマージも継承もしない。個別指定で
`enabled: false` とすれば、そのクリックだけ揺らぎを無効にできる。乱数シードを記録・
ログに残すため、同じ seed の再実行は同じ座標列になる。範囲外なら既定で失敗する（暗黙の
clamp はしない）。

```text
base point -> seed 付き PRNG -> uniform/normal offset -> bounds check -> CDP mouse move/down/up
```

実効 seed は符号なし 64 bit の**10 進文字列**で保存・指定する。YAML number は JavaScript の
安全整数範囲を超え得るため許可しない。PRNG は `xoshiro256**`、seed 拡張は `splitmix64` とし、
未指定 seed の生成には `crypto.getRandomValues()` を使う。`uniform` は半径内の一様な円盤分布、
`normal` は標準偏差 `radius_px / 3` の二次元正規分布を半径内に rejection sampling した分布と
する。`radius_px` は最大オフセット距離、`min_distance_from_edge_px` は CSS viewport の各辺から
確保する最小距離である。最大 16 回の試行後に有効な点を作れなければ `out_of_bounds` を適用する。

`click`／`double_click` に個別 `jitter` を書く場合、`enabled: false` だけなら他の項目を省略でき、
その操作だけ揺らぎなしにする。`enabled: true` の場合は `distribution`、`radius_px`、
`min_distance_from_edge_px`、`out_of_bounds` の全項目を必須とする。個別設定があるとき、全体設定の
`playback.jitter` は一切参照しない。

ドラッグは始点／終点の両方に独立した揺らぎを適用し、`steps`（既定 12）で補間する。テキスト
入力には IME を要しない `Input.insertText` を既定とし、ショートカット等は個別の key down/up を
使う。これは DOM 値代入ではなく、CDP が提供するテキスト入力注入である。

## 5. 表示の固定と可搬性

画面座標の再現性は OS のスケーリングに依存する。v1 は CfT を
`--force-device-scale-factor=1` で起動し、CSS 座標の `devicePixelRatio` を 1 に固定する。
さらに `--disable-features=Translate,TranslateUI` を指定し、翻訳ポップアップがページを覆わないようにする。
`--disable-save-password-bubble` と password manager の専用プロファイル設定により、ログイン後の
パスワード保存・漏えい検出ポップアップも抑止する。既存の永続プロファイルには他の設定を残したまま
この設定を反映する。プロファイルは必ず絶対パスで渡す。再生・記録ともに `--app=<URL>` を使う同じ CfT アプリ
ウィンドウとして起動し、Chrome のタブ・アドレスバー UI をページ座標系から除外する。`--disable-infobars`
で CfT banner の抑止を要求する。CfT のバージョンまたは UI 状態によって banner が表示される場合は、
記録開始前にページ左上へ注入する 64×64 CSS px のマーカーを物理クリックして、DOM `clientX/clientY` と
画面座標を対応付ける。マーカーのクリックは記録から除外する。v1 は次を再生前提とする。

- Windows の表示スケーリングは任意とする。実行時に CfT の `devicePixelRatio` を検査し、strict
  の場合は想定値と異なれば失敗にする。
- `window.content` は CfT に要求する content host のサイズ、`window.bounds` は画面上の DIP 位置である。
  スクロールバー等により実効 CSS viewport が異なる場合、記録は `window.viewport` に座標系を保存する。
  再生は `Browser.setWindowBounds` と `Browser.setContentsSize` を順に実行し、`window.viewport`
  （未指定時は `window.content`）との実測一致を検証する。
- `play` / `run` の `--ignore-viewport-mismatch` 指定時は mismatch を `display.json` と標準エラーへ
  保存して再生を継続する。この escape hatch は座標の再現性を保証しない調査用途に限定する。
- `browser_zoom` は `100` のみを v1 の厳密保証範囲とする。Chrome UI のサイト別ズームは CDP の
  安定 API で直接固定できないためである。100% 以外を必要とする場合は、専用プロファイル
  テンプレートに事前設定したズームを使い、`visualViewport.scale` と CSS viewport の検証を
  `zoom_check: advisory` として行う。`Emulation.setPageScaleFactor` を Chrome の UI ズーム設定の
  代替にはしない。
- ページ側レイアウトの差、フォント、Cookie、A/B テスト、広告、認証状態は座標再生を壊し得る。
  profile template、ネットワーク条件、固定 URL をシナリオで管理し、必要な `assert` を置く。

## 6. シナリオ形式

拡張子は `.crer.yaml`。UTF-8、改行 LF、スキーマバージョン `1` を必須とする。機密値は書かず、
環境変数参照 `${ENV:NAME}` のみを許可する。シークレットを含むシナリオは Git にコミットしない。

```yaml
version: 1
name: order-search
browser:
  chrome: chrome-for-testing@pinned
  profile: ephemeral                 # ephemeral | persistent:<directory>
  initial_url: https://example.test/orders
  window:
    bounds: { left: 1640, top: 80, width: 1080, height: 900 } # screen DIP
    content: { width: 1040, height: 760 }                       # CfT に要求するサイズ
    viewport: { width: 1025, height: 745 }                      # 任意: 実効 CSS 座標系
  display:
    expected_dpr: 1
    browser_zoom: 100
    zoom_check: strict                # strict | advisory | off
playback:
  seed: 20260816                   # 任意。省略時は uint64 を暗号学的乱数で生成・記録
  speed: 1.0
  jitter:
    enabled: true
    distribution: normal              # none | uniform | normal
    radius_px: 3
    min_distance_from_edge_px: 4
    out_of_bounds: fail               # fail | disable-for-step
  timeouts: { navigation_ms: 30000, action_ms: 10000 }
  step_delay_ms: 1000               # 任意。各操作後（最後の操作後を含む）の待機時間
  on_failure:                       # ステップ失敗種別ごとの既定動作
    default: abort                   # abort | continue
    timeout: continue
    assertion: continue
    action: abort
    jitter_bounds: abort
steps:
  - { do: wait_for, url: "https://example.test/orders*", state: network_idle }
  - { do: click, at: { x: 211, y: 182 }, delay_ms: 1040 }
  - { do: text, value: "${ENV:ORDER_ID}" }
  - { do: key, key: Enter }
  - { do: wait_for, locator_hint: { role: table, name: Results, text: "10 results" }, state: visible }
  - { do: scroll, at: { x: 920, y: 620 }, delta: { x: 0, y: 561 } }
```

許可する `do` は `navigate`、`wait_for`、`click`、`double_click`、`mouse_move`、`drag`、`scroll`、
`text`、`key`、`key_chord`、`screenshot`、`assert`、`sleep`、`log`、`if`、`repeat`、`repeat_until`、`call`
である。`wait_for` と `assert` は
ページ状態を読むため CDP Runtime/DOM を使ってよいが、ページを変更してはならない。
`sleep` 以外の各操作には任意の `delay_ms`（0 以上のミリ秒）を指定できる。成功した操作の直後に
待機してから次のステップへ進む。`normalize` は記録された操作間隔を原則として前の操作の `delay_ms`
へ出力する。先頭または単独の待機を表す場合だけ、`{ do: sleep, ms: ... }` を用いる。`sleep` と
`delay_ms` の併用は無効である。

`log` は必須の文字列 `message` を標準出力へ `[crer] <message>` として出力する進捗確認用のステップである。
ブラウザ・ページには操作をせず、メッセージは該当する `steps.ndjson` の記録にも含める。

`repeat` は 0 以上の整数 `count` とステップ配列 `steps` を必須とし、子ステップ列を `count` 回順に実行する。
`count: 0` は正常な no-op であり、子ステップを実行しない。子ステップには通常の操作、`if`、入れ子の
`repeat` を指定できる。子ステップで abort 対象の失敗が起きた場合は、残りの反復を実行しない。repeat 自身と
子ステップは `steps.ndjson` に別々に記録し、子の index は親 index・反復番号・子番号をドットで結合した
文字列とする。

`click` は任意の正の整数 `count` を指定でき、省略時は `1` とする。click は同じ解決済み座標を `count` 回
実行する。`delay_ms` と `playback.step_delay_ms` は各クリックの後に適用する。jitter の結果または template
一致矩形内で選んだ位置は、同じ click step 内の全反復で共有する。`click.count` は `0` を許可しない。

`repeat_until` は `template`、`state`、正の整数 `max_attempts`、`on_limit`、ステップ配列 `steps` を必須と
する。`state` は `visible` または `hidden`、`on_limit` は `fail` または `continue` である。毎回、子ステップを
実行する**前**にテンプレートを探索し、`visible` なら similarity が閾値以上、`hidden` なら閾値未満になった
時点で成功として子ステップを実行せず終了する。未達なら `steps` を 1 回実行して再判定する。子ステップ列を
`max_attempts` 回実行しても未達の場合、`on_limit: fail` は `template` 失敗、`on_limit: continue` は
`status: skipped` を記録して次の兄弟ステップへ進む。探索ごとに screenshot を
`template-<step-index>.attempt-<試行回数>.png` として artifacts に残し、similarity と閾値を標準出力に出力する。
`state: hidden` と同一 template の子 click を組み合わせると、クリックのたびに画面を再探索して画像がなくなる
まで処理できる。各試行で親判定と最初の子 click の template path・実効閾値が同じ場合は一致結果を再利用する。

`for_each_template` は `template`、1〜100 の整数 `max_matches`、ステップ配列 `steps` を必須とする。一枚の
screenshot 内で similarity が閾値以上の template 矩形を類似度降順に最大 `max_matches` 件列挙し、各矩形に
対して `steps` を実行する。IoU が 0.5 以上の候補は同一矩形として最高 similarity の一件へ抑制する。
子ステップの `${match_left}`、`${match_top}`、`${match_width}`、`${match_height}`、`${match_center_x}`、
`${match_center_y}`、`${match_similarity}` はそれぞれ当該一致の数値へ展開される。一致がない場合は
`template.on_missing` の `fail`／`skip` に従う。検出件数と矩形は `steps.ndjson`、探索元は
`template-<step-index>.png` に保存する。子ステップによるページ変化後に再探索はせず、列挙対象は最初の
screenshot へ固定する。

scenario トップレベルの `functions` は、識別子名をキー、ステップ配列を値とする名前付き操作列の mapping
である。値は従来形式のステップ配列、または `params`（一意な識別子の配列）と `steps` を持つ mapping である。
`call` は必須文字列 `function` で定義済みの名前を指定し、引数付き関数では必須 mapping `args` に全 parameter
を文字列または有限の数値として過不足なく指定する。関数内の文字列フィールドにある `${parameter}` は対応する
引数で置換してから実行する。フィールド値全体が一つの `${parameter}` であり、引数が数値なら数値のまま保持する。
これにより `repeat.count`、`click.count`、`repeat_until.max_attempts`、`delay_ms`、`sleep.ms`、座標などの数値フィールドへ渡せる。文字列中へ埋め込む場合は
数値を10進文字列として置換する。関数内には通常の操作、`if`、`repeat`、`call` を含められるが、直接・間接を問わず
循環する呼び出しは action 失敗として停止する。`call` 自身と展開された子は `steps.ndjson` に記録する。
`locator_hint` は任意の `role`、`name`、`text` を持ち、指定した各値が完全一致する可視要素を条件にする。

`click` と `double_click` の `jitter` は `playback.jitter` と同じスキーマを持つ任意フィールド
である。省略時だけ `playback.jitter` を使う。`jitter: { enabled: false }` は個別揺らぎの無効化を
表し、`enabled: true` の場合だけ `radius_px` 等の全項目が必要で、全体設定から補完しない。`drag` の個別揺らぎは v1 では
未対応で、常に `playback.jitter` を使う。

`click` は `at` の代わりに `template` を指定できる。テンプレートのファイルパスは scenario YAML からの
相対パスであり、再生直前に CDP screenshot 上で探索する。`playback.template` は `min_similarity`（0〜1、
既定 `0.8`）、`random_inset_px`（既定 `0`）、`on_missing`（`fail` または `skip`、既定 `fail`）の既定値を
指定する。個別の `steps[].template` はこれらを上書きする。一致矩形内の位置は実効 seed を使う一様乱数で選び、
`random_inset_px` は各辺をクリック候補から除外する。`at`、`jitter` と `template` は併用しない。一致不足で
`on_missing: fail` の場合は `template` 失敗として扱う。`skip` の場合はクリックせず `steps.ndjson` に
`status: skipped` として記録し、失敗にせず次のステップへ進む。どちらの場合も探索元の screenshot と
similarity・矩形を artifacts に保存する。探索ごとに標準出力へテンプレートパス・実測 similarity・適用した
threshold を出力する。`on_missing` は step 直下ではなく `template` 内に置く。`on_missing: fail` 後に
停止するかは `playback.on_failure.template`、なければ `playback.on_failure.default` の policy に従う。

template 指定の `click` は任意の `then`（ステップ配列）を指定できる。similarity が閾値以上なら一致矩形内を
クリックしてから `then` を順に実行する。クリック自身の `delay_ms` および全体の `step_delay_ms` は `then` の
前に適用する。閾値未満ならクリックも `then` も実行せず、`matched: false`、`status: skipped` を記録して正常に
次のステップへ進む。これは template 条件の `if` の `then` 内に同一 template click を置く糖衣構文であり、
`on_missing` は適用しない。`else` は指定できない。

`do: if` は `template` と `then`（ステップ配列）を必須とする条件ステップである。template の similarity が
閾値以上なら `then` を順に実行し、閾値未満で `else` がなければ次の兄弟ステップへ進む。任意の `else`
（ステップ配列）を指定した場合は、閾値未満なら代わりに `else` を順に実行する。これは正常な
条件分岐であり、`on_missing` や `on_failure.template` の対象ではない。`if` では `at` と `jitter` を指定せず、
`playback.template.min_similarity` を個別指定がない場合の既定値として用いる。条件評価の screenshot、
similarity、実行または skip の状態は artifacts に保存する。
条件分岐先の最初のステップが同一 template path・同一実効 `min_similarity` の template click なら、条件評価の
screenshot と一致結果を再利用し、二度目の探索をしない。この場合も click 自身の artifact screenshot は保存し、
標準出力の template 行末に `(reused)` を付ける。途中に別ステップを挟む場合、template path または実効閾値が
異なる場合は再探索する。

`do: if` は `template` の代わりに `weekdays` を条件にできる。`weekdays` は `mon`、`tue`、`wed`、`thu`、
`fri`、`sat`、`sun` から一つ以上を選ぶ配列である。実行時のローカル時刻の曜日が含まれる場合だけ `then` を
実行する。`time_zone` に IANA タイムゾーン（例 `Asia/Tokyo`）を指定すれば、そのタイムゾーンで判定する。
`template`、`weekdays`、`equals` は同じ `if` に併用せず、ちょうど一つを必須とする。不一致は正常な
条件分岐として `steps.ndjson` に記録し、`else` があればそれを実行し、なければ次の兄弟ステップへ進む。

`do: if` は `equals` を条件にできる。`equals` は必須文字列 `left` と `right` を持ち、完全一致した場合に
`then`、不一致なら `else`（なければ次の兄弟ステップ）を実行する。`template`、`weekdays`、`equals` は同じ
`if` に併用せず、ちょうど一つを指定する。関数の展開時は `${parameter}` を引数値へ置換した後に
`equals` を評価するため、関数引数による分岐に使える。

`playback.step_delay_ms` は再生中の各ステップ後に待機するミリ秒数で、最後のステップの後にも適用する。
目視確認には `1000` 以上を推奨する。CLI の `play --step-delay-ms <ms>` を指定すると、YAML の値を
その実行だけ上書きする。これは記録済みの `sleep` に**追加する**目視用の固定待機である。

`playback.on_failure` は、続行可能なステップ失敗に対するポリシーである。キーは `default`、
`navigation`、`timeout`、`action`、`assertion`、`jitter_bounds`、`template` のみを許可し、値は `abort` または
`continue` とする。該当種別の設定を優先し、なければ `default`、さらに `default` もなければ
`abort` を使う。`continue` の場合は失敗を artifacts と最終結果に記録した上で次のステップへ
進む。YAML／CLI 検証エラー、実行環境不一致、CDP 接続喪失、CfT クラッシュ、利用者による中断は
続行不能であり、この設定にかかわらず停止する。

失敗種別は次で固定する。`navigation` は `navigate` または URL 待機の CDP エラー、`timeout` は
ステップの待機時間超過、`action` は CDP 入力の拒否・入力状態不整合、`template` は画像テンプレートの
一致不足、`assertion` は `assert` または
`wait_for` の条件不成立、`jitter_bounds` は揺らぎ後の有効座標を得られない場合である。`continue`
を選んだ場合は、まず未解放の mouse/key を release してから、失敗種別・step index・実効座標・
スクリーンショットを artifacts に記録し、次のステップを開始する。続行した失敗が一件でもあれば
scenario の最終結果は `failed`、CLI 終了コードは `4` とする。`continue` は「後続操作を試行する」
指定であり、実行全体を成功扱いにする指定ではない。

## 7. 実行計画（直列・並列）

`.crer.plan.yaml` はシナリオを合成する。各 leaf は別 CfT プロセスなので並列枝は独立しており、
物理マウスを奪い合わない。1 ブラウザ内での並列タブ実行は座標・フォーカスが競合するため v1
では禁止する。

```yaml
version: 1
name: nightly-check
max_parallel: 2
timeouts: { worker_ms: 0 }          # 0 は無制限
on_failure:                         # 子 job の結果ごとの既定動作
  default: abort                    # abort | continue
  scenario_failure: continue
  timeout: continue
  environment: abort
run:
  serial:
    - scenario: login.crer.yaml
    - parallel:
        fail_fast: false
        jobs:
          - scenario: sales-report.crer.yaml
          - serial:
              - scenario: inventory.crer.yaml
              - scenario: logout.crer.yaml
```

`serial` は前項の成功後に次項を開始する。子 job が失敗した場合は plan の `on_failure` に従う。
キーは `default`、`scenario_failure`、`timeout`、`environment` で、解決規則は scenario と同じで
ある。`continue` なら失敗を集約して次項へ進む。`parallel` は全 job の完了を待ち、失敗を集約する。
`fail_fast: true` は未開始 job を中止し、実行中 job には CDP の graceful close を要求する。環境
喪失など続行不能な失敗は常に当該 job を停止する。各 leaf に `run-id`、専用プロファイル、専用
artifacts ディレクトリを割り当てる。

plan の `on_failure` では、`scenario_failure` は child scenario が終了コード `4` で終わった場合、
`environment` は child worker が起動できない・CDP が失われた・終了コード `3` の場合、`timeout`
は plan が持つ `timeouts.worker_ms` を超えて child worker が終了しない場合を指す。`worker_ms` を
省略した場合は `0`（無制限）であり、`timeout` は発生しない。`continue` の child があっても、
plan は一件でも失敗を集約した場合は終了コード `4` を返す。ただし `environment` の失敗は常に
終了コード `3` を返す。`fail_fast: true` は `on_failure: continue` より優先する。

## 8. CLI

```text
crer doctor                         # Windows、CfT、CDP、DPI を診断
crer browser install --channel stable --version <version>
crer record new orders.crer.yaml --url https://example.test/orders
crer record resume orders.crer.yaml
crer validate orders.crer.yaml
crer play orders.crer.yaml --position 1640,80 --seed 42 --mute-audio
crer run nightly.crer.plan.yaml --max-parallel 2 --mute-audio
crer inspect artifacts/<run-id>      # ステップ、失敗、スクリーンショットを表示
```

`record new` はテンプレートと CfT を起動し、利用者が終了コマンドを送るか CfT を閉じたときに
正規化・検証した YAML を保存する。`play` の CLI オプションは明示的に指定した場合のみ
front matter を上書きし、実行ログに override を記録する。`--position` は再生ウィンドウだけを
移動する。`--mute-audio` は再生する CfT プロセスだけへ Chrome の mute 指定を渡すため、Windows 全体や
通常 Chrome の音量には影響しない。

終了コードは `0` 成功、`2` YAML/CLI 検証エラー、`3` 環境・ブラウザ不一致、`4` 操作または
assert の失敗、`5` 中断とする。

## 9. 安全性・ログ・失敗時の扱い

- 初回起動時に CfT 実行ファイルのパスと SHA-256、通常 Chrome とは分離することを表示して確認する。
- リモートデバッグは loopback 限定。一時 run profile は終了後に既定で削除する。`--keep-artifacts` は
  デバッグ目的で残す。`persistent:<directory>` または `--profile-dir` のプロファイルは常に残す。
- URL は既定で `http` / `https` のみ。`file:`、拡張機能、ダウンロード、権限要求は明示フラグを
  必要とする。
- 各ステップに時刻、実効座標、jitter offset、CDP 応答、URL、スクリーンショットをログする。
  入力テキストと環境変数の値は既定でマスクする。
- `on_failure` が `abort` の失敗、または続行不能な失敗時は、以後の同一シナリオ手順を停止する。
  `continue` の失敗時も、最終スクリーンショットと診断（viewport、DPR、URL、locator hint）を
  artifacts に残して次のステップへ進む。終了時は成否を問わず CfT を graceful close する。

## 10. 受入基準

1. 通常 Chrome を開いたまま `crer play` しても、通常 Chrome のタブ、プロファイル、カーソル位置、
   フォーカスが変化しない。
2. 再生中にメモ帳等へ入力しても、CfT の再生ログのキーイベントは変化せず、逆に CfT のテキスト
   ステップはメモ帳へ入らない。
3. `--position` 指定で CfT のみが指定 DIP 位置へ移動し、viewport と DPR の検査が成功する。
4. 同じ scenario + seed で jitter 後の座標列が一致し、異なる seed では指定半径内で変わる。
5. YAML の `parallel` で 2 シナリオを実行してもプロファイル／CDP 接続／artifacts が混在しない。
6. `browser_zoom: 100` 以外を `strict` 指定したとき、検証不能なら安全側に失敗する。
7. `playback.seed` を省略した実行では uint64 の実効 seed が生成・記録され、その値を指定した
   再実行で jitter 後の座標列が一致する。
8. `on_failure.<kind>: continue` を指定した続行可能な失敗では、失敗が記録されつつ後続ステップ
   または後続 job が実行される。CDP 接続喪失など続行不能な失敗では実行されない。

## 11. 段階的実装

1. **基盤**: Deno TypeScript CLI、CfT のダウンロード／固定、専用起動、CDP client、`doctor`、YAML schema。
2. **再生 MVP**: navigate/wait/click/scroll/text/key、window bounds、DPR・viewport 検証、artifacts。
3. **記録**: Windows Raw Input ネイティブブリッジ、座標正規化、イベント圧縮、YAML 出力、locator hint。
4. **合成**: plan scheduler、並列 worker、キャンセル、統合レポート。
5. **堅牢化**: profile template、drag/IME、スクリーンショット差分、署名済み Windows 配布物。

## 12. 主要な制約と設計判断

- 「見える headful ブラウザ」と「物理入力に一切影響しない」は両立する。CDP 入力は OS の
  カーソルを経由しない。ただしサイトが synthetic input の差異を検出する可能性までは排除できない。
- CDP の tip-of-tree は互換性保証がない。実装ではインストールした CfT の `/json/protocol` を取得し、
  対応コマンドを起動時に検査する。
- 座標中心の方式は、DOM locator 中心のテスト自動化より画面の見た目に敏感である。これは「実際の
  クリックを記録し、DOM 操作を使わない」という要件を優先した意図的なトレードオフである。
- `Browser.setWindowBounds` は experimental CDP コマンドであるため、CfT バージョンを pin し、
  `doctor` の必須検査項目にする。

## 参考資料

- [Chrome for Testing: reliable downloads for browser automation](https://developer.chrome.com/blog/chrome-for-testing)
- [Chrome DevTools Protocol: Input](https://chromedevtools.github.io/devtools-protocol/tot/Input/)
- [Chrome DevTools Protocol: Browser（window bounds）](https://chromedevtools.github.io/devtools-protocol/tot/Browser/)
- [Chrome DevTools Protocol: Target（browser context）](https://chromedevtools.github.io/devtools-protocol/tot/Target/)

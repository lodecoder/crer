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

| 層 | 採用 | 理由 |
| --- | --- | --- |
| CLI・実行系 | Deno 2.8+ + TypeScript | 単一バイナリ配布、組み込み Web API、権限の明示、YAML／並行制御の実装性がよい。 |
| ブラウザ | 固定バージョンの Chrome for Testing | 自動更新する通常 Chrome と分離し、再現可能なバイナリを使う。 |
| 再生入力 | CDP の `Input` ドメイン | OS 入力を発生させず、ブラウザに低レベル入力を配送する。 |
| ウィンドウ | CDP `Browser.setWindowBounds` | CfT の対象ウィンドウだけを DIP 単位で移動・リサイズする。 |
| 記録入力 | Windows Raw Input（主）+ Low Level Hook（補助） | 物理入力を取得する。CDP は注入はできるが物理入力を記録する API ではない。 |
| シナリオ | YAML + JSON Schema | 人間編集、バリデーション、将来の自動補完を両立する。 |

Deno 側は組み込みの `WebSocket`、`jsr:@std/yaml`、`jsr:@zod/zod`（または JSON Schema validator）、
`jsr:@std/cli` を用いる。いずれも Deno で利用でき、Node.js 互換レイヤーを前提にしない。
Raw Input と HWND 操作は C ABI を公開する薄い C++ DLL `crer-win-input.dll` とし、
`Deno.dlopen()` でロードする。実行バイナリには同梱した信頼済み DLL のパスだけに
`--allow-ffi` を許可する。PowerShell や AutoHotkey をランタイム依存にはしない。

### 3.1.1 ネイティブ DLL 境界

`crer-win-input.dll` は各 Deno 配布バイナリと同じアーキテクチャ（`win-x86_64` または
`win-aarch64`）で同梱する。DLL は `crer_input_abi_version`、`crer_input_start`、
`crer_input_stop`、`crer_input_read`、`crer_input_last_error` だけを C ABI で export する。
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
既定では一時プロファイルを使い、`--profile-template` を指定したときのみ、停止中に複製した
テンプレートを使う。CfT プロセスは成功・失敗・中断のいずれでも、終了処理で CDP
`Browser.close` による graceful close を要求して閉じる。CDP が応答しない場合に限り、実行
ワーカーが起動した CfT 子プロセスだけをタイムアウト後に終了する。

## 4. 入力の記録と再生

### 4.1 記録

`crer record` は CfT を専用プロファイルで起動し、対象のトップレベル HWND と CDP target を
対応付ける。Windows Raw Input から受けた入力について、カーソル直下の HWND が対象 CfT の
コンテンツ領域である時だけ採用する。物理スクリーン座標は `GetClientRect`、DPI、CDP
`Page.getLayoutMetrics` を使って CSS viewport 座標に正規化する。

- `WM_INPUT` の移動、ボタン、ホイールを時間順に採取する。
- キーは対象 CfT が前景の場合だけ採取し、Scan Code／Virtual Key／修飾キーを保存する。
- テキストは `WM_CHAR` と IME の確定文字列を優先して 1 つの `text` ステップに畳む。
- クリックは down/up と移動をイベントとして保持し、停止時にクリック・ドラッグ・スクロール
  として可読なステップへ正規化する。元イベント列は `artifacts/raw-input.ndjson` に任意保存する。
- 記録中は UI によるページ操作を妨げない。CfT 以外で行った入力は記録しない。

記録開始後の停止操作は `Ctrl+C`（1 回目は graceful stop、2 回目は強制中断）または CfT
ウィンドウの終了とする。graceful stop では、未確定の down/up 対を `raw-input.ndjson` に残し、
YAML へは不完全な操作を出力せず警告する。CfT が前景でない間のキー入力、Chrome のタブバー・
アドレスバー・DevTools 上の入力、対象コンテンツ領域外のポインター入力は記録しない。

座標は、Raw Input の物理 screen px を対象コンテンツ HWND の物理 client px に変換し、同時点の
`Page.getLayoutMetrics().layoutViewport.clientWidth/clientHeight` と `GetClientRect` の幅・高さの
比で CSS viewport px に換算する。すなわち `x = clientX * cssWidth / clientWidth`、
`y = clientY * cssHeight / clientHeight` とする。記録中に client rect、DPR、viewport が変化した
場合は、その直後に `viewport_changed` 境界イベントを挿入する。既定ではこのイベントをまたぐ
記録を停止して利用者に分割を求めるため、異なる表示条件の座標を一つの scenario に混在させない。

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

`click`／`double_click` に個別 `jitter` を書く場合は `enabled`、`distribution`、`radius_px`、
`min_distance_from_edge_px`、`out_of_bounds` の全項目を必須とする。個別設定があるとき、全体設定の
`playback.jitter` は一切参照しない。

ドラッグは始点／終点の両方に独立した揺らぎを適用し、`steps`（既定 12）で補間する。テキスト
入力には IME を要しない `Input.insertText` を既定とし、ショートカット等は個別の key down/up を
使う。これは DOM 値代入ではなく、CDP が提供するテキスト入力注入である。

## 5. 表示の固定と可搬性

画面座標の再現性は OS のスケーリングに依存する。v1 は次を再生前提とする。

- 100% の Windows 表示スケーリングを推奨し、実行時に主モニター DPI と CfT の `devicePixelRatio`
  を検査する。
- `window.content` は CSS viewport の目標サイズ、`window.bounds` は画面上の DIP 位置である。
  `Browser.setWindowBounds` と `Browser.setContentsSize` を順に実行し、実測値を検証する。
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
  profile: ephemeral                 # ephemeral | template:<path>
  initial_url: https://example.test/orders
  window:
    bounds: { left: 1640, top: 80, width: 1080, height: 900 } # screen DIP
    content: { width: 1040, height: 760 }                       # CSS px
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
  on_failure:                       # ステップ失敗種別ごとの既定動作
    default: abort                   # abort | continue
    timeout: continue
    assertion: continue
    action: abort
    jitter_bounds: abort
steps:
  - do: wait_for
    url: "https://example.test/orders*"
    state: network_idle
  - do: click
    at: { x: 211, y: 182 }
    locator_hint: { role: textbox, name: Search }
    jitter:                         # この click では playback.jitter を完全に上書き
      enabled: true
      distribution: uniform
      radius_px: 1
      min_distance_from_edge_px: 2
      out_of_bounds: fail
  - do: text
    value: "${ENV:ORDER_ID}"
  - do: key
    key: Enter
  - do: wait_for
    locator_hint: { role: table, name: Results }
    state: visible
  - do: scroll
    at: { x: 920, y: 620 }
    delta: { x: 0, y: 561 }
```

許可する `do` は `navigate`、`wait_for`、`click`、`double_click`、`mouse_move`、`drag`、`scroll`、
`text`、`key`、`key_chord`、`screenshot`、`assert`、`sleep` である。`wait_for` と `assert` は
ページ状態を読むため CDP Runtime/DOM を使ってよいが、ページを変更してはならない。

`click` と `double_click` の `jitter` は `playback.jitter` と同じスキーマを持つ任意フィールド
である。省略時だけ `playback.jitter` を使う。`jitter` を指定したステップに `radius_px` 等の
フィールドがない場合はエラーとし、全体設定から補完しない。`drag` の個別揺らぎは v1 では
未対応で、常に `playback.jitter` を使う。

`playback.on_failure` は、続行可能なステップ失敗に対するポリシーである。キーは `default`、
`navigation`、`timeout`、`action`、`assertion`、`jitter_bounds` のみを許可し、値は `abort` または
`continue` とする。該当種別の設定を優先し、なければ `default`、さらに `default` もなければ
`abort` を使う。`continue` の場合は失敗を artifacts と最終結果に記録した上で次のステップへ
進む。YAML／CLI 検証エラー、実行環境不一致、CDP 接続喪失、CfT クラッシュ、利用者による中断は
続行不能であり、この設定にかかわらず停止する。

失敗種別は次で固定する。`navigation` は `navigate` または URL 待機の CDP エラー、`timeout` は
ステップの待機時間超過、`action` は CDP 入力の拒否・入力状態不整合、`assertion` は `assert` または
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
crer play orders.crer.yaml --position 1640,80 --seed 42
crer run nightly.crer.plan.yaml --max-parallel 2
crer inspect artifacts/<run-id>      # ステップ、失敗、スクリーンショットを表示
```

`record new` はテンプレートと CfT を起動し、利用者が終了コマンドを送るか CfT を閉じたときに
正規化・検証した YAML を保存する。`play` の CLI オプションは明示的に指定した場合のみ
front matter を上書きし、実行ログに override を記録する。`--position` は再生ウィンドウだけを
移動する。

終了コードは `0` 成功、`2` YAML/CLI 検証エラー、`3` 環境・ブラウザ不一致、`4` 操作または
assert の失敗、`5` 中断とする。

## 9. 安全性・ログ・失敗時の扱い

- 初回起動時に CfT 実行ファイルのパスと SHA-256、通常 Chrome とは分離することを表示して確認する。
- リモートデバッグは loopback 限定、run profile は終了後に既定で削除する。`--keep-artifacts` のみ
  スクリーンショット、CDP trace、プロファイルを保持する。
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

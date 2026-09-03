# crer

Windows 上で Chrome for Testing を専用プロファイルで起動し、GUI ブラウザ操作を記録・再生する
Deno CLI です。再生は Chrome DevTools Protocol (CDP) の入力注入を使うため、OS の物理マウス・
キーボードや普段使いの Chrome を操作しません。

CfT には翻訳、パスワード保存、パスワード漏えい検出のブラウザ UI を抑止する設定を適用するため、
記録・再生中にログインパスワードを保存するか確認するポップアップは表示しません。

## 現在の実装範囲

- `.crer.yaml` の検証、CfT の隔離起動、headful な CDP 入力再生
- click / double-click / move / drag / scroll / text / key / key_chord / navigate / wait / assert / screenshot / sleep
- シード付きクリック揺らぎ、失敗 artifacts、YAML plan の直列・並列実行（`max_parallel` 対応）
- C# .NET 10 Native AOT の Raw Input DLL と NDJSON 記録

screen px から CSS viewport px への正確な変換、IME を含む text
正規化は次の実装段階です。

現在の `record` は Windows のキーボードレイアウトを使って英数字と記号を正規化します。日本語 IME の
確定文字列、特殊キー、複雑なショートカットは YAML を後から編集してください。

## 実行

Deno 2.8 以降と Chrome for Testing を用意し、Chrome 実行ファイルを `CRER_CHROME` に設定するか
`--chrome` で渡します。未指定時に通常 Chrome へフォールバックすることはありません。

固定版を導入する場合は `./scripts/install-chrome-for-testing.ps1 -Version 152.0.7977.42` を実行します。
導入結果は `.crer/browsers/crer-chrome.json` に保存されます。

すでに CfT を導入済みの場合は、ダウンロードせず manifest に登録できます。

```powershell
. .\scripts\install-chrome-for-testing.ps1 -ChromePath $env:CRER_CHROME -Version 152.0.7977.42
deno task dev doctor
```

```powershell
$env:CRER_CHROME = 'C:\\path\\to\\chrome.exe'
deno task dev validate scenario.crer.yaml
deno task dev play scenario.crer.yaml --keep-artifacts
deno task dev run nightly.crer.plan.yaml
deno task dev inspect .crer\runs\<run-id>
deno task test
```

ローカル fixture による headful 再生と物理カーソル不変の確認は次で実行できます（実行中はマウスを動かさないでください）。
再生前の control 観測でもカーソルが動くデスクトップ環境では、スクリプトは判定不能として警告します。

```powershell
.\scripts\test-playback-fixture.ps1
```

ドラッグ再生の fixture は次で確認できます。マゼンタのハンドルが右へ動き、終了位置が assertion されます。

```powershell
.\scripts\test-playback-fixture.ps1 `
  -Scenario fixtures\playback\drag.crer.yaml
```

失敗 artifacts は次で確認できます。

```powershell
.\scripts\test-playback-fixture.ps1 `
  -Scenario fixtures\playback\failure.crer.yaml `
  -ExpectedExitCode 4
```

### 手動記録と正規化

初回だけ Native AOT DLL を公開ビルドします。Visual Studio Build Tools の MSVC と Windows SDK が必要です。

```powershell
dotnet publish native/Crer.WinInput.csproj -c Release -r win-x64
```

CfT のコンテンツ領域で操作を記録し、直接起動時は PowerShell で `Enter` または `Ctrl+C` を押して停止します。
`--stop-file` を使う自動化では、指定した stop file の作成で停止します。
較正完了後のクリックは標準出力へ CSS 座標（例: `[crer] click: { x: 493, y: 132 }`）を表示します。
content bounds を取得できない場合だけ、代わりに物理画面座標を `screen_px` として表示します。

```powershell
deno task dev record .crer\raw-input.ndjson `
  --url 'https://example.test' `
  --content-size 860,560 `
  --position 1200,80
deno task dev normalize .crer\raw-input.ndjson `
  --url 'https://example.test' `
  --output recorded.crer.yaml `
  --name example-recording
```

重いサイトでキャッシュ・Cookie・Local Storage・Service Worker を再利用する場合は、CfT 専用の
永続プロファイルを指定します。指定できるのはワークスペースの `.crer\profiles` 配下だけです。
通常 Chrome のプロファイルは指定できません。

```powershell
$profile = "$PWD\.crer\profiles\yahoo"

deno task dev record .crer\yahoo.ndjson `
  --url https://www.yahoo.co.jp/ `
  --profile-dir $profile

deno task dev play .crer\yahoo.recorded.crer.yaml `
  --chrome $env:CRER_CHROME `
  --profile-dir $profile `
  --mute-audio
```

`record --profile-dir` の sidecar を `normalize` すると、生成 YAML の `browser.profile` は
`persistent:<絶対パス>` になります。以後は `play` の `--profile-dir` を省略しても同じプロファイルを
使えます。CLI 指定は YAML より優先します。永続プロファイルは削除されず、同時に複数の再生で共有できません。
`run` で使う場合は `max_parallel: 1` にしてください。`.crer\profiles` 外のパス、`..` を含むパス、
実体が外部を指すシンボリックリンクは拒否されます。

`record` の主な指定:

- `--content-size <width>,<height>` — CfT に要求する content host のサイズ。既定は `860,560`。
  ページがスクロールバーを表示する場合、実効 CSS viewport はこれより小さくなる。記録は両方を
  保存し、再生時は記録済みの実効 viewport が1秒維持されるまで最大30秒待ってから検証する。
  ページ遷移などで実効 viewport が変化した場合は、その後の各入力に新しい座標系を保存して
  正規化します。
- `--position <left>,<top>` — CfT ウィンドウの画面上の位置。負の座標も指定可能。
- `--profile-dir <directory>` — CfT の専用プロファイルを再利用する。`record` / `play` / `run` で使用可能。
- `--mute-audio` — `play` / `run` 時の CfT だけをミュートする。Windows 全体および通常 Chrome の音量には影響しない。

正規化後の YAML には、実測した値が次のように保存されるため、再生時も同じ viewport と位置を使う。

```yaml
browser:
  window:
    bounds: { left: 1200, top: 80 }
    content: { width: 860, height: 560 }  # CfT に要求するサイズ
    viewport: { width: 845, height: 545 } # スクロールバーがある場合の実効 CSS 座標系
```

ローカル fixture を使う手動 P0 テストは、次の補助スクリプトで一つの PowerShell から実行できます。

```powershell
.\scripts\record-playback-fixture.ps1
```

Chrome for Testing が fixture を開いたら、まずページ左上のマゼンタ色の 64×64 CSS px 点をクリックして
座標を較正します。点が消えた後に検索欄への入力と Submit のクリックを行い、記録元の PowerShell で
Enter を押します。較正クリック自体は記録されません。結果は `.crer/fixture.raw-input.ndjson` に保存されます。
同スクリプトは続けて `.crer/fixture.recorded.crer.yaml` も生成します。`-Output`、`-Scenario`、`-Port`、
`-Chrome` で変更できます。記録時に有効なコンテンツ領域と CDP viewport を取得できた場合、`.meta.json`
sidecar が作成され、`normalize` はこれを使って CSS 座標へ自動変換します。要求サイズは
`browser.window.content`、スクロールバーを含む実効 CSS 座標系は必要に応じて
`browser.window.viewport` として YAML に保存します。sidecar がない場合は、従来どおり
`--client-origin`、`--client-size`、`--viewport` をすべて指定してください。

`record --duration-ms 500` は、実入力をせずに DLL の起動・停止を確認する smoke test です。

### 画像テンプレートでのクリック

記録済みの `click` は、`at` をテンプレート指定に置き換えられます。再生直前の CDP screenshot から
テンプレートを探索し、一致矩形内のランダム位置をクリックします。テンプレート画像のパスは YAML からの
相対パスです。

```yaml
playback:
  template: { min_similarity: 0.8, random_inset_px: 2, on_missing: skip }
steps:
  - { do: click, template: { path: templates/sign-in.png, on_missing: fail } }
```

`playback.template` は全 template click の既定値です。個別の `template` に `min_similarity`、
`random_inset_px`、`on_missing` を指定するとその値を優先します。`min_similarity` は 0〜1、既定値は
`0.8`、`random_inset_px` の既定値は `0` です。`on_missing` は既定の `fail` なら `template` 種別の
失敗、`skip` ならクリックを行わず正常に次のステップへ進みます。どちらの場合も探索直前の画面を
`template-<step-index>.png` として artifacts に残します。クリック位置は再生 seed で決まるため、同じ
seed なら同じ位置が選ばれます。テンプレートはクリック可能な領域だけを切り出してください。

`on_missing` は step 直下ではなく、必ず `template` 内へ指定します。`fail` は template 失敗を発生させますが、
停止するかどうかは `playback.on_failure.template`（なければ `playback.on_failure.default`）に従います。
必ず停止したい場合は `playback.on_failure.template: abort` を明示してください。
探索ごとに標準出力へ、テンプレートパス・実測類似度・適用閾値を出力します。

```text
[crer] template: templates/sign-in.png, similarity: 0.9234, threshold: 0.8
```

### テンプレート条件分岐

テンプレートが閾値以上で見つかった場合だけ複数の操作を実行するには `do: if` と `then` を使います。
不一致は正常な分岐であり、`else` があればそれを、なければ次の兄弟ステップへ進みます。条件の既定値には
同じ `playback.template` を使います。

```yaml
- do: if
  template: { path: templates/signed-in.png, min_similarity: 0.9 }
  then:
    - { do: click, at: { x: 500, y: 200 } }
    - { do: text, value: "continue" }
  else:
    - { do: log, message: "ログイン状態ではありません" }
```

template click に `then` を指定すると、画像が見つかった場合だけクリックし、その後に操作列を実行できます。
画像が閾値未満ならクリックも `then` も実行せず、正常に次のステップへ進みます。これは `if` の `then` 内に
同じ template click を置く場合の糖衣構文です。`on_missing` は適用されません。

```yaml
- do: click
  template: { path: templates/target.png, min_similarity: 0.9 }
  delay_ms: 2012
  then:
    - { do: log, message: "target をクリックしました" }
```

条件評価時のスクリーンショットは `template-<step-index>.png`、実行・不一致の結果は `steps.ndjson` に
残ります。画像ファイルの欠落や読み込み不能は設定エラーとして失敗します。

`then` または `else` の**最初のステップ**が、条件と同じテンプレートパス・実効 `min_similarity` の
template click なら、条件評価済みの screenshot と一致結果を再利用します。たとえば次の click は二度目の
template matching を行いません。コンソール出力の末尾に `(reused)` と表示されます。途中に別ステップを
挟む場合、画像または閾値が異なる場合は、ページ状態の変化を正しく扱うため再探索します。

```yaml
- do: if
  template: { path: templates/target.png, min_similarity: 0.9 }
  then:
    - { do: click, template: { path: templates/target.png, min_similarity: 0.9 }, delay_ms: 2012 }
```

曜日で条件分岐する場合は `weekdays` に `mon`、`tue`、`wed`、`thu`、`fri`、`sat`、`sun` を一つ以上
指定します。既定は実行マシンのローカル時刻ですが、再現性が必要なシナリオでは `time_zone` に IANA
タイムゾーンを指定してください。

```yaml
- do: if
  weekdays: [mon, wed, fri]
  time_zone: Asia/Tokyo
  then:
    - { do: click, at: { x: 500, y: 200 } }
```

曜日が一致しない場合は、`else` があればそれを実行し、なければ `steps.ndjson` に条件評価結果を残して
次のステップへ進みます。

### 再生進捗の標準出力

任意の箇所へ `{ do: log, message: "..." }` を追記すると、再生時に標準出力へ `[crer] ...` と表示します。
進捗確認用のステップで、ブラウザやページには操作を行いません。メッセージは `steps.ndjson` にも残ります。

```yaml
- { do: log, message: "ログイン画面を表示しました" }
- { do: click, at: { x: 500, y: 200 } }
- { do: log, message: "ログイン操作を送信しました" }
```

### 操作の繰り返し

`do: repeat` の `steps` を `count` 回（0 以上の整数）繰り返します。`count: 0` は子ステップを実行しない
正常な no-op です。操作の間隔は、各子ステップの `delay_ms` で指定できます。

同じ場所を続けてクリックするだけなら、`click.count` に 1 以上の整数を指定できます。省略時は `1` です。
指定回数のクリックごとに `delay_ms` と全体の `step_delay_ms` を適用するため、次は同じ click を 3 ステップ
並べる場合と同じ待機順になります。

```yaml
- { do: click, at: { x: 970, y: 689 }, count: 3, delay_ms: 8080 }
```

座標の jitter または template の一致矩形内のランダム位置は最初に一度だけ決定し、全クリックで同じ位置を使います。

```yaml
- do: repeat
  count: 3
  steps:
    - { do: click, at: { x: 493, y: 132 }, delay_ms: 500 }
    - { do: log, message: "クリックを繰り返しました" }
```

子ステップは通常の操作・`if`・さらに `repeat` を含められます。実行ログの step index は
`<repeat-index>.<繰り返し回数>.<子-step-index>` の形式になります。

### テンプレート状態までの繰り返し

`do: repeat_until` は、テンプレートが指定した状態になるまで子ステップ列を繰り返します。`state: visible`
は画像が見つかるまで、`state: hidden` は画像が見つからなくなるまで待ちます。各試行の**前**に探索するため、
開始時点で状態を満たしていれば子ステップは実行しません。

```yaml
- do: repeat_until
  template: { path: templates/complete.png, min_similarity: 0.9 }
  state: visible
  max_attempts: 10
  on_limit: fail
  steps:
    - { do: click, at: { x: 500, y: 200 }, delay_ms: 500 }
    - { do: log, message: "完了画面を確認中" }
```

`max_attempts` は子ステップ列を実行する最大回数（1 以上の整数）です。上限後も状態を満たさない場合、
`on_limit: fail` は `template` 種別の失敗にし、`on_limit: continue` は `status: skipped` を
`steps.ndjson` に残して次の兄弟ステップへ進みます。各探索画面は
`template-<step-index>.attempt-<試行回数>.png` に保存され、類似度も標準出力へ表示します。

同じ画像が複数あり、クリックするたびに対象が消える／変化する場合は `state: hidden` を使います。現在の
最良一致をクリックして画面を再探索するため、画像がなくなるまで順に処理できます。親の判定と最初の子 click が
同じ template path・実効閾値なら、その試行では一致結果を再利用し、template matching は一度だけです。

```yaml
- do: repeat_until
  template: { path: templates/item.png, min_similarity: 0.9 }
  state: hidden
  max_attempts: 50
  on_limit: fail
  steps:
    - { do: click, template: { path: templates/item.png, min_similarity: 0.9 }, delay_ms: 500 }
```

クリック後も同じ画像が残る静的な複数矩形を、一枚の screenshot から重複なく列挙する機能はまだありません。
この場合は上限に達するため、対象が消える操作に限ってこの形式を使います。

### 操作列の再利用

トップレベルの `functions` に名前付き操作列を定義し、`do: call` で呼び出せます。関数名は英字または
`_` で始まり、英数字・`_`・`-` を使えます。引数を使って共通操作を再利用できます。

```yaml
functions:
  search:
    params: [query]
    steps:
      - { do: click, at: { x: 200, y: 100 } }
      - { do: text, value: "${query}" }
      - { do: key, key: Enter }

steps:
  - { do: call, function: search, args: { query: "crer" } }
  - { do: log, message: "検索操作を完了しました" }
```

未定義関数や循環呼び出しは失敗として停止します。関数内には通常の操作、`if`、`repeat`、別の `call` を
書けます。`params` は一意な識別子の配列、`args` はその全てを文字列または有限の数値で指定する mapping です。関数内の
`${名前}` は対応する引数に置換され、`value`、`message`、`url`、テンプレートのパスを含む文字列フィールドで
使えます。プレースホルダーだけで構成される数値フィールドには数値のまま展開されるため、`repeat.count`、
`delay_ms`、`sleep.ms`、座標、`click.count`、`repeat_until.max_attempts` にも使えます。引数を持たない従来の `functions.<名前>: [steps...]` 形式も
引き続き利用できます。

```yaml
functions:
  retry:
    params: [count, delay]
    steps:
      - { do: repeat, count: "${count}", steps: [ { do: click, at: { x: 493, y: 132 }, delay_ms: "${delay}" } ] }
steps:
  - { do: call, function: retry, args: { count: 3, delay: 500 } }
```

関数引数の値で分岐するには `if.equals` の `left` と `right` を比較します。関数内では置換後の文字列で
比較されます。

```yaml
- do: if
  equals: { left: "${mode}", right: "weekday" }
  then:
    - { do: log, message: "平日処理" }
  else:
    - { do: log, message: "休日処理" }
```

`normalize` が生成する YAML は、編集しやすいよう `steps` の各要素を一行の flow mapping
（例: `{ do: click, at: { x: 10, y: 20 } }`）で出力します。
既存の複数行形式も読み込み可能です。

全体で揺らぎを有効にしている場合も、特定のクリックだけは次のように無効化できます。

```yaml
- { do: click, at: { x: 973, y: 640 }, jitter: { enabled: false } }
```

記録された操作間の待機時間は、通常は直前の操作の `delay_ms` として出力します。たとえばクリック後に
1,040 ms 待つ場合は `{ do: click, at: { x: 493, y: 132 }, delay_ms: 1040 }` です。再生では成功した
操作の後に待機してから次の操作へ進みます。手編集で先頭や単独の待機を入れる場合は、従来どおり
`{ do: sleep, ms: 1040 }` を使えます。

再生時に viewport mismatch を警告だけにして続行する必要がある場合は、`play` または `run` に
`--ignore-viewport-mismatch` を指定します。`display.json` には実測値とこの指定の有無が保存されます。
座標操作の安全性は下がるため、画面差異を確認する調査用途に限って使用してください。

### 配布用ビルド

次で `dist\win-x64\crer.exe` と同じフォルダの `crer-win-input.dll` を生成します。CfT は配布物に
含めないため、実行時に `CRER_CHROME` または `--chrome` で専用の `chrome.exe` を指定してください。

```powershell
.\scripts\build-release.ps1
$env:CRER_CHROME = 'C:\path\to\chrome.exe'
.\dist\win-x64\crer.exe doctor
```

ARM64 の Windows PC 向けには、ARM64 用の MSVC / Windows SDK を導入した上で次を実行します。

```powershell
.\scripts\build-release.ps1 -Runtime win-arm64
```

CfT を使う配布物の smoke test は次です。これは `crer.exe` で fixture を再生し、artifacts と物理カーソルを
検査します。

```powershell
.\scripts\test-release.ps1
```

実行 artifacts は `.crer/runs/<run-id>` に出力されます。`steps.ndjson` には各ステップの時刻、実効座標、
jitter offset、URL、成否が追記され、`crer inspect` で件数と失敗数を確認できます。

- 仕様書: [docs/specification.md](docs/specification.md)
- 手動結合テスト: [docs/manual-test.md](docs/manual-test.md)
- Raw Input bridge: [native/README.md](native/README.md)

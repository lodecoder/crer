# C# CLI 初期 milestone の互換性

この一覧は実装前に定める対応範囲であり、動作確認の完了記録ではない。既存 Deno CLI を残し、
`src/cli.ts`、`src/main.ts`、`src/yaml.ts`、`src/types.ts`、`src/errors.ts`、
`src/runtime.ts`、`src/actions.ts` と関連テストを互換性の基準にする。
実装・publish・実機検証の結果と再現コマンドは移行実装の文書に追記する。

## 対応予定

| 対象                   | 初期 milestone の範囲                                                                                                                                       |
| ---------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------- |
| コマンド               | 引数なし / `help`、`validate <scenario>`、`play <scenario> [options]`                                                                                       |
| 必須 CLI オプション    | `play --chrome <exe>`、`--keep-artifacts`。`--chrome` を `CRER_CHROME` より優先                                                                             |
| 基本 YAML              | `version: 1`、文字列 `name`、`browser`、任意の `playback`、`steps` 配列                                                                                     |
| ブラウザ               | http/https の `initial_url`、省略または `profile: ephemeral`、任意の文字列 `chrome`                                                                         |
| ウィンドウ             | `window.bounds`、`window.content`、`window.viewport`。content は要求サイズ、viewport は CSS 座標系                                                          |
| 表示検査               | `display.expected_dpr`、`browser_zoom: 100`、`zoom_check: strict` を使う fixture。実測 viewport / DPR / visual scale を確認                                 |
| 再生設定               | jitter 省略または `{ enabled: false }`、`timeouts.action_ms`、`step_delay_ms`。追加設定は移植済みの場合のみ受理                                             |
| `click`                | `at: { x, y }`、`hold_ms`、`delay_ms`。CDP の移動、左ボタン押下、左ボタン解放。`count` は回数と各回の待機を移植する場合だけ受理                             |
| `wait_for` / `assert`  | URL、`state: complete`、`locator_hint: { role, name, text }`。`wait_for state: visible` は hint 必須。`network_idle` は Network 追跡を移植する場合だけ受理  |
| `screenshot` / `sleep` | 安全な `name` に PNG 保存 / 非負の `ms` を中断可能に待機                                                                                                    |
| 結果                   | `play` の標準出力に JSON `{ code, failures, runDir }`、診断は標準エラー。成功 0、CLI/YAML 2、環境 3、step/assert 4、中断 5                                  |
| artifacts              | `.crer/runs/<run-id>` の `run.json`、`steps.ndjson`、明示・失敗時 screenshot、起動・表示・終了の診断。一時 profile は既定で削除し `--keep-artifacts` で保持 |

この範囲外の既存オプション（`--dll`、`--seed`、`--step-delay-ms`、`--profile-dir`、
`--template-screenshots`、`--position`、`--ignore-viewport-mismatch`、`--mute-audio`）は、
移植を追加して検証するまで拒否する。対応を追加する場合も名称と優先順位を変えない。
`browser.window.foreground` / `foreground_mode` / `opacity`、永続 profile、URL 遮断、
有効な jitter、template、failure policy、functions、制御構文、その他の action と
`doctor` / `inspect` / `run` / `record` / `normalize` は初期必須範囲に含めない。

## 起動前の検証と拒否

`validate` と `play` は同じ対応範囲を検査する。未知のキー、不正な型・値、未移植の機能は
Chrome 起動前に終了コード 2 と対象のパスを含む診断で拒否する。たとえば
`steps[0].do is not supported by the C# milestone: text` のように、利用者が修正箇所を特定できるようにする。
既存 Deno で有効でも未移植なら、その機能を無視して `valid` または成功を返さない。
plan を scenario として扱わず、未対応であることを明示する。

互換 validator の細部は次のとおり。

- `version` は数値の 1、`name` は文字列、`steps` は配列。既存 validator は空の name / steps を許可する。
- `initial_url` は http/https の絶対 URL。`browser.chrome` は任意の文字列であり、実行ファイルの選択には使わない。
- `bounds.left` / `top` は必須の整数で負数も許可。`width` / `height` は任意の正整数。
  `content` / `viewport` の width / height は有限正数で、既存 validator は整数に限定しない。
- `expected_dpr` / `browser_zoom` は有限正数。`zoom_check` は `strict` / `advisory` / `off`。
  100 以外の zoom や未移植の検査モードは、値の妥当性と対応可否を区別して診断する。
- `at.x` / `at.y` は有限数で、既存 validator 自体は負数や viewport 外を拒否しない。
  jitter が無効なら元の座標をそのまま CDP に渡す。
- `delay_ms` / `hold_ms` / `sleep.ms` / `step_delay_ms` / `action_ms` は有限非負数。
  `hold_ms` は click のみ、`delay_ms` は sleep では禁止。`click.count` を受理する場合は正整数。
- `wait_for` / `assert` は url / state / locator_hint のいずれか必須。assert の state は complete のみ。
  hint の許可キーは role / name / text、値は文字列。
- screenshot の name は必須で、`^[A-Za-z0-9][A-Za-z0-9._-]*$` に一致する安全なファイル名。
- CLI は `command file options` の順。値付きオプションと値は別引数で、`--chrome=...` は非対応。
  重複オプション、未知のオプション、値不足（次が `--...` の場合も含む）を拒否する。

`playback.jitter` および step の jitter は省略時も無効。`{ enabled: false }` は、
許可キーを確認後に既存 validator が追加の値検査を省略する点にも注意する。
seed を移植する場合、YAML では uint64 範囲内の十進**文字列**が必須で、数値は受理しない。

## 実装から分かる意味と文書との差

| 項目               | 現在の Deno 実装と互換性上の判断                                                                                                                                                                                                            |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `--keep-artifacts` | `runtime.ts` は run metadata / PNG を常に残し、この switch は一時 `runDir/profile` の削除のみ抑止する。runDir 全体を削除しない                                                                                                              |
| パス               | scenario の相対パスは呼び出し時の cwd 基準。runDir も cwd の `.crer/runs` 配下。template は YAML の所在基準（今回は未移植）。Chrome の相対パスも cwd 基準                                                                                   |
| Chrome の指定      | `--chrome` > `CRER_CHROME`。`browser.chrome` は型検査のみで、この優先順位に入らない。通常 Chrome の自動探索・既存ブラウザへの接続はない                                                                                                     |
| 起動例             | specification の `--remote-debugging-port=0` / `--new-window` に対し、runtime は loopback の空き port を予約して数値で渡し、`--app=<initial_url>` を使用。C# も自身の profile の接続先だけを使う                                            |
| CLI 例             | specification の `browser install`、`record new/resume`、`run --max-parallel` は現在の CLI parser に存在しない。現行の実装済み構文を基準にする                                                                                              |
| locator            | 実装は明示的な `[role]` 要素だけを探索する。name は `aria-label` または trim した textContent、text は trim した textContent の完全一致。getClientRects があり visibility が hidden でない要素を一致とする。暗黙の button role を推測しない |
| URL 待機           | URL はアンカー付きの一致判定。現在は最初の `*` を wildcard に変換する。複数 wildcard の意味を勝手に拡張しない                                                                                                                               |
| display            | viewport は `window.viewport ?? window.content` と実測値を比較する。DPR / zoom / visual scale は strict 時に失敗、advisory または省略時は警告、off 時は省略。初期 fixture は strict を明示し、検査を無視して成功にしない                    |
| zoom               | `browser_zoom` は設定を変更する機能ではない。100 以外を strict で指定すると環境不一致。起動は `--force-device-scale-factor=1`、実測 visualViewport.scale は 1 を確認する                                                                    |
| playback 設定      | validator は `speed` と `timeouts.navigation_ms` を受理するが runtime に使用箇所がない。`action_ms` の既定値は 10000、step_delay_ms は 0。C# は未実装の設定を黙って受理しない                                                               |
| 記録の待機         | specification の記録節には sleep への変換例が残るが、README と現在の normalize は通常、前の操作の delay_ms として出力する。C# の click 後待機も delay_ms の意味を保つ                                                                       |
| 型定義             | `types.ts` は browser.chrome を必須文字列と記述するが、実際の `scenarioFrom` は省略を許可する。validator と既存 fixture を基準にする                                                                                                        |

click の CDP メッセージは mouseMoved → mousePressed → mouseReleased。
押下は `button: left, buttons: 1, clickCount: 1`、解放は `button: left, buttons: 0, clickCount: 1`。
押下が成功した後の中断・失敗時にも解放を試みる。`count` は double-click の clickCount とは異なり、
単独クリックの反復である。反復ごとに hold_ms、delay_ms、全体の step_delay_ms を適用する。
DOM の click / dispatchEvent / 値代入で入力を代替せず、OS の SendInput / SetCursorPos を使わない。

通常の最上位エラーは `crer: <message>` を標準エラーへ出す。Deno は CRER_DEBUG=1 の場合だけ
stack を追加する。play の実行開始後の失敗は JSON 結果にも含む。既存 Deno の標準出力には
scenario log が混ざることもあるため、すべてのコマンドの標準出力を単一 JSON と仮定しない。

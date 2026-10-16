# crer の C# 完全移行用 Goal

> 履歴資料: この文書は Deno → C# 完全移行時のプロンプトを保存したものです。
> DLL / ABI 維持、`--dll` / `CRER_INPUT_DLL` の互換性と旧パスに関する要件は当時のもので、現行契約ではありません。
> 現行は `src/Crer.Cli` から `src/Crer.Windows` を参照する単一 exe 構成です。ビルド・テスト・配布は [C# CLI](csharp-cli.md) を参照してください。

クリック再生の初期実装を引き継ぎ、現行機能、開発・検証、配布を C#/.NET 10 に移すためのプロンプト。
基準コミットは `50b50fd8bf82a3ce09c25040bca51f60304ba126`。
以下のコードブロック全体を送信して開始する。この文書の作成だけでは Goal を開始しない。

```text
/goal codex/csharp-migration ブランチで、crer を C#/.NET 10 に完全移行してください。現在のクリック再生 milestone を土台に、既存 Deno CLI が実装している全コマンド・全オプション・全 YAML 設定・全 step と記録・正規化・plan・session reuse・artifacts を移植し、通常の利用、ビルド、テスト、配布から Deno / Node.js / Bun 依存を取り除いてください。公開した C# crer.exe だけで現行の操作と記録・再生の一連の流れを Windows 上で実際に通し、下記の完了条件を満たすまで実装と検証を続けてください。

着手時の確認と互換性の基準:
- AGENTS.md、README.md、TODO.md、docs/specification.md、docs/csharp-cli.md、docs/csharp-cli-compatibility.md と現在の差分を読み、ブランチを確認する。利用者の変更、既存の profile、検証 artifacts を保護する。
- 基準コミット 50b50fd8bf82a3ce09c25040bca51f60304ba126 の src/**/*.ts、tests/**/*.ts、native、fixtures、scripts を現行実装の比較対象として固定する。現在の差分に利用者が追加した機能も調べ、移植対象の取りこぼしを防ぐ。
- 初期 C# 実装は help / validate / play と click / wait_for / assert / screenshot / sleep、専用 profile、CDP、表示検査、終了処理まで実機検証済みである。過去の成功を今回の全機能の検証結果に流用しない。
- 最初に全コマンド、各 option と環境変数、YAML キー、step、artifacts、既存テストの対応表を作る。各項目に移植先、検証方法、実行結果、残件を記録し、実装中も更新する。
- 文書と実装が違う場合は実際の入出力を調べて記録する。既存利用者が使う契約を基準にし、仕様書に例示されるだけで未実装の browser install、record new/resume、run --max-parallel 等を移行と同時に新設する必要はない。
- 現行のバグや曖昧な挙動を見つけても、移植の都合で黙って挙動を変えない。互換維持と修正の判断、その根拠、影響する入力、回帰テストを分けて記録する。

「完全移行」の定義:
- 公開コマンド名と公開 exe は crer / crer.exe とし、全ての製品機能を C#/.NET の実装で処理する。TypeScript を外部プロセスや埋め込みの Deno / Node.js / Bun で実行して代替しない。
- 配布物の起動時に .NET SDK や共有 .NET runtime のインストールを要求しない。win-x64 の最終 CLI は Native AOT で公開する。通常の self-contained publish は途中の切り分けには使ってよいが、それだけで最終 AOT 条件の達成とはしない。
- 新規 clone からの標準ビルド・検証・配布手順は .NET と Windows の必要なビルドツールで完結する。PowerShell はビルド・検証・CfT 導入の補助スクリプトに使ってよいが、CLI の製品機能を PowerShell に委譲しない。
- 検証期間中は固定した旧 Deno 実装を比較専用に使ってよい。移植と比較が終わったら、通常の src / tests / task / 配布経路から旧 TypeScript 実装と Deno 設定・lock・CI 依存を取り除く。比較用の手順は固定 Git ref から旧実装を取り出す任意の別経路に隔離し、履歴は残す。
- Native AOT の C# DLL は全て C# なので、単一 exe 化や DLL 境界の撤去自体は完了条件にしない。必要な DLL と ABI の維持、配置、探索、アーキテクチャ一致まで製品として動作させる。

移植する機能:
1. CLI と形式互換
   help、doctor、inspect、validate、play、run、record、normalize と src/cli.ts / src/main.ts に実装済みの全 option を移植する。CRER_CHROME / CRER_INPUT_DLL / CRER_CHROME_MANIFEST 等の利用箇所を調べ、明示 option の優先順位、引数順、パス解決、標準出力・標準エラー、ログ、JSON 結果と診断を維持する。--dll を含む互換 option を意味のない no-op にしない。
   scenario / plan の version: 1、未知キー拒否、型と値域、重複 option、値不足、YAML scalar / tag / alias / merge、uint64 seed の十進文字列、CLI の数値変換、safe artifact name を検証する。既存 validator の正負ケースを C# 側へ移し、生成・保存する YAML も既存 CLI で読み取れることを比較する。
2. 全 atomic action と入力状態
   navigate、wait_for、click、double_click、mouse_move、drag、scroll、text、key、key_chord、assert、screenshot、sleep、log、fail を移植する。click.count、hold_ms、delay_ms、step_delay_ms、キーの code / VK / modifiers / text、ドラッグ補間、URL 待機、明示 role の locator、network idle の意味を維持する。
   入力は対象 page session の CDP Input メッセージで送り、成功した down の解放と応答が失われた場合の保守的な解放を扱う。操作を DOM の click()、dispatchEvent()、値の直接代入、フォーム送信へ置き換えない。DOM やページ状態の読み取りは探索・照合・assert・記録較正に使ってよい。
3. seed と jitter
   splitmix64 / xoshiro256**、uint64 overflow、乱数の消費順、uniform / normal、radius、edge margin、out_of_bounds、step 設定による完全上書きを現行と一致させる。同じ seed で旧実装と乱数列・実効座標列が一致するテストを作り、浮動小数点演算の差は許容根拠と境界ケースで検証する。未指定 seed は暗号学的に生成して再実行可能な値を artifacts に保存する。
4. template と制御構文
   画像 template の探索・複数一致・類似度・random inset・on_missing、click.then、playback.template の既定値と上書きを移植する。template_screenshots の all / failure-only と失敗画像の保持を維持する。
   if の template / equals / weekdays と time_zone、then / else、repeat、repeat_until、for_each_template、break、functions / call / params / args、引数展開、再帰防止、反復上限、ネストした step index と cache scope を現行実装と一致させる。IANA time zone、日付境界、文字列展開、数値展開も検証する。
5. browser と Windows 制御
   明示した CfT を headful の専用 app window で起動し、自分の profile の loopback CDP endpoint と page / target / session のみを使う。通常 Chrome の探索・フォールバックや既存ブラウザへの接続をしない。
   ephemeral / persistent profile、--profile-dir、profile の排他、.crer/profiles 配下への制限、.. と symlink / junction 経由の逸脱拒否、パスの正規化と大文字小文字の扱いを移植する。
   window.bounds / content / viewport、DPR、browser_zoom、zoom_check strict / advisory / off、--ignore-viewport-mismatch、DPI 互換環境変数の除去、foreground と foreground_mode always / once、topmost watchdog、opacity と復元、mute-audio を移植する。明示的な調査 option による継続は診断を残し、通常の一致判定と区別する。
   browser.block_urls は既存の JavaScript 正規表現ソースとしての意味と検証を維持し、初回 navigation 前から遮断、cache / Service Worker の制御、解除、session reuse 時の差替えを扱う。.NET Regex に置換するだけで意味が変わらないかを比較する。
6. plan と session reuse
   serial / parallel のネスト、max_parallel、fail_fast、worker_ms、scenario / plan の on_failure、終了コード集約、未開始 job の中止、実行中 job の協調停止、同じ profile の直列化を移植する。
   browser_session.reuse: same-profile と focus: once / before-step を移植し、profile ごとの CfT process / window / CDP session 再利用、scenario ごとの URL・window・display・遮断設定の再適用、foreground_mode の優先順位、続行不能時の session 破棄、次回再作成、plan 終了時の全 CfT 終了を保証する。
   run 専用の window bounds / opacity override と template screenshot policy、play の position のマージ規則を混同しない。
7. record と normalize
   native/Exports.cs と native/tests の実装資産を利用し、Raw Input / Low Level Hook の開始・停止・overflow・再開始、対象 HWND の選別、QPC、物理座標と DPI、キーボードレイアウト、較正、コンテンツ領域と viewport の変化、sidecar、NDJSON、停止方法を移植する。
   Enter / Ctrl+C、--duration-ms、--stop-file、CfT の終了で正しく停止し、hook と未確定入力を処理する。既存の record は物理入力の観測であり、再生による物理入力注入とは分けて扱う。
   normalize は既存 raw NDJSON と古い sidecar も読み、mouse / drag / wheel / key / text、修飾キー、layout 由来の文字、操作間隔から delay_ms への変換、座標較正、新旧 metadata の fallback、不完全入力の警告、profile の引継ぎ、YAML 保存を互換にする。未対応の IME 確定文字列などは現行の制約を超えて成功扱いにしない。
   UnmanagedCallersOnly export を通常の C# メソッドとして呼ばない。共有 core への整理または正しい DLL 呼出しで再利用し、hook 処理やライフサイクルを二重実装しない。
8. 診断、artifacts、終了処理
   doctor の CfT path / version / SHA-256 / manifest / DLL の診断と inspect の artifact 要約を移植する。
   run-id の形式と日本語名の扱い、run.json、steps.ndjson、display / launch / foreground などの診断、明示・失敗時 PNG、template 画像保存方針、raw NDJSON / sidecar を維持する。入力 text の値など、既存がログに出していない情報を追加で記録しない。
   --keep-artifacts は一時 profile の保持を制御し、診断と画像は保持する。persistent profile は削除しない。
   成功 0、CLI / YAML 2、環境 3、step / assert 4、中断 5 を維持する。abort / continue の失敗集約、fail の無条件停止、環境喪失時の停止を検証する。
   成功・失敗・Ctrl+C・接続喪失・起動 timeout・worker timeout・fail_fast・record の停止で、保留 CDP 要求、入力、hook、watchdog、worker、socket、profile lock と自身の CfT を解放する。利用者に未処理例外の stack を出さない。

実装と検証の進め方:
- まず現行の Deno verify と C# のテスト・publish・click-only 実機 fixture を確認し、開始時の失敗と新しい回帰を区別する。
- CLI / schema、atomic actions、template / 制御構文、plan / reuse、record / normalize、配布切替を、依存関係に応じた検証可能なまとまりで進める。独立した作業は subagent に並列化してよいが、共有ファイルの競合を避け、統合後の検証も行う。
- C# の実行コードを CLI 解析、モデル / validator、CDP / browser lifecycle、executor、scheduler / session、record / normalize、artifacts 等に分ける。実装に合った構成を選び、移植範囲の拡大を単一巨大クラスへの追記だけで処理しない。
- 各段階で旧実装と同一入力を比較する。可変の日時・PID・UUID 等だけを正規化し、終了コード、ページ状態、入力列、seed / 座標、制御フロー、artifacts の意味の差を隠す正規化はしない。
- 本番の再生経路に SendInput / SetCursorPos / 物理キー注入を入れない。record の受入検証では利用者の手動操作、既存 raw-input fixture、テスト用 hook packet を使う。手動操作が必要なら、実装と準備済みの具体的手順を整えて依頼し、その間に独立してできる検証を進める。
- fixture server も C# / BCL で動かす。サーバーと Chrome を同じ接続可能なネットワーク環境で起動し、サーバー疎通失敗をブラウザ再生成功に読み替えない。
- Native AOT と globalization / serialization / image 処理 / regex / time zone を早期から publish で確認する。reflection / trimming / AOT 警告を無条件抑制して成功扱いにせず、警告の原因と実行経路を検証して解消する。

最終受入条件:
1. 対応表に現行機能の未移植項目がなく、C# の schema / CLI / 実行系が全コマンド・全 option・全設定・全 step を扱う。文書にだけある未実装構文と元からの制約は別に記録する。
2. 旧 tests/**/*.ts と native/tests が検証する振る舞いを C# の単体・結合テストへ移し、未知キー、不正入力、並行処理、境界、取消し、リソース解放を含めて成功する。テスト数の一致だけで移植を判断せず、元テストと対応表で意味の保存を確認する。
3. 同じ YAML を書き換えずに旧 CLI と公開 C# exe で比較し、click-only、search、keyboard-submit、drag、失敗、明示 fail、continue、plan、session reuse を実 CfT 上で成功させる。template、各制御構文、seed / jitter、永続 profile、URL 遮断、foreground always / once、opacity、DPI / viewport は既存 integration test と専用の決定的な fixture で実測する。
4. 成功・失敗・中断・CDP 切断・起動 timeout・worker timeout・並列 fail_fast・record overflow で、終了コード、診断、入力解放、全 pending 解消、自身の子プロセス / hook / lock の後始末を検査する。並列再生、同じ profile の排他、profile 逸脱拒否も検証する。
5. 既存 raw-input / sidecar の normalize 比較を通し、実機で record → normalize → validate → play を公開 C# exe だけで実行して、記録した操作の結果を assert で確認する。手動記録を mock や合成入力だけで代替した結果を実機確認済みとしない。
6. 通常の非 foreground 再生で物理カーソル・作業中の前景ウィンドウ・通常 Chrome に干渉しないことを、コード検査と読み取り観測で確認する。明示 foreground の場合は最前面・前景化と終了時復元を確認する。外部操作で判定不能の観測は成功と区別して残す。
7. 標準の build-release / test-release と fixture 補助スクリプト、README、仕様・移行文書、開発用 verify を C# 用に切り替える。新規環境で Deno / Node.js / Bun を PATH から外し、製品 exe だけでなく通常の build / test / fixture / release の経路が動作することを検証する。旧 parity harness は任意の比較専用手順へ移す。
8. win-x64 の最終 Native AOT publish と公開配布物による全受入が成功する。既存の win-arm64 配布ターゲットも維持して publish を検証する。ARM64 実機がない場合、その headful E2E は元から残る実機未検証として明記し、x64 の成功を ARM64 実機成功と表現しない。ARM64 build / publish 自体の未達は配布残件として記録する。
9. 製品経路に残る Deno / Node.js / Bun 呼出し・埋め込み runtime・TypeScript 参照を検索と依存関係検査で確認し、通常利用・標準 build / test / release からなくす。ブラウザ fixture の JavaScript や CDP による読み取り式はこの除去対象ではない。
10. 再現コマンド、互換性の判断、差分、全受入の結果、公開 exe と artifacts の場所、使用した CfT / .NET / Windows、元からの制約を最終文書にまとめる。作業を意味のある単位でコミットし、利用者由来の変更を除いて作業ツリーを整理する。

境界と停止条件:
- 製品仕様の新設、大規模な YAML / CLI 仕様変更、OS の自動操作機能への拡張はしない。受入のために旧 fixture / assertion を弱めたり、未対応機能を no-op や成功扱いにしたりしない。
- 通常 Chrome、通常 Chrome の profile、利用者の入力・既存の検証 artifacts を変更・削除しない。停止対象のプロセスと削除対象の一時ディレクトリは、その実行が所有するものに限定する。
- Git 操作は AGENTS.md に従い、対応する Git MCP を優先する。コミット message は prefix を付け、prefix 以外を日本語にする。push、PR 作成、merge は含めない。
- 環境制約や手動操作待ちがあっても、独立して進められる実装と検証を続ける。完了条件に必要な実機 E2E、AOT、配布、互換機能が残る間は完全移行済みとしない。
- 外部要因で進められなくなった場合は、試した経路、成功した検証、残る条件、必要な入力・環境を具体的に記録する。Goal の pause / blocked / complete は利用可能なツールの規則に従い、自己判断で達成条件を縮小しない。
- 完了報告は、移植範囲、Deno 依存の除去、旧実装との比較、テスト・実機 E2E・publish 結果、証跡、コミット、残る既知の制約を簡潔に示す。
```

構成は [OpenAI Docs の Using Goals in Codex](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex)
の、成果・検証方法・制約・対象範囲・反復方針・進行不能時の扱いを定める考え方を参照している。

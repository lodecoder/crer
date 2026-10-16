# C# 完全移行の対応表と検証台帳

> 履歴資料: 以下の状態・検証コマンド・証跡は 2026-10-15 の Deno → C# 移行受入時点のものです。
> `csharp/` / `native/`、入力 DLL、ABI 2、`--dll` / `CRER_INPUT_DLL` は当時の構成として保存しています。
> [テスト台帳](csharp-migration-tests.tsv) のパス・結果も同時点の記録です。現行の製品は `src/`、テストは `tests/` にあり、Windows 操作を単一 exe に組み込んでいます。
> 現行のビルド・検証・配布手順は [C# CLI](csharp-cli.md) を参照してください。

基準: `50b50fd8bf82a3ce09c25040bca51f60304ba126`。作業開始 HEAD:
`4ab1f47`、`codex/csharp-migration`。開始時の作業ツリーは clean。
この基準との差分は `docs/csharp-full-migration-goal.md` の追加だけで、利用者の実装変更はない。
過去の milestone の成功は今回の受入結果に数えない。状態は未移植・実装中・単体済・実機済・完了を区別する。

2026-10-15 の利用者の追加指示「ARM64は除外して進めて下さい」により、今回の受入対象は win-x64。
ARM64 の既存ターゲット指定は維持するが、publish / 実機検証と追加ツール導入は今回の完了条件から除外する。
除外前に不足していた ARM64 C++ component と Installer の診断は
`.crer/csharp-arm64-publish-20261015/` に保護する。ARM64 成功とは報告しない。

## 開始時点の検証（2026-10-15）

| 検証 | 結果・証跡 | 残件 |
| --- | --- | --- |
| `deno task verify` | 102 passed / 0 failed / 6 ignored | opt-in 実機を今回実行 |
| `dotnet run --project csharp/Crer.Tests -c Release` | 18 群成功 | milestone の拒否テストを移植に合わせ更新 |
| `dotnet run --project csharp/Crer.CdpTests -c Release` | 8 件成功 | 統合後再検証 |
| `dotnet run --project native/tests -c Release` | lifecycle 成功 | overflow・実機記録受入 |
| `scripts/build-csharp.ps1` | win-x64 Native AOT publish 成功 | 全移植後 x64/ARM64 publish |
| `scripts/test-csharp-fixture.ps1` | 同一 YAML の C#/Deno 3 ケース再生成功。`.crer/csharp-fixture/e758f67ac8ec4003a5120795d4496bab/summary.json` | カーソルは flag ケース不変、他2ケース外部操作の可能性あり判定不能 |
| 制限環境の fixture | `.crer/csharp-fixture/936d1b24bbd54225a90c5e812e8e1387`、CfT 起動 code 3 | 承認済みデスクトップ環境で上記再実行済 |
| NuGet 復元 | 初回 NU1900（audit service 接続失敗）。通常環境で復元後、新規依存の復元成功 | 最終クリーン build の警告再確認 |

## コマンド・引数・環境変数

全値 option は別引数、重複・未知・値不足を拒否。file は command の直後。

| 対象（基準の公開契約） | 移植先 | 検証方法 | 結果 / 残件 |
| --- | --- | --- | --- |
| 引数なし / help | Program / CliOptions | CLI 単体・公開 exe stdout | 実装済。Foundation / Crer.Tests 単体成功。新規 checkout の JS runtime 不在 PATH で verify 成功 |
| doctor、`--chrome --dll` | Diagnostics | path/version/hash/manifest/DLL 比較 | 実装済。Foundation の manifest/hash/path/CLI優先度単体成功 |
| inspect `<runDir>` | Diagnostics | files/steps/screenshots/欠損 JSON | 実装済。Foundation の浅い列挙・欠損/破損 JSON・profile非公開単体成功 |
| validate `<file>`（scenario/plan） | Scenario / Program | 全旧 yaml_test・YAML parity | 全 schema 実装済。Schema/Crer.Tests と固定旧 ref YAML36ケース成功 |
| play `<file>` | Playback | 同一 YAML、結果 code/failures/runDir | 実装済。同一 YAML click/search/keyboard/drag/failure parity と公開 AOT control12群成功 |
| play `--chrome --dll --seed --step-delay-ms --profile-dir --template-screenshots --position` | CliOptions / Playback | 順序・優先度・JS Number・uint64・各実効値 | 実装済。Foundation/Action/Executor 単体、seed実座標・step override実機成功 |
| play `--keep-artifacts --ignore-viewport-mismatch --mute-audio` | CliOptions / Playback | profile 保持・display 診断・起動引数 | 実装済。profile cleanup/表示診断と公開異常suite14件成功 |
| run `<plan>` `--chrome --dll --profile-dir --plan-window-bounds-override --plan-window-opacity-override --template-screenshots` | CliOptions / PlanRunner | override 完全置換 / reuse への適用 | 実装済。PlanTests の override 伝播と profile別reuse/timeout実機成功 |
| run `--ignore-viewport-mismatch --mute-audio` | PlanRunner | leaf 伝播 | 実装済。PlanTests の leaf option 伝播成功 |
| record `<raw>` `--chrome --dll --profile-dir --url --content-size --position --duration-ms --stop-file --name` | Recorder / NativeInput | 実機手動・停止各経路・metadata | 実装済。RecordTests と実入力手動record→normalize→validate→play成功。正式公開exeでduration停止とDPI互換除去・物理/CSS 600×400実測成功 |
| normalize `<raw>` `--output --url --name --client-origin --client-size --viewport` | Normalize / Program | 既存 raw/sidecar 比較・YAML 往復 | 実装済。NormalizeTests38件、raw/sidecar旧比較・YAML往復成功 |
| `CRER_CHROME`（明示 CLI 優先）、`CRER_INPUT_DLL`（明示 CLI 優先） | CliOptions / NativeInput | 不正 env と明示 option、ABI と配置 | 実装済。Foundation のCLI優先度、Record/native ABI単体・実機記録成功 |
| `CRER_CHROME_MANIFEST`、cwd と Chrome ancestor manifest 探索 | Diagnostics | path/hash 一致・欠損・破損 | 実装済。Foundation の構成manifest/hash比較・不一致・欠損単体成功 |
| `CRER_DEBUG`、`NO_COLOR`、端末判定 | Program / ScenarioLogger | stderr/ANSI/並列名前分離 | 実装済。Foundation の行ごと prefix/ANSI/plain 単体成功。公開異常CLIでno-stack成功 |
| `__COMPAT_LAYER` DPI flags 除去 | browser 起動 | chrome_environment_test・実測 | 共通launcher単体と正式公開exeのplay/record実機成功。DPIUNAWARE継承を除去し、record sidecarの物理600×400/CSS600×400・比率1を確認 |

## YAML 全キーと実行意味

| 対象 | 移植先 | 検証方法 | 結果 / 残件 |
| --- | --- | --- | --- |
| version/name/browser/playback/functions/steps と未知キー | Scenario | 全旧 validator 正負ケース | 全 schema 実装・単体成功 |
| scalar/tag/alias/merge、非有限値、重複、複数 document、循環 | Scenario YAML reader | 旧 parser parity + 境界 | 旧parserとの固定ref36 parityとSchema/Crer.Tests境界単体成功 |
| browser.chrome（型のみ）、initial_url、profile、block_urls | Scenario / browser / Profiles | URL・profile 逸脱・ECMAScript regex | 実装済。profile逸脱/排他単体、hidden V8 matcherでURL regex実機成功 |
| window.bounds.left/top/width/height、content/viewport.width/height | browser | サイズ/位置の実測、play position merge | 実装済。PlanTests option伝播とDPI/viewport/位置の実機成功 |
| window.foreground/foreground_mode/opacity | NativeInput / window lifecycle | always/once/watchdog/復元/再利用 | 実装済。always/once/watchdog/restore/reuse/opacity実機成功 |
| display.expected_dpr/browser_zoom/zoom_check | browser | strict/advisory/off/ignore 実測 | 実装済。正式公開exeで物理/CSS1330×1200、DPR不一致strict code3/advisory・off code0、viewport不一致code3/明示ignore code0と診断保存を確認。異display scale端末は未検証 |
| playback.seed/speed/step_delay_ms/log_color | PRNG / executor / logger | 消費順・遅延・色・入力 secrecy | 実装済。固定旧vector、同seed2回の実CDP座標一致、logger単体成功。speedは基準どおり未使用 |
| playback.timeouts.navigation_ms/action_ms | browser / executor | 0・timeout・取消 | action timeout/取消実装。navigation_msは基準どおり受理のみ、startupは固定15秒。公開異常suite14件成功 |
| playback.jitter.enabled/distribution/radius_px/min_distance_from_edge_px/out_of_bounds | Prng | splitmix/xoshiro/座標の旧比較 | 実装済。5seedの60乱数/80座標＋再試行消費、step完全上書き、実座標/drag両端成功 |
| playback.template.min_similarity/random_inset_px/on_missing | Template | 単/複数一致・失敗保持 | 実装済。Template9件・公開AOTの単/複数/skip/fail/保持成功 |
| playback.artifacts.template_screenshots | Template / artifacts | all/failure-only の実ファイル | 実装済。公開AOT controlでall/failure-onlyの実PNG数/名前/内容成功 |
| playback.on_failure.default/navigation/timeout/action/assertion/jitter_bounds/template | executor | continue 集約・続行不能停止 | 実装済。普通のfailure parityとtemplate失敗、続行/明示fail停止を確認。公開異常suite14件で環境/取消code3/5成功 |
| functions.<name>（配列 または params/steps）、call.function/args | Scenario / executor | 型維持展開・過不足・再帰・index | 実装済。Executor11件＋公開AOTで数値/文字列展開・直接/間接再帰・index/cache成功 |
| plan.version/name/max_parallel/timeouts.worker_ms/on_failure.default/scenario_failure/timeout/environment | Scenario / PlanRunner | scheduler の全既存意味・終了コード | 実装済。PlanTests21件と並列worker timeout/fail_fast実機成功 |
| plan.run.scenario/serial/parallel.jobs/fail_fast のネスト | PlanRunner | 共有上限・順序・未開始取消・drain | 実装済。共有上限/順序/取消/drain単体、公開AOT plan実機成功 |
| browser_session.reuse/focus | BrowserSessions | PID/window/session 一致・破棄・差替え | 実装済。正式公開exeでprofile A/Bそれぞれの同PID/HWND/target/session/sessionStorageと別profile間の分離、worker timeout後の該当session再作成と他session保持を確認 |

## 全 step

| step / 固有キー（共通 delay_ms は sleep/fail を除く） | 移植先 | 検証方法 | 結果 / 残件 |
| --- | --- | --- | --- |
| navigate:url | Actions | navigation・待機エラー | 実装済。atomic adapter/旧CDP列単体、同一YAML検索/drag実機parity成功 |
| wait_for:url/state/locator_hint(role/name/text) | Playback conditions | URL 初回 wildcard、visible/complete/network_idle | 実装済。V8による初回starの15ケース固定比較＋各fixture実機成功 |
| click:at/template/jitter/count/hold_ms/then | Actions / executor / Template | 同じ解決座標反復・消費順・入力解放 | 実装済。34件atomic単体＋template/count/then/cache/消費順の実機成功 |
| double_click:at/jitter | Actions | clickCount 1→2、個別 jitter | 実装済。clickCount/hold単体とstep jitter override実機成功 |
| mouse_move:at | Actions | CDP 列 | 実装済。固定旧CDP列と同seed実座標成功 |
| drag:from/to/steps | Actions | 補間・両端 jitter・失敗解放 | 実装済。補間/解放単体、同一YAML drag parity・両端同offset実機成功 |
| scroll:at/delta | Actions | wheel delta | 実装済。固定旧CDP列の単体と正式公開exeでwheel ±175・座標350,350のCDP入力、実ページ上下scroll状態を確認 |
| text:value | Actions | CDP insertText、値の非記録 | 実装済。insertText単体・keyboard実機parity成功、stepsに入力値を保存しない |
| key:key、key_chord:keys | Actions / InputGuard | code/VK/text/modifier、逆順解放 | 実装済。code/VK/text/modifiers・Unicode VK・逆順/曖昧down解放単体、Tab/Space実機成功 |
| assert:url/state/locator_hint | Playback | 明示 role・完全一致・非可視 | 実装済。明示role/完全一致を同一YAMLと公開AOT control実機確認 |
| screenshot:name | artifacts | safe name・PNG 内容 | 実装済。safe名はschema、明示/failure PNGは同一YAMLと公開AOTで成功 |
| sleep:ms | executor | 接続喪失・取消の応答 | 実装済。取消単体・fixture実機成功。接続喪失/取消は公開異常suiteでcode3/5・解放成功 |
| log:message | ScenarioLogger | 改行ごと名前・色・artifact | 実装済。prefix/色単体、実stepsのmessageと改行・入力secrecy確認 |
| fail:message | executor | continue でも無条件停止、code4 | 実装済。必ずcode4停止。固定旧refと同一YAMLでcode/failures/ExplicitFailureError prefix一致を再比較成功 |
| if:template/equals(left/right)/weekdays/time_zone/then/else | executor | 各条件・IANA・日付境界・一子 cache | 実装済。IANA境界/引数型単体とtemplate真偽/equals/weekdays/cache実機成功 |
| repeat:count/steps | executor | 0・ネスト index・中断 | 実装済。repeat0/3・ネストindexと取消単体、公開AOT実機成功 |
| repeat_until:template/state/max_attempts/on_limit/steps | executor / Template | 最終判定・画像保持・cache | 実装済。visible/hidden/attempt0/limit continue/fail・最後のPNG同内容・cache実機成功 |
| for_each_template:template/max_matches/steps、break | executor / Template | 静的候補・IoU・match_* 数値展開・最内 break | 実装済。静的2候補・数値match_*展開・nested if経由最内breakを実機成功 |

## artifacts・終了処理・配布

| 対象 | 移植先 | 検証方法 | 結果 / 残件 |
| --- | --- | --- | --- |
| UTC ms + safe 日本語名 + UUID run-id | RunArtifacts | run_id_test 移植・順序・Unicode | 実装済。FoundationのUTCms完全一致・順序・NFKC/日本語/astral切詰め成功 |
| run.json、steps.ndjson、display/launch/foreground/opacity/cleanup 等 JSON | artifacts | 内容比較・failure/index/入力 secrecy | 実装済。Foundation/同一YAML parity/public controlでindex/failure/message/input secrecy確認 |
| 明示/失敗 PNG、template policy、raw NDJSON/sidecar | 各実行系 | ファイル意味/PNG検証 | 実装済。public controlのPNG実ファイル/最後のtemplate画像保持、実手動記録raw/sidecar成功 |
| ephemeral/persistent/lock、keep-artifacts | Profiles / browser | cleanup、排他、symlink/junction、case | 実装済。Foundation20件で排他/再取得/Default・Cache・Preferences・lock外部junction拒否。正式公開exeでprofile排他と実junction逸脱code2を確認し、外部sentinel保持・Chrome未起動を確認 |
| code0/2/3/4/5 と abort/continue/fail 集約 | errors/executor/scheduler | 全異常 harness | 実装済。単体/plan/control/旧比較成功。公開異常suite14caseでcode2/3/4/5・診断・no-stack成功 |
| 成功/失敗/Ctrl+C/CDP喪失/起動timeout/worker timeout/fail_fast/overflow | 各 lifecycle | pending0・解放列・自分の子だけ終了・lock/hook/watchdog | 実装済。atomic/Record/Cdp/Plan/UrlBlocker異常単体とcleanup実機。公開異常suite14caseでpending0・profile/processcleanupと押下中CtrlC解放成功 |
| 非foreground 非干渉、明示foreground復元 | code + 読取観測 | カーソル/前景/通常 Chrome | 起動時の前景化を非activate launchへ修正。正式公開exeの専用時間帯127sampleでforeground/cursor変化0、owned CfT前景0、直後の通常Chrome既存64PIDは全生存。明示foregroundのstandalone/reuse・always/once・opacity・watchdog・復元も同じ配布物で4群成功 |
| fixture server BCL | Crer.FixtureServer | 実 Chrome からの疎通 | Crer.FixtureServerとcontrol専用BCL serverが実Chrome疎通・自分のprocess停止成功 |
| win-x64/ARM64 AOT、DLL ABI/配置 | build-release/test-release | publish と公開配布物 | 正式dist/win-x64へCLI/DLL AOT publish。最終新規checkoutから警告0で再公開し、標準test-releaseの5fixtureとrecord lifecycle5ケース成功、同一bitの配布物を正式distへ配置。ARM64は利用者指示で対象外 |
| build/test/verify/fixture/release の Deno/Node/Bun 除去 | scripts/docs/CI | PATH 除外・検索・fresh clone | 通常scriptsのDeno/Node/Bun呼出し除去、旧tracked src/tests/deno設定退役済。最終新規checkoutで3runtime不在PATHを確認しverify390件（12CLI project）+native、x64 AOT警告0、標準test-release5fixtureとrecord lifecycle5ケースすべて成功 |
| 旧比較経路を固定 ref に隔離 | optional parity scripts | 旧 checkout と同じ YAML | scripts/compatのgit archive固定refへ隔離済。標準実行から独立。YAML36＋play/plan同YAML比較成功 |

固定refの旧 tests/**/*.ts は `csharp-migration-tests.tsv` に test 名単位で列挙する。
単に件数を合わせず、各元 test の意味と追加境界・取消・cleanup を確認する。
108項目（旧29ファイル）は単体成功85、単体・実機成功18、実機成功5で、未移植0。
各行の証跡ファイルの存在も検査し、`.crer/migration-ledger-audit/summary.json` に結果を保存する。
この旧test対応に加え、背景非干渉は起動時の回帰を修正した公開exeで再受入した。

## 再現コマンド

標準検証は `./scripts/verify.ps1`、正式公開は `./scripts/build-release.ps1`、配布物検証は
`./scripts/test-release.ps1` を使う。個別のschema/plan・実機移植テストは次のとおり。
実機は通常のデスクトップ環境で実行し、foreground検証中は他の実機harnessを停止する。
外部操作のある背景観測は判定不能として元logを残す。

```powershell
dotnet run --project csharp/Crer.SchemaTests -c Release
dotnet run --project csharp/Crer.PlanTests -c Release
dotnet run --project csharp/Crer.IntegrationTests -c Release -- --exe dist/win-x64/crer.exe --chrome "$env:CRER_CHROME" --dll dist/win-x64/crer-win-input.dll --windows
```

IntegrationTestsはBCL loopback serverを空きportに立て、公開exeだけで再生・plan・記録・正規化を実行する。
`--only plan,blocking,keyboard,profiles,display,record,observation,windows` の値を絞ると対象群だけを再実行できる。
foregroundの2群は `--windows` が必要。`--probe-startup` は所有CfTだけで起動APIを比較する診断専用経路。


## 今回の追加検証（2026-10-15）

- `.crer/csharp-ledger-unit-20261015/summary.json`: Actions34、Executor11、Template9、UrlBlocker10、Foundation20 が成功。Foundationのjunction作成はsandboxで拒否されたため、承認済み通常権限で再実行し成功した。失敗時のlogも保護する。
- `.crer/control-integration/6f8e4f111bfc41b59a6841519ce7505d/summary.json`: 正式 `dist/win-x64/crer.exe` によりcontrol/template/seedの12群、14回の公開CLI再生が成功。画像fixtureはBCL生成PNGと専用loopback serverを使用する。追加の `.crer/control-integration/13c4bdcf93eb4ac4ac9074d268daecbb/summary.json` ではscroll専用1群が成功し、計13群。wheel ±175、座標350,350と実DOMの上下scroll状態を確認し、既存12群のassertは変更していない。
- `csharp/Crer.ActionTests/baseline-prng-vectors.json`: 固定旧refから60乱数と80座標を採取。normal三角関数の許容差は1e-12、今回の実差は0。disabled jitter・16回再試行fallbackの乱数消費も比較した。
- `.crer/schema-parity/whatwg-hosts-20261015/`: Schema173チェックに加え、固定旧refと172 scenario/planのaccept/reject・全モデル値を比較し差分0。WHATWG host境界37入力も差分0。受理モデルはobject key順だけを正規化し、数値・型・default・配列順を比較した。拒否理由の文言はこのモデル比較の完全一致対象には含めず、不正入力の公開code2を別のCLIテストで確認する。
- `.crer/fixture/{bfdd35887994464ba74a0cf50c6f9bb9,0d8db0b994804f62bd6889a9c4e41d0d,a3befd5cfd534cf1b9ec0b76fa3768f0,79c04cdc4e9e4267b69266798cd7dc35,e0111dde47d94900861f2ed8272f2660,d354d298186a45519874e2b446e37573}/parity-summary.json`: click/search/keyboard/drag/ordinary failure/continue planは同一YAMLのhash、code、failures、seed、各stepの意味が一致した。
- `.crer/integration/1e61c0394d/summary.json`: 初回main request遮断・reuse中のpattern交換/無効化・ECMAScript regexが成功。`.crer/integration/6a6aef7cdc/summary.json`: foreground always/once・opacity・watchdog・reuse/restoreが成功。`.crer/integration/d49c4a7f02/summary.json`: profile別並列reuseとworker timeout分離が成功。
- `.crer/integration/7cca61d387/summary.json`: 正式 `dist/win-x64/crer.exe` と隣接DLLで14群中12成功、1失敗、1判定不能。成功はplanのPID/HWND/session再利用・timeout分離・fail_fast code4とdrain、URL初回遮断・交換・解除・V8 regex、rawのTab/2種Space、profile排他・実junction拒否、play/recordのDPI、strict/advisory/off/ignore表示検査、standalone always/once・opacity・復元。recordは実DLLをduration1500msで停止し、`record-dpi.ndjson.meta.json`の物理/CSS双方600×400・比率1を実測した。これは実入力手動記録の代替には数えず、手動受入は別のmanual-record証跡を用いる。
- 同じ `7cca61d387` のreuse once overlay失敗と背景入力観測inconclusiveを保持する。同時間帯に別の実機harnessが並行していたため、全担当のCfT操作を停止した専用時間帯に同じ正式exeで再検証し、`.crer/integration/bd9c6c1d6a/summary.json` はstandalone/reuse両群2成功・失敗0となった。overlayの生観測は `overlay-once*.json` に保持する。これで元の失敗ログを書き換えない。
- `.crer/integration/d1d2ea710d/summary.json`: 背景の読取専用追加観測は1判定不能。`background-attribution.json`は128 sample中owned CfT前景52を記録し、cursor/foregroundとも変化した。別担当の明示foreground比較と時間帯が重なった当時の観測を成功へ読み替えず保持する。その後の独占観測・非activate起動修正・公開exe再受入は下に別記する。
- `.crer/integration/fb185c6fac/summary.json`: cleanupエラー伝播修正時の公開exe（SHA-256 `01af70f88e2826f4b79a683b9b879dcfc2250b6603c5ce79409e216bccc60965`）を専用時間帯で再検証し、plan2群・Windows2群の4成功、失敗0、背景観測1判定不能。`background-attribution.json`では物理カーソル全128sample不変、foregroundだけ05:33:21.185～05:33:22.506 UTCの約1.3秒、owned CfT PID47100へ移り元PID20676に復元した。この起動時前景化は後述の製品修正と公開exe再受入で解消した。元の判定不能logは保持する。
- `.crer/integration/startup-c868dac257b94203b2fdfc6f87cf2767/summary.json` と `startup-7c04e2236c064ec7a2f1877efe1f4c07/summary.json`: BCL診断probeで `CreateProcessW` の `STARTF_USESHOWWINDOW` + `SW_SHOWNOACTIVATE(4)` / `SW_SHOWNA(8)` を比較した。両指定で20ms観測の前景/カーソル変化0、visibleな単一app pageを維持。後者は通常のCDP window bounds/content size設定後にも前景変化0、standalone=true/CSS600×400、CDP mouseクリック1とkey入力 `a` をDOM読取assertで確認した。製品への配線と公開exeでの再受入は別に扱う。
- `.crer/integration/7a094c9915/summary.json`: 非activate launchを組み込んだ正式公開exeで背景再生1成功・失敗0・判定不能0。`background-attribution.json`は127sampleすべてcursor/foreground変化0・owned CfT前景0。通常Chrome既存64PIDはテスト終了時に全生存し、入力や通常profileへの接続は行わない。その後、同じ配布物で `.crer/integration/fd5b1c92c5/summary.json` のplan2群/Windows2群が4成功・失敗0となり、background起動後のreuseで明示foregroundへ切り替わる経路も確認した。CLI/DLL hashは `7a094c9915/release-hashes.json` に記録する。
- 上の短い背景再生と、その後の通常Chrome全subprocess観測は区別する。数分後の `7a094c9915/ordinary-chrome-after.json` では通常Chromeのsubprocess増減（既存1終了/追加6）があり、plan/windows終端でも既存6PID終了を観測した。この後続観測を「全PID変化0」とは報告しない。sandboxではpathを読めなかった追加snapshotも `ordinary-chrome-after.sandbox-unreadable.json` に残し、通常デスクトップの読取結果と区別する。
- `.crer/fresh-no-js/d3c7db142ae6429b8dfc44b24e3dbfd3/`: source `2301c974`（製品 `9205492`、非activate起動 `fa8996c`、window gate `9205492`）の新規checkoutからPATHをdotnet/PowerShell/System32/Windowsに限定。`runtime-path.json`でdeno/node/bun不在、`verify.log`で390件・12CLI test projects+native成功、`build-release.log`でx64 Native AOT警告0を確認した。同じPATHの `test-release.log` はsearch/keyboard-submit/drag/continue plan/session reuseの標準5fixtureすべて成功。`record-lifecycle.log` はtimed-record/normalize/validate/stop-file/missing-dllの5ケース（正常code0、不足DLL code3）成功。集約は `release-summary.json`。
- `.crer/migration-final-20261015/distribution.json`: 上記新規checkoutの成果物とbit同一で正式 `dist/win-x64` へ配置。Native AOT CLIは8,390,656 bytes、SHA-256 `6d45ebc3eef838542751a5fd6803a80f0d824e670074a8311e1a4cc06337fcd7`。DLLは1,730,560 bytes、SHA-256 `e7a47e0c620ba16e45a2ad41f13a3e95cc717523b867b151e190c469d8f8c747`、x64・ABI2。
- `.crer/fixture/d428455c1fda47a2a2a266498c82b6ee/parity-summary.json` は明示failのprefixを含む厳密比較成功、`.crer/fixture/064e3f60bebf4a02864ac4002866a3d7/parity-summary.json` はsession reuse成功planの2scenario各6step/seed42/failures一致、`.crer/fixture/4b1b6a2c1a1f4dedbcd1c361e3dbf214/parity-summary.json` はcontinue plan一致。continueの初回は旧Denoだけviewport884×661の起動raceでcode3となり、失敗runを保護したまま同じYAML/assertで再試行して成功した。

- `.crer/csharp-errors/8ffa27781bca4531967c85eb98338399/summary.json`: 公開AOTの異常14ケース成功。CLI/YAML code2は起動せず、early Chrome exit42/startup timeout/CDP lossはcode3、assert code4、押下中Ctrl+Cはcode5。全所有process/profile/pending解放、mouseMoved→mousePressed→mouseReleasedの3列を確認した。
- `.crer/csharp-errors/4eb2fac2d4dc4b6283b9847d102efb58/summary.json`: 上記14ケースを最終CLI/DLL・JS runtime不在PATHでも再受入し全成功。code2/3/4/5とno-stack診断、pending0、所有CfT終了、一時profile削除、押下中Ctrl+Cの解放列を再確認した。
- `.crer/migration-final-20261015/manual-final-summary.json`: 保存済みの実手動rawを最終公開exeだけで再normalize→validate→asserted playし、`Clicks: 1` の状態を確認してcode0・pending0・所有CfT終了。JS runtime不在PATHで実行した。元rawのSHA-256 `23140e16426e4c88a2f125809f20c860f81b9369a0e73274de60591592fe64b6` は変更せず、元の手動record証跡と合わせて記録→再生を確認した。合成fixtureを手動入力の代わりに数えない。

## 互換性判断

- `browser install`、`record new/resume`、`run --max-parallel` は文書例のみで旧 parser にないため追加しない。
- `browser.chrome` と `playback.speed` は旧 validator が受理するが実行で参照しない既存契約。移植でも意味を新設しない。
- `navigation_ms` は旧validatorが受理するが旧runtimeが参照しないため、移植でも個別navigation期限を新設しない。CfT endpoint起動は固定15秒、action_msは各操作の期限とする。
- `drag` の個別 jitter は旧 schema が許可しない。click/double_click の step 設定のみ完全上書きする。
- 旧 `saveYaml` の空 steps 出力は null として再読込を拒否する既存バグ。
  C# では `steps: []` と保存する修正を明示し、空録画の往復回帰テストを設ける。
- ARM64 は利用者の追加指示で今回の対象外。過去のlinker不足やpublish未達を成功として読み替えない。
- 応答を失ったdownでも入力が届いた可能性があるため、C# InputGuardはawait前から追跡し保守的にreleaseする。release失敗は元例外を保ちつつcleanupErrorsへ記録し、続行を止めて環境code3とする。
- artifactsの文書契約に合わせ成功log行へmessageを追加し、環境/割込み失敗もstep行へ記録する診断改善を明示する。旧成功log.message欠落だけを任意parityで差分として報告し、入力text値は保存しない。
- failure PNGの取得失敗は主失敗を置換しない。template失敗は最後に評価したメモリ内PNGを保持し、異なる状態の再撮影で置換しない。
- session cleanupの診断書込失敗をplan成功として返さない。全資源のcleanupを最後まで試した後、残ったcleanupErrorsを環境code3へ伝播する。診断IO失敗後のprofile lock再取得をFoundationで検証し、plan集約の回帰テストを追加した。
- plan fixture移植時にBCL serverが全応答へ追加していた `Cache-Control: no-store` は、元Deno.serveの `/seen` 応答には存在しなかった。この差でbody未消費fetchの完了イベントが遅れ、旧/C#双方が同じnetwork idle timeoutになった。元Deno.serveを変更せず再実行して成功を確認し、`.crer/network-headers/`の実HTTP比較に基づきBCLの `/seen` だけ同じheaderへ修正した。body `ok` と全元assertは維持した。製品側でContent-Lengthから完了を推測したり、network idleの意味を変更したりしていない。
- 通常background再生の受入で、CfT起動時の約1.3秒のactivationを独占観測した。`CreateProcessW` の `STARTF_USESHOWWINDOW/SW_SHOWNOACTIVATE` で所有CfTをvisibleのまま起動し、明示foregroundは既存WindowControllerで適用する修正を行った。stdin/stdout/stderr、環境、所有handleとprocess cleanupは専用launcherの13件の回帰テストで検証した。既存のbackground非干渉契約を満たすための修正で、旧観測の失敗や判定不能は消さない。

## 最終受入10条件の確認

| 条件 | 結果 | 確認内容・主な証跡 |
| --- | --- | --- |
| 1. 全公開機能の移植 | 完了 | この対応表の全コマンド・option・環境変数・YAML設定・stepにC#実装と検証先がある。文書だけの旧未実装構文と未使用設定は互換性判断に分離した。 |
| 2. 旧testの意味・境界・取消・解放 | 完了 | 旧29ファイル108項目は未移植0、証跡参照切れ0。最終新規checkoutの `verify.log` は12CLI project計390件とnative lifecycle成功。 |
| 3. 同じYAMLの旧比較と実CfT実測 | 完了 | 固定旧refとclick/search/keyboard/drag/failure/explicit fail/continue/plan/session reuseが一致。control13群、plan/URL/keyboard/profiles/display/Windowsの公開AOT実機と厳密な追試を確認した。 |
| 4. 異常と全資源の解放 | 完了 | 最終公開異常14ケース `4eb2fac2d4dc4b6283b9847d102efb58` が成功。worker timeout/fail_fast/profile排他/junction拒否は実機、overflow/hook/未確定入力はRecord/native単体を含め、code・pending0・入力解放・所有資源cleanupを確認した。 |
| 5. 既存raw比較と実手動記録の往復 | 完了 | normalize旧比較に加え `manual-record/7363420a8d6648f9b53206b59bf84aee` の利用者の実入力recordを保存。最終 `manual-final-summary.json` でraw hash不変・JS runtime不在のnormalize→validate→playと `Clicks: 1` assertが成功。 |
| 6. 背景非干渉と明示foreground | 完了 | 起動時activationを製品修正し、`7a094c9915` の127sampleでcursor/foreground変化0・owned CfT前景0。`fd5b1c92c5` はplan/Windows4群成功。外部操作や旧不一致は成功と区別して保持した。 |
| 7. 標準経路切替とJS runtime不在 | 完了 | 最終新規checkout `d3c7db142ae6429b8dfc44b24e3dbfd3` のruntime不在PATHでverify/build-release/標準test-release5fixture/record lifecycle5ケースが成功。README/仕様/移行/verify/配布補助はC#へ切替済み。 |
| 8. 最終x64 Native AOT配布 | 完了 | CLI/DLLともx64 Native AOT、警告0。新規checkoutとbit同一の正式 `dist/win-x64` を `distribution.json` のSHA/size・ABI2で固定。ARM64は利用者の明示指示で今回の対象外。 |
| 9. 製品・標準経路のJS runtime除去 | 完了 | 旧tracked TypeScript・Deno設定/lockを通常ツリーから退役。製品の外部起動は明示CfTのみ。依存・入力経路監査は `.crer/migration-audit-20261015/`、任意の旧比較だけ `scripts/compat` の固定refへ隔離した。 |
| 10. 再現・判断・結果・配布・環境の記録 | 完了 | [最終受入結果](csharp-migration-results.md) に再現コマンド、互換性判断、実装コミット、OS/.NET/CfT、元からの制約、配布先/hashを集約。この対応表とtest台帳を合わせて最終結果を固定した。 |

今回のwin-x64対象で未移植・未検証の受入項目はない。残るのは元からのIME・zoom等の制約と、利用者が対象外にしたARM64のpublish/実機検証である。過去の失敗・環境制約・判定不能の証跡も保持し、成功へ読み替えない。

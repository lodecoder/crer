# C# 完全移行の受入結果

対象は固定基準 `50b50fd8bf82a3ce09c25040bca51f60304ba126` の実装済み機能。
2026-10-15 の利用者の指示により win-x64 を対象とし、ARM64 の publish・実機検証は除外した。
この対象範囲の全受入を完了した。
要件ごとの実装先は [対応表](csharp-migration-matrix.md)、旧テスト108件の意味の移植先は
[テスト台帳](csharp-migration-tests.tsv)、挙動の判断は [互換性記録](csharp-cli-compatibility.md) を参照する。

## 実装と配布物

全公開コマンド、option、YAML 設定、atomic step、template と制御構文、plan/session reuse、
記録・正規化、診断・artifacts を C#/.NET 10 に移した。実行コードは CLI/schema、CDP/browser、
executor/actions、template、scheduler/session、record/normalize、artifacts に分離している。
native DLL は ABI 2 のまま unmanaged function pointer で呼び出す。

公開先は `C:\Users\a5\devai\crer\dist\win-x64`。

| ファイル | 形式 | SHA-256 |
| --- | --- | --- |
| `crer.exe` | win-x64 Native AOT、8,390,656 bytes | `6d45ebc3eef838542751a5fd6803a80f0d824e670074a8311e1a4cc06337fcd7` |
| `crer-win-input.dll` | win-x64 Native AOT、ABI 2 | `e7a47e0c620ba16e45a2ad41f13a3e95cc717523b867b151e190c469d8f8c747` |

製品ソースの最終修正は `9205492`、実機harnessを含むsnapshotは `2301c97`。新規 checkout 相当の隔離ソースから標準 build-release で生成し、
同じ bytes を公開先へ配置した。配布物は .NET SDK・共有 runtime を要求しない。
hash・PE machine・doctor 診断は `.crer/migration-final-20261015/` に保存した。

環境は Windows x64 / OS build `10.0.26200`、.NET SDK `10.0.400` / runtime・AOT toolchain `10.0.11`、
CfT `152.0.7977.42`。CfT の SHA-256 は
`b3b221d637b5b6a745c51cac1d22a618e7da5e67202be52695999886720c7d56`。

## 検証結果と証跡

証跡はこのワークスペースの `.crer` に保持する。初回失敗・環境制約・追試も削除していない。

| 対象 | 結果 | 主な証跡 |
| --- | --- | --- |
| 標準 verify | C# 390件、native lifecycle 成功。CLI build 警告0 | `.crer/fresh-no-js/d3c7db142ae6429b8dfc44b24e3dbfd3/verify.log` |
| x64 Native AOT | CLI と DLL の clean publish 成功、警告0 | 同ディレクトリの `build-release.log` |
| JS runtime を除外した新規 checkout | PATH を dotnet / PowerShell / Windows のみに限定し verify・build 成功 | 同 `source.json`、`runtime-path.json` |
| 標準 test-release | 同じ制限PATHでsearch/keyboard/drag/continue plan/session reuse成功、カーソル不変 | 同 `test-release.log`、`release-summary.json` |
| 既定値の記録補助 | 正式exe・横DLL・Release serverでduration/normalize/validate/stop-file/missing-DLLの5ケース成功 | 同 `record-lifecycle.log`、checkout下 `.crer/csharp-record/dcf8280d190b47a683907aa8c54aa9d3/summary.json` |
| 全 schema / YAML | C# 173群、旧形式との値・受理/拒否比較、tag/alias/merge/URL/seed 境界成功 | `.crer/schema-parity/whatwg-hosts-20261015/`、`whatwg-20261015/` |
| seed / atomic input | 旧乱数60値・座標82点の実差0、CDP入力列・曖昧なdownの解放成功 | `Crer.ActionTests` と固定 baseline vectors |
| template / 制御構文 | 実機12群成功、複数一致・cache・関数・反復・break・PNG保存・seed再現 | `.crer/control-integration/6f8e4f111bfc41b59a6841519ce7505d/summary.json` |
| scroll | 実CDP wheelの上下入力、DOM scroll位置と状態のassert成功 | `.crer/control-integration/13c4bdcf93eb4ac4ac9074d268daecbb/summary.json` |
| plan / reuse / URL / display / keyboard / profiles | 正式AOTの機能別実機検証成功。PID/HWND/target/session、worker timeout分離、排他とjunction拒否を含む | `.crer/integration/7cca61d387/summary.json`、下記追試 |
| Windows制御 | standalone と reuse、always/once、opacity、watchdog、終了時復元成功 | `.crer/integration/bd9c6c1d6a/summary.json` |
| 非前景起動の修正後 | 起動から終了まで127sampleで物理カーソル・前景変化0、owned CfT前景0 | `.crer/integration/7a094c9915/summary.json`、`background-attribution.json` |
| 起動処理変更後のplan/Windows | 同じ正式AOTで4群成功、全pending・process・profile後始末を確認 | `.crer/integration/fd5b1c92c5/summary.json` |
| 異常系 | 最終配布物で14ケース成功。code2/3/4/5、起動終了42・timeout・CDP切断・押下中Ctrl+C | `.crer/csharp-errors/4eb2fac2d4dc4b6283b9847d102efb58/summary.json` |
| 入力と資源の解放 | mouseMoved→mousePressed→mouseReleased、pending0、所有CfT終了、一時profile削除を実測 | 同 `interrupt-evidence.json`、各 run の `cleanup.json` |
| normalize | 旧raw/sidecar 14入力のsteps・metadata・警告比較成功、生成YAMLを旧validatorでも確認 | `.crer/normalize-migration-parity-20261015/summary.json` |
| 手動記録 | 利用者がmarkerとIncrementを実操作。公開exeだけのrecord→normalize→validate→play、Clicks:1 assert成功 | `.crer/manual-record/7363420a8d6648f9b53206b59bf84aee/summary.json` |
| 最終配布物での手動raw再受入 | 同じ実手動rawのhashを保ち、JS runtime不在PATHで再normalize/validate/play・Clicks:1成功 | `.crer/migration-final-20261015/manual-final-summary.json` |
| hook / overflow | native lifecycle と RecordTests24件でoverflow・停止timeout・不完全metadata・再開始を検証 | 標準 verify、`Crer.RecordTests` |
| CfT導入 | Node不要の公式JSON解決・実download・再実行保持・NoPersistを含む11件成功 | `.crer/cft-installer/002a53bd3b0c43e3938c7dc43dfb577b/summary.json` |
| 依存・入力経路監査 | 製品の外部起動は明示CfTのみ。通常scriptのJS runtime呼出し、tracked TS、OS入力注入は0 | `.crer/migration-audit-20261015/` |

単体件数は Action34 / BackgroundProcess13 / Cdp19 / Executor11 / Foundation20 / Normalize38 / Plan21 /
Record24 / Schema173 / Template9 / 既存基盤18 / UrlBlocker10 の合計390。
単に件数を合わせず、旧108テストの各 assertion の意味を台帳で照合した。

## 旧実装との同一入力比較

固定 ref を `scripts/compat/Export-Legacy.ps1` で取り出し、YAMLを変更せず旧CLIと公開AOTで再生した。
可変日時だけを除いてcode・failures・seed・step indexと制御・入力座標を比較し、YAML hashも検査した。
成功 log.message の追加だけは明示した診断改善として別記する。

| 入力 | 一致した証跡（`.crer/fixture/<id>/parity-summary.json`） |
| --- | --- |
| click-only | `bfdd35887994464ba74a0cf50c6f9bb9` |
| search | `0d8db0b994804f62bd6889a9c4e41d0d` |
| keyboard-submit | `a3befd5cfd534cf1b9ec0b76fa3768f0` |
| drag | `79c04cdc4e9e4267b69266798cd7dc35` |
| assertion failure | `e0111dde47d94900861f2ed8272f2660` |
| explicit fail | `d428455c1fda47a2a2a266498c82b6ee` |
| continue / 失敗を集約するplan | `4b1b6a2c1a1f4dedbcd1c361e3dbf214` |
| 成功plan / session reuse | `064e3f60bebf4a02864ac4002866a3d7` |

旧CLIだけのoccluded viewport raceも保存し、成功へ読み替えていない。
BCL fixture の余分な no-store header は元の Deno.serve と実HTTP応答を比較して修正した。
本文・YAML・assertion・製品のnetwork idle条件は維持した。

## 観測の区別と既存の制約

`.crer/integration/7cca61d387` の集計は12成功・1失敗・1判定不能。
once overlay の不一致を保存したうえで、専用デスクトップ枠の `bd9c6c1d6a` では
同じ厳密なassertionが2群とも成功した。カーソル・前景の変化はrawのPID/HWND/座標とともに保持し、
原因を外部操作と断定したり非干渉成功へ読み替えたりしない。
専用slotで外部操作なしの起動時約1.3秒の前景化を再現したため、製品側の課題として修正した。
非foreground再生はC#からWindowsの非activate表示を指定して起動し、同じapp windowを表示する。
標準入出力の明示handle継承、Unicode引数/環境、即時終了/失敗時回収、混在起動の排他を13群で検証した。
公開exeの `7a094c9915` ではカーソル・前景の変化0となり、起動後のbounds変更・CDP入力も維持した。
診断の `restorePerformed: false` は復元不要の意味で、OSの復元APIを呼んだ成功と区別する。

IME確定文字列、通常Chromeとの接続、任意倍率のstrict zoom、別target/worker/WebSocketのURL遮断は
元から保証しない。browser install、record new/resume、run --max-parallelは旧CLI未実装のため新設しない。
browser.chrome・playback.speed・navigation_msの旧runtimeで未使用だった意味も新設しない。
ARM64 は今回の対象外であり、x64成功をARM64成功と表現しない。

## 再現

```powershell
.\scripts\verify.ps1
.\scripts\test-release.ps1 -Chrome $env:CRER_CHROME
dotnet run --project csharp/Crer.IntegrationTests -c Release -- --exe dist/win-x64/crer.exe --chrome "$env:CRER_CHROME" --dll dist/win-x64/crer-win-input.dll --windows
dotnet run --project csharp/Crer.ControlIntegrationTests -c Release -- --exe dist/win-x64/crer.exe --chrome "$env:CRER_CHROME"
.\scripts\test-csharp-record.ps1
```

前景・重なり順を検査する間は別のCfT検証を同時起動しない。手動記録の手順と個別の選択実行は
[実機手順](manual-test.md) を参照する。標準経路は .NET と Windows build tools のみを使用する。
旧TypeScript・Deno設定・lockは通常ツリーから退役し、比較だけを任意の `scripts/compat` に隔離した。

実装コミットは `05c5cd5`、標準経路切替は `a0b55dd`、終了失敗の伝播修正は `1942ad0`。
非前景起動は `fa8996c`、混在起動のhandle継承保護は `9205492`、実機harnessは `2301c97`。
push・PR作成・merge は行わない。

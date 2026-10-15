# C#/.NET 10 CLI

既存 Deno CLI を残したまま、Windows 用 C# CLI を `csharp/Crer.Cli` に追加している。
公開実行ファイル名は `crer.exe`。C# の再生と fixture server は Deno / Node.js / Bun や
PowerShell に処理を委譲しない。PowerShell はビルド・検証用 harness にのみ使う。
着手時の判断と既存文書との差は [互換性調査](csharp-cli-compatibility.md) に残している。

## 対応範囲

| 対象               | 現在の C# CLI                                                                                                                                              |
| ------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| コマンド           | 引数なし / `help`、`validate <file.crer.yaml>`、`play <file.crer.yaml> [options]`                                                                          |
| play のオプション  | `--chrome <exe>`、`--keep-artifacts`、`--seed <uint64>`、`--position <left,top>`、`--step-delay-ms <ms>`、`--mute-audio`                                   |
| browser            | http/https の `initial_url`、`profile: ephemeral` または省略、任意文字列の `chrome`                                                                        |
| window             | `bounds: { left, top, width?, height? }`、`content: { width, height }`、`viewport: { width, height }`                                                      |
| display            | `expected_dpr`、`browser_zoom`、`zoom_check: strict`。再生は DPR 1・zoom 100・visual scale 1 が必要                                                        |
| playback           | `seed`、`step_delay_ms`、`jitter: { enabled: false }`、`timeouts.navigation_ms` / `action_ms`、`artifacts.template_screenshots`、abort のみの `on_failure` |
| click              | CSS viewport 座標 `at: { x, y }`、単独クリック反復 `count`、押下待機 `hold_ms`、終了後待機 `delay_ms`、無効な jitter                                       |
| wait_for           | `url`、`state: complete / visible / network_idle`、`locator_hint: { role, name, text }`、`delay_ms`                                                        |
| assert             | `url`、`state: complete`、`locator_hint: { role, name, text }`、`delay_ms`                                                                                 |
| screenshot / sleep | 安全な `name` と任意の `delay_ms` / 非負の `ms`                                                                                                            |

CLI の引数順は `crer play <file> [options]`。ファイルを option より前に置き、値は別引数で渡す。
重複 option、値不足、未知の option / YAML キー、未対応の step / 設定は起動前に終了コード 2 で拒否する。
`validate` はこの C# 対応範囲の検査であり、plan は受理しない。

Chrome は `--chrome` を `CRER_CHROME` より優先する。YAML の `browser.chrome` は既存形式との
互換キーであり、実行パスの選択には使わない。通常 Chrome の探索や既存ブラウザへの接続は行わない。
相対的な scenario / Chrome パスと `.crer/runs` は呼び出し時の作業ディレクトリを基準にする。
`--position` は YAML bounds の位置だけを上書きし、寸法を保持する。
`--step-delay-ms` は YAML の step_delay_ms を上書きする追加待機である。

`playback.seed` は uint64 範囲の十進**文字列**で、CLI seed、YAML seed、暗号学的乱数の順に選択する。
今段階の jitter は無効のみ。click.count は各回が clickCount 1 の単独クリックで、各回に hold_ms、
delay_ms、全体の step_delay_ms を適用する。sleep に delay_ms は付けない。
action_ms の既定値は 10000、step_delay_ms は 0、起動待機 navigation_ms は 10000。
navigation_ms の 0 は起動待機を即時 timeout させる値で、無制限待機の指定ではない。

window.content は要求するブラウザ内容領域、window.viewport はスクロールバーなどを除いた座標系。
viewport を省略した旧形式は content を期待値とする。DPR / zoom / viewport が一致しない場合は
環境エラーで停止する。display の省略時も C# は DPR 1 と scale 1 を検査する。
advisory / off と `--ignore-viewport-mismatch` は未対応。

locator は明示的に `[role]` 属性がある要素だけを探索する。role / name / text は完全一致で、
name は aria-label または trim した textContent、text は trim した textContent。
要素の矩形と visibility も確認する。fixture では button に `role="button"`、結果に
`role="status" aria-label="Result"` を明示する。DOM 読み取りは検証に使用し、操作は
CDP mouseMoved → mousePressed → mouseReleased で送る。OS の物理入力は注入しない。

## ビルド・テスト・publish

リポジトリのルートを作業ディレクトリにする。.NET 10 SDK が必要で、Native AOT publish には
Visual Studio Build Tools の C++ ツールと Windows SDK も必要になる。通常の build は次のとおり。

```powershell
dotnet build csharp/Crer.Cli -c Release
dotnet run --project csharp/Crer.Tests -c Release
dotnet run --project csharp/Crer.CdpTests -c Release
dotnet build csharp/Crer.FixtureServer -c Release
dotnet build csharp/Crer.StartupStub -c Release
dotnet build csharp/Crer.InterruptHelper -c Release
```

Crer.Tests は CLI / YAML の対応範囲と型・値を検査する。Crer.CdpTests は BCL の loopback
WebSocket server を使い、応答の逆順、切断、キャンセル、timeout、Dispose、不正 JSON で
保留要求が残らないことを検証する。Chrome は不要。

既存 Deno パーサーとの型判定比較も再実行できる。

```powershell
.\scripts\test-csharp-yaml-parity.ps1
```

36 ケースで数値・文字列・明示タグ・日付・merge alias を比較する。
ロック済みの `@std/yaml` 1.2.0 に合わせ、`012` は 10、`0o52` / `_42` / `42_` は文字列、
クォートのない日付は文字列として受理しない。明示的な `!!int` の判定も既存パーサーに合わせる。
結果は `.crer/csharp-yaml-parity/<id>/summary.json` に保存する。

Native AOT の出力先は `dist/csharp/win-x64` とし、既存 Deno の配布物から分離する。

```powershell
# VS の検出に vswhere.exe が必要な環境では Installer ディレクトリを PATH に追加する。
$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
.\scripts\build-csharp.ps1

# 同じ publish を直接実行する場合
dotnet publish csharp/Crer.Cli -c Release -r win-x64 --self-contained true -p:PublishAot=true -o dist/csharp/win-x64
.\dist\csharp\win-x64\crer.exe help
```

YAML は YamlDotNet の RepresentationModel から明示的に値を構築し、反射によるオブジェクト
deserialization を使わない。JSON は JsonNode と source-generated context を使い、
reflection serialization を無効にした構成で build / publish する。
2026-10-15 の現時点で win-x64 Native AOT publish は警告 0 で成功している。
実機での受入結果は末尾の検証記録に別途記載する。

AOT ツールチェーンを使えない環境で縦断検証を進める場合は、別ディレクトリへ通常の
self-contained publish を行い、その結果と AOT の障害を区別して記録する。

```powershell
dotnet publish csharp/Crer.Cli -c Release -r win-x64 --self-contained true -p:PublishAot=false -o dist/csharp-self-contained/win-x64
```

## validate・play・同一 fixture の比較

明示的な Chrome for Testing と、ボタンを押すと `Clicks: 0` から `Clicks: 1` に変わる
`fixtures/playback/click-only.html` を使う。server は別ターミナルで起動しておく。

```powershell
dotnet run --project csharp/Crer.FixtureServer -c Release -- --port 8080 --root fixtures/playback
```

別ターミナルで開発用 CLI、publish 済み exe、既存 Deno CLI に同じ YAML を渡す。
fixture 内の jitter は明示的に無効。search.crer.yaml は text なども使うため、この milestone の
受入 fixture にはしない。

```powershell
$env:CRER_CHROME = 'C:\path\to\chrome-for-testing\chrome.exe'

dotnet run --project csharp/Crer.Cli -c Release -- validate fixtures/playback/click-only.crer.yaml
dotnet run --project csharp/Crer.Cli -c Release -- play fixtures/playback/click-only.crer.yaml --chrome $env:CRER_CHROME --keep-artifacts

.\dist\csharp\win-x64\crer.exe validate fixtures/playback/click-only.crer.yaml
.\dist\csharp\win-x64\crer.exe play fixtures/playback/click-only.crer.yaml --chrome $env:CRER_CHROME --keep-artifacts

deno task dev validate fixtures/playback/click-only.crer.yaml
deno task dev play fixtures/playback/click-only.crer.yaml --chrome $env:CRER_CHROME --keep-artifacts
```

play の標準出力は JSON の code / failures / runDir、診断は標準エラー。CLI / YAML / 未指定の
Chrome などの起動前エラーは標準エラーに診断を出す。未処理例外の stack は表示しない。
終了コードは成功 0、CLI/YAML 2、環境 3、step/assert 4、中断 5。
各実行の exit code が 0、failures が空、steps.ndjson の assert が成功、before.png と
result.png にクリック前後の状態が保存されることを比較する。

## 比較 harness

手動で起動した fixture server を終了し、8080 番ポートを空けてから実行する。
この harness は C# server を起動し、終了時に自身で停止する。

```powershell
.\scripts\test-csharp-fixture.ps1 -Chrome $env:CRER_CHROME
```

既定の server exe は Debug build の配置を使う。Release build の場合は
`-FixtureServerExe csharp/Crer.FixtureServer/bin/Release/net10.0/Crer.FixtureServer.exe` を指定する。
無効な CRER_CHROME と有効な --chrome の組み合わせ、環境変数だけの選択、
profile 保持・削除と同一 YAML の Deno 実行を検査する。
1 秒の control と 20 ms 間隔の GetCursorPos で物理カーソルを読み取り、
移動があった場合は外部操作を排除できないため、再生成功とは別に判定不能を記録する。
検証結果は `.crer/csharp-fixture/<id>/summary.json` と cursor.json に残る。

Codex の制限されたプロセス環境では server が起動していても Chrome から接続できないことがあった。
実機検証では server と CLI を同じデスクトップ・ネットワーク環境で起動する。

## 異常経路 harness

前述の fixture server を起動した状態で実行する。Crer.StartupStub と Crer.InterruptHelper を
先に build しておく。新しいシナリオは `.crer/csharp-errors/<id>` に生成し、既存 fixture を変更しない。

```powershell
.\scripts\test-csharp-errors.ps1 -CrerExe .\dist\csharp\win-x64\crer.exe -Chrome $env:CRER_CHROME
```

環境変数未指定、不正 CLI、不正 YAML、未対応設定・step、Chrome 不在、assert 失敗、
起動 timeout、CDP 切断、長いクリック押下中の Ctrl+C を検証する。各再生で終了コードと
JSON 結果、pending CDP requests 0、自分の CfT の終了、一時 profile の削除、cleanup エラーなしを確認する。
assert 失敗時は failure-0.png も要求する。

Ctrl+C helper は対象 CLI の専用 console を非表示で作成し、その console だけに Ctrl+C を送る。
helper 自身の AttachConsole / FreeConsole が呼び出し元 PowerShell の console に作用することはない。
送信前には対象 run の CDP endpoint から button の :active を読み、実際の押下中であることを確認する。
CDP 切断テストで終了させるのも run.json の chromePid と専用 profile、実行パスを確認した
その実行の CfT だけである。summary.json、ログ、interrupt-evidence.json を evidence として保持する。

既存 Deno / native の回帰確認は次で行う。native 側の変更がなければその旨も結果に記載する。

```powershell
deno task verify
dotnet run --project native/tests -c Release
```

## artifacts と制約

各 run は cwd の `.crer/runs/<run-id>` に作る。run.json は scenario、実効 seed、Chrome の
パス・SHA-256・PID、profile、終了 code / failures を含む。steps.ndjson、display.json、
launch.json、cleanup.json、chrome-stderr.log と明示・失敗時 PNG で実行を追跡する。
`--keep-artifacts` を省略してもログ・PNG は残り、一時 profile だけを削除する。
指定した場合は profile も保持する。

未移植は record / normalize、doctor / inspect、plan / session reuse、永続 profile、template 照合、
有効な jitter、continue の failure policy、foreground / opacity、URL 遮断、text / key / scroll /
drag / double_click / navigate、functions と制御構文。未対応キーを黙って無視することはない。
template_screenshots は既存 YAML の値を検証・記録するが、template action 自体はまだ使えない。
Chrome の座標入力は viewport に依存するため、不一致を無視して成功させる option は提供しない。

## 検証記録

- Native AOT: win-x64 publish 成功、警告 0（2026-10-15）。
- CDP 統合テスト: loopback WebSocket で 8 件成功。
- CLI / YAML 単体テスト: 18 群成功。Deno との YAML 比較: 36 ケースすべて一致。
  `.crer/csharp-yaml-parity/b2b028afcaa04a1685e63902ecfeb079/summary.json` に記録。
- 公開した Native AOT exe と Deno に同じ click-only.crer.yaml を書き換えずに渡し、
  両方で Clicks: 0 → 1、2 つの assert、before.png / result.png、終了コード 0 を確認。
- 比較 harness は 3 ケース成功。--chrome が無効な環境変数より優先されること、
  CRER_CHROME 単独での選択、--keep-artifacts の有無による profile 保持・削除を検証。
  `.crer/csharp-fixture/a020126a6f2d42f491a79f8718766c43/summary.json` に記録。
- 最終比較の 3 ケースで物理カーソルは control / 再生中とも変化なしを観測。
  以前の比較で移動があった観測は外部操作と切り分けられず、元の記録に判定不能として残した。
  C# runtime に SendInput / SetCursorPos や DOM による再生の代替はない。
- 異常 harness は 13 ケース成功。60 秒 sleep 中の CDP 切断でも直ちに code 3、
  実際の mousePressed を観測してからの Ctrl+C で code 5 と mouseReleased を確認。
  `.crer/csharp-errors/4162a1af80de40a38652e46112e7af64/summary.json` と
  interrupt-evidence.json に記録。CfT 終了、pending 0、profile 削除、cleanup エラーなし。
- `deno task verify`: 102 passed / 0 failed / 6 ignored（Chrome 用 opt-in テスト）。
  `dotnet run --project native/tests/Crer.WinInput.NativeTests.csproj --no-restore` は成功。
  native のソースと既存 Deno 実装は変更していない。

検証環境は Windows 11、.NET SDK 10.0.400 / runtime 10.0.11、
Chrome for Testing 152.0.7977.42。最終 AOT exe は `dist/csharp/win-x64/crer.exe`。
C# と Deno の各 run の完全なパスは比較 summary.json から参照できる。

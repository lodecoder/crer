# C#/.NET 10 CLI

製品 CLI は `src/Crer.Cli`、Windows 入力・ウィンドウ操作は `src/Crer.Windows` に実装する。
CLI から Windows 実装を通常の `ProjectReference` で参照し、公開名は単一の `crer.exe` とする。
再生入力、YAML、plan、記録・正規化、診断、artifacts は C# で処理する。
実行時の .NET SDK / 共有 runtime は不要。PowerShell はビルド・CfT 導入・検証の補助に使う。
今回の受入対象は利用者の追加指示により **win-x64**。ARM64 は対象外。

実装と実機受入は区別する。[移行対応表](csharp-migration-matrix.md) と
[テスト台帳](csharp-migration-tests.tsv) は 2026-10-15 の Deno → C# 移行時の証跡であり、
その時点の DLL 構成やパスを含む。[互換性調査](csharp-cli-compatibility.md) には現行との差分も記録する。

## ビルドと検証

Windows、.NET 10 SDK、Visual Studio の C++ x64 build tools と Windows SDK が必要。
リポジトリルートから実行する。

```powershell
.\scripts\verify.ps1 -Configuration Release
.\scripts\build-release.ps1
.\dist\win-x64\crer.exe help
.\dist\win-x64\crer.exe doctor --chrome $env:CRER_CHROME
.\scripts\test-release.ps1 -Chrome $env:CRER_CHROME
```

`build-release` は Windows 操作を含む単一の Native AOT `crer.exe` を `dist/win-x64` に配置する。
入力記録と前景・不透明度の制御も同じ exe で処理し、DLL の別ビルド・配置は不要。
旧 `--dll` option は廃止し、指定すると未知 option として拒否する。`CRER_INPUT_DLL` は参照しない。
`doctor` から DLL 探索結果の `ffi` / `ffiExists` を削除した。

`build-csharp.ps1` の既定出力先は Native AOT ビルドでは `dist/<Runtime>`、
開発用の `-NoAot` では `dist/development/<Runtime>` とする。これにより標準配布先の AOT exe を
開発用ビルドで上書きしない。`-OutputDirectory` を明示した場合は、その指定先を使う。

`verify.ps1` は `tests/` の C# テストプロジェクトを検出し、Windows 入力の lifecycle も実行する。
実機用 IntegrationTests は別途実行する。個別の再実行例:

```powershell
dotnet run --project tests/Crer.SchemaTests -c Release
dotnet run --project tests/Crer.ActionTests -c Release
dotnet run --project tests/Crer.NormalizeTests -c Release
dotnet run --project tests/Crer.PlanTests -c Release
dotnet run --project tests/Crer.WindowsTests -c Release
.\scripts\build-csharp.ps1
```

YamlDotNet は明示的なノード変換で使い、反射 deserialization を使わない。
JSON も reflection serialization を無効にする。
ECMAScript 正規表現の構文検証には Acornima を使う。URL 遮断の照合は同じ専用 CfT に所有する
非表示 target の V8 で行い、表示用 page は一つに保つ。照合用 target も終了時に閉じる。
IANA time zone と Unicode のため globalization を有効にして AOT publish する。

2026-10-16 の構成整理では、`verify.ps1` の13テストプロジェクト（390件と Windows 入力の
直接 API／lifecycle テスト）、インストーラー／パッケージの13ケース、win-x64 Native AOT publish が成功した。
外部入力 DLL を同梱しない exe で、検索・キーボード・ドラッグ・plan・session reuse、時間指定／stop-file 記録と
normalize、前面化・透明度・watchdog・終了時復元を実 CfT で確認した。
カーソル不変の観測は外部操作により判定不能であり、機能テストの成功とは区別する。ARM64 は今回未検証。

## CLI 契約

```text
crer <doctor|inspect|validate|play|run|record|normalize> <file> [options]
```

file は command の直後。値付き option と値は別の引数とする。
未知・重複 option、値不足（次の引数が `--...` の場合も含む）を拒否する。
引数なしと `help` は help を表示する。doctor は file を取らない。

| command | option |
| --- | --- |
| doctor | `--chrome` |
| inspect / validate | なし |
| play | `--chrome --seed --step-delay-ms --profile-dir --template-screenshots --position`、`--keep-artifacts --ignore-viewport-mismatch --mute-audio` |
| run | `--chrome --profile-dir --plan-window-bounds-override --plan-window-opacity-override --template-screenshots`、`--ignore-viewport-mismatch --mute-audio` |
| record | `--chrome --profile-dir --url --content-size --position --duration-ms --stop-file --name` |
| normalize | `--output --url --name --client-origin --client-size --viewport` |

Chrome は `--chrome` > `CRER_CHROME`。通常 Chrome の探索・既存接続は行わない。
`browser.chrome` は旧形式の型検査専用キー。
相対ファイル・Chrome・profile・artifacts は cwd 基準、plan の scenario は plan 所在基準、
template は scenario 所在基準。doctor の manifest は `CRER_CHROME_MANIFEST`、cwd、Chrome ancestor の順。

`play --position` は YAML bounds の位置だけを上書きする。
`run --plan-window-bounds-override` は bounds を完全に置換する。
不透明度と template screenshot policy の run option は全 leaf に適用する。
`--keep-artifacts` は一時 profile の保持を指定し、ログ・画像は指定なしでも保存する。
永続 profile は `.crer/profiles` 配下に制限し、排他使用する。`..` やリンクによる逸脱を拒否する。

## 再生と記録

全 atomic step、template、if / repeat / repeat_until / for_each_template / break、
functions / params / args / call の schema と実行系を移植している。
乱数は splitmix64 / xoshiro256**。実効 seed は uint64 の十進文字列として保存する。
操作は対象 page session の CDP Input だけで送り、DOM の click・値代入や物理入力注入は使わない。
曖昧な down 応答も保守的に解放し、解放できなければ環境エラーとして停止する。

plan は共通の同時実行上限、profile ごとの排他、協調停止を持つ。
same-profile reuse では URL・window・display・遮断を再適用し、plan の最後に全 CfT を閉じる。
初回・再利用時のページ遷移と初期文書の準備待ちは、起動処理全体の期限（既定60秒）を共有する。
scenario の `playback.timeouts.startup_ms` で変更できる。値は0〜2147483647の整数ミリ秒で、
`0` は無制限。利用者の中断と plan の `worker_ms` はこの設定にかかわらず有効で、
準備完了後の step 実行には起動期限を適用しない。既存の `navigation_ms` は引き続き受理のみとする。
並列起動時などに応答が10秒を超えても待機し、利用者の中断や plan の worker timeout には従う。
CfT は空の app ウィンドウで起動し、位置・content size・不透明度・前景設定を適用してからページへ遷移する。
再利用時も同じ順序で設定するため、ページの最初のスクリプトから指定の content size を参照できる。
重なったウィンドウで描画フレームが停止しないよう `--disable-backgrounding-occluded-windows` を指定する。
起動の各段階、経過時間、設定した期限、通信の開始・応答・終了・失敗件数は `startup.json` に記録し、
タイムアウト時も失敗画像の取得を試みる。`network.documentRequests` / `documentResponses` は
iframe を含む文書要求の件数で、URL・ヘッダー・本文は追加の診断に保存しない。
`network.ndjson` には応答の HTTP 状態と安全な `net::ERR_*` コードも記録する。
要求開始後に文書応答が0件のまま `navigation` で停止した場合は、ページ応答を待っている状態を示す。
DNS・TLS・サーバーのいずれが原因かは、追加の調査で確認する。
viewport は `window.viewport ?? window.content` と実測比較する。
occluded renderer のリサイズが遅延した場合は、読み取り専用キャプチャ後に再測定する。
一致条件は緩和しない。foreground always / once と opacity は独立して制御する。

record は `Crer.Windows` の C# API を直接呼び、Raw Input / Low Level Hook で物理操作を観測する。
marker の手動クリックで較正し、Enter / Ctrl+C / duration / stop-file / CfT 終了で停止する。
古い sidecar、requested_content 由来の DPI scale、event ごとの viewport、QPC 間隔、
layout 由来の文字、Space の既定操作を保持する。IME 確定文字列は元から対象外。

```powershell
.\dist\win-x64\crer.exe record .crer\sample.ndjson --chrome $env:CRER_CHROME --url https://example.test/
.\dist\win-x64\crer.exe normalize .crer\sample.ndjson --url https://example.test/ --output recorded.crer.yaml
.\dist\win-x64\crer.exe validate recorded.crer.yaml
.\dist\win-x64\crer.exe play recorded.crer.yaml --chrome $env:CRER_CHROME
```

## 実機 fixture と任意の旧比較

```powershell
.\scripts\test-playback-fixture.ps1
.\scripts\test-plan-fixture.ps1
.\scripts\test-session-reuse-fixture.ps1
.\scripts\test-csharp-record.ps1
.\scripts\test-csharp-manual-record.ps1
dotnet run --project tests/Crer.IntegrationTests -- --exe dist/win-x64/crer.exe --chrome $env:CRER_CHROME --windows
```

3並列での初回表示サイズ・背面ウィンドウの描画・10秒超の初期応答・同じ profile の再利用・起動中の worker timeout を確認するには、
上の IntegrationTests コマンドに `--only startup` を追加する。外部サイトや既存の利用者 profile は使わない。

fixture server は BCL 製。サーバーと CfT を同じデスクトップ・ネットワーク環境で動かす。
カーソル等が動いた観測は、外部操作を切り分けられない場合に判定不能として保存する。
手動受入は専用画面の marker と Increment を各1回実操作し、表示された stop-file を作成する。
その後 normalize → validate → assert 付きの別 YAML の play と cleanup を検証する。

旧実装は固定 Git ref `50b50fd8bf82a3ce09c25040bca51f60304ba126` から
`scripts/compat/Export-Legacy.ps1` で比較専用の別フォルダへ取り出せる。
任意の比較だけが Deno を使用し、製品と標準検証の依存にはしない。

終了コードは成功 0、CLI/YAML 2、環境 3、step/assert 4、利用者中断 5。
ログ、診断 JSON、PNG、raw/sidecar と summary は `.crer` 配下に残す。
2026-10-15 の Deno → C# 移行時の win-x64 受入は完了。
[当時の結果と証跡](csharp-migration-results.md) は、単一 exe 化後の検証結果とは区別する。

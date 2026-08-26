# crer

Windows 上で Chrome for Testing を専用プロファイルで起動し、GUI ブラウザ操作を記録・再生する
Deno CLI です。再生は Chrome DevTools Protocol (CDP) の入力注入を使うため、OS の物理マウス・
キーボードや普段使いの Chrome を操作しません。

## 現在の実装範囲

- `.crer.yaml` の検証、CfT の隔離起動、headful な CDP 入力再生
- click / double-click / move / drag / scroll / text / key / key_chord / navigate / wait / assert / screenshot / sleep
- シード付きクリック揺らぎ、失敗 artifacts、YAML plan の直列・並列実行（`max_parallel` 対応）
- C# .NET 10 Native AOT の Raw Input DLL と NDJSON 記録

screen px から CSS viewport px への正確な変換、IME を含む text
正規化は次の実装段階です。

Raw Input だけではキーボード配列や IME の確定文字列を復元できないため、現在の `record` は英数字と基本
ショートカットのみを安全に正規化します。

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
deno task test
```

ローカル fixture による headful 再生と物理カーソル不変の確認は次で実行できます（実行中はマウスを動かさないでください）。
再生前の control 観測でもカーソルが動くデスクトップ環境では、スクリプトは判定不能として警告します。

```powershell
.\scripts\test-playback-fixture.ps1
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

CfT のコンテンツ領域で操作を記録し、`Ctrl+C` で停止します。

```powershell
deno task dev record .crer\raw-input.ndjson --url https://example.test
deno task dev normalize .crer\raw-input.ndjson `
  --url https://example.test `
  --output recorded.crer.yaml
```

ローカル fixture を使う手動 P0 テストは、次の補助スクリプトで一つの PowerShell から実行できます。

```powershell
.\scripts\record-playback-fixture.ps1
```

Chrome for Testing が fixture を開いたら、まずページ左上のマゼンタ色の 8×8 CSS px 点をクリックして
座標を較正します。点が消えた後に検索欄への入力と Submit のクリックを行い、記録元の PowerShell で
Enter を押します。較正クリック自体は記録されません。結果は `.crer/fixture.raw-input.ndjson` に保存されます。
同スクリプトは続けて `.crer/fixture.recorded.crer.yaml` も生成します。`-Output`、`-Scenario`、`-Port`、
`-Chrome` で変更できます。記録時に有効なコンテンツ領域と CDP viewport を取得できた場合、`.meta.json`
sidecar が作成され、`normalize` はこれを使って CSS 座標へ自動変換し、同時に記録時の CSS viewport を
`browser.window.content` として YAML に保存します。sidecar がない場合は、従来どおり
`--client-origin`、`--client-size`、`--viewport` をすべて指定してください。

`record --duration-ms 500` は、実入力をせずに DLL の起動・停止を確認する smoke test です。

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

実行 artifacts は `.crer/runs/<run-id>` に出力されます。

- 仕様書: [docs/specification.md](docs/specification.md)
- 手動結合テスト: [docs/manual-test.md](docs/manual-test.md)
- Raw Input bridge: [native/README.md](native/README.md)

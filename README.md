# crer

Windows 上で Chrome for Testing を専用プロファイルで起動し、GUI ブラウザ操作を記録・再生する
Deno CLI です。再生は Chrome DevTools Protocol (CDP) の入力注入を使うため、OS の物理マウス・
キーボードや普段使いの Chrome を操作しません。

## 現在の実装範囲

- `.crer.yaml` の検証、CfT の隔離起動、headful な CDP 入力再生
- click / double-click / move / scroll / text / key / navigate / wait / screenshot / sleep
- シード付きクリック揺らぎ、失敗 artifacts、YAML plan の直列・並列実行
- C# .NET 10 Native AOT の Raw Input DLL と NDJSON 記録

`drag` / `assert` / `key_chord`、screen px から CSS viewport px への正確な変換、IME を含む text
正規化は次の実装段階です。

## 実行

Deno 2.8 以降と Chrome for Testing を用意し、Chrome 実行ファイルを `CRER_CHROME` に設定するか
`--chrome` で渡します。

```powershell
$env:CRER_CHROME = 'C:\\path\\to\\chrome.exe'
deno task dev validate scenario.crer.yaml
deno task dev play scenario.crer.yaml --keep-artifacts
deno task dev run nightly.crer.plan.yaml
deno task test
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

Chrome for Testing が fixture を開いたら、検索欄への入力と Submit のクリックを行い、記録元の
PowerShell で `Ctrl+C` を押します。結果は `.crer/fixture.raw-input.ndjson` に保存されます。
`-Output`、`-Port`、`-Chrome` で変更できます。記録時に有効なコンテンツ領域と CDP viewport を取得できた
場合、`.meta.json` sidecar が作成され、`normalize` はこれを使って CSS 座標へ自動変換します。sidecar が
ない場合は、従来どおり `--client-origin`、`--client-size`、`--viewport` をすべて指定してください。

`record --duration-ms 500` は、実入力をせずに DLL の起動・停止を確認する smoke test です。

実行には Deno の `-A` を使いますが、配布版は同梱 DLL のみに限定した FFI 権限を要求する予定です。
実行 artifacts は `.crer/runs/<run-id>` に出力されます。

- 仕様書: [docs/specification.md](docs/specification.md)
- Raw Input bridge: [native/README.md](native/README.md)

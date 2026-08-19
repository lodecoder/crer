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

ローカル fixture を使う手動 P0 テストでは、別の PowerShell で次を起動します。

```powershell
.\scripts\serve-playback-fixture.ps1
```

次に `http://127.0.0.1:8080/index.html` を `record --url` に渡して、検索欄への入力と Submit の
クリックを記録します。現段階では CSS 座標の client origin／size／viewport を `normalize` へ明示的に
渡します。

`record --duration-ms 500` は、実入力をせずに DLL の起動・停止を確認する smoke test です。

実行には Deno の `-A` を使いますが、配布版は同梱 DLL のみに限定した FFI 権限を要求する予定です。
実行 artifacts は `.crer/runs/<run-id>` に出力されます。

- 仕様書: [docs/specification.md](docs/specification.md)
- Raw Input bridge: [native/README.md](native/README.md)

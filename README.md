# crer

Windows 上で Chrome for Testing を専用プロファイルで起動し、GUI ブラウザ操作を記録・再生する
Deno CLI です。再生は Chrome DevTools Protocol (CDP) の入力注入を使うため、OS の物理マウス・
キーボードや普段使いの Chrome を操作しません。

## 現在の実装範囲

- `.crer.yaml` の検証、CfT の隔離起動、headful な CDP 入力再生
- click / double-click / move / scroll / text / key / navigate / wait / screenshot / sleep
- シード付きクリック揺らぎ、失敗 artifacts、YAML plan の直列・並列実行
- `record` 用の C ABI と CMake DLL スタブ

Raw Input を実際に採取する DLL 本体と、`drag` / `assert` / `key_chord` は次の実装段階です。
`record` は DLL スタブしかない状態では明示的に失敗します。

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

実行には Deno の `-A` を使いますが、配布版は同梱 DLL のみに限定した FFI 権限を要求する予定です。
実行 artifacts は `.crer/runs/<run-id>` に出力されます。

- 仕様書: [docs/specification.md](docs/specification.md)
- Raw Input bridge: [native/README.md](native/README.md)
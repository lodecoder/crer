# Node.js 24 LTS への移行記録

## 起点と保全した履歴

移行の起点は Deno 実装のコミット `c11ed0d2ba6b025665fa6305bf724c84abafa84e` とし、
移行作業は `codex/nodejs-migration` ブランチで行う。

移行開始時の C# 版 `main` はコミット `7abed305667f8c6ec0a8b02168d64b16627da076` であり、
同じコミットを `codex/archive-csharp-main-20261016` ブランチにも保存した。
`main` の履歴を巻き戻さず、移行前の実装を参照・再利用できる状態で保持する。

## 実装方針

- CLI とシナリオ実行処理は既存 TypeScript を維持し、Node.js 24 LTS へ移す。
- Deno のファイル・子プロセス・環境変数・シグナル API を Node.js 標準 API へ置き換える。
- 開発実行は `tsx`、型検査は TypeScript、テストは `node:test`、依存管理は npm を使う。
- YAML と検証用 assertion は既存の標準ライブラリを npm パッケージ経由で使う。
- C# .NET 10 Native AOT の `crer-win-input.dll` と C ABI バージョン 2 を維持し、
  Koffi から呼び出す。記録、HWND、最前面属性、不透明度の既存処理を再利用する。
- 配布用 CLI は esbuild でまとめ、Node.js SEA に組み込む。`crer.exe`、
  `crer-win-input.dll`、`node_modules/koffi`、
  `node_modules/@koromix/koffi-win32-<arch>` を含む出力ディレクトリ全体を配布する。
- Chrome for Testing の隔離プロファイル、CDP 入力再生、artifact 形式と終了コードを維持する。

開発・ビルド・検証コマンドは [README](../README.md) と
[手動結合テスト手順](manual-test.md) を参照する。

## 検証記録

以下は 2026-10-16 に Windows x64 上で実行した結果。通常のテストでスキップする実 CfT テストは、
`CRER_TEST_CHROME` と `CRER_TEST_WINDOWS=1` を指定して別途実行する。

| 対象                 | コマンドまたは確認項目                                                 | 結果・証跡                                                                                                                                                                                                                |
| -------------------- | ---------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 実行環境             | Windows / Node.js 24 / CfT のバージョン                                | Windows x64、配布用 Node.js 24.21.0、CfT 152.0.7977.42。ソース結合テストでは Node.js 24.19.0 も確認                                                                                                                       |
| 静的検査・単体テスト | `npm run verify`                                                       | 型検査・lint・整形確認・114 件成功。実 CfT 6 件は通常実行ではスキップ                                                                                                                                                     |
| Native AOT のビルド  | `scripts/build-native.ps1`                                             | 成功。既存 DLL の C# ソースと ABI は変更していない                                                                                                                                                                        |
| ネイティブ状態管理   | `npm run test:native`、DLL の連続 start / stop                         | 成功。overflow、hook 初期化失敗、停止 timeout の managed fault test と実 DLL の連続記録開始・停止を確認                                                                                                                   |
| CLI                  | help、doctor、validate、inspect、不正オプションの終了コード            | 成功。配布版 doctor は Node.js 24.21.0、同梱 DLL、CfT と manifest の SHA-256 一致を確認                                                                                                                                   |
| 実 CfT の結合テスト  | DPI、キー操作、並列 session 再利用、URL ブロック、最前面属性、不透明度 | 最終コードで 6 件成功、失敗・スキップ 0。DPI 192、awareness 2 の環境で確認                                                                                                                                                |
| 記録の終了処理       | DLL 起動・停止、時間指定、stop file、異常時の後始末                    | 配布版 7 件成功。時間 0 / 500ms、stop-file、不正時間、normalize → validate、NDJSON / metadata と Chrome 終了を確認。`.crer/nodejs-migration/recording/2026-10-16T11-16-16-498Z-ba73660e/evidence.json`                    |
| fixture 再生         | 成功・失敗 artifact、plan 継続、session 再利用、物理カーソル           | 配布版 search は終了 0、failure は終了 4、診断・画像を確認。失敗後の plan 継続と 2 scenario の session 再利用が成功。失敗 fixture でカーソル不変を確認。成功 fixture は再生前にも外部操作でカーソルが動き、分離判定は不能 |
| 配布物               | SEA のビルド、リポジトリ外からの起動、同梱 DLL の読み込み、実 CfT 再生 | `scripts/build-release.ps1` 成功。`dist/win-x64` に配布物を生成。リポジトリ外・Node.js 不在の PATH でも help / doctor / validate が成功                                                                                   |

録画終了の検証は次で再現できる。物理入力を発生させず、時間指定と stop-file の終了経路を確認する。

```powershell
npm run test:record -- --chrome $env:CRER_CHROME
```

移行時には Chrome 起動中の `about:blank` を完成したページと誤認する競合が見つかったため、
空の data 文書で起動し、CDP の準備後に目的 URL へ明示的に遷移するようにした。
また、stderr の終了待ち中に変わったプロセス終了状態を再取得し、終了待ちのタイマーを解放する。
`record --duration-ms` の不正値は Chrome 起動前に拒否し、`0` でも停止タイマーを設定する。

SEA の埋め込みでは元の Node.js バイナリの署名に関する postject の警告が出る。
生成物の起動は Windows 上で確認しているが、crer 独自のコード署名は付与していない。

配布版の最終 fixture 証跡は `.crer/runs/` の次のディレクトリに保存した。

- `20261016T112307592Z-playback-fixture-search-5119c797-67cd-4513-a954-1f359ad78b2e`
- `20261016T112315262Z-playback-fixture-failure-c5c59459-4434-4968-96f2-195fc948f185`
- `20261016T112320554Z-playback-fixture-keyboard-submit-3564c405-9fba-4322-98d6-b9c1309e27dd`
- `20261016T112327838Z-playback-fixture-session-reuse-37061d1c-5ee6-4c05-8aec-e9f2c5a68309`
- `20261016T112329917Z-playback-fixture-session-reuse-f881d80c-66de-4a56-bfed-4395a13a1237`

## アーキテクチャの制約

Windows x64 と ARM64 の Native AOT ビルド設定を維持する。
SEA のビルドは、対象と同じアーキテクチャの Windows 上で Node.js 24 を実行して行う。
たとえば `-Runtime win-arm64` は ARM64 版 Node.js 24 を使う ARM64 Windows ホストが必要であり、
x64 ホストからのクロスビルドは行わない。

ARM64 のビルドと実機での回帰テストは未検証とする。x64 の確認結果を ARM64 の動作保証には使わない。
日本語 IME の確定文字列など、元の実装で手動編集が必要な操作の正規化は今回の移行範囲に含めない。

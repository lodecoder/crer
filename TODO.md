# crer 実装ロードマップ

仕様の正本は [docs/specification.md](docs/specification.md) とする。このファイルは実装の優先順位、
完了条件、現在の差分を追跡する。個別作業は GitHub Issues で管理する。

## 現在の到達点

- Deno CLI、YAML の基本検証、CfT の隔離起動、CDP 再生の骨格を実装済み。
- click / double-click / move / scroll / text / key / navigate / wait / screenshot / sleep を実装済み。
- seed・jitter・artifacts の基本実装と、C# .NET 10 Native AOT の Raw Input DLL を追加済み。
- Native AOT DLL は `win-x64` で公開ビルド済み。ただし実 CfT を通した結合テストは未実施。

## P0 — 実ブラウザ再生の結合テスト

- [ ] 固定した CfT バージョンを導入する手順を確立する。
- [ ] ローカル fixture ページに対し `play` を実行し、起動・ウィンドウ位置・入力・終了を確認する。
- [ ] OS のカーソル位置、前面ウィンドウ、通常 Chrome が変化しないことを確認する。
- [ ] 成功／失敗時の screenshot・run metadata・終了コードを自動テストする。

完了条件: Windows 11 上で headful CfT の再生を CI 非依存で再現でき、証跡を artifacts に残せること。

## P0 — record から YAML への正規化

- [ ] `crer record` から Native AOT DLL を起動し、`raw-input.ndjson` を取得する。
- [ ] Raw Input の mouse / wheel / key イベントを click・scroll・key・text の steps へ正規化する。
- [ ] screen px から CSS viewport px への座標変換と viewport 変化の検出を実装する。
- [ ] 停止時に有効な `.crer.yaml` を保存し、不完全な down/up を警告する。

完了条件: CfT コンテンツで手動記録した検索操作を YAML 化し、同じ専用 CfT で再生できること。

## P1 — シナリオ言語を仕様へ追随

- [ ] `drag`、`assert`、`key_chord` を実装する。
- [ ] `wait_for` の URL / locator hint / network idle を仕様どおり実装する。
- [ ] 個別 jitter、viewport / DPR / zoom の検証を厳密化する。

## P1 — plan scheduler

- [ ] `max_parallel`、`fail_fast`、worker timeout を実装する。
- [ ] scenario / plan の `on_failure` と終了コード集約を仕様どおり実装する。

## P2 — 配布と堅牢化

- [ ] `deno compile` と Native AOT DLL をパッケージ化する。
- [ ] CfT の固定バージョン導入、`doctor` の診断、サンプル scenario を整える。
- [ ] Windows x64 / ARM64 のビルドと回帰テストを整備する。
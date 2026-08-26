# crer 実装ロードマップ

仕様の正本は [docs/specification.md](docs/specification.md) とする。このファイルは実装の優先順位、
完了条件、現在の差分を追跡する。個別作業は GitHub Issues で管理する。

## 現在の到達点

- Deno CLI、YAML の基本検証、CfT の隔離起動、CDP 再生の骨格を実装済み。
- click / double-click / move / drag / scroll / text / key / key_chord / navigate / wait / assert / screenshot / sleep を実装済み。
- seed・jitter・artifacts の基本実装と、C# .NET 10 Native AOT の Raw Input DLL を追加済み。
- Native AOT DLL は `win-x64` で公開ビルド済み。実 CfT fixture の再生結合テストも成功済み。

## P0 — 実ブラウザ再生の結合テスト

- [x] 固定した CfT バージョンを導入する手順を確立する。
  - `install-chrome-for-testing.ps1 -Version <version>` と `crer-chrome.json` manifest を提供。
- [x] ローカル fixture ページに対し `play` を実行し、起動・ウィンドウ位置・入力・終了を確認する。
  - 2026-08-19: `fixtures/playback/search.crer.yaml` を CfT 152.0.7977.42 で再生し、終了コード 0 と `result.png` / `run.json` を確認。
- [ ] OS のカーソル位置、前面ウィンドウ、通常 Chrome が変化しないことを確認する。
- [x] 成功／失敗時の screenshot・run metadata・終了コードを自動テストする。
  - `scripts/test-playback-fixture.ps1` が成功時の `result.png`、失敗時の `failure-0.png`、両方の
    `run.json` / `display.json` と終了コードを検査する。

完了条件: Windows 11 上で headful CfT の再生を CI 非依存で再現でき、証跡を artifacts に残せること。

## P0 — record から YAML への正規化

- [x] `crer record` から Native AOT DLL を起動し、`raw-input.ndjson` を取得する。
- [x] Raw Input の mouse / wheel / key イベントを click・drag・scroll・key・text の steps へ正規化する。
- [x] screen px から CSS viewport px への座標変換を実装する。
  - 記録した content bounds と CDP viewport の sidecar を取得し、完全な metadata で自動変換する。手動 record → replay で確認済み。
  - 記録中の viewport 変化検出は未実装。
- [x] 停止時に有効な `.crer.yaml` を保存し、不完全な down/up を警告する。

完了条件: CfT コンテンツで手動記録した検索操作を YAML 化し、同じ専用 CfT で再生できること。**達成済み**。

## P1 — シナリオ言語を仕様へ追随

- [x] `drag`、`assert`、`key_chord` を実装する。
- [x] `wait_for` の URL / locator hint / network idle を仕様どおり実装する。
  - URL パターン、可視 locator、CDP Network イベントに基づく 500ms の idle 判定を実装済み。
- [x] 個別 jitter、viewport / DPR / zoom の検証を厳密化する。
  - jitter の必須項目・型・値域、`window.content` の実測 viewport、DPR、`visualViewport.scale` の実装は完了。
    `browser_zoom` は仕様どおり 100% だけを strict に保証する。2026-08-26 に headful CfT の
    `keyboard-submit` fixture で回帰確認済み。

## P1 — plan scheduler

- [x] `max_parallel`、`fail_fast`、worker timeout を実装する。
- [x] plan の `on_failure` を直列・並列ノードへ適用する。
- [x] scenario / plan の `on_failure` と終了コード集約を仕様どおり実装する。
  - 2026-08-26: `continue-after-failure` plan fixture で、失敗 artifact を残して後続 scenario を
    実行し、plan 全体が code 4 になることを headful CfT で確認済み。

## P2 — 配布と堅牢化

- [x] `deno compile` と Native AOT DLL をパッケージ化する。
  - `build-release.ps1`、standalone exe の DLL 自動検出、`test-release.ps1` を実装。2026-08-26 に
    win-x64 配布物の headful CfT fixture 再生、artifact、物理カーソル不変を確認済み。
- [ ] CfT の固定バージョン導入、`doctor` の診断、サンプル scenario を整える。
  - `doctor` に CfT 実行ファイル・バージョン・導入 manifest の診断を追加。固定版の再導入と manifest 確認が残る。
- [ ] Windows x64 / ARM64 のビルドと回帰テストを整備する。

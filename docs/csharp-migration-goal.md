# C# 移行の最初の `/goal`

作業ブランチ: `codex/csharp-migration`

以下のコードブロック全体を Codex に送信して開始する。この文書の作成だけでは Goal を開始しない。
初期段階の完了条件を定めたプロンプトであり、C# への全面移行は次の段階で扱う。

```text
/goal codex/csharp-migration ブランチで、crer の C#/.NET 10 CLI を実装し、既存の YAML 形式と CLI の呼び出し規約を維持したまま、Windows 上で「Chrome for Testing の隔離起動 → CDP 接続 → 座標クリックの再生 → 結果確認 → 終了処理」を実際に通してください。設計案やモックの成功だけで完了にせず、以下の受入条件を満たすまで実装と検証を進めてください。

着手時の確認:
- AGENTS.md、README.md、docs/specification.md と現在の差分を読み、作業ブランチを確認する。利用者の変更を保護する。
- src/cli.ts、src/main.ts、src/yaml.ts、src/types.ts、src/errors.ts、src/runtime.ts、src/cdp.ts、src/actions.ts と関連テストを、互換性の判断基準にする。文書と実装に差があれば調べて記録する。
- native/Crer.WinInput.csproj、native/Exports.cs、native/tests と既存 fixture を確認し、Windows 操作の実装資産を必要な範囲で再利用する。
- 最初に、この段階で対応するコマンド・オプション・YAML 設定・step と、未対応の機能を短い互換性一覧に記録する。

実装範囲:
1. 既存 Deno 実装と並行して開発できる C# CLI とテストを追加する。プロジェクトの配置は既存構成を見て決め、公開コマンド名は crer とする。
2. 最低限 help、validate、play を実装する。公開 exe は既存と同じ `crer.exe play <file.crer.yaml> --chrome <chrome.exe> --keep-artifacts` で呼べるようにし、開発用の dotnet run コマンドも記載する。publish 先は既存 Deno 配布物と分ける。
3. CRER_CHROME と --chrome の優先順位、引数順、ファイルパスの解決、標準出力・標準エラーの役割、play の JSON 結果の code / failures / runDir は現行と整合させる。--keep-artifacts の保存・削除条件も維持する。
4. version: 1 の browser、playback、steps と `do: click` / `at: { x, y }` など、既存の YAML キー・型・意味・既定値を変えない。実装範囲では現行 validator と同じ妥当性判定を行う。未知のキー、不正な値、重複オプション、値不足を検出する。未移植の設定・オプション・step は、黙って無視せず Chrome 起動前に非ゼロ終了と具体的な診断を返す。
5. 明示された Chrome for Testing を headful で起動する。専用の一時プロファイル、loopback の動的 CDP ポート、ページ target / session の選択は現行の分離方針を維持する。現行実装は空きポートを確保して数値で --remote-debugging-port に渡す。別案として Chrome に割り当てさせる場合は --remote-debugging-port=0 と DevToolsActivePort を組み合わせ、自分が起動したプロファイルの接続先だけを使う。通常 Chrome へのフォールバックや既存ブラウザへの接続は行わない。
6. CLI から C# の WebSocket / JSON 処理で CDP に接続する。クリックは CSS viewport 座標で Input.dispatchMouseEvent の mouseMoved → mousePressed → mouseReleased を送る。button、clickCount、座標、入力解放は現行 click の意味に合わせる。DOM の click()、dispatchEvent()、値の直接代入やフォーム送信で再生を代替しない。
7. ローカルでクリック結果を検証するために必要な wait_for、assert、screenshot の最小範囲も実装する。ページ状態の読み取りを検証に使うことはよい。browser.window と display の必要な設定を扱い、viewport / DPR / zoom の不一致を無視して成功にしない。初期 fixture では jitter を明示的に無効にする。
8. 成功・失敗・Ctrl+C・CDP 切断・起動タイムアウトの各経路で、保留中の CDP 要求を終了させ、入力解放とリソース解放を行う。自分が起動した CfT を終了し、一時プロファイルと artifacts は現行規約に従って扱う。
9. 終了コードは成功 0、CLI/YAML エラー 2、環境エラー 3、step/assert 失敗 4、中断 5 に合わせる。エラー経路で未処理例外のスタックを利用者にそのまま出さない。
10. Native AOT を想定した依存関係と設計を選び、JSON/YAML の AOT 互換性を早期に小さな publish で確認する。初期 milestone の必須条件は C# exe の publish と実動作であり、AOT が成立しない場合は通常の self-contained publish で縦断検証を完了し、AOT の障害を具体的に記録する。

検証と完了条件:
- 既存と同じ YAML 形式を使うクリック専用 scenario と、ボタンを押すとカウンターや status が変わるローカル HTML fixture を追加する。既存 wait_for / assert の探索に合わせ、ボタンに role="button"、結果に role="status" と aria-label="Result" を明示する。実在する fixtures/playback/search.crer.yaml は text も使うので、初期段階の必須条件にはしない。
- 同じクリック専用 YAML を、書き換えずに既存 Deno CLI と新しい C# CLI の両方で実行し、両方が期待したページ状態になることを検証する。両実装が同じ fixture を通った記録を残す。
- 新しい C# の実行経路と新規 fixture サーバーは、Deno / Node.js / Bun や、それらのプロセスへの処理委譲に依存させない。既存実装の比較・回帰確認のために Deno を使うことはよい。
- C# の build、意味のある単体・結合テスト、win-x64 向け publish が成功する。公開した exe で実際の CfT を起動し、CDP 経由のクリックで fixture の状態が変わり、assert が成功して終了コード 0 になる。
- 同じテストで `--chrome` と CRER_CHROME の選択・優先順位を確認する。不正 YAML、不正 CLI 引数、存在しない Chrome、assert 失敗、中断、CDP 切断について、終了コード・診断・子プロセスの後始末を検証する。
- run.json、steps.ndjson、結果スクリーンショットなど、今回の対応範囲で既存規約に沿った artifacts を残し、クリック前後の状態と実行結果を追跡できるようにする。既存 scripts/test-playback-fixture.ps1 の -CrerExe を使う場合は、そのスクリプトが要求する artifacts と検証にも対応する。必要なら既存テストを壊さない専用 harness を追加する。
- 再生コードが OS の物理入力を注入しないことを確認する。物理カーソル位置の観測はしてよいが、SendInput / SetCursorPos 等で動かしてはならない。観測が外部操作の影響で判定不能なら、成功と区別して記録する。
- 既存 Deno 側の verify と、変更した場合の native テストを実行し、回帰を確認する。検証専用の PowerShell harness は使用してよいが、C# CLI のランタイム依存にはしない。
- README または専用の移行文書に、ビルド・publish・validate・play・fixture 検証の再現コマンド、対応範囲、未移植部分、AOT の確認結果を記載する。

境界と進め方:
- この Goal は最初の縦断実装までを対象とする。record / normalize、plan の直列・並列実行、session reuse、画像テンプレート照合、全 action の移植、既存配布経路の全面置換は次の段階に残す。
- 既存 Deno 実装、既存 YAML と CLI 契約を残す。受入を通すために既存 fixture や検証を弱めたり、未対応機能を成功扱いにしたりしない。
- ネイティブ実装は必要な部分だけ再利用する。UnmanagedCallersOnly 付き export を通常の C# 呼び出しとして扱わず、共有 core メソッドへの整理や既存 DLL の利用など、最小の変更で進める。DLL 境界の完全撤去はこの段階の完了条件にしない。
- 実装・検証・修正を繰り返し、得られた証拠から次の作業を決める。独立した作業は並列化し、同じファイルへの競合を避ける。
- Git 操作は AGENTS.md に従い、対応する Git MCP を優先する。コミットする場合は prefix を付け、prefix 以外は日本語で記述する。push、PR 作成、merge はこの Goal に含めない。
- 外部要因で検証ができない場合も、独立してできる実装と検証を進める。実機 E2E を実行できない状態を完了扱いにせず、試したこと・成功したチェック・残る条件・必要な環境を具体的に記録し、Goal の停止や状態変更は利用可能なツールの規則に従う。
- 完了報告では、対応した範囲、変更理由、実行したコマンドと結果、C# と Deno の比較結果、artifacts の場所、未移植機能、残る制約を簡潔に示す。
```

Goal の構成は [OpenAI Docs: Using Goals in Codex](https://developers.openai.com/cookbook/examples/codex/using_goals_in_codex)
の、成果・検証方法・制約・対象範囲を明示する考え方を参考にしている。

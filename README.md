# crer

Windows 上で **Chrome for Testing** を専用プロファイルで起動し、実際に行った
マウス・キーボード操作をテキストとして記録・編集・再生する CLI ツールの仕様です。

再生は Chrome DevTools Protocol (CDP) の入力注入で行うため、OS のマウスカーソル、
キーボードフォーカス、普段使いの Chrome プロセスには触れません。

- 仕様書: [docs/specification.md](docs/specification.md)
- 対象: Windows 11 以降、Chrome for Testing、Node.js 22 LTS

現時点では設計仕様のみで、実装は含みません。

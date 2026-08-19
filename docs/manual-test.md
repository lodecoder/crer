# CRER 手動結合テスト手順

Windows 11 上で PowerShell を開き、リポジトリのルートへ移動して実行する。

```powershell
cd C:\Users\a5\devai\crer
$env:CRER_CHROME = 'C:\path\to\chrome.exe'
dotnet publish native\Crer.WinInput.csproj -c Release -r win-x64
deno task dev doctor
```

`doctor` で `chromeExists: true` と `ffiExists: true` を確認する。以下のテスト中は普段使いの
Chrome を操作しない。CRER は `CRER_CHROME` または `--chrome` を明示しない限り起動しない。

## 1. 成功再生とカーソル

マウスを動かさず、次を実行する。

```powershell
.\scripts\test-playback-fixture.ps1
```

期待結果:

- CfT が表示され、fixture の Search に `crer` が入力され Submit が押される。
- 終了コードは 0。
- `.crer\runs\<run-id>` に `run.json`、`display.json`、`result.png` がある。
- `PASS` が表示される。デスクトップ環境自体がカーソルを動かす場合は `INCONCLUSIVE` 警告でもよい。

## 2. 失敗 artifacts

```powershell
.\scripts\test-playback-fixture.ps1 `
  -Scenario fixtures\playback\failure.crer.yaml `
  -ExpectedExitCode 4
```

期待結果:

- 実行は終了コード 4 を確認して完了する。
- 最新 `.crer\runs\<run-id>` に `failure-0.png`、`run.json`、`display.json` がある。
- CfT ウィンドウが終了する。

## 3. 手動 record → YAML → replay

```powershell
.\scripts\record-playback-fixture.ps1
```

CfT の fixture が開いたら次を行う。

1. Search 欄をクリックする。
2. `Crer42` と入力する。
3. Submit をクリックする。
4. 記録元の PowerShell で `Ctrl+C` を一度押す。

期待結果:

- `.crer\fixture.raw-input.ndjson` が作成される。
- `.crer\fixture.recorded.crer.yaml` が作成される。
- 有効な content bounds を取得できた場合は `.crer\fixture.raw-input.ndjson.meta.json` も作成される。
- bounds 警告が出た場合、YAML の座標は物理 screen px の可能性があるため replay は行わず、その警告文を報告する。

metadata が作成され、bounds 警告がなければ再生する。

```powershell
deno task dev play .crer\fixture.recorded.crer.yaml --chrome $env:CRER_CHROME --keep-artifacts
```

報告してほしい情報:

- 各コマンドの出力全体
- 成功／失敗の終了コード
- `.crer\runs\<run-id>` のファイル名一覧
- 手動 record 時に表示された警告

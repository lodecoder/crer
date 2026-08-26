# CRER 手動結合テスト手順

Windows 11 上で PowerShell を開き、リポジトリのルートへ移動して実行する。

```powershell
cd C:\Users\a5\devai\crer
$env:CRER_CHROME = 'C:\path\to\chrome.exe'
.\scripts\build-native.ps1
deno task dev doctor
```

`build-native.ps1` は Visual Studio 2026 の `vswhere.exe` をそのビルド処理だけ PATH に追加する。
同じ PowerShell で直接 `dotnet publish` を実行する必要がある場合は、先に次を実行する。

```powershell
$env:Path = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:Path"
dotnet publish native\Crer.WinInput.csproj -c Release -r win-x64
```

`doctor` で `chrome.exists: true` と `ffiExists: true` を確認する。`chrome.manifest` がある場合は
`matchesChrome: true` と `isChromeForTesting: true` も確認する。manifest がない場合、CfT かどうかは
パスだけでは確定できないため `isChromeForTesting: "unverified"` となる。以下のテスト中は普段使いの
Chrome を操作しない。CRER は `CRER_CHROME` または `--chrome` を明示しない限り起動しない。
CRER は CfT を `devicePixelRatio: 1` および翻訳ポップアップ無効で起動するため、Windows の表示倍率を
変更する必要はない。

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

この補助スクリプトは、指定した raw NDJSON・metadata・生成 YAML を開始時に削除して、新しい記録だけを
出力する。残したい記録には `-Output` と `-Scenario` で別のパスを指定する。

CfT の fixture が開いたら次を行う。

1. ページ左上に表示されるマゼンタ色の 8×8 CSS px 点をクリックする。点が消え、以後の操作の記録が始まる。
2. Search 欄をクリックする。
3. `Crer42` と入力する。
4. Submit をクリックする。
5. 記録元の PowerShell に戻り、Enter を一度押す。

Windows の `Ctrl+C` は PowerShell の子プロセスを強制終了して後続の normalize を実行できないことがある。
このスクリプトは Enter を受けると stop file を作成し、record に graceful close を依頼してから normalize する。

期待結果:

- `.crer\fixture.raw-input.ndjson` が作成される。
- `.crer\fixture.recorded.crer.yaml` が作成される。
- 有効な content bounds を取得でき、較正点をクリックした場合は `.crer\fixture.raw-input.ndjson.meta.json` に `marker_calibration` が作成される。
- bounds 警告が出た場合、YAML の座標は物理 screen px の可能性があるため replay は行わず、その警告文を報告する。

metadata が作成され、bounds 警告がなければ再生する。記録時の操作間隔は YAML の `sleep` ステップに
自動保存され、通常の再生ではその間隔を再現する。

```powershell
deno task dev play .crer\fixture.recorded.crer.yaml --chrome $env:CRER_CHROME --keep-artifacts
```

Tab で Submit にフォーカスして Enter で実行するキー再生は、次の fixture で確認できる。

```powershell
.\scripts\test-playback-fixture.ps1 -Scenario fixtures\playback\keyboard-submit.crer.yaml
```

失敗を記録したまま次の scenario を続行し、plan が code 4 に集約されることは次で確認できる。

```powershell
.\scripts\test-plan-fixture.ps1
```

目視確認でさらに待機したい場合は、記録済みの `sleep` に 1 秒を追加する。

```powershell
deno task dev play .crer\fixture.recorded.crer.yaml --chrome $env:CRER_CHROME --keep-artifacts --step-delay-ms 1000
```

## 5. standalone 配布物

Native AOT DLL を横に置いた standalone 実行ファイルを生成し、同じ fixture で再生する。

```powershell
.\scripts\test-release.ps1
```

期待結果:

- `dist\win-x64\crer.exe` と `dist\win-x64\crer-win-input.dll` が生成される。
- fixture 再生は終了コード 0 で終了し、`result.png` を含む artifact が作成される。
- `PASS: standalone distribution playback completed` が表示され、物理カーソル不変の判定も通る。

報告してほしい情報:

- 各コマンドの出力全体
- 成功／失敗の終了コード
- `.crer\runs\<run-id>` のファイル名一覧
- 手動 record 時に表示された警告

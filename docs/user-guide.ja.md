# Windows x64 版 crer の使い方

[English](user-guide.en.md)

GitHub Releases に Windows x64 用の配布物が公開されたら、以下の手順で利用できます。

Windows x64 と Windows PowerShell 5.1 または PowerShell 7 を使います。管理者権限、.NET、Deno、Node.js、Bun は不要です。Chrome for Testing（CfT）は別途導入します。

## インストール

[GitHub Releases](https://github.com/lodecoder/crer/releases) から `crer-win-x64.zip` と `SHA256SUMS` を同じフォルダへダウンロードします。PowerShell でそのフォルダを開き、ZIP のハッシュを `SHA256SUMS` の該当行と比較してください。英字の大文字・小文字は問いません。

```powershell
Get-FileHash .\crer-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS
```

一致を確認してから実行します。

```powershell
Unblock-File .\crer-win-x64.zip
Expand-Archive .\crer-win-x64.zip -DestinationPath .\crer-setup
& .\crer-setup\install.ps1
```

既定では `%LOCALAPPDATA%\Programs\crer` へ配置し、ユーザーの PATH に追加します。新しい PowerShell を開き、`crer help` を実行してください。

「スクリプトの実行が無効」と表示された場合は、同じ PowerShell で `Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned` を実行してから再試行します。この設定はその PowerShell を閉じると解除されます。

任意で `-InstallDirectory "$env:LOCALAPPDATA\Programs\crer-custom"` と `-NoPath` を指定できます。`-NoPath` の場合は `crer.exe` をフルパスで実行します。配置先を変更した場合は、以下の既定パスも置き換えてください。

## Chrome for Testing の設定

同梱の補助スクリプトで CfT を導入します。

```powershell
& "$env:LOCALAPPDATA\Programs\crer\install-chrome-for-testing.ps1" -InstallRoot "$env:LOCALAPPDATA\crer\browsers" -Version stable
```

ユーザー環境変数 `CRER_CHROME` と `CRER_CHROME_MANIFEST` が登録されます。新しい PowerShell を開いて確認します。

```powershell
crer doctor
```

Chrome が存在し CfT と認識され、`ffiExists` が `true` になっていることを確認します。`crer.exe` と `crer-win-input.dll` は同じフォルダに置いてください。crer は専用の CfT ウィンドウとプロファイルを使い、普段使いの Chrome は使いません。

既存の **CfT** を登録する場合は、同じ補助スクリプトに `-ChromePath 'C:\path\to\chrome.exe'` と `-InstallRoot "$env:LOCALAPPDATA\crer\browsers"` を指定します。manifest と両環境変数をまとめて更新できます。コマンド単位の `--chrome` は `CRER_CHROME` より優先しますが、この環境変数だけを書き換えても manifest は更新されません。同じ PowerShell に設定を反映するときは、補助スクリプトの dot-source も利用できます。

## 最初の再生

インストール先とは別の作業フォルダを用意します。

```powershell
$work = Join-Path $env:USERPROFILE 'crer-work'
New-Item -ItemType Directory -Force $work | Out-Null
Set-Location $work
```

次の内容を `first.crer.yaml` として保存してください。

```yaml
version: 1
name: first-run
browser:
  initial_url: https://example.com/
steps:
  - { do: wait_for, state: complete }
  - { do: screenshot, name: first-page }
```

```powershell
crer validate .\first.crer.yaml
crer play .\first.crer.yaml
```

ファイル名はコマンドの直後に置き、オプションと値は別の引数にします。plan は `run <plan-file>` で実行します。

## 記録して再生

記録ごとに新しい raw ファイル名を使ってください。既存の raw ファイルには追記されます。以下の2コマンドの URL は、記録するページに置き換えてください。

```powershell
crer record .\recording-01.ndjson --url https://example.com/
```

1. ページ左上のマゼンタ色の四角をクリックし、`Calibration complete` の表示を待ちます。
2. 専用の CfT ウィンドウ内で操作します。
3. 記録を開始した PowerShell に戻り、Enter を押して停止します。raw と隣の `.ndjson.meta.json` はセットで保存してください。

```powershell
crer normalize .\recording-01.ndjson --url https://example.com/ --output .\recorded.crer.yaml
crer validate .\recorded.crer.yaml
crer play .\recorded.crer.yaml
```

生成した YAML を確認してから再生します。IME の確定文字列や複雑なショートカットは手編集が必要な場合があります。再生は CfT への CDP 入力を使い、物理カーソルの移動や他のアプリへのキー入力は行いません。

Ctrl+C または専用 CfT を閉じることでも記録を停止できます。`--stop-file <path>` はそのファイルが存在すると停止するため、開始時には存在しないパスを使います。`--duration-ms 30000` はマーカー較正を行わない時間指定の記録です。Enter 停止が使えるのは、両停止オプションを付けない対話端末での実行時です。

## 保存先・更新・削除

ログ・診断・スクリーンショットは作業フォルダの `.crer\runs\<run-id>` に保存され、再生結果の `runDir` に表示されます。`crer inspect '<runDir>'` で確認できます。`--keep-artifacts` は一時ブラウザプロファイルを保持する指定で、ログ・画像は指定なしでも残ります。永続プロファイルは同じ作業フォルダの `.crer\profiles` 配下に置き、同時に共有できません。`browser.window.foreground: true` を指定したシナリオは前景と重なり順を変えます。表示不一致では診断を確認してから viewport 設定を調整してください。

更新時は crer と専用 CfT を終了し、新しい ZIP のハッシュを確認して新しいセットアップフォルダへ展開し、同じ配置オプションで installer を実行します。

削除前に YAML・raw・sidecar・作業フォルダをバックアップしてから実行します。

```powershell
& "$env:LOCALAPPDATA\Programs\crer\uninstall.ps1"
```

追跡対象のインストールファイルと installer が追加した PATH 項目を削除します。CfT、Chrome 関連の環境変数、`.crer` のプロファイル・artifacts を含む作業フォルダ、利用者が変更したインストールファイルは保持します。

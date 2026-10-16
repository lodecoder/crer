# Windows の実機受入

C#/.NET 10 SDK、Visual Studio Build Tools の MSVC と Windows SDK、明示した Chrome for Testing を用意する。
配布済み `crer.exe` の実行には .NET SDK・共有 runtime は不要。今回の対象は win-x64。
既存の通常 Chrome・profile を使わず、各 harness が所有する専用 CfT と `.crer` の検証ディレクトリを使う。

```powershell
.\scripts\build-release.ps1
.\dist\win-x64\crer.exe doctor --chrome $env:CRER_CHROME
.\scripts\verify.ps1
```

`doctor` の Chrome path/version/SHA-256/manifest と `ffiExists` を確認する。CfT 未指定時の自動探索は行わない。
manifest に登録する場合は `install-chrome-for-testing.ps1 -ChromePath <chrome.exe> -Version <version>` を使う。

## 再生と非干渉の観測

```powershell
.\scripts\test-playback-fixture.ps1
.\scripts\test-playback-fixture.ps1 -Scenario fixtures\playback\keyboard-submit.crer.yaml
.\scripts\test-playback-fixture.ps1 -Scenario fixtures\playback\drag.crer.yaml
.\scripts\test-plan-fixture.ps1
.\scripts\test-session-reuse-fixture.ps1
```

各 assertion、終了コード、PNG、run/display/cleanup 診断を検査する。通常の非 foreground 再生中は
マウスやキーボードに触れずに待つ。外部操作が入ったカーソル・前景観測は `INCONCLUSIVE` として残し、
再生の機能成功と分けて扱う。通常 Chrome の既存 PID を読み取り比較し、終了対象に含めない。

```powershell
.\scripts\test-playback-fixture.ps1 -Scenario fixtures\playback\cursor-idle.crer.yaml
.\scripts\test-playback-fixture.ps1 -Scenario fixtures\playback\failure.crer.yaml -ExpectedExitCode 4
.\scripts\test-playback-fixture.ps1 -Scenario fixtures\playback\explicit-failure.crer.yaml -ExpectedExitCode 4
```

明示 fail は continue 指定があっても停止し、`failure-0.png` と kind: explicit の診断を保存する。
後続の screenshot は生成されない。ephemeral profile は既定で削除し、診断・画像と persistent profile は保持する。

## 結合テスト

BCL の動的 loopback server と公開 exe を使う。`--only` を省略すると全グループを実行する。
`--windows` は前景化・重なり順を明示的に操作するテストなので、実行中は CfT の検証を待つ。

```powershell
dotnet run --project csharp/Crer.IntegrationTests -c Release -- `
  --exe dist/win-x64/crer.exe --chrome "$env:CRER_CHROME" `
  --dll dist/win-x64/crer-win-input.dll --windows

dotnet run --project csharp/Crer.ControlIntegrationTests -c Release -- `
  --exe dist/win-x64/crer.exe --chrome "$env:CRER_CHROME"
```

前者は plan の並列性・profile 分離・再利用・worker timeout・fail_fast、URL 遮断の初回/差替え/解除、
Tab と Space の旧記録、DPI・viewport、profile 排他、非干渉観測を検証する。
Windows グループは foreground always/once の優先順位、sleep 中の topmost watchdog、opacity と
再利用時の変更・解除、終了時の復元を実際の HWND で検証する。
選択例は `--only plan,blocking`、`--only keyboard,display,profiles`、`--only windows --windows`。
後者は template の単/複数一致、cache、missing、画像保存方針、条件・反復・break・関数、seed/jitter を検証する。

## 手動 record → normalize → validate → play

```powershell
.\scripts\record-playback-fixture.ps1
```

1. CfT 左上のマゼンタの較正点を一度クリックする。
2. 点が消えたら Search 欄をクリックし、`Crer42` を入力して Submit を押す。
3. 記録元の PowerShell に戻り Enter を一度押す。

このスクリプトは出力先の既存 raw/metadata/YAML を置き換えるため、保護したい記録には
`-Output` と `-Scenario` で新しいパスを指定する。record は物理操作の観測だけを行う。
stop file で graceful stop した後、metadata の較正・content/viewport と不完全入力の警告を確認する。
操作間隔は直前 action の `delay_ms` として保存される。

```powershell
.\dist\win-x64\crer.exe validate .crer\fixture.recorded.crer.yaml
.\dist\win-x64\crer.exe play .crer\fixture.recorded.crer.yaml --chrome $env:CRER_CHROME --keep-artifacts
```

`test-csharp-manual-record.ps1` は一意な保存先に受入用 counter を開く。
較正点、Increment を各一度クリックし、表示が `Clicks: 1` になったら、出力された stopFile を作成する。
公開 exe の normalize/validate/play と最終 `Clicks: 1` assertion、所有 CfT の終了まで検査する。
IME の確定文字列は元から観測対象外。mock や hook packet の成功を手動実測の代わりに数えない。

自動の停止経路・DLL 障害検査は `scripts/test-csharp-record.ps1`、hook の overflow/再開始は
`dotnet run --project native/tests -c Release` と `Crer.RecordTests` に分ける。

## 配布物

```powershell
.\scripts\test-release.ps1
```

`dist/win-x64/crer.exe` と同じフォルダの `crer-win-input.dll` を Native AOT でビルドし、公開配布物で
search/keyboard/drag/plan/session reuse を検証する。実行環境、再現コマンド、結果と証跡は
[C# 移行対応表](csharp-migration-matrix.md) に記録する。比較専用の旧実装は `scripts/compat` へ隔離する。

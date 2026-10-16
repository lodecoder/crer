# Windows x64 リリースの準備

公開用ファイルを準備する手順。GitHub Release の作成・タグ・push は自動では行わない。
ARM64 は今回の対象外。

## 配布物を作る

Windows PowerShell 5.1 または PowerShell 7 で、リポジトリルートから実行する。
ビルドには .NET 10 SDK、MSVC x64 build tools、Windows SDK が必要。

```powershell
.\scripts\verify.ps1
.\scripts\build-release.ps1
.\scripts\test-release.ps1 -Chrome $env:CRER_CHROME
$tag = Read-Host 'GitHub Release tag'
.\scripts\package-release.ps1 -Version $tag
```

`package-release.ps1` は既存の `dist/win-x64` を再ビルドせず、次を `dist/releases` に生成する。
同名ファイルがあると拒否するので、再作成時は `-OutputDirectory 'dist/releases/candidate-2'` のように別フォルダを使う。

- `crer-win-x64.zip`
- `SHA256SUMS`（ZIP の SHA-256）

ZIP 直下は Windows 操作を含む単一の `crer.exe`、`install.ps1`、`uninstall.ps1`、
`install-common.ps1`、CfT 導入補助2本、`README.en.md`、`README.ja.md`、`release.json`。
ルートに LICENSE があれば同梱する。CfT 本体、PDB、profile、検証 artifacts は含めない。
Windows 入力 DLL の別ビルド・同梱は不要。

GitHub Release のタグは `$tag` と一致させ、上記 ZIP と SHA256SUMS を添付する。
利用者は ZIP 全体を展開し、同梱 installer を実行する。利用者の .NET SDK は不要。

## インストールの検証

```powershell
.\scripts\test-install.ps1 -RegistryTests
powershell.exe -NoProfile -File .\scripts\test-install.ps1 -RegistryTests
```

配布 ZIP の作成から初回配置・更新・削除、変更済みファイルと未知ファイルの保持、
ロック時の rollback と削除再試行、日本語・空白のパス、junction/traversal 拒否を検証する。
13ケースは PowerShell 7 と Windows PowerShell 5.1 で成功した。
証跡は `.crer/install-tests/04cd5fc7a68a420daa75d8199ae8a9b3/summary.json`（7）、
`.crer/install-tests/5bd7d6869d7d44218065e24171c27bc1/summary.json`（5.1）。
製品コードのビルドや CfT 操作は行わない。

通常テストは `-NoPath`、`-RegistryTests` は専用の一時 HKCU キーへストレージを切り替えた配布物を使う。
実際のユーザー PATH を変更せず、追加・重複回避・所有判定・変数参照とレジストリ型の保持・削除を検証する。
テスト用レジストリキーは最後に削除し、実行証跡は保持する。

インストーラは管理状態 `.crer-install.json` を作り、追跡ファイルと hash、追加した PATH を記録する。
アンインストールは同じ管理状態を使い、変更・未追跡のファイル、CfT、作業フォルダを保持する。
ユーザー PATH の `%変数%` と既存の `REG_SZ` / `REG_EXPAND_SZ` の型も維持する。

利用者へ渡す説明は [日本語](user-guide.ja.md) と [English](user-guide.en.md)。
ZIP では `README.ja.md` / `README.en.md` として同梱し、相互リンクも配布名に合わせる。

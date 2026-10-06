- commitmessageはfix:などprefixを付け、prefixを除き日本語で記載

## Git 操作

- Git MCP を優先する理由は、Codex の実行環境で `.git/index.lock` への書き込みが権限不足で失敗するバグを回避するためである。
- 次の操作は、対応する Git MCP ツールを優先して使用する: `git_status`、`git_add`、`git_commit`、`git_reset`、`git_create_branch`、`git_checkout`、`git_branch`。
- 上記に対応するツールがない操作は、Git コマンドを直接実行する。
- Git コマンドが権限不足で失敗した場合は、承認リクエストを行い、承認を得てから再実行する。

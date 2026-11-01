# 修改约定

这是 Vibe coding 产物，纯分享、不维护。修改时保留 README 中的项目状态；开发说明供自行构建或 fork 使用，不代表维护承诺。

- 先读 [开发说明](docs/DEVELOPMENT.md)，按现有 C# / WinForms 结构做最小修改，保持系统 .NET Framework 编译器可构建。
- 关闭目标必须同时满足所属进程和到期文案匹配；原生文字、网页正文、UI Automation 共用 `Rules.Matches`。读取失败时跳过。
- 保留跨进程读取超时、后台任务数量上限、窗口身份复核和关闭次数限制。关闭行为用 `WM_CLOSE`，不结束 IDM、不修改授权数据。
- 修复先补能复现真实窗口结构的测试。用 `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test` 验证；模拟窗口通过 PID 隔离，不能操作用户的真实 IDM。
- 新增界面优先使用可用的 apple-design、emil-design-eng、animate 技能，保持克制、清晰、即时反馈；现有托盘菜单沿用系统控件。
- 行为或限制变化时同步 README；真实窗口验证和模拟测试分别报告。
- 提交前运行 `git diff --check`。源码仓库排除 `dist/`、日志、诊断报告、凭据及个人机器路径。

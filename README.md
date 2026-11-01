# IDM Close

安静运行在 Windows 托盘中的 IDM 简体中文试用到期提示关闭工具。

> **Vibe coding 产物，纯分享，不维护。**
>
> 这是借助 AI 编写、为个人使用场景做的小工具，按现状分享。不承诺后续更新、版本适配、问题修复或使用答疑，也不保证响应 Issue / PR。有需要请自行阅读代码、构建和修改。

只处理弹窗遮挡问题，不修改 IDM 的授权状态，也不能让已到期的 IDM 恢复使用。

## 获取与运行

仓库提供源码，`dist/` 构建产物不提交到 Git。下载或克隆源码后，在 Windows 上按下方“构建和测试”生成 EXE。

```powershell
git clone https://github.com/TianJie52009/idm-close.git
cd idm-close
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

## 使用

1. 双击 `dist\IDMClose.exe`。没有主窗口，托盘出现盾牌图标（可能在任务栏的隐藏图标里）。
2. 右键图标可暂停／恢复拦截、勾选开机启动、运行只读诊断、查看错误日志或退出。
3. 开机启动默认关闭；程序不保存暂停状态，下次启动恢复拦截。
4. 卸载前取消“开机启动”并退出，然后删除程序目录即可。运行数据位于 `%LOCALAPPDATA%\IDMClose`，需要时可手动删除。

程序目录应放在固定位置；勾选开机启动后如果移动 EXE，需要在新位置重新勾选。自启动只写入当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\IDMClose`。

## 工作方式与限制

- 只处理 `IDMan.exe` 所属顶层窗口。标题及控件文字合并、去除空白后，须同时包含 `Internet Download Manager` 和 `免费试用已经到期`。
- 窗口显示／名称变化事件触发检查，每秒扫描兜底。向匹配窗口发送 `WM_CLOSE`，确认窗口已销毁或不可见后才累计数量。
- 同一窗口至多请求关闭三次，每次至少间隔五秒。权限不足、窗口不响应等问题记录到错误日志；不结束 IDM 进程。
- 不修改 IDM 文件、注册信息、试用期限或授权状态。关闭提示不能使已到期的 IDM 恢复使用。
- 外部程序只能在窗口出现后响应，可能短暂闪现或抢走焦点。具体关闭行为由 IDM 决定，不能保证绝对无闪现或不影响其界面。
- 首版只支持上述简体中文文案。内嵌网页弹窗通过 `Internet Explorer_Server` 读取可见正文后匹配；不能通过系统文字、网页正文或 UI Automation 读取的弹窗会跳过，不按尺寸、颜色或坐标猜测关闭。
- 默认普通权限运行；若 IDM 以管理员身份运行，本工具可能被 Windows 阻止发送关闭消息。可让 IDM 普通权限运行，或手动以相同权限运行本工具。不会自动提权。
- 网页正文和 UI Automation 各最多保留一个后台读取任务；异常提供程序若卡住，原生文字扫描继续工作，对应回退需等待任务返回或重启工具。

不添加自定义过渡动效，使用 Windows 原生菜单、系统字体和即时状态反馈；关闭弹窗不显示成功通知。

## 诊断

右键“只读诊断…”或运行：

```powershell
.\dist\IDMClose.exe --inspect
```

只读取 IDM 窗口信息，写入 `%LOCALAPPDATA%\IDMClose\diagnostics.txt` 并用记事本打开，不发送关闭消息。希望保留目标弹窗供诊断时，先暂停拦截。诊断包含窗口和控件文字（排除 Edit 输入控件），可能含下载相关信息，分享前请自行检查。

错误日志在 `%LOCALAPPDATA%\IDMClose\errors.log`，超过约 256 KiB 后保留一份 `.old`。日志仅在出现问题时写入，不持续记录下载内容。

## 构建和测试

需要 Windows 自带的 .NET Framework 4.x（含 C# 编译器），无需 NuGet、Python 或 .NET SDK。

构建前先从托盘退出已运行的 IDM Close，以免 EXE 被占用；保留 IDM 运行即可。脚本使用 Windows PowerShell 5.1 可执行。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

产物位于 `dist`。测试覆盖文案规则、模拟窗口的关闭确认／重复请求限制／无响应、窗口提前销毁、单实例入口以及启动项写入与恢复。测试不会操作真实 IDM 窗口。

测试会临时改写当前用户的 `IDMClose` 启动项，并在 `finally` 中恢复原值；强制终止测试可能打断恢复。测试的模拟窗口放在屏幕外。只需构建时可省略 `-Test`。

自行修改代码前可参考 [开发说明](docs/DEVELOPMENT.md)；已知修复见 [修改记录](CHANGELOG.md)。

2026-11-02 验证：48 项断言通过，包含空标题的内嵌网页弹窗回归测试。真实到期弹窗此前无法读取原生文字，补充网页正文读取后精确命中；新版启动约 1.1 秒后该窗口已不可见，IDM 主进程仍运行。未验证完整下载任务；处理延迟不代表所有场景的性能保证。

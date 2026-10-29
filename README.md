# Codex 任务栏额度看板

Windows 任务栏上的独立小组件，显示当前 Codex 账号的 5 小时和每周剩余额度及重置时间，每分钟刷新。额度来自本机 Codex `app-server` 的 `account/rateLimits/read` 接口，不执行模型任务。

## 界面预览

![Codex 任务栏额度看板示意图](docs/preview.svg)

*示意图中的百分比和重置时间为示例，实际显示以当前账号数据及任务栏背景为准。*

## 使用条件

- Windows，.NET Framework 4.x（构建需要系统自带的 C# 编译器）
- 安装并登录 Codex 桌面版；程序在 `%LOCALAPPDATA%\OpenAI\Codex\bin` 中查找 `codex.exe`
- 主屏幕使用底部任务栏；任务栏隐藏或前台全屏时看板自动隐藏

## 启动

1. 将整个项目放在有写入权限的固定目录，保留 `app` 和 `work` 的相对位置。
2. 右键 `app/启用自动启动.ps1`，选择“使用 PowerShell 运行”，配置当前用户登录任务。该步骤不会要求管理员权限或保存密码；同名任务会被更新。
3. 双击 `app/CodexTaskbarUsage.exe` 启动。也可从 PowerShell 在该目录运行 `./CodexTaskbarUsage.exe --detached`，不创建自动启动任务。

任务计划程序引用可执行文件的绝对路径，移动目录后需重新运行自动启动脚本。右键看板可刷新、恢复位置、设置自动启动或退出；双击刷新，拖动可调整位置。退出会停止看板和守护进程。显示 `--` 表示尚未读取到额度，数值带 `*` 表示本次读取失败、保留上次结果。重置时间按本机时区显示。

## 从源码构建

在 PowerShell 中运行 `./build.ps1`，输出到 `app/CodexTaskbarUsage.exe`。构建不会自动启动或安装程序。若程序正在运行，请先退出再覆盖可执行文件。源码基于 .NET Framework WinForms，无需 NuGet 依赖。

## 数据与隐私

程序以当前用户权限调用本机 Codex，并使用现有登录状态。它不打包登录凭据，不将额度主动上传至其他服务。位置设置、用量状态和运行日志写入项目目录下的 `work/runtime`，这些文件不纳入版本控制。该接口及本机 Codex 的目录结构可能随版本变化。

本项目是独立工具，与 OpenAI 无官方关联。

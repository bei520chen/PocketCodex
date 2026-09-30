# PocketCodex v0.1.0

首个 Windows x64 分发版本。压缩包包含自包含的 .NET 后端、移动端网页、OCR 模型及启动脚本；无需另装 .NET 运行时。

## 功能

- 在电脑浏览器或手机 Safari 中查看 Codex 项目、历史会话和任务，创建、继续及中断任务，并通过实时连接查看执行过程。
- 通过访问密码、工作区白名单及 Tailscale Serve 在自己的设备间访问。
- 可选的 Windows 微信实例监控、消息 OCR 识别与手动确认后的联系人回复。

## 使用前

- Windows 10/11 x64，安装并登录 Codex CLI。
- 解压后先填写 `config\workspace-roots.txt`；如无法自动找到 Codex，再填写 `config\codex-path.txt`。
- 手机访问需要在电脑和手机上安装 Tailscale；微信功能需要本机安装并登录 Windows 微信。
- 详细步骤见仓库 README 和压缩包内的 README。

## 隐私与限制

- 压缩包不包含作者的访问密码、数据库、聊天记录、工作区配置或 Codex 登录凭据。每位使用者首次运行时会生成自己的密码和本地数据。
- 微信监控可能将会话标为已读，并把识别到的联系人与消息正文发送到所选 Codex 会话；窗口操作和 OCR 可能出现误识别。请仅在有权处理这些消息时启用。
- 当前移动端没有 Codex 操作审批界面；任务使用 `workspace-write` 和 `never` 审批策略。请只把需要操作的项目目录加入白名单，不要将服务端口直接暴露公网。
- 程序未进行代码签名，Windows 可能显示 SmartScreen 提示。

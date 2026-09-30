# Pocket Console Windows 分发包

这是一个 Windows x64 自包含分发包。它包含 Pocket Console 的后端、已构建的移动端网页、OCR 模型和启动脚本；不包含任何人的 Codex 登录凭据、访问密码、数据库、日志或工作区文件。

## 使用前准备

1. Windows 10/11 64 位。
2. 安装并登录 Codex CLI，并确认在 PowerShell 中执行 `codex --version` 有输出。
3. 如果要从手机访问，在电脑和手机上安装 Tailscale，并登录同一个 tailnet。
4. 微信监控/回复功能还需要本机安装并登录 Windows 微信客户端。

## 第一次使用

1. 将整个目录解压到一个固定位置，例如 `D:\PocketConsole`。
2. 编辑 `config\workspace-roots.txt`，每行填入一个允许 Codex 访问的目录。建议只填写实际项目所在目录，不要直接填写整个系统盘。
3. 如果 `codex.exe` 不在 PATH 中，编辑 `config\codex-path.txt`，填写它的完整路径。
4. 双击 `Start-PocketConsole.cmd`，或在 PowerShell 中执行：

   ```powershell
   .\scripts\Start-PocketConsole.ps1
   ```

5. 在电脑浏览器打开 `http://127.0.0.1:5086`。
6. 登录密码会显示在启动窗口中，也会保存在 `.runtime\access-password.txt`。

如果没有配置工作区目录，程序默认只允许访问当前分发包目录。

## 手机访问

1. 确认电脑和手机连接到同一个 Tailscale 网络。
2. 在电脑上执行：

   ```powershell
   .\scripts\Enable-Tailscale.ps1
   ```

3. 按脚本输出的地址在手机 Safari 打开。如果 Tailscale 要求首次授权，完成授权后重新执行脚本。
4. 如果暂时无法启用 HTTPS，可临时执行：

   ```powershell
   .\scripts\Enable-Tailscale.ps1 -Http
   ```

5. 手机登录时使用 `.runtime\access-password.txt` 中的密码。

## 停止与日志

- 停止服务：双击 `Stop-PocketConsole.cmd`，或执行 `.\scripts\Stop-PocketConsole.ps1`。
- 标准输出：`.runtime\pocket-console.out.log`。
- 错误日志：`.runtime\pocket-console.err.log`。
- 数据库和上传文件：`.runtime\data`、`.runtime\uploads`。

## 安全提醒

- 不要把 `.runtime` 目录、访问密码或数据库分享给其他人。
- 不要把 `5086` 端口直接暴露到公网；手机访问优先使用 Tailscale。
- 每位使用者都需要使用自己的 Codex CLI 登录状态。
- 微信监控会打开未读会话，可能将消息标为已读，并把识别到的联系人和消息正文发送到选定的 Codex 会话；仅在有权处理这些消息时启用。
- `config\workspace-roots.txt` 和 `config\codex-path.txt` 应填写使用者自己的路径，不要复制作者或他人的私人配置。
- 更新版本时，先停止旧服务并备份 `.runtime`，再替换应用文件。

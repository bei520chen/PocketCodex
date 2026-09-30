# PocketCodex

PocketCodex（界面名称 Pocket Console）是在 Windows 电脑上运行的 Codex 移动控制台。你可以在电脑浏览器或通过 Tailscale 在手机上查看项目、历史会话和任务，继续对话并查看执行过程。它还提供可选的 Windows 微信消息监控和指定联系人回复功能。

这是个人部署工具。服务默认监听本机回环地址；不要把服务端口直接开放到公网。

## 下载与准备

从 [Releases](https://github.com/bei520chen/PocketCodex/releases) 下载 `PocketCodex-v0.1.0-win-x64.zip`，解压到固定目录，例如 `D:\PocketCodex`。不要在压缩包内部直接运行。

需要：

- Windows 10/11 x64。
- 已安装并登录的 Codex CLI；在 PowerShell 执行 `codex --version` 确认可以找到它。如果不在 `PATH`，可在下文指定 `codex.exe` 路径。
- 手机远程访问需要在电脑和手机上安装 Tailscale，并登录同一个 tailnet。
- 微信功能需要在这台电脑上安装并登录 Windows 微信客户端；不用微信功能时无需安装。

分发包已包含 .NET 运行时、网页和 OCR 模型。只使用分发包无需安装 .NET SDK 或 Node.js。

## 第一次启动：需要改的地方

在解压目录中配置以下文件。配置文件初始只有注释，不含作者的个人路径或凭据。

| 文件 | 需要填写什么 | 是否必填 |
| --- | --- | --- |
| `config\workspace-roots.txt` | 每行一个允许 Codex 访问的现有 Windows 目录，例如 `D:\Projects`；只填实际需要的项目目录 | 建议填写；留空时仅允许分发包目录 |
| `config\codex-path.txt` | `codex.exe` 的完整路径，例如你自己电脑上的安装路径 | 仅在自动查找失败时填写 |

然后双击 `Start-PocketConsole.cmd`，或在解压目录的 PowerShell 中运行：

```powershell
.\scripts\Start-PocketConsole.ps1
```

打开 `http://127.0.0.1:5086`，输入启动窗口显示的随机访问密码。密码也保存在本机 `.runtime\access-password.txt`，后续启动会复用。若修改了工作区配置，先停止再重新启动服务。不要将 `.runtime` 目录、密码文件或数据库发给其他人。

启动后可在页面中创建项目、浏览 Codex 会话并创建任务。项目路径必须处于 `workspace-roots.txt` 中允许的目录下。

停止服务：双击 `Stop-PocketConsole.cmd`，或运行 `.\scripts\Stop-PocketConsole.ps1`。

## 手机访问

1. 确认电脑与手机已连接同一个 Tailscale 网络，且 PocketCodex 正在电脑上运行。
2. 在电脑的解压目录执行 `.\scripts\Enable-Tailscale.ps1`。若提示首次授权，按脚本显示的 Tailscale 地址授权后重试。
3. 在手机 Safari 打开脚本显示的 `https://...ts.net` 地址，输入访问密码；需要时可通过 Safari 分享菜单添加到主屏幕。

若暂时无法启用 HTTPS，可执行 `.\scripts\Enable-Tailscale.ps1 -Http` 做临时私网连接测试。关闭远程访问时执行 `.\scripts\Disable-Tailscale.ps1`。若启动时改了端口，启用 Tailscale 时也要传相同的 `-Port` 参数。

## 微信功能与隐私

微信监控是可选功能。进入页面后先刷新微信实例，再选择接收通知的 Codex 会话并启动监控。监控发现数字未读后会打开会话读取消息，这可能把消息标为已读，并将识别到的联系人名称和消息正文发送到所选 Codex 会话。请仅在你有权处理这些消息时启用。回复功能需要手动选择账号、联系人、填写正文并确认发送；刷新联系人也可能将会话标为已读。

软件会在本机 `.runtime` 保存访问密码、应用数据库、日志和上传文件；Codex 的登录与会话数据由本机 Codex 管理。分发包不附带这些本机数据。不要共享使用过的解压目录，也不要在截图或日志中暴露访问密码、工作区路径、联系人和聊天内容。

## 更新与排错

更新时先停止服务，备份自己解压目录下的 `.runtime` 和 `config`，将新版本解压到新目录，再按需迁移自己的配置和数据。不要把这些私人文件提交到 Git 或上传到 Release。

- 页面打不开：确认启动窗口没有报错，并访问 `http://127.0.0.1:5086`；查看 `.runtime\pocket-console.err.log`。
- Codex 无法启动：确认已登录 Codex，执行 `codex --version`；必要时填写 `config\codex-path.txt` 后重启。
- 项目路径被拒绝：检查 `config\workspace-roots.txt` 中的目录是否存在，以及项目是否位于这些目录下。
- 手机打不开：确认 Tailscale 两端在线，并重新运行 `Enable-Tailscale.ps1` 查看 Serve 状态。

## 从源码运行与打包

源码构建需要 .NET 9 SDK、Node.js 和 npm。首次在仓库根目录运行：

```powershell
Set-Location .\src\PocketConsole.Web
npm ci
npm run build
Set-Location ..\..
dotnet build .\PocketConsole.sln
.\scripts\Start-PocketConsole.ps1 -WorkspaceRoots @('D:\Projects')
```

源码启动脚本也接受 `-Build` 参数，会构建前后端。它默认把仓库父目录作为工作区，因此建议显式传入你自己的 `-WorkspaceRoots`。本机密码与数据库会分别写入被 Git 忽略的 `.runtime` 和 `src\PocketConsole.Api\Data`。

在 Windows x64 上构建可分发 ZIP：

```powershell
.\scripts\Package-PocketConsole.ps1 -PackageName PocketCodex-v0.1.0-win-x64
```

生成文件位于 `dist\PocketCodex-v0.1.0-win-x64.zip`。分发步骤详见 [docs/DISTRIBUTION.md](docs/DISTRIBUTION.md)。项目规划见 [docs/PROJECT_PLAN.md](docs/PROJECT_PLAN.md)。

## 安全边界

- API 和实时连接需要登录；工作区目录由白名单限制。首次密码为本机随机生成。
- 当前 Codex 任务使用 `workspace-write` 沙箱与 `never` 审批策略，移动端尚无审批界面；只配置你愿意交给 Codex 操作的目录。
- Tailscale Serve 只用于私有 tailnet 访问。不要通过路由器端口转发或公网反向代理公开 `5086` 端口。

本仓库目前未附开源许可证；再分发或修改时请先确认仓库所有者的授权。

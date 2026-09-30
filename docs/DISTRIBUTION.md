# 分发构建

在 Windows x64 开发机上，从仓库根目录执行：

```powershell
.\scripts\Package-PocketConsole.ps1
```

默认输出：

- `dist\PocketConsole-win-x64.zip`
- `dist\PocketConsole-win-x64\` 解压后的目录

创建 GitHub Release 使用的版本化文件名：

```powershell
.\scripts\Package-PocketConsole.ps1 -PackageName PocketCodex-v0.1.0-win-x64
```

只上传脚本新生成并检查过的 ZIP。已经启动过的解压目录会产生 `.runtime` 中的访问密码、数据库和日志，不能将该目录重新压缩后分享。

脚本会执行前端构建和 .NET 自包含发布，并将以下内容放入包中：

- `src\PocketConsole.Api`：Windows x64 发布产物和前端静态文件
- `scripts`：启动、停止、Tailscale 脚本
- `config`：工作区和 Codex 路径配置模板
- `README.md`：给接收者的使用说明

脚本不会复制仓库中的 `.runtime`、本机数据库、密码、日志、`node_modules` 或源码缓存。
如果临时打包目录中发现数据库、日志或本机凭据文件，脚本会拒绝生成 ZIP。

## 常用参数

```powershell
.\scripts\Package-PocketConsole.ps1 -OutputDirectory .\dist -PackageName PocketConsole-win-x64
```

如果前端静态文件已经构建完成，可以跳过前端构建：

```powershell
.\scripts\Package-PocketConsole.ps1 -SkipFrontendBuild
```

分发包默认使用 `win-x64` 和 Release 配置。接收者无需安装 .NET 运行时，但仍需单独安装并登录 Codex CLI；手机访问还需单独安装 Tailscale。

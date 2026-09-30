# Codex Pocket Console 项目规划

## 1. 目标

在 iPhone 上提供一个接近参考截图中“Codex 工作台”的移动端界面，实现：

1. 查看和搜索开发机上的历史项目与 Codex 会话。
2. 新建会话、续接历史会话、分叉会话。
3. 向运行中的任务发送排队消息或即时引导消息。
4. 实时查看回答、命令执行、文件修改和任务状态。
5. 在手机上批准或拒绝高风险操作。
6. 归档、置顶、重命名会话，并查看长期目标状态。
7. 通过安全的私有网络从外部访问开发机。

非目标：把 iPhone 变成完整终端、远程桌面，或允许任意系统命令无审批执行。

## 2. 先用官方方案还是自建

### 方案 A：ChatGPT 手机端 Remote（首选）

截至 2026 年 8 月，官方 Remote 已覆盖从手机连接主机、查看工作区与会话、远程启动和引导 Codex 工作等核心场景。先在 ChatGPT iOS 客户端检查当前账号是否已有该入口。

优点：无需自行维护认证和协议兼容，iPhone 原生体验，并且功能能随 Codex 更新。

限制：是否可用取决于账号和客户端功能开放情况；首页、项目聚合、快捷工作流和公司内部集成难以完全自定义。

### 方案 B：自建私有 PWA（本项目）

适合需要截图所示自定义工作台、公司项目聚合、专用快捷操作或内部系统联动的情况。

建议采用“官方 Remote 日常使用 + 自建控制台补充管理”的混合模式，而不是完全替代官方客户端。

## 3. 技术架构

### 3.1 前端

- Vue 3 + TypeScript + Vite。
- PWA，可从 Safari 添加到主屏幕。
- 移动端优先，适配安全区域、深色模式和弱网重连。
- 实时事件统一使用 ASP.NET Core SignalR，不手写 WebSocket 协议；普通查询使用 REST。
- 本地仅缓存界面偏好，不保存 OpenAI 凭据。

主要页面：

| 页面 | 功能 |
| --- | --- |
| 首页 | 新建任务、最近项目、最近任务、最近会话、待审批数量 |
| 任务 | 独立任务列表、状态流转、项目和 thread 关联 |
| 项目 | 工作区列表、Git 分支、最近会话、运行状态 |
| 会话 | 对话流、工具事件、文件变更、输入框、排队/引导切换 |
| 历史 | 搜索、时间筛选、置顶、归档、恢复、分叉 |
| 文件 | 限定工作区内只读浏览和 diff 查看 |
| 设置 | 主机状态、工作区白名单、默认权限、通知和安全设置 |

### 3.2 后端网关

- ASP.NET Core Web API，使用当前机器可用的 .NET LTS 版本。
- SignalR 向手机转发 Codex 增量事件和审批请求。
- SQLite 保存项目别名、标签、快捷指令、设备和审计记录。
- 后台托管 `codex app-server` 子进程，通过 `stdio` JSON-RPC 通信。
- 不直接解析或修改 `~/.codex` 内部 SQLite/JSONL；历史数据通过 App Server API 获取。

选择 `stdio` 的原因：App Server 的远程 WebSocket 传输仍属于实验能力；由网关持有本机进程连接，再对外提供自己的 HTTPS API，边界更稳定也更安全。

### 3.3 Codex 适配层

第一版使用这些核心能力：

- `thread/list`：分页、搜索、按工作目录和归档状态筛选。
- `thread/read`：只读加载会话详情。
- `thread/start`、`thread/resume`、`thread/fork`：创建、续接和分叉。
- `turn/start`、`turn/steer`、`turn/interrupt`：发送、引导和停止。
- `thread/name/set`、`thread/metadata/update`：重命名和置顶。
- `thread/archive` / `thread/unarchive`：归档与恢复。
- `thread/goal/get` / `thread/goal/set`：长期目标。
- 命令执行、文件变更、权限请求和工具输入审批。

实验接口放到功能开关后，不能作为首版唯一数据路径。开发时必须使用本机当前 Codex 版本生成的 schema 校验具体方法和字段。

### 3.4 项目模型

Codex 的核心对象是 thread，不完全等同于“项目”。本项目增加独立的 Project 聚合层：

```text
Project
├─ id
├─ displayName
├─ workspacePath
├─ repositoryRoot
├─ defaultBranch
├─ tags
└─ sessions[]  ← 按 thread.cwd / Git 信息关联
```

项目目录只允许来自配置的工作区根目录。任何客户端传入路径都必须在后端规范化并验证，防止路径穿越。

## 4. 核心交互

### 新建与续接

1. 手机选择项目、分支/工作树策略和权限档位。
2. 网关验证工作区路径并创建或续接 thread。
3. 用户发送任务，网关启动 turn。
4. 增量事件实时推送到手机。

历史详情先只读加载；只有用户点击“继续”时才恢复会话。

### 运行中控制

- “排队”：网关保留消息，收到当前 turn 完成事件后再发送。
- “立即引导”：调用 steer，并校验当前 `turnId`。
- “停止”：中断当前 turn。

### 审批

1. App Server 发出命令、文件或权限审批请求。
2. 网关记录短期审批状态并推送手机通知。
3. 手机展示命令、目录、网络目标、文件变更和原因。
4. 用户选择单次允许、会话允许、拒绝或取消。
5. 网关返回决定并写入审计日志。

## 5. 后端 API 草案

```text
GET    /api/host/status
GET    /api/projects
POST   /api/projects
GET    /api/projects/{id}/sessions

GET    /api/sessions
GET    /api/sessions/{id}
POST   /api/sessions
POST   /api/sessions/{id}/resume
POST   /api/sessions/{id}/fork
POST   /api/sessions/{id}/messages
POST   /api/sessions/{id}/steer
POST   /api/sessions/{id}/interrupt
POST   /api/sessions/{id}/archive
POST   /api/sessions/{id}/pin
PATCH  /api/sessions/{id}/name

GET    /api/approvals
POST   /api/approvals/{id}/decision
GET    /hubs/codex
```

浏览器不接触 Codex JSON-RPC，也不获得 App Server 的本地连接凭据。

## 6. 安全与部署

推荐拓扑：

```text
iPhone + Tailscale
        │ 私有 Tailnet HTTPS
        ▼
Windows 开发机上的 Pocket Gateway
        │ 本机 stdio
        ▼
Codex App Server
```

不建议第一版使用公网域名直接暴露开发机。安全基线：

- 网关默认仅监听回环地址，再通过 Tailscale Serve 暴露。
- 使用 Tailscale 身份或 Passkey 登录，禁止简单固定密码。
- Cookie 使用 `Secure`、`HttpOnly`、`SameSite=Strict`。
- 强制工作区根目录白名单。
- 默认只读或工作区写入，禁止默认全盘访问。
- 高风险操作不允许永久自动批准。
- 危险命令、扩大目录权限等操作二次确认。
- 日志屏蔽令牌、Cookie、Authorization 头和常见密钥格式。
- App Server 崩溃后自动重启，未完成审批全部作废。

## 7. 数据库范围

应用数据库只保存控制台自己的数据，不复制完整 Codex 对话正文。Task 是用户要完成的工作目标，thread 是 Codex 的执行会话，两者不能混为一体：

- `Projects`：项目别名、目录、标签、排序。
- Tasks：标题、任务提示、项目路径、状态、错误、生命周期时间和可选 ThreadId。
- `QuickPrompts`：快捷任务模板。
- `Devices`：已授权移动设备和吊销状态。
- `ApprovalAudits`：审批摘要、结果、时间和设备。
- `UserPreferences`：主题、默认项目、消息模式。

会话正文、turn 和 item 仍以 Codex App Server 为事实来源，避免双写和格式漂移。

Tasks 状态包括 draft、queued、running、waitingApproval、completed、failed、cancelled。首版一个任务可关联一个当前 thread，后续增加任务与多次执行记录的一对多关系。

## 8. 开发阶段

### Phase 0：环境验证（0.5 天）

- 确认本机 Codex 可启动 `app-server`。
- 生成与当前版本匹配的 TypeScript/JSON Schema。
- 验证历史列表、只读详情、恢复和事件流。
- 确认 ChatGPT iOS Remote 是否对当前账号开放。

验收：测试程序能列出最近会话，并只读打开一个会话。

### Phase 1：只读 MVP（1–2 天）

- 创建后端、前端、SQLite 和 PWA 脚手架。
- 实现主机状态、项目列表、历史会话、会话详情。
- 实现手机布局、深色主题和断线重连。
- 通过 Tailscale 在 iPhone Safari 打开。

验收：手机可查看项目和历史会话，不具备写操作。

### Phase 2：会话控制（2–3 天）

- 新建、续接、分叉、重命名、置顶和归档会话。
- 实现实时回答、命令和文件变更事件。
- 实现发送、排队、引导和停止。

验收：手机发起任务后，可实时看到完整生命周期并续接历史会话。

### Phase 3：安全审批（1–2 天）

- 命令、文件、权限和工具输入审批卡片。
- 单次/会话授权、拒绝、取消和超时。
- 审计日志、设备身份和二次确认。

验收：需要审批的任务在手机确认前不能继续，拒绝后 Codex 收到正确结果。

### Phase 4：体验增强（2–4 天）

- 推送通知、快捷指令、附件上传、目标管理、代码审阅。
- 项目标签、全文搜索、会话树和工作树管理。
- 离线壳、弱网恢复、应用图标和启动画面。

验收：体验达到参考截图的工作台形态，并可连续日常使用。

## 9. 首版目录建议

```text
Connection/
├─ src/
│  ├─ PocketConsole.Api/
│  ├─ PocketConsole.Application/
│  ├─ PocketConsole.Infrastructure/
│  └─ PocketConsole.Web/
├─ tests/
│  ├─ PocketConsole.UnitTests/
│  └─ PocketConsole.IntegrationTests/
├─ docs/
│  └─ PROJECT_PLAN.md
└─ README.md
```

核心后端模块：

- `CodexProcessHost`：启动、监控和重启 App Server。
- `CodexRpcClient`：请求 ID、超时、并发请求和通知分发。
- `ThreadService`：会话查询与生命周期。
- `TurnService`：发送、排队、引导和中断。
- `ApprovalService`：审批状态机和审计。
- `WorkspaceGuard`：路径白名单和权限验证。
- `CodexHub`：实时事件推送。

## 10. 主要风险

| 风险 | 处理方式 |
| --- | --- |
| Codex 协议随版本变化 | 读取版本；由当前 CLI 生成 schema；适配层隔离协议 |
| 实验接口不稳定 | MVP 依赖核心接口；实验功能用 feature flag |
| 手机断网导致审批悬挂 | 审批超时、重连恢复、过期请求自动取消 |
| 开发机被公网扫描 | 仅 Tailscale 私网访问，不开放路由器端口 |
| 越权访问其他目录 | 规范化路径并强制工作区白名单 |
| 网关重启丢失状态 | 重连后读取 thread 状态校准，短期队列持久化 |
| 与官方 Remote 重复建设 | 先验证官方能力，只开发明确缺失的定制功能 |

## 11. 完成定义

- iPhone 可从主屏幕打开 PWA，并通过私有网络连接开发机。
- 能查看、搜索、续接和归档本机 Codex 历史会话。
- 能选择项目创建新会话并实时查看执行过程。
- 能排队、引导和停止运行中的任务。
- 所有命令、文件和权限审批可在手机处理并留有审计记录。
- 浏览器和应用数据库中不存在 OpenAI 登录令牌明文。
- 未配置的目录无法被浏览或作为任务工作目录。
- 断网、网关重启和 App Server 重启后能够安全恢复或明确失败。

import type { AuthStatus, CreateProject, CreateSession, CreateTask, HostStatus, PocketTask, Project, Session, SessionDetail, UploadedAttachment, WeChatMonitorStatus } from "./types";

async function errorMessage(response: Response) {
  const text = await response.text();
  if (!text) return "Request failed: " + response.status;
  try {
    const value = JSON.parse(text) as { error?: string; detail?: string; title?: string };
    return value.error || value.detail || value.title || text;
  } catch { return text; }
}

async function request<T>(path: string): Promise<T> {
  const response = await fetch(path, { headers: { Accept: "application/json" } });
  if (!response.ok) throw Object.assign(new Error(await errorMessage(response)), { status: response.status });
  const text = await response.text();
  const contentType = response.headers.get("content-type") || "";
  if (!contentType.includes("json") || text.trimStart().startsWith("<!doctype") || text.trimStart().startsWith("<html")) {
    throw Object.assign(new Error("当前服务仍在运行旧版本，请稍后重启服务"), { status: 426 });
  }
  try { return JSON.parse(text) as T; }
  catch { throw Object.assign(new Error("服务器返回了无效的会话数据"), { status: 502 }); }
}

async function upload(path: string, file: File, onProgress?: (progress: number) => void): Promise<UploadedAttachment> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open("POST", path);
    request.timeout = 120_000;
    request.withCredentials = true;
    request.setRequestHeader("Accept", "application/json");
    request.upload.onprogress = event => {
      if (event.lengthComputable) onProgress?.(Math.min(99, Math.round(event.loaded / event.total * 100)));
    };
    request.onerror = () => reject(new Error("附件上传失败，请检查网络连接"));
    request.ontimeout = () => reject(new Error("附件上传超时，请缩小文件后重试"));
    request.onabort = () => reject(new Error("附件上传已取消"));
    request.onload = () => {
      if (request.status < 200 || request.status >= 300) {
        try {
          const value = JSON.parse(request.responseText) as { error?: string; detail?: string; title?: string };
          reject(Object.assign(new Error(value.error || value.detail || value.title || "附件上传失败"), { status: request.status }));
        } catch { reject(Object.assign(new Error(request.responseText || "附件上传失败"), { status: request.status })); }
        return;
      }
      try {
        onProgress?.(100);
        resolve(JSON.parse(request.responseText) as UploadedAttachment);
      } catch { reject(new Error("服务器返回了无效的附件数据")); }
    };
    const body = new FormData();
    body.append("file", file);
    request.send(body);
  });
}

async function send<T>(path: string, method: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) throw Object.assign(new Error(await errorMessage(response)), { status: response.status });
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

export const api = {
  authStatus: () => request<AuthStatus>("/api/auth/status"),
  login: (password: string) => send<AuthStatus>("/api/auth/login", "POST", { password }),
  logout: () => send<void>("/api/auth/logout", "POST"),
  host: () => request<HostStatus>("/api/host/status"),
  weChatMonitor: () => request<WeChatMonitorStatus>("/api/wechat-monitor/status"),
  startWeChatMonitor: (threadId: string, intervalSeconds = 60) => send<WeChatMonitorStatus>("/api/wechat-monitor/start", "POST", { threadId, intervalSeconds }),
  stopWeChatMonitor: () => send<WeChatMonitorStatus>("/api/wechat-monitor/stop", "POST"),
  refreshWeChatInstances: () => send<WeChatMonitorStatus>("/api/wechat-monitor/refresh-instances", "POST"),
  renameWeChatInstance: (id: string, name: string) => send<WeChatMonitorStatus>("/api/wechat-monitor/instances/" + encodeURIComponent(id) + "/name", "POST", { name }),
  projects: (search = "") => request<Project[]>("/api/projects?search=" + encodeURIComponent(search)),
  projectRoots: () => request<string[]>("/api/projects/roots"),
  createProject: (project: CreateProject) => send<Project>("/api/projects", "POST", project),
  uploadAttachment: (file: File, onProgress?: (progress: number) => void) => upload("/api/attachments", file, onProgress),
  sessions: (search = "", cwd = "") => request<{ items: Session[] }>("/api/sessions?limit=50&search=" + encodeURIComponent(search) + "&cwd=" + encodeURIComponent(cwd)),
  session: (id: string) => request<SessionDetail>("/api/sessions/" + encodeURIComponent(id)),
  sessionSummary: async (id: string) => {
    try { return await request<SessionDetail>("/api/sessions/" + encodeURIComponent(id) + "/summary"); }
    catch (reason) { if ((reason as { status?: number }).status === 426) return request<SessionDetail>("/api/sessions/" + encodeURIComponent(id)); throw reason; }
  },
  sessionContent: async (id: string) => {
    try { return await request<SessionDetail>("/api/sessions/" + encodeURIComponent(id) + "/content"); }
    catch (reason) { if ((reason as { status?: number }).status === 426) return request<SessionDetail>("/api/sessions/" + encodeURIComponent(id)); throw reason; }
  },
  createSession: (value: CreateSession) => send<SessionDetail>("/api/sessions", "POST", value),
  sendSessionMessage: (id: string, message: string, attachmentIds: string[] = []) => send<SessionDetail>("/api/sessions/" + encodeURIComponent(id) + "/messages", "POST", { message, attachmentIds }),
  tasks: () => request<PocketTask[]>("/api/tasks"),
  createTask: (task: CreateTask) => send<PocketTask>("/api/tasks", "POST", task),
  updateTask: (id: string, value: Partial<Pick<PocketTask, "title" | "status" | "threadId" | "lastError" | "projectPath">>) => send<PocketTask>("/api/tasks/" + id, "PATCH", value),
  deleteTask: (id: string) => send<void>("/api/tasks/" + id, "DELETE"),
  startTask: (id: string) => send<PocketTask>("/api/tasks/" + id + "/start", "POST"),
  interruptTask: (id: string) => send<PocketTask>("/api/tasks/" + id + "/interrupt", "POST"),
  sendTaskMessage: (id: string, message: string) => send<PocketTask>("/api/tasks/" + id + "/messages", "POST", { message })
};

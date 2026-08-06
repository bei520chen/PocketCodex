export type HostStatus = {
  connected: boolean;
  codexVersion: string;
  executablePath: string;
  startedAt: string;
};

export type Session = {
  id: string;
  sessionId: string;
  title: string;
  preview: string;
  workingDirectory: string;
  projectName: string;
  status: string;
  source: string;
  branch: string | null;
  agentNickname: string | null;
  createdAt: number;
  updatedAt: number;
};

export type Project = {
  id: string;
  name: string;
  workingDirectory: string;
  branch: string | null;
  sessionCount: number;
  lastActiveAt: number;
  lastSessionTitle: string;
};

export type SessionDetail = { summary: Session; thread: { turns: Turn[] } };
export type Turn = { id: string; status: string; items: ThreadItem[]; startedAt: number | null };
export type ThreadItem = Record<string, unknown> & { type: string; id: string };

export type PocketTask = {
  id: string;
  title: string;
  prompt: string;
  projectPath: string;
  threadId: string | null;
  status: string;
  lastError: string | null;
  createdAt: string;
  updatedAt: string;
  startedAt: string | null;
  completedAt: string | null;
};

export type CreateTask = { title: string; prompt: string; projectPath: string; threadId?: string | null };

export type AuthStatus = { authenticated: boolean };

export type UploadedAttachment = { id: string; name: string; contentType: string; size: number; isImage: boolean };

export type CreateProject = { name: string; workingDirectory: string; createDirectory: boolean };
export type CreateSession = { projectPath: string; message: string; attachmentIds?: string[] };

export type WeChatInstance = {
  id: string;
  number: number;
  displayName: string;
  windowHandle: number;
  processId: number;
  online: boolean;
  loggedIn: boolean;
  windowState: string;
  lastSeenAt: string | null;
  lastScanAt: string | null;
  lastUnreadCount: number;
  lastError: string | null;
};

export type WeChatMonitorStatus = {
  running: boolean;
  threadId: string | null;
  intervalSeconds: number;
  lastScanAt: string | null;
  lastNotificationAt: string | null;
  pendingMessageCount: number;
  lastError: string | null;
  instances: WeChatInstance[];
};

export type WeChatContacts = { instanceId: string; instanceName: string; contacts: string[] };
export type SendWeChatMessageResult = { sent: boolean; instanceName: string; contactName: string; sentAt: string };

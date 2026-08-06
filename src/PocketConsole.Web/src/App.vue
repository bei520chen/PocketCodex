<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, onUnmounted, reactive, ref } from "vue";
import type { HubConnection } from "@microsoft/signalr";
import { api } from "./api";
import { connectRealtime } from "./realtime";
import ProjectGrid from "./components/ProjectGrid.vue";
import SessionList from "./components/SessionList.vue";
import TaskList from "./components/TaskList.vue";
import MessageContent from "./components/MessageContent.vue";
import ToolActivity from "./components/ToolActivity.vue";
import type { HostStatus, PocketTask, Project, Session, SessionDetail, ThreadItem, UploadedAttachment, WeChatMonitorStatus } from "./types";

type Tab = "home" | "tasks" | "projects" | "history" | "settings";
type TimelineKind = "user" | "agent" | "reasoning" | "command" | "file" | "mcp" | "tool";
type PendingAttachment = UploadedAttachment & { localId: string; progress: number; status: "uploading" | "completed" | "failed"; error?: string };

type LiveTimelineItem = {
  id: string;
  turnId?: string;
  kind: TimelineKind;
  label: string;
  title?: string;
  text: string;
  status: "running" | "completed" | "failed";
};

const tab = ref<Tab>("home");
const authenticated = ref(false);
const authReady = ref(false);
const password = ref("");
const loginError = ref("");
const host = ref<HostStatus | null>(null);
const projects = ref<Project[]>([]);
const projectRoots = ref<string[]>([]);
const sessions = ref<Session[]>([]);
const tasks = ref<PocketTask[]>([]);
const selected = ref<SessionDetail | null>(null);
const openingSessionId = ref<string | null>(null);
const sessionContentLoading = ref(false);
let sessionContentRequest = 0;
const search = ref("");
const searchInput = ref<HTMLInputElement | null>(null);
const featureNotice = ref("");
const activeSearch = ref("");
const selectedProject = ref<Project | null>(null);
const loading = ref(true);
const searching = ref(false);
const error = ref("");
const realtimeConnected = ref(false);
const showCreateMenu = ref(false);
const showTaskForm = ref(false);
const showProjectForm = ref(false);
const projectCreationContext = ref<"standalone" | "session" | "task">("standalone");
const showSessionForm = ref(false);
const associatingTask = ref<PocketTask | null>(null);
const savingTask = ref(false);
const taskFormError = ref("");
const taskForm = reactive({ title: "", prompt: "", projectPath: "" });
const projectForm = reactive({ name: "", workingDirectory: "", createDirectory: true });
const sessionForm = reactive({ projectPath: "", message: "" });
const projectFormError = ref("");
const sessionFormError = ref("");
const savingProject = ref(false);
const creatingSession = ref(false);
const newSessionAttachments = ref<PendingAttachment[]>([]);
const sessionAttachments = ref<PendingAttachment[]>([]);
const uploadingNewSessionAttachment = ref(false);
const uploadingSessionAttachment = ref(false);
const sessionMessage = ref("");
const sendingSessionMessage = ref(false);
const sessionRunning = ref(false);
const liveItems = ref<LiveTimelineItem[]>([]);
const timeline = ref<HTMLElement | null>(null);
const displayedTurnCount = ref(10);
const weChatMonitor = ref<WeChatMonitorStatus | null>(null);
const weChatThreadId = ref("");
const weChatIntervalSeconds = ref(60);
const weChatMonitorBusy = ref(false);
const weChatMonitorError = ref("");
const weChatReplyInstanceId = ref("");
const weChatReplyContacts = ref<string[]>([]);
const weChatReplyContact = ref("");
const weChatReplyMessage = ref("");
const weChatReplyBusy = ref(false);
const weChatReplyError = ref("");
const weChatReplySuccess = ref("");
let connection: HubConnection | null = null;
let weChatMonitorTimer: number | null = null;

const normalizedSearch = computed(() => activeSearch.value.trim().toLocaleLowerCase());
const filteredProjects = computed(() => filterBySearch(projects.value, project => [project.name, project.workingDirectory, project.lastSessionTitle]));
const filteredSessions = computed(() => filterBySearch(sessions.value, session => [session.title, session.preview, session.projectName, session.workingDirectory, session.branch || ""]));
const visibleSessions = computed(() => tab.value === "home" ? filteredSessions.value.slice(0, 6) : filteredSessions.value);
const recentTasks = computed(() => tab.value === "home" ? tasks.value.slice(0, 5) : tasks.value);
const searchResultCount = computed(() => filteredProjects.value.length + filteredSessions.value.length);
const visibleSessionTurns = computed(() => {
  const turns = selected.value?.thread.turns || [];
  return turns.slice(Math.max(0, turns.length - displayedTurnCount.value));
});
const hasEarlierSessionTurns = computed(() => (selected.value?.thread.turns.length || 0) > displayedTurnCount.value);
const runningTasks = computed(() => tasks.value.filter(task => task.status === "running"));
const completedTasks = computed(() => tasks.value.filter(task => task.status === "completed"));
const activeSessions = computed(() => sessions.value.slice(0, 4));
const recentActivities = computed(() => [
  ...tasks.value.map(task => ({ id: "task-" + task.id, kind: "task" as const, title: task.title, timestamp: new Date(task.updatedAt).getTime(), task })),
  ...sessions.value.map(session => ({ id: "session-" + session.id, kind: "session" as const, title: session.title, timestamp: session.updatedAt * 1000, session }))
].sort((left, right) => right.timestamp - left.timestamp).slice(0, 5));
const projectHealth = computed(() => projects.value.slice(0, 6).map(project => {
  const ageDays = Math.max(0, (Date.now() - project.lastActiveAt * 1000) / 86_400_000);
  const score = Math.max(35, Math.min(100, Math.round(92 - ageDays * 3 + Math.min(project.sessionCount, 8))));
  return { project, score, state: score >= 80 ? "healthy" : score >= 60 ? "warning" : "critical", label: score >= 80 ? "健康" : score >= 60 ? "注意" : "较少活动" };
}));
onMounted(async () => {
  try { authenticated.value = (await api.authStatus()).authenticated; }
  finally { authReady.value = true; }
  if (!authenticated.value) return;
  await startApplication();
});

async function startApplication() {
  await refresh();
  startWeChatMonitorPolling();
  try {
    connection = await connectRealtime({
      taskCreated: upsertTask,
      taskUpdated: upsertTask,
      taskDeleted: (id) => tasks.value = tasks.value.filter((task) => task.id !== id),
      codexEvent: (method, parameters) => void handleCodexEvent(method, parameters),
      stateChanged: (connected) => realtimeConnected.value = connected
    });
  } catch { realtimeConnected.value = false; }
}

onBeforeUnmount(() => {
  void connection?.stop();
  if (weChatMonitorTimer !== null) window.clearInterval(weChatMonitorTimer);
});

async function refresh() {
  loading.value = true;
  error.value = "";
  try {
    const [hostResult, projectResult, sessionResult, taskResult, rootResult, weChatResult] = await Promise.all([
      api.host(), api.projects(), api.sessions("", selectedProject.value?.workingDirectory || ""), api.tasks(), api.projectRoots(), api.weChatMonitor()
    ]);
    host.value = hostResult;
    projects.value = projectResult;
    sessions.value = sessionResult.items;
    tasks.value = taskResult;
    projectRoots.value = rootResult;
    weChatMonitor.value = weChatResult;
    if (weChatResult.threadId) weChatThreadId.value = weChatResult.threadId;
    else if (!weChatThreadId.value && sessionResult.items.length) weChatThreadId.value = sessionResult.items[0].id;
    if (weChatResult.running) weChatIntervalSeconds.value = weChatResult.intervalSeconds;
    if (!projectForm.workingDirectory && rootResult.length) projectForm.workingDirectory = rootResult[0] + "\\NewProject";
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : "连接开发机失败";
  } finally { loading.value = false; }
}

async function performSearch() {
  searching.value = true;
  activeSearch.value = search.value.trim();
  await nextTick();
  searching.value = false;
}

function clearSearch() {
  search.value = "";
  activeSearch.value = "";
}

function filterBySearch<T>(items: T[], values: (item: T) => string[]) {
  if (!normalizedSearch.value) return items;
  return items.filter(item => values(item).some(value => value.toLocaleLowerCase().includes(normalizedSearch.value)));
}

async function login() {
  loginError.value = "";
  try {
    authenticated.value = (await api.login(password.value)).authenticated;
    password.value = "";
    await startApplication();
  } catch {
    loginError.value = "密码不正确";
  }
}

async function logout() {
  await connection?.stop();
  connection = null;
  await api.logout();
  authenticated.value = false;
  host.value = null;
  projects.value = [];
  sessions.value = [];
  tasks.value = [];
  weChatMonitor.value = null;
  if (weChatMonitorTimer !== null) window.clearInterval(weChatMonitorTimer);
  weChatMonitorTimer = null;
}

function startWeChatMonitorPolling() {
  if (weChatMonitorTimer !== null) window.clearInterval(weChatMonitorTimer);
  weChatMonitorTimer = window.setInterval(() => { if (authenticated.value) void loadWeChatMonitor(); }, 5000);
}

async function loadWeChatMonitor() {
  try
  {
    weChatMonitor.value = await api.weChatMonitor();
    if (weChatMonitor.value.threadId) weChatThreadId.value = weChatMonitor.value.threadId;
    if (weChatMonitor.value.running) weChatIntervalSeconds.value = weChatMonitor.value.intervalSeconds;
  }
  catch { }
}

async function refreshWeChatInstances() {
  if (weChatMonitorBusy.value) return;
  weChatMonitorBusy.value = true;
  weChatMonitorError.value = "";
  try { weChatMonitor.value = await api.refreshWeChatInstances(); }
  catch (reason) { weChatMonitorError.value = reason instanceof Error ? reason.message : "刷新微信实例失败"; }
  finally { weChatMonitorBusy.value = false; }
}

async function startWeChatMonitor() {
  if (!weChatThreadId.value || weChatMonitorBusy.value) return;
  weChatMonitorBusy.value = true;
  weChatMonitorError.value = "";
  try { weChatMonitor.value = await api.startWeChatMonitor(weChatThreadId.value, weChatIntervalSeconds.value); }
  catch (reason) { weChatMonitorError.value = reason instanceof Error ? reason.message : "启动微信监控失败"; }
  finally { weChatMonitorBusy.value = false; }
}

async function stopWeChatMonitor() {
  if (weChatMonitorBusy.value) return;
  weChatMonitorBusy.value = true;
  weChatMonitorError.value = "";
  try { weChatMonitor.value = await api.stopWeChatMonitor(); }
  catch (reason) { weChatMonitorError.value = reason instanceof Error ? reason.message : "停止微信监控失败"; }
  finally { weChatMonitorBusy.value = false; }
}

async function renameWeChatInstance(id: string, event: Event) {
  const name = (event.target as HTMLInputElement).value;
  try { weChatMonitor.value = await api.renameWeChatInstance(id, name); }
  catch (reason) { weChatMonitorError.value = reason instanceof Error ? reason.message : "修改微信名称失败"; }
}

async function refreshWeChatContacts() {
  if (!weChatReplyInstanceId.value || weChatReplyBusy.value) return;
  weChatReplyBusy.value = true;
  weChatReplyError.value = "";
  weChatReplySuccess.value = "";
  try {
    const result = await api.weChatContacts(weChatReplyInstanceId.value);
    weChatReplyContacts.value = result.contacts;
    if (!result.contacts.includes(weChatReplyContact.value)) weChatReplyContact.value = "";
  }
  catch (reason) { weChatReplyError.value = reason instanceof Error ? reason.message : "读取微信联系人失败"; }
  finally { weChatReplyBusy.value = false; }
}

async function sendWeChatReply() {
  const instance = weChatMonitor.value?.instances.find(item => item.id === weChatReplyInstanceId.value);
  const contact = weChatReplyContact.value.trim();
  const message = weChatReplyMessage.value.trim();
  if (!instance || !contact || !message || weChatReplyBusy.value) return;
  const confirmed = window.confirm(`将通过 ${instance.displayName} 向“${contact}”发送：\n\n${message}\n\n确认发送吗？`);
  if (!confirmed) return;
  weChatReplyBusy.value = true;
  weChatReplyError.value = "";
  weChatReplySuccess.value = "";
  try {
    const result = await api.sendWeChatMessage(instance.id, contact, message);
    weChatReplySuccess.value = `已通过 ${result.instanceName} 发送给 ${result.contactName}`;
    weChatReplyMessage.value = "";
  }
  catch (reason) { weChatReplyError.value = reason instanceof Error ? reason.message : "微信消息发送失败"; }
  finally { weChatReplyBusy.value = false; }
}

async function openSession(id: string) {
  if (openingSessionId.value) return;
  const summary = sessions.value.find((session) => session.id === id);
  if (!summary) return;
  const requestId = ++sessionContentRequest;
  openingSessionId.value = id;
  sessionContentLoading.value = true;
  displayedTurnCount.value = 10;
  error.value = "";
  liveItems.value = [];
  selected.value = { summary, thread: { turns: [] } };
  sessionMessage.value = "";
  await nextTick();
  await scrollTimelineToBottom();
  openingSessionId.value = null;
  try {
    const content = await api.sessionContent(id);
    if (requestId === sessionContentRequest && selected.value?.summary.id === id) {
      selected.value = content;
      displayedTurnCount.value = 10;
      await nextTick();
      await scrollTimelineToBottom();
    }
  } catch (reason) {
    if (requestId === sessionContentRequest && selected.value?.summary.id === id) {
      error.value = reason instanceof Error ? reason.message : "读取会话内容失败，请稍后重试";
    }
  } finally {
    if (requestId === sessionContentRequest) sessionContentLoading.value = false;
  }
}

function closeSession() {
  sessionContentRequest++;
  selected.value = null;
  sessionContentLoading.value = false;
  liveItems.value = [];
  sessionAttachments.value = [];
  displayedTurnCount.value = 10;
}

async function loadEarlierSessionTurns() {
  const element = timeline.value;
  if (!element || !hasEarlierSessionTurns.value) return;
  const previousHeight = element.scrollHeight;
  const previousTop = element.scrollTop;
  displayedTurnCount.value += 10;
  await nextTick();
  element.scrollTop = previousTop + element.scrollHeight - previousHeight;
}

function handleGlobalKeydown(event: KeyboardEvent) {
  if (event.key === "Escape" && selected.value) closeSession();
  if ((event.ctrlKey || event.metaKey) && event.key.toLocaleLowerCase() === "k") {
    event.preventDefault();
    if (tab.value !== "home" && tab.value !== "history") tab.value = "home";
    void nextTick(() => searchInput.value?.focus());
  }
}

function showFeatureNotice(feature: string) {
  featureNotice.value = feature + "尚未接入后端能力，当前版本先保留入口。";
}

function openActivity(activity: (typeof recentActivities.value)[number]) {
  if (activity.kind === "session") void openSession(activity.session.id);
  else tab.value = "tasks";
}
onMounted(() => window.addEventListener("keydown", handleGlobalKeydown));
onUnmounted(() => window.removeEventListener("keydown", handleGlobalKeydown));

async function sendSessionMessage() {
  const message = sessionMessage.value.trim();
  if (!selected.value || (!message && !sessionAttachments.value.some(item => item.status === "completed")) || sendingSessionMessage.value || sessionRunning.value || uploadingSessionAttachment.value) return;
  const threadId = selected.value.summary.id;
  const attachments = sessionAttachments.value.filter(item => item.status === "completed");
  const optimisticText = [message, ...attachments.map(item => "附件：" + item.name)].filter(Boolean).join("\n");
  liveItems.value.push({ id: "local-" + Date.now(), kind: "user", label: "你", text: optimisticText, status: "completed" });
  sessionMessage.value = "";
  sessionAttachments.value = [];
  sendingSessionMessage.value = true;
  sessionRunning.value = true;
  error.value = "";
  await scrollTimelineToBottom();
  try {
    await api.sendSessionMessage(threadId, message, attachments.map(item => item.id));
  } catch (reason) {
    sessionRunning.value = false;
    const optimistic = liveItems.value.find((item) => item.id.startsWith("local-"));
    if (optimistic) optimistic.status = "failed";
    sessionAttachments.value = attachments;
    error.value = reason instanceof Error ? reason.message : "继续会话失败";
  } finally { sendingSessionMessage.value = false; }
}

async function handleCodexEvent(method: string, parameters: unknown) {
  if (!selected.value) return;
  const value = parameters as Record<string, unknown> | null;
  const threadId = String(value?.threadId || ((value?.thread as Record<string, unknown> | undefined)?.id || ""));
  if (threadId && threadId !== selected.value.summary.id) return;

  if (method === "turn/started") sessionRunning.value = true;
  else if (method === "item/started" || method === "item/completed") {
    const item = value?.item as ThreadItem | undefined;
    if (item && item.type !== "userMessage") upsertLiveItem(normalizeItem(item, method === "item/completed" ? "completed" : "running"), method !== "item/completed");
  } else if (method === "item/agentMessage/delta") appendLiveDelta(value, "agent", "Codex", "delta");
  else if (method === "item/reasoning/summaryTextDelta") appendLiveDelta(value, "reasoning", "思考摘要", "delta");
  else if (method === "item/plan/delta") appendLiveDelta(value, "reasoning", "执行计划", "delta");
  else if (method === "item/commandExecution/outputDelta") appendLiveDelta(value, "command", "命令输出", "delta");
  else if (method === "item/fileChange/outputDelta") appendLiveDelta(value, "file", "文件修改", "delta");
  else if (method === "item/fileChange/patchUpdated") {
    const id = String(value?.itemId || "file-change");
    const changes = formatValue(value?.changes);
    upsertLiveItem({ id, turnId: String(value?.turnId || ""), kind: "file", label: "文件修改", text: changes, status: "running" });
  } else if (method === "item/mcpToolCall/progress") {
    const id = String(value?.itemId || "mcp-tool");
    upsertLiveItem({ id, turnId: String(value?.turnId || ""), kind: "mcp", label: "MCP 工具", text: String(value?.message || "正在调用…"), status: "running" });
  }

  if (method === "turn/completed") {
    sessionRunning.value = false;
    selected.value = await api.sessionContent(selected.value.summary.id);
    liveItems.value = [];
  }
  await scrollTimelineToBottom();
}

function appendLiveDelta(value: Record<string, unknown> | null, kind: TimelineKind, label: string, field: string) {
  const id = String(value?.itemId || kind + "-stream");
  const delta = String(value?.[field] || "");
  const existing = liveItems.value.find((item) => item.id === id);
  if (existing) existing.text += delta;
  else liveItems.value.push({ id, turnId: String(value?.turnId || ""), kind, label, text: delta, status: "running" });
}

function upsertLiveItem(item: LiveTimelineItem, preserveStream = false) {
  const index = liveItems.value.findIndex((value) => value.id === item.id);
  if (index < 0) liveItems.value.push(item);
  else {
    const current = liveItems.value[index];
    liveItems.value[index] = { ...current, ...item, title: item.title || current.title, text: preserveStream && current.text && !item.text ? current.text : item.text || current.text };
  }
}

function normalizeItem(item: ThreadItem, status?: LiveTimelineItem["status"]): LiveTimelineItem {
  const kind = timelineKind(item);
  return {
    id: item.id,
    kind,
    label: timelineLabel(item),
    title: timelineTitle(item),
    text: itemText(item),
    status: status || itemStatus(item)
  };
}

async function scrollTimelineToBottom() {
  await nextTick();
  const element = timeline.value;
  if (element) element.scrollTop = element.scrollHeight;
}

function openCreateMenu() {
  showCreateMenu.value = true;
}

function chooseCreateType(type: "session" | "task" | "project") {
  showCreateMenu.value = false;
  if (type === "session") showSessionForm.value = true;
  if (type === "task") showTaskForm.value = true;
  if (type === "project") openProjectForm("standalone");
}

function openProjectForm(context: "standalone" | "session" | "task") {
  projectCreationContext.value = context;
  if (context === "session") showSessionForm.value = false;
  showProjectForm.value = true;
}

async function createProject() {
  projectFormError.value = "";
  if (!projectForm.name.trim() || !projectForm.workingDirectory.trim()) {
    projectFormError.value = "请输入项目名称和电脑目录";
    return;
  }
  savingProject.value = true;
  try {
    const created = await api.createProject({
      name: projectForm.name.trim(),
      workingDirectory: projectForm.workingDirectory.trim(),
      createDirectory: projectForm.createDirectory
    });
    projects.value.unshift(created);
    projectForm.name = "";
    showProjectForm.value = false;
    if (projectCreationContext.value === "session") {
      sessionForm.projectPath = created.workingDirectory;
      showSessionForm.value = true;
    } else if (projectCreationContext.value === "task" && associatingTask.value) {
      await associateTask(created.workingDirectory);
    } else {
      tab.value = "projects";
    }
    projectCreationContext.value = "standalone";
  } catch (reason) {
    projectFormError.value = reason instanceof Error ? reason.message : "创建项目失败";
  } finally { savingProject.value = false; }
}

async function createSession() {
  sessionFormError.value = "";
  const completedAttachments = newSessionAttachments.value.filter(item => item.status === "completed");
  if (!sessionForm.message.trim() && !completedAttachments.length) {
    sessionFormError.value = newSessionAttachments.value.some(item => item.status === "failed") ? "附件上传失败，请删除失败附件后重新选择" : "请输入第一条消息或选择附件";
    return;
  }
  creatingSession.value = true;
  try {
    selected.value = await api.createSession({
      projectPath: sessionForm.projectPath,
      message: sessionForm.message.trim(),
      attachmentIds: completedAttachments.map(item => item.id)
    });
    sessionForm.message = "";
    newSessionAttachments.value = [];
    showSessionForm.value = false;
    await refresh();
    await scrollTimelineToBottom();
  } catch (reason) {
    sessionFormError.value = reason instanceof Error ? reason.message : "创建对话失败";
  } finally { creatingSession.value = false; }
}


function createLocalAttachmentId() {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") return "upload-" + crypto.randomUUID();
  return "upload-" + Date.now().toString(36) + "-" + Math.random().toString(36).slice(2);
}

function handleAttachmentInput(event: Event, target: "new" | "session") {
  const input = event.target as HTMLInputElement;
  void uploadAttachments(input.files, target);
  input.value = "";
}

async function uploadAttachments(files: FileList | null, target: "new" | "session") {
  if (!files?.length) return;
  const uploading = target === "new" ? uploadingNewSessionAttachment : uploadingSessionAttachment;
  const values = target === "new" ? newSessionAttachments : sessionAttachments;
  const pendingUploads = Array.from(files).map(file => {
    const localId = createLocalAttachmentId();
    const pending: PendingAttachment = {
      id: "",
      localId,
      name: file.name,
      contentType: file.type || "application/octet-stream",
      size: file.size,
      isImage: file.type.startsWith("image/"),
      progress: 0,
      status: "uploading"
    };
    values.value.push(pending);
    return { file, localId };
  });
  uploading.value = true;
  await nextTick();
  await new Promise(resolve => requestAnimationFrame(() => resolve(undefined)));
  for (const { file, localId } of pendingUploads) {
    try {
      const uploaded = await api.uploadAttachment(file, progress => {
        const current = values.value.find(item => item.localId === localId);
        if (current) current.progress = progress;
      });
      const index = values.value.findIndex(item => item.localId === localId);
      if (index >= 0) values.value[index] = { ...uploaded, localId, progress: 100, status: "completed" };
    } catch (reason) {
      const current = values.value.find(item => item.localId === localId);
      if (current) {
        current.status = "failed";
        current.error = reason instanceof Error ? reason.message : "附件上传失败";
      }
    }
  }
  uploading.value = values.value.some(item => item.status === "uploading");
}

function removeAttachment(target: "new" | "session", localId: string) {
  const values = target === "new" ? newSessionAttachments : sessionAttachments;
  values.value = values.value.filter(item => item.localId !== localId);
}

function formatFileSize(size: number) {
  if (size < 1024) return size + " B";
  if (size < 1024 * 1024) return Math.round(size / 1024) + " KB";
  return (size / 1024 / 1024).toFixed(1) + " MB";
}

function openTaskAssociation(task: PocketTask) {
  associatingTask.value = task;
}

async function associateTask(projectPath: string) {
  if (!associatingTask.value || !projectPath) return;
  try {
    const updated = await api.updateTask(associatingTask.value.id, { projectPath });
    upsertTask(updated);
    associatingTask.value = null;
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : "关联项目失败";
  }
}

async function createTask() {
  taskFormError.value = "";
  if (!taskForm.title.trim()) {
    taskFormError.value = "请输入任务标题";
    return;
  }
  if (!taskForm.prompt.trim()) {
    taskFormError.value = "请输入任务描述";
    return;
  }

  savingTask.value = true;
  try {
    const created = await api.createTask({
      title: taskForm.title.trim(),
      prompt: taskForm.prompt.trim(),
      projectPath: taskForm.projectPath.trim()
    });
    upsertTask(created);
    Object.assign(taskForm, { title: "", prompt: "", projectPath: "" });
    showTaskForm.value = false;
    tab.value = "tasks";
  } catch (reason) {
    taskFormError.value = reason instanceof Error ? reason.message : "创建任务失败";
  } finally {
    savingTask.value = false;
  }
}

async function controlTask(task: PocketTask, action: "start" | "interrupt") {
  try {
    upsertTask(action === "start" ? await api.startTask(task.id) : await api.interruptTask(task.id));
  } catch (reason) { error.value = reason instanceof Error ? reason.message : "控制任务失败"; }
}

function upsertTask(task: PocketTask) {
  const index = tasks.value.findIndex((item) => item.id === task.id);
  if (index >= 0) tasks.value[index] = task;
  else tasks.value.unshift(task);
  tasks.value.sort((left, right) => right.updatedAt.localeCompare(left.updatedAt));
}

function openProject(project: Project) {
  selectedProject.value = project;
  clearSearch();
  tab.value = "history";
  void refresh();
}

function clearProjectFilter() {
  selectedProject.value = null;
  clearSearch();
  void refresh();
}

function itemText(item: ThreadItem) {
  if (item.type === "agentMessage" || item.type === "plan") return String(item.text || "");
  if (item.type === "reasoning") {
    const summary = Array.isArray(item.summary) ? item.summary.join("\n") : String(item.summary || "");
    return summary || "正在分析当前任务…";
  }
  if (item.type === "commandExecution") {
    const command = String(item.command || "");
    const output = String(item.aggregatedOutput || "");
    return [command && "$ " + command, output].filter(Boolean).join("\n");
  }
  if (item.type === "fileChange") return formatValue(item.changes) || "文件变更已完成";
  if (item.type === "mcpToolCall") {
    const result = item.error ? "错误：" + formatValue(item.error) : item.result ? formatValue(item.result) : "";
    return [formatValue(item.arguments), result].filter(Boolean).join("\n") || "工具调用中…";
  }
  if (item.type === "dynamicToolCall") return [formatValue(item.arguments), formatValue(item.contentItems)].filter(Boolean).join("\n") || "工具调用中…";
  if (item.type === "webSearch") return String(item.query || formatValue(item.action) || "网页搜索");
  if (item.type === "userMessage" && Array.isArray(item.content)) {
    return item.content.map((part) => typeof part === "object" && part && "text" in part ? String((part as { text: unknown }).text) : "").filter(Boolean).join("\n");
  }
  if (item.type === "contextCompaction") return "上下文已压缩";
  return "";
}

function timelineKind(item: ThreadItem): TimelineKind {
  if (item.type === "userMessage") return "user";
  if (item.type === "agentMessage") return "agent";
  if (item.type === "reasoning" || item.type === "plan") return "reasoning";
  if (item.type === "commandExecution") return "command";
  if (item.type === "fileChange") return "file";
  if (item.type === "mcpToolCall") return "mcp";
  return "tool";
}

function timelineLabel(item: ThreadItem) {
  return ({ userMessage: "你", agentMessage: "Codex", reasoning: "思考摘要", plan: "执行计划", commandExecution: "命令", fileChange: "文件修改", mcpToolCall: "MCP 工具", dynamicToolCall: "工具", webSearch: "网页搜索", contextCompaction: "上下文" } as Record<string, string>)[item.type] || "工具活动";
}

function timelineTitle(item: ThreadItem) {
  if (item.type === "commandExecution") return String(item.cwd || "");
  if (item.type === "mcpToolCall") return [item.server, item.tool].filter(Boolean).join(" / ");
  if (item.type === "dynamicToolCall") return [item.namespace, item.tool].filter(Boolean).join(" / ");
  if (item.type === "fileChange" && Array.isArray(item.changes)) return item.changes.length + " 个文件";
  return "";
}

function itemStatus(item: ThreadItem): LiveTimelineItem["status"] {
  const status = String(item.status || "completed").toLowerCase();
  if (status.includes("fail") || item.error) return "failed";
  if (status.includes("progress") || status.includes("running")) return "running";
  return "completed";
}

function formatValue(value: unknown) {
  if (value === null || value === undefined || value === "") return "";
  if (typeof value === "string") return value;
  try { return JSON.stringify(value, null, 2); }
  catch { return String(value); }
}

function timeAgo(value: number | string) {
  const timestamp = typeof value === "number" ? value * 1000 : new Date(value).getTime();
  const diff = Math.max(0, Date.now() - timestamp);
  if (diff < 60_000) return "刚刚";
  if (diff < 3_600_000) return Math.floor(diff / 60_000) + " 分钟前";
  if (diff < 86_400_000) return Math.floor(diff / 3_600_000) + " 小时前";
  if (diff < 604_800_000) return Math.floor(diff / 86_400_000) + " 天前";
  return new Date(timestamp).toLocaleDateString("zh-CN");
}

function statusName(status: string) {
  return ({ draft: "草稿", queued: "排队", running: "运行中", waitingApproval: "待审批", completed: "已完成", failed: "失败", cancelled: "已取消" } as Record<string, string>)[status] || status;
}
</script>

<template>
  <div v-if="authReady && !authenticated" class="login-shell">
    <section class="login-card">
      <div class="login-mark">⌁</div>
      <span class="eyebrow">CODEX 移动控制台</span>
      <h1>Codex 工作台</h1>
      <p>输入启动脚本生成的访问密码。</p>
      <form @submit.prevent="login">
        <input v-model="password" type="password" autocomplete="current-password" placeholder="访问密码" autofocus>
        <button class="primary-button full" type="submit">登录</button>
      </form>
      <div v-if="loginError" class="error-card">{{ loginError }}</div>
    </section>
  </div>
  <div v-else-if="!authReady" class="login-shell"><div class="empty">正在检查登录状态…</div></div>
  <div v-else class="app-shell">
    <aside class="workspace-sidebar">
      <div class="brand"><span class="brand-mark">⌁</span><strong>Codex</strong></div>
      <nav class="side-nav" aria-label="主要导航">
        <button :class="{ active: tab === 'home' }" @click="tab = 'home'"><span>▦</span><strong>工作台</strong></button>
        <button :class="{ active: tab === 'projects' }" @click="tab = 'projects'"><span>□</span><strong>项目</strong></button>
        <button :class="{ active: tab === 'history' }" @click="tab = 'history'"><span>◷</span><strong>对话</strong><small>{{ sessions.length }}</small></button>
        <button :class="{ active: tab === 'tasks' }" @click="tab = 'tasks'"><span>✓</span><strong>任务</strong><small>{{ tasks.length }}</small></button>
      </nav>
      <div class="side-label">开发</div>
      <nav class="side-nav side-nav-muted">
        <button @click="showFeatureNotice('智能体')"><span>◉</span><strong>智能体</strong><small>待接入</small></button>
        <button @click="showFeatureNotice('终端')"><span>›_</span><strong>终端</strong></button>
        <button @click="showFeatureNotice('Git 中心')"><span>⑂</span><strong>Git 中心</strong></button>
        <button @click="showFeatureNotice('文件管理')"><span>▤</span><strong>文件</strong></button>
      </nav>
      <div class="side-label">平台</div>
      <nav class="side-nav side-nav-muted">
        <button @click="showFeatureNotice('MCP 服务')"><span>◔</span><strong>MCP 服务</strong></button>
        <button @click="showFeatureNotice('技能市场')"><span>☆</span><strong>技能市场</strong></button>
        <button @click="showFeatureNotice('自动化')"><span>ϟ</span><strong>自动化</strong></button>
      </nav>      <div class="side-label">系统</div>
      <nav class="side-nav">
        <button :class="{ active: tab === 'settings' }" @click="tab = 'settings'"><span>⚙</span><strong>设置</strong></button>
      </nav>
      <div class="workspace-profile"><span>北</span><div><strong>个人工作区</strong><small>移动控制台</small></div></div>
    </aside>

    <header class="topbar">
      <div class="topbar-title"><span class="eyebrow">CODEX 工作空间</span><h1>{{ tab === 'home' ? '工作台' : tab === 'projects' ? '项目' : tab === 'history' ? '对话' : tab === 'tasks' ? '任务' : '设置' }}</h1></div>
      <form v-if="tab === 'home' || tab === 'history'" class="top-search" @submit.prevent="performSearch">
        <span>⌕</span><input ref="searchInput" v-model="search" placeholder="搜索或输入命令…"><button type="submit" :disabled="searching">{{ searching ? '搜索中' : '搜索' }}</button>
      </form>
      <button class="status-pill" @click="refresh"><span :class="['dot', host?.connected && 'online']" />{{ host?.connected ? 'Codex 在线' : 'Codex 离线' }}</button>
    </header>

    <main>
      <div v-if="featureNotice" class="feature-notice"><span>{{ featureNotice }}</span><button @click="featureNotice = ''">×</button></div>
      <div v-if="activeSearch && (tab === 'home' || tab === 'history')" class="search-summary"><span>&#20851;&#38190;&#35789; {{ activeSearch }}&#65306;&#25214;&#21040; {{ searchResultCount }} &#26465;&#32467;&#26524;</span><button @click="clearSearch">&#28165;&#38500;</button></div>
      <div v-if="error" class="error-card">{{ error }}</div>

      <template v-if="tab === 'home'">
        <section class="hero dashboard-heading"><div><h2>工作台</h2><p>欢迎回来，这是你的工作空间概览</p></div><button class="hero-create" @click="openCreateMenu">＋ 新建</button></section>
        <section class="metrics-grid">
          <article class="metric-card green"><span>运行中的任务</span><strong>{{ runningTasks.length }}</strong><small>实时同步任务状态</small></article>
          <article class="metric-card blue"><span>已完成任务</span><strong>{{ completedTasks.length }}</strong><small>共 {{ tasks.length }} 个任务</small></article>
          <article class="metric-card violet"><span>历史对话</span><strong>{{ sessions.length }}</strong><small>{{ projects.length }} 个项目工作区</small></article>
          <article class="metric-card amber"><span>Codex 状态</span><strong>{{ host?.connected ? '在线' : '离线' }}</strong><small>{{ host?.codexVersion || '正在连接' }}</small></article>
        </section>
        <section class="dashboard-grid">
          <article class="dashboard-panel active-panel">
            <header><h3>活跃会话</h3><button @click="tab = 'history'">查看全部</button></header>
            <div v-if="loading" class="panel-empty">正在读取 Codex 历史…</div>
            <div v-else-if="!activeSessions.length" class="panel-empty">暂无历史会话</div>
            <button v-for="session in activeSessions" v-else :key="session.id" class="active-session-row" @click="openSession(session.id)">
              <span :class="['activity-dot', session.status]" />
              <div><strong>{{ session.title }}</strong><small>{{ session.projectName }} · {{ timeAgo(session.updatedAt) }}</small></div>
              <em>{{ session.status === 'active' || session.status === 'running' ? '运行中' : '可继续' }}</em>
            </button>
          </article>
          <article class="dashboard-panel activity-panel">
            <header><h3>最近动态</h3><button @click="refresh">刷新</button></header>
            <div v-if="!recentActivities.length" class="panel-empty">暂无动态</div>
            <button v-for="activity in recentActivities" v-else :key="activity.id" class="activity-row" @click="openActivity(activity)">
              <span>{{ timeAgo(Math.floor(activity.timestamp / 1000)) }}</span>
              <i :class="activity.kind" />
              <div><strong>{{ activity.kind === 'session' ? 'Codex 对话' : '任务' }}</strong><p>{{ activity.title }}</p></div>
            </button>
          </article>
        </section>
        <article class="dashboard-panel health-panel">
          <header><h3>项目健康度</h3><div class="health-legend"><span><i class="healthy" />健康</span><span><i class="warning" />注意</span><span><i class="critical" />较少活动</span></div></header>
          <div v-if="!projectHealth.length" class="panel-empty">暂无项目</div>
          <div v-else class="health-grid">
            <button v-for="item in projectHealth" :key="item.project.id" class="health-card" @click="openProject(item.project)">
              <div><strong>{{ item.project.name }}</strong><span :class="item.state">{{ item.label }}</span></div>
              <p>{{ item.project.sessionCount }} 个会话 · {{ timeAgo(item.project.lastActiveAt) }}</p>
              <div class="health-progress"><i :class="item.state" :style="{ width: item.score + '%' }" /></div>
              <small>{{ item.score }}% 活跃度</small>
            </button>
          </div>
        </article>
      </template>
      <template v-else-if="tab === 'tasks'">
        <div class="page-heading row-heading"><div><h2>任务</h2><p>独立于 thread 的工作目标</p></div><button class="primary-button" @click="openCreateMenu">＋ 新建</button></div>
        <TaskList :items="tasks" @control="controlTask" @associate="openTaskAssociation" />
      </template>

      <template v-else-if="tab === 'projects'">
        <div class="page-heading"><h2>项目</h2><p>按 Codex 会话工作目录自动聚合</p></div>
        <ProjectGrid :items="filteredProjects" @open="openProject" />
      </template>

      <template v-else-if="tab === 'history'">
        <div class="page-heading"><h2>{{ selectedProject ? selectedProject.name : '历史会话' }}</h2><p>{{ selectedProject ? selectedProject.workingDirectory : sessions.length + ' 个最近会话' }}</p><button v-if="selectedProject" class="clear-filter" @click="clearProjectFilter">查看全部历史</button></div>
        <SessionList :items="visibleSessions" :loading="loading" :opening-id="openingSessionId" @open="openSession" />
      </template>

      <template v-else>
        <div class="page-heading"><h2>设置</h2><p>私有移动控制台</p></div>
        <div class="settings-card">
          <div class="setting-row"><span>Codex 状态</span><strong>{{ host?.connected ? '已连接' : '未连接' }}</strong></div>
          <div class="setting-row"><span>SignalR</span><strong>{{ realtimeConnected ? '已连接' : '重连中' }}</strong></div>
          <div class="setting-row"><span>数据库</span><strong>SQLite / Tasks</strong></div>
          <div class="setting-row"><span>远程访问</span><strong>Tailscale 私网</strong></div>
        </div>
        <section class="wechat-monitor-card">
          <header>
            <div><span class="eyebrow">WECHAT MONITOR</span><h3>微信消息监控</h3></div>
            <span :class="['monitor-status', weChatMonitor?.running ? 'running' : 'stopped']">{{ weChatMonitor?.running ? '运行中' : '已停止' }}</span>
          </header>
          <label class="monitor-thread">提醒目标会话
            <select v-model="weChatThreadId" :disabled="weChatMonitor?.running || weChatMonitorBusy">
              <option value="" disabled>请选择 Codex 会话</option>
              <option v-for="session in sessions" :key="session.id" :value="session.id">{{ session.title }} · {{ session.projectName }}</option>
            </select>
          </label>
          <label class="monitor-thread">扫描间隔
            <select v-model.number="weChatIntervalSeconds" :disabled="weChatMonitor?.running || weChatMonitorBusy">
              <option :value="15">15 秒</option>
              <option :value="30">30 秒</option>
              <option :value="60">1 分钟</option>
              <option :value="120">2 分钟</option>
              <option :value="300">5 分钟</option>
              <option :value="600">10 分钟</option>
            </select>
          </label>
          <div class="monitor-actions">
            <button class="secondary-button" :disabled="weChatMonitorBusy" @click="refreshWeChatInstances">刷新实例</button>
            <button v-if="!weChatMonitor?.running" class="primary-button" :disabled="weChatMonitorBusy || !weChatThreadId" @click="startWeChatMonitor">{{ weChatMonitorBusy ? '处理中…' : '启动监控' }}</button>
            <button v-else class="danger-button" :disabled="weChatMonitorBusy" @click="stopWeChatMonitor">{{ weChatMonitorBusy ? '处理中…' : '停止监控' }}</button>
          </div>
          <div class="monitor-summary">
            <span>每 {{ weChatMonitor?.intervalSeconds || 60 }} 秒扫描</span>
            <span>待发送 {{ weChatMonitor?.pendingMessageCount || 0 }} 条</span>
            <span>最近扫描 {{ weChatMonitor?.lastScanAt ? new Date(weChatMonitor.lastScanAt).toLocaleTimeString() : '暂无' }}</span>
          </div>
          <div v-if="weChatMonitorError || weChatMonitor?.lastError" class="monitor-error">{{ weChatMonitorError || weChatMonitor?.lastError }}</div>
          <div v-if="!weChatMonitor?.instances.length" class="monitor-empty">未发现已打开的微信主窗口</div>
          <div v-else class="wechat-instance-list">
            <article v-for="instance in weChatMonitor.instances" :key="instance.id" class="wechat-instance">
              <div><strong>{{ instance.displayName }}</strong><small>PID {{ instance.processId }} · {{ instance.windowState === 'minimized' ? '已最小化' : '窗口可见' }}</small></div>
              <span :class="instance.online && instance.loggedIn ? 'online' : 'offline'">{{ instance.online ? (instance.loggedIn ? '已登录' : '待识别') : '离线' }}</span>
              <input class="wechat-alias" :placeholder="'自定义名称，例如：工作微信'" :disabled="weChatMonitor?.running" @change="renameWeChatInstance(instance.id, $event)">
              <p>未读 {{ instance.lastUnreadCount }} 条 · 最近扫描 {{ instance.lastScanAt ? new Date(instance.lastScanAt).toLocaleTimeString() : '暂无' }}</p>
              <em v-if="instance.lastError">{{ instance.lastError }}</em>
            </article>
          </div>
          <div class="monitor-warning">发现新增数字未读后会依次打开聊天读取完整消息，因此会将这些微信消息标为已读。多个微信窗口扫描时可能短暂闪现。</div>
        </section>
        <section class="wechat-monitor-card wechat-reply-card">
          <header><div><span class="eyebrow">WECHAT REPLY</span><h3>微信联系人回复</h3></div></header>
          <label class="monitor-thread">发送账号
            <select v-model="weChatReplyInstanceId" :disabled="weChatReplyBusy" @change="weChatReplyContacts = []; weChatReplyContact = ''">
              <option value="" disabled>请选择微信账号</option>
              <option v-for="instance in weChatMonitor?.instances.filter(item => item.online) || []" :key="instance.id" :value="instance.id">{{ instance.displayName }}</option>
            </select>
          </label>
          <div class="monitor-actions"><button class="secondary-button" :disabled="weChatReplyBusy || !weChatReplyInstanceId" @click="refreshWeChatContacts">{{ weChatReplyBusy ? '处理中…' : '刷新联系人' }}</button></div>
          <label class="monitor-thread">联系人或群聊
            <input v-model="weChatReplyContact" list="wechat-contact-options" maxlength="80" placeholder="选择或输入当前会话列表中的名称">
            <datalist id="wechat-contact-options"><option v-for="contact in weChatReplyContacts" :key="contact" :value="contact" /></datalist>
          </label>
          <label class="monitor-thread">回复内容
            <textarea v-model="weChatReplyMessage" maxlength="1000" rows="4" placeholder="请输入要发送的文本消息" />
          </label>
          <div class="reply-counter">{{ weChatReplyMessage.length }} / 1000</div>
          <div v-if="weChatReplyError" class="monitor-error">{{ weChatReplyError }}</div>
          <div v-if="weChatReplySuccess" class="reply-success">{{ weChatReplySuccess }}</div>
          <button class="primary-button full" :disabled="weChatReplyBusy || !weChatReplyInstanceId || !weChatReplyContact.trim() || !weChatReplyMessage.trim()" @click="sendWeChatReply">{{ weChatReplyBusy ? '发送中…' : '确认并发送' }}</button>
          <div class="monitor-warning">发送前会再次显示账号、联系人和正文确认。程序会核对微信聊天标题，无法精确匹配时不会发送。</div>
        </section>
        <button class="logout-button" @click="logout">退出登录</button>
        <div class="notice">当前支持历史项目与会话、独立任务、启动、中断和实时状态。手机审批界面尚未接入，因此任务按工作区写入且不弹出审批。</div>
      </template>
    </main>

    <nav class="bottom-nav">
      <button :class="{ active: tab === 'home' }" @click="tab = 'home'"><span>⌂</span><small>首页</small></button>
      <button :class="{ active: tab === 'tasks' }" @click="tab = 'tasks'"><span>✓</span><small>任务</small></button>
      <button :class="{ active: tab === 'projects' }" @click="tab = 'projects'"><span>▦</span><small>项目</small></button>
      <button :class="{ active: tab === 'history' }" @click="tab = 'history'"><span>◷</span><small>历史</small></button>
      <button :class="{ active: tab === 'settings' }" @click="tab = 'settings'"><span>⚙</span><small>设置</small></button>
    </nav>

    <div v-if="showCreateMenu" class="sheet-backdrop" @click="showCreateMenu = false">
      <section class="sheet create-menu-sheet" @click.stop><div class="sheet-handle" /><header><div><span class="eyebrow">CREATE</span><h2>&#26032;&#24314;</h2></div><button @click="showCreateMenu = false">&#215;</button></header>
        <div class="create-choice-grid"><button @click="chooseCreateType('session')"><span>&#9719;</span><strong>&#26032;&#24314;&#23545;&#35805;</strong><small>&#36873;&#25321;&#39033;&#30446;&#21518;&#31435;&#21363;&#21644; Codex &#23545;&#35805;</small></button><button @click="chooseCreateType('task')"><span>&#10003;</span><strong>&#26032;&#24314;&#20219;&#21153;</strong><small>&#21487;&#20197;&#20808;&#20445;&#23384;&#65292;&#31245;&#21518;&#20851;&#32852;&#39033;&#30446;</small></button><button @click="chooseCreateType('project')"><span>&#9638;</span><strong>&#26032;&#24314;&#39033;&#30446;</strong><small>&#30331;&#35760;&#25110;&#21019;&#24314;&#30005;&#33041;&#19978;&#30340;&#39033;&#30446;&#30446;&#24405;</small></button></div>
      </section>
    </div>

    <div v-if="showProjectForm" class="sheet-backdrop" @click="showProjectForm = false">
      <section class="sheet form-sheet" @click.stop><div class="sheet-handle" /><header><div><span class="eyebrow">NEW PROJECT</span><h2>&#21019;&#24314;&#39033;&#30446;</h2></div><button @click="showProjectForm = false">&#215;</button></header>
        <form @submit.prevent="createProject"><label>&#39033;&#30446;&#21517;&#31216;<input v-model="projectForm.name" placeholder="&#20363;&#22914;&#65306;my-app" @input="projectFormError = ''"></label><label>&#30005;&#33041;&#30446;&#24405;<input v-model="projectForm.workingDirectory" placeholder="D:\Desktop\Projects\my-app" @input="projectFormError = ''"></label><small v-if="projectRoots.length" class="form-hint">&#20801;&#35768;&#30340;&#26681;&#30446;&#24405;&#65306;{{ projectRoots.join(' / ') }}</small><label class="check-row"><input v-model="projectForm.createDirectory" type="checkbox"><span>&#30446;&#24405;&#19981;&#23384;&#22312;&#26102;&#33258;&#21160;&#21019;&#24314;</span></label><div v-if="projectFormError" class="form-error">{{ projectFormError }}</div><button class="primary-button full" type="submit" :disabled="savingProject">{{ savingProject ? '????' : '????' }}</button></form>
      </section>
    </div>

    <div v-if="showSessionForm" class="sheet-backdrop" @click="showSessionForm = false">
      <section class="sheet form-sheet" @click.stop><div class="sheet-handle" /><header><div><span class="eyebrow">NEW CHAT</span><h2>&#26032;&#24314;&#23545;&#35805;</h2></div><button @click="showSessionForm = false">&#215;</button></header>
        <form @submit.prevent="createSession"><label>&#24037;&#20316;&#30446;&#24405;&#65288;&#21487;&#36873;&#65289;<select v-model="sessionForm.projectPath"><option value="">&#40664;&#35748;&#24037;&#20316;&#21306;&#22495;</option><option v-for="project in projects" :key="project.id" :value="project.workingDirectory">{{ project.name }}</option></select></label><small class="form-hint">&#19981;&#36873;&#25321;&#39033;&#30446;&#26102;&#65292;&#23558;&#20351;&#29992;&#40664;&#35748;&#24037;&#20316;&#21306;&#22495;&#30452;&#25509;&#24320;&#22987;&#23545;&#35805;&#12290;</small><button class="inline-create" type="button" @click="openProjectForm('session')">&#65291; &#26032;&#24314;&#39033;&#30446;&#65288;&#21487;&#36873;&#65289;</button><label>&#31532;&#19968;&#26465;&#28040;&#24687;<textarea v-model="sessionForm.message" rows="6" placeholder="&#21578;&#35785; Codex &#38656;&#35201;&#23436;&#25104;&#20160;&#20040;" @input="sessionFormError = ''"></textarea></label><label class="attachment-picker"><input type="file" multiple accept="image/*,.pdf,.txt,.md,.json,.csv,.doc,.docx,.xls,.xlsx,.zip" @change="handleAttachmentInput($event, 'new')"><span>{{ uploadingNewSessionAttachment ? '上传中…' : '＋ 添加图片或文件' }}</span></label><div v-if="newSessionAttachments.length" class="attachment-list"><div v-for="attachment in newSessionAttachments" :key="attachment.localId" :class="['attachment-chip', attachment.status]"><span>{{ attachment.isImage ? '图片' : '文件' }}</span><div><strong>{{ attachment.name }}</strong><small v-if="attachment.status === 'uploading'">正在上传 {{ attachment.progress }}%</small><small v-else-if="attachment.status === 'failed'" class="upload-error">{{ attachment.error || '上传失败' }}</small><small v-else>已上传 · {{ formatFileSize(attachment.size) }}</small><div v-if="attachment.status === 'uploading'" class="upload-progress"><i :style="{ width: attachment.progress + '%' }" /></div></div><button type="button" @click="removeAttachment('new', attachment.localId)">×</button></div></div><div v-if="sessionFormError" class="form-error">{{ sessionFormError }}</div><button class="primary-button full" type="submit" :disabled="creatingSession || uploadingNewSessionAttachment">{{ creatingSession ? '&#21019;&#24314;&#20013;&#8230;' : '&#21019;&#24314;&#24182;&#21457;&#36865;' }}</button></form>
      </section>
    </div>

    <div v-if="associatingTask && !showProjectForm" class="sheet-backdrop" @click="associatingTask = null">
      <section class="sheet form-sheet" @click.stop><div class="sheet-handle" /><header><div><span class="eyebrow">LINK PROJECT</span><h2>&#20851;&#32852;&#39033;&#30446;</h2></div><button @click="associatingTask = null">&#215;</button></header><p class="sheet-description">{{ associatingTask.title }}</p><div class="project-link-list"><button v-for="project in projects" :key="project.id" @click="associateTask(project.workingDirectory)"><strong>{{ project.name }}</strong><small>{{ project.workingDirectory }}</small></button></div><button class="inline-create" @click="openProjectForm('task')">&#65291; &#21019;&#24314;&#26032;&#39033;&#30446;</button>
      </section>
    </div>

    <div v-if="showTaskForm" class="sheet-backdrop" @click="showTaskForm = false">
      <section class="sheet form-sheet" @click.stop><div class="sheet-handle" /><header><div><span class="eyebrow">NEW TASK</span><h2>创建任务</h2></div><button @click="showTaskForm = false">×</button></header>
        <form @submit.prevent="createTask"><label>标题<input v-model="taskForm.title" maxlength="200" placeholder="例如：优化登录模块" @input="taskFormError = ''"></label><label>项目目录（可选）<select v-model="taskForm.projectPath"><option value="">暂不关联项目</option><option v-for="project in projects" :key="project.id" :value="project.workingDirectory">{{ project.name }}</option></select></label><label>任务描述<textarea v-model="taskForm.prompt" rows="6" placeholder="描述希望 Codex 完成的目标" @input="taskFormError = ''"></textarea></label><div v-if="taskFormError" class="form-error">{{ taskFormError }}</div><button class="primary-button full" type="submit" :disabled="savingTask">{{ savingTask ? '保存中…' : '保存任务' }}</button></form>
      </section>
    </div>

    <div v-if="selected" class="sheet-backdrop" @click="closeSession">
      <section class="sheet session-sheet" @click.stop>
        <div class="sheet-handle" />
        <header class="session-header"><button class="session-back" type="button" aria-label="返回历史列表" @click="closeSession"><span>‹</span><strong>返回</strong></button><div class="session-heading"><span class="eyebrow">{{ selected.summary.projectName }}</span><h2>{{ selected.summary.title }}</h2></div><button class="session-close" type="button" aria-label="关闭会话" @click="closeSession">×</button></header>
        <div class="sheet-meta"><span>{{ timeAgo(selected.summary.updatedAt) }}</span><span>{{ selected.summary.source }}</span><span>{{ selected.summary.branch || '无分支' }}</span></div>
        <div ref="timeline" class="timeline session-timeline">
          <button v-if="hasEarlierSessionTurns" class="load-earlier" type="button" @click="loadEarlierSessionTurns">加载更早对话</button>
          <template v-for="turn in visibleSessionTurns" :key="turn.id">
            <template v-for="item in turn.items" :key="item.id">
              <article v-if="itemText(item) && ['user', 'agent'].includes(timelineKind(item))" :class="['timeline-entry', timelineKind(item), itemStatus(item)]">
                <div v-if="timelineKind(item) === 'user'" class="entry-heading">
                  <span class="entry-icon">{{ timelineKind(item) === 'user' ? '↗' : timelineKind(item) === 'agent' ? '⌁' : timelineKind(item) === 'reasoning' ? '◌' : timelineKind(item) === 'command' ? '›_' : timelineKind(item) === 'file' ? '±' : timelineKind(item) === 'mcp' ? 'M' : '◇' }}</span>
                  <div><strong>{{ timelineLabel(item) }}</strong><small v-if="timelineTitle(item)">{{ timelineTitle(item) }}</small></div>
                  <span v-if="itemStatus(item) === 'running'" class="entry-status running">运行中</span>
                  <span v-else-if="itemStatus(item) === 'failed'" class="entry-status failed">失败</span>
                </div>
                <MessageContent :text="itemText(item)" />
              </article>
              <ToolActivity v-else-if="itemText(item)" :kind="timelineKind(item)" :label="timelineLabel(item)" :title="timelineTitle(item)" :text="itemText(item)" :status="itemStatus(item)" />
            </template>
          </template>
          <template v-for="item in liveItems" :key="item.id">
          <article v-if="['user', 'agent'].includes(item.kind)" :class="['timeline-entry', item.kind, item.status, 'live']">
            <div v-if="item.kind === 'user'" class="entry-heading">
              <span class="entry-icon">{{ item.kind === 'user' ? '↗' : item.kind === 'agent' ? '⌁' : item.kind === 'reasoning' ? '◌' : item.kind === 'command' ? '›_' : item.kind === 'file' ? '±' : item.kind === 'mcp' ? 'M' : '◇' }}</span>
              <div><strong>{{ item.label }}</strong><small v-if="item.title">{{ item.title }}</small></div>
              <span v-if="item.status === 'running'" class="entry-status running"><i />运行中</span>
              <span v-else-if="item.status === 'failed'" class="entry-status failed">失败</span>
            </div>
            <MessageContent :text="item.text" :streaming="item.kind === 'agent' && item.status === 'running'" />
          </article>
          <ToolActivity v-else :kind="item.kind" :label="item.label" :title="item.title" :text="item.text" :status="item.status" />
          </template>
          <div v-if="sessionContentLoading" class="empty">正在加载会话内容…</div><div v-else-if="!selected.thread.turns.length && !liveItems.length" class="empty">没有可展示的会话内容</div>
        </div>
        <form class="session-composer" @submit.prevent="sendSessionMessage">
          <div v-if="sessionAttachments.length" class="composer-attachments"><div v-for="attachment in sessionAttachments" :key="attachment.localId" :class="['attachment-chip', attachment.status]"><span>{{ attachment.isImage ? '图片' : '文件' }}</span><div><strong>{{ attachment.name }}</strong><small v-if="attachment.status === 'uploading'">正在上传 {{ attachment.progress }}%</small><small v-else-if="attachment.status === 'failed'" class="upload-error">{{ attachment.error || '上传失败' }}</small><small v-else>已上传 · {{ formatFileSize(attachment.size) }}</small><div v-if="attachment.status === 'uploading'" class="upload-progress"><i :style="{ width: attachment.progress + '%' }" /></div></div><button type="button" @click="removeAttachment('session', attachment.localId)">×</button></div></div>
          <div class="composer-row"><label class="composer-attach"><input type="file" multiple accept="image/*,.pdf,.txt,.md,.json,.csv,.doc,.docx,.xls,.xlsx,.zip" @change="handleAttachmentInput($event, 'session')"><span>{{ uploadingSessionAttachment ? '…' : '＋' }}</span></label><textarea v-model="sessionMessage" rows="2" placeholder="继续这段会话…" @keydown.ctrl.enter.prevent="sendSessionMessage" /><button class="primary-button" type="submit" :disabled="sendingSessionMessage || sessionRunning || uploadingSessionAttachment || (!sessionMessage.trim() && !sessionAttachments.some(item => item.status === 'completed'))">{{ sessionRunning ? '处理中…' : sendingSessionMessage ? '发送中…' : '发送' }}</button></div>
        </form>
      </section>
    </div>
  </div>
</template>

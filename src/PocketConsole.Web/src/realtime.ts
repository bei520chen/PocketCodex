import * as signalR from "@microsoft/signalr";
import type { PocketTask } from "./types";

export type RealtimeHandlers = {
  taskCreated: (task: PocketTask) => void;
  taskUpdated: (task: PocketTask) => void;
  taskDeleted: (id: string) => void;
  codexEvent: (method: string, parameters: unknown) => void;
  stateChanged: (connected: boolean) => void;
};

export async function connectRealtime(handlers: RealtimeHandlers) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/codex")
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  connection.on("task:created", handlers.taskCreated);
  connection.on("task:updated", handlers.taskUpdated);
  connection.on("task:deleted", handlers.taskDeleted);
  connection.on("codex:event", handlers.codexEvent);
  connection.onreconnecting(() => handlers.stateChanged(false));
  connection.onreconnected(() => handlers.stateChanged(true));
  connection.onclose(() => handlers.stateChanged(false));
  await connection.start();
  handlers.stateChanged(true);
  return connection;
}

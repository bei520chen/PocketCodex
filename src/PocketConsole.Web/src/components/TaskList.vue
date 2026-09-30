<script setup lang="ts">
import type { PocketTask } from "../types";

defineProps<{ items: PocketTask[] }>();
defineEmits<{ control: [task: PocketTask, action: "start" | "interrupt"]; associate: [task: PocketTask] }>();

function statusName(status: string) {
  return ({ draft: "草稿", queued: "排队", running: "运行中", waitingApproval: "待审批", completed: "已完成", failed: "失败", cancelled: "已取消" } as Record<string, string>)[status] || status;
}
</script>

<template>
  <div v-if="!items.length" class="empty">还没有任务</div>
  <div v-else class="task-list">
    <article v-for="task in items" :key="task.id" class="task-card">
      <div class="task-top"><span :class="['task-status', task.status]">{{ statusName(task.status) }}</span><small>{{ new Date(task.updatedAt).toLocaleDateString('zh-CN') }}</small></div>
      <strong>{{ task.title }}</strong><p>{{ task.prompt }}</p><small class="path">{{ task.projectPath || '暂未关联项目' }}</small>
      <div class="task-actions">
        <button v-if="!task.projectPath" class="secondary" @click="$emit('associate', task)">关联项目</button>
        <button v-if="['draft', 'queued', 'failed', 'cancelled', 'completed'].includes(task.status)" @click="$emit('control', task, 'start')">{{ task.threadId ? '再次执行原任务' : '开始任务' }}</button>
        <button v-if="task.status === 'running'" @click="$emit('control', task, 'interrupt')">停止</button>
      </div>
    </article>
  </div>
</template>

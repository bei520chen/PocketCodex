<script setup lang="ts">
import type { Session } from "../types";

const props = defineProps<{ items: Session[]; loading?: boolean; openingId?: string | null }>();
defineEmits<{ open: [id: string] }>();

function timeAgo(seconds: number) {
  const diff = Math.max(0, Date.now() - seconds * 1000);
  if (diff < 60_000) return "刚刚";
  if (diff < 3_600_000) return Math.floor(diff / 60_000) + " 分钟前";
  if (diff < 86_400_000) return Math.floor(diff / 3_600_000) + " 小时前";
  if (diff < 604_800_000) return Math.floor(diff / 86_400_000) + " 天前";
  return new Date(seconds * 1000).toLocaleDateString("zh-CN");
}
</script>

<template>
  <div v-if="loading" class="empty">正在读取 Codex 历史…</div>
  <div v-else-if="!items.length" class="empty">没有找到会话</div>
  <div v-else class="session-list">
    <button v-for="session in items" :key="session.id" class="session-card" :class="{ opening: props.openingId === session.id }" :disabled="Boolean(props.openingId)" @click="$emit('open', session.id)">
      <div class="session-main"><span :class="['session-state', session.status]"/><div><strong>{{ session.title }}</strong><p>{{ session.preview }}</p><small>{{ session.projectName }}<template v-if="session.branch"> · {{ session.branch }}</template> · {{ timeAgo(session.updatedAt) }}</small></div></div><span class="chevron">{{ props.openingId === session.id ? "打开中…" : "›" }}</span>
    </button>
  </div>
</template>

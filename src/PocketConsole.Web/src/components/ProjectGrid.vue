<script setup lang="ts">
import type { Project } from "../types";

defineProps<{ items: Project[] }>();
defineEmits<{ open: [project: Project] }>();

function timeAgo(seconds: number) {
  const diff = Math.max(0, Date.now() - seconds * 1000);
  if (diff < 3_600_000) return Math.max(1, Math.floor(diff / 60_000)) + " 分钟前";
  if (diff < 86_400_000) return Math.floor(diff / 3_600_000) + " 小时前";
  if (diff < 604_800_000) return Math.floor(diff / 86_400_000) + " 天前";
  return new Date(seconds * 1000).toLocaleDateString("zh-CN");
}
</script>

<template>
  <div class="project-grid">
    <button v-for="project in items" :key="project.id" class="project-card" @click="$emit('open', project)">
      <span class="folder">▰</span><div><strong>{{ project.name }}</strong><small>{{ project.sessionCount }} 个会话 · {{ timeAgo(project.lastActiveAt) }}</small><p>{{ project.lastSessionTitle }}</p></div>
    </button>
  </div>
</template>

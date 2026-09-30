<script setup lang="ts">
import { computed } from "vue";

const props = defineProps<{
  kind: string;
  label: string;
  title?: string;
  text: string;
  status: "running" | "completed" | "failed";
}>();

const icon = computed(() => ({ reasoning: "◌", command: ">_", file: "±", mcp: "◇", tool: "◇" }[props.kind] || "◇"));
const summary = computed(() => props.title ? `${props.label} · ${props.title}` : props.label);
</script>

<template>
  <details :class="['tool-activity', kind, status]" :open="status === 'running'">
    <summary>
      <span class="tool-activity-icon">{{ icon }}</span>
      <span class="tool-activity-title">{{ summary }}</span>
      <span v-if="status === 'running'" class="tool-activity-status running">处理中</span>
      <span v-else-if="status === 'failed'" class="tool-activity-status failed">失败</span>
      <span v-else class="tool-activity-toggle"><span class="expand-label">展开</span><span class="collapse-label">收起</span></span>
    </summary>
    <pre>{{ text || '正在处理…' }}</pre>
  </details>
</template>

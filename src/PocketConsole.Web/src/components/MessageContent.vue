<script setup lang="ts">
import { computed, ref } from "vue";

const props = defineProps<{ text: string; streaming?: boolean }>();

type InlineToken = { type: "text" | "code" | "strong"; value: string };
type ContentBlock = { type: "paragraph" | "code" | "list"; text?: string; language?: string; items?: string[] };

const copiedCode = ref("");

const blocks = computed<ContentBlock[]>(() => parseBlocks(props.text));

function parseBlocks(value: string) {
  const lines = value.replace(/\r\n/g, "\n").split("\n");
  const result: ContentBlock[] = [];
  let paragraph: string[] = [];
  let list: string[] = [];
  let code: string[] | null = null;
  let language = "文本";

  const flushParagraph = () => {
    if (!paragraph.length) return;
    result.push({ type: "paragraph", text: paragraph.join("\n").trim() });
    paragraph = [];
  };
  const flushList = () => {
    if (!list.length) return;
    result.push({ type: "list", items: [...list] });
    list = [];
  };

  for (const line of lines) {
    const fence = line.match(/^```\s*([^\s`]*)\s*$/);
    if (fence) {
      if (code) {
        result.push({ type: "code", language, text: code.join("\n") });
        code = null;
        language = "文本";
      } else {
        flushParagraph();
        flushList();
        code = [];
        language = fence[1] || "文本";
      }
      continue;
    }
    if (code) {
      code.push(line);
      continue;
    }
    const listItem = line.match(/^\s*[-*]\s+(.+)$/);
    if (listItem) {
      flushParagraph();
      list.push(listItem[1]);
      continue;
    }
    if (!line.trim()) {
      flushParagraph();
      flushList();
      continue;
    }
    flushList();
    paragraph.push(line);
  }

  if (code) result.push({ type: "code", language, text: code.join("\n") });
  flushParagraph();
  flushList();
  return result;
}

function inlineTokens(value: string): InlineToken[] {
  const tokens: InlineToken[] = [];
  const pattern = /(`[^`]+`|\*\*[^*]+\*\*)/g;
  let offset = 0;
  for (const match of value.matchAll(pattern)) {
    const index = match.index || 0;
    if (index > offset) tokens.push({ type: "text", value: value.slice(offset, index) });
    const token = match[0];
    tokens.push(token.startsWith("`")
      ? { type: "code", value: token.slice(1, -1) }
      : { type: "strong", value: token.slice(2, -2) });
    offset = index + token.length;
  }
  if (offset < value.length) tokens.push({ type: "text", value: value.slice(offset) });
  return tokens;
}

async function copyCode(value: string) {
  await navigator.clipboard.writeText(value);
  copiedCode.value = value;
  window.setTimeout(() => { if (copiedCode.value === value) copiedCode.value = ""; }, 1400);
}
</script>

<template>
  <div class="message-content">
    <template v-for="(block, blockIndex) in blocks" :key="blockIndex">
      <p v-if="block.type === 'paragraph'" class="message-paragraph">
        <template v-for="(token, tokenIndex) in inlineTokens(block.text || '')" :key="tokenIndex">
          <code v-if="token.type === 'code'">{{ token.value }}</code>
          <strong v-else-if="token.type === 'strong'">{{ token.value }}</strong>
          <template v-else>{{ token.value }}</template>
        </template>
      </p>
      <ul v-else-if="block.type === 'list'" class="message-list">
        <li v-for="(item, itemIndex) in block.items" :key="itemIndex">
          <template v-for="(token, tokenIndex) in inlineTokens(item)" :key="tokenIndex">
            <code v-if="token.type === 'code'">{{ token.value }}</code>
            <strong v-else-if="token.type === 'strong'">{{ token.value }}</strong>
            <template v-else>{{ token.value }}</template>
          </template>
        </li>
      </ul>
      <section v-else class="message-code">
        <header><span>{{ block.language }}</span><button type="button" @click="copyCode(block.text || '')">{{ copiedCode === block.text ? '已复制' : '复制' }}</button></header>
        <pre><code>{{ block.text }}</code></pre>
      </section>
    </template>
    <i v-if="streaming" class="stream-cursor" />
  </div>
</template>
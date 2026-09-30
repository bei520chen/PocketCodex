import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
export default defineConfig({
    plugins: [vue()],
    server: {
        host: "0.0.0.0",
        port: 5173,
        proxy: {
            "/api": "http://127.0.0.1:5086",
            "/hubs": { target: "http://127.0.0.1:5086", ws: true }
        }
    },
    build: { outDir: "../PocketConsole.Api/wwwroot", emptyOutDir: true }
});

import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The dev server proxies /api to the running service, so the SPA develops against a live
// backend. The production build lands in ../src/Wolfstare.Service/wwwroot, which the service
// serves same-origin.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      "/api": {
        target: "http://127.0.0.1:8437",
        changeOrigin: false,
      },
    },
  },
  build: {
    outDir: "../src/Wolfstare.Service/wwwroot",
    emptyOutDir: true,
  },
});

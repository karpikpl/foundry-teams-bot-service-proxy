import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { resolve } from "node:path";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: resolve(__dirname, "../wwwroot/teams-chat"),
    emptyOutDir: true,
    sourcemap: false,
    rollupOptions: {
      output: {
        manualChunks: {
          teams: ["@microsoft/teams-js", "@azure/msal-browser"],
          fluent: ["@fluentui/react-components"]
        }
      }
    }
  }
});

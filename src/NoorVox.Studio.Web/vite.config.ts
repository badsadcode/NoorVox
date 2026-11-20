import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Built into the WPF project's wwwroot and served from https://app.noorvox.local/ (virtual host, no server, no CDN).
export default defineConfig({
  plugins: [react()],
  base: "./",
  build: {
    outDir: "../NoorVox.Studio/wwwroot",
    emptyOutDir: true,
    sourcemap: false,
    target: "es2022",
  },
  server: { port: 5178, strictPort: true },
});

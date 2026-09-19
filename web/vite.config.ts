import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// VITE_KEYCLOAK_URL / VITE_API_URL are set per environment; defaults suit
// local development against the docker-compose stack.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": {
        target: process.env.VITE_API_URL || "http://localhost:8080",
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: "dist",
    sourcemap: false,
  },
});

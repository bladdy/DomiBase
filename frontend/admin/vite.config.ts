import { defineConfig, loadEnv, type Plugin } from "vite";
import react from "@vitejs/plugin-react";

const productionCsp = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self'",
  "img-src 'self' data:",
  "font-src 'self'",
  "connect-src 'self'",
  "base-uri 'self'",
  "object-src 'none'"
].join("; ");

function contentSecurityPolicy(): Plugin {
  return {
    name: "domibase-admin-csp",
    apply: "build",
    transformIndexHtml: {
      order: "post",
      handler(html) {
        return html.replace(
          "<head>",
          `<head>\n    <meta http-equiv="Content-Security-Policy" content="${productionCsp}" />`
        );
      }
    }
  };
}

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  const controlApiTarget = env.CONTROL_API_TARGET || "http://127.0.0.1:5080";

  return {
    plugins: [react(), contentSecurityPolicy()],
    server: {
      proxy: {
        // Owner login is /admin/v1/login, outside /api; keep it on the panel origin too.
        "/api": {
          target: controlApiTarget,
          changeOrigin: false
        },
        "/admin": {
          target: controlApiTarget,
          changeOrigin: false
        }
      }
    },
    preview: {
      proxy: {
        "/api": {
          target: controlApiTarget,
          changeOrigin: false
        },
        "/admin": {
          target: controlApiTarget,
          changeOrigin: false
        }
      }
    },
    build: {
      target: "es2022"
    }
  };
});

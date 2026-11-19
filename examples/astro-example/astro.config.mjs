import node from "@astrojs/node";
import { defineConfig, envField } from "astro/config";

export default defineConfig({
  // On-demand rendering: every page is rendered on the server for each request.
  output: "server",
  adapter: node({ mode: "standalone" }),
  server: {
    // Dedicated port for the DomiBase example; the default 4321 may be used by another local app.
    port: 4325
  },
  security: {
    // Reject cross-origin form submissions (CSRF defence in depth; CORS is not authorization).
    checkOrigin: true
  },
  env: {
    schema: {
      // Server-only secrets: never imported from astro:env/client and never sent to the browser.
      DOMIBASE_API_URL: envField.string({
        context: "server",
        access: "secret",
        default: "http://localhost:5080"
      }),
      DOMIBASE_PROJECT_KEY: envField.string({ context: "server", access: "secret" }),
      DOMIBASE_PROJECT_KEY_B: envField.string({
        context: "server",
        access: "secret",
        optional: true
      }),
      DOMIBASE_REVOKED_PROJECT_KEY: envField.string({
        context: "server",
        access: "secret",
        optional: true
      }),
      DOMIBASE_PROJECT_NAME: envField.string({
        context: "server",
        access: "secret",
        default: "Paneles Solares"
      }),
      DOMIBASE_PROJECT_NAME_B: envField.string({
        context: "server",
        access: "secret",
        default: "Aislamiento Demo"
      })
    }
  }
});

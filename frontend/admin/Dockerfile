# syntax=docker/dockerfile:1
# Local test image for the DomiBase admin panel. Build context: frontend/admin.

FROM node:22-bookworm-slim AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM node:22-bookworm-slim AS runtime
WORKDIR /app
ENV NODE_ENV=production
COPY --from=build /app/package.json ./package.json
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/vite.config.ts ./vite.config.ts
COPY --from=build /app/dist ./dist
EXPOSE 5190
CMD ["npx", "vite", "preview", "--host", "0.0.0.0", "--port", "5190", "--strictPort"]

# PC MATE — сервер-ретранслятор
# Сборка: docker build -t pcmate-server .
# Запуск:  docker compose up -d

FROM node:22-bookworm-slim AS build
WORKDIR /app

# better-sqlite3 при отсутствии готовой сборки компилируется из исходников.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 make g++ \
    && rm -rf /var/lib/apt/lists/*

COPY package.json package-lock.json* ./
RUN npm install --omit=dev --ignore-scripts=false

COPY tsconfig.json ./
COPY src ./src
RUN npm install --no-save typescript@5.6.3 \
    && npx tsc -p tsconfig.json \
    && rm -rf node_modules/typescript

FROM node:22-bookworm-slim AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    PCMATE_PORT=8080 \
    PCMATE_DB=/data/pcmate.db

RUN mkdir -p /data && chown -R node:node /data

COPY --from=build --chown=node:node /app/node_modules ./node_modules
COPY --from=build --chown=node:node /app/dist ./dist
COPY --chown=node:node package.json ./

USER node
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD node -e "fetch('http://127.0.0.1:'+(process.env.PCMATE_PORT||8080)+'/api/v1/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"

CMD ["node", "dist/index.js"]

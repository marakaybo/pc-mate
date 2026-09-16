import Fastify from 'fastify';
import { pathToFileURL } from 'node:url';
import cors from '@fastify/cors';
import rateLimit from '@fastify/rate-limit';
import { config } from './config.js';
import { Store } from './db/database.js';
import { Hub } from './ws/hub.js';
import { registerRoutes } from './routes/index.js';
import { logger } from './lib/logger.js';

export async function createServer(databasePath = config.databasePath) {
  const store = new Store(databasePath);
  const hub = new Hub(store);

  const app = Fastify({
    logger: false,
    bodyLimit: 256 * 1024,
    trustProxy: true
  });

  await app.register(cors, { origin: true });
  await app.register(rateLimit, {
    max: config.rateLimitMax,
    timeWindow: config.rateLimitWindowMs,
    // Сопряжение и регистрация — самые чувствительные к перебору.
    keyGenerator: (request) => request.ip
  });

  app.setErrorHandler((error: Error & { statusCode?: number }, request, reply) => {
    logger.error({ url: request.url, error }, 'Ошибка обработки запроса');
    reply.code(error.statusCode ?? 500).send({
      error: 'server_error',
      message: error.message
    });
  });

  await registerRoutes(app, { store, hub });

  return { app, store, hub };
}

async function main(): Promise<void> {
  const { app, store, hub } = await createServer();

  await app.listen({ port: config.port, host: config.host });
  hub.attach(app.server);

  logger.info(
    { port: config.port, host: config.host, db: config.databasePath },
    'PC MATE relay запущен'
  );

  const shutdown = async (signal: string) => {
    logger.info({ signal }, 'Останавливаем сервер');
    try {
      await hub.close();
      await app.close();
      store.close();
    } finally {
      process.exit(0);
    }
  };

  process.on('SIGTERM', () => void shutdown('SIGTERM'));
  process.on('SIGINT', () => void shutdown('SIGINT'));
}

// Запуск только когда файл выполняется напрямую (в тестах импортируем createServer).
const entryUrl = process.argv[1] ? pathToFileURL(process.argv[1]).href : '';
if (entryUrl === import.meta.url) {
  main().catch((error) => {
    logger.error({ error }, 'Не удалось запустить сервер');
    process.exit(1);
  });
}

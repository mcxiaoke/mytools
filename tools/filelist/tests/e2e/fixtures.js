const { test: base, expect } = require('@playwright/test');
const { startServer } = require('./test-server');

const test = base.extend({
  // Worker-scoped server fixture: starts once per worker, shuts down after all tests finish.
  server: [
    async ({}, use) => {
      const server = await startServer({
        name: `worker-${process.pid}`,
        uploadEnabled: true
      });
      await use(server);
      await server.stop();
      server.cleanup();
    },
    { scope: 'worker' }
  ],

  // Convenience page fixture that automatically navigates to server.baseUrl if needed
  appPage: async ({ page, server }, use) => {
    await page.goto(server.baseUrl);
    // Wait for SPA initialization (tableWrap not showing spinner)
    await page.waitForSelector('#loading', { state: 'hidden', timeout: 5000 }).catch(() => {});
    await use({ page, server, baseUrl: server.baseUrl });
  }
});

module.exports = { test, expect };

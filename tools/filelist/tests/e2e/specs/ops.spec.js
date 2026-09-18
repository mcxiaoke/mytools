const { test, expect } = require('@playwright/test');
const { startServer } = require('../test-server');

// ── Ops panel E2E ───────────────────────────────────────────────
//
// These tests exist because the panel once broke the whole page in
// production without a single Go test noticing: the injected config
// landed in a JavaScript property name, making the script block a
// syntax error. Every test here therefore asserts on the PAGE as a
// browser sees it — console errors, rendered DOM, real interaction —
// not just on the WebSocket protocol.

const OPS_TOKEN = 'ops-e2e-token';
const TERM_TOKEN = 'ops-e2e-term';

let server;

test.beforeAll(async () => {
  server = await startServer({
    name: `ops-${process.pid}`,
    uploadEnabled: false,
    ops: {
      enabled: true,
      token: OPS_TOKEN,
      terminalToken: TERM_TOKEN,
      // Allow a harmless command so the happy path is exercised
      // without depending on the built-in allowlist contents.
      allow: ['^echo .*$']
    }
  });
});

test.afterAll(async () => {
  if (server) {
    await server.stop();
    server.cleanup();
  }
});

// collectErrors attaches a console listener and returns the array.
function collectErrors(page) {
  const errors = [];
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

test.describe('Ops Panel', () => {
  test('page loads with no console errors when the panel is enabled', async ({ page }) => {
    const errors = collectErrors(page);

    await page.goto(server.baseUrl);
    await page.waitForSelector('#ops-panel-root .ops-drawer', { timeout: 5000 });
    await page.waitForTimeout(300);

    // The SPA must have initialised; a broken config block would leave
    // the file list empty and the console full of ReferenceErrors.
    await expect(page.locator('table tbody tr').first()).toBeVisible();

    const fatal = errors.filter((e) =>
      /is not defined|SyntaxError|Unexpected token/.test(e));
    expect(fatal, `console errors:\n${errors.join('\n')}`).toEqual([]);
  });

  test('injected ops config is valid JavaScript', async ({ page }) => {
    await page.goto(server.baseUrl);
    await page.waitForSelector('#ops-panel-root .ops-drawer');

    // Evaluate the config object in the page: if the substitution were
    // malformed the script block would never have run and this would
    // be undefined.
    const cfg = await page.evaluate(() => window.__FILELIST_CONFIG__);
    expect(cfg).toBeTruthy();
    expect(cfg.ops).toBeTruthy();
    expect(cfg.ops.enabled).toBe(true);
    expect(typeof cfg.ops.mode).toBe('string');
  });

  test('toolbar button is injected by the module', async ({ page }) => {
    await page.goto(server.baseUrl);
    // The host template contains no ops button; ops.js adds it.
    await expect(page.locator('#opsToggleBtn')).toBeVisible();
  });

  test('drawer opens and asks for the panel token', async ({ page }) => {
    await page.goto(server.baseUrl);
    await page.click('#opsToggleBtn');

    // The panel token is separate from the browsing token, so the
    // prompt must appear before anything else.
    await expect(page.locator('input[placeholder="ops.token"]')).toBeVisible();
  });

  test('rejects a wrong panel token and accepts the right one', async ({ page }) => {
    const errors = collectErrors(page);
    await page.goto(server.baseUrl);
    await page.click('#opsToggleBtn');

    // Wrong token: the prompt stays.
    await page.fill('input[placeholder="ops.token"]', 'not-the-token');
    await page.click('button:has-text("连接")');
    await page.waitForTimeout(800);
    await expect(page.locator('input[placeholder="ops.token"]')).toBeVisible();

    // Correct token: the command input replaces the prompt.
    await page.fill('input[placeholder="ops.token"]', OPS_TOKEN);
    await page.click('button:has-text("连接")');
    await expect(page.locator('input[placeholder*="systemctl"]')).toBeVisible({ timeout: 5000 });

    const fatal = errors.filter((e) => /is not defined|SyntaxError/.test(e));
    expect(fatal).toEqual([]);
  });

  test('runs an allowed command and shows output with an exit code', async ({ page }) => {
    await page.goto(server.baseUrl);
    await page.click('#opsToggleBtn');
    await page.fill('input[placeholder="ops.token"]', OPS_TOKEN);
    await page.click('button:has-text("连接")');

    const input = page.locator('input[placeholder*="systemctl"]');
    await expect(input).toBeVisible({ timeout: 5000 });

    await input.fill('echo ops-e2e-ok');
    await input.press('Enter');

    const output = page.locator('.ops-output');
    await expect(output).toContainText('ops-e2e-ok', { timeout: 5000 });
    await expect(output).toContainText('exit 0');
  });

  test('refuses a denied command and explains why', async ({ page }) => {
    await page.goto(server.baseUrl);
    await page.click('#opsToggleBtn');
    await page.fill('input[placeholder="ops.token"]', OPS_TOKEN);
    await page.click('button:has-text("连接")');

    const input = page.locator('input[placeholder*="systemctl"]');
    await expect(input).toBeVisible({ timeout: 5000 });

    await input.fill('rm -rf /tmp/should-not-run');
    await input.press('Enter');

    const output = page.locator('.ops-output');
    await expect(output).toContainText('拒绝', { timeout: 5000 });
  });

  test('cwd follows directory navigation', async ({ page }) => {
    await page.goto(server.baseUrl);
    await page.click('#opsToggleBtn');
    await page.fill('input[placeholder="ops.token"]', OPS_TOKEN);
    await page.click('button:has-text("连接")');
    await expect(page.locator('input[placeholder*="systemctl"]')).toBeVisible({ timeout: 5000 });

    // Navigate into a directory; the panel should follow.
    await page.locator('table tbody tr').first().locator('a').first().click();
    await page.waitForTimeout(600);

    const cwd = await page.textContent('.ops-cwd');
    expect(cwd.trim().length).toBeGreaterThan(1);
    expect(cwd.trim()).not.toBe('/');
  });

  test('the panel token never reaches the page source', async ({ page }) => {
    await page.goto(server.baseUrl);
    const html = await page.content();
    expect(html).not.toContain(OPS_TOKEN);
    expect(html).not.toContain(TERM_TOKEN);
  });
});

test.describe('Ops Panel disabled', () => {
  test('disabled panel leaves the page clean and exposes no routes', async ({ page, request }) => {
    const srv = await startServer({ name: `ops-off-${process.pid}`, uploadEnabled: false });
    try {
      const errors = collectErrors(page);
      await page.goto(srv.baseUrl);
      await page.waitForSelector('table tbody tr');

      // No ops markup, no injected button, no console noise.
      expect(await page.locator('#opsToggleBtn').count()).toBe(0);
      const fatal = errors.filter((e) => /is not defined|SyntaxError/.test(e));
      expect(fatal).toEqual([]);

      // Routes are not registered at all.
      const info = await request.get(`${srv.baseUrl}/api/ops/info`);
      expect(info.status()).toBe(404);
      const opsPage = await request.get(`${srv.baseUrl}/ops`);
      expect(opsPage.status()).toBe(404);
    } finally {
      await srv.stop();
      srv.cleanup();
    }
  });
});

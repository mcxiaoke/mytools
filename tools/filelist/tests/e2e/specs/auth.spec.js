const { test: base, expect } = require('@playwright/test');
const { startServer } = require('../test-server');

base.describe('Token Authentication Flow', () => {
  let authServer;

  base.beforeAll(async () => {
    authServer = await startServer({
      name: 'auth-test',
      token: 'e2e-secret-token',
      uploadEnabled: true
    });
  });

  base.afterAll(async () => {
    if (authServer) {
      await authServer.stop();
      authServer.cleanup();
    }
  });

  base('should block unauthorized browser access without token', async ({ page }) => {
    const res = await page.goto(authServer.baseUrl);
    expect(res.status()).toBe(401);
    await expect(page.locator('h1')).toContainText('需要访问令牌');
  });

  base('should block unauthorized API requests with 401 JSON', async ({ request }) => {
    const res = await request.get(`${authServer.baseUrl}/api/roots`);
    expect(res.status()).toBe(401);
    const json = await res.json();
    expect(json.error).toBe('unauthorized');
  });

  base('should grant access with ?token= query and persist session cookie', async ({ page, context }) => {
    // Visit with ?token=
    const res = await page.goto(`${authServer.baseUrl}/?token=e2e-secret-token`);
    expect(res.status()).toBe(200);

    // Verify cookie was set
    const cookies = await context.cookies(authServer.baseUrl);
    const tokenCookie = cookies.find((c) => c.name === 'filelist_token');
    expect(tokenCookie).toBeDefined();
    expect(tokenCookie.value).toBe('e2e-secret-token');

    // Subsequent navigation without ?token= should succeed seamlessly
    const subRes = await page.goto(`${authServer.baseUrl}/data`);
    expect(subRes.status()).toBe(200);
    await expect(page.locator('table tbody tr')).not.toHaveCount(0);
  });
});

const { test, expect } = require('../fixtures');

test.describe('File Download & Raw Preview', () => {
  test.beforeEach(async ({ appPage }) => {
    await appPage.page.goto(`${appPage.baseUrl}/data`);
    await expect(appPage.page.locator('table tbody tr')).not.toHaveCount(0);
  });

  test('should trigger browser download when clicking action download button', async ({ appPage }) => {
    const { page } = appPage;

    // Wait for download event upon clicking download button for hello.txt.
    // Target .btn-dl specifically: the action cell also holds the novel
    // reader link, so a bare `td.actions a` matches two elements and
    // trips Playwright's strict mode.
    const downloadPromise = page.waitForEvent('download');
    await page.locator('tr:has-text("hello.txt") td.actions a.btn-dl').click();
    const download = await downloadPromise;

    expect(download.suggestedFilename()).toBe('hello.txt');

    // Read download stream content
    const stream = await download.createReadStream();
    const chunks = [];
    for await (const chunk of stream) {
      chunks.push(chunk);
    }
    const content = Buffer.concat(chunks).toString('utf-8');
    expect(content).toContain('Hello FileList E2E Content');
  });

  test('should provide direct raw preview link with correct href', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    const fileLink = page.locator('tr:has-text("hello.txt") td.name a');
    await expect(fileLink).toHaveAttribute('href', '/raw/data/hello.txt');
    await expect(fileLink).toHaveAttribute('target', '_blank');

    // Fetch the raw URL directly to verify HTTP response
    const response = await page.request.get(`${baseUrl}/raw/data/hello.txt`);
    expect(response.status()).toBe(200);
    const body = await response.text();
    expect(body).toContain('Hello FileList E2E Content');
  });
});

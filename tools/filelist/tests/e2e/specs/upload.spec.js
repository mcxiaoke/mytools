const { test, expect } = require('../fixtures');

test.describe('File Upload & Duplicate Collision', () => {
  test.beforeEach(async ({ appPage }) => {
    // Navigate into /data
    await appPage.page.goto(`${appPage.baseUrl}/data`);
    await expect(appPage.page.locator('table tbody tr')).not.toHaveCount(0);
  });

  test('should show upload button only in directory view and hide during search', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    // Inside /data: upload button is visible
    await expect(page.locator('#uploadBtn')).toBeVisible();

    // Trigger search: upload button should be hidden
    await page.locator('#searchInput').fill('hello');
    await page.locator('#searchBtn').click();
    await expect(page.locator('#uploadBtn')).toBeHidden();

    // Clear search: upload button returns
    await page.locator('#clearBtn').click();
    await expect(page.locator('#uploadBtn')).toBeVisible();

    // Navigate to root: upload button should be hidden
    await page.goto(`${baseUrl}/`);
    await expect(page.locator('#uploadBtn')).toBeHidden();
  });

  test('should upload a file and automatically refresh the directory list', async ({ appPage }) => {
    const { page } = appPage;

    const fileName = 'e2e-sample.txt';
    const fileContent = 'Hello from Playwright automated upload!';

    // Upload file via hidden file input
    await page.locator('#uploadInput').setInputFiles({
      name: fileName,
      mimeType: 'text/plain',
      buffer: Buffer.from(fileContent)
    });

    // Check status text shows success
    const statusEl = page.locator('#uploadStatus');
    await expect(statusEl).toHaveClass(/success/, { timeout: 5000 });
    await expect(statusEl).toContainText('成功上传');

    // Verify the new file appears in the table without page reload
    const names = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim()));
    expect(names).toContain(fileName);
  });

  test('should handle duplicate upload by automatically appending collision suffix (1)', async ({ appPage }) => {
    const { page } = appPage;

    const fileName = 'collision-test.txt';

    // First upload: collision-test.txt
    await page.locator('#uploadInput').setInputFiles({
      name: fileName,
      mimeType: 'text/plain',
      buffer: Buffer.from('Original file')
    });
    await expect(page.locator('#uploadStatus')).toHaveClass(/success/, { timeout: 5000 });

    // Second upload with identical filename: collision-test.txt
    await page.locator('#uploadInput').setInputFiles({
      name: fileName,
      mimeType: 'text/plain',
      buffer: Buffer.from('Second file with identical name')
    });
    await expect(page.locator('#uploadStatus')).toHaveClass(/success/, { timeout: 5000 });

    // Table should now contain BOTH collision-test.txt AND collision-test (1).txt
    const names = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim()));

    expect(names).toContain('collision-test.txt');
    expect(names).toContain('collision-test (1).txt');
  });

  test('should block uploading sensitive excluded files like .env', async ({ appPage }) => {
    const { page } = appPage;

    // Try uploading .env
    await page.locator('#uploadInput').setInputFiles({
      name: '.env',
      mimeType: 'text/plain',
      buffer: Buffer.from('MALICIOUS_KEY=123')
    });

    // Server should block excluded file (returns 0 uploaded or error)
    const statusEl = page.locator('#uploadStatus');
    // Upload should finish (either error status or 0 uploaded)
    await expect(statusEl).not.toHaveText('', { timeout: 5000 });

    // .env should NOT be listed in the table
    const names = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim()));
    expect(names).not.toContain('.env');
  });

  test('should support drag-and-drop file upload onto the table', async ({ appPage }) => {
    const { page } = appPage;

    const dragFileName = 'dragged-file.md';

    // Emulate dragover on window to verify .drop-active class toggle
    await page.evaluate(() => {
      const dt = new DataTransfer();
      const event = new DragEvent('dragover', { bubbles: true, cancelable: true, dataTransfer: dt });
      window.dispatchEvent(event);
    });
    await expect(page.locator('#tableWrap')).toHaveClass(/drop-active/);

    // Emulate drop event with DataTransfer payload
    const buffer = Buffer.from('# Drag and Drop E2E Content');
    const base64Data = buffer.toString('base64');

    await page.evaluate(
      ({ name, base64 }) => {
        const byteCharacters = atob(base64);
        const byteNumbers = new Array(byteCharacters.length);
        for (let i = 0; i < byteCharacters.length; i++) {
          byteNumbers[i] = byteCharacters.charCodeAt(i);
        }
        const byteArray = new Uint8Array(byteNumbers);
        const file = new File([byteArray], name, { type: 'text/markdown' });

        const dt = new DataTransfer();
        dt.items.add(file);

        const dropEvent = new DragEvent('drop', {
          bubbles: true,
          cancelable: true,
          dataTransfer: dt
        });
        window.dispatchEvent(dropEvent);
      },
      { name: dragFileName, base64: base64Data }
    );

    // Check upload status
    await expect(page.locator('#uploadStatus')).toHaveClass(/success/, { timeout: 5000 });

    // Verify file is in table
    const names = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim()));
    expect(names).toContain(dragFileName);
  });
});

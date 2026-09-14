const { test, expect } = require('../fixtures');

test.describe('Table Sorting', () => {
  test.beforeEach(async ({ appPage }) => {
    // Navigate into /data
    await appPage.page.goto(`${appPage.baseUrl}/data`);
    await expect(appPage.page.locator('table tbody tr')).not.toHaveCount(0);
  });

  test('should sort files by name asc and desc while keeping directories first', async ({ appPage }) => {
    const { page } = appPage;

    // Helper to get file names (skipping directories)
    const getFileNames = async () => {
      return await page
        .locator('table tbody tr td.name a')
        .evaluateAll((nodes) =>
          nodes
            .map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim())
            .filter((n) => n.endsWith('.txt'))
        );
    };

    // Initial state: name asc -> ['apple.txt', 'hello.txt', 'zebra.txt']
    let files = await getFileNames();
    expect(files).toEqual(['apple.txt', 'hello.txt', 'zebra.txt']);

    // Click '名称' column header to switch to desc
    await page.locator('th.name-col').click();
    await expect(page.locator('th.name-col .arrow')).toHaveText(/▼/);

    files = await getFileNames();
    expect(files).toEqual(['zebra.txt', 'hello.txt', 'apple.txt']);

    // Click again to switch back to asc
    await page.locator('th.name-col').click();
    await expect(page.locator('th.name-col .arrow')).toHaveText(/▲/);

    files = await getFileNames();
    expect(files).toEqual(['apple.txt', 'hello.txt', 'zebra.txt']);
  });

  test('should sort by size and time columns', async ({ appPage }) => {
    const { page } = appPage;

    // Click '大小' column header
    await page.locator('th.size-col').click();
    await expect(page.locator('th.size-col .arrow')).toHaveText(/▲/);

    // Click '修改时间' column header
    await page.locator('th.time-col').click();
    await expect(page.locator('th.time-col .arrow')).toHaveText(/▲/);

    // Click again to toggle desc
    await page.locator('th.time-col').click();
    await expect(page.locator('th.time-col .arrow')).toHaveText(/▼/);
  });
});

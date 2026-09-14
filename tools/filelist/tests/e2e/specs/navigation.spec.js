const { test, expect } = require('../fixtures');

test.describe('Navigation & Directory Browsing', () => {
  test('should render root view with configured mounts and hidden upload button', async ({ appPage }) => {
    const { page } = appPage;

    await expect(page).toHaveTitle('FileList');
    await expect(page.locator('#homeLink')).toBeVisible();
    await expect(page.locator('#searchInput')).toBeVisible();

    // In root view, upload button must be hidden
    await expect(page.locator('#uploadBtn')).toBeHidden();

    // Check that configured roots are listed
    const rootRows = page.locator('table tbody tr');
    await expect(rootRows).toHaveCount(2);

    const names = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5]+/, '').trim()));
    expect(names).toEqual(['data', 'docs']);
  });

  test('should navigate into subdirectories and update breadcrumbs', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    // Click 'data' root
    await page.locator('table tbody tr td.name a:has-text("data")').click();
    await expect(page).toHaveURL(`${baseUrl}/data`);

    // Wait for table to load items in /data
    await expect(page.locator('table tbody tr')).not.toHaveCount(0);

    // In directory view, upload button should be visible
    await expect(page.locator('#uploadBtn')).toBeVisible();

    // Verify breadcrumbs: 根目录 / data
    const breadcrumbText = await page.locator('#breadcrumb').innerText();
    expect(breadcrumbText).toContain('根目录');
    expect(breadcrumbText).toContain('data');

    // Verify directory contents
    const cleanNames = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim()));

    expect(cleanNames).toContain('subfolder');
    expect(cleanNames).toContain('empty-dir');
    expect(cleanNames).toContain('hello.txt');
    expect(cleanNames).toContain('apple.txt');
    expect(cleanNames).toContain('zebra.txt');

    // Verify excluded sensitive file (.env) is NOT displayed
    expect(cleanNames).not.toContain('.env');
  });

  test('should support deep navigation and breadcrumb return', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    // Go to /data
    await page.locator('table tbody tr td.name a:has-text("data")').click();
    await expect(page).toHaveURL(`${baseUrl}/data`);

    // Go to subfolder
    await page.locator('table tbody tr td.name a:has-text("subfolder")').click();
    await expect(page).toHaveURL(`${baseUrl}/data/subfolder`);

    // Breadcrumb check
    await expect(page.locator('#breadcrumb')).toContainText('subfolder');

    // Click 'data' link in breadcrumb to return to parent
    await page.locator('#breadcrumb a:has-text("data")').click();
    await expect(page).toHaveURL(`${baseUrl}/data`);
    await expect(page.locator('table tbody tr td.name a:has-text("subfolder")')).toBeVisible();

    // Click '根目录' link in breadcrumb to return to root
    await page.locator('#breadcrumb a:has-text("根目录")').click();
    await expect(page).toHaveURL(`${baseUrl}/`);
    await expect(page.locator('table tbody tr td.name a:has-text("data")')).toBeVisible();
  });

  test('should display empty directory state', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    // Go to /data/empty-dir
    await page.goto(`${baseUrl}/data/empty-dir`);

    // Should show empty state
    await expect(page.locator('.empty')).toBeVisible();
    await expect(page.locator('.empty')).toContainText('此目录为空');
  });
});

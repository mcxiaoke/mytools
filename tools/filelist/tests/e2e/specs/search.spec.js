const { test, expect } = require('../fixtures');

test.describe('Search Interaction', () => {
  test('should search files across roots and render matching results', async ({ appPage }) => {
    const { page } = appPage;

    // Type query into search input
    const searchInput = page.locator('#searchInput');
    await searchInput.fill('deep');
    await page.locator('#searchBtn').click();

    // Clear button should become visible
    await expect(page.locator('#clearBtn')).toBeVisible();

    // Wait for search results
    await expect(page.locator('table tbody tr')).not.toHaveCount(0);

    const matchTexts = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim()));

    expect(matchTexts).toContain('deep');
    expect(matchTexts).toContain('deep.log');

    // Matches should be highlighted with <mark>
    const marks = await page.locator('table tbody tr td.name a mark').allInnerTexts();
    expect(marks.length).toBeGreaterThan(0);
    expect(marks.some((m) => m.toLowerCase() === 'deep')).toBeTruthy();
  });

  test('should show empty prompt when no files match', async ({ appPage }) => {
    const { page } = appPage;

    await page.locator('#searchInput').fill('non_existent_file_xyz');
    await page.locator('#searchBtn').click();

    await expect(page.locator('.empty')).toBeVisible();
    await expect(page.locator('.empty')).toContainText('没有找到匹配的文件');
  });

  test('should clear search and restore directory view', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    // First go to /data
    await page.goto(`${baseUrl}/data`);
    await expect(page.locator('table tbody tr')).not.toHaveCount(0);

    // Search something
    await page.locator('#searchInput').fill('hello');
    await page.locator('#searchBtn').click();
    await expect(page.locator('#clearBtn')).toBeVisible();

    // Click clear button
    await page.locator('#clearBtn').click();
    await expect(page.locator('#clearBtn')).toBeHidden();
    await expect(page.locator('#searchInput')).toHaveValue('');

    // Directory view should be restored
    await expect(page).toHaveURL(`${baseUrl}/data`);
    const names = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim()));
    expect(names).toContain('apple.txt');
    expect(names).toContain('zebra.txt');
  });

  test('should navigate to target directory when clicking directory from search results', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    await page.locator('#searchInput').fill('subfolder');
    await page.locator('#searchBtn').click();

    // Click 'subfolder' in search results
    await page.locator('table tbody tr td.name a:has-text("subfolder")').click();

    // Should navigate into /data/subfolder and exit search mode
    await expect(page).toHaveURL(`${baseUrl}/data/subfolder`);
    await expect(page.locator('#clearBtn')).toBeHidden();

    // Subfolder items should be shown
    const names = await page
      .locator('table tbody tr td.name a')
      .evaluateAll((nodes) => nodes.map((n) => n.textContent.replace(/^[^\w\u4e00-\u9fa5\.-]+/, '').trim()));
    expect(names).toContain('deep');
    expect(names).toContain('nested.txt');
  });
});

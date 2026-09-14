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

  test('should adjust font size scale and persist in localStorage', async ({ appPage }) => {
    const { page } = appPage;

    const fontVal = page.locator('#fontReset');
    const incBtn = page.locator('#fontInc');
    const decBtn = page.locator('#fontDec');

    await expect(fontVal).toBeVisible();
    await expect(fontVal).toHaveText('100%');

    // Click A+ twice to step from 100% -> 115% -> 130%
    await incBtn.click();
    await expect(fontVal).toHaveText('115%');
    await incBtn.click();
    await expect(fontVal).toHaveText('130%');

    // Check zoom and localStorage
    const saved = await page.evaluate(() => ({
      zoom: document.body.style.zoom,
      ls: localStorage.getItem('filelist_zoom')
    }));
    expect(saved.zoom).toBe('1.3');
    expect(saved.ls).toBe('1.3');

    // Reload page to verify persistence
    await page.reload();
    await expect(page.locator('#fontReset')).toHaveText('130%');
    const reloadedZoom = await page.evaluate(() => document.body.style.zoom);
    expect(reloadedZoom).toBe('1.3');

    // Click middle button to reset to 100%
    await page.locator('#fontReset').click();
    await expect(page.locator('#fontReset')).toHaveText('100%');
    const resetZoom = await page.evaluate(() => document.body.style.zoom);
    expect(resetZoom).toBe('1');
  });

  test('should adapt layout cleanly to mobile viewport (400x840) without overflow', async ({ appPage }) => {
    const { page } = appPage;

    // Set mobile viewport 400x840 (user phone screen)
    await page.setViewportSize({ width: 400, height: 840 });

    const homeLink = page.locator('#homeLink');
    const fontCtrl = page.locator('.font-ctrl');
    const searchBar = page.locator('.search-bar');
    const searchInput = page.locator('#searchInput');
    const searchBtn = page.locator('#searchBtn');

    await expect(homeLink).toBeVisible();
    await expect(fontCtrl).toBeVisible();
    await expect(searchBar).toBeVisible();
    await expect(searchInput).toBeVisible();
    await expect(searchBtn).toBeVisible();

    // Verify Row 1: homeLink and fontCtrl are on the first row (same top offset range)
    const homeBox = await homeLink.boundingBox();
    const fontBox = await fontCtrl.boundingBox();
    const searchBox = await searchBar.boundingBox();

    expect(homeBox).not.toBeNull();
    expect(fontBox).not.toBeNull();
    expect(searchBox).not.toBeNull();

    // homeLink and fontCtrl should be on row 1 (roughly same Y position)
    expect(Math.abs(homeBox.y - fontBox.y)).toBeLessThan(10);

    // searchBar should be below row 1 (y position is greater than row 1 y + height)
    expect(searchBox.y).toBeGreaterThanOrEqual(homeBox.y + homeBox.height - 5);

    // Check no horizontal scrollbar on root document (scrollWidth <= clientWidth)
    const overflowCheck = await page.evaluate(() => {
      const root = document.documentElement;
      return {
        scrollWidth: root.scrollWidth,
        clientWidth: root.clientWidth,
        hasOverflow: root.scrollWidth > root.clientWidth
      };
    });
    expect(overflowCheck.hasOverflow).toBe(false);

    // Test font magnification on mobile (zoom to 130%)
    await page.locator('#fontInc').click();
    await page.locator('#fontInc').click();
    await expect(page.locator('#fontReset')).toHaveText('130%');

    // Verify still no horizontal overflow under 130% zoom
    const zoomedOverflow = await page.evaluate(() => {
      const root = document.documentElement;
      return root.scrollWidth > root.clientWidth;
    });
    expect(zoomedOverflow).toBe(false);

    // Reset zoom
    await page.locator('#fontReset').click();
    await expect(page.locator('#fontReset')).toHaveText('100%');
  });

  test('should display server version, git commit hash, and build time in footer', async ({ appPage }) => {
    const { page } = appPage;
    const footer = page.locator('.site-footer');
    await expect(footer).toBeVisible();
    await expect(footer).toContainText('FileList v0.2.0');
    await expect(footer.locator('.commit')).toBeVisible();
    const commitText = await footer.locator('.commit').innerText();
    expect(commitText.length).toBeGreaterThan(0);
    await expect(footer.locator('.build-time')).toContainText('构建于');
  });
});

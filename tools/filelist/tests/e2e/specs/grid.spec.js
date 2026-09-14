const { test, expect } = require('../fixtures');

test.describe('Image Grid Mode & Lightbox Viewer', () => {
  test('should toggle between list and grid mode and persist in localStorage', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    // Navigate to /data/images
    await page.goto(`${baseUrl}/data/images`);
    await expect(page.locator('#viewListBtn')).toBeVisible();
    await expect(page.locator('#viewGridBtn')).toBeVisible();

    // Default should be list mode
    await expect(page.locator('table')).toBeVisible();
    await expect(page.locator('.grid-wrap')).toBeHidden();
    await expect(page.locator('#viewListBtn')).toHaveClass(/active/);

    // Switch to grid mode
    await page.locator('#viewGridBtn').click();
    await expect(page.locator('table')).toBeHidden();
    await expect(page.locator('.grid-wrap')).toBeVisible();
    await expect(page.locator('#viewGridBtn')).toHaveClass(/active/);

    // Verify localStorage persistence
    const savedMode = await page.evaluate(() => localStorage.getItem('filelist_view_mode'));
    expect(savedMode).toBe('grid');

    // Reload and check grid mode is retained
    await page.reload();
    await expect(page.locator('.grid-wrap')).toBeVisible();
    await expect(page.locator('table')).toBeHidden();
    await expect(page.locator('#viewGridBtn')).toHaveClass(/active/);

    // Switch back to list mode
    await page.locator('#viewListBtn').click();
    await expect(page.locator('table')).toBeVisible();
    await expect(page.locator('.grid-wrap')).toBeHidden();
  });

  test('should render image cards with thumbnails and support navigation in grid view', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    // Start in /data in grid mode
    await page.goto(`${baseUrl}/data`);
    await page.locator('#viewGridBtn').click();
    await expect(page.locator('.grid-wrap')).toBeVisible();

    // Check folder cards in grid view
    const folderCard = page.locator('.grid-card.is-dir:has-text("images")');
    await expect(folderCard).toBeVisible();

    // Click folder to navigate into /data/images
    await folderCard.locator('a.grid-thumb').click();
    await expect(page).toHaveURL(`${baseUrl}/data/images`);

    // Verify breadcrumb updated
    expect(await page.locator('#breadcrumb').innerText()).toContain('images');

    // Verify image cards exist
    const imgCards = page.locator('.grid-card.is-img');
    const count = await imgCards.count();
    expect(count).toBeGreaterThan(0);

    // Check thumbnail image has loading="lazy"
    const firstImg = imgCards.first().locator('img.grid-img');
    await expect(firstImg).toHaveAttribute('loading', 'lazy');
    const src = await firstImg.getAttribute('src');
    expect(src).toContain('/raw/data/images/');
  });

  test('should open full-screen lightbox on image click and support navigation and shortcuts', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    await page.goto(`${baseUrl}/data/images`);
    await page.locator('#viewGridBtn').click();

    const imgCards = page.locator('.grid-card.is-img');
    const totalImgs = await imgCards.count();
    expect(totalImgs).toBeGreaterThanOrEqual(1);

    // Lightbox initially hidden
    await expect(page.locator('#lightboxOverlay')).toBeHidden();

    // Click first image thumbnail
    await imgCards.first().locator('.grid-thumb').click();

    // Lightbox opens
    const overlay = page.locator('#lightboxOverlay');
    await expect(overlay).toBeVisible();

    // Check counter and image displayed
    const counter = page.locator('.lightbox-counter');
    await expect(counter).toContainText(`1 / ${totalImgs}`);

    const largeImg = page.locator('.lightbox-img');
    await expect(largeImg).toBeVisible();
    const firstSrc = await largeImg.getAttribute('src');
    expect(firstSrc).toBeTruthy();

    if (totalImgs > 1) {
      // Navigate next via keyboard ArrowRight
      await page.keyboard.press('ArrowRight');
      await expect(counter).toContainText(`2 / ${totalImgs}`);

      // Navigate prev via click on Prev button
      await page.locator('#lightboxPrev').click();
      await expect(counter).toContainText(`1 / ${totalImgs}`);
    }

    // Close via Escape key
    await page.keyboard.press('Escape');
    await expect(overlay).toBeHidden();
  });

  test('should render properly in mobile viewport (400x840) without horizontal overflow', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    await page.setViewportSize({ width: 400, height: 840 });
    await page.goto(`${baseUrl}/data/images`);
    await page.locator('#viewGridBtn').click();

    await expect(page.locator('.grid-wrap')).toBeVisible();

    // Check page has no horizontal overflow
    const hasHorizontalOverflow = await page.evaluate(() => {
      return document.documentElement.scrollWidth > document.documentElement.clientWidth;
    });
    expect(hasHorizontalOverflow).toBe(false);

    // Click image to verify lightbox fits mobile screen
    const imgCards = page.locator('.grid-card.is-img');
    if (await imgCards.count() > 0) {
      await imgCards.first().locator('.grid-thumb').click();
      await expect(page.locator('#lightboxOverlay')).toBeVisible();

      // Check lightbox buttons are accessible
      await expect(page.locator('.lightbox-btn.close')).toBeVisible();
      await page.locator('.lightbox-btn.close').click();
      await expect(page.locator('#lightboxOverlay')).toBeHidden();
    }
  });
});

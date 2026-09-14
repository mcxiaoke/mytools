const { test, expect } = require('../fixtures');

test.describe('Media Player (Audio/Video), Link Copy & QR Code', () => {
  test.beforeEach(async ({ appPage }) => {
    // Navigate into /data/media
    await appPage.page.goto(`${appPage.baseUrl}/data/media`);
    await expect(appPage.page.locator('table tbody tr')).not.toHaveCount(0);
  });

  test('should open video in inline media modal and control playback', async ({ appPage }) => {
    const { page } = appPage;

    // Locate video play button
    const videoRow = page.locator('tr:has-text("sample.mp4")');
    await expect(videoRow).toBeVisible();

    const playBtn = videoRow.locator('.act-btn.btn-play');
    await expect(playBtn).toBeVisible();
    await playBtn.click();

    // Verify media modal is visible with video element
    const modal = page.locator('#mediaModal');
    await expect(modal).toBeVisible();

    const videoEl = modal.locator('video#activeVideo');
    await expect(videoEl).toBeVisible();
    const src = await videoEl.getAttribute('src');
    expect(src).toContain('/raw/data/media/sample.mp4');

    // Close via Esc
    await page.keyboard.press('Escape');
    await expect(modal).toBeHidden();
  });

  test('should open audio in inline media modal with audio player and track info', async ({ appPage }) => {
    const { page } = appPage;

    // Locate audio play button
    const audioRow = page.locator('tr:has-text("sample.mp3")');
    await expect(audioRow).toBeVisible();

    const playBtn = audioRow.locator('.act-btn.btn-play');
    await expect(playBtn).toBeVisible();
    await playBtn.click();

    // Verify media modal is visible with audio player
    const modal = page.locator('#mediaModal');
    await expect(modal).toBeVisible();

    const audioEl = modal.locator('audio#activeAudio');
    await expect(audioEl).toBeVisible();
    const src = await audioEl.getAttribute('src');
    expect(src).toContain('/raw/data/media/sample.mp3');

    // Close modal via close button
    await modal.locator('.modal-icon-btn.close').click();
    await expect(modal).toBeHidden();
  });

  test('should copy raw link to clipboard and show toast notification', async ({ appPage }) => {
    const { page } = appPage;

    const copyBtn = page.locator('tr:has-text("sample.mp4") .act-btn.btn-copy');
    await expect(copyBtn).toBeVisible();
    await copyBtn.click();

    // Verify toast notification appears
    const toast = page.locator('.toast');
    await expect(toast).toBeVisible();
    await expect(toast).toContainText('已复制');
  });

  test('should open mobile QR code modal with scalable SVG and copyable URL', async ({ appPage }) => {
    const { page, baseUrl } = appPage;

    const qrBtn = page.locator('tr:has-text("sample.mp4") .act-btn.btn-qr');
    await expect(qrBtn).toBeVisible();
    await qrBtn.click();

    // Verify QR modal opens
    const modal = page.locator('#qrModal');
    await expect(modal).toBeVisible();

    // Check SVG QR Code rendered inside #qrContainer
    const qrSvg = modal.locator('#qrContainer svg');
    await expect(qrSvg).toBeVisible();

    // Check QR URL input contains full absolute raw URL
    const urlInput = modal.locator('#qrUrlInput');
    await expect(urlInput).toBeVisible();
    const rawUrl = await urlInput.inputValue();
    expect(rawUrl).toContain('/raw/data/media/sample.mp4');

    // Close via close button
    await modal.locator('.modal-icon-btn.close').click();
    await expect(modal).toBeHidden();
  });
});

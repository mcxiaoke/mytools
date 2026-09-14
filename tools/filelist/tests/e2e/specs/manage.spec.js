const fs = require('fs');
const path = require('path');
const { test, expect } = require('../fixtures');

test.describe('File Management Operations (Mkdir, Edit, Rename, Zip, Safe Delete)', () => {
  test.beforeEach(async ({ appPage }) => {
    await appPage.page.goto(`${appPage.baseUrl}/data`);
    await expect(appPage.page.locator('table tbody tr')).not.toHaveCount(0);
  });

  test('should create a new folder via mkdir modal', async ({ appPage }) => {
    const { page, baseUrl, server } = appPage;

    const mkdirBtn = page.locator('#mkdirBtn');
    await expect(mkdirBtn).toBeVisible();
    await mkdirBtn.click();

    const modal = page.locator('#mkdirModal');
    await expect(modal).toBeVisible();

    const input = page.locator('#mkdirInput');
    await input.fill('e2e-created-dir');

    await page.locator('#confirmMkdirBtn').click();
    await expect(modal).toBeHidden();

    // Verify folder row is created in table
    const folderRow = page.locator('tr:has-text("e2e-created-dir")');
    await expect(folderRow).toBeVisible();

    // Navigate into the new folder
    await folderRow.locator('td.name a').click();
    await expect(page).toHaveURL(`${baseUrl}/data/e2e-created-dir`);
    await expect(page.locator('.empty')).toBeVisible();

    // Clean up created folder
    try {
      fs.rmSync(path.join(server.fixtures.root1, 'e2e-created-dir'), { recursive: true, force: true });
    } catch (_) {}
  });

  test('should open text file in editor modal, modify content, and save successfully', async ({ appPage }) => {
    const { page, server } = appPage;

    // Create dedicated edit test file
    const editFile = path.join(server.fixtures.root1, 'edit-target.txt');
    fs.writeFileSync(editFile, 'Initial Line 1\nInitial Line 2\n', 'utf-8');
    await page.reload();

    const editBtn = page.locator('tr:has-text("edit-target.txt") .act-btn.btn-edit');
    await expect(editBtn).toBeVisible();
    await editBtn.click();

    const modal = page.locator('#textModal');
    await expect(modal).toBeVisible();

    const textarea = page.locator('#textEditorArea');
    await expect(textarea).toBeVisible();
    await expect(textarea).toHaveValue(/Initial Line 1/);

    // Edit content
    const updatedContent = 'Updated Content by Playwright Test Suite\nLine 2';
    await textarea.fill(updatedContent);

    // Verify dirty indicator is shown
    await expect(page.locator('.modal-badge-dirty')).toBeVisible();

    // Click save
    const saveBtn = page.locator('#saveTextBtn');
    await saveBtn.click();

    // Verify toast or save status
    await expect(page.locator('.toast')).toContainText('保存成功');

    // Close modal
    await modal.locator('.close').click();
    await expect(modal).toBeHidden();

    // Reopen and check content persisted
    await editBtn.click();
    await expect(modal).toBeVisible();
    await expect(textarea).toHaveValue(updatedContent);
    await modal.locator('.close').click();

    // Clean up
    try { fs.rmSync(editFile, { force: true }); } catch (_) {}
  });

  test('should rename a file successfully', async ({ appPage }) => {
    const { page, server } = appPage;

    // Create dedicated rename test file
    const origFile = path.join(server.fixtures.root1, 'rename-target.txt');
    const destFile = path.join(server.fixtures.root1, 'renamed-ok.txt');
    fs.writeFileSync(origFile, 'Rename Me Content\n', 'utf-8');
    await page.reload();

    const renameBtn = page.locator('tr:has-text("rename-target.txt") .act-btn.btn-rename');
    await expect(renameBtn).toBeVisible();
    await renameBtn.click();

    const modal = page.locator('#renameModal');
    await expect(modal).toBeVisible();

    const input = page.locator('#renameInput');
    await expect(input).toHaveValue('rename-target.txt');
    await input.fill('renamed-ok.txt');

    await page.locator('#confirmRenameBtn').click();
    await expect(modal).toBeHidden();

    // Verify rename reflected in table
    await expect(page.locator('tr:has-text("renamed-ok.txt")')).toBeVisible();
    await expect(page.locator('tr:has-text("rename-target.txt")')).toHaveCount(0);

    // Clean up
    try { fs.rmSync(destFile, { force: true }); } catch (_) {}
  });

  test('should stream download directory as ZIP', async ({ appPage }) => {
    const { page } = appPage;

    // Test row zip action on subfolder
    const zipBtn = page.locator('tr:has-text("subfolder") .act-btn.btn-zip');
    await expect(zipBtn).toBeVisible();

    const downloadPromise = page.waitForEvent('download');
    await zipBtn.click();
    const download = await downloadPromise;

    expect(download.suggestedFilename()).toBe('subfolder.zip');

    // Test toolbar zip button for current directory
    const tbZipBtn = page.locator('#zipCurrentBtn');
    await expect(tbZipBtn).toBeVisible();

    const tbDownloadPromise = page.waitForEvent('download');
    await tbZipBtn.click();
    const tbDownload = await tbDownloadPromise;
    expect(tbDownload.suggestedFilename()).toBe('data.zip');
  });

  test('should protect deletion with deleteToken: reject on bad token and succeed on valid token', async ({ appPage }) => {
    const { page, server } = appPage;

    // Create dedicated delete test file
    const delFile = path.join(server.fixtures.root1, 'delete-target.txt');
    fs.writeFileSync(delFile, 'Delete Me Content\n', 'utf-8');
    await page.reload();

    const delBtn = page.locator('tr:has-text("delete-target.txt") .act-btn.btn-del');
    await expect(delBtn).toBeVisible();
    await delBtn.click();

    const modal = page.locator('#deleteModal');
    await expect(modal).toBeVisible();

    const tokenInput = page.locator('#deleteTokenInput');
    const confirmBtn = page.locator('#confirmDeleteBtn');

    // 1. Attempt delete with WRONG token
    await tokenInput.fill('wrong-secret-token');
    await confirmBtn.click();

    // Should show error and remain visible
    const errMsg = modal.locator('.modal-err-msg');
    await expect(errMsg).toBeVisible();
    await expect(errMsg).toContainText(/invalid delete token|口令|失败/i);

    // Verify file is NOT deleted
    await modal.locator('.close').click();
    await expect(modal).toBeHidden();
    await expect(page.locator('tr:has-text("delete-target.txt")')).toBeVisible();

    // 2. Attempt delete with CORRECT token
    await delBtn.click();
    await expect(modal).toBeVisible();
    await tokenInput.fill('e2e-secret-delete-token');
    await confirmBtn.click();

    // Should close modal and remove file from table
    await expect(modal).toBeHidden();
    await expect(page.locator('tr:has-text("delete-target.txt")')).toHaveCount(0);
  });
});

import { isProtected, originKey, isExtensionUrl } from './lib.js';

// ---------- session 存储兼容层（Chrome <102 降级） ----------
const hasSession = !!(chrome.storage && chrome.storage.session);
async function sessGet(keys) {
  if (hasSession) return chrome.storage.session.get(keys);
  return chrome.storage.local.get(keys); // 降级：用 local 模拟，但会在 onStartup 清理
}
async function sessSet(obj) {
  if (hasSession) return chrome.storage.session.set(obj);
  return chrome.storage.local.set(obj);
}

// ---------- 核心拦截逻辑 ----------
async function shouldLock(url) {
  if (!url || isExtensionUrl(url)) return false;
  if (!url.startsWith('http://') && !url.startsWith('https://')) return false;
  const { protected: list = [] } = await chrome.storage.local.get('protected');
  const { cred } = await chrome.storage.local.get('cred');
  if (!cred) return false; // 未设 PIN 时不锁，引导去设置页（避免死锁）
  if (!isProtected(url, list)) return false;
  const { unlocked = [] } = await sessGet('unlocked');
  if (unlocked.includes(originKey(url))) return false;
  return true;
}

async function redirectToLock(tabId, targetUrl) {
  try {
    const tab = await chrome.tabs.get(tabId).catch(() => null);
    if (!tab) return;
    const { pending = {} } = await sessGet('pending');
    // 合并写入，不覆盖其他 tab 的 pending
    pending[tabId] = targetUrl;
    await sessSet({ pending });
    const lockUrl = chrome.runtime.getURL('lock.html') + '?tab=' + tabId;
    await chrome.tabs.update(tabId, { url: lockUrl });
  } catch (e) {
    console.warn('[PIN Lock] redirect failed', e);
  }
}

async function handleNavigation(details) {
  // 只处理顶层
  if (details.frameId !== 0) return;
  // parentFrameId === -1 双保险，确保是顶层
  if (typeof details.parentFrameId !== 'undefined' && details.parentFrameId !== -1) return;
  const url = details.url;
  if (await shouldLock(url)) {
    await redirectToLock(details.tabId, url);
  }
}

// A1 默认：onBeforeNavigate 保留深链，补充 onHistoryStateUpdated 覆盖 SPA
chrome.webNavigation.onBeforeNavigate.addListener(handleNavigation, { url: [{ schemes: ['http', 'https'] }] });
chrome.webNavigation.onHistoryStateUpdated.addListener(handleNavigation, { url: [{ schemes: ['http', 'https'] }] });
// 不监听 onCommitted：该事件触发时页面已开始加载，会造成受保护内容闪现，
// 且与 onBeforeNavigate 双重处理同一导航。会话恢复等极端场景漏拦属可接受损失（本扩展定位为门禁）。

// tab 关闭时清理 pending，避免泄漏
chrome.tabs.onRemoved.addListener(async (tabId) => {
  const { pending = {} } = await sessGet('pending');
  if (pending[tabId]) {
    delete pending[tabId];
    await sessSet({ pending });
  }
});

// 未设 PIN 时首次命中受保护地址，引导到设置页（每个浏览器会话仅提示一次，避免反复弹窗）
chrome.webNavigation.onBeforeNavigate.addListener(async (details) => {
  if (details.frameId !== 0) return;
  const { cred } = await chrome.storage.local.get('cred');
  if (cred) return;
  const { protected: list = [] } = await chrome.storage.local.get('protected');
  if (!isProtected(details.url, list)) return;
  const { noPinHintShown = false } = await sessGet('noPinHintShown');
  if (noPinHintShown) return;
  await sessSet({ noPinHintShown: true });
  console.warn('[PIN Lock] protected hit but no PIN set, open options');
  chrome.runtime.openOptionsPage();
}, { url: [{ schemes: ['http', 'https'] }] });

// ---------- 空闲自动锁定 ----------
async function applyIdle() {
  const { idleMinutes = 10 } = await chrome.storage.local.get('idleMinutes');
  const seconds = Math.max(15, Math.trunc(Number(idleMinutes) * 60) || 600);
  chrome.idle.setDetectionInterval(seconds);
}

async function autoLock() {
  const { cred } = await chrome.storage.local.get('cred');
  if (!cred) return; // 与 shouldLock 保持一致：未设 PIN 不做任何锁动作
  const { protected: list = [] } = await chrome.storage.local.get('protected');
  // 1) 软锁：清空本会话解锁态
  await sessSet({ unlocked: [] });

  // 2) 硬锁：重定向已打开的受保护标签（合并 pending，不丢弃）
  const tabs = await chrome.tabs.query({}).catch(() => []);
  const { pending: oldPending = {} } = await sessGet('pending');
  const pending = { ...oldPending };
  for (const tab of tabs) {
    if (!tab.url || isExtensionUrl(tab.url)) continue;
    if (!isProtected(tab.url, list)) continue;
    pending[tab.id] = tab.url;
    const lockUrl = chrome.runtime.getURL('lock.html') + '?tab=' + tab.id;
    try { await chrome.tabs.update(tab.id, { url: lockUrl }); } catch {}
  }
  await sessSet({ pending });
}

chrome.idle.onStateChanged.addListener((state) => {
  console.warn('[PIN Lock] idle state ->', state);
  if (state === 'idle' || state === 'locked') autoLock();
});

chrome.runtime.onInstalled.addListener(applyIdle);
chrome.runtime.onStartup.addListener(async () => {
  // 降级模式下清理模拟的 session 数据
  if (!hasSession) await chrome.storage.local.remove(['unlocked', 'pending', 'noPinHintShown', 'lockRate']);
  applyIdle();
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.idleMinutes) applyIdle();
});

// SW 启动时立即应用一次
applyIdle();

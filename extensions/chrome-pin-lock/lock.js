import { verifyPin, originKey } from './lib.js';

const params = new URLSearchParams(location.search);
const rawTabId = params.get('tab');
const tabId = rawTabId ? String(parseInt(rawTabId, 10)) : null;

const hasSession = !!(chrome.storage && chrome.storage.session);
const sessGet = (k) => hasSession ? chrome.storage.session.get(k) : chrome.storage.local.get(k);
const sessSet = (o) => hasSession ? chrome.storage.session.set(o) : chrome.storage.local.set(o);

const hintEl = document.getElementById('hint');
const msgEl = document.getElementById('msg');
const pinEl = document.getElementById('pin');
const okBtn = document.getElementById('ok');

// 读取 pending 目标
const store = await sessGet('pending');
const target = tabId ? (store.pending || {})[tabId] : null;

if (!target) {
  hintEl.textContent = '未找到待解锁地址，可能是标签已关闭或会话已过期。请重新访问目标站点。';
  okBtn.disabled = true;
} else {
  // 仅展示 origin，避免深链在共享屏幕时泄露敏感路径（不放 title/hover 提示）
  try { hintEl.textContent = '即将访问：' + new URL(target).origin + ' （完整路径已隐藏）'; }
  catch { hintEl.textContent = '即将访问：' + target; }
}

// 未设 PIN 时引导（链接点击行为在下方统一绑定一次）
const { cred } = await chrome.storage.local.get('cred');
if (!cred) {
  msgEl.textContent = '尚未设置 PIN，请先去设置页创建。';
  okBtn.disabled = true;
}

// 限流：5 次失败锁定 30 秒（体验兜底，非安全机制）
const RATE_KEY = 'lockRate';
async function checkRate() {
  const { [RATE_KEY]: rate = { count: 0, until: 0 } } = await sessGet(RATE_KEY);
  if (Date.now() < rate.until) {
    const sec = Math.ceil((rate.until - Date.now()) / 1000);
    msgEl.textContent = `尝试次数过多，请 ${sec} 秒后再试`;
    okBtn.disabled = true;
    setTimeout(() => { okBtn.disabled = false; msgEl.textContent = ''; }, rate.until - Date.now());
    return false;
  }
  return true;
}
async function recordFail() {
  const { [RATE_KEY]: rate = { count: 0, until: 0 } } = await sessGet(RATE_KEY);
  const next = { count: rate.count + 1, until: rate.until };
  if (next.count >= 5) { next.until = Date.now() + 30_000; next.count = 0; }
  await sessSet({ [RATE_KEY]: next });
}
async function resetRate() { await sessSet({ [RATE_KEY]: { count: 0, until: 0 } }); }

document.getElementById('goOptions').onclick = (e) => { e.preventDefault(); chrome.runtime.openOptionsPage(); };

okBtn.onclick = async () => {
  if (!await checkRate()) return;
  const pin = pinEl.value;
  if (!pin) { msgEl.textContent = '请输入 PIN'; return; }
  okBtn.disabled = true;
  const ok = await verifyPin(pin);
  if (!ok) {
    await recordFail();
    msgEl.textContent = 'PIN 错误';
    okBtn.disabled = false;
    return;
  }
  await resetRate();
  // 去重写入 unlocked
  const u = await sessGet('unlocked');
  const set = new Set(u.unlocked || []);
  if (target) set.add(originKey(target));
  await sessSet({ unlocked: [...set] });

  // 清理 pending（合并写入，避免覆盖其他 tab）
  const p = await sessGet('pending');
  const pending = p.pending || {};
  if (tabId) delete pending[tabId];
  await sessSet({ pending });

  // 确保 unlocked 已落盘再跳转，避免 SW 侧竞争回跳
  if (target) location.replace(target);
  else location.replace('about:blank');
};

pinEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') okBtn.click(); });

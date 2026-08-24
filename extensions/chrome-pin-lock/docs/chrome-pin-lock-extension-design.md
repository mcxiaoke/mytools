# Chrome PIN 站点锁 · 扩展设计文档（方案 A：纯 MV3 扩展）

> 目标：访问指定域名 / IP 前必须输入 PIN 码；纯客户端实现，无后端、无第三方依赖。
> 适用：Chrome / Edge（Chromium 系，Manifest V3，Chrome 102+）。
> 定位：**威慑 / 便利门禁**，不是加密级保密；可被技术用户绕过（见 §8）。

---

## 1. 目标与威胁模型

| 项 | 说明 |
|---|---|
| 要解决的问题 | 他人借用/瞥见浏览器时，不能直接打开某些站点（如后台、内网 IP、管理页） |
| 锁定的对象 | 指定的 `域名`、`IP（含端口）`、可选 `URL 前缀` |
| 触发时机 | 顶层导航（`main_frame`）进入受保护地址时，含 `pushState` 产生的 SPA 导航 |
| 防护强度定位 | **威慑 / 便利门禁**，不是加密级保密 |
| 可被绕过的方式 | 禁用扩展、`--disable-extensions` 启动、开隐身模式（若未授权）、直接读磁盘 `storage`、SPA 内部路由未触发导航（已在本文修复） |

> ⚠️ **前提认知**：任何纯客户端锁都能被有技术的用户绕过。本方案的价值是“挡住非技术用户 / 防止误操作”，而非“保护机密数据”。真要保密请用系统层加密（方案 D）。

---

## 2. 总体架构

运行两个进程角色：

- **Service Worker（`background.js`）**：监听导航事件，判断是否需要锁，命中则把目标 URL 暂存并跳转到密码页。
- **密码页（`lock.html` + `lock.js`）**：输入 PIN，校验哈希，通过后把该 origin 写入“会话已解锁”集合并跳回原 URL。
- **设置页（`options.html`）**：配置受保护列表、设置 / 修改 PIN、配置空闲自动锁。
- **公共库（`lib.js`）**：PIN 派生哈希、校验、URL 匹配等纯函数（ES Module，Service Worker 与页面共用）。

### 两种拦截实现（任选其一）

| 方案 | 机制 | 能否保留原始深链 URL | 复杂度 | 推荐 |
|---|---|---|---|---|
| **A1（默认）** | `webNavigation.onBeforeNavigate` + `onHistoryStateUpdated` + `tabs.update` 重定向 | ✅ 完整保留（含路径/端口） | 低 | ✅ 主推 |
| A2（变体） | `declarativeNetRequest` 规则 `redirect` 到密码页 | ⚠️ 会丢失具体路径，仅适合“锁整域根” | 中 | 仅作 SW 休眠兜底时可选 |

> 下文以 **A1** 给出完整代码；A2 在 §7 末尾给出差异说明。

### 会话解锁流程（A1 修正版）

```
浏览器输入/点击受保护地址（含 SPA pushState）
        │
        ▼
webNavigation.onBeforeNavigate / onHistoryStateUpdated (main_frame)
        │  isProtected(url) == true 且 该 origin 未在本会话解锁 且 cred 已存在
        ▼
session.pending[tabId] = url  （合并写入，不覆盖其他 tab）
tabs.update(tabId, { url: lock.html?tab=<tabId> })
        │
        ▼
lock.html 读取 pending[tabId] 得到原 URL（若为空则提示异常，不跳转）
输入 PIN → verifyPin() 比对 storage.local 中的 salt+hash（PBKDF2 直存，无二次 SHA-256）
        │
   ┌────┴─────┐
 失败        成功
  │           │
 停留/限流    unlocked = Set(add origin) 去重后写入 session
（5次锁定30s） 删除 pending[tabId]
            location.replace(原 URL)  // 已 await session 写入，避免竞争回跳
```

---

## 3. 文件结构

```
chrome-pin-lock/
├── manifest.json
├── background.js        # Service Worker (type: module)
├── lib.js               # 公共函数（ES Module）
├── lock.html            # 密码页
├── lock.js              # 密码页逻辑（type=module）
├── options.html         # 设置页
├── options.js           # 设置页逻辑（type=module）
└── icons/
    ├── icon16.png
    ├── icon48.png
    └── icon128.png
```

---

## 4. 实现细节

### 4.1 `manifest.json`（已修正）

```json
{
  "manifest_version": 3,
  "name": "PIN Site Lock",
  "version": "1.0.0",
  "description": "访问指定域名 / IP 前需输入 PIN 码",
  "minimum_chrome_version": "102",
  "permissions": ["webNavigation", "storage", "tabs", "idle"],
  "host_permissions": ["<all_urls>"],
  "background": { "service_worker": "background.js", "type": "module" },
  "options_ui": { "page": "options.html", "open_in_tab": true },
  "action": { "default_title": "PIN Site Lock" },
  "incognito": "spanning",
  "icons": {
    "16": "icons/icon16.png",
    "48": "icons/icon48.png",
    "128": "icons/icon128.png"
  }
}
```

**相对原文档的修正说明：**

- `background.type: "module"`：原文档缺失，`background.js` 使用 `import` 会直接导致 SW 启动失败 `SyntaxError`，此为阻塞级 Bug。
- 删除 `web_accessible_resources`：`lock.html` 是 `chrome-extension://<id>/lock.html` 跳转目标，不是供网页注入的资源，不需要 WAR；原配置对外暴露增加指纹面。
- `options_page` → `options_ui`：`options_page` 在 MV3 已废弃，改用 `options_ui`，`open_in_tab: true` 体验更好。
- 新增 `minimum_chrome_version: "102"`：`chrome.storage.session` 需 102+，显式声明避免低版本静默失败。
- 新增 `incognito: "spanning"`：与单例共享同一份 `session` 状态，配合文档说明引导用户去 `chrome://extensions` 开启“允许隐身模式”，否则隐身窗口直接绕过。
- 新增 `icons`：商店上架必需，本地自用也避免扩展列表空白图标。
- `host_permissions: "<all_urls>"` 保留但需在商店描述中给出 justification：`webNavigation` 过滤器需覆盖用户自定义的任意域名/IP，否则无法拦截；如需更严格可改 `optional_host_permissions` 动态申请（见 §5）。

- `permissions` 中 `tabs` 用于 `tabs.query/update/get`，`idle` 用于空闲锁，`storage` 同时覆盖 `local` 与 `session`。

### 4.2 `lib.js`（公共库，已修正）

```js
// ---------- URL 匹配（修正版） ----------
export function isProtected(url, list = []) {
  let u;
  try { u = new URL(url); } catch { return false; }
  const host = u.hostname.toLowerCase();
  const port = u.port;
  const hostWithPort = port ? `${host}:${port}` : host;
  const fullUrl = url.toLowerCase(); // 整体小写化（含路径），本扩展定位为门禁，路径按不区分大小写处理

  return list.some((item) => {
    if (!item || !item.value) return false;
    const raw = String(item.value).trim();
    if (!raw) return false;
    const value = raw.toLowerCase();

    if (item.type === 'domain') {
      // 兼容用户误填 "example.com:3000/path" 的情况，截断到纯 domain
      const domain = value.split('/')[0].split(':')[0].replace(/^\.+/, '');
      if (!domain) return false;
      return host === domain || host.endsWith('.' + domain);
    }
    if (item.type === 'ip') {
      // 支持 "192.168.1.1"（匹配该 IP 任意端口） 和 "192.168.1.1:3000"（精确端口）
      // 仅支持 IPv4；IPv6 字面量含冒号会被误判为端口分隔，暂不支持
      if (value.includes(':')) return hostWithPort === value;
      return host === value;
    }
    if (item.type === 'url') {
      // URL 前缀匹配：归一化规则尾斜杠后做「全等 或 边界前缀」比较，
      // 避免 "https://a.com/admin" 误命中 "https://a.com/admin-evil"
      const rule = value.endsWith('/') ? value.slice(0, -1) : value;
      return fullUrl === rule
        || fullUrl === rule + '/'
        || fullUrl.startsWith(rule + '/');
    }
    return false;
  });
}

export function originKey(url) {
  try { return new URL(url).origin.toLowerCase(); } catch { return String(url).toLowerCase(); }
}

export function isExtensionUrl(url) {
  return String(url).startsWith('chrome-extension://');
}

// ---------- PIN 哈希（Web Crypto / PBKDF2 直存） ----------
const ITER = 150000;

export async function deriveKey(pin, salt, iterations = ITER) {
  const enc = new TextEncoder();
  const mat = await crypto.subtle.importKey(
    'raw', enc.encode(pin), 'PBKDF2', false, ['deriveBits']
  );
  const bits = await crypto.subtle.deriveBits(
    { name: 'PBKDF2', salt, iterations, hash: 'SHA-256' }, mat, 256
  );
  return new Uint8Array(bits);
}

export async function setPin(pin) {
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const key = await deriveKey(pin, salt);
  // 修正：直接存储 PBKDF2 输出，不再二次 SHA-256；二次哈希不增加安全性且增加复杂度
  await chrome.storage.local.set({
    cred: {
      salt: Array.from(salt),
      hash: Array.from(key),
      iterations: ITER,
      createdAt: Date.now()
    }
  });
}

export async function verifyPin(pin) {
  const { cred } = await chrome.storage.local.get('cred');
  if (!cred) return false;
  const salt = new Uint8Array(cred.salt);
  const key = await deriveKey(pin, salt, cred.iterations || ITER);
  const stored = new Uint8Array(cred.hash);
  if (key.length !== stored.length) return false;
  let diff = 0;
  for (let i = 0; i < key.length; i++) diff |= key[i] ^ stored[i];
  return diff === 0; // 常量时间比较（习惯性写法，本场景无侧信道威胁模型）
}
```

**修正要点：**

- `isProtected` 原版 `host === item.value` 对 `IP:端口` 失效；现区分 `hostWithPort` 精确匹配与裸 IP 通配。
- `domain` 匹配前做 `split(':')[0].split('/')[0]` 防止用户把端口/路径写进 domain 导致永不命中。
- `url` 前缀匹配：规则尾斜杠归一化 + 边界前缀（`rule + '/'`），既统一 `https://a.com` 与 `https://a.com/`，又避免 `/admin` 误命中 `/admin-evil`；整体小写化，注释与实现一致。
- `originKey` 统一小写，避免 `https://Example.COM` 与 `https://example.com` 被视为不同 origin 导致重复弹窗。
- 移除 `PBKDF2 -> SHA-256` 二次哈希，直存 PBKDF2 输出。**不保留旧格式迁移**：本项目为新开发、无历史用户数据，迁移代码是死代码。
- `isExtensionUrl` 供 `background` 过滤自身跳转，避免递归。

### 4.3 `background.js`（Service Worker，修正版）

```js
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
  if (state === 'idle' || state === 'locked') autoLock();
});

chrome.runtime.onInstalled.addListener(applyIdle);
chrome.runtime.onStartup.addListener(async () => {
  // 降级模式下清理模拟的 session 数据
  if (!hasSession) await chrome.storage.local.remove(['unlocked', 'pending']);
  applyIdle();
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.idleMinutes) applyIdle();
  // 可选：监听 protected 变更，动态刷新 DNR 规则（A2 叠加时）
});

// SW 启动时立即应用一次
applyIdle();
```

**相对原文档的关键修复：**

- 补充 `onHistoryStateUpdated`，覆盖 SPA `pushState` 导航，原文档仅 `onBeforeNavigate` 会被 SPA 绕过；**不使用** `onCommitted` 兜底（会闪现受保护内容且双重处理）。
- 未设 PIN 的引导监听器用 session 标记 `noPinHintShown` 保证每会话仅提示一次。
- `shouldLock` 前置检查 `cred` 是否存在，未设 PIN 时不重定向，避免死锁；原文档无此分支。
- `redirectToLock` 先 `tabs.get(tabId)` 校验 tab 存在性，加 `try/catch`，合并写入 `pending` 而非覆盖。
- `autoLock` 硬锁时合并写入旧 `pending`，原文档 `const pending = {}` 会丢弃旧数据；且前置 `cred` 检查与 `shouldLock` 行为一致。
- `unlocked` 写入侧（见 `lock.js`）去重，`sessGet/sessSet` 封装兼容 `Chrome <102` 无 `storage.session` 的降级。
- `tabs.onRemoved` 清理 `pending`，防止泄漏。
- `isExtensionUrl` 过滤避免对 `chrome-extension://` 自身导航的递归。
- `applyIdle` 对 `idleMinutes` 做 `Number` 校验，`onStartup` 清理降级数据，启动即调用 `applyIdle`。

### 4.4 `lock.html` + `lock.js`（修正版）

`lock.html`：

```html
<!doctype html>
<html lang="zh">
<head><meta charset="utf-8"><title>需要 PIN</title>
<style>
  body{font-family:system-ui;max-width:480px;margin:40px auto;padding:24px}
  #hint{word-break:break-all;color:#666}
  #msg{color:#c00;min-height:1.2em}
  button:disabled{opacity:.5}
</style>
</head>
<body>
  <h2>此站点已加锁</h2>
  <p id="hint"></p>
  <input id="pin" type="password" placeholder="输入 PIN" autofocus />
  <button id="ok">解锁</button>
  <p id="msg"></p>
  <p><a href="#" id="goOptions">去设置 PIN</a></p>
  <script type="module" src="lock.js"></script>
</body>
</html>
```

`lock.js`：

```js
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

// 限流：5 次失败锁定 30 秒
const RATE_KEY = 'lockRate';
async function checkRate() {
  const { [RATE_KEY]: rate = { count: 0, until: 0 } } = await sessGet(RATE_KEY);
  if (Date.now() < rate.until) {
    const sec = Math.ceil((rate.until - Date.now())/1000);
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

document.getElementById('goOptions').onclick = (e) => { e.preventDefault(); chrome.runtime.openOptionsPage(); }; // 统一绑定一次

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
```

**修正要点：**

- `tabId` 统一 `String(parseInt)`，与 `background` 侧 `pending` 键类型一致。
- `target` 为空时不尝试跳转，禁用按钮并提示，原文档会 `location.replace(undefined)`。
- `cred` 为空时禁用解锁并提供 `openOptionsPage` 入口，解决死锁。
- 限流：`session` 存储失败次数，5 次后锁定 30s，原文档仅文字提示无实现。
- `unlocked` 用 `Set` 去重，原文档 `push` 会无限增长。
- `pending` 清理时先 `get` 再 `delete`，合并写回，原文档直接覆盖。
- `hint` 仅展示 `origin`，减少深链敏感信息在锁屏页暴露；不再放 `title` hover 提示（会直接泄露完整 URL，与隐藏目的矛盾）。
- 顶层 `await` 要求 `lock.html` 以 `type="module"` 加载，已满足；增加 `Enter` 快捷键。

### 4.5 `options.html` + `options.js`（设置受保护列表 & PIN，修正版）

设置页负责：

1. **受保护列表管理**：增删 `{ type: 'domain'|'ip'|'url', value }`，存 `storage.local.protected`，增加格式校验与去重。
2. **设置 / 修改 PIN**：调用 `lib.setPin()`，增加二次确认与强度提示。
3. **空闲时长**：存 `storage.local.idleMinutes`。

示例 `options.js`（核心逻辑，已补全校验与渲染）：

```js
import { setPin } from './lib.js';
import { isProtected } from './lib.js'; // 复用校验

function normalizeValue(type, value) {
  const v = value.trim();
  if (type === 'domain') return v.toLowerCase().replace(/\/.*$/, '').replace(/:.*$/, '');
  if (type === 'ip') return v; // 保留端口，校验在下方
  if (type === 'url') return v;
  return v;
}

function validateItem(type, value) {
  if (!value) return '不能为空';
  if (type === 'domain') {
    if (!/^[a-z0-9.-]+\.[a-z]{2,}$/i.test(value)) return '域名格式不正确';
  }
  if (type === 'ip') {
    if (!/^(\d{1,3}\.){3}\d{1,3}(:\d{1,5})?$/.test(value)) return 'IP 格式不正确，示例：192.168.1.10 或 192.168.1.10:3000';
    const ip = value.split(':')[0];
    if (ip.split('.').some(n => Number(n) > 255)) return 'IP 段超出范围';
  }
  if (type === 'url') {
    try { new URL(value); } catch { return 'URL 需包含协议，示例：https://example.com/admin'; }
    if (!value.startsWith('http://') && !value.startsWith('https://')) return '仅支持 http/https 前缀';
  }
  return null;
}

document.getElementById('add').onclick = async () => {
  const type = document.getElementById('type').value;
  let value = document.getElementById('value').value.trim();
  if (!value) return;
  value = normalizeValue(type, value);
  const err = validateItem(type, value);
  if (err) { alert(err); return; }
  const { protected: list = [] } = await chrome.storage.local.get('protected');
  if (list.some(i => i.type === type && i.value.toLowerCase() === value.toLowerCase())) {
    alert('已存在相同规则'); return;
  }
  list.push({ type, value });
  await chrome.storage.local.set({ protected: list });
  document.getElementById('value').value = '';
  render();
};

document.getElementById('setPin').onclick = async () => {
  const pin = document.getElementById('newpin').value;
  const confirm = document.getElementById('confirmpin').value;
  // 门禁定位：仅要求最少 4 位防误触/防随手乱试，不强制强口令
  if (pin.length < 4) { alert('PIN 至少 4 位'); return; }
  if (pin !== confirm) { alert('两次输入不一致'); return; }
  await setPin(pin);
  alert('PIN 已设置');
  document.getElementById('newpin').value = '';
  document.getElementById('confirmpin').value = '';
};

async function render() {
  const { protected: list = [] } = await chrome.storage.local.get('protected');
  const ul = document.getElementById('list');
  ul.innerHTML = '';
  list.forEach((item, idx) => {
    const li = document.createElement('li');
    li.textContent = `[${item.type}] ${item.value} `;
    const del = document.createElement('button');
    del.textContent = '删除';
    del.onclick = async () => {
      list.splice(idx, 1);
      await chrome.storage.local.set({ protected: list });
      render();
    };
    li.appendChild(del);
    ul.appendChild(li);
  });
}

// 空闲时长
document.getElementById('saveIdle').onclick = async () => {
  const m = parseInt(document.getElementById('idleMin').value, 10);
  // 上限 240：chrome.idle.setDetectionInterval 最大 14400 秒（4 小时），超过会被截断/报错
  if (!(m > 0 && m <= 240)) { alert('请输入 1~240 分钟'); return; }
  await chrome.storage.local.set({ idleMinutes: m });
  alert('已保存，立即生效');
};
async function loadIdle() {
  const { idleMinutes = 10 } = await chrome.storage.local.get('idleMinutes');
  document.getElementById('idleMin').value = idleMinutes;
}
loadIdle();
render();
```

**修正要点：** 原文档 `options.js` 仅骨架，无校验、去重、删除、二次确认；现补全且与 `lib.isProtected` 保持一致的归一化逻辑。PIN 仅要求 ≥4 位（门禁定位，见 §8）；`idleMinutes` 上限 240 分钟，对齐 `setDetectionInterval` 的 API 上限。

### 4.6 空闲自动锁定（idle auto-lock，已修正）

目标：用户离开（如静置 10 分钟）或系统锁屏时，自动“重新上锁”——清空本会话解锁集合，并可选把已打开的受保护标签重定向回密码页。

实现见 `background.js` 的 `applyIdle`/`autoLock`，修正点已在 §4.3 列出，设置页补充 `idleMinutes` 范围校验 `1~240` 分钟，避免 `0` 或负数导致 `setDetectionInterval` 异常，同时不超过 API 的 4 小时上限。

要点：

- `chrome.idle.setDetectionInterval` 最小 15 秒；10 分钟 = 600 秒，代码中 `Math.max(15, ...)` 兜底。
- `state === 'locked'` 表示 OS 已锁屏（Win+L），立即触发。
- **软锁**：仅清 `unlocked`，下次导航才问密码，不打扰当前页面。
- **硬锁**：额外重定向已打开的受保护标签，立刻产生“锁屏感”。两者可二选一或都用；默认建议都用。
- 空闲阈值改完即时生效，无需 reload（靠 `storage.onChanged` 刷新）。
- `onStartup` 时重建 `applyIdle`，避免 SW 休眠后阈值丢失。

---

## 5. 关键设计点（修正版）

| 关注点 | 处理（修正后） |
|---|---|
| **保留深链** | A1 在 `onBeforeNavigate/onHistoryStateUpdated` 时已有完整 URL，存进 `session.pending[tabId]`，解锁后 `location.replace` 回去，路径/端口/参数全保留 |
| **避免每次都弹** | 解锁后把 `origin`（小写）写进 `storage.session.unlocked` 去重集合；SW 每次先查，已解锁则放行。会话结束自动清除；`session` 不可用时降级到 `local` 并在 `onStartup` 清理。**解锁粒度为 origin 是有意简化**：同 origin 下多个受保护 url 前缀规则，解开一个即全部放行，符合门禁定位 |
| **只锁顶层** | 监听器里 `frameId === 0 && parentFrameId === -1` 且 `schemes: http/https`，`isExtensionUrl` 过滤，不干扰子资源。不使用 `onCommitted` 兜底（会闪现受保护内容且双重处理） |
| **SPA 绕过** | 新增 `onHistoryStateUpdated`，覆盖 `pushState` 导航，原文档仅 `onBeforeNavigate` 会被 Notion/Gmail 等 SPA 绕过 |
| **隐身绕过** | `manifest incognito: spanning` + 文档引导开启“允许隐身模式”，未授权则预期绕过 |
| **限流** | `lock.js` 用 `session` 记录失败次数，5 次锁定 30s，原文档无实现 |
| **多标签** | 用 `tabId` 区分 `pending`，`onRemoved` 清理，`autoLock` 合并写入避免覆盖 |
| **空闲自动锁** | `chrome.idle` 检测静置超阈值或系统锁屏，清空 `session.unlocked` 并重定向已打开的受保护标签；`applyIdle` 做数值校验 |
| **安全上下文** | `chrome-extension://` 视为安全上下文，`crypto.subtle` 可用；`http://` 页面不跑校验逻辑 |
| **上架合规** | `host_permissions <all_urls>` 需在商店描述中 justification，否则改 `optional_host_permissions` 动态申请 |

---

## 6. 开发与调试流程

### 6.1 环境准备

- Chromium 系浏览器（Chrome / Edge）最新稳定版（≥102）。
- 不需要 Node/Python；纯静态文件。
- 建议测试地址：`http://127.0.0.1:8080` 或任意自有域名。

### 6.2 加载未打包扩展

1. 打开 `chrome://extensions`。
2. 右上角开启 **开发者模式（Developer mode）**。
3. 点击 **加载已解压的扩展程序（Load unpacked）**，选择 `chrome-pin-lock/` 目录。
4. 记住扩展 ID（`chrome-extension://<id>/`）。

### 6.3 热重载（改完代码后）

- 在 `chrome://extensions` 卡片上点 **刷新（↻ / Reload）**。
- Service Worker 改动后**必须 reload** 才生效；SW 空闲会被回收，调试时保持 DevTools 打开可防止回收。
- `type: module` 的 SW 修改后如报 `Failed to load module`，检查 `manifest` 的 `type` 字段与文件路径。

### 6.4 分组件调试

| 组件 | 打开方式 | 看什么 |
|---|---|---|
| Service Worker | 扩展卡片上 **“检查视图：Service Worker”** | `background.js` 日志、监听是否触发、报错 |
| 设置页 | 扩展卡片 → **扩展选项 / Options** | `protected` / `cred` / `idleMinutes` 是否写入 `storage.local` |
| 密码页 | 触发一次锁后，在密码页上 **右键 → 检查** | `pending[tabId]` 是否取到、校验结果、限流状态 |
| 全局存储 | 任意扩展页 DevTools 执行 `chrome.storage.local.get(null)` / `chrome.storage.session.get(null)` | 核对 `protected` / `cred` / `unlocked` / `pending` / `lockRate` |

### 6.5 验证拦截是否生效

- 在 Service Worker 控制台临时加日志：
  ```js
  console.log('nav', details.url, 'protected?', isProtected(details.url, list));
  ```
- 用 Network 面板观察是否先跳到 `chrome-extension://.../lock.html` 再跳回。
- SPA 测试：打开受保护域下的 SPA，点击站内路由（如 `/admin -> /admin/user`），观察 `onHistoryStateUpdated` 是否触发二次校验。

### 6.6 端到端测试清单

- [ ] 未设 PIN 时访问受保护地址：不死锁，提示去设置页设 PIN。
- [ ] 设 PIN 后访问受保护地址：弹出密码页，仅展示 origin。
- [ ] 输错 PIN 5 次：锁定 30s，提示倒计时。
- [ ] 输对 PIN：跳回**原完整 URL**（含路径/端口/参数）。
- [ ] 同一会话再访问同域不同路径：不再弹（已解锁）。
- [ ] 关浏览器重开：再次要求 PIN（session 清除）。
- [ ] 非受保护地址：完全不干扰。
- [ ] 子资源（图片/XHR）不受影响。
- [ ] SPA 内 `pushState` 跳转到受保护路径：同样拦截。
- [ ] 隐身窗口：已授权时同样拦截；未授权则绕过（预期）。
- [ ] 空闲软锁：调小阈值至 1 分钟，静置后下次访问重新要求 PIN。
- [ ] 空闲硬锁：超时后已打开的受保护标签自动跳回密码页，且不丢失其他 tab 的 `pending`。
- [ ] 系统锁屏（Win+L）立即触发自动锁。
- [ ] 修改空闲时长后即时生效（无需 reload）。
- [ ] 关闭锁页标签：`pending` 被 `onRemoved` 清理，不泄漏。
- [ ] `IP:端口` 规则：`192.168.1.1:3000` 仅锁该端口，`192.168.1.1` 锁该 IP 所有端口。

### 6.7 打包 / 分发

- **本地自用**：保持“加载未解压”即可；或用 **打包扩展程序（Pack extension）** 生成 `.crx`（需 `.pem`）。
- **上架商店**：Zip 整个目录（排除 `.git`、`.pem`、`temp/`），在商店后台填写 `host_permissions` justification：用于匹配用户自定义的任意受保护域名/IP。
- 注意：`session` 解锁态上架后行为一致；若需“每次启动都重新输”，本设计已天然满足。

### 6.8 常见坑（已更新）

| 现象 | 原因 / 解决 |
|---|---|
| 改了 SW 不生效 | 没点 Reload；或 SW 被回收，保持 DevTools 打开；检查 `manifest` 是否含 `type: module` |
| `crypto.subtle` 为 undefined | 不在安全上下文；确保逻辑只跑在 `chrome-extension://` 页；`http` 页面无 `subtle` |
| 锁了之后循环跳转 | `onBeforeNavigate` 未过滤 `isExtensionUrl` 或未校验 `unlocked` 已写入；检查 `await sessSet` 时序 |
| 隐身窗口直接进 | 扩展未开启“允许隐身模式” |
| 子资源被挡 | 误把规则应用到所有 `resourceType`；已用 `frameId===0 && parentFrameId===-1` 过滤 |
| `chrome.storage.session` 报错 | 浏览器版本 <102；已加 `hasSession` 降级到 `local` |
| SPA 内部跳转不锁 | 未监听 `onHistoryStateUpdated`；已在修正版中补充 |
| `IP:端口` 永远不命中 | 旧版 `isProtected` 仅比 `hostname`；已修正为 `hostWithPort` 匹配 |
| `pending` 丢失 | `autoLock` 覆盖写入；已改为合并 `...oldPending` |

---

## 7. A2 变体（declarativeNetRequest 拦截）差异

若改用 DNR 而非 `webNavigation`：

- 在 `manifest.json` 用 `declarativeNetRequest` 权限，配 `rule_resources` 或运行时 `updateDynamicRules`。
- 命中规则时 `action: { type: 'redirect', redirect: { extensionPath: '/lock.html' } }`。
- **URL 传递问题**：DNR 重定向时拿不到“原请求 URL”，只能跳到固定的 `lock.html`，**会丢失原路径/端口**。解决方式通常是只做“锁整域根”，解锁后导航到域名根；要保留深链需额外在 `onBeforeNavigate` 里存 `pending`（即回到 A1 的暂存思路），那样 DNR 就只剩“兜底拦截”作用。
- IP 匹配用 `urlFilter: "||192.168.1.100^"`（`requestDomains` 不支持裸 IP）。
- 优点：更底层、即使 SW 休眠也能拦；缺点：URL 保真差、规则管理更繁琐。
- **建议**：默认用 A1，SW 休眠兜底可叠加 A2 的 `block` 规则（非 `redirect`），命中时仅记录，仍由 A1 负责跳转，避免深链丢失。

---

## 8. 安全边界与定位（务必知悉）

> **定位共识**：本扩展是**防君子不防小人**的个人设备门禁——防止他人（同事/家人/借电脑的人）随手打开指定站点，以及防止自己误操作。它运行在自己的设备上，攻击者若真想绕过，直接禁用扩展即可，因此**不需要强加密、强口令或抗爆破设计**。

1. **可被任意技术手段绕过**：禁用扩展、`--disable-extensions` 启动、隐身模式（未授权时）、直接改 `storage.local`。这些都是预期内的，不做对抗。
2. **PIN 策略从简**：仅要求 ≥4 位，作用是"挡住随手乱试 / 防误触"，不强制字母数字混合、不提示强度警告。PBKDF2（150k 迭代 + 随机 salt）只是习惯性正确写法，并非为抵抗离线爆破。
3. **限流仅为体验兜底**：5 次 / 30 秒的锁定是为了防止别人坐在电脑前无限乱试影响观感，不是安全机制，无需指数退避等强化。
4. 本方案**不加密任何网页内容**，只是"进门问密码"。若目标是真正的数据保密，请走方案 D（系统/磁盘加密 + 独立账户）。
5. `host_permissions: <all_urls>` 属高敏权限，上架需 justification；自用加载无此要求。

---

## 9. 可选增强（详见其他方案文档）

- **方案 B（扩展 + Native Messaging）**：PIN 校验放到本地原生程序（Go/Node），哈希不落 JS，破解成本更高。
- **方案 C（外部启动器）**：用 PowerShell / AutoHotkey 先弹密码再启动 Chrome，实现“开浏览器即锁”。
- **方案 D（系统层）**：Veracrypt 容器 / 独立 Windows 账户 / BitLocker，提供真实保密。

---

## 10. 变更概述（相对原文档的变更清单）

### 二次修订（2026-08-24 13:21）

- **逻辑修复**：`idleMinutes` 校验上限 1440 → **240**（`setDetectionInterval` API 上限 4 小时，超出会被截断/报错）。
- **逻辑修复**：未设 PIN 的引导监听器补 session 标记 `noPinHintShown`，实现真正的“每会话仅提示一次”。
- **逻辑修复**：移除 `onCommitted` 监听（页面已开始加载、会闪现受保护内容且双重处理）；删除死代码 `sessGetAll`；`autoLock` 补 `cred` 前置检查与 `shouldLock` 对齐。
- **匹配修复**：url 前缀改为「尾斜杠归一化 + 边界前缀」匹配，避免 `/admin` 误命中 `/admin-evil`；整体小写化并修正注释与实现的矛盾。
- **简化**：删除 `verifyPinWithMigration` 及旧格式兼容逻辑（新项目无历史数据，属死代码）；PIN 要求从 ≥6 位放宽为 **≥4 位**，移除强口令提示；§8 重写为“防君子不防小人”的门禁定位。
- **体验修正**：锁页不再用 `title` 暴露完整 URL；`goOptions` 只绑定一次。
- **文档明确**：解锁粒度为 origin 属有意简化；IP 规则仅支持 IPv4。

### 阻塞级修复
- `manifest.json:8` `background` 补 `type: module`，否则 `import` 语法直接崩溃。
- 删除 `web_accessible_resources` 对 `lock.html` 的暴露，原配置冗余且增加指纹面。
- `manifest` `options_page` → `options_ui`，补 `minimum_chrome_version: 102`、`incognito: spanning`、`icons`，否则低版本/隐身/上架场景异常。
- `background.js` `redirectToLock` 加 `tabs.get` 存在性校验 + `try/catch`，`pending` 改合并写入，原文档覆盖写入会丢数据。
- `lock.js` 补 `target` 为空、`cred` 为空分支，原文档会 `location.replace(undefined)` 死锁。
- `lib.js` 去除 `PBKDF2 -> SHA-256` 二次哈希，直存 PBKDF2 输出。

### 逻辑缺陷修复
- 拦截面：新增 `webNavigation.onHistoryStateUpdated`，修复 SPA `pushState` 绕过；增加 `parentFrameId === -1` 与 `isExtensionUrl` 过滤，修复子资源/自跳转递归。
- 会话态：`unlocked` 改 `Set` 去重，原 `push` 会无限增长；`pending` 在 `autoLock` 与 `lock.js` 中均改为 `get->modify->set` 合并，原 `const pending = {}` 覆盖丢失。
- 生命周期：新增 `tabs.onRemoved` 清理 `pending`，新增 `sessGet/sessSet` 兼容 `storage.session` 不可用降级，`onStartup` 清理降级数据。
- 未设 PIN 死锁：`shouldLock` 与 `lock.js` 均检查 `cred`，未设时引导 `openOptionsPage` 而非重定向到锁页。
- 竞争：`lock.js` 确保 `await sessSet(unlocked)` 后再 `location.replace`，避免 SW 侧读到旧值回跳。
- 限流：`lock.js` 新增 `lockRate` 5 次/30s 锁定，原文档仅文字无实现。

### 安全与匹配增强
- `isProtected` 重写：支持 `IP:端口` 精确匹配、裸 IP 通配、domain 去端口/路径、URL 前缀归一化尾斜杠、`host` 小写化；`originKey` 统一小写。
- `options.js` 补全校验、去重、删除、二次确认，PIN 仅要求 ≥4 位（门禁定位），`idleMinutes` 范围校验 `1~240`。
- `lock.html` 提示仅展示 `origin` 而非全 URL，减少敏感路径泄露。
- `applyIdle` 增加数值校验与启动即调用，`autoLock` 合并 `oldPending`。

### 文档与体验
- §4.1/§4.2/§4.3/§4.4/§4.5 代码块全部替换为修正版，行内标注修正原因。
- §5 关键设计点表格新增 SPA、隐身、限流、上架合规行。
- §6 测试清单新增 SPA、限流、IP:端口、pending 清理等 6 项；常见坑新增 4 项。
- §7 A2 补充 SW 休眠兜底建议（`block` 而非 `redirect`）。
- §8 重写为“防君子不防小人”的门禁定位说明（二次修订）。

> 备份：初版备份 `temp/backups/chrome-pin-lock-extension-design-20260824-130957.md`（13:09:57 改写前）；二次修订前版本备份 `temp/backups/chrome-pin-lock-extension-design-20260824-132120.md`；二次修订时间 2026-08-24 13:21 GMT+8。

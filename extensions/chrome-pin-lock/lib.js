// ---------- URL 匹配 ----------
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
  // 直接存储 PBKDF2 输出，不做二次哈希
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

import { setPin, verifyPin, isProtected } from './lib.js';

function normalizeValue(type, value) {
  const v = value.trim();
  if (type === 'domain') return v.toLowerCase().replace(/\/.*$/, '').replace(/:.*$/, '');
  if (type === 'ip') return v; // 保留端口，校验在下方
  return v;
}

function validateItem(type, value) {
  if (!value) return '不能为空';
  if (type === 'domain') {
    if (!/^[a-z0-9.-]+\.[a-z]{2,}$/i.test(value)) return '域名格式不正确';
  }
  if (type === 'ip') {
    if (!/^(\d{1,3}\.){3}\d{1,3}(:\d{1,5})?$/.test(value)) return 'IP 格式不正确，示例：192.168.1.10 或 192.168.1.10:3000（仅支持 IPv4）';
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

// ---------- PIN 管理：已有 PIN 时必须先验证当前 PIN ----------
const hasCred = !!(await chrome.storage.local.get('cred')).cred;
if (hasCred) {
  document.getElementById('curpinRow').style.display = 'block';
  document.querySelectorAll('.card h2')[1].textContent = '修改 PIN';
}

document.getElementById('setPin').onclick = async () => {
  // 门禁定位：仅要求最少 4 位防误触/防随手乱试，不强制强口令
  const pin = document.getElementById('newpin').value;
  const confirm = document.getElementById('confirmpin').value;
  if (hasCred) {
    const cur = document.getElementById('curpin').value;
    if (!cur) { alert('请输入当前 PIN'); return; }
    if (!await verifyPin(cur)) { alert('当前 PIN 不正确'); return; }
  }
  if (pin.length < 4) { alert('PIN 至少 4 位'); return; }
  if (pin !== confirm) { alert('两次输入不一致'); return; }
  await setPin(pin);
  alert(hasCred ? 'PIN 已修改' : 'PIN 已设置');
  document.getElementById('curpin').value = '';
  document.getElementById('newpin').value = '';
  document.getElementById('confirmpin').value = '';
};

async function render() {
  const { protected: list = [] } = await chrome.storage.local.get('protected');
  const ul = document.getElementById('list');
  ul.innerHTML = '';
  if (!list.length) {
    const li = document.createElement('li');
    li.className = 'empty';
    li.style.fontFamily = 'inherit';
    li.textContent = '暂无规则，添加后访问这些地址时将要求输入 PIN';
    ul.appendChild(li);
    return;
  }
  list.forEach((item, idx) => {
    const li = document.createElement('li');
    const text = document.createElement('span');
    text.textContent = `[${item.type}] ${item.value}`;
    const del = document.createElement('button');
    del.textContent = '删除';
    del.className = 'del';
    del.onclick = async () => {
      // 已设 PIN 时，删除保护规则必须先验证当前 PIN，防止绕过门禁
      if (hasCred) {
        const cur = window.prompt('请输入当前 PIN 以删除该规则');
        if (cur === null) return;
        if (!await verifyPin(cur)) { alert('PIN 不正确，已取消删除'); return; }
      }
      list.splice(idx, 1);
      await chrome.storage.local.set({ protected: list });
      render();
    };
    li.appendChild(text);
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

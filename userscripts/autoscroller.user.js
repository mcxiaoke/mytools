// ==UserScript==
// @name         AutoScroller 自动滚动器
// @namespace    http://tampermonkey.net/
// @version      2026-08-20
// @description  可暂停/继续、可拖拽、到底自动停、快捷键启停的无限流页面自动滚动工具
// @author       You
// @match        https://*/*
// @icon         data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==
// @grant        none
// @noframes
// ==/UserScript==

(function () {
  'use strict';

  // 只在最外层窗口运行，避免在 iframe 里重复出现图标（双保险：@noframes + 运行时判断）
  if (window.self !== window.top) return;

  const STORE_KEY = 'autoscroller_settings';
  const DEFAULTS = {
    step: 900,        // 滚动距离
    unit: 'px',       // 'px' 或 'vh'(视口比例)
    interval: 2500,   // 滚动间隔(ms)
    smooth: true,     // 平滑滚动
    stopAtEnd: true,  // 到底 / 无新内容自动停
    autoStart: false, // 进入页面自动开始
    pos: null         // 面板位置 {x, y}，null 表示用默认右下角
  };

  // ---------- 设置读写（带校验，避免脏数据导致 NaN） ----------
  function loadCfg() {
    let c = Object.assign({}, DEFAULTS);
    try { c = Object.assign(c, JSON.parse(localStorage.getItem(STORE_KEY) || '{}')); } catch (e) {}
    c.step = Number(c.step) || DEFAULTS.step;
    c.interval = Number(c.interval) || DEFAULTS.interval;
    c.unit = c.unit === 'vh' ? 'vh' : 'px';
    c.smooth = !!c.smooth;
    c.stopAtEnd = !!c.stopAtEnd;
    c.autoStart = !!c.autoStart;
    c.pos = (c.pos && typeof c.pos.x === 'number' && typeof c.pos.y === 'number') ? c.pos : null;
    return c;
  }
  function saveCfg() { localStorage.setItem(STORE_KEY, JSON.stringify(cfg)); }

  const cfg = loadCfg();

  // ---------- 运行状态（不持久化） ----------
  let mode = 'stopped';  // 'stopped' | 'running' | 'paused'
  let runId = 0;         // 关键：每次起停都 +1，使任何遗留的 setTimeout 立即失效
  let timer = null;
  let stuck = 0;         // 连续无位移计数
  let scrollCount = 0;   // 已滚动次数
  let lastMsg = '';

  function computeStep() {
    return cfg.unit === 'vh'
      ? Math.max(1, cfg.step * window.innerHeight)
      : Math.max(1, cfg.step);
  }

  // ---------- 核心循环 ----------
  function loop(id) {
    if (mode !== 'running' || id !== runId) return;        // 令牌校验：失效的循环直接退出
    const before = window.scrollY;
    window.scrollBy({ top: computeStep(), behavior: cfg.smooth ? 'smooth' : 'auto' });
    scrollCount++;
    timer = setTimeout(function () {
      if (mode !== 'running' || id !== runId) return;      // 再一次令牌校验
      const after = window.scrollY;
      if (after - before < 1) {
        stuck++;
        if (cfg.stopAtEnd && stuck >= 3) {
          stop('已到底 / 无新内容，自动停止');
          return;
        }
      } else {
        stuck = 0;
      }
      updateUI();
      loop(id);
    }, cfg.interval);
  }

  function start() {                 // 从停止态开始（重置计数）
    if (mode === 'running') return;
    mode = 'running';
    runId++;
    stuck = 0;
    scrollCount = 0;
    lastMsg = '';
    const myId = runId;
    updateUI();
    loop(myId);
  }

  function pause() {                 // 挂起，保留进度，可继续
    if (mode !== 'running') return;
    mode = 'paused';
    runId++;                         // 让在途定时器作废
    if (timer) { clearTimeout(timer); timer = null; }
    lastMsg = '已暂停';
    updateUI();
  }

  function resume() {                // 从暂停继续
    if (mode !== 'paused') return;
    mode = 'running';
    runId++;
    stuck = 0;
    const myId = runId;
    updateUI();
    loop(myId);
  }

  function stop(msg) {               // 彻底停止并复位
    mode = 'stopped';
    runId++;
    if (timer) { clearTimeout(timer); timer = null; }
    stuck = 0;
    scrollCount = 0;
    lastMsg = msg || '';
    updateUI();
  }

  function togglePlay() {            // 播放/暂停切换，覆盖三态
    if (mode === 'running') pause();
    else if (mode === 'paused') resume();
    else start();
  }

  // ---------- UI ----------
  const root = document.createElement('div');
  root.id = 'autoscroller-root';
  root.innerHTML = `
    <style>
      #autoscroller-root { position: fixed; right: 16px; bottom: 16px; z-index: 2147483647;
        font: 13px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif; }
      #as-toggle { width: 44px; height: 44px; border-radius: 50%; border: none; cursor: pointer;
        background: #2563eb; color: #fff; font-size: 20px; box-shadow: 0 4px 12px rgba(0,0,0,.3); }
      #as-toggle:hover { background: #1d4ed8; }
      #as-panel { display: none; position: absolute; right: 0; bottom: 56px; width: 248px;
        background: #1f2937; color: #e5e7eb; border-radius: 10px; padding: 12px;
        box-shadow: 0 8px 24px rgba(0,0,0,.35); }
      #as-panel.open { display: block; }
      #as-handle { margin: 0 0 8px; font-size: 14px; cursor: move; user-select: none;
        display: flex; align-items: center; justify-content: space-between; }
      #as-handle .grip { color: #6b7280; font-size: 12px; }
      #as-panel label { display: block; margin: 8px 0 2px; font-size: 12px; color: #9ca3af; }
      #as-panel label.row { display: flex; align-items: center; gap: 6px; margin-top: 6px;
        color: #e5e7eb; cursor: pointer; font-size: 12px; }
      #as-panel input[type=number], #as-panel select { width: 100%; box-sizing: border-box;
        padding: 6px; border-radius: 6px; border: 1px solid #374151;
        background: #111827; color: #e5e7eb; }
      #as-panel .row { display: flex; align-items: center; gap: 6px; margin-top: 8px; }
      #as-panel button { flex: 1; padding: 8px; border: none; border-radius: 6px; cursor: pointer;
        font-weight: 600; color: #fff; }
      #as-play   { background: #16a34a; }
      #as-stop   { background: #dc2626; }
      #as-bottom { background: #4b5563; }
      #as-status { margin-top: 8px; font-size: 12px; color: #9ca3af; }
    </style>
    <div id="as-panel">
      <div id="as-handle" title="拖拽移动面板">自动滚动设置 <span class="grip">⠿</span></div>
      <label>距离单位</label>
      <select id="as-unit">
        <option value="px">像素 (px)</option>
        <option value="vh">视口比例 (0.1–5 屏)</option>
      </select>
      <label id="as-step-label">滚动距离 (px)</label>
      <input id="as-step" type="number" min="10" step="10" value="${cfg.step}">
      <label>滚动间隔 (ms)</label>
      <input id="as-interval" type="number" min="100" step="100" value="${cfg.interval}">
      <label class="row"><input id="as-smooth" type="checkbox" ${cfg.smooth ? 'checked' : ''}> 平滑滚动</label>
      <label class="row"><input id="as-stopend" type="checkbox" ${cfg.stopAtEnd ? 'checked' : ''}> 到底自动停</label>
      <label class="row"><input id="as-autostart" type="checkbox" ${cfg.autoStart ? 'checked' : ''}> 进入页面自动开始</label>
      <div class="row">
        <button id="as-play">开始</button>
        <button id="as-stop">停止</button>
        <button id="as-bottom">到底</button>
      </div>
      <div id="as-status"></div>
    </div>
    <button id="as-toggle" title="自动滚动设置（Alt+S 启停/暂停）">⚙️</button>
  `;
  (document.body || document.documentElement).appendChild(root);

  // 恢复面板位置
  if (cfg.pos) {
    root.style.left = cfg.pos.x + 'px';
    root.style.top = cfg.pos.y + 'px';
    root.style.right = 'auto';
    root.style.bottom = 'auto';
  }

  const $ = (s) => root.querySelector(s);
  const toggle = $('#as-toggle');
  const panel = $('#as-panel');
  const handle = $('#as-handle');
  const unitSel = $('#as-unit');
  const stepInput = $('#as-step');
  const stepLabel = $('#as-step-label');
  const intervalInput = $('#as-interval');
  const smoothInput = $('#as-smooth');
  const stopAtEndInput = $('#as-stopend');
  const autoStartInput = $('#as-autostart');
  const playBtn = $('#as-play');
  const statusEl = $('#as-status');

  unitSel.value = cfg.unit;
  toggle.addEventListener('click', () => panel.classList.toggle('open'));

  function applyUnit() {
    if (cfg.unit === 'vh') {
      stepLabel.textContent = '滚动距离 (视口比例)';
      stepInput.min = '0.1'; stepInput.step = '0.1'; stepInput.max = '5';
      stepInput.value = Math.min(5, Math.max(0.1, cfg.step));
    } else {
      stepLabel.textContent = '滚动距离 (px)';
      stepInput.min = '10'; stepInput.step = '10'; stepInput.removeAttribute('max');
      stepInput.value = Math.max(10, cfg.step);
    }
  }
  applyUnit();

  function syncFromUI() {
    cfg.unit = unitSel.value;
    cfg.smooth = smoothInput.checked;
    cfg.stopAtEnd = stopAtEndInput.checked;
    cfg.autoStart = autoStartInput.checked;
    cfg.interval = Math.max(100, Number(intervalInput.value) || DEFAULTS.interval);
    cfg.step = Number(stepInput.value);
    if (cfg.unit === 'vh') cfg.step = Math.min(5, Math.max(0.1, cfg.step || 0.9));
    else cfg.step = Math.max(10, cfg.step || DEFAULTS.step);
    saveCfg();
    applyUnit();
  }
  [unitSel, stepInput, intervalInput, smoothInput, stopAtEndInput, autoStartInput]
    .forEach((el) => el.addEventListener('change', syncFromUI));

  playBtn.addEventListener('click', togglePlay);
  $('#as-stop').addEventListener('click', () => stop());
  $('#as-bottom').addEventListener('click', () => {
    stop();
    window.scrollTo({
      top: Math.max(document.body.scrollHeight, document.documentElement.scrollHeight),
      behavior: 'smooth'
    });
  });

  // 拖拽面板（拖标题栏）
  let drag = null;
  handle.addEventListener('mousedown', (e) => {
    drag = { x: e.clientX, y: e.clientY, left: root.offsetLeft, top: root.offsetTop };
    e.preventDefault();
  });
  window.addEventListener('mousemove', (e) => {
    if (!drag) return;
    root.style.left = (drag.left + e.clientX - drag.x) + 'px';
    root.style.top = (drag.top + e.clientY - drag.y) + 'px';
    root.style.right = 'auto';
    root.style.bottom = 'auto';
  });
  window.addEventListener('mouseup', () => {
    if (!drag) return;
    drag = null;
    cfg.pos = { x: root.offsetLeft, y: root.offsetTop };
    saveCfg();
  });

  function updateUI() {
    let state;
    if (mode === 'running') state = '<b style="color:#4ade80">滚动中</b>';
    else if (mode === 'paused') state = '<b style="color:#fbbf24">已暂停</b>';
    else state = '<b style="color:#f87171">已停止</b>';
    statusEl.innerHTML = '状态：' + state + ' · 已滚 ' + scrollCount + ' 次'
      + (lastMsg ? '<br>' + lastMsg : '');
    playBtn.textContent = mode === 'running' ? '暂停' : (mode === 'paused' ? '继续' : '开始');
  }

  // 快捷键 Alt+S 启停/暂停（在输入框内不触发，避免和打字冲突）
  document.addEventListener('keydown', (e) => {
    if (e.altKey && (e.key === 'S' || e.key === 's')) {
      const t = e.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
      e.preventDefault();
      togglePlay();
    }
  });

  updateUI();
  if (cfg.autoStart) start();
})();

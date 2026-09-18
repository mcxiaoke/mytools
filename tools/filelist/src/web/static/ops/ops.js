/* ── Ops Panel: standalone front-end module ─────────────────────
 *
 * This file is deliberately self-contained. It does not read any
 * state from the host SPA (app.js) and the host does not know it
 * exists beyond one empty <div> and one event name.
 *
 * Integration contract with the host (the only two symbols shared):
 *
 *   1. #ops-panel-root           — an empty container to mount into
 *   2. 'filelist:navigate'       — a CustomEvent with { detail: { path } }
 *                                  dispatched by the host whenever the
 *                                  browsed directory changes
 *
 * Everything else — the toolbar button, the drawer markup, the Alpine
 * component — is created here at runtime, so removing this directory
 * leaves the host untouched.
 */
(function () {
  'use strict';

  var root = document.getElementById('ops-panel-root');
  if (!root) return;

  // Config is read from the container's data attributes, not from the
  // host's global config object, so this module has no symbol-level
  // dependency on app.js.
  var base = root.getAttribute('data-ops-base') || '';
  var cfg = window.__FILELIST_OPS__ || { enabled: false };

  // The host renders the config as a placeholder; when the panel is
  // disabled we do not even build the UI.
  if (!cfg.enabled) return;

  // ── Drawer markup ────────────────────────────────────────────
  // Built here rather than in index.html so the host template stays
  // free of ops-specific structure.
  root.innerHTML = [
    '<div class="ops-drawer" :class="{ \'ops-open\': open }" x-data="opsPanel" x-cloak>',
    '  <div class="ops-header">',
    '    <span class="ops-title">运维面板</span>',
    '    <button class="ops-icon-btn" @click="popOut()" title="在新窗口打开">&#8599;</button>',
    '    <button class="ops-icon-btn" @click="open = false" title="关闭">&#10005;</button>',
    '  </div>',
    '  <div class="ops-modes" x-show="terminalAvailable">',
    '    <button class="ops-mode-btn" :class="{ \'ops-active\': mode === \'cmd\' }" @click="setMode(\'cmd\')">命令</button>',
    '    <button class="ops-mode-btn" :class="{ \'ops-active\': mode === \'term\' }" @click="setMode(\'term\')">终端</button>',
    '  </div>',
    '  <div class="ops-context">',
    '    <span>cwd</span>',
    '    <span class="ops-cwd" x-text="cwd || \'(\u6839\u76ee\u5f55)\'"></span>',
    '    <button class="ops-icon-btn ops-lock" @click="cwdLocked = !cwdLocked"',
    '            :title="cwdLocked ? \'\u5df2\u9501\u5b9a\uff0c\u4e0d\u518d\u8ddf\u968f\u76ee\u5f55\u53d8\u5316\' : \'\u8ddf\u968f\u5f53\u524d\u6d4f\u89c8\u76ee\u5f55\'"',
    '            x-text="cwdLocked ? \'\ud83d\udd12\' : \'\ud83d\udd13\'"></button>',
    '  </div>',
    '  <div class="ops-notice" x-show="notice" x-text="notice"></div>',

    // Command mode
    '  <template x-if="mode === \'cmd\'">',
    '    <div style="display:flex;flex-direction:column;flex:1 1 auto;min-height:0;">',
    '      <div class="ops-output" x-ref="output">',
    '        <template x-for="(entry, i) in entries" :key="i">',
    '          <div class="ops-entry">',
    '            <div class="ops-cmdline" x-text="\'$ \' + entry.cmd"></div>',
    '            <div x-text="entry.output"></div>',
    '            <div class="ops-status" :class="entry.ok ? \'ops-pass\' : \'ops-fail\'" x-text="entry.status"></div>',
    '          </div>',
    '        </template>',
    '      </div>',
    '      <div class="ops-input-row">',
    '        <input class="ops-input" type="text" x-model="input" :disabled="running"',
    '               placeholder="\u8f93\u5165\u547d\u4ee4\uff0c\u5982 systemctl status nginx"',
    '               @keydown.enter="submit()" @keydown.up.prevent="historyPrev()" @keydown.down.prevent="historyNext()">',
    '        <button class="ops-btn ops-stop" x-show="running" @click="stop()">\u23f9</button>',
    '        <button class="ops-btn ops-primary" x-show="!running" @click="submit()" :disabled="!input.trim()">\u23ce</button>',
    '      </div>',
    '    </div>',
    '  </template>',

    // Terminal mode
    '  <template x-if="mode === \'term\'">',
    '    <div style="display:flex;flex-direction:column;flex:1 1 auto;min-height:0;">',
    '      <template x-if="!termReady">',
    '        <div class="ops-token-form">',
    '          <label>\u4ea4\u4e92\u7ec8\u7aef\u9700\u8981\u72ec\u7acb\u53e3\u4ee4</label>',
    '          <input type="password" x-model="termToken" @keydown.enter="openTerminal()" placeholder="terminalToken">',
    '          <div class="ops-hint">\u8be5\u53e3\u4ee4\u4ec5\u7528\u4e8e\u672c\u6b21\u8fde\u63a5\uff0c\u4e0d\u4f1a\u4fdd\u5b58\u3002</div>',
    '          <div class="ops-hint" x-show="notice" x-text="notice"></div>',
    '        </div>',
    '      </template>',
    '      <div class="ops-terminal-wrap" x-show="termReady" x-ref="terminal"></div>',
    '    </div>',
    '  </template>',
    '</div>'
  ].join('\n');

  // ── Toolbar button ───────────────────────────────────────────
  // Injected into the host toolbar at runtime; index.html contains no
  // reference to it.
  function injectToolbarButton() {
    var toolbar = document.querySelector('.toolbar-left') || document.querySelector('.toolbar');
    if (!toolbar) return;

    var btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'tb-btn ops-toggle-btn';
    btn.id = 'opsToggleBtn';
    btn.title = '运维面板';
    btn.setAttribute('aria-pressed', 'false');
    btn.innerHTML = '&#128295; \u8fd0\u7ef4';
    btn.addEventListener('click', function () {
      var el = document.getElementById('ops-panel-root');
      if (!el) return;
      var data = Alpine.$data(el.querySelector('.ops-drawer'));
      if (data) {
        data.open = !data.open;
        btn.setAttribute('aria-pressed', data.open ? 'true' : 'false');
      }
    });
    toolbar.appendChild(btn);
  }

  // ── Alpine component ─────────────────────────────────────────
  document.addEventListener('alpine:init', function () {
    Alpine.data('opsPanel', function () {
      return {
        open: false,
        mode: 'cmd',
        cwd: '',
        cwdLocked: false,
        input: '',
        entries: [],
        running: false,
        notice: '',
        terminalAvailable: !!cfg.terminal,
        termReady: false,
        termToken: '',

        ws: null,
        currentSID: '',
        history: [],
        historyIdx: -1,
        term: null,
        fitAddon: null,

        init: function () {
          // The standalone page opens the drawer immediately.
          if (document.body.classList.contains('ops-standalone')) {
            this.open = true;
          }

          // Contract with the host: subscribe to directory changes.
          // The host only broadcasts; it never calls into this module.
          window.addEventListener('filelist:navigate', function (e) {
            if (this.cwdLocked) return;
            if (e && e.detail && typeof e.detail.path === 'string') {
              this.cwd = e.detail.path;
            }
          }.bind(this));

          // Restore the last directory from the URL when popped out.
          var params = new URLSearchParams(location.search);
          var initial = params.get('cwd');
          if (initial) this.cwd = initial;

          this.history = this.loadHistory();
          this.connect();
        },

        // ── WebSocket ──
        connect: function () {
          var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
          // The token is passed explicitly: the server refuses an
          // upgrade that relies on the session cookie alone.
          var token = new URLSearchParams(location.search).get('token') || '';
          var url = proto + '//' + location.host + base + '/api/ops/ws';
          if (token) url += '?token=' + encodeURIComponent(token);

          var self = this;
          var ws;
          try {
            ws = new WebSocket(url);
          } catch (err) {
            self.notice = '\u65e0\u6cd5\u8fde\u63a5\u8fd0\u7ef4\u9762\u677f';
            return;
          }
          this.ws = ws;

          ws.onopen = function () {
            self.notice = '';
          };
          ws.onmessage = function (ev) { self.onFrame(ev); };
          ws.onclose = function () {
            self.running = false;
            if (self.open) self.notice = '\u8fde\u63a5\u5df2\u65ad\u5f00\uff0c\u6b63\u5728\u91cd\u8fde\u2026';
            setTimeout(function () { self.connect(); }, 3000);
          };
          ws.onerror = function () {
            self.notice = '\u8fde\u63a5\u51fa\u9519';
          };
        },

        send: function (obj) {
          if (!this.ws || this.ws.readyState !== WebSocket.OPEN) return false;
          this.ws.send(JSON.stringify(obj));
          return true;
        },

        onFrame: function (ev) {
          var f;
          try { f = JSON.parse(ev.data); } catch (e) { return; }

          switch (f.type) {
            case 'out':
              if (this.mode === 'term' && this.term) {
                this.term.write(f.data);
              } else if (this.entries.length) {
                var last = this.entries[this.entries.length - 1];
                last.output += f.data;
                this.scrollOutput();
              }
              break;

            case 'exit':
              this.running = false;
              if (this.mode === 'cmd' && this.entries.length) {
                var e = this.entries[this.entries.length - 1];
                var code = (f.code === null || f.code === undefined) ? 0 : f.code;
                e.ok = code === 0;
                var parts = ['exit ' + code];
                if (f.durationMs) parts.push(f.durationMs + 'ms');
                if (f.truncated) parts.push('\u8f93\u51fa\u5df2\u622a\u65ad');
                e.status = parts.join(' \u00b7 ');
                this.scrollOutput();
              }
              break;

            case 'ready':
              this.currentSID = f.sid || '';
              if (f.cwd) this.cwd = f.cwd;
              break;

            case 'err':
              this.running = false;
              this.notice = this.describeError(f);
              if (this.mode === 'cmd' && this.entries.length) {
                var lastE = this.entries[this.entries.length - 1];
                lastE.ok = false;
                lastE.status = this.notice;
              }
              break;

            case 'pong':
              break;
          }
        },

        describeError: function (f) {
          var map = {
            denied_by_policy: '\u547d\u4ee4\u88ab\u7b56\u7565\u62d2\u7edd',
            cwd_out_of_sandbox: '\u5de5\u4f5c\u76ee\u5f55\u8d85\u51fa\u5141\u8bb8\u8303\u56f4',
            concurrency_limit: '\u5e76\u53d1\u4f1a\u8bdd\u8d85\u9650',
            terminal_token: '\u7ec8\u7aef\u53e3\u4ee4\u4e0d\u6b63\u786e',
            terminal_disabled: '\u4ea4\u4e92\u7ec8\u7aef\u672a\u542f\u7528',
            pty_unavailable: '\u5f53\u524d\u5e73\u53f0\u4e0d\u652f\u6301\u4ea4\u4e92\u7ec8\u7aef',
            no_session: '\u4f1a\u8bdd\u5df2\u4e0d\u5b58\u5728',
            bad_frame: '\u8bf7\u6c42\u683c\u5f0f\u9519\u8bef'
          };
          return (map[f.errCode] || f.msg || '\u6267\u884c\u5931\u8d25') + (f.msg && map[f.errCode] ? '\uff1a' + f.msg : '');
        },

        // ── Command mode ──
        submit: function () {
          var cmd = this.input.trim();
          if (!cmd || this.running) return;

          this.entries.push({ cmd: cmd, output: '', status: '\u6267\u884c\u4e2d\u2026', ok: true });
          this.running = true;
          this.notice = '';
          this.rememberHistory(cmd);
          this.input = '';
          this.scrollOutput();

          if (!this.send({ type: 'exec', cmd: cmd, cwd: this.cwd })) {
            this.running = false;
            this.notice = '\u672a\u8fde\u63a5';
          }
        },

        stop: function () {
          if (this.currentSID) {
            this.send({ type: 'signal', sid: this.currentSID, name: 'INT' });
          }
        },

        scrollOutput: function () {
          var self = this;
          this.$nextTick(function () {
            var el = self.$refs.output;
            if (el) el.scrollTop = el.scrollHeight;
          });
        },

        // ── History (local only, grouped by cwd) ──
        historyKey: function () { return 'ops_history:' + (this.cwd || '/'); },

        loadHistory: function () {
          try {
            return JSON.parse(localStorage.getItem(this.historyKey()) || '[]');
          } catch (e) { return []; }
        },

        rememberHistory: function (cmd) {
          this.history = this.history.filter(function (h) { return h !== cmd; });
          this.history.push(cmd);
          if (this.history.length > 100) this.history.shift();
          this.historyIdx = -1;
          try {
            localStorage.setItem(this.historyKey(), JSON.stringify(this.history));
          } catch (e) { /* storage full or disabled — history is optional */ }
        },

        historyPrev: function () {
          if (!this.history.length) return;
          if (this.historyIdx === -1) this.historyIdx = this.history.length;
          if (this.historyIdx > 0) this.historyIdx--;
          this.input = this.history[this.historyIdx] || '';
        },

        historyNext: function () {
          if (this.historyIdx === -1) return;
          this.historyIdx++;
          if (this.historyIdx >= this.history.length) {
            this.historyIdx = -1;
            this.input = '';
            return;
          }
          this.input = this.history[this.historyIdx] || '';
        },

        // ── Terminal mode ──
        setMode: function (m) {
          this.mode = m;
          this.notice = '';
          if (m === 'term' && this.term) {
            this.$nextTick(this.fitTerminal.bind(this));
          }
        },

        openTerminal: function () {
          var self = this;
          if (!this.send({ type: 'openpty', cwd: this.cwd, token: this.termToken, cols: 80, rows: 24 })) {
            this.notice = '\u672a\u8fde\u63a5';
            return;
          }
          this.termReady = true;
          this.$nextTick(function () { self.mountTerminal(); });
        },

        mountTerminal: function () {
          if (!window.FilelistXterm || !this.$refs.terminal) return;

          var X = window.FilelistXterm;
          this.term = new X.Terminal({
            fontFamily: 'ui-monospace, Menlo, Consolas, monospace',
            fontSize: 13,
            cursorBlink: true,
            theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
          });
          this.fitAddon = new X.FitAddon();
          this.term.loadAddon(this.fitAddon);
          if (X.WebLinksAddon) this.term.loadAddon(new X.WebLinksAddon());
          this.term.open(this.$refs.terminal);

          var self = this;
          this.term.onData(function (d) {
            self.send({ type: 'input', sid: self.currentSID, data: btoa(unescape(encodeURIComponent(d))) });
          });

          this.fitTerminal();

          // Debounced resize so dragging the drawer edge does not flood
          // the socket with size updates.
          var timer = null;
          window.addEventListener('resize', function () {
            clearTimeout(timer);
            timer = setTimeout(function () { self.fitTerminal(); }, 100);
          });
        },

        fitTerminal: function () {
          if (!this.fitAddon || !this.term) return;
          try {
            this.fitAddon.fit();
            this.send({ type: 'resize', sid: this.currentSID, cols: this.term.cols, rows: this.term.rows });
          } catch (e) { /* container not measurable yet */ }
        },

        popOut: function () {
          var url = base + '/ops';
          var params = [];
          if (this.cwd) params.push('cwd=' + encodeURIComponent(this.cwd));
          var token = new URLSearchParams(location.search).get('token');
          if (token) params.push('token=' + encodeURIComponent(token));
          if (params.length) url += '?' + params.join('&');
          window.open(url, '_blank');
        }
      };
    });
  });

  injectToolbarButton();
})();

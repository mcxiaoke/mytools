/**
 * FileList SPA - Alpine.js Core Component
 */
document.addEventListener('alpine:init', () => {
  Alpine.data('filelistApp', () => ({
    // Global Server Config
    base: (window.__FILELIST_CONFIG__ && window.__FILELIST_CONFIG__.base) || '',
    uploadEnabled: !!(window.__FILELIST_CONFIG__ && window.__FILELIST_CONFIG__.uploadEnabled),
    manageConfig: (window.__FILELIST_CONFIG__ && window.__FILELIST_CONFIG__.manage) || {
      enabled: false,
      allowEdit: false,
      allowMkdir: false,
      allowRename: false,
      allowDelete: false
    },

    // State
    path: '',
    items: [],
    loading: false,
    errorMsg: '',
    searchMode: false,
    searchQuery: '',
    sortCol: 'name',
    sortDir: 'asc',
    uploading: false,
    uploadStatus: '',
    uploadStatusClass: '',
    isDragOver: false,
    zoomScale: 1.0,
    fontScales: [0.85, 1.0, 1.15, 1.3, 1.45, 1.6, 1.8, 2.0],
    viewMode: 'list',
    activeImg: null,
    toastMsg: '',
    toastTimer: null,

    // Modals
    mediaModal: {
      show: false,
      item: null,
      isVideo: false
    },
    textModal: {
      show: false,
      item: null,
      content: '',
      origContent: '',
      loading: false,
      saving: false,
      err: '',
      status: '',
      get dirty() {
        return this.content !== this.origContent;
      }
    },
    mkdirModal: {
      show: false,
      name: '',
      err: '',
      submitting: false
    },
    renameModal: {
      show: false,
      item: null,
      newName: '',
      err: '',
      submitting: false
    },
    deleteModal: {
      show: false,
      item: null,
      token: '',
      rememberSession: true,
      err: '',
      submitting: false
    },
    qrModal: {
      show: false,
      item: null,
      url: '',
      svg: ''
    },

    // Lifecycle
    init() {
      this.initViewMode();
      this.initZoom();
      this.initHistory();
      this.initDragAndDrop();
      this.initUnsavedWarning();
      var initialPath = this.pathFromLocation();
      this.navigate(initialPath, true, true);
    },

    initUnsavedWarning() {
      window.addEventListener('beforeunload', (e) => {
        if (this.textModal.show && this.textModal.dirty) {
          e.preventDefault();
          e.returnValue = '';
        }
      });
    },

    initViewMode() {
      try {
        var saved = localStorage.getItem('filelist_view_mode');
        if (saved === 'list' || saved === 'grid') {
          this.viewMode = saved;
        }
      } catch (e) {}
    },

    setViewMode(mode) {
      if (mode !== 'list' && mode !== 'grid') return;
      this.viewMode = mode;
      try {
        localStorage.setItem('filelist_view_mode', mode);
      } catch (e) {}
    },

    initDragAndDrop() {
      window.addEventListener('dragover', (e) => {
        e.preventDefault();
        if (this.uploadEnabled && this.path && !this.searchMode) {
          this.isDragOver = true;
        }
      });
      window.addEventListener('dragleave', (e) => {
        if (!e.relatedTarget || e.relatedTarget === document.documentElement) {
          this.isDragOver = false;
        }
      });
      window.addEventListener('drop', (e) => {
        e.preventDefault();
        this.isDragOver = false;
        if (this.uploadEnabled && this.path && !this.searchMode && e.dataTransfer && e.dataTransfer.files.length > 0) {
          this.uploadFiles(e.dataTransfer.files);
        }
      });
    },

    // ---- Path Helpers ----
    decodePath(p) {
      if (!p) return '';
      try { return decodeURIComponent(p); } catch (e) { return p; }
    },

    encodePath(p) {
      return encodeURI(p || '').replace(/#/g, '%23').replace(/\?/g, '%3F');
    },

    dirHref(p) {
      if (!p || p === '/') return this.base + '/';
      var path = p.charAt(0) === '/' ? p : '/' + p;
      return this.base + this.encodePath(path);
    },

    rawHref(p) {
      if (!p) return '';
      var path = p.charAt(0) === '/' ? p : '/' + p;
      return this.base + '/raw' + this.encodePath(path);
    },

    fileHref(p, download) {
      var path = p.charAt(0) === '/' ? p : '/' + p;
      return this.base + '/raw' + this.encodePath(path) + (download ? '?download=1' : '');
    },

    downloadHref(item) {
      return this.fileHref(item.path, true);
    },

    zipHref(p) {
      if (!p || p === '/') return '';
      var path = p.charAt(0) === '/' ? p : '/' + p;
      return this.base + '/api/zip?path=' + this.encodePath(path);
    },

    pathFromLocation() {
      var p = this.decodePath(window.location.pathname);
      if (this.base && p.indexOf(this.base) === 0) {
        p = p.slice(this.base.length);
      }
      if (p === '/' || p === '') return '';
      if (p.length > 1 && p.charAt(p.length - 1) === '/') {
        p = p.slice(0, -1);
      }
      if (p.charAt(0) !== '/') {
        p = '/' + p;
      }
      return p;
    },

    initHistory() {
      window.addEventListener('popstate', (e) => {
        var p = (e.state && e.state.path !== undefined) ? e.state.path : this.pathFromLocation();
        this.navigate(p, true, true);
      });

      document.addEventListener('click', (e) => {
        var a = e.target.closest('a');
        if (!a) return;
        if (a.getAttribute('target') === '_blank') return;
        if (a.href && a.href.indexOf('?download=1') > -1) return;
        var href = a.getAttribute('href');
        if (!href) return;
        if (this.base) {
          if (href.indexOf(this.base + '/') !== 0 && href !== this.base) return;
          href = href.slice(this.base.length);
        }
        if (href.charAt(0) !== '/') return;
        if (href.indexOf('/raw/') === 0 || href.indexOf('/static/') === 0 || href.indexOf('/api/') === 0) return;
        e.preventDefault();
        var p = (href === '/') ? '' : this.decodePath(href);
        this.navigate(p, false, false);
      });
    },

    // ---- Navigation ----
    navigate(path, skipState, force) {
      path = path || '';
      if (path === '/') path = '';
      if (path && path.charAt(0) !== '/') path = '/' + path;

      if (!force && path === this.path && !this.searchMode) return;

      this.path = path;
      this.searchMode = false;
      this.searchQuery = '';
      this.uploadStatus = '';
      this.uploadStatusClass = '';
      this.activeImg = null;
      this.closeMedia();
      this.textModal.show = false;
      this.textModal.item = null;
      this.mkdirModal.show = false;
      this.renameModal.show = false;
      this.deleteModal.show = false;
      this.qrModal.show = false;

      if (!skipState) {
        var url = this.base + (path || '/');
        try {
          history.pushState({ path: path }, '', url);
        } catch (e) {
          try { history.replaceState({ path: path }, '', url); } catch (e2) {}
        }
      }

      // Broadcast the directory change for optional modules. The host
      // neither knows nor cares who subscribes; when nothing is
      // listening this is a no-op.
      try {
        window.dispatchEvent(new CustomEvent('filelist:navigate', { detail: { path: path } }));
      } catch (e) {}

      this.loadDir(path);
    },

    loadDir(path) {
      this.loading = true;
      this.errorMsg = '';

      var url = (!path || path === '/')
        ? (this.base + '/api/roots')
        : (this.base + '/api/list?path=' + encodeURIComponent(path));

      fetch(url)
        .then((r) => {
          if (!r.ok) throw new Error('HTTP ' + r.status);
          return r.json();
        })
        .then((data) => {
          this.items = Array.isArray(data) ? data : [];
          this.loading = false;
        })
        .catch((err) => {
          this.errorMsg = '加载失败: ' + err.message;
          this.loading = false;
        });
    },

    // ---- Search ----
    doSearch(query) {
      query = (query !== undefined ? query : this.searchQuery) || '';
      query = query.trim();
      if (!query) {
        this.navigate(this.path, true, true);
        return;
      }

      this.searchMode = true;
      this.searchQuery = query;
      this.loading = true;
      this.errorMsg = '';

      var url = this.base + '/api/search?q=' + encodeURIComponent(query);
      fetch(url)
        .then((r) => {
          if (!r.ok) throw new Error('HTTP ' + r.status);
          return r.json();
        })
        .then((data) => {
          this.items = Array.isArray(data) ? data : [];
          this.loading = false;
        })
        .catch((err) => {
          this.errorMsg = '搜索失败: ' + err.message;
          this.loading = false;
        });
    },

    clearSearch() {
      this.searchQuery = '';
      this.navigate(this.path, true, true);
    },

    // ---- Sorting ----
    toggleSort(col) {
      if (this.sortCol === col) {
        this.sortDir = this.sortDir === 'asc' ? 'desc' : 'asc';
      } else {
        this.sortCol = col;
        this.sortDir = 'asc';
      }
    },

    get sortedItems() {
      var arr = (this.items || []).slice();
      var col = this.sortCol;
      var dir = this.sortDir === 'asc' ? 1 : -1;

      arr.sort((a, b) => {
        // Directories always come first
        if (a.isDir !== b.isDir) {
          return a.isDir ? -1 : 1;
        }
        if (col === 'size') {
          var diff = (a.size - b.size) * dir;
          if (diff !== 0) return diff;
          return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' }) * dir;
        }
        if (col === 'time') {
          var ta = a.modTime ? new Date(a.modTime).getTime() : 0;
          var tb = b.modTime ? new Date(b.modTime).getTime() : 0;
          var diff = (ta - tb) * dir;
          if (diff !== 0) return diff;
          return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' }) * dir;
        }
        // Default: name sort (natural collation)
        return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' }) * dir;
      });

      return arr;
    },

    // ---- Breadcrumbs ----
    get breadcrumbItems() {
      if (this.searchMode) {
        return [];
      }
      if (!this.path) {
        return [{ name: '根目录', path: '', isCur: true, href: this.base + '/' }];
      }

      var parts = this.path.split('/').filter(Boolean);
      var res = [{ name: '根目录', path: '', isCur: false, href: this.base + '/' }];
      var cum = '';

      for (var i = 0; i < parts.length; i++) {
        cum += '/' + parts[i];
        var isCur = (i === parts.length - 1);
        res.push({
          name: parts[i],
          path: cum,
          isCur: isCur,
          href: this.dirHref(cum)
        });
      }
      return res;
    },

    // ---- UI Scale / Font Zoom ----
    initZoom() {
      var saved = null;
      try {
        saved = localStorage.getItem('filelist_zoom');
      } catch (e) {}
      if (saved) {
        var val = parseFloat(saved);
        if (!isNaN(val) && val >= 0.7 && val <= 3.0) {
          this.setZoom(val, false);
          return;
        }
      }
      this.setZoom(1.0, false);
    },

    setZoom(scale, save) {
      this.zoomScale = Math.round(scale * 100) / 100;
      document.body.style.zoom = this.zoomScale;
      if (save) {
        try {
          localStorage.setItem('filelist_zoom', String(this.zoomScale));
        } catch (e) {}
      }
    },

    decZoom() {
      var next = this.fontScales[0];
      for (var i = this.fontScales.length - 1; i >= 0; i--) {
        if (this.fontScales[i] < this.zoomScale - 0.01) {
          next = this.fontScales[i];
          break;
        }
      }
      this.setZoom(next, true);
    },

    incZoom() {
      var next = this.fontScales[this.fontScales.length - 1];
      for (var i = 0; i < this.fontScales.length; i++) {
        if (this.fontScales[i] > this.zoomScale + 0.01) {
          next = this.fontScales[i];
          break;
        }
      }
      this.setZoom(next, true);
    },

    resetZoom() {
      this.setZoom(1.0, true);
    },

    // ---- File Upload & Drag and Drop ----
    triggerUpload() {
      var input = document.getElementById('uploadInput');
      if (input) input.click();
    },

    onFileInputChange(e) {
      var files = e.target.files;
      if (files && files.length > 0) {
        this.uploadFiles(files);
      }
      e.target.value = '';
    },

    handleDrop(e) {
      this.isDragOver = false;
      if (!this.uploadEnabled || !this.path || this.searchMode) return;
      var files = e.dataTransfer ? e.dataTransfer.files : null;
      if (files && files.length > 0) {
        this.uploadFiles(files);
      }
    },

    uploadFiles(files) {
      if (!files || files.length === 0) return;
      if (!this.path) {
        this.uploadStatus = '不能直接上传到根挂载点';
        this.uploadStatusClass = 'error';
        return;
      }

      var fd = new FormData();
      for (var i = 0; i < files.length; i++) {
        fd.append('files', files[i]);
      }

      this.uploading = true;
      this.uploadStatus = '准备上传 ' + files.length + ' 个文件...';
      this.uploadStatusClass = '';

      var url = this.base + '/api/upload?path=' + encodeURIComponent(this.path);
      var xhr = new XMLHttpRequest();
      xhr.open('POST', url);

      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable) {
          var pct = Math.round((e.loaded / e.total) * 100);
          this.uploadStatus = '上传中 ' + pct + '%';
        }
      };

      xhr.onload = () => {
        this.uploading = false;
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            var res = JSON.parse(xhr.responseText);
            var count = (res.uploaded && res.uploaded.length) || 0;
            this.uploadStatus = '成功上传 ' + count + ' 个文件';
            this.uploadStatusClass = 'success';
          } catch (ex) {
            this.uploadStatus = '上传成功';
            this.uploadStatusClass = 'success';
          }
          this.loadDir(this.path);
        } else {
          var msg = 'HTTP ' + xhr.status;
          try {
            var errObj = JSON.parse(xhr.responseText);
            if (errObj && errObj.error) msg = errObj.error;
          } catch (ex) {}
          this.uploadStatus = '上传失败: ' + msg;
          this.uploadStatusClass = 'error';
        }
      };

      xhr.onerror = () => {
        this.uploading = false;
        this.uploadStatus = '网络错误，上传失败';
        this.uploadStatusClass = 'error';
      };

      xhr.send(fd);
    },

    // ---- Display Formatters ----
    fmtSize(b) {
      if (b === 0) return '0 B';
      if (!b) return '-';
      var u = ['B', 'KB', 'MB', 'GB', 'TB'];
      var i = 0;
      var n = b;
      while (n >= 1024 && i < u.length - 1) {
        n /= 1024;
        i++;
      }
      return (i === 0 ? n : n.toFixed(1)) + ' ' + u[i];
    },

    fmtTime(iso) {
      if (!iso) return '-';
      var d = new Date(iso);
      if (isNaN(d.getTime())) return '-';
      var pad = function(n) { return n < 10 ? '0' + n : '' + n; };
      return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate())
        + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
    },

    getIcon(item) {
      if (item.isDir) return '\uD83D\uDCC1'; // 📁
      var name = item.name || '';
      var ext = (name.split('.').pop() || '').toLowerCase();
      var icons = {
        pdf: '\uD83D\uDCD5',
        doc: '\uD83D\uDCDD', docx: '\uD83D\uDCDD', txt: '\uD83D\uDCDD', md: '\uD83D\uDCDD',
        jpg: '\uD83D\uDDBC\uFE0F', jpeg: '\uD83D\uDDBC\uFE0F', png: '\uD83D\uDDBC\uFE0F', gif: '\uD83D\uDDBC\uFE0F',
        svg: '\uD83D\uDDBC\uFE0F', webp: '\uD83D\uDDBC\uFE0F', ico: '\uD83D\uDDBC\uFE0F',
        mp4: '\uD83C\uDFAC', mkv: '\uD83C\uDFAC', avi: '\uD83C\uDFAC', mov: '\uD83C\uDFAC', webm: '\uD83C\uDFAC',
        mp3: '\uD83C\uDFB5', flac: '\uD83C\uDFB5', wav: '\uD83C\uDFB5', ogg: '\uD83C\uDFB5',
        zip: '\uD83D\uDCE6', tar: '\uD83D\uDCE6', gz: '\uD83D\uDCE6', rar: '\uD83D\uDCE6', '7z': '\uD83D\uDCE6',
        iso: '\uD83D\uDCBF', exe: '\u2699\uFE0F', apk: '\uD83D\uDCF1'
      };
      return icons[ext] || '\uD83D\uDCC4'; // 📄
    },

    escapeHtml(str) {
      var div = document.createElement('div');
      div.textContent = str || '';
      return div.innerHTML;
    },

    highlight(name) {
      if (!this.searchMode || !this.searchQuery) {
        return this.escapeHtml(name);
      }
      var q = this.searchQuery;
      var lowerName = name.toLowerCase();
      var lowerQ = q.toLowerCase();
      var idx = lowerName.indexOf(lowerQ);
      if (idx === -1) {
        return this.escapeHtml(name);
      }
      var before = name.slice(0, idx);
      var match = name.slice(idx, idx + q.length);
      var after = name.slice(idx + q.length);
      return this.escapeHtml(before) + '<mark>' + this.escapeHtml(match) + '</mark>' + this.highlight(after);
    },

    // ---- Image & Lightbox ----
    isImage(item) {
      if (!item || item.isDir) return false;
      var name = item.name || '';
      return /\.(jpe?g|png|gif|webp|bmp|svg|avif|ico)$/i.test(name);
    },

    get currentImages() {
      return this.sortedItems.filter((item) => this.isImage(item));
    },

    get activeImgIndex() {
      if (!this.activeImg) return -1;
      return this.currentImages.findIndex((i) => i.path === this.activeImg.path);
    },

    openLightbox(item) {
      if (this.isImage(item)) {
        this.activeImg = item;
      }
    },

    closeLightbox() {
      this.activeImg = null;
    },

    prevImg() {
      if (!this.activeImg) return;
      var imgs = this.currentImages;
      if (imgs.length === 0) return;
      var idx = imgs.findIndex((i) => i.path === this.activeImg.path);
      if (idx > 0) {
        this.activeImg = imgs[idx - 1];
      } else {
        this.activeImg = imgs[imgs.length - 1];
      }
    },

    nextImg() {
      if (!this.activeImg) return;
      var imgs = this.currentImages;
      if (imgs.length === 0) return;
      var idx = imgs.findIndex((i) => i.path === this.activeImg.path);
      if (idx >= 0 && idx < imgs.length - 1) {
        this.activeImg = imgs[idx + 1];
      } else {
        this.activeImg = imgs[0];
      }
    },

    // ---- Media Player & Editor Type Checks ----
    isMedia(item) {
      if (!item || item.isDir) return false;
      var ext = (item.name.split('.').pop() || '').toLowerCase();
      return ['mp4', 'webm', 'mkv', 'mov', 'avi', 'mp3', 'wav', 'ogg', 'flac', 'aac', 'm4a'].indexOf(ext) > -1;
    },

    isVideo(item) {
      if (!item || item.isDir) return false;
      var ext = (item.name.split('.').pop() || '').toLowerCase();
      return ['mp4', 'webm', 'mkv', 'mov', 'avi'].indexOf(ext) > -1;
    },

    isAudio(item) {
      if (!item || item.isDir) return false;
      var ext = (item.name.split('.').pop() || '').toLowerCase();
      return ['mp3', 'wav', 'ogg', 'flac', 'aac', 'm4a'].indexOf(ext) > -1;
    },

    isText(item) {
      if (!item || item.isDir) return false;
      var ext = (item.name.split('.').pop() || '').toLowerCase();
      return ['txt', 'md', 'json', 'yaml', 'yml', 'xml', 'html', 'htm', 'css', 'js', 'ts', 'go', 'py', 'sh', 'bash', 'zsh', 'bat', 'ps1', 'ini', 'conf', 'config', 'log', 'toml', 'sql', 'c', 'cpp', 'h', 'hpp', 'rs', 'java', 'kt', 'dart', 'diff', 'patch'].indexOf(ext) > -1;
    },

    isNovel(item) {
      if (!item || item.isDir) return false;
      var ext = (item.name.split('.').pop() || '').toLowerCase();
      return ext === 'txt';
    },

    readerHref(item) {
      if (!item) return '';
      var rawUrl = this.rawHref(item.path);
      var title = item.name;
      return this.base + '/static/reader.html?url=' + encodeURIComponent(rawUrl) + '&title=' + encodeURIComponent(title);
    },

    openReader(item) {
      if (!item) return;
      var url = this.readerHref(item);
      window.open(url, '_blank');
    },

    onFileClick(e, item) {
      if (e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
      if (this.isImage(item)) {
        e.preventDefault();
        this.openLightbox(item);
      } else if (this.isMedia(item)) {
        e.preventDefault();
        this.openMedia(item);
      } else if (this.isText(item)) {
        e.preventDefault();
        this.openText(item);
      }
    },

    // ---- Audio & Video Player Modal ----
    openMedia(item) {
      this.mediaModal.show = true;
      this.mediaModal.item = item;
      this.mediaModal.isVideo = this.isVideo(item);
    },

    closeMedia() {
      this.mediaModal.show = false;
      this.mediaModal.item = null;
      var v = document.getElementById('activeVideo');
      if (v) { try { v.pause(); v.removeAttribute('src'); v.load(); } catch (e) {} }
      var a = document.getElementById('activeAudio');
      if (a) { try { a.pause(); a.removeAttribute('src'); a.load(); } catch (e) {} }
    },

    parseApiError(r) {
      return r.text().then((text) => {
        try {
          var d = JSON.parse(text);
          if (d && d.error) return d.error;
        } catch (e) {}
        return text || ('HTTP ' + r.status);
      });
    },

    // ---- Text Viewer & In-Place Editor ----
    openText(item) {
      if (item.size > 2 * 1024 * 1024) {
        if (!confirm('此文件较大 (' + this.fmtSize(item.size) + ')，在线编辑可能较慢，确定打开吗？')) return;
      }
      this.textModal.show = true;
      this.textModal.item = item;
      this.textModal.content = '';
      this.textModal.origContent = '';
      this.textModal.loading = true;
      this.textModal.saving = false;
      this.textModal.err = '';
      this.textModal.status = '';
      var url = this.base + '/api/content?path=' + encodeURIComponent(item.path);
      fetch(url)
        .then((r) => {
          if (!r.ok) {
            return this.parseApiError(r).then((msg) => { throw new Error(msg); });
          }
          return r.json();
        })
        .then((data) => {
          var txt = (data && data.content !== undefined) ? data.content : '';
          this.textModal.content = txt;
          this.textModal.origContent = txt;
          this.textModal.loading = false;
          this.$nextTick(() => {
            var el = document.getElementById('textEditorArea');
            if (el) el.focus();
          });
        })
        .catch((err) => {
          this.textModal.err = '加载文件失败: ' + err.message;
          this.textModal.loading = false;
        });
    },

    confirmCloseText() {
      if (this.textModal.dirty) {
        if (!confirm('当前有未保存的修改，确定要关闭吗？已修改的内容将丢失。')) return;
      }
      this.textModal.show = false;
      this.textModal.item = null;
    },

    saveText() {
      if (!this.manageConfig.allowEdit || !this.textModal.item || this.textModal.saving) return;
      this.textModal.saving = true;
      this.textModal.status = '保存中...';
      var url = this.base + '/api/content?path=' + encodeURIComponent(this.textModal.item.path);
      fetch(url, {
        method: 'PUT',
        headers: { 'Content-Type': 'text/plain; charset=utf-8' },
        body: this.textModal.content
      })
        .then((r) => {
          if (!r.ok) {
            return this.parseApiError(r).then((msg) => { throw new Error(msg); });
          }
          return r.json();
        })
        .then(() => {
          this.textModal.origContent = this.textModal.content;
          this.textModal.saving = false;
          this.textModal.status = '已保存';
          this.showToast('保存成功');
          this.loadDir(this.path);
          setTimeout(() => {
            if (this.textModal.status === '已保存') this.textModal.status = '';
          }, 3000);
        })
        .catch((err) => {
          this.textModal.saving = false;
          this.textModal.status = '';
          alert('保存失败: ' + err.message);
        });
    },

    handleTextKeydown(e) {
      if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) {
        e.preventDefault();
        this.saveText();
        return;
      }
      if (e.key === 'Tab') {
        e.preventDefault();
        var ta = e.target;
        var start = ta.selectionStart;
        var end = ta.selectionEnd;
        var indent = '  ';
        this.textModal.content = this.textModal.content.substring(0, start) + indent + this.textModal.content.substring(end);
        this.$nextTick(() => {
          ta.selectionStart = ta.selectionEnd = start + indent.length;
        });
      }
    },

    get textEditorInfo() {
      var c = this.textModal.content || '';
      var lines = c ? c.split('\n').length : 0;
      return lines + ' 行 | ' + c.length + ' 字符 | UTF-8';
    },

    // ---- Directory Creation ----
    openMkdir() {
      this.mkdirModal.show = true;
      this.mkdirModal.name = '';
      this.mkdirModal.err = '';
      this.mkdirModal.submitting = false;
      this.$nextTick(() => {
        var el = document.getElementById('mkdirInput');
        if (el) el.focus();
      });
    },

    doMkdir() {
      var name = (this.mkdirModal.name || '').trim();
      if (!name) {
        this.mkdirModal.err = '文件夹名称不能为空';
        return;
      }
      if (/[\/\\:]/.test(name)) {
        this.mkdirModal.err = '文件夹名称不能包含 / \\ : 等特殊字符';
        return;
      }
      this.mkdirModal.submitting = true;
      this.mkdirModal.err = '';
      var url = this.base + '/api/mkdir';
      fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path: this.path, name: name })
      })
        .then((r) => {
          if (!r.ok) {
            return this.parseApiError(r).then((msg) => { throw new Error(msg); });
          }
          return r.json();
        })
        .then(() => {
          this.mkdirModal.show = false;
          this.mkdirModal.submitting = false;
          this.showToast('文件夹已创建: ' + name);
          this.loadDir(this.path);
        })
        .catch((err) => {
          this.mkdirModal.submitting = false;
          this.mkdirModal.err = err.message || '创建文件夹失败';
        });
    },

    // ---- File / Directory Rename ----
    openRename(item) {
      this.renameModal.show = true;
      this.renameModal.item = item;
      this.renameModal.newName = item.name;
      this.renameModal.err = '';
      this.renameModal.submitting = false;
      this.$nextTick(() => {
        var el = document.getElementById('renameInput');
        if (el) { el.focus(); el.select(); }
      });
    },

    doRename() {
      var newName = (this.renameModal.newName || '').trim();
      if (!newName) {
        this.renameModal.err = '名称不能为空';
        return;
      }
      if (/[\/\\:]/.test(newName)) {
        this.renameModal.err = '名称不能包含 / \\ : 等特殊字符';
        return;
      }
      if (this.renameModal.item && newName === this.renameModal.item.name) {
        this.renameModal.show = false;
        return;
      }
      this.renameModal.submitting = true;
      this.renameModal.err = '';
      var url = this.base + '/api/rename';
      fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path: this.renameModal.item.path, newName: newName })
      })
        .then((r) => {
          if (!r.ok) {
            return this.parseApiError(r).then((msg) => { throw new Error(msg); });
          }
          return r.json();
        })
        .then(() => {
          this.renameModal.show = false;
          this.renameModal.submitting = false;
          this.showToast('重命名成功');
          this.loadDir(this.path);
        })
        .catch((err) => {
          this.renameModal.submitting = false;
          this.renameModal.err = err.message || '重命名失败';
        });
    },

    // ---- Safe Deletion with deleteToken ----
    openDelete(item) {
      this.deleteModal.show = true;
      this.deleteModal.item = item;
      this.deleteModal.err = '';
      this.deleteModal.submitting = false;
      var saved = sessionStorage.getItem('filelist_del_token');
      if (saved) {
        this.deleteModal.token = saved;
      }
      this.$nextTick(() => {
        var el = document.getElementById('deleteTokenInput');
        if (el) { el.focus(); if (this.deleteModal.token) el.select(); }
      });
    },

    doDelete() {
      var token = (this.deleteModal.token || '').trim();
      if (!token) {
        this.deleteModal.err = '请输入安全删除验证口令';
        return;
      }
      this.deleteModal.submitting = true;
      this.deleteModal.err = '';
      var url = this.base + '/api/delete';
      fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ path: this.deleteModal.item.path, token: token })
      })
        .then((r) => {
          if (!r.ok) {
            return this.parseApiError(r).then((msg) => { throw new Error(msg); });
          }
          return r.json();
        })
        .then(() => {
          if (this.deleteModal.rememberSession) {
            sessionStorage.setItem('filelist_del_token', token);
          }
          this.deleteModal.show = false;
          this.deleteModal.submitting = false;
          this.showToast('已删除: ' + (this.deleteModal.item ? this.deleteModal.item.name : ''));
          this.loadDir(this.path);
        })
        .catch((err) => {
          this.deleteModal.submitting = false;
          this.deleteModal.err = err.message || '删除失败，请检查验证口令';
        });
    },

    // ---- Mobile QR Code & Link Sharing ----
    showQR(item) {
      var fullUrl = window.location.origin + this.rawHref(item.path);
      this.qrModal.item = item;
      this.qrModal.url = fullUrl;
      this.qrModal.svg = '';
      try {
        if (typeof window.qrcode === 'function') {
          var qr = window.qrcode(0, 'M');
          qr.addData(fullUrl);
          qr.make();
          this.qrModal.svg = qr.createSvgTag({ scalable: true });
        }
      } catch (ex) {
        console.error('QR generation failed:', ex);
      }
      this.qrModal.show = true;
    },

    copyLink(item) {
      var fullUrl = window.location.origin + this.rawHref(item.path);
      this.copyText(fullUrl);
    },

    copyText(text) {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(() => {
          this.showToast('链接已复制到剪贴板');
        }).catch(() => {
          this.fallbackCopy(text);
        });
      } else {
        this.fallbackCopy(text);
      }
    },

    fallbackCopy(text) {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.style.position = 'fixed';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.focus();
      ta.select();
      try {
        document.execCommand('copy');
        this.showToast('链接已复制到剪贴板');
      } catch (e) {
        this.showToast('复制失败，请手动复制');
      }
      document.body.removeChild(ta);
    },

    showToast(msg) {
      this.toastMsg = msg;
      clearTimeout(this.toastTimer);
      this.toastTimer = setTimeout(() => {
        this.toastMsg = '';
      }, 2500);
    }
  }));
});

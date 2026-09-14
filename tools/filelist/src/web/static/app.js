/**
 * FileList SPA - Alpine.js Core Component
 */
document.addEventListener('alpine:init', () => {
  Alpine.data('filelistApp', () => ({
    // Global Server Config
    base: (window.__FILELIST_CONFIG__ && window.__FILELIST_CONFIG__.base) || '',
    uploadEnabled: !!(window.__FILELIST_CONFIG__ && window.__FILELIST_CONFIG__.uploadEnabled),

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

    // Lifecycle
    init() {
      this.initZoom();
      this.initHistory();
      this.initDragAndDrop();
      var initialPath = this.pathFromLocation();
      this.navigate(initialPath, true, true);
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

    fileHref(p, download) {
      var path = p.charAt(0) === '/' ? p : '/' + p;
      return this.base + '/raw' + this.encodePath(path) + (download ? '?download=1' : '');
    },

    downloadHref(item) {
      return this.fileHref(item.path, true);
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

      if (!skipState) {
        var url = this.base + (path || '/');
        try {
          history.pushState({ path: path }, '', url);
        } catch (e) {
          try { history.replaceState({ path: path }, '', url); } catch (e2) {}
        }
      }

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
    }
  }));
});

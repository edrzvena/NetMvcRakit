// Builder > Playground: object tree, SQL editor (Ace), results, object structure and the new-table designer.
// Everything the server sends is put into the page with textContent / createTextNode, never as HTML.
(function () {
    'use strict';

    const root = document.getElementById('pg-root');
    if (!root) return;

    const $ = (id) => document.getElementById(id);
    const base = root.dataset.base.replace(/\/$/, '');
    const settings = {
        write: root.dataset.write === '1',
        defaultRows: parseInt(root.dataset.defaultRows, 10),
        maxRows: parseInt(root.dataset.maxRows, 10),
        timeout: parseInt(root.dataset.timeout, 10),
        maxTimeout: parseInt(root.dataset.maxTimeout, 10),
        schemas: JSON.parse(root.dataset.schemas || '[]'),
        types: JSON.parse(root.dataset.types || '[]'),
    };
    const tokenInput = root.querySelector('input[name="__RequestVerificationToken"]');

    // ---- small helpers -------------------------------------------------------------------------
    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (v === null || v === undefined || v === false) return;
            if (k === 'text') node.textContent = v;
            else if (k === 'class') node.className = v;
            else node.setAttribute(k, v === true ? '' : v);
        });
        (children || []).forEach((c) => { if (c) node.appendChild(typeof c === 'string' ? document.createTextNode(c) : c); });
        return node;
    }
    const icon = (name) => el('i', { class: 'bi ' + name });
    const nullCell = () => el('td', {}, [el('em', { class: 'text-muted', text: 'NULL' })]);
    const store = {
        get(key) { try { return window.localStorage.getItem(key); } catch (e) { return null; } },
        set(key, value) { try { window.localStorage.setItem(key, value); } catch (e) { /* storage blocked */ } },
    };

    async function api(method, path, body, signal) {
        const headers = { Accept: 'application/json' };
        let payload;
        if (method !== 'GET') {
            headers['Content-Type'] = 'application/json';
            headers.RequestVerificationToken = tokenInput ? tokenInput.value : '';
            payload = JSON.stringify(body || {});
        }
        const res = await fetch(base + path, { method, headers, body: payload, signal });
        let data = null;
        try { data = await res.json(); } catch (e) { /* not JSON (anti-forgery failure, 404 page) */ }
        return { ok: res.ok, status: res.status, data };
    }

    function failureText(res) {
        if (res.status === 404) return 'Tidak ditemukan, atau Playground dimatikan.';
        if (res.data && res.data.message) return res.data.message + (res.data.detail ? ' (' + res.data.detail + ')' : '');
        return 'Permintaan gagal (HTTP ' + res.status + '). Muat ulang halaman lalu coba lagi.';
    }

    // ---- editor and its tabs ---------------------------------------------------------------------
    // One Ace editor; every query tab owns an EditSession (text, undo, cursor) and a results box, so switching tabs brings its
    // own query and its last result back. A script made from the tree opens in a new tab (or fills the active one when empty).
    ace.config.set('basePath', root.dataset.ace);
    const editor = ace.edit('pg-editor');
    const isDark = () => document.documentElement.getAttribute('data-bs-theme') === 'dark';
    const applyTheme = () => editor.setTheme(isDark() ? 'ace/theme/tomorrow_night' : 'ace/theme/chrome');
    applyTheme();
    window.addEventListener('rakit-theme', applyTheme);
    editor.setOptions({
        fontSize: 13,
        showPrintMargin: false,
        enableBasicAutocompletion: true,
        enableLiveAutocompletion: true,
        enableSnippets: false,
    });
    editor.renderer.setScrollMargin(6, 6);
    editor.commands.addCommand({
        name: 'rakit-run', bindKey: { win: 'Ctrl-Enter', mac: 'Command-Enter' }, exec: () => run(), readOnly: true,
    });
    // the resizable wrapper: keep Ace's size in step
    new ResizeObserver(() => editor.resize()).observe($('pg-editor'));

    // object names for autocompletion (filled once the tree is loaded)
    const objectNames = new Set();
    ace.require('ace/ext/language_tools').addCompleter({
        getCompletions(ed, session, pos, prefix, callback) {
            callback(null, Array.from(objectNames).map((n) => ({ caption: n, value: n, meta: 'objek', score: 1000 })));
        },
    });

    const TABS_KEY = 'rakit-pg-tabs';
    const MAX_TABS = 12;
    const tabs = [];
    let active = null;
    let tabCounter = 0;
    let saveTimer = 0;
    const tabStrip = $('pg-qtabs');
    const blankSession = ace.createEditSession('', 'ace/mode/sql'); // what the (hidden) editor holds while there is no tab

    function saveTabs() {
        store.set(TABS_KEY, JSON.stringify({
            active: tabs.indexOf(active), counter: tabCounter,
            tabs: tabs.map((t) => ({ title: t.title, sql: t.session.getValue() })),
        }));
    }
    function scheduleSave() {
        clearTimeout(saveTimer);
        saveTimer = setTimeout(saveTabs, 400);
    }
    window.addEventListener('pagehide', saveTabs);

    /** A new tab (not shown yet). `title` null = "Query N". */
    function createTab(text, title) {
        const session = ace.createEditSession(text, 'ace/mode/sql');
        session.setUseWrapMode(false);
        session.setTabSize(4);
        session.setUseSoftTabs(true);
        const tab = {
            id: ++uidCounter, title: title || 'Query ' + (++tabCounter), session: session,
            results: el('div', { class: 'pg-tab-results' }, [el('div', { class: 'text-muted small', text: 'Hasil muncul di sini.' })]),
            controller: null, ran: false, edited: false, silent: false,
        };
        session.on('change', () => { if (!tab.silent) tab.edited = true; scheduleSave(); });
        tabs.push(tab);
        return tab;
    }
    let uidCounter = 0;

    function renderTabs() {
        const nodes = tabs.map((t) => {
            const label = el('button', {
                type: 'button', class: 'pg-qtab-label', role: 'tab', 'aria-selected': t === active ? 'true' : 'false',
                title: t.title + ' (klik dua kali untuk ganti nama)',
            }, [t.controller ? el('span', { class: 'spinner-border spinner-border-sm me-1', 'aria-hidden': 'true' }) : null, t.title]);
            label.addEventListener('click', () => activate(t, true));
            label.addEventListener('dblclick', () => renameTab(t));
            label.addEventListener('auxclick', (ev) => { if (ev.button === 1) { ev.preventDefault(); closeTab(t); } });
            const close = el('button', { type: 'button', class: 'pg-qtab-close', title: 'Tutup tab', 'aria-label': 'Tutup tab ' + t.title }, [icon('bi-x-lg')]);
            close.addEventListener('click', () => closeTab(t));
            return el('div', { class: 'pg-qtab' + (t === active ? ' active' : '') }, [label, close]);
        });
        const add = el('button', { type: 'button', class: 'pg-qtab-add', title: 'Query baru', 'aria-label': 'Query baru' }, [icon('bi-plus-lg')]);
        add.addEventListener('click', () => newBlank());
        tabStrip.replaceChildren(...nodes, add);
        const current = tabStrip.querySelector('.pg-qtab.active');
        if (current && current.scrollIntoView) current.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }

    function syncRun() {
        const busy = !!(active && active.controller);
        $('pg-run').disabled = busy;
        $('pg-cancel').classList.toggle('d-none', !busy);
    }

    /** No tab left: the editor, the toolbar and the results are hidden until a tab is opened (+ or the tree). */
    function showEmpty() {
        active = null;
        tabCounter = 0; // the next tab is "Query 1" again
        editor.setSession(blankSession);
        $('pg-query-body').classList.add('d-none');
        $('pg-empty').classList.remove('d-none');
        renderTabs();
        syncRun();
        scheduleSave();
    }

    function activate(tab, focus) {
        active = tab;
        $('pg-query-body').classList.remove('d-none');
        $('pg-empty').classList.add('d-none');
        $('pg-empty-note').classList.add('d-none');
        editor.setSession(tab.session);
        $('pg-results').replaceChildren(tab.results);
        renderTabs();
        syncRun();
        scheduleSave();
        if (focus) editor.focus();
    }

    function renameTab(tab) {
        const name = window.prompt('Nama tab', tab.title);
        if (name === null || name.trim() === '') return;
        tab.title = name.trim().slice(0, 40);
        renderTabs();
        scheduleSave();
    }

    function closeTab(tab) {
        // only a query that was typed and never run is worth asking about
        if (tab.edited && !tab.ran && tab.session.getValue().trim() !== ''
            && !window.confirm('Tab "' + tab.title + '" berisi query yang belum dijalankan. Tutup?')) return;
        if (tab.controller) tab.controller.abort();
        const index = tabs.indexOf(tab);
        tabs.splice(index, 1);
        if (active === tab) editor.setSession(blankSession);
        if (tabs.length === 0) showEmpty();
        else if (active === tab) activate(tabs[Math.min(index, tabs.length - 1)], true);
        else { renderTabs(); scheduleSave(); }
        tab.session.destroy();
    }

    /** An empty tab, ready to type in. */
    function newBlank() {
        if (tabs.length >= MAX_TABS) { showNotice('warning', 'Sudah ada ' + MAX_TABS + ' tab. Tutup beberapa dulu.'); return null; }
        const tab = createTab('', null);
        activate(tab, true);
        $('pg-tab-query').click();
        return tab;
    }

    /** Opens a generated script: in the active tab when that is empty and idle, otherwise in a new tab named `title`. */
    function openScript(title, text, thenRun) {
        let tab = active && !active.controller && active.session.getValue().trim() === '' ? active : null;
        if (tab) {
            tab.title = title;
        } else {
            if (tabs.length >= MAX_TABS) { showNotice('warning', 'Sudah ada ' + MAX_TABS + ' tab. Tutup beberapa dulu.'); return null; }
            tab = createTab('', title);
        }
        tab.silent = true;
        tab.session.setValue(text);
        tab.silent = false;
        tab.edited = false;
        tab.ran = false;
        activate(tab, true);
        editor.selection.clearSelection();
        editor.moveCursorTo(0, 0);
        $('pg-tab-query').click();
        if (thenRun) run();
        return tab;
    }

    function restoreTabs() {
        let saved = null;
        try { saved = JSON.parse(store.get(TABS_KEY) || 'null'); } catch (e) { saved = null; }
        if (saved && Array.isArray(saved.tabs) && saved.tabs.length > 0) {
            tabCounter = Number(saved.counter) || saved.tabs.length;
            saved.tabs.slice(0, MAX_TABS).forEach((t) => { createTab(String((t && t.sql) || ''), String((t && t.title) || 'Query').slice(0, 40)).ran = true; });
            activate(tabs[Math.min(Math.max(0, Number(saved.active) || 0), tabs.length - 1)]);
        } else if (saved && Array.isArray(saved.tabs)) {
            showEmpty(); // the user closed every tab: keep it that way
        } else {
            // the single draft of the one-editor version, or a sample
            activate(createTab(store.get('rakit-pg-draft') || 'SELECT TOP (10) * FROM app.Customers;\n', null));
        }
    }

    // ---- history -------------------------------------------------------------------------------
    const HISTORY_KEY = 'rakit-pg-history';
    function readHistory() {
        try { const items = JSON.parse(store.get(HISTORY_KEY) || '[]'); return Array.isArray(items) ? items : []; } catch (e) { return []; }
    }
    function addHistory(sql) {
        const items = readHistory().filter((h) => h.sql !== sql);
        items.unshift({ sql: sql, at: new Date().toISOString() });
        store.set(HISTORY_KEY, JSON.stringify(items.slice(0, 20)));
        renderHistory();
    }
    function renderHistory() {
        const menu = $('pg-history-menu');
        const items = readHistory();
        if (items.length === 0) {
            menu.replaceChildren(el('li', {}, [el('span', { class: 'dropdown-item-text text-muted small', text: 'Belum ada riwayat.' })]));
            return;
        }
        menu.replaceChildren(...items.map((h) => {
            const first = h.sql.trim().split(/\r?\n/)[0];
            const button = el('button', { type: 'button', class: 'dropdown-item text-truncate', title: h.sql.slice(0, 400) }, [
                el('code', { text: first.length > 70 ? first.slice(0, 70) + '…' : first }),
                el('small', { class: 'text-muted ms-2', text: new Date(h.at).toLocaleTimeString() }),
            ]);
            button.addEventListener('click', () => { editor.setValue(h.sql, -1); editor.focus(); });
            return el('li', {}, [button]);
        }));
    }
    renderHistory();
    $('pg-clear').addEventListener('click', () => { editor.setValue('', -1); editor.focus(); });

    // ---- running -------------------------------------------------------------------------------
    const rollbackBox = $('pg-rollback');
    if (rollbackBox) {
        rollbackBox.checked = store.get('rakit-pg-rollback') === '1';
        rollbackBox.addEventListener('change', () => store.set('rakit-pg-rollback', rollbackBox.checked ? '1' : '0'));
    }

    function clampInt(input, min, max, fallback) {
        const n = parseInt(input.value, 10);
        const value = Number.isFinite(n) ? Math.min(max, Math.max(min, n)) : fallback;
        input.value = value;
        return value;
    }

    async function run() {
        const tab = active;
        if (!tab || tab.controller) return;
        const selected = editor.getSelectedText();
        const useSelection = selected.trim() !== '';
        const sql = useSelection ? selected : editor.getValue();
        if (sql.trim() === '') { showNotice('warning', 'Tulis SQL dulu.', tab); return; }
        const lineOffset = useSelection ? editor.getSelectionRange().start.row : 0;

        tab.session.clearAnnotations();
        tab.controller = new AbortController();
        tab.ran = true;
        renderTabs();
        syncRun();
        showNotice('secondary', 'Menjalankan...', tab);
        addHistory(sql);

        try {
            const res = await api('POST', '/Run', {
                sql: sql,
                maxRows: clampInt($('pg-maxrows'), 1, settings.maxRows, settings.defaultRows),
                timeoutSeconds: clampInt($('pg-timeout'), 1, settings.maxTimeout, settings.timeout),
                rollback: !!(rollbackBox && rollbackBox.checked),
            }, tab.controller.signal);
            if (res.ok && res.data) renderRun(res.data, lineOffset, tab);
            else {
                let text = failureText(res);
                if (res.data && res.data.line) text += ' (baris ' + (res.data.line + lineOffset) + ')';
                showNotice('danger', text, tab);
            }
        } catch (e) {
            if (e && e.name === 'AbortError') showNotice('warning', 'Dibatalkan.', tab);
            else showNotice('danger', 'Server tidak dapat dihubungi.', tab);
        } finally {
            tab.controller = null;
            renderTabs();
            syncRun();
        }
    }
    $('pg-run').addEventListener('click', run);
    $('pg-cancel').addEventListener('click', () => { if (active && active.controller) active.controller.abort(); });

    /** A message in a tab's results box (the active tab unless told otherwise). */
    function showNotice(kind, text, tab) {
        const target = tab || active;
        if (!target) {
            // no tab is open: the message goes into the empty state
            const note = $('pg-empty-note');
            note.className = 'alert alert-' + kind + ' mt-3';
            note.textContent = text;
            return;
        }
        target.results.replaceChildren(el('div', { class: 'alert alert-' + kind + ' mb-0', role: 'alert', text: text }));
    }

    const number = (n) => Number(n).toLocaleString('id-ID');

    function renderRun(data, lineOffset, tab) {
        const parts = [];
        const rows = data.resultSets.reduce((sum, s) => sum + s.rows.length, 0);
        const summary = [
            data.success ? 'Selesai' : 'Gagal',
            data.resultSets.length + ' result set',
            number(rows) + ' baris',
            data.durationMs + ' ms',
            data.write ? (data.committed ? 'di-commit' : 'di-rollback') : 'hanya baca (di-rollback)',
        ].join(' · ');
        parts.push(el('div', { class: 'small mb-2 ' + (data.success ? 'text-muted' : 'text-danger fw-semibold'), text: summary }));

        if (data.error) parts.push(renderError(data.error, lineOffset, tab));

        const tabs = el('ul', { class: 'nav nav-pills pg-result-tabs mb-2', role: 'tablist' });
        const panes = el('div', { class: 'tab-content' });
        const items = data.resultSets.map((s, i) => ({
            label: 'Hasil ' + (i + 1) + ' (' + number(s.totalRows) + ')', node: renderGrid(s), title: 'Batch ' + s.batch,
        }));
        if (data.messages.length > 0 || items.length === 0) {
            items.push({ label: 'Pesan (' + data.messages.length + ')', node: renderMessages(data.messages), title: '' });
        }
        items.forEach((item, i) => {
            const id = 'pg-res-' + tab.id + '-' + i;
            const link = el('button', {
                class: 'nav-link py-1 px-2' + (i === 0 ? ' active' : ''), type: 'button', role: 'tab', title: item.title,
                'data-bs-toggle': 'pill', 'data-bs-target': '#' + id, 'aria-controls': id, 'aria-selected': i === 0 ? 'true' : 'false', text: item.label,
            });
            tabs.appendChild(el('li', { class: 'nav-item', role: 'presentation' }, [link]));
            panes.appendChild(el('div', { class: 'tab-pane fade' + (i === 0 ? ' show active' : ''), id: id, role: 'tabpanel' }, [item.node]));
        });
        parts.push(tabs, panes);
        tab.results.replaceChildren(...parts);
    }

    function renderError(error, lineOffset, tab) {
        const box = el('div', { class: 'alert alert-danger mb-2', role: 'alert' }, [
            el('div', { class: 'fw-semibold', text: error.message }),
        ]);
        const where = [];
        if (error.line) {
            const row = error.line + lineOffset;
            const link = el('a', { href: '#', text: 'baris ' + row });
            link.addEventListener('click', (ev) => { ev.preventDefault(); activate(tab); editor.gotoLine(row, 0, true); editor.focus(); });
            where.push(el('span', {}, ['Di ', link]));
            tab.session.setAnnotations([{ row: row - 1, column: 0, text: error.message, type: 'error' }]);
        }
        if (error.procedure) where.push(el('span', { text: 'Di prosedur ' + error.procedure + (error.procedureLine ? ', baris ' + error.procedureLine : '') }));
        where.push(el('span', { text: 'Batch ' + error.batch + ' · galat ' + error.number + ' (tingkat ' + error.severity + ')' }));
        const line = el('div', { class: 'small mt-1' });
        where.forEach((w, i) => { if (i > 0) line.appendChild(document.createTextNode(' · ')); line.appendChild(w); });
        box.appendChild(line);
        if (error.hint) box.appendChild(el('div', { class: 'small mt-1', text: error.hint }));
        return box;
    }

    function renderMessages(messages) {
        if (messages.length === 0) return el('div', { class: 'text-muted small', text: 'Tidak ada pesan.' });
        return el('pre', { class: 'pg-messages mb-0' }, [document.createTextNode(messages.map((m) => m.text).join('\n'))]);
    }

    // Every result set is shown as JSON first (an array of objects, one per row) and as a table on request.
    const NUMERIC_TYPES = new Set(['tinyint', 'smallint', 'int', 'bigint', 'decimal', 'numeric', 'float', 'real', 'money', 'smallmoney']);

    // A double keeps about 15 significant digits: a bigint past 2^53 or a long decimal written as a bare number would be
    // silently rounded by whoever parses the JSON, so those stay strings
    const MAX_SAFE_DIGITS = 15;

    function isSafeNumber(base, value) {
        if (!/^-?\d+(\.\d+)?([eE][+-]?\d+)?$/.test(value)) return false;
        if (base === 'float' || base === 'real') return true; // already a double on the server
        let digits = value.replace(/^-/, '');
        if (digits.includes('.')) digits = digits.replace(/0+$/, '');
        digits = digits.replace('.', '').replace(/^0+/, '');
        return digits.length <= MAX_SAFE_DIGITS;
    }

    /** The cell as a JSON literal: numbers and bits by their column type, null as null, everything else as a string. */
    function jsonLiteral(type, value) {
        if (value === null) return 'null';
        const base = String(type || '').split('(')[0].trim().toLowerCase();
        if (base === 'bit' && (value === 'true' || value === 'false')) return value;
        if (NUMERIC_TYPES.has(base) && isSafeNumber(base, value)) return value;
        return JSON.stringify(value);
    }

    function toJsonText(set) {
        if (set.rows.length === 0) return '[]';
        // a repeated column name would overwrite the first one, and the suffix itself may already be taken (x, x, x_2)
        const used = new Set();
        const keys = set.columns.map((c, i) => {
            const name = c.name || '(kolom ' + (i + 1) + ')';
            let key = name;
            for (let n = 2; used.has(key); n++) key = name + '_' + n;
            used.add(key);
            return key;
        });
        const rows = set.rows.map((row) => '  {\n' + row.map((v, i) => '    ' + JSON.stringify(keys[i]) + ': ' + jsonLiteral(set.columns[i].type, v)).join(',\n') + '\n  }');
        return '[\n' + rows.join(',\n') + '\n]';
    }

    function renderGrid(set) {
        const json = toJsonText(set);
        let mode = 'json';

        const tools = el('div', { class: 'd-flex flex-wrap align-items-center gap-2 mb-1 small' });
        tools.appendChild(el('span', { class: 'text-muted', text: set.truncated
            ? 'Menampilkan ' + number(set.rows.length) + ' dari ' + number(set.totalRows) + ' baris'
            : number(set.rows.length) + ' baris' }));
        if (set.truncated) tools.appendChild(el('span', { class: 'badge text-bg-warning', text: 'dipotong' }));

        const jsonButton = el('button', { type: 'button', class: 'btn btn-sm btn-primary py-0', 'aria-pressed': 'true', text: 'JSON' });
        const tableButton = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary py-0', 'aria-pressed': 'false', text: 'Tabel' });
        const toggle = el('div', { class: 'btn-group btn-group-sm ms-2', role: 'group', 'aria-label': 'Tampilan hasil' }, [jsonButton, tableButton]);

        const copy = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary py-0 ms-auto' }, [icon('bi-clipboard'), ' Salin']);
        const download = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary py-0' }, [icon('bi-download'), ' .json']);
        tools.append(toggle, copy, download);

        const jsonView = el('pre', { class: 'pg-json mb-0', tabindex: '0', 'aria-label': 'Hasil dalam JSON' }, [document.createTextNode(json)]);
        // The table is built on the first switch to Tabel: building it next to the JSON doubled the work for up to 5000 rows per set
        const wrap = el('div', { class: 'pg-grid d-none' });
        let gridBuilt = false;

        function buildGrid() {
            const head = el('tr', {}, [el('th', { class: 'pg-rownum', text: '#' })].concat(
                set.columns.map((c, i) => el('th', { text: c.name || '(kolom ' + (i + 1) + ')', title: c.type }))));
            const body = set.rows.map((row, r) => el('tr', {}, [el('td', { class: 'pg-rownum text-muted', text: r + 1 })].concat(
                row.map((v) => (v === null ? nullCell() : el('td', { text: v }))))));
            wrap.appendChild(el('table', { class: 'table table-sm table-bordered table-hover mb-0' }, [el('thead', {}, [head]), el('tbody', {}, body)]));
            if (set.rows.length === 0) wrap.appendChild(el('div', { class: 'text-muted small p-2', text: 'Tidak ada baris.' }));
            gridBuilt = true;
        }

        function show(next) {
            mode = next;
            const asJson = mode === 'json';
            if (!asJson && !gridBuilt) buildGrid();
            jsonView.classList.toggle('d-none', !asJson);
            wrap.classList.toggle('d-none', asJson);
            jsonButton.className = 'btn btn-sm py-0 ' + (asJson ? 'btn-primary' : 'btn-outline-secondary');
            tableButton.className = 'btn btn-sm py-0 ' + (asJson ? 'btn-outline-secondary' : 'btn-primary');
            jsonButton.setAttribute('aria-pressed', asJson ? 'true' : 'false');
            tableButton.setAttribute('aria-pressed', asJson ? 'false' : 'true');
            download.replaceChildren(icon('bi-download'), asJson ? ' .json' : ' .csv');
        }
        jsonButton.addEventListener('click', () => show('json'));
        tableButton.addEventListener('click', () => show('table'));

        // Salin and the download follow the view that is showing: JSON as it is, the table as tab-separated text / CSV
        copy.addEventListener('click', async () => {
            try { await navigator.clipboard.writeText(mode === 'json' ? json : toDelimited(set, '\t')); copy.replaceChildren(icon('bi-clipboard-check'), ' Tersalin'); } catch (e) { copy.textContent = 'Gagal'; }
            setTimeout(() => copy.replaceChildren(icon('bi-clipboard'), ' Salin'), 1200);
        });
        download.addEventListener('click', () => {
            if (mode === 'json') download_('hasil.json', json, 'application/json');
            else download_('hasil.csv', toDelimited(set, ',', true), 'text/csv');
        });

        return el('div', {}, [tools, jsonView, wrap]);
    }

    function toDelimited(set, sep, quote) {
        const cell = (v) => {
            if (v === null) return '';
            if (!quote && !/[\t\r\n]/.test(v)) return v;
            return /[",\r\n\t]/.test(v) || quote ? '"' + v.replace(/"/g, '""') + '"' : v;
        };
        const lines = [set.columns.map((c) => cell(c.name)).join(sep)];
        set.rows.forEach((r) => lines.push(r.map(cell).join(sep)));
        return lines.join('\r\n');
    }

    function download_(name, text, type) {
        const url = URL.createObjectURL(new Blob([(type === 'text/csv' ? '﻿' : '') + text], { type: type + ';charset=utf-8' }));
        const link = el('a', { href: url, download: name });
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    // ---- object tree ---------------------------------------------------------------------------
    const kinds = [
        ['TABLE', 'Tables', 'bi-table'],
        ['VIEW', 'Views', 'bi-eye'],
        ['PROCEDURE', 'Procedures', 'bi-gear'],
        ['FUNCTION', 'Functions', 'bi-braces'],
    ];
    const kindIcon = Object.fromEntries(kinds.map((k) => [k[0], k[2]]));
    let selectedKey = '';

    async function loadTree() {
        const host = $('pg-tree');
        host.replaceChildren(el('div', { class: 'text-muted small p-2', text: 'Memuat objek...' }));
        const res = await api('GET', '/Tree');
        if (!res.ok) { host.replaceChildren(el('div', { class: 'text-danger small p-2', text: failureText(res) })); return; }

        objectNames.clear();
        const nodes = res.data.map((schema) => {
            const folders = kinds.map(([kind, title, ico]) => {
                const objects = schema.objects.filter((o) => o.kind === kind);
                objects.forEach((o) => { objectNames.add(o.name); objectNames.add(o.schema + '.' + o.name); });
                const list = el('div', { class: 'pg-children' }, objects.map((o) => treeItem(o)));
                return folder(title, ico, objects.length, list, kind === 'TABLE');
            });
            return folder(schema.name, 'bi-database', schema.objects.length, el('div', { class: 'pg-children' }, folders), true, true);
        });
        host.replaceChildren(...nodes);
        applyFilter();
        highlight();
    }

    function folder(title, ico, count, children, open, isSchema) {
        const toggle = el('button', { type: 'button', class: 'pg-toggle', 'aria-expanded': open ? 'true' : 'false' }, [
            icon('bi-chevron-right pg-chev'), icon(ico), el('span', { class: 'pg-name', text: title }), el('span', { class: 'pg-count', text: count }),
        ]);
        const node = el('div', { class: 'pg-node' + (open ? ' is-open' : '') + (isSchema ? ' pg-schema' : ''), role: 'group' }, [toggle, children]);
        toggle.addEventListener('click', () => {
            const now = node.classList.toggle('is-open');
            toggle.setAttribute('aria-expanded', now ? 'true' : 'false');
        });
        return node;
    }

    function treeItem(o) {
        const key = o.schema + '.' + o.name;
        const open = el('button', { type: 'button', class: 'pg-open', title: key }, [icon(kindIcon[o.kind]), el('span', { class: 'pg-name', text: o.name })]);
        open.addEventListener('click', () => selectObject(o));
        const actions = el('span', { class: 'pg-actions' });
        if (o.kind === 'TABLE' || o.kind === 'VIEW') {
            const play = el('button', { type: 'button', class: 'pg-act', title: 'SELECT TOP (1) * di tab baru', 'aria-label': 'SELECT TOP 1 ' + key }, [icon('bi-play-fill')]);
            play.addEventListener('click', () => scriptInto(o, 'select', true));
            actions.appendChild(play);
        } else if (o.kind === 'PROCEDURE') {
            const exec = el('button', { type: 'button', class: 'pg-act', title: 'Skrip EXEC', 'aria-label': 'Skrip EXEC ' + key }, [icon('bi-terminal')]);
            exec.addEventListener('click', () => scriptInto(o, 'exec', false));
            actions.appendChild(exec);
        }
        return el('div', {
            class: 'pg-item', 'data-key': key, 'data-name': o.name.toLowerCase(), 'data-schema': o.schema, 'data-obj': o.name, 'data-kind': o.kind,
            role: 'treeitem',
        }, [open, actions]);
    }

    function highlight() {
        document.querySelectorAll('#pg-tree .pg-item').forEach((n) => n.classList.toggle('active', n.dataset.key === selectedKey));
    }

    function applyFilter() {
        const text = $('pg-tree-filter').value.trim().toLowerCase();
        document.querySelectorAll('#pg-tree .pg-item').forEach((n) => {
            n.classList.toggle('d-none', text !== '' && !n.dataset.key.toLowerCase().includes(text));
        });
        // folders and schemas with nothing visible are hidden; while filtering, the rest stay open
        const nodes = Array.from(document.querySelectorAll('#pg-tree .pg-node')).reverse();
        nodes.forEach((n) => {
            const any = n.querySelector('.pg-item:not(.d-none)');
            n.classList.toggle('d-none', text !== '' && !any);
            n.classList.toggle('is-filtering', text !== '');
        });
    }
    $('pg-tree-filter').addEventListener('input', applyFilter);
    $('pg-tree-refresh').addEventListener('click', loadTree);

    async function scriptInto(o, kind, thenRun) {
        const res = await api('POST', '/Script', { kind: kind, schema: o.schema, name: o.name });
        if (!res.ok) { showNotice('danger', failureText(res)); $('pg-tab-query').click(); return; }
        // each script gets its own tab, named after the object (the verb first unless it is a plain look at the data)
        const plain = kind === 'select' || kind === 'selectTop' || kind === 'exec';
        openScript((plain ? '' : kind.toUpperCase() + ' ') + o.name, res.data.sql, thenRun);
    }

    // ---- structure -----------------------------------------------------------------------------
    async function selectObject(o) {
        selectedKey = o.schema + '.' + o.name;
        highlight();
        const host = $('pg-structure');
        host.replaceChildren(el('div', { class: 'text-muted small', text: 'Memuat struktur...' }));
        $('pg-tab-structure').click();

        const res = await api('GET', '/Object?schema=' + encodeURIComponent(o.schema) + '&name=' + encodeURIComponent(o.name));
        if (!res.ok) { host.replaceChildren(el('div', { class: 'alert alert-danger', text: failureText(res) })); return; }
        renderStructure(res.data);
    }

    const scriptMenus = {
        TABLE: [['select', 'SELECT TOP 1 *'], ['selectTop', 'SELECT 100 teratas'], ['insert', 'INSERT'], ['update', 'UPDATE'], ['delete', 'DELETE'], ['create', 'CREATE TABLE'], ['addColumn', 'Tambah kolom'], ['drop', 'DROP TABLE']],
        VIEW: [['select', 'SELECT TOP 1 *'], ['selectTop', 'SELECT 100 teratas'], ['definition', 'Definisi'], ['alter', 'Ubah (CREATE OR ALTER)'], ['drop', 'DROP VIEW']],
        PROCEDURE: [['exec', 'EXEC'], ['definition', 'Definisi'], ['alter', 'Ubah (CREATE OR ALTER)'], ['drop', 'DROP PROCEDURE']],
        FUNCTION: [['definition', 'Definisi'], ['alter', 'Ubah (CREATE OR ALTER)'], ['drop', 'DROP FUNCTION']],
    };


    // ---- right-click menu on the tree ----------------------------------------------------------------
    const menu = el('ul', { class: 'dropdown-menu pg-ctx', role: 'menu' });
    document.body.appendChild(menu);
    const closeMenu = () => menu.classList.remove('show');
    document.addEventListener('click', closeMenu);
    document.addEventListener('keydown', (ev) => { if (ev.key === 'Escape') closeMenu(); });
    window.addEventListener('resize', closeMenu);
    window.addEventListener('blur', closeMenu);
    $('pg-tree').addEventListener('scroll', closeMenu);

    function openMenu(x, y, entries) {
        menu.replaceChildren(...entries.map((e) => {
            if (e === '-') return el('li', {}, [el('hr', { class: 'dropdown-divider' })]);
            const button = el('button', { type: 'button', class: 'dropdown-item', role: 'menuitem' }, [e.icon ? icon(e.icon + ' me-2') : null, e.label]);
            button.addEventListener('click', () => { closeMenu(); e.run(); });
            return el('li', {}, [button]);
        }));
        menu.classList.add('show');
        // keep the menu on screen
        const box = menu.getBoundingClientRect();
        menu.style.left = Math.max(4, Math.min(x, window.innerWidth - box.width - 4)) + 'px';
        menu.style.top = Math.max(4, Math.min(y, window.innerHeight - box.height - 4)) + 'px';
    }

    $('pg-tree').addEventListener('contextmenu', (ev) => {
        ev.preventDefault();
        const item = ev.target.closest ? ev.target.closest('.pg-item') : null;
        const entries = [{ label: 'Query baru', icon: 'bi-file-earmark-plus', run: () => newBlank() }];
        if (item) {
            const o = { schema: item.dataset.schema, name: item.dataset.obj, kind: item.dataset.kind };
            entries.push({ label: 'Struktur', icon: 'bi-diagram-2', run: () => selectObject(o) }, '-');
            (scriptMenus[o.kind] || []).forEach(([kind, label]) => {
                entries.push({ label: label, icon: 'bi-code-slash', run: () => scriptInto(o, kind, kind === 'select' || kind === 'selectTop') });
            });
            entries.push('-', {
                label: 'Salin nama', icon: 'bi-clipboard',
                run: async () => { try { await navigator.clipboard.writeText(o.schema + '.' + o.name); } catch (e) { /* clipboard blocked */ } },
            });
        } else {
            entries.push({ label: 'Muat ulang objek', icon: 'bi-arrow-clockwise', run: () => loadTree() });
        }
        openMenu(ev.clientX, ev.clientY, entries);
    });

    function renderStructure(d) {
        const menu = el('ul', { class: 'dropdown-menu dropdown-menu-end' });
        (scriptMenus[d.kind] || []).forEach(([kind, label]) => {
            const item = el('button', { type: 'button', class: 'dropdown-item', text: label });
            item.addEventListener('click', () => scriptInto(d, kind, kind === 'select' || kind === 'selectTop'));
            menu.appendChild(el('li', {}, [item]));
        });
        const header = el('div', { class: 'd-flex flex-wrap align-items-center gap-2 mb-3' }, [
            icon(kindIcon[d.kind] + ' fs-5'),
            el('h2', { class: 'h5 mb-0', text: d.schema + '.' + d.name }),
            el('span', { class: 'badge text-bg-secondary', text: d.kind }),
            d.approxRows !== null && d.approxRows !== undefined ? el('span', { class: 'text-muted small', text: '≈ ' + number(d.approxRows) + ' baris' }) : null,
            el('div', { class: 'dropdown ms-auto' }, [
                el('button', { type: 'button', class: 'btn btn-sm btn-outline-primary dropdown-toggle', 'data-bs-toggle': 'dropdown', 'aria-expanded': 'false' }, [icon('bi-code-slash'), ' Skrip']),
                menu,
            ]),
        ]);

        const sections = [];
        if (d.columns.length > 0) sections.push(['Kolom', columnsTable(d), d.columns.length]);
        if (d.kind === 'PROCEDURE') sections.push(['Parameter', parametersTable(d), d.parameters.length]);
        if (d.indexes.length > 0) sections.push(['Index', indexesTable(d), d.indexes.length]);
        if (d.foreignKeys.length > 0) sections.push(['Foreign key', foreignKeysTable(d), d.foreignKeys.length]);
        if (d.checks.length > 0) sections.push(['Check', checksTable(d), d.checks.length]);
        if (d.definition) sections.push(['Definisi', definitionBlock(d), null]);

        const tabs = el('ul', { class: 'nav nav-pills pg-result-tabs mb-2', role: 'tablist' });
        const panes = el('div', { class: 'tab-content' });
        sections.forEach(([title, node, count], i) => {
            const id = 'pg-st-' + i;
            tabs.appendChild(el('li', { class: 'nav-item', role: 'presentation' }, [el('button', {
                class: 'nav-link py-1 px-2' + (i === 0 ? ' active' : ''), type: 'button', role: 'tab',
                'data-bs-toggle': 'pill', 'data-bs-target': '#' + id, 'aria-controls': id, 'aria-selected': i === 0 ? 'true' : 'false',
                text: title + (count !== null ? ' (' + count + ')' : ''),
            })]));
            panes.appendChild(el('div', { class: 'tab-pane fade' + (i === 0 ? ' show active' : ''), id: id, role: 'tabpanel' }, [node]));
        });

        const content = sections.length > 0 ? [tabs, panes] : [el('div', { class: 'text-muted small', text: 'Tidak ada detail yang bisa dibaca.' })];
        $('pg-structure').replaceChildren(header, ...content);
    }

    function simpleTable(headers, rows) {
        return el('div', { class: 'pg-grid' }, [el('table', { class: 'table table-sm table-bordered mb-0' }, [
            el('thead', {}, [el('tr', {}, headers.map((h) => el('th', { text: h })))]),
            el('tbody', {}, rows.map((r) => el('tr', {}, r.map((c) => {
                if (c === null || c === undefined || c === '') return el('td');
                return el('td', {}, [c instanceof Node ? c : document.createTextNode(String(c))]);
            })))),
        ])]);
    }

    const badge = (text, cls) => el('span', { class: 'badge ' + cls + ' me-1', text: text });
    const code = (text) => (text ? el('code', { text: text }) : '');

    function columnsTable(d) {
        return simpleTable(['#', 'Nama', 'Tipe', 'Null', 'Kunci', 'Identity', 'Default', 'Dihitung', 'Collation'], d.columns.map((c) => [
            c.ordinal, c.name, c.type, c.isNullable ? 'ya' : 'tidak',
            c.isPrimaryKey ? badge('PK', 'text-bg-primary') : '',
            c.isIdentity ? 'IDENTITY' + (c.identityText || '') : '',
            code(c.defaultDefinition), code(c.computedDefinition), c.collation,
        ]));
    }

    function parameterType(p) {
        const t = p.dataType.toLowerCase();
        if (/^(n?var)?(char|binary)$|^nchar$/.test(t)) {
            const length = p.maxLength < 0 ? 'max' : (t.startsWith('n') ? p.maxLength / 2 : p.maxLength);
            return p.dataType + '(' + length + ')';
        }
        if (t === 'decimal' || t === 'numeric') return p.dataType + '(' + p.precision + ',' + p.scale + ')';
        return p.dataType;
    }

    function parametersTable(d) {
        return simpleTable(['Nama', 'Tipe', 'Arah', 'Opsional'], d.parameters.map((p) => [
            p.name, parameterType(p), p.isOutput ? 'OUTPUT' : 'input', p.hasDefault ? 'ya' : '',
        ]));
    }

    function indexesTable(d) {
        return simpleTable(['Nama', 'Jenis', 'Kolom kunci', 'Include', 'Filter'], d.indexes.map((i) => [
            el('span', {}, [i.name + ' ', i.isPrimaryKey ? badge('PK', 'text-bg-primary') : i.isUniqueConstraint ? badge('UNIQUE', 'text-bg-info') : i.isUnique ? badge('unik', 'text-bg-info') : '']),
            i.type, i.keyColumns.map((k) => k.name + (k.descending ? ' DESC' : '')).join(', '), i.includedColumns.join(', '), code(i.filter),
        ]));
    }

    function foreignKeysTable(d) {
        return simpleTable(['Nama', 'Kolom', 'Mengacu ke', 'ON DELETE', 'ON UPDATE'], d.foreignKeys.map((f) => [
            f.name, f.columns.join(', '), f.referencedSchema + '.' + f.referencedTable + ' (' + f.referencedColumns.join(', ') + ')',
            f.onDelete.replace('_', ' '), f.onUpdate.replace('_', ' '),
        ]));
    }

    function checksTable(d) {
        return simpleTable(['Nama', 'Definisi'], d.checks.map((c) => [c.name, code(c.definition)]));
    }

    function definitionBlock(d) {
        const copy = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary py-0 mb-1' }, [icon('bi-clipboard'), ' Salin']);
        copy.addEventListener('click', async () => {
            try { await navigator.clipboard.writeText(d.definition); copy.replaceChildren(icon('bi-clipboard-check'), ' Tersalin'); } catch (e) { copy.textContent = 'Gagal'; }
            setTimeout(() => copy.replaceChildren(icon('bi-clipboard'), ' Salin'), 1200);
        });
        return el('div', {}, [copy, el('pre', { class: 'pg-definition mb-0' }, [document.createTextNode(d.definition)])]);
    }

    // ---- new table designer --------------------------------------------------------------------
    const modalEl = $('pg-table-modal');
    const modal = new bootstrap.Modal(modalEl);
    const sizeHints = {
        decimal: '18,2', numeric: '18,2', nvarchar: '100', varchar: '100', nchar: '10', char: '10', varbinary: '100', binary: '16',
        float: '', datetime2: '', time: '', datetimeoffset: '',
    };
    const sizeTypes = new Set(Object.keys(sizeHints));

    function designerRow(preset) {
        const row = el('tr');
        const name = el('input', { class: 'form-control form-control-sm', maxlength: '128', 'aria-label': 'Nama kolom', autocomplete: 'off' });
        const type = el('select', { class: 'form-select form-select-sm', 'aria-label': 'Tipe' }, settings.types.map((t) => el('option', { value: t, text: t })));
        const size = el('input', { class: 'form-control form-control-sm', 'aria-label': 'Ukuran', autocomplete: 'off' });
        const nullable = el('input', { class: 'form-check-input', type: 'checkbox', 'aria-label': 'Boleh null', checked: true });
        const pk = el('input', { class: 'form-check-input', type: 'checkbox', 'aria-label': 'Primary key' });
        const identity = el('input', { class: 'form-check-input', type: 'checkbox', 'aria-label': 'Identity' });
        const dflt = el('input', { class: 'form-control form-control-sm', 'aria-label': 'Default', autocomplete: 'off' });
        const remove = el('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', 'aria-label': 'Hapus kolom' }, [icon('bi-trash')]);
        remove.addEventListener('click', () => row.remove());

        const syncType = () => {
            const t = type.value;
            size.disabled = !sizeTypes.has(t);
            size.placeholder = sizeHints[t] !== undefined ? sizeHints[t] || 'bawaan' : '';
            if (size.disabled) size.value = '';
            identity.disabled = !['int', 'bigint', 'smallint', 'tinyint'].includes(t);
            if (identity.disabled) identity.checked = false;
        };
        type.addEventListener('change', syncType);
        pk.addEventListener('change', () => { if (pk.checked) nullable.checked = false; });
        identity.addEventListener('change', () => { if (identity.checked) { nullable.checked = false; dflt.value = ''; } });

        const cell = (child, center) => el('td', { class: center ? 'text-center' : '' }, [child]);
        row.append(cell(name), cell(type), cell(size), cell(nullable, true), cell(pk, true), cell(identity, true), cell(dflt), cell(remove, true));

        if (preset) {
            name.value = preset.name || '';
            type.value = preset.type || 'nvarchar';
            size.value = preset.size || '';
            nullable.checked = preset.nullable !== false;
            pk.checked = !!preset.pk;
            identity.checked = !!preset.identity;
            dflt.value = preset.dflt || '';
        }
        syncType();
        row.collect = () => {
            const t = type.value;
            const text = size.value.trim();
            const column = { name: name.value.trim(), type: t, nullable: nullable.checked, identity: identity.checked, primaryKey: pk.checked, default: dflt.value.trim() || null };
            if (t === 'decimal' || t === 'numeric') {
                const [p, s] = text.split(',').map((x) => x.trim());
                if (p) column.precision = parseInt(p, 10);
                if (s) column.scale = parseInt(s, 10);
            } else if (t === 'float') {
                if (text) column.precision = parseInt(text, 10);
            } else if (t === 'datetime2' || t === 'time' || t === 'datetimeoffset') {
                if (text) column.scale = parseInt(text, 10);
            } else if (text) {
                column.length = text;
            }
            return column;
        };
        return row;
    }

    function openDesigner() {
        const body = $('pg-nt-table').querySelector('tbody');
        if (body.children.length === 0) {
            body.appendChild(designerRow({ name: 'Id', type: 'int', nullable: false, pk: true, identity: true }));
            body.appendChild(designerRow({ name: 'Name', type: 'nvarchar', size: '100', nullable: false }));
        }
        $('pg-nt-error').classList.add('d-none');
        modal.show();
    }
    $('pg-new-table').addEventListener('click', openDesigner);
    $('pg-nt-add').addEventListener('click', () => $('pg-nt-table').querySelector('tbody').appendChild(designerRow()));
    modalEl.addEventListener('shown.bs.modal', () => $('pg-nt-name').focus());

    $('pg-nt-make').addEventListener('click', async () => {
        const rows = Array.from($('pg-nt-table').querySelectorAll('tbody tr'));
        const res = await api('POST', '/NewTable', { schema: $('pg-nt-schema').value, name: $('pg-nt-name').value.trim(), columns: rows.map((r) => r.collect()) });
        const error = $('pg-nt-error');
        if (!res.ok) { error.textContent = failureText(res); error.classList.remove('d-none'); return; }
        error.classList.add('d-none');
        modal.hide();
        openScript('CREATE TABLE ' + ($('pg-nt-name').value.trim() || 'baru'), res.data.sql, false);
        if (!settings.write) showNotice('info', 'Skrip CREATE TABLE sudah ada di tab baru. Playground sedang hanya baca, jadi menjalankannya akan ditolak: nyalakan Rakit:Playground:AllowWrite untuk membuat tabel.');
    });

    // ---- start ---------------------------------------------------------------------------------
    restoreTabs();
    loadTree();
})();

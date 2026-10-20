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

    // ---- editor --------------------------------------------------------------------------------
    ace.config.set('basePath', root.dataset.ace);
    const editor = ace.edit('pg-editor');
    const isDark = () => document.documentElement.getAttribute('data-bs-theme') === 'dark';
    const applyTheme = () => editor.setTheme(isDark() ? 'ace/theme/tomorrow_night' : 'ace/theme/chrome');
    applyTheme();
    window.addEventListener('rakit-theme', applyTheme);
    editor.session.setMode('ace/mode/sql');
    editor.session.setUseWrapMode(false);
    editor.setOptions({
        fontSize: 13,
        showPrintMargin: false,
        tabSize: 4,
        useSoftTabs: true,
        enableBasicAutocompletion: true,
        enableLiveAutocompletion: true,
        enableSnippets: false,
    });
    editor.renderer.setScrollMargin(6, 6);
    editor.setValue(store.get('rakit-pg-draft') || 'SELECT TOP (10) * FROM app.Customers;\n', -1);
    let draftTimer = 0;
    editor.session.on('change', () => {
        clearTimeout(draftTimer);
        draftTimer = setTimeout(() => store.set('rakit-pg-draft', editor.getValue()), 400);
    });
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

    /** Puts text into the editor: replaces an empty editor, otherwise appends it and selects it so Ctrl+Enter runs just that. */
    function putScript(text) {
        const current = editor.getValue();
        let startRow = 0;
        if (current.trim() === '') {
            editor.setValue(text, -1);
        } else {
            // one blank line between the old text and the new script
            const separator = current.endsWith('\n') ? '\n' : '\n\n';
            startRow = current.split('\n').length + (current.endsWith('\n') ? 0 : 1);
            const last = editor.session.getLength() - 1;
            editor.session.insert({ row: last, column: editor.session.getLine(last).length }, separator + text);
        }
        const lastRow = editor.session.getLength() - 1;
        editor.selection.setSelectionRange({
            start: { row: startRow, column: 0 },
            end: { row: lastRow, column: editor.session.getLine(lastRow).length },
        });
        editor.scrollToLine(startRow, true, true, () => {});
        editor.focus();
        $('pg-tab-query').click();
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
    let controller = null;
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
        if (controller) return;
        const selected = editor.getSelectedText();
        const useSelection = selected.trim() !== '';
        const sql = useSelection ? selected : editor.getValue();
        if (sql.trim() === '') { showNotice('warning', 'Tulis SQL dulu.'); return; }
        const lineOffset = useSelection ? editor.getSelectionRange().start.row : 0;

        editor.session.clearAnnotations();
        controller = new AbortController();
        $('pg-run').disabled = true;
        $('pg-cancel').classList.remove('d-none');
        showNotice('secondary', 'Menjalankan...');
        addHistory(sql);

        try {
            const res = await api('POST', '/Run', {
                sql: sql,
                maxRows: clampInt($('pg-maxrows'), 1, settings.maxRows, settings.defaultRows),
                timeoutSeconds: clampInt($('pg-timeout'), 1, settings.maxTimeout, settings.timeout),
                rollback: !!(rollbackBox && rollbackBox.checked),
            }, controller.signal);
            if (res.ok && res.data) renderRun(res.data, lineOffset);
            else {
                let text = failureText(res);
                if (res.data && res.data.line) text += ' (baris ' + (res.data.line + lineOffset) + ')';
                showNotice('danger', text);
            }
        } catch (e) {
            if (e && e.name === 'AbortError') showNotice('warning', 'Dibatalkan.');
            else showNotice('danger', 'Server tidak dapat dihubungi.');
        } finally {
            controller = null;
            $('pg-run').disabled = false;
            $('pg-cancel').classList.add('d-none');
        }
    }
    $('pg-run').addEventListener('click', run);
    $('pg-cancel').addEventListener('click', () => { if (controller) controller.abort(); });

    function showNotice(kind, text) {
        $('pg-results').replaceChildren(el('div', { class: 'alert alert-' + kind + ' mb-0', role: 'alert', text: text }));
    }

    const number = (n) => Number(n).toLocaleString('id-ID');

    function renderRun(data, lineOffset) {
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

        if (data.error) parts.push(renderError(data.error, lineOffset));

        const tabs = el('ul', { class: 'nav nav-pills pg-result-tabs mb-2', role: 'tablist' });
        const panes = el('div', { class: 'tab-content' });
        const items = data.resultSets.map((s, i) => ({
            label: 'Hasil ' + (i + 1) + ' (' + number(s.totalRows) + ')', node: renderGrid(s), title: 'Batch ' + s.batch,
        }));
        if (data.messages.length > 0 || items.length === 0) {
            items.push({ label: 'Pesan (' + data.messages.length + ')', node: renderMessages(data.messages), title: '' });
        }
        items.forEach((item, i) => {
            const id = 'pg-res-' + i;
            const link = el('button', {
                class: 'nav-link py-1 px-2' + (i === 0 ? ' active' : ''), type: 'button', role: 'tab', title: item.title,
                'data-bs-toggle': 'pill', 'data-bs-target': '#' + id, 'aria-controls': id, 'aria-selected': i === 0 ? 'true' : 'false', text: item.label,
            });
            tabs.appendChild(el('li', { class: 'nav-item', role: 'presentation' }, [link]));
            panes.appendChild(el('div', { class: 'tab-pane fade' + (i === 0 ? ' show active' : ''), id: id, role: 'tabpanel' }, [item.node]));
        });
        parts.push(tabs, panes);
        $('pg-results').replaceChildren(...parts);
    }

    function renderError(error, lineOffset) {
        const box = el('div', { class: 'alert alert-danger mb-2', role: 'alert' }, [
            el('div', { class: 'fw-semibold', text: error.message }),
        ]);
        const where = [];
        if (error.line) {
            const row = error.line + lineOffset;
            const link = el('a', { href: '#', text: 'baris ' + row });
            link.addEventListener('click', (ev) => { ev.preventDefault(); editor.gotoLine(row, 0, true); editor.focus(); });
            where.push(el('span', {}, ['Di ', link]));
            editor.session.setAnnotations([{ row: row - 1, column: 0, text: error.message, type: 'error' }]);
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

    function renderGrid(set) {
        const head = el('tr', {}, [el('th', { class: 'pg-rownum', text: '#' })].concat(
            set.columns.map((c, i) => el('th', { text: c.name || '(kolom ' + (i + 1) + ')', title: c.type }))));
        const body = set.rows.map((row, r) => el('tr', {}, [el('td', { class: 'pg-rownum text-muted', text: r + 1 })].concat(
            row.map((v) => (v === null ? nullCell() : el('td', { text: v }))))));

        const tools = el('div', { class: 'd-flex flex-wrap align-items-center gap-2 mb-1 small' });
        tools.appendChild(el('span', { class: 'text-muted', text: set.truncated
            ? 'Menampilkan ' + number(set.rows.length) + ' dari ' + number(set.totalRows) + ' baris'
            : number(set.rows.length) + ' baris' }));
        if (set.truncated) tools.appendChild(el('span', { class: 'badge text-bg-warning', text: 'dipotong' }));
        const copy = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary py-0 ms-auto' }, [icon('bi-clipboard'), ' Salin']);
        copy.addEventListener('click', async () => {
            try { await navigator.clipboard.writeText(toDelimited(set, '\t')); copy.replaceChildren(icon('bi-clipboard-check'), ' Tersalin'); } catch (e) { copy.textContent = 'Gagal'; }
            setTimeout(() => copy.replaceChildren(icon('bi-clipboard'), ' Salin'), 1200);
        });
        const csv = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary py-0' }, [icon('bi-download'), ' CSV']);
        csv.addEventListener('click', () => download('hasil.csv', toDelimited(set, ',', true), 'text/csv'));
        tools.append(copy, csv);

        const wrap = el('div', { class: 'pg-grid' }, [
            el('table', { class: 'table table-sm table-bordered table-hover mb-0' }, [el('thead', {}, [head]), el('tbody', {}, body)]),
        ]);
        if (set.rows.length === 0) wrap.appendChild(el('div', { class: 'text-muted small p-2', text: 'Tidak ada baris.' }));
        return el('div', {}, [tools, wrap]);
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

    function download(name, text, type) {
        const url = URL.createObjectURL(new Blob(['﻿' + text], { type: type + ';charset=utf-8' }));
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
            const play = el('button', { type: 'button', class: 'pg-act', title: 'SELECT 100 teratas dan jalankan', 'aria-label': 'SELECT 100 teratas ' + key }, [icon('bi-play-fill')]);
            play.addEventListener('click', () => scriptInto(o, 'select', true));
            actions.appendChild(play);
        } else if (o.kind === 'PROCEDURE') {
            const exec = el('button', { type: 'button', class: 'pg-act', title: 'Skrip EXEC', 'aria-label': 'Skrip EXEC ' + key }, [icon('bi-terminal')]);
            exec.addEventListener('click', () => scriptInto(o, 'exec', false));
            actions.appendChild(exec);
        }
        return el('div', { class: 'pg-item', 'data-key': key, 'data-name': o.name.toLowerCase(), role: 'treeitem' }, [open, actions]);
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
        putScript(res.data.sql);
        if (thenRun) run();
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
        TABLE: [['select', 'SELECT 100 teratas'], ['insert', 'INSERT'], ['update', 'UPDATE'], ['delete', 'DELETE'], ['create', 'CREATE TABLE'], ['addColumn', 'Tambah kolom'], ['drop', 'DROP TABLE']],
        VIEW: [['select', 'SELECT 100 teratas'], ['definition', 'Definisi'], ['alter', 'Ubah (CREATE OR ALTER)'], ['drop', 'DROP VIEW']],
        PROCEDURE: [['exec', 'EXEC'], ['definition', 'Definisi'], ['alter', 'Ubah (CREATE OR ALTER)'], ['drop', 'DROP PROCEDURE']],
        FUNCTION: [['definition', 'Definisi'], ['alter', 'Ubah (CREATE OR ALTER)'], ['drop', 'DROP FUNCTION']],
    };

    function renderStructure(d) {
        const menu = el('ul', { class: 'dropdown-menu dropdown-menu-end' });
        (scriptMenus[d.kind] || []).forEach(([kind, label]) => {
            const item = el('button', { type: 'button', class: 'dropdown-item', text: label });
            item.addEventListener('click', () => scriptInto(d, kind, kind === 'select'));
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
        putScript(res.data.sql);
        if (!settings.write) showNotice('info', 'Skrip CREATE TABLE sudah ada di editor. Playground sedang hanya baca, jadi menjalankannya akan ditolak: nyalakan Rakit:Playground:AllowWrite untuk membuat tabel.');
    });

    // ---- start ---------------------------------------------------------------------------------
    loadTree();
})();

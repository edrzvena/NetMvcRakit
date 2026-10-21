// Runtime page of a Table module: loads one page of rows as JSON and draws the grid. All state (page, size, sort,
// search) lives in the page URL under the reserved _skip/_take/_order/_q keys; every OTHER key of the URL is passed on
// to the server untouched because the data source may read it through its parameter mappings (e.g. ?id=2).
// When the module has record actions (Table CRUD), /data also says which buttons to show and gives each row's key; add / edit
// happen in a Bootstrap modal, delete behind a confirmation modal, and every write goes out as FormData with the anti-forgery
// header. The server's per-field messages (400 `errors`) are drawn under the matching inputs.
// Filters (the panel above the grid) live in the URL too, under the filter's own name; the "more" menu refreshes, exports the page or
// every matching row as CSV and imports a CSV; a row's "Lihat" opens the read-only view of the record.
(function () {
    'use strict';

    const root = document.getElementById('object-page');
    if (!root) return;

    const $ = (id) => document.getElementById(id);
    const dataUrl = root.dataset.url;
    const defaultTake = parseInt(root.dataset.take, 10) || 10;
    const maxTake = parseInt(root.dataset.maxTake, 10) || 100;
    const CONTROL = ['_skip', '_take', '_order', '_q'];
    let filterInfo = []; // the filter panel's inputs: [{name, type, lookup}]
    try { filterInfo = JSON.parse(root.dataset.filters || '[]') || []; } catch (e) { filterInfo = []; }
    const filterNames = filterInfo.map((f) => f.name.toLowerCase());
    let rowActions = []; // workflow buttons drawn on every row: [{code, label, style, confirm}]
    try { rowActions = JSON.parse(root.dataset.rowActions || '[]') || []; } catch (e) { rowActions = []; }

    // ---- state, read from / written to the URL ----
    const params = new URLSearchParams(window.location.search);
    const state = {
        skip: Math.max(parseInt(params.get('_skip'), 10) || 0, 0),
        take: parseInt(params.get('_take'), 10) || defaultTake,
        order: params.get('_order') || '',
        q: params.get('_q') || '',
    };
    const others = []; // [key, value] of every non-control key, kept in order
    params.forEach((value, key) => { if (!CONTROL.includes(key)) others.push([key, value]); });

    function query() {
        const p = new URLSearchParams();
        others.forEach(([k, v]) => p.append(k, v));
        if (state.skip > 0) p.set('_skip', String(state.skip));
        if (state.take !== defaultTake) p.set('_take', String(state.take));
        if (state.order) p.set('_order', state.order);
        if (state.q) p.set('_q', state.q);
        return p.toString();
    }

    // ---- DOM helpers (textContent only: nothing from the database is ever parsed as HTML) ----
    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (k === 'class') node.className = v;
            else if (k === 'text') node.textContent = v;
            else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
            else if (v === true) node.setAttribute(k, '');
            else if (v !== false && v != null) node.setAttribute(k, v);
        });
        (children || []).forEach((c) => node.appendChild(c));
        return node;
    }

    function showError(message) {
        const box = $('page-error');
        box.textContent = message;
        box.classList.remove('d-none');
        $('grid').replaceChildren();
        $('pager').classList.add('d-none');
        $('page-status').textContent = '';
    }

    // ---- rendering ----
    function sortOf(order) {
        const cut = order.lastIndexOf('_');
        return cut > 0 ? { field: order.slice(0, cut).toLowerCase(), desc: order.slice(cut + 1).toLowerCase() === 'desc' } : null;
    }

    function render(data) {
        $('page-error').classList.add('d-none');
        $('pager').classList.remove('d-none');
        state.skip = data.skip;
        state.take = data.take;
        state.order = data.orderBy || '';

        const actions = data.actions || {};
        const showActions = (actions.canView || actions.canEdit || actions.canDelete || rowActions.length > 0) && Array.isArray(data.keys);
        const importItem = $('menu-import');
        if (importItem) importItem.classList.toggle('d-none', !actions.canImport);
        lastRowCount = data.rows.length;
        const addButton = $('record-add');
        if (addButton) addButton.classList.toggle('d-none', !actions.canAdd);

        const sort = data.orderBy ? sortOf(data.orderBy) : null;
        const headCells = data.columns.map((c) => {
            const align = c.format === 'number' ? 'text-end' : '';
            if (!c.sortable) return el('th', { scope: 'col', class: align, text: c.label });

            const active = sort && sort.field === c.field.toLowerCase();
            const mark = active ? (sort.desc ? ' ▼' : ' ▲') : '';
            const link = el('a', { href: '#', class: 'text-decoration-none text-reset', text: c.label + mark });
            link.addEventListener('click', (ev) => {
                ev.preventDefault();
                state.order = c.field + '_' + (active && !sort.desc ? 'desc' : 'asc');
                state.skip = 0;
                load();
            });
            return el('th', { scope: 'col', class: align, 'aria-sort': active ? (sort.desc ? 'descending' : 'ascending') : 'none' }, [link]);
        });
        if (showActions) headCells.push(el('th', { scope: 'col', class: 'text-end', text: 'Aksi' }));
        const head = el('tr', {}, headCells);

        const body = data.rows.length === 0
            ? [el('tr', {}, [el('td', { colspan: String(Math.max(data.columns.length + (showActions ? 1 : 0), 1)), class: 'text-muted text-center py-4', text: 'Tidak ada data.' })])]
            : data.rows.map((row, r) => {
                const cells = row.map((value, i) => {
                    const align = data.columns[i] && data.columns[i].format === 'number' ? 'text-end' : '';
                    return value === null ? el('td', { class: align }, [el('span', { class: 'text-muted', text: '-' })]) : el('td', { class: align, text: value });
                });
                if (showActions) {
                    const key = data.keys[r];
                    const buttons = [];
                    if (key != null && actions.canView) buttons.push(el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: 'Lihat', onclick: () => openView(key, row) }));
                    if (key != null && actions.canEdit) buttons.push(el('button', { type: 'button', class: 'btn btn-sm btn-outline-primary', text: 'Ubah', onclick: () => openEdit(key) }));
                    if (key != null && actions.canDelete) buttons.push(el('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', text: 'Hapus', onclick: () => openDelete(key, row) }));
                    if (key != null) {
                        rowActions.forEach((a) => buttons.push(el('button', {
                            type: 'button', class: 'btn btn-sm btn-outline-' + a.style, text: a.label,
                            onclick: (ev) => runAction(a, key, ev.currentTarget),
                        })));
                    }
                    cells.push(el('td', { class: 'text-end text-nowrap' }, buttons));
                }
                return el('tr', {}, cells);
            });

        $('grid').replaceChildren(el('table', { class: 'table table-sm table-hover table-striped align-middle' }, [
            el('thead', {}, [head]), el('tbody', {}, body),
        ]));

        // pager
        const first = data.rows.length === 0 ? 0 : data.skip + 1;
        const last = data.skip + data.rows.length;
        $('pager-info').textContent = data.rows.length === 0
            ? (data.total === 0 ? 'Tidak ada data.' : 'Tidak ada baris pada halaman ini.')
            : 'Menampilkan ' + first + '-' + last + (data.total != null ? ' dari ' + data.total : '');
        $('page-prev').disabled = data.skip === 0;
        $('page-next').disabled = !data.hasNext;

        const sizes = [...new Set([10, 25, 50, 100, defaultTake, data.take].filter((n) => n >= 1 && n <= maxTake))].sort((a, b) => a - b);
        $('page-size').replaceChildren(...sizes.map((n) => {
            const o = el('option', { value: String(n), text: String(n) });
            if (n === data.take) o.selected = true;
            return o;
        }));

        $('search-input').value = data.search || '';
        state.q = data.search || '';
        $('page-status').textContent = '';

        const qs = query();
        window.history.replaceState(null, '', window.location.pathname + (qs ? '?' + qs : ''));
    }

    // ---- loading ----
    let loadId = 0;
    let lastRowCount = 0;
    async function load() {
        const mine = ++loadId;
        $('page-status').textContent = 'Memuat...';
        try {
            const res = await fetch(dataUrl + '?' + query(), { headers: { Accept: 'application/json' } });
            let data = null;
            try { data = await res.json(); } catch (e) { /* non-JSON body */ }
            if (mine !== loadId) return; // a newer request has taken over
            if (res.ok && data) render(data);
            else showError((data && data.message) || 'Data tidak dapat dimuat (HTTP ' + res.status + ').');
        } catch (e) {
            if (mine === loadId) showError('Server tidak dapat dihubungi.');
        }
    }

    // ---- controls ----
    $('search-form').addEventListener('submit', (ev) => {
        ev.preventDefault();
        state.q = $('search-input').value.trim();
        state.skip = 0;
        load();
    });
    $('search-clear').addEventListener('click', () => {
        $('search-input').value = '';
        state.q = '';
        state.skip = 0;
        load();
    });
    $('page-prev').addEventListener('click', () => { state.skip = Math.max(0, state.skip - state.take); load(); });
    $('page-next').addEventListener('click', () => { state.skip += state.take; load(); });
    $('page-size').addEventListener('change', () => {
        state.take = parseInt($('page-size').value, 10) || defaultTake;
        state.skip = 0;
        load();
    });

    // ---- filters: values in the URL under the filter's name; a select may read its choices from a lookup ----
    const filterInputs = () => [...document.querySelectorAll('[data-filter]')];
    const filterValue = (name) => { const hit = others.find(([k]) => k.toLowerCase() === name.toLowerCase()); return hit ? hit[1] : ''; };

    function syncFilterInputs() {
        filterInputs().forEach((input) => {
            const v = filterValue(input.dataset.filter);
            if (input.tagName === 'SELECT' && v !== '' && ![...input.options].some((o) => o.value === v)) {
                input.appendChild(el('option', { value: v, text: v }));
            }
            input.value = v;
        });
        updateFilterCount();
    }
    function updateFilterCount() {
        const badge = $('filter-count');
        if (!badge) return;
        const active = filterNames.filter((n) => filterValue(n) !== '').length;
        badge.textContent = String(active);
        badge.classList.toggle('d-none', active === 0);
        const panel = $('filter-panel');
        if (active > 0 && panel && panel.classList.contains('d-none')) toggleFilterPanel(true);
    }
    function toggleFilterPanel(open) {
        const panel = $('filter-panel');
        if (!panel) return;
        panel.classList.toggle('d-none', !open);
        $('filter-toggle').setAttribute('aria-expanded', open ? 'true' : 'false');
    }
    function readFilters() {
        for (let i = others.length - 1; i >= 0; i--) if (filterNames.includes(others[i][0].toLowerCase())) others.splice(i, 1);
        filterInputs().forEach((input) => {
            const v = input.value.trim();
            if (v !== '') others.push([input.dataset.filter, v]);
        });
        state.skip = 0;
        updateFilterCount();
        load();
    }
    async function loadFilterLookups() {
        await Promise.all(filterInputs().filter((i) => i.dataset.filterLookup === '1').map(async (select) => {
            const url = (root.dataset.filterLookupUrl || '').replace('__NAME__', encodeURIComponent(select.dataset.filter));
            try {
                const res = await fetch(url, { headers: { Accept: 'application/json' } });
                const data = res.ok ? await res.json() : null;
                if (!data) return;
                data.items.forEach((item) => select.appendChild(el('option', { value: item.value, text: item.text })));
            } catch (e) { /* the filter stays usable with "Semua" */ }
        }));
        syncFilterInputs();
    }
    if ($('filter-form')) {
        $('filter-toggle').addEventListener('click', () => toggleFilterPanel($('filter-panel').classList.contains('d-none')));
        $('filter-form').addEventListener('submit', (ev) => { ev.preventDefault(); readFilters(); });
        $('filter-clear').addEventListener('click', () => { filterInputs().forEach((i) => { i.value = ''; }); readFilters(); });
        syncFilterInputs();
        loadFilterLookups();
    }

    // ---- menu: refresh, export (this page / everything) and import ----
    function downloadName(res) {
        const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(res.headers.get('Content-Disposition') || '');
        return m ? decodeURIComponent(m[1]) : 'data.csv';
    }
    async function exportCsv(all, button) {
        clearMessage();
        const p = new URLSearchParams(query());
        if (all) { p.delete('_skip'); p.delete('_take'); p.set('_all', '1'); }
        if (button) button.disabled = true;
        try {
            const res = await fetch(root.dataset.exportUrl + '?' + p.toString(), { headers: { Accept: 'text/csv' } });
            if (!res.ok) {
                let message = 'Export gagal (HTTP ' + res.status + ').';
                try { message = (await res.json()).message || message; } catch (e) { /* not JSON */ }
                showMessage(message, false);
                return;
            }
            const blob = await res.blob();
            const link = el('a', { href: URL.createObjectURL(blob), download: downloadName(res) });
            document.body.appendChild(link);
            link.click();
            link.remove();
            setTimeout(() => URL.revokeObjectURL(link.href), 2000);
            const rows = res.headers.get('X-Rakit-Export-Rows');
            showMessage('Export selesai' + (rows ? ' (' + rows + ' baris)' : '') + '.' +
                (res.headers.get('X-Rakit-Export-Truncated') === '1' ? ' Hasil dipotong di ' + rows + ' baris: persempit dengan pencarian atau filter.' : ''), true);
        } catch (e) {
            showMessage('Server tidak dapat dihubungi.', false);
        } finally {
            if (button) button.disabled = false;
        }
    }
    if ($('menu-refresh')) $('menu-refresh').addEventListener('click', () => { clearMessage(); load(); });
    if ($('menu-export')) $('menu-export').addEventListener('click', (ev) => exportCsv(false, ev.currentTarget));
    if ($('menu-export-all')) $('menu-export-all').addEventListener('click', (ev) => exportCsv(true, ev.currentTarget));

    // -- import
    if ($('import-form')) {
        $('menu-import').addEventListener('click', () => {
            $('import-file').value = '';
            $('import-alert').classList.add('d-none');
            $('import-result').replaceChildren();
            window.RakitRecordForm.modalOf('import-modal').show();
        });
        $('import-form').addEventListener('submit', async (ev) => {
            ev.preventDefault();
            const file = $('import-file').files[0];
            const alertBox = $('import-alert');
            alertBox.classList.add('d-none');
            $('import-result').replaceChildren();
            if (!file) { alertBox.textContent = 'Pilih file CSV dulu.'; alertBox.classList.remove('d-none'); return; }
            const body = new FormData();
            body.append('file', file);
            $('import-run').disabled = true;
            const res = await window.RakitRecordForm.create({ root: root, form: null }).send('POST', root.dataset.importUrl, body);
            $('import-run').disabled = false;
            if (!res.ok || !res.data || res.data.total === undefined) {
                alertBox.textContent = (res.data && res.data.message) || 'Impor gagal (HTTP ' + res.status + ').';
                alertBox.classList.remove('d-none');
                return;
            }
            const d = res.data;
            const parts = [el('div', { class: 'alert alert-' + (d.imported === d.total ? 'success' : d.imported === 0 ? 'danger' : 'warning'), role: 'status',
                text: d.imported + ' dari ' + d.total + ' baris tersimpan' + (d.total - d.imported > 0 ? ', ' + (d.total - d.imported) + ' gagal' : '') + '.' })];
            d.notes.forEach((n) => parts.push(el('div', { class: 'small text-muted mb-1', text: n })));
            if (d.failures.length > 0) {
                parts.push(el('div', { class: 'table-responsive border', style: 'max-height: 240px; overflow: auto' }, [
                    el('table', { class: 'table table-sm mb-0' }, [
                        el('thead', {}, [el('tr', {}, [el('th', { text: 'Baris' }), el('th', { text: 'Alasan' })])]),
                        el('tbody', {}, d.failures.map((f) => el('tr', {}, [el('td', { text: String(f.row) }), el('td', { text: f.message })]))),
                    ]),
                ]));
                if (d.total - d.imported > d.failures.length) parts.push(el('div', { class: 'small text-muted mt-1', text: 'Hanya ' + d.failures.length + ' kegagalan pertama yang ditampilkan.' }));
            }
            $('import-result').replaceChildren(...parts);
            if (d.imported > 0) { state.skip = 0; load(); }
        });
    }

    // ---- the read-only view of a record ("Lihat") ----
    async function openView(key, row) {
        clearMessage();
        const alertBox = $('view-alert');
        alertBox.classList.add('d-none');
        $('view-body').replaceChildren();
        const label = row.find((v) => v != null && String(v).trim() !== '');
        $('view-title').textContent = 'Detail' + (label != null ? ': ' + label : '');
        window.RakitRecordForm.modalOf('view-modal').show();
        const res = await window.RakitRecordForm.create({ root: root, form: null }).send('GET', (root.dataset.viewUrl || '').replace('__KEY__', encodeURIComponent(key)));
        if (!res.ok) {
            alertBox.textContent = (res.data && res.data.message) || 'Detail tidak dapat dimuat (HTTP ' + res.status + ').';
            alertBox.classList.remove('d-none');
            return;
        }
        const list = el('dl', { class: 'row mb-0' });
        res.data.items.forEach((item) => {
            if (item.isSection) {
                list.appendChild(el('div', { class: 'col-12' }, [el('h3', { class: 'h6 border-bottom pb-1 mt-3 mb-1', text: item.label }),
                    item.text ? el('p', { class: 'small text-muted mb-1', text: item.text }) : el('span')]));
                return;
            }
            list.appendChild(el('dt', { class: 'col-sm-4 text-muted fw-normal', text: item.label }));
            list.appendChild(el('dd', { class: 'col-sm-8' }, [item.text === null ? el('span', { class: 'text-muted', text: '-' }) : document.createTextNode(item.text)]));
        });
        $('view-body').replaceChildren(list);
    }

    // ---- record actions: add / edit / delete ----
    // The form helpers (calls, field errors, select options, browse picker) live in record-form.js, shared with the Form page.
    const recordForm = $('record-form');
    const rf = window.RakitRecordForm.create({ root: root, form: recordForm, alertId: 'record-alert', hostModalId: 'record-modal' });
    const modalOf = rf.modalOf;
    let editingKey = null;  // null = adding
    let deletingKey = null;

    function showMessage(text, ok) {
        const box = $('page-message');
        box.className = 'alert ' + (ok ? 'alert-success' : 'alert-danger');
        box.textContent = text;
    }
    function clearMessage() { $('page-message').className = 'alert d-none'; }

    async function openAdd() {
        clearMessage();
        rf.clearErrors();
        await rf.loadSelects();
        rf.reset();
        editingKey = null;
        $('record-title').textContent = 'Tambah data';
        modalOf('record-modal').show();
    }
    async function openEdit(key) {
        clearMessage();
        const res = await rf.send('GET', rf.keyUrl(key));
        if (!res.ok) { showMessage(rf.failureText(res), false); return; }
        rf.clearErrors();
        await rf.loadSelects();
        rf.fill(res.data.values || {});
        editingKey = key;
        $('record-title').textContent = 'Ubah data';
        modalOf('record-modal').show();
    }

    if (recordForm) {
        // the Tambah button is left out when display.buttons.add is off, while the modal stays for Ubah
        if ($('record-add')) $('record-add').addEventListener('click', openAdd);
        $('record-modal').addEventListener('shown.bs.modal', () => {
            const first = rf.inputs().find((i) => !i.disabled);
            if (first) first.focus();
        });
        recordForm.addEventListener('submit', async (ev) => {
            ev.preventDefault();
            rf.clearErrors();
            const save = $('record-save');
            save.disabled = true;
            const res = await rf.send('POST', editingKey === null ? rf.recordUrl : rf.keyUrl(editingKey), new FormData(recordForm));
            save.disabled = false;
            if (res.ok) {
                modalOf('record-modal').hide();
                showMessage((res.data && res.data.message) || 'Tersimpan.', true);
                load();
            } else {
                rf.showErrors(res);
            }
        });
    }

    // ---- workflow buttons: toolbar ones (data-action) and per-row ones (rowActions) run a workflow on the server ----
    // A workflow that reaches an Action step answers with a form (display.kind "form"); its buttons come back through env.
    const actionEnv = {
        resumeUrl: root.dataset.resumeUrl,
        send: rf.send,
        failureText: rf.failureText,
        onDone: (data) => {
            showMessage(data.message || 'Aksi dijalankan.', true);
            load();
        },
    };
    async function runAction(action, key, button) {
        if (action.confirm && !window.confirm(action.confirm)) return;
        clearMessage();
        let url = (root.dataset.actionUrl || '').replace('__CODE__', encodeURIComponent(action.code));
        if (key != null) url += '/' + encodeURIComponent(key);
        if (button) button.disabled = true;
        const res = await rf.send('POST', url);
        if (button) button.disabled = false;
        if (res.ok) {
            const display = res.data && res.data.display;
            if (display && display.kind === 'form' && window.RakitActionResult) {
                window.RakitActionResult.show(display, actionEnv); // the message and the reload come when the form is answered
                return;
            }
            showMessage((res.data && res.data.message) || 'Aksi dijalankan.', true);
            if (window.RakitActionResult && window.RakitActionResult.show(display)) { /* a page or another module is shown */ }
            load(); // the workflow may have changed the data
        } else {
            showMessage(rf.failureText(res), false);
        }
    }
    document.querySelectorAll('[data-action]').forEach((button) => {
        button.addEventListener('click', () => runAction({ code: button.dataset.action, confirm: button.dataset.confirm }, null, button));
    });

    // -- delete
    function openDelete(key, row) {
        clearMessage();
        deletingKey = key;
        $('delete-alert').classList.add('d-none');
        const label = row.find((v) => v != null && String(v).trim() !== '');
        $('delete-text').textContent = 'Hapus data' + (label != null ? ' "' + label + '"' : ' ini') + '? Tindakan ini tidak dapat dibatalkan.';
        modalOf('delete-modal').show();
    }
    if ($('delete-confirm')) {
        $('delete-confirm').addEventListener('click', async () => {
            const button = $('delete-confirm');
            button.disabled = true;
            const res = await rf.send('POST', rf.keyUrl(deletingKey, '/delete'));
            button.disabled = false;
            if (res.ok) {
                modalOf('delete-modal').hide();
                showMessage((res.data && res.data.message) || 'Dihapus.', true);
                if (lastRowCount === 1 && state.skip > 0) state.skip = Math.max(0, state.skip - state.take); // do not stay on an emptied page
                load();
            } else {
                const box = $('delete-alert');
                box.textContent = rf.failureText(res);
                box.classList.remove('d-none');
            }
        });
    }

    $('search-input').value = state.q;
    load();
})();

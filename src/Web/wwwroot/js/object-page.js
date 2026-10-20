// Runtime page of a Table module: loads one page of rows as JSON and draws the grid. All state (page, size, sort,
// search) lives in the page URL under the reserved _skip/_take/_order/_q keys; every OTHER key of the URL is passed on
// to the server untouched because the data source may read it through its parameter mappings (e.g. ?id=2).
// When the module has record actions (Table CRUD), /data also says which buttons to show and gives each row's key; add / edit
// happen in a Bootstrap modal, delete behind a confirmation modal, and every write goes out as FormData with the anti-forgery
// header. The server's per-field messages (400 `errors`) are drawn under the matching inputs.
(function () {
    'use strict';

    const root = document.getElementById('object-page');
    if (!root) return;

    const $ = (id) => document.getElementById(id);
    const dataUrl = root.dataset.url;
    const defaultTake = parseInt(root.dataset.take, 10) || 10;
    const maxTake = parseInt(root.dataset.maxTake, 10) || 100;
    const recordUrl = root.dataset.recordUrl;
    const CONTROL = ['_skip', '_take', '_order', '_q'];

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
        const showActions = (actions.canEdit || actions.canDelete) && Array.isArray(data.keys);
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
                    if (key != null && actions.canEdit) buttons.push(el('button', { type: 'button', class: 'btn btn-sm btn-outline-primary', text: 'Ubah', onclick: () => openEdit(key) }));
                    if (key != null && actions.canDelete) buttons.push(el('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', text: 'Hapus', onclick: () => openDelete(key, row) }));
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

    // ---- record actions: add / edit / delete ----
    const recordForm = $('record-form');
    const tokenInput = root.querySelector('input[name="__RequestVerificationToken"]');
    const modalOf = (id) => (window.bootstrap && $(id) ? window.bootstrap.Modal.getOrCreateInstance($(id)) : null);
    let editingKey = null;  // null = adding
    let deletingKey = null;

    function showMessage(text, ok) {
        const box = $('page-message');
        box.className = 'alert ' + (ok ? 'alert-success' : 'alert-danger');
        box.textContent = text;
    }
    function clearMessage() { $('page-message').className = 'alert d-none'; }

    // Write responses are JSON, except when the anti-forgery check or the web server answers first (empty / HTML body).
    async function send(method, url, body) {
        const headers = { Accept: 'application/json' };
        if (tokenInput) headers.RequestVerificationToken = tokenInput.value;
        try {
            const res = await fetch(url, { method: method, headers: headers, body: body });
            let data = null;
            try { data = await res.json(); } catch (e) { /* non-JSON body */ }
            return { ok: res.ok, status: res.status, data: data };
        } catch (e) {
            return { ok: false, status: 0, data: null };
        }
    }
    function failureText(res) {
        if (res.data && res.data.message) return res.data.message;
        return res.status === 0 ? 'Server tidak dapat dihubungi.' : 'Permintaan gagal (HTTP ' + res.status + '). Muat ulang halaman lalu coba lagi.';
    }
    const keyUrl = (key, suffix) => recordUrl + '/' + encodeURIComponent(key) + (suffix || '');

    // -- form
    const inputs = () => (recordForm ? [...recordForm.querySelectorAll('[data-field]')] : []);
    function clearErrors() {
        $('record-alert').classList.add('d-none');
        inputs().forEach((i) => {
            i.classList.remove('is-invalid');
            const feedback = i.closest('[data-group]').querySelector('.invalid-feedback');
            if (feedback) feedback.textContent = '';
        });
    }
    function showFormErrors(res) {
        const general = [res.data && res.data.message ? res.data.message : failureText(res)];
        const errors = (res.data && res.data.errors) || {};
        let first = null;
        Object.entries(errors).forEach(([name, text]) => {
            const input = inputs().find((i) => i.dataset.field.toLowerCase() === name.toLowerCase());
            if (!input) { if (!general.includes(text)) general.push(text); return; }
            input.classList.add('is-invalid');
            input.closest('[data-group]').querySelector('.invalid-feedback').textContent = text;
            first = first || input;
        });
        const box = $('record-alert');
        box.textContent = general.join(' ');
        box.classList.remove('d-none');
        if (first) first.focus();
    }
    function resetForm() {
        inputs().forEach((i) => {
            const d = i.dataset.default || '';
            if (i.type === 'checkbox') i.checked = d.toLowerCase() === 'true';
            else i.value = d;
        });
    }
    function fillForm(values) {
        inputs().forEach((i) => {
            const v = values[i.dataset.field];
            if (i.type === 'checkbox') i.checked = String(v).toLowerCase() === 'true';
            else i.value = v == null ? '' : v;
        });
    }

    async function openAdd() {
        clearMessage();
        clearErrors();
        resetForm();
        editingKey = null;
        $('record-title').textContent = 'Tambah data';
        modalOf('record-modal').show();
    }
    async function openEdit(key) {
        clearMessage();
        const res = await send('GET', keyUrl(key));
        if (!res.ok) { showMessage(failureText(res), false); return; }
        clearErrors();
        fillForm(res.data.values || {});
        editingKey = key;
        $('record-title').textContent = 'Ubah data';
        modalOf('record-modal').show();
    }

    if (recordForm) {
        $('record-add').addEventListener('click', openAdd);
        $('record-modal').addEventListener('shown.bs.modal', () => {
            const first = inputs().find((i) => !i.disabled);
            if (first) first.focus();
        });
        recordForm.addEventListener('submit', async (ev) => {
            ev.preventDefault();
            clearErrors();
            const save = $('record-save');
            save.disabled = true;
            const res = await send('POST', editingKey === null ? recordUrl : keyUrl(editingKey), new FormData(recordForm));
            save.disabled = false;
            if (res.ok) {
                modalOf('record-modal').hide();
                showMessage((res.data && res.data.message) || 'Tersimpan.', true);
                load();
            } else {
                showFormErrors(res);
            }
        });
    }

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
            const res = await send('POST', keyUrl(deletingKey, '/delete'));
            button.disabled = false;
            if (res.ok) {
                modalOf('delete-modal').hide();
                showMessage((res.data && res.data.message) || 'Dihapus.', true);
                if (lastRowCount === 1 && state.skip > 0) state.skip = Math.max(0, state.skip - state.take); // do not stay on an emptied page
                load();
            } else {
                const box = $('delete-alert');
                box.textContent = failureText(res);
                box.classList.remove('d-none');
            }
        });
    }

    $('search-input').value = state.q;
    load();
})();

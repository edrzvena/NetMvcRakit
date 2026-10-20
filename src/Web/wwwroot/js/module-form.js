// Module form: the Display tab (page size, columns) lives in the browser and is serialised into the hidden ConfigJson
// field on submit, in the same shape the server stores. The list data source is a normal select (the server takes it from there).
(function () {
    'use strict';

    const form = document.getElementById('module-form');
    if (!form) return;

    const $ = (id) => document.getElementById(id);
    const configInput = $('ConfigJson');

    const FORMATS = [['text', 'Text'], ['number', 'Number'], ['date', 'Date'], ['dateTime', 'DateTime'], ['boolean', 'Boolean']];

    let config = {};
    try { config = JSON.parse(configInput.value || '{}') || {}; } catch (e) { config = {}; }
    config.display = config.display || {};
    config.display.columns = config.display.columns || [];
    const columns = config.display.columns; // edited in place: [{field, label, visible, sortable, format}]
    columns.forEach((c) => { if (c.visible === undefined) c.visible = true; });

    let known = null; // Set of lower-case column names last loaded from the data source (null = not loaded)

    // ---- small DOM helpers (textContent only: nothing from the database is ever parsed as HTML) ----
    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (k === 'class') node.className = v;
            else if (k === 'text') node.textContent = v;
            else if (k === 'value') node.value = v;
            else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
            else if (v === true) node.setAttribute(k, '');
            else if (v !== false && v != null) node.setAttribute(k, v);
        });
        (children || []).forEach((c) => node.appendChild(c));
        return node;
    }
    function iconButton(text, title, onClick, disabled) {
        return el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: text, title: title, onclick: onClick, disabled: !!disabled });
    }

    // ---- simple fields ----
    $('take').value = config.display.take || 10;
    $('maxTake').value = config.display.maxTake || 100;

    // ---- columns editor ----
    function renderColumns() {
        const host = $('columns');
        if (columns.length === 0) {
            host.replaceChildren(el('div', { class: 'text-muted small mb-2', text: 'Belum ada kolom: semua kolom hasil akan ditampilkan apa adanya.' }));
            return;
        }
        const head = el('div', { class: 'row g-2 small text-muted mb-1 d-none d-md-flex' }, [
            el('div', { class: 'col-md-3', text: 'Field' }), el('div', { class: 'col-md-3', text: 'Label' }),
            el('div', { class: 'col-md-1', text: 'Tampil' }), el('div', { class: 'col-md-1', text: 'Sortable' }),
            el('div', { class: 'col-md-2', text: 'Format' }), el('div', { class: 'col-md-2', text: '' }),
        ]);
        const rows = columns.map((c, i) => {
            const field = el('input', { class: 'form-control form-control-sm', value: c.field || '', placeholder: 'nama kolom' });
            const label = el('input', { class: 'form-control form-control-sm', value: c.label || '', placeholder: 'label (kosong = field)' });
            const visible = el('input', { type: 'checkbox', class: 'form-check-input', id: 'vis-' + i });
            const sortable = el('input', { type: 'checkbox', class: 'form-check-input', id: 'sort-' + i });
            visible.checked = c.visible !== false;
            sortable.checked = !!c.sortable;
            const format = el('select', { class: 'form-select form-select-sm' });
            FORMATS.forEach(([v, t]) => { const o = el('option', { value: v, text: t }); if (v === (c.format || 'text')) o.selected = true; format.appendChild(o); });

            field.addEventListener('input', () => { c.field = field.value.trim(); warn.classList.toggle('d-none', !isMissing(c)); });
            label.addEventListener('input', () => { c.label = label.value; });
            visible.addEventListener('change', () => { c.visible = visible.checked; });
            sortable.addEventListener('change', () => { c.sortable = sortable.checked; });
            format.addEventListener('change', () => { c.format = format.value; });

            const warn = el('div', { class: 'col-12 small text-warning' + (isMissing(c) ? '' : ' d-none'), text: 'Kolom ini tidak ada di hasil data source (akan tampil kosong).' });
            const move = (d) => () => { const j = i + d; if (j < 0 || j >= columns.length) return; [columns[i], columns[j]] = [columns[j], columns[i]]; renderColumns(); };
            return el('div', { class: 'row g-2 mb-2 align-items-center' }, [
                el('div', { class: 'col-md-3' }, [field]),
                el('div', { class: 'col-md-3' }, [label]),
                el('div', { class: 'col-md-1 form-check ps-4' }, [visible]),
                el('div', { class: 'col-md-1 form-check ps-4' }, [sortable]),
                el('div', { class: 'col-md-2' }, [format]),
                el('div', { class: 'col-md-2 text-nowrap' }, [
                    iconButton('↑', 'Naik', move(-1), i === 0), iconButton('↓', 'Turun', move(1), i === columns.length - 1),
                    iconButton('×', 'Hapus', () => { columns.splice(i, 1); renderColumns(); }),
                ]),
                warn,
            ]);
        });
        host.replaceChildren(head, ...rows);
    }
    const isMissing = (c) => known !== null && c.field && !known.has(c.field.toLowerCase());

    $('add-column').addEventListener('click', () => { columns.push({ field: '', label: '', visible: true, sortable: false, format: 'text' }); renderColumns(); });

    // ---- helpers shared by "load columns" and "preview" ----
    function token() {
        const input = form.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }
    async function postJson(url, body) {
        const res = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: token() },
            body: JSON.stringify(body),
        });
        let data = null;
        try { data = await res.json(); } catch (e) { /* non-JSON body (e.g. 400 from anti-forgery) */ }
        return { ok: res.ok, status: res.status, data: data };
    }
    function errorBox(res, fallback) {
        const d = res.data;
        if (!d) return el('div', { class: 'alert alert-danger mb-0', role: 'alert', text: fallback + ' (HTTP ' + res.status + '). Muat ulang halaman lalu coba lagi.' });
        const box = el('div', { class: 'alert alert-' + (res.status === 403 ? 'info' : 'danger') + ' mb-0', role: 'alert' }, [el('div', { text: d.message || fallback })]);
        (d.errors || []).forEach((line) => box.appendChild(el('div', { class: 'small', text: line })));
        if (d.detail) box.appendChild(el('div', { class: 'small', text: 'Detail: ' + d.detail }));
        return box;
    }
    const dataSourceId = () => $('DataSourceId').value || null;

    // ---- load columns from the data source ----
    $('load-columns').addEventListener('click', async () => {
        const status = $('columns-status');
        const id = dataSourceId();
        if (!id) { status.textContent = 'Pilih data source dulu (tab Source).'; return; }
        status.textContent = 'memuat...';
        try {
            const res = await postJson('/Builder/Module/LoadColumns', { dataSourceId: id });
            if (!res.ok) { status.textContent = ''; $('columns').prepend(errorBox(res, 'Kolom tidak dapat dimuat.')); return; }

            known = new Set(res.data.columns.map((c) => c.name.toLowerCase()));
            const have = new Set(columns.map((c) => (c.field || '').toLowerCase()));
            let added = 0;
            res.data.columns.forEach((c) => {
                if (have.has(c.name.toLowerCase())) return;
                columns.push({ field: c.name, label: '', visible: true, sortable: false, format: c.suggestedFormat || 'text' });
                added++;
            });
            renderColumns();
            status.textContent = added + ' kolom ditambahkan' + (res.data.hasRows ? '' : ' (data source tidak mengembalikan baris: format ditebak "text")') + '.';
        } catch (e) {
            status.textContent = 'Server tidak dapat dihubungi.';
        }
    });
    $('DataSourceId').addEventListener('change', () => { known = null; renderColumns(); });

    // ---- config (shared by Save and Preview) ----
    function collectConfig() {
        const take = parseInt($('take').value, 10) || 10;
        const maxTake = parseInt($('maxTake').value, 10) || 100;
        return {
            display: {
                take: take, maxTake: maxTake,
                columns: columns.filter((c) => c.field).map((c) => {
                    const out = { field: c.field, visible: c.visible !== false, sortable: !!c.sortable, format: c.format || 'text' };
                    if (c.label && c.label.trim()) out.label = c.label.trim();
                    return out;
                }),
            },
            access: config.access || { view: ['*'] },
        };
    }
    form.addEventListener('submit', () => { configInput.value = JSON.stringify(collectConfig()); });

    // ---- preview ----
    const state = { skip: 0, orderBy: null };

    function renderPreview(data) {
        const parts = [];
        parts.push(el('div', { class: 'small text-muted mb-2', text:
            data.rows.length + ' baris' + (data.totalCount != null ? ' dari ' + data.totalCount : '') +
            ' | halaman mulai dari ' + (data.skip + 1) + (data.orderBy ? ' | urut ' + data.orderBy : '') }));

        const head = el('tr', {}, data.columns.map((c) => {
            if (!c.sortable) return el('th', { text: c.label });
            const mark = data.orderBy && data.orderBy.toLowerCase().startsWith(c.field.toLowerCase() + '_')
                ? (data.orderBy.toLowerCase().endsWith('_desc') ? ' ▼' : ' ▲') : '';
            const link = el('a', { href: '#', class: 'text-decoration-none', text: c.label + mark });
            link.addEventListener('click', (ev) => {
                ev.preventDefault();
                const same = data.orderBy && data.orderBy.toLowerCase().startsWith(c.field.toLowerCase() + '_');
                const desc = same && data.orderBy.toLowerCase().endsWith('_asc');
                state.orderBy = c.field + (desc ? '_desc' : '_asc');
                state.skip = 0;
                runPreview();
            });
            return el('th', {}, [link]);
        }));
        const body = data.rows.map((r) => el('tr', {}, r.map((v) =>
            v === null ? el('td', {}, [el('em', { class: 'text-muted', text: 'NULL' })]) : el('td', { text: v }))));
        parts.push(el('div', { class: 'table-responsive border mb-2', style: 'max-height: 360px; overflow: auto' }, [
            el('table', { class: 'table table-sm table-bordered table-striped mb-0' }, [el('thead', {}, [head]), el('tbody', {}, body)]),
        ]));
        if (data.rows.length === 0) parts.push(el('div', { class: 'small text-muted', text: 'Tidak ada baris.' }));

        const prev = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: '« Sebelumnya', disabled: data.skip === 0 });
        const next = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: 'Berikutnya »', disabled: !data.hasNext });
        prev.addEventListener('click', () => { state.skip = Math.max(0, data.skip - data.take); runPreview(); });
        next.addEventListener('click', () => { state.skip = data.skip + data.take; runPreview(); });
        parts.push(el('div', { class: 'd-flex gap-2' }, [prev, next]));
        $('preview-result').replaceChildren(...parts);
    }

    async function runPreview() {
        const button = $('run-preview');
        button.disabled = true;
        $('preview-status').textContent = 'menjalankan...';
        try {
            const res = await postJson('/Builder/Module/Preview', {
                name: $('Name').value, dataSourceId: dataSourceId(), config: collectConfig(),
                query: $('preview-query').value, search: $('preview-search').value,
                skip: state.skip, orderBy: state.orderBy,
            });
            if (res.ok && res.data) renderPreview(res.data);
            else $('preview-result').replaceChildren(errorBox(res, 'Preview gagal.'));
        } catch (e) {
            $('preview-result').replaceChildren(el('div', { class: 'alert alert-danger mb-0', text: 'Preview gagal: server tidak dapat dihubungi.' }));
        } finally {
            button.disabled = false;
            $('preview-status').textContent = '';
        }
    }
    $('run-preview').addEventListener('click', () => { state.skip = 0; runPreview(); });

    renderColumns();
})();

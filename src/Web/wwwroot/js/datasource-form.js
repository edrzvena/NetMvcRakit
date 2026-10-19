// Data Source form: the dynamic parts (columns, filters, sort, parameters, query) live in the browser and are
// serialised into the hidden DefinitionJson field on submit, in the same shape the server stores.
(function () {
    'use strict';

    const form = document.getElementById('ds-form');
    if (!form) return;

    const $ = (id) => document.getElementById(id);
    const definitionInput = $('DefinitionJson');

    const OPERATORS = ['=', '<>', '>', '>=', '<', '<=', 'like'];
    const SOURCES = [
        ['queryString', 'QueryString'], ['formField', 'FormField'], ['routeValue', 'RouteValue'],
        ['userClaim', 'UserClaim'], ['constant', 'Constant'], ['stepOutput', 'StepOutput'], ['expression', 'Expression'],
    ];

    let def = {};
    try { def = JSON.parse(definitionInput.value || '{}') || {}; } catch (e) { def = {}; }
    def.table = def.table || { schema: '', name: '', columns: [], filters: [], sort: [] };
    def.table.columns = def.table.columns || [];
    def.table.filters = def.table.filters || [];
    def.table.sort = def.table.sort || [];
    def.parameters = def.parameters || [];

    let columns = []; // [{name, dataType}] of the selected table

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
    function select(options, current, cls) {
        const s = el('select', { class: 'form-select form-select-sm ' + (cls || '') });
        options.forEach(([value, label]) => {
            const o = el('option', { value: value, text: label });
            if (value === current) o.selected = true;
            s.appendChild(o);
        });
        return s;
    }
    function removeButton(onClick) {
        return el('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', text: '×', title: 'Hapus', onclick: onClick });
    }
    const columnOptions = () => [['', '- kolom -']].concat(columns.map((c) => [c.name, c.name]));

    // ---- mode / panels ----
    const currentMode = () => (form.querySelector('input[name="Mode"]:checked') || {}).value || 'Table';
    function applyMode() {
        const table = currentMode() === 'Table';
        $('panel-table').classList.toggle('d-none', !table);
        $('panel-query').classList.toggle('d-none', table);
    }
    form.querySelectorAll('input[name="Mode"]').forEach((r) => r.addEventListener('change', applyMode));

    // ---- simple fields ----
    $('timeoutSeconds').value = def.timeoutSeconds || 30;
    $('query').value = def.query || '';

    // ---- table + columns ----
    const tableSelect = $('tableSelect');

    function selectedTable() {
        const o = tableSelect.selectedOptions[0];
        return o && o.dataset.schema ? { schema: o.dataset.schema, name: o.dataset.name } : null;
    }

    async function loadColumns(preserve) {
        const t = selectedTable();
        columns = [];
        if (!t) { renderColumns(); renderFilters(); renderSorts(); return; }
        try {
            const url = '/Builder/DataSource/Columns?schemaName=' + encodeURIComponent(t.schema) + '&name=' + encodeURIComponent(t.name);
            const res = await fetch(url, { headers: { Accept: 'application/json' } });
            if (!res.ok) throw new Error(String(res.status));
            columns = await res.json();
        } catch (e) {
            $('columns').replaceChildren(el('span', { class: 'text-danger', text: 'Kolom tidak dapat dimuat.' }));
            return;
        }
        if (!preserve) { def.table.columns = []; def.table.filters = []; def.table.sort = []; }
        renderColumns(); renderFilters(); renderSorts();
    }

    function renderColumns() {
        const host = $('columns');
        if (columns.length === 0) { host.replaceChildren(el('span', { class: 'text-muted', text: 'Pilih tabel dulu.' })); return; }
        const chosen = def.table.columns.length === 0 ? columns.map((c) => c.name.toLowerCase()) : def.table.columns.map((c) => c.toLowerCase());
        host.replaceChildren(...columns.map((c, i) => {
            const id = 'col-' + i;
            const cb = el('input', { type: 'checkbox', class: 'form-check-input', id: id, value: c.name });
            cb.checked = chosen.includes(c.name.toLowerCase());
            cb.addEventListener('change', () => { syncColumns(); });
            return el('div', { class: 'form-check' }, [cb, el('label', { class: 'form-check-label', for: id, text: c.name + ' (' + c.dataType + ')' })]);
        }));
    }
    function syncColumns() {
        const boxes = Array.from($('columns').querySelectorAll('input[type=checkbox]'));
        const checked = boxes.filter((b) => b.checked).map((b) => b.value);
        def.table.columns = checked.length === boxes.length ? [] : checked; // all = follow the table
    }
    tableSelect.addEventListener('change', () => loadColumns(false));

    // ---- filters ----
    function renderFilters() {
        const host = $('filters');
        host.replaceChildren(...def.table.filters.map((f, i) => {
            const col = select(columnOptions(), f.column, 'w-auto');
            const op = select(OPERATORS.map((o) => [o, o]), f.operator || '=', 'w-auto');
            const param = el('input', { class: 'form-control form-control-sm', placeholder: 'nama parameter', value: f.parameter || '' });
            col.addEventListener('change', () => { f.column = col.value; });
            op.addEventListener('change', () => { f.operator = op.value; });
            param.addEventListener('input', () => { f.parameter = param.value.trim(); });
            return el('div', { class: 'd-flex gap-2 mb-2' }, [col, op, param, removeButton(() => { def.table.filters.splice(i, 1); renderFilters(); })]);
        }));
    }
    $('add-filter').addEventListener('click', () => { def.table.filters.push({ column: '', operator: '=', parameter: '' }); renderFilters(); });

    // ---- sort ----
    function renderSorts() {
        const host = $('sorts');
        host.replaceChildren(...def.table.sort.map((s, i) => {
            const col = select(columnOptions(), s.column, 'w-auto');
            const desc = el('input', { type: 'checkbox', class: 'form-check-input', id: 'desc-' + i });
            desc.checked = !!s.descending;
            col.addEventListener('change', () => { s.column = col.value; });
            desc.addEventListener('change', () => { s.descending = desc.checked; });
            return el('div', { class: 'd-flex gap-2 align-items-center mb-2' }, [
                col,
                el('div', { class: 'form-check mb-0' }, [desc, el('label', { class: 'form-check-label', for: 'desc-' + i, text: 'menurun' })]),
                removeButton(() => { def.table.sort.splice(i, 1); renderSorts(); }),
            ]);
        }));
        $('sort-warning').classList.toggle('d-none', def.table.sort.length > 0);
    }
    $('add-sort').addEventListener('click', () => { def.table.sort.push({ column: '', descending: false }); renderSorts(); });

    // ---- parameters ----
    function renderParams() {
        const host = $('params');
        host.replaceChildren(...def.parameters.map((p, i) => {
            const name = el('input', { class: 'form-control form-control-sm', placeholder: 'nama', value: p.name || '' });
            const src = select(SOURCES, p.source || 'constant', 'w-auto');
            const key = el('input', { class: 'form-control form-control-sm', placeholder: 'key', value: p.key || '' });
            const val = el('input', { class: 'form-control form-control-sm', placeholder: 'value', value: p.value || '' });
            name.addEventListener('input', () => { p.name = name.value.trim(); });
            src.addEventListener('change', () => { p.source = src.value; });
            key.addEventListener('input', () => { p.key = key.value.trim(); });
            val.addEventListener('input', () => { p.value = val.value; });
            return el('div', { class: 'd-flex gap-2 mb-2' }, [name, src, key, val, removeButton(() => { def.parameters.splice(i, 1); renderParams(); })]);
        }));
    }
    $('add-param').addEventListener('click', () => { def.parameters.push({ name: '', source: 'queryString', key: '', value: '' }); renderParams(); });

    // Add a parameter row for every {name} in the query (and every filter parameter) that has none yet.
    $('detect-params').addEventListener('click', () => {
        const known = new Set(def.parameters.map((p) => (p.name || '').toLowerCase()));
        const found = new Set();
        const re = /\{([A-Za-z_][A-Za-z0-9_]*)\}/g;
        let m;
        while ((m = re.exec($('query').value)) !== null) found.add(m[1]);
        found.forEach((n) => { if (!known.has(n.toLowerCase())) def.parameters.push({ name: n, source: 'queryString', key: n, value: '' }); });
        renderParams();
    });

    // ---- submit ----
    form.addEventListener('submit', () => {
        const table = currentMode() === 'Table';
        const out = { timeoutSeconds: parseInt($('timeoutSeconds').value, 10) || 30, parameters: [] };

        if (table) {
            syncColumns();
            const t = selectedTable();
            out.table = {
                schema: t ? t.schema : '', name: t ? t.name : '',
                columns: def.table.columns,
                filters: def.table.filters.filter((f) => f.column),
                sort: def.table.sort.filter((s) => s.column),
            };
        } else {
            out.query = $('query').value;
        }

        out.parameters = def.parameters.filter((p) => p.name).map((p) => {
            const row = { name: p.name, source: p.source || 'constant' };
            if (p.key) row.key = p.key;
            if (p.value) row.value = p.value;
            return row;
        });
        definitionInput.value = JSON.stringify(out);
    });

    // ---- initial state ----
    if (def.table && def.table.schema) {
        const wanted = (def.table.schema + '.' + def.table.name).toLowerCase();
        Array.from(tableSelect.options).forEach((o) => { if (o.value.toLowerCase() === wanted) o.selected = true; });
    }
    applyMode();
    renderParams();
    renderSorts();
    loadColumns(true);
})();

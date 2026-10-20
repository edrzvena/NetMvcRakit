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
    def.procedure = def.procedure || { schema: '', name: '' };

    let columns = []; // [{name, dataType}] of the selected table
    let procParams = null; // [{name, dataType, isOutput, hasDefault, isPaging}] of the selected procedure

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
        const mode = currentMode();
        $('panel-table').classList.toggle('d-none', mode !== 'Table');
        $('panel-query').classList.toggle('d-none', mode !== 'Query');
        $('panel-sp').classList.toggle('d-none', mode !== 'StoredProcedure');
        $('panel-statement').classList.toggle('d-none', mode === 'Table');
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

    // ---- stored procedure ----
    const procSelect = $('procSelect');

    function selectedProc() {
        const o = procSelect.selectedOptions[0];
        return o && o.dataset.schema ? { schema: o.dataset.schema, name: o.dataset.name } : null;
    }

    const isMapped = (name) => def.parameters.some((p) => (p.name || '').toLowerCase() === name.toLowerCase());

    function renderProcInfo() {
        const host = $('sp-info');
        if (!selectedProc()) { host.replaceChildren(el('span', { class: 'text-muted', text: 'Pilih prosedur dulu.' })); return; }
        if (!procParams) return; // still loading, or failed (message already shown)

        const inputs = procParams.filter((p) => !p.isOutput && !p.isPaging);
        const required = inputs.filter((p) => !p.hasDefault);
        const optional = inputs.filter((p) => p.hasDefault);
        const outputs = procParams.filter((p) => p.isOutput);
        const paging = procParams.filter((p) => p.isPaging && !p.isOutput);
        const lines = [];
        const label = (p) => '@' + p.name + ' (' + p.dataType + ')';

        lines.push(el('div', { class: 'small mb-1', text: required.length === 0
            ? 'Parameter wajib: tidak ada.'
            : 'Parameter wajib (sudah dibuatkan baris mapping): ' + required.map(label).join(', ') }));

        if (optional.length > 0) {
            const row = el('div', { class: 'small mb-1 d-flex flex-wrap gap-1 align-items-center' }, [el('span', { text: 'Parameter opsional (punya default), klik untuk di-map:' })]);
            optional.forEach((p) => {
                const mapped = isMapped(p.name);
                row.appendChild(el('button', {
                    type: 'button', class: 'btn btn-sm ' + (mapped ? 'btn-light text-muted' : 'btn-outline-secondary'),
                    text: (mapped ? '✓ ' : '+ ') + label(p), disabled: mapped,
                    onclick: () => { def.parameters.push({ name: p.name, source: 'queryString', key: p.name, value: '' }); renderParams(); renderProcInfo(); },
                }));
            });
            lines.push(row);
        }
        if (paging.length > 0) lines.push(el('div', { class: 'small mb-1 text-muted', text: 'Diisi otomatis dari request: ' + paging.map((p) => '@' + p.name).join(', ') }));
        if (outputs.length > 0) lines.push(el('div', { class: 'small mb-1 text-muted', text: 'OUTPUT (dibaca setelah dijalankan): ' + outputs.map(label).join(', ') }));
        host.replaceChildren(...lines);
    }

    async function loadProcParams(autoAdd) {
        const p = selectedProc();
        procParams = null;
        renderProcInfo();
        if (!p) return;
        try {
            const url = '/Builder/DataSource/ProcedureParameters?schemaName=' + encodeURIComponent(p.schema) + '&name=' + encodeURIComponent(p.name);
            const res = await fetch(url, { headers: { Accept: 'application/json' } });
            if (!res.ok) throw new Error(String(res.status));
            procParams = await res.json();
        } catch (e) {
            $('sp-info').replaceChildren(el('span', { class: 'text-danger', text: 'Parameter prosedur tidak dapat dimuat (tidak ada di whitelist, atau database tidak tersedia).' }));
            return;
        }
        if (autoAdd) {
            procParams.filter((x) => !x.isOutput && !x.isPaging && !x.hasDefault && !isMapped(x.name))
                .forEach((x) => def.parameters.push({ name: x.name, source: 'queryString', key: x.name, value: '' }));
            renderParams();
        }
        renderProcInfo();
    }
    procSelect.addEventListener('change', () => { $('sp-warning').classList.add('d-none'); loadProcParams(true); });

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
            name.addEventListener('change', () => { renderSamples(); renderProcInfo(); });
            src.addEventListener('change', () => { p.source = src.value; });
            key.addEventListener('input', () => { p.key = key.value.trim(); });
            val.addEventListener('input', () => { p.value = val.value; });
            return el('div', { class: 'd-flex gap-2 mb-2' }, [name, src, key, val, removeButton(() => { def.parameters.splice(i, 1); renderParams(); renderProcInfo(); })]);
        }));
        renderSamples();
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

    // ---- definition (shared by Save and Preview) ----
    function collectDefinition() {
        const mode = currentMode();
        const table = mode === 'Table';
        const out = { timeoutSeconds: parseInt($('timeoutSeconds').value, 10) || 30, parameters: [] };

        if (mode === 'StoredProcedure') {
            const sp = selectedProc();
            out.procedure = { schema: sp ? sp.schema : '', name: sp ? sp.name : '' };
            out.listResultSet = parseInt($('listResultSet').value, 10) || 0;
            const count = parseInt($('countResultSet').value, 10);
            if (!isNaN(count)) out.countResultSet = count;
        } else if (table) {
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
        return out;
    }

    form.addEventListener('submit', () => {
        definitionInput.value = JSON.stringify(collectDefinition());
    });

    // ---- preview ----
    const sampleValues = {}; // parameter name -> text typed by the builder (kept while the form is open)

    function renderSamples() {
        const host = $('preview-samples');
        const names = def.parameters.map((p) => p.name).filter(Boolean);
        if (names.length === 0) {
            host.replaceChildren(el('div', { class: 'text-muted small', text: 'Tidak ada parameter: preview jalan tanpa nilai contoh.' }));
            return;
        }
        host.replaceChildren(
            el('div', { class: 'small text-muted mb-1', text: 'Nilai contoh (kosong = null; filter dengan nilai null dilewati):' }),
            ...names.map((n) => {
                const input = el('input', { class: 'form-control form-control-sm', value: sampleValues[n] || '' });
                input.addEventListener('input', () => { sampleValues[n] = input.value; });
                return el('div', { class: 'row g-2 mb-1 align-items-center' }, [
                    el('div', { class: 'col-auto' }, [el('code', { text: n })]),
                    el('div', { class: 'col-md-4' }, [input]),
                ]);
            }));
    }

    function showMessage(kind, text, extra) {
        const box = el('div', { class: 'alert alert-' + kind + ' mb-0', role: 'alert' }, [el('div', { text: text })]);
        (extra || []).forEach((line) => box.appendChild(el('div', { class: 'small', text: line })));
        $('preview-result').replaceChildren(box);
    }

    function renderResult(data) {
        const parts = [];
        const summary = data.resultSets.map((s) => s.rows.length + (s.truncated ? '+' : '') + ' baris').join(', ');
        parts.push(el('div', { class: 'small text-muted mb-2', text:
            (summary || 'tidak ada result set') +
            (data.totalCount != null ? ' | total ' + data.totalCount : '') +
            ' | ' + data.durationMs + ' ms' }));

        const countIndex = parseInt($('countResultSet').value, 10);
        if (data.isProcedure && data.listResultSet >= data.resultSets.length) {
            parts.push(el('div', { class: 'alert alert-warning py-2', role: 'alert', text:
                'Result set list (#' + data.listResultSet + ') tidak terbaca: prosedur hanya mengembalikan ' + data.resultSets.length +
                ' result set, atau pembacaan berhenti di result set sebelumnya karena melewati batas 50 baris.' }));
        }

        data.resultSets.forEach((s, i) => {
            if (data.isProcedure) {
                const tags = (i === data.listResultSet ? ' (list)' : '') + (i === countIndex ? ' (count)' : '');
                parts.push(el('div', { class: 'small fw-semibold mb-1', text: 'Result set #' + i + tags }));
            }
            const head = el('tr', {}, s.columns.map((c) => el('th', { text: c })));
            const body = s.rows.map((r) => el('tr', {}, r.map((v) =>
                v === null ? el('td', {}, [el('em', { class: 'text-muted', text: 'NULL' })]) : el('td', { text: v }))));
            parts.push(el('div', { class: 'table-responsive border mb-2', style: 'max-height: 360px; overflow: auto' }, [
                el('table', { class: 'table table-sm table-bordered table-striped mb-0' }, [el('thead', {}, [head]), el('tbody', {}, body)]),
            ]));
            if (s.truncated) parts.push(el('div', { class: 'small text-warning', text: 'Hasil dipotong: hanya sebagian baris yang ditampilkan.' }));
            if (s.rows.length === 0) parts.push(el('div', { class: 'small text-muted', text: 'Tidak ada baris.' }));
        });

        if (data.isProcedure) {
            const names = Object.keys(data.outputParameters || {});
            const lines = [el('div', { class: 'small', text: 'Return value: ' + (data.returnValue == null ? '(tidak tersedia)' : data.returnValue) })];
            names.forEach((n) => {
                const v = data.outputParameters[n];
                lines.push(el('div', { class: 'small' }, [
                    el('span', { text: 'OUTPUT ' }), el('code', { text: '@' + n }), el('span', { text: ' = ' }),
                    v === null ? el('em', { class: 'text-muted', text: 'NULL' }) : el('span', { text: v })]));
            });
            parts.push(el('div', { class: 'border rounded p-2' }, lines));
        }
        $('preview-result').replaceChildren(...parts);
    }

    async function runPreview() {
        const button = $('run-preview');
        const statement = currentMode() === 'Table' ? 'Query' : $('statementType').value;
        const tokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
        button.disabled = true;
        $('preview-status').textContent = 'menjalankan...';
        try {
            const res = await fetch('/Builder/DataSource/Preview', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    Accept: 'application/json',
                    RequestVerificationToken: tokenInput ? tokenInput.value : '',
                },
                body: JSON.stringify({
                    mode: currentMode(),
                    statementType: statement,
                    definition: collectDefinition(),
                    samples: sampleValues,
                }),
            });

            let data = null;
            try { data = await res.json(); } catch (e) { /* non-JSON body (e.g. 400 from anti-forgery) */ }

            if (res.ok && data) { renderResult(data); return; }
            if (!data) { showMessage('danger', 'Preview gagal (HTTP ' + res.status + '). Muat ulang halaman lalu coba lagi.'); return; }

            const lines = [].concat(data.errors || []);
            if (data.detail) lines.push('Detail database: ' + data.detail);
            showMessage(res.status === 403 ? 'info' : 'danger', data.message || 'Preview gagal.', lines);
        } catch (e) {
            showMessage('danger', 'Preview gagal: server tidak dapat dihubungi.');
        } finally {
            button.disabled = false;
            $('preview-status').textContent = '';
        }
    }
    $('run-preview').addEventListener('click', runPreview);

    // ---- initial state ----
    if (def.table && def.table.schema) {
        const wanted = (def.table.schema + '.' + def.table.name).toLowerCase();
        Array.from(tableSelect.options).forEach((o) => { if (o.value.toLowerCase() === wanted) o.selected = true; });
    }
    if (def.procedure && def.procedure.name) {
        const wanted = (def.procedure.schema + '.' + def.procedure.name).toLowerCase();
        let found = false;
        Array.from(procSelect.options).forEach((o) => { if (o.value.toLowerCase() === wanted) { o.selected = true; found = true; } });
        if (!found) {
            // Saved procedure is no longer offered (not whitelisted / inactive): keep it visible, saving will be refused by the server.
            const o = el('option', { value: def.procedure.schema + '.' + def.procedure.name, text: def.procedure.schema + '.' + def.procedure.name + ' (tidak di-whitelist)' });
            o.dataset.schema = def.procedure.schema;
            o.dataset.name = def.procedure.name;
            o.selected = true;
            procSelect.appendChild(o);
            $('sp-warning').classList.remove('d-none');
        }
    }
    $('listResultSet').value = def.listResultSet || 0;
    $('countResultSet').value = def.countResultSet == null ? '' : def.countResultSet;

    applyMode();
    renderParams();
    renderSorts();
    loadColumns(true);
    if (currentMode() === 'StoredProcedure') loadProcParams(false);
})();

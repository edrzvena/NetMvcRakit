// Module form: the Display tab (page size, key field, columns), the Form tab (record fields) and the Widget and Chart tabs live in the
// browser and are serialised into the hidden ConfigJson field on submit, in the same shape the server stores. Every data source
// (list and record actions) is a normal select: the server takes them from there.
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

    config.form = config.form || {};
    config.form.fields = config.form.fields || [];
    const fields = config.form.fields; // edited in place: [{name, label, type, required, maxLength, regex, default, readonly}]
    const FIELD_TYPES = [['text', 'Text'], ['textarea', 'Textarea'], ['number', 'Number'], ['date', 'Date'], ['dateTime', 'DateTime'], ['boolean', 'Boolean'],
        ['select', 'Select (dropdown lookup)'], ['browse', 'Browse (modal cari)']];
    const isLookupType = (t) => t === 'select' || t === 'browse';
    const isTextType = (t) => t === 'text' || t === 'textarea' || isLookupType(t); // MaxLength / Regex apply (a lookup value is stored as text)

    config.widget = config.widget || {};
    const widget = config.widget; // Widget module settings, read back from the inputs by collectWidget()

    config.chart = config.chart || {};
    const chart = config.chart; // Chart module settings; the series list is edited in place, the rest is read by collectChart()
    chart.series = chart.series || [];
    const chartSeries = chart.series; // [{valueField, label, color}] (color unset = the default palette)

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
    $('keyField').value = config.display.keyField || '';

    // ---- columns editor ----
    function refreshKeyColumns() {
        const names = new Set(columns.map((c) => (c.field || '').trim()).filter(Boolean));
        if (known) known.forEach((n) => names.add(n));
        $('key-columns').replaceChildren(...[...names].map((n) => el('option', { value: n })));
    }
    function renderColumns() {
        refreshKeyColumns();
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

    // ---- module type: what is shown depends on the Tipe (elements carry data-types="table form") ----
    const typeInput = $('Type');
    const currentType = () => (typeInput.value || 'Table').toLowerCase();
    function applyType() {
        const type = currentType();
        document.querySelectorAll('[data-types]').forEach((node) => {
            node.classList.toggle('d-none', !node.dataset.types.toLowerCase().split(/\s+/).includes(type));
        });
        $('DataSourceId').required = type === 'table' || type === 'widget' || type === 'chart'; // a hidden required select would block the submit
        const title = $('record-sources-title');
        if (title) title.textContent = type === 'form' ? title.dataset.titleForm : title.dataset.titleTable;
        const listTitle = $('list-source-title');
        if (listTitle) listTitle.textContent = listTitle.dataset['title' + type.charAt(0).toUpperCase() + type.slice(1)] || listTitle.dataset.titleTable;
        // the preview tab is shared by Widget and Chart: a card is narrow, a chart takes the whole width
        $('visual-preview-host').className = 'mt-2' + (type === 'widget' ? ' col-sm-8 col-md-5 col-lg-4' : '');
        const active = document.querySelector('.nav-tabs .nav-link.active');
        if (active && active.closest('li').classList.contains('d-none')) $('tab-source').click(); // never stay on a tab that just disappeared
    }
    typeInput.addEventListener('change', applyType);

    // ---- Page / Html: character counter, "Sisipkan module" and the sanitized preview ----
    const pageHtml = $('PageHtml');
    if (pageHtml) {
        const count = () => { $('page-html-count').textContent = String(pageHtml.value.length); };
        pageHtml.addEventListener('input', count);
        count();

        $('embed-insert').addEventListener('click', () => {
            const id = $('embed-module').value;
            if (!id) return;
            const marker = '\n{{module:' + id + '}}\n';
            const at = pageHtml.selectionEnd != null ? pageHtml.selectionEnd : pageHtml.value.length;
            pageHtml.value = pageHtml.value.slice(0, at) + marker + pageHtml.value.slice(at);
            pageHtml.focus();
            pageHtml.selectionStart = pageHtml.selectionEnd = at + marker.length;
            count();
        });

        $('page-preview-run').addEventListener('click', async () => {
            const status = $('page-preview-status');
            const frame = $('page-preview-frame');
            status.textContent = 'menjalankan...';
            const res = await postJson('/Builder/Module/PreviewHtml', { html: pageHtml.value });
            if (!res.ok || !res.data) {
                frame.classList.add('d-none');
                status.replaceChildren(errorBox(res, 'Preview gagal.'));
                return;
            }
            status.textContent = res.data.changed ? 'Sebagian HTML dibuang oleh sanitasi (yang tersimpan nanti adalah hasil ini).' : 'HTML tidak berubah oleh sanitasi.';
            frame.srcdoc = '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css"></head><body class="p-3">'
                + res.data.html + '</body></html>';
            frame.classList.remove('d-none');
        });
    }

    // ---- form fields editor ----
    function renderFields() {
        const host = $('fields');
        if (fields.length === 0) {
            host.replaceChildren(el('div', { class: 'text-muted small mb-2', text: 'Belum ada field: aksi Tambah dan Ubah membutuhkan setidaknya satu.' }));
            return;
        }
        const rows = fields.map((f, i) => {
            const name = el('input', { class: 'form-control form-control-sm', value: f.name || '', placeholder: 'nama (huruf/angka/_)', maxlength: '128' });
            const label = el('input', { class: 'form-control form-control-sm', value: f.label || '', placeholder: 'label (kosong = nama)' });
            const type = el('select', { class: 'form-select form-select-sm' });
            FIELD_TYPES.forEach(([v, t]) => { const o = el('option', { value: v, text: t }); if (v === (f.type || 'text')) o.selected = true; type.appendChild(o); });
            const required = el('input', { type: 'checkbox', class: 'form-check-input', id: 'req-' + i });
            const readonly = el('input', { type: 'checkbox', class: 'form-check-input', id: 'ro-' + i });
            required.checked = !!f.required;
            readonly.checked = !!f.readonly;
            const maxLength = el('input', { class: 'form-control form-control-sm', type: 'number', min: '1', max: '4000', placeholder: 'maks panjang' });
            maxLength.value = f.maxLength || '';
            const regex = el('input', { class: 'form-control form-control-sm', value: f.regex || '', placeholder: 'regex (seluruh nilai)', maxlength: '500' });
            const def = el('input', { class: 'form-control form-control-sm', value: f.default || '', placeholder: 'default' });

            // lookup (select / browse): where the choices come from
            const lookup = f.lookup = f.lookup || { dataSourceId: '', valueField: '', textField: '' };
            const lkSource = el('select', { class: 'form-select form-select-sm', 'aria-label': 'Data source lookup' });
            lkSource.appendChild(el('option', { value: '', text: '- data source lookup (Query) -' }));
            [...$('DataSourceId').options].filter((o) => o.value).forEach((o) => {
                const opt = el('option', { value: o.value, text: o.textContent.trim() });
                if (o.value === lookup.dataSourceId) opt.selected = true;
                lkSource.appendChild(opt);
            });
            if (lookup.dataSourceId && ![...lkSource.options].some((o) => o.value === lookup.dataSourceId)) {
                const missing = el('option', { value: lookup.dataSourceId, text: '(tidak ditemukan atau bukan Query) ' + lookup.dataSourceId });
                missing.selected = true;
                lkSource.appendChild(missing);
            }
            const colsId = 'lk-cols-' + i;
            const lkValue = el('input', { class: 'form-control form-control-sm', value: lookup.valueField || '', placeholder: 'kolom nilai (disimpan)', list: colsId, maxlength: '128' });
            const lkText = el('input', { class: 'form-control form-control-sm', value: lookup.textField || '', placeholder: 'kolom teks (dibaca user)', list: colsId, maxlength: '128' });
            const lkCols = el('datalist', { id: colsId });
            const lkStatus = el('span', { class: 'small text-muted ms-1', 'aria-live': 'polite' });
            const lkLoad = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: 'Muat kolom' });
            lkSource.addEventListener('change', () => { lookup.dataSourceId = lkSource.value; lkCols.replaceChildren(); lkStatus.textContent = ''; });
            lkValue.addEventListener('input', () => { lookup.valueField = lkValue.value.trim(); });
            lkText.addEventListener('input', () => { lookup.textField = lkText.value.trim(); });
            lkLoad.addEventListener('click', async () => {
                if (!lkSource.value) { lkStatus.textContent = 'Pilih data source dulu.'; return; }
                lkStatus.textContent = 'memuat...';
                try {
                    const res = await postJson('/Builder/Module/LoadColumns', { dataSourceId: lkSource.value });
                    if (!res.ok) { lkStatus.textContent = ''; lkRow.appendChild(el('div', { class: 'col-12' }, [errorBox(res, 'Kolom lookup tidak dapat dimuat.')])); return; }
                    const names = res.data.columns.map((c) => c.name);
                    lkCols.replaceChildren(...names.map((n) => el('option', { value: n })));
                    // first column = value, second (or the same one) = text, only where nothing is filled in yet
                    if (!lookup.valueField && names.length > 0) { lookup.valueField = names[0]; lkValue.value = names[0]; }
                    if (!lookup.textField && names.length > 0) { lookup.textField = names[1] || names[0]; lkText.value = lookup.textField; }
                    lkStatus.textContent = names.length + ' kolom dimuat.';
                } catch (e) {
                    lkStatus.textContent = 'Server tidak dapat dihubungi.';
                }
            });
            const lkRow = el('div', { class: 'row g-2 align-items-center mt-1' }, [
                el('div', { class: 'col-md-4' }, [lkSource]), el('div', { class: 'col-md-3' }, [lkValue]), el('div', { class: 'col-md-3' }, [lkText, lkCols]),
                el('div', { class: 'col-md-2 text-nowrap' }, [lkLoad, lkStatus]),
            ]);

            const syncType = () => {
                const t = f.type || 'text';
                const text = isTextType(t);
                maxLength.disabled = !text;
                regex.disabled = !text;
                lkRow.classList.toggle('d-none', !isLookupType(t));
            };
            name.addEventListener('input', () => { f.name = name.value.trim(); });
            label.addEventListener('input', () => { f.label = label.value; });
            type.addEventListener('change', () => { f.type = type.value; syncType(); });
            required.addEventListener('change', () => { f.required = required.checked; });
            readonly.addEventListener('change', () => { f.readonly = readonly.checked; });
            maxLength.addEventListener('input', () => { f.maxLength = parseInt(maxLength.value, 10) || null; });
            regex.addEventListener('input', () => { f.regex = regex.value; });
            def.addEventListener('input', () => { f.default = def.value; });
            syncType();

            const move = (d) => () => { const j = i + d; if (j < 0 || j >= fields.length) return; [fields[i], fields[j]] = [fields[j], fields[i]]; renderFields(); };
            const check = (box, text) => el('div', { class: 'form-check form-check-inline' }, [box, el('label', { class: 'form-check-label small', for: box.id, text: text })]);
            return el('div', { class: 'border rounded p-2 mb-2' }, [
                el('div', { class: 'row g-2 mb-1 align-items-center' }, [
                    el('div', { class: 'col-md-3' }, [name]),
                    el('div', { class: 'col-md-3' }, [label]),
                    el('div', { class: 'col-md-2' }, [type]),
                    el('div', { class: 'col-md-2 text-nowrap' }, [check(required, 'wajib'), check(readonly, 'readonly')]),
                    el('div', { class: 'col-md-2 text-nowrap text-end' }, [
                        iconButton('↑', 'Naik', move(-1), i === 0), iconButton('↓', 'Turun', move(1), i === fields.length - 1),
                        iconButton('×', 'Hapus', () => { fields.splice(i, 1); renderFields(); }),
                    ]),
                ]),
                el('div', { class: 'row g-2 align-items-center' }, [
                    el('div', { class: 'col-md-2' }, [maxLength]), el('div', { class: 'col-md-6' }, [regex]), el('div', { class: 'col-md-4' }, [def]),
                ]),
                lkRow,
            ]);
        });
        host.replaceChildren(...rows);
    }
    $('add-field').addEventListener('click', () => { fields.push({ name: '', label: '', type: 'text', required: false, readonly: false }); renderFields(); });

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
    $('DataSourceId').addEventListener('change', () => {
        known = null;
        renderColumns();
        $('widget-columns').replaceChildren();
        $('chart-columns').replaceChildren();
        refreshWidgetWarnings();
        refreshChartWarnings();
    });

    // Loads the columns of the chosen data source into <datalist> `list` and remembers them in `known`; null when it failed.
    async function fetchColumns(status, list) {
        const id = dataSourceId();
        if (!id) { status.textContent = 'Pilih data source dulu (tab Source).'; return null; }
        status.textContent = 'memuat...';
        try {
            const res = await postJson('/Builder/Module/LoadColumns', { dataSourceId: id });
            if (!res.ok) { status.replaceChildren(errorBox(res, 'Kolom tidak dapat dimuat.')); return null; }
            known = new Set(res.data.columns.map((c) => c.name.toLowerCase()));
            list.replaceChildren(...res.data.columns.map((c) => el('option', { value: c.name })));
            return res.data;
        } catch (e) {
            status.textContent = 'Server tidak dapat dihubungi.';
            return null;
        }
    }
    const loadedNote = (data) => data.columns.length + ' kolom dimuat' + (data.hasRows ? '' : ' (data source tidak mengembalikan baris: format ditebak "text")') + '.';

    // ---- Widget tab ----
    const widgetInput = (name) => $('widget-' + name);
    widgetInput('valueField').value = widget.valueField || '';
    widgetInput('label').value = widget.label || '';
    widgetInput('labelField').value = widget.labelField || '';
    widgetInput('format').value = widget.format || 'number';
    widgetInput('decimals').value = widget.decimals || 0;
    widgetInput('prefix').value = widget.prefix || '';
    widgetInput('suffix').value = widget.suffix || '';
    widgetInput('color').value = widget.color || 'primary';
    widgetInput('refreshSeconds').value = widget.refreshSeconds || 0;

    // A column name that the loaded data source does not return would only show an empty card, so say so (like the Display tab).
    function refreshWidgetWarnings() {
        ['valueField', 'labelField'].forEach((name) => {
            const value = widgetInput(name).value.trim();
            $('widget-' + name + '-warn').classList.toggle('d-none', !(known !== null && value && !known.has(value.toLowerCase())));
        });
    }
    ['valueField', 'labelField'].forEach((name) => widgetInput(name).addEventListener('input', refreshWidgetWarnings));
    refreshWidgetWarnings();

    $('widget-load-columns').addEventListener('click', async () => {
        const status = $('widget-columns-status');
        const data = await fetchColumns(status, $('widget-columns'));
        if (!data) return;
        // Nothing chosen yet: the first number column (else the first column) becomes the value, and its type the format.
        if (!widgetInput('valueField').value.trim() && data.columns.length > 0) {
            const pick = data.columns.find((c) => c.suggestedFormat === 'number') || data.columns[0];
            widgetInput('valueField').value = pick.name;
            widgetInput('format').value = pick.suggestedFormat || 'text';
        }
        refreshWidgetWarnings();
        status.textContent = loadedNote(data);
    });

    function collectWidget() {
        const out = {
            valueField: widgetInput('valueField').value.trim(),
            format: widgetInput('format').value,
            decimals: parseInt(widgetInput('decimals').value, 10) || 0,
            color: widgetInput('color').value,
            refreshSeconds: parseInt(widgetInput('refreshSeconds').value, 10) || 0,
        };
        const label = widgetInput('label').value.trim();
        const labelField = widgetInput('labelField').value.trim();
        if (label) out.label = label;
        if (labelField) out.labelField = labelField;
        // prefix / suffix are kept as typed: a trailing space ("Rp ") is part of them
        const prefix = widgetInput('prefix').value;
        const suffix = widgetInput('suffix').value;
        if (prefix) out.prefix = prefix;
        if (suffix) out.suffix = suffix;
        return out;
    }

    // ---- Chart tab ----
    const chartInput = (name) => $('chart-' + name);
    const isRound = () => chartInput('kind').value === 'pie' || chartInput('kind').value === 'doughnut';
    const MAX_SERIES = 8;
    const PALETTE = (window.RakitVisual && window.RakitVisual.palette) || [];
    chartInput('kind').value = chart.kind || 'bar';
    chartInput('labelField').value = chart.labelField || '';
    chartInput('title').value = chart.title || '';
    chartInput('maxRows').value = chart.maxRows || 100;
    chartInput('stacked').checked = !!chart.stacked;
    chartInput('horizontal').checked = !!chart.horizontal;
    chartInput('legend').checked = chart.legend !== false;

    function refreshChartWarnings() {
        const missing = (value) => known !== null && value && !known.has(value.trim().toLowerCase());
        $('chart-labelField-warn').classList.toggle('d-none', !missing(chartInput('labelField').value));
        document.querySelectorAll('#chart-series [data-series-warn]').forEach((node) => {
            const s = chartSeries[Number(node.dataset.seriesWarn)];
            node.classList.toggle('d-none', !(s && missing(s.valueField || '')));
        });
    }
    chartInput('labelField').addEventListener('input', refreshChartWarnings);

    // Which options make sense depends on the kind: a pie takes one series and neither stacking nor horizontal bars.
    function syncChartKind() {
        const kind = chartInput('kind').value;
        const round = isRound();
        chartInput('stacked').disabled = round;
        chartInput('horizontal').disabled = kind !== 'bar';
        if (round) chartInput('stacked').checked = false;
        if (kind !== 'bar') chartInput('horizontal').checked = false;
        $('chart-round-hint').classList.toggle('d-none', !round);
        $('chart-round-hint').classList.toggle('text-danger', round && chartSeries.length > 1);
        $('chart-add-series').disabled = chartSeries.length >= (round ? 1 : MAX_SERIES);
    }
    chartInput('kind').addEventListener('change', syncChartKind);

    function renderChartSeries() {
        const host = $('chart-series');
        if (chartSeries.length === 0) {
            host.replaceChildren(el('div', { class: 'text-muted small mb-2', text: 'Belum ada seri: tambahkan setidaknya satu (atau "Muat kolom").' }));
            syncChartKind();
            return;
        }
        const rows = chartSeries.map((s, i) => {
            const field = el('input', { class: 'form-control form-control-sm', value: s.valueField || '', list: 'chart-columns', maxlength: '128', placeholder: 'kolom angka', autocomplete: 'off', 'aria-label': 'Kolom seri ' + (i + 1) });
            const label = el('input', { class: 'form-control form-control-sm', value: s.label || '', maxlength: '100', placeholder: 'label legenda (kosong = nama kolom)', 'aria-label': 'Label seri ' + (i + 1) });
            const own = el('input', { type: 'checkbox', class: 'form-check-input', id: 'series-own-' + i });
            own.checked = !!s.color;
            const color = el('input', { type: 'color', class: 'form-control form-control-color form-control-sm', 'aria-label': 'Warna seri ' + (i + 1) });
            color.value = s.color || PALETTE[i % Math.max(PALETTE.length, 1)] || '#2a78d6';
            color.disabled = !own.checked;
            const warn = el('div', { class: 'col-12 small text-warning d-none', 'data-series-warn': String(i), text: 'Kolom ini tidak ada di hasil data source.' });

            field.addEventListener('input', () => { s.valueField = field.value.trim(); refreshChartWarnings(); });
            label.addEventListener('input', () => { s.label = label.value; });
            own.addEventListener('change', () => { color.disabled = !own.checked; s.color = own.checked ? color.value : ''; });
            color.addEventListener('input', () => { s.color = color.value; });
            const move = (d) => () => { const j = i + d; if (j < 0 || j >= chartSeries.length) return; [chartSeries[i], chartSeries[j]] = [chartSeries[j], chartSeries[i]]; renderChartSeries(); };
            return el('div', { class: 'row g-2 mb-2 align-items-center' }, [
                el('div', { class: 'col-md-3' }, [field]),
                el('div', { class: 'col-md-4' }, [label]),
                el('div', { class: 'col-md-3 d-flex align-items-center gap-2' }, [
                    el('div', { class: 'form-check mb-0' }, [own, el('label', { class: 'form-check-label small', for: 'series-own-' + i, text: 'warna sendiri' })]), color,
                ]),
                el('div', { class: 'col-md-2 text-nowrap text-end' }, [
                    iconButton('↑', 'Naik', move(-1), i === 0), iconButton('↓', 'Turun', move(1), i === chartSeries.length - 1),
                    iconButton('×', 'Hapus', () => { chartSeries.splice(i, 1); renderChartSeries(); }),
                ]),
                warn,
            ]);
        });
        host.replaceChildren(...rows);
        refreshChartWarnings();
        syncChartKind();
    }
    $('chart-add-series').addEventListener('click', () => { chartSeries.push({ valueField: '', label: '', color: '' }); renderChartSeries(); });

    $('chart-load-columns').addEventListener('click', async () => {
        const status = $('chart-columns-status');
        const data = await fetchColumns(status, $('chart-columns'));
        if (!data) return;
        // Nothing chosen yet: the first non-number column labels the categories, the first number column becomes a series.
        const first = data.columns.find((c) => c.suggestedFormat !== 'number') || data.columns[0];
        if (!chartInput('labelField').value.trim() && first) chartInput('labelField').value = first.name;
        const label = chartInput('labelField').value.trim().toLowerCase();
        const number = data.columns.find((c) => c.suggestedFormat === 'number' && c.name.toLowerCase() !== label);
        if (chartSeries.length === 0 && number) { chartSeries.push({ valueField: number.name, label: '', color: '' }); renderChartSeries(); }
        refreshChartWarnings();
        status.textContent = loadedNote(data);
    });

    function collectChart() {
        const round = isRound();
        const out = {
            kind: chartInput('kind').value,
            labelField: chartInput('labelField').value.trim(),
            series: chartSeries.filter((s) => (s.valueField || '').trim()).map((s) => {
                const item = { valueField: s.valueField.trim() };
                if ((s.label || '').trim()) item.label = s.label.trim();
                if (s.color) item.color = s.color;
                return item;
            }),
            stacked: !round && chartInput('stacked').checked,
            horizontal: chartInput('kind').value === 'bar' && chartInput('horizontal').checked,
            legend: chartInput('legend').checked,
            maxRows: parseInt(chartInput('maxRows').value, 10) || 100,
        };
        const title = chartInput('title').value.trim();
        if (title) out.title = title;
        return out;
    }

    // ---- suggest form fields from the Detail data source ----
    $('suggest-fields').addEventListener('click', async () => {
        const status = $('fields-status');
        const id = $('DetailDataSourceId').value;
        if (!id) { status.textContent = 'Pilih data source Detail dulu (tab Source).'; return; }
        status.textContent = 'memuat...';
        try {
            const res = await postJson('/Builder/Module/LoadColumns', { dataSourceId: id, key: $('suggest-key').value.trim() || null });
            if (!res.ok) { status.textContent = ''; $('fields').prepend(errorBox(res, 'Kolom Detail tidak dapat dimuat.')); return; }

            const key = $('keyField').value.trim().toLowerCase();
            const have = new Set(fields.map((f) => (f.name || '').toLowerCase()));
            let added = 0;
            res.data.columns.forEach((c) => {
                const valid = /^[A-Za-z_][A-Za-z0-9_]*$/.test(c.name);
                if (!valid || c.name.toLowerCase() === key || have.has(c.name.toLowerCase())) return;
                fields.push({ name: c.name, label: '', type: c.suggestedFormat || 'text', required: false, readonly: false });
                added++;
            });
            renderFields();
            status.textContent = added + ' field ditambahkan' + (res.data.hasRows ? '' : ' (tanpa baris contoh: tipe ditebak "text"; isi kunci contoh untuk tebakan yang lebih baik)') + '.';
        } catch (e) {
            status.textContent = 'Server tidak dapat dihubungi.';
        }
    });

    // ---- config (shared by Save and Preview) ----
    function collectConfig() {
        // A Widget has only its own settings (the server ignores Display and Form for it anyway).
        if (currentType() === 'widget') return { widget: collectWidget(), access: config.access || { view: ['*'] } };
        if (currentType() === 'chart') return { chart: collectChart(), access: config.access || { view: ['*'] } };

        const take = parseInt($('take').value, 10) || 10;
        const maxTake = parseInt($('maxTake').value, 10) || 100;
        const keyField = $('keyField').value.trim();
        const formFields = fields.filter((f) => f.name).map((f) => {
            const type = f.type || 'text';
            const out = { name: f.name, type: type, required: !!f.required, readonly: !!f.readonly };
            if (f.label && f.label.trim()) out.label = f.label.trim();
            if (isTextType(type)) {
                if (f.maxLength) out.maxLength = f.maxLength;
                if (f.regex) out.regex = f.regex;
            }
            if (f.default) out.default = f.default;
            // A lookup without a data source is left out so the server reports "membutuhkan lookup" instead of failing to read the config.
            if (isLookupType(type) && f.lookup && f.lookup.dataSourceId) {
                out.lookup = { dataSourceId: f.lookup.dataSourceId, valueField: (f.lookup.valueField || '').trim(), textField: (f.lookup.textField || '').trim() };
            }
            return out;
        });
        const display = {
            take: take, maxTake: maxTake,
            columns: columns.filter((c) => c.field).map((c) => {
                const out = { field: c.field, visible: c.visible !== false, sortable: !!c.sortable, format: c.format || 'text' };
                if (c.label && c.label.trim()) out.label = c.label.trim();
                return out;
            }),
        };
        if (keyField) display.keyField = keyField;
        return { display: display, form: { fields: formFields }, access: config.access || { view: ['*'] } };
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

    // ---- Widget / Chart preview: the server answers with the same JSON as the runtime page, drawn by the same renderer ----
    $('visual-preview-run').addEventListener('click', async () => {
        const button = $('visual-preview-run');
        const status = $('visual-preview-status');
        const host = $('visual-preview-host');
        button.disabled = true;
        status.textContent = 'menjalankan...';
        try {
            const res = await postJson('/Builder/Module/PreviewVisual', {
                name: $('Name').value, type: currentType(), dataSourceId: dataSourceId(),
                config: collectConfig(), query: $('visual-preview-query').value,
            });
            if (res.ok && res.data && currentType() === 'chart') {
                window.RakitVisual.renderChart(host, res.data);
                status.textContent = '';
            } else if (res.ok && res.data) {
                window.RakitVisual.renderWidget(host, res.data);
                const seconds = Number(res.data.refreshSeconds);
                status.textContent = seconds ? 'Di halaman runtime kartu dimuat ulang tiap ' + seconds + ' detik.' : '';
            } else {
                host.replaceChildren(errorBox(res, 'Preview gagal.'));
                status.textContent = '';
            }
        } catch (e) {
            host.replaceChildren(el('div', { class: 'alert alert-danger mb-0', text: 'Preview gagal: server tidak dapat dihubungi.' }));
            status.textContent = '';
        } finally {
            button.disabled = false;
        }
    });

    renderColumns();
    renderFields();
    renderChartSeries();
    applyType();
})();

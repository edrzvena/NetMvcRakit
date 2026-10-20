// Module form: the "Buat dari tabel" wizard. Three steps (table, columns and key, actions) in a modal; "Terapkan" asks the server to
// make the data sources (and CRUD procedures), adds them to the data source selects and loads the suggested Display / Form settings
// through window.RakitModuleForm. Nothing is saved until the module itself is saved. Server text goes into the page as text only.
(function () {
    'use strict';

    const RM = window.RakitModuleForm;
    const openButton = document.getElementById('wizard-open');
    const modalEl = document.getElementById('wizard-modal');
    if (!RM || !openButton || !modalEl) return;

    const $ = (id) => document.getElementById(id);
    const el = RM.el;
    const modal = new bootstrap.Modal(modalEl);
    const canProcedures = modalEl.dataset.canProcedures === '1';

    let tables = null;      // [{schema, name, kind}] (loaded once)
    let inspection = null;  // the chosen table, as the server describes it
    let step = 1;
    let busy = false;

    const forForm = () => RM.currentType() === 'form';

    async function getJson(url) {
        const res = await fetch(url, { headers: { Accept: 'application/json' } });
        let data = null;
        try { data = await res.json(); } catch (e) { /* not JSON */ }
        return { ok: res.ok, status: res.status, data: data };
    }
    const failure = (res) => (res.data && res.data.message ? res.data.message : 'Permintaan gagal (HTTP ' + res.status + ').')
        + (res.data && res.data.detail ? ' ' + res.data.detail : '');

    function showError(text) {
        const box = $('wizard-error');
        box.textContent = text || '';
        box.classList.toggle('d-none', !text);
    }

    // ---- steps ----
    const TITLES = { 1: 'tabel', 2: 'kolom dan kunci', 3: 'aksi' };
    function show(n) {
        step = n;
        [1, 2, 3].forEach((i) => $('wizard-step-' + i).classList.toggle('d-none', i !== n));
        $('wizard-done').classList.add('d-none');
        $('wizard-steps').textContent = 'Langkah ' + n + ' dari 3: ' + TITLES[n];
        $('wizard-back').classList.toggle('d-none', n === 1);
        $('wizard-next').classList.toggle('d-none', n === 3);
        $('wizard-apply').classList.toggle('d-none', n !== 3);
        $('wizard-cancel').textContent = 'Batal';
        showError('');
    }

    function setBusy(value) {
        busy = value;
        ['wizard-back', 'wizard-next', 'wizard-apply'].forEach((id) => { $(id).disabled = value; });
    }

    // ---- step 1: tables ----
    function renderTables() {
        const text = $('wizard-filter').value.trim().toLowerCase();
        const keep = $('wizard-table').value;
        const options = (tables || [])
            .filter((t) => (!forForm() || t.kind === 'TABLE') && (text === '' || (t.schema + '.' + t.name).toLowerCase().includes(text)))
            .map((t) => el('option', { value: t.schema + '.' + t.name, text: t.schema + '.' + t.name + '  (' + t.kind + ')' }));
        $('wizard-table').replaceChildren(...options);
        if (keep) $('wizard-table').value = keep;
    }
    $('wizard-filter').addEventListener('input', renderTables);
    $('wizard-table').addEventListener('dblclick', () => { if (!busy) next(); });

    openButton.addEventListener('click', async () => {
        show(1);
        inspection = null;
        $('wizard-filter').value = '';
        modal.show();
        if (tables === null) {
            $('wizard-table').replaceChildren(el('option', { text: 'Memuat...', disabled: true }));
            const res = await getJson('/Builder/Module/WizardTables');
            if (!res.ok) { $('wizard-table').replaceChildren(); showError(failure(res)); return; }
            tables = res.data.tables;
        }
        renderTables();
    });
    modalEl.addEventListener('shown.bs.modal', () => $('wizard-filter').focus());

    // ---- step 2: columns ----
    function selectedColumns() {
        return Array.from($('wizard-columns').querySelectorAll('tbody input[data-column]:checked')).map((c) => c.dataset.column);
    }

    function selectedFilters() {
        return Array.from($('wizard-columns').querySelectorAll('tbody input[data-filter-column]:checked')).map((c) => c.dataset.filterColumn);
    }

    function badge(text, cls) { return el('span', { class: 'badge ' + cls + ' me-1', text: text }); }

    function renderColumns() {
        const keyName = $('wizard-key').value;
        const rows = inspection.columns.map((c) => {
            const isKey = keyName && c.name.toLowerCase() === keyName.toLowerCase();
            const box = el('input', { type: 'checkbox', class: 'form-check-input', 'data-column': c.name, 'aria-label': 'Sertakan ' + c.name });
            box.checked = c.selectable;
            box.disabled = !c.selectable || !!isKey;
            const filterBox = el('input', { type: 'checkbox', class: 'form-check-input', 'data-filter-column': c.name, 'aria-label': 'Jadikan ' + c.name + ' sebagai filter' });
            filterBox.disabled = !c.filterable;
            const marks = [];
            if (c.isPrimaryKey) marks.push(badge('PK', 'text-bg-primary'));
            if (c.isIdentity) marks.push(badge('identity', 'text-bg-secondary'));
            marks.push(badge(c.isNullable ? 'null' : 'NOT NULL', c.isNullable ? 'text-bg-light border' : 'text-bg-light border'));
            if (c.hasDefault) marks.push(badge('default', 'text-bg-light border'));
            return el('tr', { class: c.selectable ? '' : 'text-muted' }, [
                el('td', {}, [box]), el('td', {}, [el('code', { text: c.name })]), el('td', { text: c.type }),
                el('td', {}, marks), el('td', { class: 'text-center' }, [filterBox]), el('td', { class: 'small', text: c.note || '' }),
            ]);
        });
        $('wizard-columns').querySelector('tbody').replaceChildren(...rows);
    }

    function renderStep2() {
        const keyOptions = [el('option', { value: '', text: '(tanpa kunci)' })]
            .concat(inspection.columns.filter((c) => c.selectable).map((c) => el('option', { value: c.name, text: c.name })));
        $('wizard-key').replaceChildren(...keyOptions);
        $('wizard-key').value = inspection.suggestedKey || '';
        $('wizard-key').disabled = false;

        const notes = inspection.notes.map((n) => el('div', { class: 'alert alert-info py-2 small mb-2', role: 'note', text: n }));
        $('wizard-table-notes').replaceChildren(...notes);
        renderColumns();
    }
    $('wizard-key').addEventListener('change', () => {
        const keep = new Set(selectedColumns().map((c) => c.toLowerCase()));
        const keepFilters = new Set(selectedFilters().map((c) => c.toLowerCase()));
        renderColumns();
        $('wizard-columns').querySelectorAll('tbody input[data-column]').forEach((box) => {
            if (!box.disabled) box.checked = keep.has(box.dataset.column.toLowerCase());
        });
        $('wizard-columns').querySelectorAll('tbody input[data-filter-column]').forEach((box) => {
            if (!box.disabled) box.checked = keepFilters.has(box.dataset.filterColumn.toLowerCase());
        });
    });
    $('wizard-all').addEventListener('click', () => $('wizard-columns').querySelectorAll('tbody input[data-column]:not(:disabled)').forEach((b) => { b.checked = true; }));
    $('wizard-none').addEventListener('click', () => $('wizard-columns').querySelectorAll('tbody input[data-column]:not(:disabled)').forEach((b) => { b.checked = false; }));

    // ---- step 3: actions ----
    function renderStep3() {
        const hasKey = !!$('wizard-key').value;
        const writePossible = inspection.canWrite && canProcedures;
        const insert = $('wizard-insert');
        const update = $('wizard-update');
        const del = $('wizard-delete');

        document.querySelectorAll('[data-wizard-key-only]').forEach((n) => n.classList.toggle('d-none', forForm()));
        insert.disabled = !writePossible;
        update.disabled = !writePossible || !hasKey;
        del.disabled = !writePossible || !hasKey;
        insert.checked = writePossible;
        update.checked = writePossible && hasKey && !forForm();
        del.checked = writePossible && hasKey && !forForm();
        $('wizard-no-procedures').classList.toggle('d-none', canProcedures || !inspection.canWrite);

        const parts = [];
        if (!forForm()) parts.push('data source List' + (hasKey ? ' dan Detail' : ' (tanpa kunci: tidak ada Detail)'));
        parts.push('isian tab ' + (forForm() ? 'Form' : 'Display dan Form'));
        $('wizard-summary').textContent = 'Akan dibuat atau diperbarui: ' + parts.join(', ') + ', ditambah data source dan prosedur untuk aksi yang dicentang.'
            + (inspection.canWrite ? '' : ' (View: hanya baca.)');
    }

    // ---- navigation ----
    async function next() {
        if (busy) return;
        showError('');
        if (step === 1) {
            const chosen = $('wizard-table').value;
            if (!chosen) { showError('Pilih satu tabel atau view dulu.'); return; }
            const dot = chosen.indexOf('.');
            setBusy(true);
            try {
                const res = await getJson('/Builder/Module/WizardInspect?schema=' + encodeURIComponent(chosen.slice(0, dot)) + '&name=' + encodeURIComponent(chosen.slice(dot + 1)));
                if (!res.ok) { showError(failure(res)); return; }
                inspection = res.data;
            } catch (e) { showError('Server tidak dapat dihubungi.'); return; } finally { setBusy(false); }
            renderStep2();
            show(2);
        } else if (step === 2) {
            if (selectedColumns().length === 0) { showError('Pilih setidaknya satu kolom.'); return; }
            renderStep3();
            show(3);
        }
    }
    $('wizard-next').addEventListener('click', next);
    $('wizard-back').addEventListener('click', () => { if (!busy && step > 1) show(step - 1); });

    // ---- apply ----
    $('wizard-apply').addEventListener('click', async () => {
        if (busy) return;
        showError('');
        setBusy(true);
        try {
            const res = await RM.postJson('/Builder/Module/WizardApply', {
                schema: inspection.schema,
                name: inspection.name,
                columns: selectedColumns(),
                keyColumn: $('wizard-key').value || null,
                filterColumns: forForm() ? [] : selectedFilters(),
                insert: $('wizard-insert').checked && !$('wizard-insert').disabled,
                update: $('wizard-update').checked && !$('wizard-update').disabled,
                delete: $('wizard-delete').checked && !$('wizard-delete').disabled,
                forForm: forForm(),
            });
            if (!res.ok || !res.data) { showError(failure(res)); return; }
            finish(res.data);
        } catch (e) {
            showError('Server tidak dapat dihubungi.');
        } finally {
            setBusy(false);
        }
    });

    function finish(result) {
        result.dataSources.forEach((ds) => RM.addDataSourceOption(ds.role, ds));

        // Only what the wizard knows is loaded: Display (columns, key) for a table, Form fields when there are write actions.
        const partial = {};
        const config = result.config || {};
        if (!forForm() && config.display) {
            partial.display = { keyField: config.display.keyField || '', columns: config.display.columns || [] };
            // what the wizard knows about the page: filters it made (only when it made some, so manual ones are not wiped), the
            // Lihat button when it made a Detail data source, and a Muat ulang item; the other buttons and menu items stay as they are
            if ((config.display.filters || []).length > 0) partial.display.filters = config.display.filters;
            partial.display.buttons = { detail: !!(config.display.buttons && config.display.buttons.detail) };
            partial.display.menu = { refresh: true };
        }
        if (config.form && config.form.fields && config.form.fields.length > 0) partial.form = { fields: config.form.fields };
        RM.applyConfig(partial);

        const roleText = { list: 'List', detail: 'Detail', insert: 'Tambah', update: 'Ubah', delete: 'Hapus' };
        const items = result.dataSources.map((ds) => el('li', {}, [
            el('strong', { text: roleText[ds.role] + ': ' }), el('code', { text: ds.code }),
            document.createTextNode(' (' + ds.mode + ', ' + (ds.created ? 'baru' : 'versi baru dari yang sudah ada') + ')'),
        ]));
        const notes = result.notes.map((n) => el('li', { text: n }));
        const done = $('wizard-done');
        done.replaceChildren(
            el('div', { class: 'alert alert-success', role: 'status' }, [
                el('div', { class: 'fw-semibold', text: 'Selesai. Data source dipilih dan tab Display / Form sudah terisi.' }),
                el('div', { class: 'small', text: 'Module belum tersimpan: periksa tab Display dan Form, lalu klik Simpan.' }),
            ]),
            el('ul', { class: 'mb-2' }, items),
            notes.length > 0 ? el('div', { class: 'alert alert-warning small mb-0' }, [el('ul', { class: 'mb-0' }, notes)]) : el('span'),
        );
        [1, 2, 3].forEach((i) => $('wizard-step-' + i).classList.add('d-none'));
        done.classList.remove('d-none');
        $('wizard-steps').textContent = 'Selesai';
        ['wizard-back', 'wizard-next', 'wizard-apply'].forEach((id) => $(id).classList.add('d-none'));
        $('wizard-cancel').textContent = 'Tutup';

        $('wizard-status').className = 'small mt-2 text-success';
        $('wizard-status').textContent = 'Terakhir: ' + inspection.schema + '.' + inspection.name + ' (' + result.dataSources.length + ' data source). Belum tersimpan sampai Anda menyimpan module.';
    }
})();

// Workflow form: the step list lives in the browser and is serialised into the hidden StepsJson field on submit, in the shape
// the server stores ([{code, name, type, dataSourceId}], run in this order). "Periksa" sends the unsaved list to the server for
// a static check (nothing is executed). Everything from the database is written with textContent, never parsed as HTML.
(function () {
    'use strict';

    const form = document.getElementById('workflow-form');
    if (!form) return;

    const $ = (id) => document.getElementById(id);
    const host = $('steps');
    const stepsInput = $('StepsJson');
    const sources = JSON.parse(host.dataset.sources || '[]'); // [{id, label}]

    let steps = [];
    try { steps = JSON.parse(stepsInput.value || '[]') || []; } catch (e) { steps = []; }
    steps.forEach((s) => { s.type = s.type || 'dataSource'; s.dataSourceId = s.dataSourceId || ''; s.code = s.code || ''; s.name = s.name || ''; });

    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (k === 'class') node.className = v;
            else if (k === 'text') node.textContent = v;
            else if (k === 'value') node.value = v;
            else if (v === true) node.setAttribute(k, '');
            else if (v !== false && v != null) node.setAttribute(k, v);
        });
        (children || []).forEach((c) => node.appendChild(c));
        return node;
    }
    function iconButton(text, title, onClick, disabled) {
        const b = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: text, title: title, disabled: !!disabled });
        b.addEventListener('click', onClick);
        return b;
    }

    function render() {
        if (steps.length === 0) {
            host.replaceChildren(el('div', { class: 'text-muted small mb-2', text: 'Belum ada langkah: tambahkan setidaknya satu.' }));
            return;
        }
        const head = el('div', { class: 'row g-2 small text-muted mb-1 d-none d-md-flex' }, [
            el('div', { class: 'col-md-1', text: '#' }), el('div', { class: 'col-md-2', text: 'Kode' }), el('div', { class: 'col-md-3', text: 'Nama (opsional)' }),
            el('div', { class: 'col-md-4', text: 'Data source' }), el('div', { class: 'col-md-2', text: '' }),
        ]);
        const rows = steps.map((s, i) => {
            const code = el('input', { class: 'form-control form-control-sm', value: s.code, maxlength: '50', placeholder: 'mis. create', 'aria-label': 'Kode langkah ' + (i + 1) });
            const name = el('input', { class: 'form-control form-control-sm', value: s.name, maxlength: '100', placeholder: 'tampil di pesan', 'aria-label': 'Nama langkah ' + (i + 1) });
            const source = el('select', { class: 'form-select form-select-sm', 'aria-label': 'Data source langkah ' + (i + 1) });
            source.appendChild(el('option', { value: '', text: '- pilih data source -' }));
            sources.forEach((o) => { const opt = el('option', { value: o.id, text: o.label }); if (o.id === s.dataSourceId) opt.selected = true; source.appendChild(opt); });
            if (s.dataSourceId && !sources.some((o) => o.id === s.dataSourceId)) {
                const gone = el('option', { value: s.dataSourceId, text: '(tidak ditemukan) ' + s.dataSourceId });
                gone.selected = true;
                source.appendChild(gone);
            }
            code.addEventListener('input', () => { s.code = code.value.trim(); });
            name.addEventListener('input', () => { s.name = name.value; });
            source.addEventListener('change', () => { s.dataSourceId = source.value; });

            const move = (d) => () => { const j = i + d; if (j < 0 || j >= steps.length) return; [steps[i], steps[j]] = [steps[j], steps[i]]; render(); };
            return el('div', { class: 'row g-2 mb-2 align-items-center' }, [
                el('div', { class: 'col-md-1 text-muted', text: String(i + 1) }),
                el('div', { class: 'col-md-2' }, [code]),
                el('div', { class: 'col-md-3' }, [name]),
                el('div', { class: 'col-md-4' }, [source]),
                el('div', { class: 'col-md-2 text-nowrap text-end' }, [
                    iconButton('↑', 'Naik', move(-1), i === 0), iconButton('↓', 'Turun', move(1), i === steps.length - 1),
                    iconButton('×', 'Hapus', () => { steps.splice(i, 1); render(); }),
                ]),
            ]);
        });
        host.replaceChildren(head, ...rows);
    }

    function nextCode() {
        const used = new Set(steps.map((s) => (s.code || '').toLowerCase()));
        let n = steps.length + 1;
        while (used.has('step' + n)) n++;
        return 'step' + n;
    }
    $('add-step').addEventListener('click', () => { steps.push({ code: nextCode(), name: '', type: 'dataSource', dataSourceId: '' }); render(); });

    function collect() {
        return steps.map((s) => {
            const out = { code: (s.code || '').trim(), type: 'dataSource', dataSourceId: s.dataSourceId || '00000000-0000-0000-0000-000000000000' };
            if ((s.name || '').trim()) out.name = s.name.trim();
            return out;
        });
    }
    form.addEventListener('submit', () => { stepsInput.value = JSON.stringify(collect()); });

    // ---- check ----
    function token() {
        const input = form.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }
    $('check-steps').addEventListener('click', async () => {
        const status = $('check-status');
        const result = $('check-result');
        status.textContent = 'memeriksa...';
        result.replaceChildren();
        try {
            const res = await fetch('/Builder/Workflow/Check', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: token() },
                body: JSON.stringify({ steps: collect() }),
            });
            let data = null;
            try { data = await res.json(); } catch (e) { /* non-JSON body */ }
            if (!res.ok || !data || !Array.isArray(data.issues)) {
                result.replaceChildren(el('div', { class: 'alert alert-danger mb-0', role: 'alert', text: (data && data.message) || 'Pemeriksaan gagal (HTTP ' + res.status + ').' }));
                return;
            }
            if (data.issues.length === 0) {
                result.replaceChildren(el('div', { class: 'alert alert-success mb-0', role: 'status', text: 'Tidak ada masalah ditemukan.' }));
                return;
            }
            const errors = data.issues.filter((i) => i.isError);
            const warnings = data.issues.filter((i) => !i.isError);
            const box = (kind, title, list) => el('div', { class: 'alert alert-' + kind + ' mb-2', role: kind === 'danger' ? 'alert' : 'status' }, [
                el('div', { class: 'fw-semibold', text: title }),
                el('ul', { class: 'mb-0 ps-3' }, list.map((i) => el('li', { text: i.message }))),
            ]);
            const parts = [];
            if (errors.length) parts.push(box('danger', errors.length + ' masalah yang menghalangi publish:', errors));
            if (warnings.length) parts.push(box('warning', warnings.length + ' peringatan:', warnings));
            result.replaceChildren(...parts);
        } catch (e) {
            result.replaceChildren(el('div', { class: 'alert alert-danger mb-0', text: 'Pemeriksaan gagal: server tidak dapat dihubungi.' }));
        } finally {
            status.textContent = '';
        }
    });

    if (steps.length === 0) steps.push({ code: 'step1', name: '', type: 'dataSource', dataSourceId: '' });
    render();
})();

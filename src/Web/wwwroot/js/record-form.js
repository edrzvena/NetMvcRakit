// Shared by the runtime pages that edit a record: the Table page (add/edit in a modal) and the Form page (inline).
// RakitRecordForm.create({ root, form, alertId, hostModalId }) wires everything that does not depend on where the form lives:
//   - write/read calls with the anti-forgery header and a uniform {ok, status, data} result (non-JSON bodies tolerated),
//   - per-field error display from the server's 400 `errors`, reset to defaults / fill from a record,
//   - select options from the lookup endpoint, and the browse picker (#lookup-modal, when the page has one).
// `root` carries data-record-url and data-lookup-url and contains the anti-forgery input. `form` holds the inputs (found by
// [data-field], rendered by _RecordFields.cshtml); it may be null on a page that only deletes. `hostModalId` is the modal the form
// lives in: Bootstrap does not stack modals, so the host hides while the picker is open and comes back when it closes.
(function () {
    'use strict';

    const $ = (id) => document.getElementById(id);

    // textContent only: nothing from the database is ever parsed as HTML
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
    const modalOf = (id) => (window.bootstrap && $(id) ? window.bootstrap.Modal.getOrCreateInstance($(id)) : null);

    function create(opts) {
        const root = opts.root;
        const form = opts.form || null;
        const alertId = opts.alertId || 'record-alert';
        const hostModalId = opts.hostModalId || null;
        const recordUrl = root.dataset.recordUrl;
        const lookupUrl = (field) => (root.dataset.lookupUrl || '').replace('__FIELD__', encodeURIComponent(field));
        const tokenInput = root.querySelector('input[name="__RequestVerificationToken"]');

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

        // -- fields
        const inputs = () => (form ? [...form.querySelectorAll('[data-field]')] : []);
        function clearErrors() {
            const box = $(alertId);
            if (box) box.classList.add('d-none');
            inputs().forEach((i) => {
                i.classList.remove('is-invalid');
                const feedback = i.closest('[data-group]').querySelector('.invalid-feedback');
                if (feedback) feedback.textContent = '';
            });
        }
        function showErrors(res) {
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
            const box = $(alertId);
            box.textContent = general.join(' ');
            box.classList.remove('d-none');
            if (first) first.focus();
        }

        // A select's options come from the lookup. A stored value that is not among them (stale, or the list was cut short) is kept
        // as an extra, marked option so that saving does not silently change it.
        function setValue(i, v) {
            const value = v == null ? '' : String(v);
            if (i.dataset.lookup === 'select' && value !== '' && ![...i.options].some((o) => o.value === value)) {
                i.appendChild(el('option', { value: value, text: value + ' (tidak ada di daftar)' }));
            }
            i.value = value;
            const caption = i.closest('[data-group]').querySelector('[data-lookup-text]');
            if (caption) caption.textContent = ''; // the text of a browse value is only known after picking
        }
        function reset() {
            inputs().forEach((i) => {
                const d = i.dataset.default || '';
                if (i.type === 'checkbox') i.checked = d.toLowerCase() === 'true';
                else setValue(i, d);
            });
        }
        function fill(values) {
            inputs().forEach((i) => {
                const v = values[i.dataset.field];
                if (i.type === 'checkbox') i.checked = String(v).toLowerCase() === 'true';
                else setValue(i, v);
            });
        }

        // -- lookups: select options (loaded every time the form opens) and the browse picker
        const fetchLookup = (field, params) => {
            const q = new URLSearchParams(params);
            return send('GET', lookupUrl(field) + (q.toString() ? '?' + q : ''));
        };
        async function loadSelects() {
            await Promise.all(inputs().filter((i) => i.dataset.lookup === 'select').map(async (select) => {
                const note = select.closest('[data-group]').querySelector('[data-lookup-note]');
                note.classList.add('d-none');
                const res = await fetchLookup(select.dataset.field, {});
                select.replaceChildren(el('option', { value: '', text: '- pilih -' }));
                if (!res.ok) {
                    note.textContent = 'Pilihan tidak dapat dimuat: ' + failureText(res);
                    note.classList.remove('d-none');
                    return;
                }
                res.data.items.forEach((item) => select.appendChild(el('option', { value: item.value, text: item.text })));
                if (res.data.hasNext) {
                    note.textContent = 'Daftar pilihan dipotong (' + res.data.items.length + (res.data.total != null ? ' dari ' + res.data.total : '') + '). Gunakan tipe browse untuk daftar yang panjang.';
                    note.classList.remove('d-none');
                }
            }));
        }

        // -- browse picker
        const picker = { input: null, field: '', skip: 0, q: '', take: 10, returning: false, pending: false };
        if (form && $('lookup-modal')) {
            const lookupStatus = (text) => { $('lookup-status').textContent = text; };

            async function loadPicker() {
                lookupStatus('Memuat...');
                $('lookup-alert').classList.add('d-none');
                const res = await fetchLookup(picker.field, { _q: picker.q, _skip: String(picker.skip) });
                lookupStatus('');
                if (!res.ok) {
                    $('lookup-list').replaceChildren();
                    $('lookup-alert').textContent = failureText(res);
                    $('lookup-alert').classList.remove('d-none');
                    $('lookup-prev').disabled = true;
                    $('lookup-next').disabled = true;
                    return;
                }
                const data = res.data;
                $('lookup-list').replaceChildren(...(data.items.length === 0
                    ? [el('div', { class: 'list-group-item text-muted', text: 'Tidak ada data.' })]
                    : data.items.map((item) => el('button', {
                        type: 'button', class: 'list-group-item list-group-item-action d-flex justify-content-between gap-3',
                        onclick: () => choose(item),
                    }, [el('span', { text: item.text }), el('span', { class: 'text-muted small', text: item.value })]))));
                const first = data.items.length === 0 ? 0 : data.skip + 1;
                $('lookup-info').textContent = data.items.length === 0 ? '' : first + '-' + (data.skip + data.items.length) + (data.total != null ? ' dari ' + data.total : '');
                $('lookup-prev').disabled = data.skip === 0;
                $('lookup-next').disabled = !data.hasNext;
                picker.take = data.take;
            }

            function choose(item) {
                const input = picker.input;
                input.value = item.value;
                input.classList.remove('is-invalid');
                input.closest('[data-group]').querySelector('[data-lookup-text]').textContent = item.text !== item.value ? item.text : '';
                modalOf('lookup-modal').hide();
            }

            function openPicker(input) {
                picker.input = input;
                picker.field = input.dataset.field;
                picker.skip = 0;
                picker.q = '';
                picker.take = 10;
                $('lookup-q').value = '';
                $('lookup-title').textContent = 'Pilih ' + (input.closest('[data-group]').querySelector('label')?.textContent || 'data').replace(/\s*\*$/, '');
                if (hostModalId) {
                    picker.pending = true;       // the picker opens once the host modal is gone
                    modalOf(hostModalId).hide();
                } else {
                    modalOf('lookup-modal').show();
                    loadPicker();
                }
            }

            if (hostModalId) {
                $(hostModalId).addEventListener('hidden.bs.modal', () => {
                    if (!picker.pending) return;
                    picker.pending = false;
                    picker.returning = true;
                    modalOf('lookup-modal').show();
                    loadPicker();
                });
                $('lookup-modal').addEventListener('hidden.bs.modal', () => {
                    if (!picker.returning) return;
                    picker.returning = false;
                    modalOf(hostModalId).show();
                });
            }
            $('lookup-modal').addEventListener('shown.bs.modal', () => $('lookup-q').focus());
            $('lookup-form').addEventListener('submit', (ev) => { ev.preventDefault(); picker.q = $('lookup-q').value.trim(); picker.skip = 0; loadPicker(); });
            $('lookup-prev').addEventListener('click', () => { picker.skip = Math.max(0, picker.skip - (picker.take || 10)); loadPicker(); });
            $('lookup-next').addEventListener('click', () => { picker.skip += picker.take || 10; loadPicker(); });

            form.addEventListener('click', (ev) => {
                const pick = ev.target.closest('[data-lookup-pick]');
                const clear = ev.target.closest('[data-lookup-clear]');
                if (pick) openPicker(pick.closest('[data-group]').querySelector('[data-lookup="browse"]'));
                if (clear) {
                    const input = clear.closest('[data-group]').querySelector('[data-lookup="browse"]');
                    input.value = '';
                    clear.closest('[data-group]').querySelector('[data-lookup-text]').textContent = '';
                }
            });
        }

        return { recordUrl, send, failureText, keyUrl, inputs, clearErrors, showErrors, reset, fill, loadSelects, modalOf };
    }

    window.RakitRecordForm = { create: create, el: el, modalOf: modalOf };
})();

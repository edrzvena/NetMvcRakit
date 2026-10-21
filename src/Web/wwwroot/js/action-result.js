// What a workflow button leaves on screen once its run succeeded (the "display" of the action response):
//   kind "html"   - a page written in the workflow's Page step (sanitized on the server before it is sent) shown in a modal
//   kind "module" - go to another module (the server built a same-site path; anything else is ignored)
//   kind "form"   - the run is waiting: a form (an Action step) is shown in a modal; its buttons send the answer to the server,
//                   which goes on with the run and answers with the next display (another form, a page, a module, or nothing)
// `env` (only needed for a form) says how to talk to the server: { resumeUrl, send, onDone(data) }, where resumeUrl holds the
// all-zero run id that is replaced by the real one, send(method, url, body) is RakitRecordForm's and onDone gets the final answer.
(function () {
    'use strict';

    const EMPTY_RUN = '00000000-0000-0000-0000-000000000000';

    function h(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (v === null || v === undefined || v === false) return;
            if (k === 'class') node.className = v;
            else node.setAttribute(k, v === true ? '' : String(v));
        });
        (children || []).forEach((c) => node.append(c));
        return node;
    }

    // ---- the form of an Action step -------------------------------------------------------------------------------
    function inputBound(type, text) {
        if (!text) return null;
        // a datetime-local input wants yyyy-MM-ddTHH:mm
        return type === 'dateTime' ? String(text).trim().replace(' ', 'T').slice(0, 16) : String(text).trim();
    }

    function fieldNode(field, index) {
        const id = 'af-' + index;
        const label = field.label || field.name;
        const width = 'col-12' + (field.colSize >= 1 && field.colSize <= 11 ? ' col-md-' + field.colSize : '');

        if (field.type === 'section') {
            const box = h('div', { class: 'col-12' }, [h('h3', { class: 'h6 border-bottom pb-1 mt-2 mb-2' })]);
            box.firstChild.textContent = label;
            if (field.help) {
                const p = h('p', { class: 'small text-muted' });
                p.textContent = field.help;
                box.append(p);
            }
            return { node: box, input: null };
        }

        const group = h('div', { class: width + ' mb-3', 'data-group': true });
        let input;
        if (field.type === 'boolean') {
            input = h('input', { class: 'form-check-input', type: 'checkbox', id: id, name: field.name, value: 'true', 'data-field': field.name });
            if (String(field.default || '').toLowerCase() === 'true') input.checked = true;
            const wrap = h('div', { class: 'form-check' }, [input, h('label', { class: 'form-check-label', for: id })]);
            wrap.querySelector('label').textContent = label;
            wrap.append(h('div', { class: 'invalid-feedback' }));
            group.append(wrap);
        } else {
            const caption = h('label', { class: 'form-label', for: id });
            caption.textContent = label + (field.required ? ' *' : '');
            group.append(caption);
            if (field.type === 'textarea') {
                input = h('textarea', { class: 'form-control', id: id, name: field.name, rows: 3, maxlength: field.maxLength, placeholder: field.placeholder, 'data-field': field.name });
                input.value = field.default || '';
            } else if (field.type === 'select') {
                input = h('select', { class: 'form-select', id: id, name: field.name, 'data-field': field.name });
                input.append(h('option', { value: '' }, []));
                input.firstChild.textContent = '- pilih -';
                (field.options || []).forEach((o) => {
                    const opt = h('option', { value: o.value });
                    opt.textContent = o.text || o.value;
                    input.append(opt);
                });
                input.value = field.default || '';
            } else {
                const types = { number: 'number', date: 'date', dateTime: 'datetime-local', email: 'email', url: 'url' };
                const ranged = field.type === 'number' || field.type === 'date' || field.type === 'dateTime';
                input = h('input', {
                    class: 'form-control', type: types[field.type] || 'text', id: id, name: field.name, 'data-field': field.name,
                    step: field.type === 'number' ? (field.step || 'any') : null,
                    min: ranged ? inputBound(field.type, field.min) : null, max: ranged ? inputBound(field.type, field.max) : null,
                    maxlength: field.maxLength, placeholder: field.placeholder, autocomplete: 'off',
                });
                input.value = field.default || '';
            }
            group.append(input, h('div', { class: 'invalid-feedback' }));
        }
        if (field.help) {
            const help = h('div', { class: 'form-text' });
            help.textContent = field.help;
            group.append(help);
        }
        return { node: group, input: input };
    }

    function showForm(prompt, env) {
        const modalEl = document.getElementById('action-form-modal');
        if (!modalEl || !window.bootstrap || !env || !env.send || !env.resumeUrl) return false;

        const title = document.getElementById('action-form-title');
        const intro = document.getElementById('action-form-intro');
        const alertBox = document.getElementById('action-form-alert');
        const fieldsBox = document.getElementById('action-form-fields');
        const footer = document.getElementById('action-form-footer');

        title.textContent = prompt.title || 'Form';
        intro.textContent = prompt.intro || '';
        intro.classList.toggle('d-none', !prompt.intro);
        alertBox.className = 'alert d-none';
        alertBox.textContent = '';
        fieldsBox.replaceChildren();
        footer.replaceChildren();

        const inputs = [];
        (prompt.fields || []).forEach((field, i) => {
            const made = fieldNode(field, i);
            fieldsBox.append(made.node);
            if (made.input) inputs.push(made.input);
        });

        const buttons = [];
        function setBusy(busy) { buttons.forEach((b) => { b.disabled = busy; }); }
        function clearErrors() {
            alertBox.className = 'alert d-none';
            inputs.forEach((i) => {
                i.classList.remove('is-invalid');
                const feedback = i.closest('[data-group]').querySelector('.invalid-feedback');
                if (feedback) feedback.textContent = '';
            });
        }
        function fail(text) {
            alertBox.className = 'alert alert-danger';
            alertBox.textContent = text;
        }

        async function answer(button) {
            clearErrors();
            if (!button.cancel && !fieldsBox.closest('form').reportValidity()) return;
            const body = new FormData(fieldsBox.closest('form'));
            body.set('_button', button.code);
            setBusy(true);
            const res = await env.send('POST', env.resumeUrl.replace(EMPTY_RUN, prompt.runId), body);
            if (res.ok) {
                const next = res.data && res.data.display;
                if (next && next.kind === 'form') {
                    // a second Action step: the same modal gets the new form (hiding it and showing it again in one go is ignored by Bootstrap)
                    show(next, env);
                    return;
                }
                window.bootstrap.Modal.getOrCreateInstance(modalEl).hide();
                if (next) show(next, env); // a page or a module
                if (env.onDone) env.onDone(res.data || {});
                return;
            }
            setBusy(false);
            if (res.status === 400) {
                const errors = (res.data && res.data.errors) || {};
                const general = [];
                Object.entries(errors).forEach(([name, text]) => {
                    const input = inputs.find((i) => i.dataset.field.toLowerCase() === name.toLowerCase());
                    if (!input) { general.push(text); return; }
                    input.classList.add('is-invalid');
                    input.closest('[data-group]').querySelector('.invalid-feedback').textContent = text;
                });
                fail(general.length ? general.join(' ') : ((res.data && res.data.message) || 'Data belum valid.'));
                const first = inputs.find((i) => i.classList.contains('is-invalid'));
                if (first) first.focus();
                return;
            }
            // the run is over (expired, answered elsewhere, or a step failed): nothing left to correct here
            fail(env.failureText ? env.failureText(res) : ((res.data && res.data.message) || 'Permintaan gagal.'));
            setBusy(true);
            footer.append(h('button', { type: 'button', class: 'btn btn-secondary', 'data-bs-dismiss': 'modal' }, []));
            footer.lastChild.textContent = 'Tutup';
        }

        (prompt.buttons || []).forEach((def) => {
            const style = def.style || 'primary';
            const button = h('button', { type: 'button', class: 'btn btn-' + (def.cancel ? 'outline-secondary' : style) });
            button.textContent = def.label;
            button.addEventListener('click', () => answer(def));
            footer.append(button);
            buttons.push(button);
        });

        // Enter in a text field sends the first button that is not a cancel one
        const form = fieldsBox.closest('form');
        form.onsubmit = (ev) => {
            ev.preventDefault();
            const first = (prompt.buttons || []).find((b) => !b.cancel) || (prompt.buttons || [])[0];
            if (first && !buttons[0].disabled) answer(first);
        };

        const firstInput = inputs.find((i) => !i.disabled);
        if (modalEl.classList.contains('show')) {
            if (firstInput) firstInput.focus(); // already open: the second form of a run
        } else {
            modalEl.addEventListener('shown.bs.modal', () => { if (firstInput) firstInput.focus(); }, { once: true });
            window.bootstrap.Modal.getOrCreateInstance(modalEl).show();
        }
        return true;
    }

    // ---- the other displays ---------------------------------------------------------------------------------------
    function show(display, env) {
        if (!display) return false;

        if (display.kind === 'form') return display.prompt ? showForm(display.prompt, env) : false;

        if (display.kind === 'module') {
            // a path of this site only: never a full URL, never protocol-relative ("//host", or "/\host" which browsers read the same way)
            if (typeof display.url === 'string' && display.url.startsWith('/') && !display.url.startsWith('//') && !display.url.includes('\\')) {
                window.location.assign(display.url);
                return true;
            }
            return false;
        }

        if (display.kind === 'html') {
            const modal = document.getElementById('action-result-modal');
            const body = document.getElementById('action-result-body');
            if (!modal || !body || !window.bootstrap) return false;
            body.innerHTML = display.html || '';
            window.bootstrap.Modal.getOrCreateInstance(modal).show();
            return true;
        }
        return false;
    }

    window.RakitActionResult = { show: show };
})();

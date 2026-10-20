// Runtime page of a Form module: one inline form that inserts a record. After a successful save the form is emptied
// (back to the field defaults) so the next record can be entered. Field helpers live in record-form.js.
(function () {
    'use strict';

    const root = document.getElementById('form-page');
    const form = document.getElementById('record-form');
    if (!root || !form) return;

    const $ = (id) => document.getElementById(id);
    const rf = window.RakitRecordForm.create({ root: root, form: form, alertId: 'record-alert', hostModalId: null });

    function showMessage(text, ok) {
        const box = $('page-message');
        box.className = 'alert ' + (ok ? 'alert-success' : 'alert-danger');
        box.textContent = text;
    }
    const clearMessage = () => { $('page-message').className = 'alert d-none'; };

    async function fresh() {
        await rf.loadSelects(); // choices are read again: they may have changed since the page opened
        rf.reset();
    }

    form.addEventListener('submit', async (ev) => {
        ev.preventDefault();
        clearMessage();
        rf.clearErrors();
        const save = $('record-save');
        save.disabled = true;
        const res = await rf.send('POST', rf.recordUrl, new FormData(form));
        save.disabled = false;
        if (res.ok) {
            showMessage((res.data && res.data.message) || 'Tersimpan.', true);
            await fresh();
            const first = rf.inputs().find((i) => !i.disabled);
            if (first) first.focus();
        } else {
            rf.showErrors(res);
        }
    });

    // Workflow buttons: the same validated form values go to the workflow instead of the Insert data source.
    // A workflow that reaches an Action step answers with a form (display.kind "form"); its buttons come back through env.
    const actionEnv = {
        resumeUrl: root.dataset.resumeUrl,
        send: rf.send,
        failureText: rf.failureText,
        onDone: async (data) => {
            showMessage(data.message || 'Aksi dijalankan.', true);
            await fresh();
        },
    };
    form.querySelectorAll('[data-action]').forEach((button) => {
        button.addEventListener('click', async () => {
            if (button.dataset.confirm && !window.confirm(button.dataset.confirm)) return;
            clearMessage();
            rf.clearErrors();
            button.disabled = true;
            const url = (root.dataset.actionUrl || '').replace('__CODE__', encodeURIComponent(button.dataset.action));
            const res = await rf.send('POST', url, new FormData(form));
            button.disabled = false;
            if (res.ok) {
                const display = res.data && res.data.display;
                if (display && display.kind === 'form' && window.RakitActionResult) {
                    window.RakitActionResult.show(display, actionEnv); // the message and the reset come when the form is answered
                    return;
                }
                showMessage((res.data && res.data.message) || 'Aksi dijalankan.', true);
                if (window.RakitActionResult) window.RakitActionResult.show(display);
                await fresh();
            } else if (res.status === 400) {
                rf.showErrors(res);
            } else {
                showMessage(rf.failureText(res), false);
            }
        });
    });

    $('record-reset').addEventListener('click', () => { clearMessage(); rf.clearErrors(); rf.reset(); });

    fresh();
})();

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

    $('record-reset').addEventListener('click', () => { clearMessage(); rf.clearErrors(); rf.reset(); });

    fresh();
})();

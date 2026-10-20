// Menu form: shows only the fields that belong to the chosen kind of item. The server ignores the others anyway.
(function () {
    'use strict';

    const form = document.getElementById('menu-form');
    if (!form) return;

    const $ = (id) => document.getElementById(id);
    const kind = () => (form.querySelector('input[name="TargetType"]:checked') || {}).value || 'Module';

    function apply() {
        const k = kind();
        $('field-module').classList.toggle('d-none', k !== 'Module');
        $('field-url').classList.toggle('d-none', k !== 'Url');
        $('field-label').classList.toggle('d-none', k === 'Delimiter');
        $('field-icon').classList.toggle('d-none', k === 'Delimiter');
    }
    form.querySelectorAll('input[name="TargetType"]').forEach((r) => r.addEventListener('change', apply));
    apply();
})();

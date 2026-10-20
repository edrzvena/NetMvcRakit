// Module list: filters the rows as you type. A row matches when every word of the query occurs in its name, group, type,
// status or id. A pasted runtime link (…/object/page/{id}) is reduced to the id inside it.
(function () {
    'use strict';

    const input = document.getElementById('module-search');
    const rows = Array.from(document.querySelectorAll('#module-table tbody tr'));
    if (!input || rows.length === 0) return;

    const empty = document.getElementById('module-search-empty');
    const count = document.getElementById('module-search-count');
    const guid = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i;

    rows.forEach((row) => { row.dataset.haystack = row.textContent.toLowerCase() + ' ' + (row.dataset.id || '').toLowerCase(); });

    function apply() {
        const raw = input.value.trim();
        const link = guid.exec(raw);
        const words = (link ? link[0] : raw).toLowerCase().split(/\s+/).filter(Boolean);
        let shown = 0;
        for (const row of rows) {
            const match = words.every((w) => row.dataset.haystack.includes(w));
            row.classList.toggle('d-none', !match);
            if (match) shown++;
        }
        if (empty) empty.classList.toggle('d-none', shown > 0);
        if (count) count.textContent = words.length ? `${shown} dari ${rows.length} module` : `${rows.length} module`;
    }

    input.addEventListener('input', apply);
    input.addEventListener('keydown', (e) => { if (e.key === 'Escape') { input.value = ''; apply(); } });
    apply();
})();

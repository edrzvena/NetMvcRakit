// Module form: the Kode tab (the whole config as JSON in Ace, applied back into the form) and the "Pindah ke module" jump list.
(function () {
    'use strict';

    const RM = window.RakitModuleForm;
    if (!RM) return;
    const $ = (id) => document.getElementById(id);

    // ---- jump to another module ----
    const jump = $('module-jump');
    if (jump) {
        const search = $('module-jump-search');
        const list = $('module-jump-list');
        const items = Array.from(list.querySelectorAll('[data-jump-item]'));

        function filter() {
            const words = search.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
            items.forEach((a) => a.classList.toggle('d-none', !words.every((w) => a.dataset.text.includes(w))));
            // a group heading stays only while one of its items shows
            let heading = null;
            let any = false;
            const settle = () => { if (heading) heading.classList.toggle('d-none', !any); };
            Array.from(list.children).forEach((node) => {
                if (node.hasAttribute('data-jump-heading')) { settle(); heading = node; any = false; }
                else if (node.hasAttribute('data-jump-item') && !node.classList.contains('d-none')) any = true;
            });
            settle();
            $('module-jump-empty').classList.toggle('d-none', items.some((a) => !a.classList.contains('d-none')));
        }
        search.addEventListener('input', filter);
        jump.addEventListener('shown.bs.dropdown', () => search.focus());
        list.addEventListener('click', (ev) => {
            const link = ev.target.closest('[data-jump-item]');
            if (link && RM.isDirty() && !window.confirm('Perubahan yang belum disimpan akan hilang. Pindah ke module ini?')) ev.preventDefault();
        });
    }

    // ---- Kode tab ----
    const host = $('code-editor');
    if (!host || typeof ace === 'undefined') return;

    const status = $('code-status');
    const say = (kind, text) => { status.className = 'small text-' + kind; status.textContent = text; };
    let editor = null;
    const isDark = () => document.documentElement.getAttribute('data-bs-theme') === 'dark';

    function ensureEditor() {
        if (editor) return editor;
        ace.config.set('basePath', '/lib/ace');
        editor = ace.edit('code-editor');
        editor.session.setMode('ace/mode/json');
        editor.setOptions({ fontSize: 13, showPrintMargin: false, tabSize: 2, useSoftTabs: true });
        editor.renderer.setScrollMargin(6, 6);
        const theme = () => editor.setTheme(isDark() ? 'ace/theme/tomorrow_night' : 'ace/theme/chrome');
        theme();
        window.addEventListener('rakit-theme', theme);
        new ResizeObserver(() => editor.resize()).observe(host);
        return editor;
    }

    function reload() {
        ensureEditor().setValue(JSON.stringify(RM.collectFullConfig(), null, 2), -1);
    }

    $('tab-code').addEventListener('shown.bs.tab', () => { reload(); say('muted', ''); });
    $('code-reload').addEventListener('click', () => { reload(); say('muted', 'Dimuat ulang dari formulir.'); });

    $('code-apply').addEventListener('click', () => {
        let parsed;
        try {
            parsed = JSON.parse(ensureEditor().getValue());
        } catch (e) {
            say('danger', 'JSON tidak valid, tidak ada yang diubah: ' + e.message);
            return;
        }
        if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
            say('danger', 'Isinya harus berupa objek JSON ({ ... }), tidak ada yang diubah.');
            return;
        }
        const result = RM.applyConfig(parsed);
        if (!result.ok) {
            say('danger', 'Tidak diterapkan: ' + result.errors.join(' '));
            return;
        }
        reload(); // what the form now really holds (unknown keys and empty parts are normalised away)
        say('success', 'Diterapkan ke formulir. Periksa tab lain, lalu klik Simpan.');
    });

    $('code-copy').addEventListener('click', async () => {
        try { await navigator.clipboard.writeText(ensureEditor().getValue()); say('success', 'Tersalin.'); } catch (e) { say('danger', 'Gagal menyalin.'); }
    });
})();

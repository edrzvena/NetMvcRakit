// Small helpers for the Builder module screens: Bootstrap tooltips on [data-bs-toggle="tooltip"] and a copy button for
// every [data-copy] element (copies the attribute's value, then shows a check icon for a moment).
(function () {
    'use strict';

    document.querySelectorAll('[data-bs-toggle="tooltip"]').forEach((el) => new bootstrap.Tooltip(el));

    async function copyText(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // clipboard API unavailable (not a secure context): fall back to a temporary textarea
            const area = document.createElement('textarea');
            area.value = text;
            area.style.position = 'fixed';
            area.style.opacity = '0';
            document.body.appendChild(area);
            area.select();
            let ok = false;
            try { ok = document.execCommand('copy'); } catch { /* ignore */ }
            area.remove();
            return ok;
        }
    }

    document.querySelectorAll('[data-copy]').forEach((button) => {
        button.addEventListener('click', async () => {
            const icon = button.querySelector('i');
            if (!(await copyText(button.dataset.copy)) || !icon) return;
            const before = icon.className;
            icon.className = 'bi bi-clipboard-check text-success';
            setTimeout(() => { icon.className = before; }, 1200);
        });
    });
})();

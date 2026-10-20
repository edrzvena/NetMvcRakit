// App shell: the sidebar on small screens, the collapsible menu folders, and the light/dark theme.
(function () {
    'use strict';

    const body = document.body;
    const $ = (id) => document.getElementById(id);

    // ---- sidebar (small screens) ----
    const toggle = $('rk-toggle');
    const backdrop = $('rk-backdrop');
    const setNav = (open) => {
        body.classList.toggle('rk-nav-open', open);
        if (toggle) toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
    };
    if (toggle) toggle.addEventListener('click', () => setNav(!body.classList.contains('rk-nav-open')));
    if (backdrop) backdrop.addEventListener('click', () => setNav(false));
    document.addEventListener('keydown', (ev) => { if (ev.key === 'Escape') setNav(false); });
    document.querySelectorAll('.rk-sidebar a.rk-link').forEach((a) => a.addEventListener('click', () => setNav(false)));

    // ---- menu folders: open while they hold the current page, otherwise as the visitor left them ----
    const stored = (key) => { try { return window.localStorage.getItem(key); } catch (e) { return null; } };
    const store = (key, value) => { try { window.localStorage.setItem(key, value); } catch (e) { /* storage blocked */ } };
    document.querySelectorAll('.rk-group').forEach((group) => {
        const button = group.querySelector('.rk-group-toggle');
        if (!button) return;
        const key = 'rk-group:' + (button.dataset.key || button.textContent.trim());
        if (stored(key) === '1') group.classList.add('is-open');
        button.setAttribute('aria-expanded', group.classList.contains('is-open') ? 'true' : 'false');
        button.addEventListener('click', () => {
            const open = group.classList.toggle('is-open');
            button.setAttribute('aria-expanded', open ? 'true' : 'false');
            store(key, open ? '1' : '0');
        });
    });

    // ---- theme ----
    const themeButton = $('rk-theme');
    const current = () => (document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'dark' : 'light');
    const paintButton = () => {
        if (!themeButton) return;
        const icon = themeButton.querySelector('i');
        if (icon) icon.className = current() === 'dark' ? 'bi bi-sun' : 'bi bi-moon-stars';
    };
    function applyTheme(theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
        store('rakit-theme', theme);
        paintButton();
        // embedded modules (iframes of a Page module) follow, and charts redraw in the new colours
        document.querySelectorAll('iframe').forEach((frame) => {
            try { frame.contentWindow.postMessage({ type: 'rakit-theme', theme: theme }, window.location.origin); } catch (e) { /* not ours */ }
        });
        window.dispatchEvent(new CustomEvent('rakit-theme', { detail: theme }));
    }
    paintButton();
    if (themeButton) themeButton.addEventListener('click', () => applyTheme(current() === 'dark' ? 'light' : 'dark'));
})();

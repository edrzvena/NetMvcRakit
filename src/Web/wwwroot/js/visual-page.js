// Runtime page of a Chart or Widget module: loads /data once (the page's own query string goes along, so the data source can
// read it through its parameter mappings) and hands the JSON to chart-render.js. A Widget with refreshSeconds reloads itself
// on that interval, never two requests at once and not while the tab is hidden; a failed reload keeps the last value on screen.
(function () {
    'use strict';

    const root = document.getElementById('visual-page');
    if (!root) return;

    const $ = (id) => document.getElementById(id);
    const type = root.dataset.type;
    const host = $('visual-host');
    const status = $('visual-status');
    const errorBox = $('visual-error');
    let shown = false;   // something has been drawn, so a failed reload must not wipe it
    let timer = null;
    let lastLoad = 0;

    async function fetchData() {
        const res = await fetch(root.dataset.url + window.location.search, {
            headers: { Accept: 'application/json' },
            cache: 'no-store',
        });
        let body = null;
        try { body = await res.json(); } catch (e) { /* not JSON: the generic text below is used */ }
        return { ok: res.ok, data: body };
    }

    function fail(res) {
        const message = (res && res.data && res.data.message) || 'Data tidak dapat dimuat saat ini.';
        if (shown) {
            status.textContent = 'Gagal memuat ulang: ' + message;
        } else {
            errorBox.textContent = message;
            errorBox.classList.remove('d-none');
            status.textContent = '';
        }
    }

    async function load() {
        let res;
        try {
            res = await fetchData();
        } catch (e) {
            res = null;
        }
        lastLoad = Date.now();
        if (!res || !res.ok) {
            fail(res);
            return null;
        }
        errorBox.classList.add('d-none');
        status.textContent = '';
        if (type === 'chart') window.RakitVisual.renderChart(host, res.data);
        else window.RakitVisual.renderWidget(host, res.data);
        shown = true;
        return res.data;
    }

    function scheduleRefresh(seconds) {
        window.clearTimeout(timer);
        timer = window.setTimeout(async () => {
            if (document.hidden) { scheduleRefresh(seconds); return; } // visibilitychange reloads when the tab is back
            await load();
            scheduleRefresh(seconds);
        }, seconds * 1000);
    }

    (async function start() {
        const data = await load();
        if (type !== 'widget') return;
        const seconds = data && Number(data.refreshSeconds);
        if (!seconds || seconds < 10) return;
        scheduleRefresh(seconds);
        document.addEventListener('visibilitychange', () => {
            if (!document.hidden && Date.now() - lastLoad >= seconds * 1000) {
                load().then(() => scheduleRefresh(seconds));
            }
        });
    })();
})();

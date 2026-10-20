// Parent side of an embedded module: an iframe marked data-rakit-embed follows the height its content reports
// (embed-resize.js). Messages are accepted only from the same origin and only for an iframe of this page.
(function () {
    'use strict';

    const MAX_HEIGHT = 4000;

    window.addEventListener('message', (ev) => {
        if (ev.origin !== window.location.origin) return;
        const data = ev.data;
        if (!data || data.type !== 'rakit-embed-height' || !Number.isFinite(data.height)) return;
        const frame = [...document.querySelectorAll('iframe[data-rakit-embed]')].find((f) => f.contentWindow === ev.source);
        if (frame) frame.style.height = Math.min(Math.max(Math.ceil(data.height), 60), MAX_HEIGHT) + 'px';
    });
})();

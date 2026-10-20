// Runs inside a page that is embedded (?embed=1) in a Page module: tells the parent window how tall the content is so the
// iframe can follow it (page-embed.js on the parent side). Only same-origin parents are told.
(function () {
    'use strict';

    if (window.parent === window) return; // opened on its own, nothing to tell

    let last = 0;
    function report() {
        // body height (not the viewport's): the iframe growing must not make the content look taller
        const height = Math.ceil(document.body.getBoundingClientRect().height + 16);
        if (height === last) return;
        last = height;
        window.parent.postMessage({ type: 'rakit-embed-height', height: height }, window.location.origin);
    }

    window.addEventListener('load', report);
    if (window.ResizeObserver) new ResizeObserver(report).observe(document.body);
    else window.setInterval(report, 500);
})();

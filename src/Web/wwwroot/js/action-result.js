// What a workflow button leaves on screen once its run succeeded (the "display" of the action response):
//   kind "html"   - a page written in the workflow's Page step (sanitized on the server before it is sent) shown in a modal
//   kind "module" - go to another module (the server built a same-site path; anything else is ignored)
(function () {
    'use strict';

    function show(display) {
        if (!display) return false;

        if (display.kind === 'module') {
            // a path of this site only: never a full URL, never protocol-relative ("//host")
            if (typeof display.url === 'string' && display.url.startsWith('/') && !display.url.startsWith('//')) {
                window.location.assign(display.url);
                return true;
            }
            return false;
        }

        if (display.kind === 'html') {
            const modal = document.getElementById('action-result-modal');
            const body = document.getElementById('action-result-body');
            if (!modal || !body || !window.bootstrap) return false;
            body.innerHTML = display.html || '';
            window.bootstrap.Modal.getOrCreateInstance(modal).show();
            return true;
        }
        return false;
    }

    window.RakitActionResult = { show: show };
})();

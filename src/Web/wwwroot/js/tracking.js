// Builder > Tracking: filters, KPI tiles, a stacked bar per day and a paged table of runs with a step-detail modal.
// Everything that comes from the server is written with textContent.
(function () {
    'use strict';

    const root = document.getElementById('tracking');
    if (!root) return;

    const $ = (id) => document.getElementById(id);
    const EMPTY_RUN = '00000000-0000-0000-0000-000000000000';
    const STATUS = {
        success: ['Berhasil', 'bg-success'], failed: ['Gagal', 'bg-danger'], waiting: ['Menunggu', 'bg-info text-dark'],
        expired: ['Kedaluwarsa', 'bg-secondary'], skipped: ['Dilewati', 'bg-secondary'], rolledback: ['Dibatalkan', 'bg-warning text-dark'],
    };
    const number = new Intl.NumberFormat('id-ID');
    let page = 1;

    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (k === 'class') node.className = v;
            else if (k === 'text') node.textContent = v;
            else if (v === true) node.setAttribute(k, '');
            else if (v !== false && v != null) node.setAttribute(k, v);
        });
        (children || []).forEach((c) => c && node.appendChild(c));
        return node;
    }
    const badge = (status, label) => {
        const known = STATUS[status] || [label || status, 'bg-secondary'];
        return el('span', { class: 'badge ' + known[1], text: label || known[0] });
    };

    function say(text, kind) {
        const box = $('tr-message');
        box.className = text ? 'alert alert-' + kind : 'alert d-none';
        box.textContent = text || '';
    }

    function params() {
        const q = new URLSearchParams({ from: $('tr-from').value, to: $('tr-to').value, page: String(page) });
        if ($('tr-workflow').value) q.set('workflowId', $('tr-workflow').value);
        if ($('tr-status').value) q.set('status', $('tr-status').value);
        return q;
    }

    function drawChart(days) {
        const host = $('tr-chart');
        if (!window.RakitVisual) return;
        const labels = days.map((d) => d.date.slice(5).split('-').reverse().join('/')); // 10-20 -> 20/10
        window.RakitVisual.renderChart(host, {
            kind: 'bar', stacked: true, legend: true, title: 'Run per hari', labels: labels,
            series: [
                { label: 'Berhasil', color: '#1baf7a', data: days.map((d) => d.success) },
                { label: 'Gagal', color: '#e34948', data: days.map((d) => d.failed) },
                { label: 'Menunggu', color: '#eda100', data: days.map((d) => d.waiting) },
            ],
        });
    }

    function drawRows(runs) {
        const body = $('tr-rows');
        body.replaceChildren();
        if (runs.length === 0) {
            body.appendChild(el('tr', {}, [el('td', { colspan: '8', class: 'text-muted text-center py-3', text: 'Tidak ada run pada filter ini.' })]));
            return;
        }
        runs.forEach((r) => {
            const detail = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', 'aria-label': 'Rincian run ' + r.runId.slice(0, 8), title: 'Rincian langkah' }, [el('i', { class: 'bi bi-list-ul', 'aria-hidden': 'true' })]);
            detail.addEventListener('click', () => openRun(r));
            body.appendChild(el('tr', {}, [
                el('td', { class: 'text-nowrap', text: r.started }),
                el('td', { text: r.workflow }),
                el('td', { text: r.actionCode || '' }),
                el('td', { text: r.runBy || '' }),
                el('td', { class: 'text-end', text: String(r.steps) }),
                el('td', { class: 'text-end', text: number.format(r.durationMs) }),
                el('td', {}, [badge(r.status)]),
                el('td', { class: 'text-end' }, [detail]),
            ]));
        });
    }

    async function load() {
        const apply = $('tr-apply');
        apply.disabled = true;
        say('', '');
        try {
            const res = await fetch(root.dataset.url + '?' + params().toString(), { headers: { Accept: 'application/json' } });
            let data = null;
            try { data = await res.json(); } catch (e) { /* non-JSON body */ }
            if (!res.ok || !data || !data.kpi) {
                say((data && data.message) || 'Data tidak dapat dimuat (HTTP ' + res.status + ').', 'danger');
                return;
            }
            $('kpi-total').textContent = number.format(data.kpi.total);
            $('kpi-success').textContent = number.format(data.kpi.success);
            $('kpi-failed').textContent = number.format(data.kpi.failed);
            $('kpi-waiting').textContent = number.format(data.kpi.waiting);
            $('kpi-expired').textContent = data.kpi.expired > 0 ? number.format(data.kpi.expired) + ' kedaluwarsa' : '';
            $('kpi-average').textContent = data.kpi.total === 0 ? '-' : number.format(data.kpi.averageMs) + ' ms';
            if (data.truncated) say('Ada lebih dari ' + number.format(data.maxRuns) + ' run pada rentang ini: yang terlama tidak ikut dihitung. Persempit rentang tanggalnya.', 'warning');
            drawChart(data.days);
            drawRows(data.runs);
            page = data.page;
            $('tr-count').textContent = number.format(data.kpi.total) + ' run';
            $('tr-page').textContent = data.page + ' / ' + data.pageCount;
            $('tr-prev').disabled = data.page <= 1;
            $('tr-next').disabled = data.page >= data.pageCount;
        } catch (e) {
            say('Data tidak dapat dimuat: server tidak dapat dihubungi.', 'danger');
        } finally {
            apply.disabled = false;
        }
    }

    async function openRun(run) {
        const modalEl = $('tr-modal');
        const body = $('tr-modal-rows');
        const alertBox = $('tr-modal-alert');
        $('tr-modal-title').textContent = run.workflow + ' · ' + run.started;
        body.replaceChildren();
        alertBox.className = 'alert d-none';
        window.bootstrap.Modal.getOrCreateInstance(modalEl).show();
        try {
            const res = await fetch(root.dataset.runUrl.replace(EMPTY_RUN, run.runId), { headers: { Accept: 'application/json' } });
            let data = null;
            try { data = await res.json(); } catch (e) { /* non-JSON body */ }
            if (!res.ok || !data || !data.steps) {
                alertBox.className = 'alert alert-danger';
                alertBox.textContent = (data && data.message) || 'Rincian tidak dapat dimuat (HTTP ' + res.status + ').';
                return;
            }
            data.steps.forEach((s) => body.appendChild(el('tr', {}, [
                el('td', { class: 'text-nowrap', text: s.time }),
                el('td', {}, [document.createTextNode(s.name), s.name !== s.code ? el('span', { class: 'text-muted', text: ' (' + s.code + ')' }) : null]),
                el('td', {}, [badge(s.status, s.label)]),
                el('td', { text: s.message || '' }),
                el('td', { class: 'text-end', text: String(s.durationMs) }),
            ])));
        } catch (e) {
            alertBox.className = 'alert alert-danger';
            alertBox.textContent = 'Rincian tidak dapat dimuat: server tidak dapat dihubungi.';
        }
    }

    function isoDate(date) {
        const pad = (n) => String(n).padStart(2, '0');
        return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate());
    }

    $('tr-filter').addEventListener('submit', (ev) => { ev.preventDefault(); page = 1; load(); });
    root.querySelectorAll('[data-days]').forEach((button) => button.addEventListener('click', () => {
        const to = new Date();
        const from = new Date();
        from.setDate(from.getDate() - parseInt(button.dataset.days, 10));
        $('tr-from').value = isoDate(from);
        $('tr-to').value = isoDate(to);
        page = 1;
        load();
    }));
    $('tr-prev').addEventListener('click', () => { page = Math.max(1, page - 1); load(); });
    $('tr-next').addEventListener('click', () => { page += 1; load(); });

    load();
})();

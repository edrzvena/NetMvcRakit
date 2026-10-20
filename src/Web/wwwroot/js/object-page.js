// Runtime page of a Table module: loads one page of rows as JSON and draws the grid. All state (page, size, sort,
// search) lives in the page URL under the reserved _skip/_take/_order/_q keys; every OTHER key of the URL is passed on
// to the server untouched because the data source may read it through its parameter mappings (e.g. ?id=2).
(function () {
    'use strict';

    const root = document.getElementById('object-page');
    if (!root) return;

    const $ = (id) => document.getElementById(id);
    const dataUrl = root.dataset.url;
    const defaultTake = parseInt(root.dataset.take, 10) || 10;
    const maxTake = parseInt(root.dataset.maxTake, 10) || 100;
    const CONTROL = ['_skip', '_take', '_order', '_q'];

    // ---- state, read from / written to the URL ----
    const params = new URLSearchParams(window.location.search);
    const state = {
        skip: Math.max(parseInt(params.get('_skip'), 10) || 0, 0),
        take: parseInt(params.get('_take'), 10) || defaultTake,
        order: params.get('_order') || '',
        q: params.get('_q') || '',
    };
    const others = []; // [key, value] of every non-control key, kept in order
    params.forEach((value, key) => { if (!CONTROL.includes(key)) others.push([key, value]); });

    function query() {
        const p = new URLSearchParams();
        others.forEach(([k, v]) => p.append(k, v));
        if (state.skip > 0) p.set('_skip', String(state.skip));
        if (state.take !== defaultTake) p.set('_take', String(state.take));
        if (state.order) p.set('_order', state.order);
        if (state.q) p.set('_q', state.q);
        return p.toString();
    }

    // ---- DOM helpers (textContent only: nothing from the database is ever parsed as HTML) ----
    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (k === 'class') node.className = v;
            else if (k === 'text') node.textContent = v;
            else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
            else if (v === true) node.setAttribute(k, '');
            else if (v !== false && v != null) node.setAttribute(k, v);
        });
        (children || []).forEach((c) => node.appendChild(c));
        return node;
    }

    function showError(message) {
        const box = $('page-error');
        box.textContent = message;
        box.classList.remove('d-none');
        $('grid').replaceChildren();
        $('pager').classList.add('d-none');
        $('page-status').textContent = '';
    }

    // ---- rendering ----
    function sortOf(order) {
        const cut = order.lastIndexOf('_');
        return cut > 0 ? { field: order.slice(0, cut).toLowerCase(), desc: order.slice(cut + 1).toLowerCase() === 'desc' } : null;
    }

    function render(data) {
        $('page-error').classList.add('d-none');
        $('pager').classList.remove('d-none');
        state.skip = data.skip;
        state.take = data.take;
        state.order = data.orderBy || '';

        const sort = data.orderBy ? sortOf(data.orderBy) : null;
        const head = el('tr', {}, data.columns.map((c) => {
            const align = c.format === 'number' ? 'text-end' : '';
            if (!c.sortable) return el('th', { scope: 'col', class: align, text: c.label });

            const active = sort && sort.field === c.field.toLowerCase();
            const mark = active ? (sort.desc ? ' ▼' : ' ▲') : '';
            const link = el('a', { href: '#', class: 'text-decoration-none text-reset', text: c.label + mark });
            link.addEventListener('click', (ev) => {
                ev.preventDefault();
                state.order = c.field + '_' + (active && !sort.desc ? 'desc' : 'asc');
                state.skip = 0;
                load();
            });
            return el('th', { scope: 'col', class: align, 'aria-sort': active ? (sort.desc ? 'descending' : 'ascending') : 'none' }, [link]);
        }));

        const body = data.rows.length === 0
            ? [el('tr', {}, [el('td', { colspan: String(Math.max(data.columns.length, 1)), class: 'text-muted text-center py-4', text: 'Tidak ada data.' })])]
            : data.rows.map((row) => el('tr', {}, row.map((value, i) => {
                const align = data.columns[i] && data.columns[i].format === 'number' ? 'text-end' : '';
                return value === null ? el('td', { class: align }, [el('span', { class: 'text-muted', text: '-' })]) : el('td', { class: align, text: value });
            })));

        $('grid').replaceChildren(el('table', { class: 'table table-sm table-hover table-striped align-middle' }, [
            el('thead', {}, [head]), el('tbody', {}, body),
        ]));

        // pager
        const first = data.rows.length === 0 ? 0 : data.skip + 1;
        const last = data.skip + data.rows.length;
        $('pager-info').textContent = data.rows.length === 0
            ? (data.total === 0 ? 'Tidak ada data.' : 'Tidak ada baris pada halaman ini.')
            : 'Menampilkan ' + first + '-' + last + (data.total != null ? ' dari ' + data.total : '');
        $('page-prev').disabled = data.skip === 0;
        $('page-next').disabled = !data.hasNext;

        const sizes = [...new Set([10, 25, 50, 100, defaultTake, data.take].filter((n) => n >= 1 && n <= maxTake))].sort((a, b) => a - b);
        $('page-size').replaceChildren(...sizes.map((n) => {
            const o = el('option', { value: String(n), text: String(n) });
            if (n === data.take) o.selected = true;
            return o;
        }));

        $('search-input').value = data.search || '';
        state.q = data.search || '';
        $('page-status').textContent = '';

        const qs = query();
        window.history.replaceState(null, '', window.location.pathname + (qs ? '?' + qs : ''));
    }

    // ---- loading ----
    let loadId = 0;
    async function load() {
        const mine = ++loadId;
        $('page-status').textContent = 'Memuat...';
        try {
            const res = await fetch(dataUrl + '?' + query(), { headers: { Accept: 'application/json' } });
            let data = null;
            try { data = await res.json(); } catch (e) { /* non-JSON body */ }
            if (mine !== loadId) return; // a newer request has taken over
            if (res.ok && data) render(data);
            else showError((data && data.message) || 'Data tidak dapat dimuat (HTTP ' + res.status + ').');
        } catch (e) {
            if (mine === loadId) showError('Server tidak dapat dihubungi.');
        }
    }

    // ---- controls ----
    $('search-form').addEventListener('submit', (ev) => {
        ev.preventDefault();
        state.q = $('search-input').value.trim();
        state.skip = 0;
        load();
    });
    $('search-clear').addEventListener('click', () => {
        $('search-input').value = '';
        state.q = '';
        state.skip = 0;
        load();
    });
    $('page-prev').addEventListener('click', () => { state.skip = Math.max(0, state.skip - state.take); load(); });
    $('page-next').addEventListener('click', () => { state.skip += state.take; load(); });
    $('page-size').addEventListener('change', () => {
        state.take = parseInt($('page-size').value, 10) || defaultTake;
        state.skip = 0;
        load();
    });

    $('search-input').value = state.q;
    load();
})();

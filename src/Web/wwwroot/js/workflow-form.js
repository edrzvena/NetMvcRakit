// Workflow form with a visual canvas. The steps live in the browser as nodes (drag to move) joined by links (drag from the dot on a
// node's right side to the dot on another node's left side, or choose "Berikutnya" in the panel). On submit they are serialised
// into the hidden StepsJson field in the shape the server stores ([{code, name, type, next, x, y, ...settings}]), entry step first.
// Inside the page a step is referred to by a private uid, so renaming a code never breaks a link. Everything that comes from the
// database (names, labels) is written with textContent, never parsed as HTML.
(function () {
    'use strict';

    const form = document.getElementById('workflow-form');
    if (!form) return;

    const $ = (id) => document.getElementById(id);
    const rootEl = $('wf-root');
    const canvas = $('wf-canvas');
    const nodesHost = $('wf-nodes');
    const svg = $('wf-edges');
    const edgeLayer = $('wf-edge-layer');
    const panel = $('wf-panel');
    const stepsInput = $('StepsJson');
    const sources = JSON.parse(rootEl.dataset.sources || '[]');   // [{id, label}] data sources
    const moduleOptions = JSON.parse(rootEl.dataset.modules || '[]'); // [{id, label}] modules

    const NODE_W = 176, NODE_H = 58, START_W = 96, START_H = 40, GAP_X = 230, GAP_Y = 96;
    const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';
    const CODE_PATTERN = /^[A-Za-z][A-Za-z0-9_]{0,49}$/;
    const TYPES = { dataSource: 'Data source', rest: 'Rest', email: 'Email', condition: 'Kondisi', page: 'Halaman', module: 'Module', javascript: 'Skrip', action: 'Form' };
    // not "form": that is the first part of {{form.X}}
    const PREFIX = { dataSource: 'db', rest: 'rest', email: 'mail', condition: 'cond', page: 'page', module: 'mod', javascript: 'js', action: 'ask' };
    const OPERATORS = [['eq', 'sama dengan'], ['ne', 'tidak sama dengan'], ['gt', 'lebih besar dari'], ['ge', 'lebih besar atau sama dengan'],
        ['lt', 'lebih kecil dari'], ['le', 'lebih kecil atau sama dengan'], ['contains', 'mengandung'], ['empty', 'kosong'], ['notEmpty', 'tidak kosong']];
    const HELP_KEYS = 'Isi teks bisa memuat {{kunci}}: {{form.NamaField}} (isian form pada tombol), {{key}} (kunci baris pada tombol per baris), '
        + '{{claim.name}}, {{now}}, dan hasil langkah sebelumnya: {{kode.row.Kolom}}, {{kode.out.Parameter}}, {{kode.return}} (data source), '
        + '{{kode.status}}, {{kode.body}}, {{kode.json.jalur}} (Rest dan Skrip), {{kode.sent}} (Email), {{kode.button}} dan {{kode.form.NamaField}} (Form).';

    const start = { x: 24, y: 24 };
    let uidCounter = 0;
    const newUid = () => 's' + (++uidCounter);

    let steps = [];            // steps[0] is the entry
    let selected = null;       // a step, 'start' or null
    let selectedEdge = null;   // {from: uid | 'start', kind: 'next' | 'yes' | 'no'}

    // ---- DOM helper ----
    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (k === 'class') node.className = v;
            else if (k === 'text') node.textContent = v;
            else if (k === 'value') node.value = v;
            else if (v === true) node.setAttribute(k, '');
            else if (v !== false && v != null) node.setAttribute(k, v);
        });
        (children || []).forEach((c) => c && node.appendChild(c));
        return node;
    }
    const svgEl = (tag, attrs) => {
        const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
        Object.entries(attrs || {}).forEach(([k, v]) => node.setAttribute(k, v));
        return node;
    };

    // ---- model ----
    function defaults(type) {
        return {
            rest: type === 'rest' ? { method: 'GET', url: '', headersText: '', body: '', contentType: '', timeoutSeconds: 15, retries: 0 } : null,
            email: type === 'email' ? { to: '', subject: '', body: '', isHtml: false } : null,
            page: type === 'page' ? { html: '' } : null,
            module: type === 'module' ? { moduleId: '', paramsText: '' } : null,
            condition: type === 'condition' ? { left: '', operator: 'eq', right: '', onTrue: null, onFalse: null } : null,
            script: type === 'javascript' ? { source: 'return {};' } : null,
            action: type === 'action' ? { title: 'Konfirmasi', intro: '', fields: [], buttons: [{ code: 'ok', label: 'OK', style: 'primary', cancel: false }] } : null,
        };
    }

    function fromServer(list) {
        const arr = list.map((s) => {
            const type = TYPES[s.type] ? s.type : 'dataSource';
            const d = defaults(type);
            const step = {
                uid: newUid(), code: s.code || '', name: s.name || '', type: type, x: s.x, y: s.y, next: s.next || null,
                dataSourceId: s.dataSourceId && s.dataSourceId !== EMPTY_GUID ? s.dataSourceId : '',
                rest: d.rest, email: d.email, page: d.page, module: d.module, condition: d.condition, script: d.script, action: d.action,
            };
            if (step.script && s.script) step.script.source = s.script.source || '';
            if (step.action && s.action) step.action = actionFromServer(s.action);
            if (step.rest && s.rest) {
                Object.assign(step.rest, { method: s.rest.method || 'GET', url: s.rest.url || '', body: s.rest.body || '', contentType: s.rest.contentType || '',
                    timeoutSeconds: s.rest.timeoutSeconds || 15, retries: s.rest.retries || 0 });
                step.rest.headersText = (s.rest.headers || []).map((h) => h.name + ': ' + h.value).join('\n');
            }
            if (step.email && s.email) Object.assign(step.email, { to: s.email.to || '', subject: s.email.subject || '', body: s.email.body || '', isHtml: !!s.email.isHtml });
            if (step.page && s.page) step.page.html = s.page.html || '';
            if (step.module && s.module) {
                step.module.moduleId = s.module.moduleId && s.module.moduleId !== EMPTY_GUID ? s.module.moduleId : '';
                step.module.paramsText = (s.module.parameters || []).map((p) => p.name + '=' + p.value).join('\n');
            }
            if (step.condition && s.condition) Object.assign(step.condition, { left: s.condition.left || '', operator: s.condition.operator || 'eq',
                right: s.condition.right || '', onTrue: s.condition.onTrue || null, onFalse: s.condition.onFalse || null });
            return step;
        });
        const byCode = new Map(arr.map((s) => [s.code.toLowerCase(), s]));
        const link = (code) => (code ? (byCode.get(String(code).toLowerCase()) || {}).uid || null : null);
        arr.forEach((s) => {
            s.next = link(s.next);
            if (s.condition) { s.condition.onTrue = link(s.condition.onTrue); s.condition.onFalse = link(s.condition.onFalse); }
        });
        return arr;
    }

    const byUid = (uid) => steps.find((s) => s.uid === uid) || null;
    const labelOf = (s) => (s.name && s.name.trim()) || s.code || '(tanpa kode)';
    const linksOf = (s) => (s.type === 'condition' ? [s.condition.onTrue, s.condition.onFalse] : [s.next]).filter(Boolean);

    function reachable(fromUid) {
        const seen = new Set();
        const queue = [fromUid];
        while (queue.length) {
            const s = byUid(queue.shift());
            if (!s) continue;
            linksOf(s).forEach((u) => { if (!seen.has(u)) { seen.add(u); queue.push(u); } });
        }
        return seen;
    }

    // entry first, then breadth-first along the links, then whatever is not connected
    function ordered() {
        const result = [];
        const seen = new Set();
        const queue = steps.length ? [steps[0].uid] : [];
        while (queue.length) {
            const s = byUid(queue.shift());
            if (!s || seen.has(s.uid)) continue;
            seen.add(s.uid);
            result.push(s);
            linksOf(s).forEach((u) => queue.push(u));
        }
        steps.forEach((s) => { if (!seen.has(s.uid)) result.push(s); });
        return result;
    }

    function collect() {
        const codeOf = (uid) => (byUid(uid) || {}).code || null;
        return ordered().map((s) => {
            const out = { code: (s.code || '').trim(), type: s.type, x: Math.round(s.x), y: Math.round(s.y) };
            if ((s.name || '').trim()) out.name = s.name.trim();
            if (s.type !== 'condition' && s.next) out.next = codeOf(s.next);
            switch (s.type) {
                case 'dataSource':
                    out.dataSourceId = s.dataSourceId || EMPTY_GUID;
                    break;
                case 'rest':
                    out.rest = {
                        method: s.rest.method, url: s.rest.url.trim(), timeoutSeconds: parseInt(s.rest.timeoutSeconds, 10) || 15,
                        retries: parseInt(s.rest.retries, 10) || 0,
                        headers: s.rest.headersText.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
                            const cut = l.indexOf(':');
                            return cut < 0 ? { name: l, value: '' } : { name: l.slice(0, cut).trim(), value: l.slice(cut + 1).trim() };
                        }),
                    };
                    if (s.rest.body) out.rest.body = s.rest.body;
                    if (s.rest.contentType.trim()) out.rest.contentType = s.rest.contentType.trim();
                    break;
                case 'email':
                    out.email = { to: s.email.to, subject: s.email.subject, body: s.email.body, isHtml: !!s.email.isHtml };
                    break;
                case 'page':
                    out.page = { html: s.page.html };
                    break;
                case 'module':
                    out.module = {
                        moduleId: s.module.moduleId || EMPTY_GUID,
                        parameters: s.module.paramsText.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
                            const cut = l.indexOf('=');
                            return cut < 0 ? { name: l, value: '' } : { name: l.slice(0, cut).trim(), value: l.slice(cut + 1).trim() };
                        }),
                    };
                    break;
                case 'condition':
                    out.condition = { left: s.condition.left, operator: s.condition.operator, right: s.condition.right || undefined,
                        onTrue: codeOf(s.condition.onTrue) || undefined, onFalse: codeOf(s.condition.onFalse) || undefined };
                    break;
                case 'javascript':
                    out.script = { source: s.script.source || '' };
                    break;
                case 'action':
                    out.action = actionToServer(s.action);
                    break;
            }
            return out;
        });
    }

    // ---- geometry ----
    const sizeOf = (s) => (s === 'start' ? { w: START_W, h: START_H } : { w: NODE_W, h: NODE_H });
    function outPoint(from, kind) {
        if (from === 'start') return { x: start.x + START_W, y: start.y + START_H / 2 };
        const s = byUid(from);
        const y = kind === 'yes' ? s.y + 18 : kind === 'no' ? s.y + 41 : s.y + NODE_H / 2;
        return { x: s.x + NODE_W, y: y };
    }
    const inPoint = (s) => ({ x: s.x, y: s.y + NODE_H / 2 });
    function curve(a, b) {
        const dx = Math.max(40, Math.abs(b.x - a.x) / 2);
        return 'M ' + a.x + ' ' + a.y + ' C ' + (a.x + dx) + ' ' + a.y + ', ' + (b.x - dx) + ' ' + b.y + ', ' + b.x + ' ' + b.y;
    }

    // ---- links ----
    function edges() {
        const list = [];
        if (steps.length) list.push({ from: 'start', kind: 'start', to: steps[0].uid });
        steps.forEach((s) => {
            if (s.type === 'condition') {
                if (s.condition.onTrue) list.push({ from: s.uid, kind: 'yes', to: s.condition.onTrue });
                if (s.condition.onFalse) list.push({ from: s.uid, kind: 'no', to: s.condition.onFalse });
            } else if (s.next) {
                list.push({ from: s.uid, kind: 'next', to: s.next });
            }
        });
        return list.filter((e) => byUid(e.to) && (e.from === 'start' || byUid(e.from)));
    }

    function setLink(from, kind, toUid) {
        const note = $('check-status');
        if (from === 'start') { setEntry(byUid(toUid)); return; }
        const s = byUid(from);
        if (!s || toUid === from) { note.textContent = 'Sebuah langkah tidak bisa tersambung ke dirinya sendiri.'; return; }
        if (reachable(toUid).has(from)) { note.textContent = 'Sambungan itu membuat alur berputar (loop), jadi ditolak.'; return; }
        note.textContent = '';
        if (kind === 'yes') s.condition.onTrue = toUid;
        else if (kind === 'no') s.condition.onFalse = toUid;
        else s.next = toUid;
        selectedEdge = null;
        renderAll();
    }

    function clearLink(from, kind) {
        if (from === 'start') return; // the entry always exists while there are steps
        const s = byUid(from);
        if (!s) return;
        if (kind === 'yes') s.condition.onTrue = null;
        else if (kind === 'no') s.condition.onFalse = null;
        else s.next = null;
    }

    function setEntry(step) {
        if (!step) return;
        steps = [step, ...steps.filter((s) => s !== step)];
        renderAll();
    }

    function removeStep(step) {
        steps = steps.filter((s) => s !== step);
        steps.forEach((s) => {
            if (s.next === step.uid) s.next = null;
            if (s.condition) {
                if (s.condition.onTrue === step.uid) s.condition.onTrue = null;
                if (s.condition.onFalse === step.uid) s.condition.onFalse = null;
            }
        });
        if (selected === step) selected = null;
        selectedEdge = null;
        renderAll();
    }

    // ---- adding steps ----
    function freeCode(type) {
        const used = new Set(steps.map((s) => (s.code || '').toLowerCase()));
        let n = 1;
        while (used.has(PREFIX[type] + n)) n++;
        return PREFIX[type] + n;
    }
    function freeSpot(x, y) {
        const taken = (px, py) => steps.some((s) => Math.abs(s.x - px) < NODE_W && Math.abs(s.y - py) < NODE_H + 12);
        let py = y;
        while (taken(x, py)) py += GAP_Y;
        return { x: x, y: py };
    }

    function addStep(type) {
        const d = defaults(type);
        const after = selected && selected !== 'start' ? selected : null;
        const spot = after ? freeSpot(after.x + GAP_X, after.y) : steps.length ? freeSpot(start.x + 130, 24) : { x: start.x + 130, y: start.y };
        const step = { uid: newUid(), code: freeCode(type), name: '', type: type, x: spot.x, y: spot.y, next: null, dataSourceId: '',
            rest: d.rest, email: d.email, page: d.page, module: d.module, condition: d.condition, script: d.script, action: d.action };
        steps.push(step); // the first one added is the entry
        if (after) {
            if (after.type === 'condition') {
                if (!after.condition.onTrue) after.condition.onTrue = step.uid;
                else if (!after.condition.onFalse) after.condition.onFalse = step.uid;
            } else if (!after.next) {
                after.next = step.uid;
            }
        }
        selected = step;
        selectedEdge = null;
        renderAll();
        // bring the new box into view only when it is off screen (so "Mulai" is not scrolled away needlessly)
        const right = step.x + NODE_W + 40, bottom = step.y + NODE_H + 40;
        if (right > canvas.scrollLeft + canvas.clientWidth) canvas.scrollLeft = right - canvas.clientWidth;
        if (bottom > canvas.scrollTop + canvas.clientHeight) canvas.scrollTop = bottom - canvas.clientHeight;
    }

    function autoLayout() {
        const level = new Map();
        const queue = steps.length ? [[steps[0].uid, 0]] : [];
        while (queue.length) {
            const [uid, depth] = queue.shift();
            if (level.has(uid) && level.get(uid) >= depth) continue;
            level.set(uid, depth);
            const s = byUid(uid);
            if (s) linksOf(s).forEach((u) => queue.push([u, depth + 1]));
        }
        const rows = new Map();
        let loose = 0;
        steps.forEach((s) => {
            const depth = level.has(s.uid) ? level.get(s.uid) : -1;
            if (depth < 0) { s.x = start.x + 130; s.y = 24 + (Math.max(0, ...[...rows.values()]) + 1 + loose++) * GAP_Y; return; }
            const row = rows.get(depth) || 0;
            rows.set(depth, row + 1);
            s.x = start.x + 130 + depth * GAP_X;
            s.y = 24 + row * GAP_Y;
        });
        renderAll();
    }

    // ---- rendering: nodes and edges ----
    function resizeCanvas() {
        const maxX = Math.max(START_W + 60, ...steps.map((s) => s.x + NODE_W)) + 260;
        const maxY = Math.max(START_H + 60, ...steps.map((s) => s.y + NODE_H)) + 200;
        svg.setAttribute('width', maxX);
        svg.setAttribute('height', maxY);
        nodesHost.style.width = maxX + 'px';
        nodesHost.style.height = maxY + 'px';
    }

    function portButton(cls, data, title) {
        return el('div', { class: 'wf-port ' + cls, 'data-port': data, title: title });
    }

    function buildNode(s) {
        const isStart = s === 'start';
        const node = el('div', { class: isStart ? 'wf-node wf-start' : 'wf-node wf-type-' + s.type, tabindex: '0', role: 'group' });
        if (isStart) {
            node.dataset.uid = 'start';
            node.setAttribute('aria-label', 'Mulai');
            node.appendChild(el('span', { text: 'Mulai' }));
            node.appendChild(portButton('wf-out', 'start', 'Tarik ke langkah pertama'));
            node.style.left = start.x + 'px';
            node.style.top = start.y + 'px';
        } else {
            node.dataset.uid = s.uid;
            node.setAttribute('aria-label', TYPES[s.type] + ': ' + labelOf(s));
            node.appendChild(el('div', { class: 'wf-kind', text: TYPES[s.type] + (steps[0] === s ? ' · awal' : '') }));
            node.appendChild(el('div', { class: 'wf-title', text: labelOf(s) }));
            if (s.name && s.name.trim()) node.appendChild(el('span', { class: 'wf-code', text: s.code }));
            node.appendChild(portButton('wf-in', 'in', 'Sambungkan ke sini'));
            if (s.type === 'condition') {
                node.appendChild(portButton('wf-out wf-yes', 'yes', 'Jika ya: tarik ke langkah berikutnya'));
                node.appendChild(portButton('wf-out wf-no', 'no', 'Jika tidak: tarik ke langkah berikutnya'));
                node.appendChild(el('span', { class: 'wf-port-label wf-yes', text: 'Ya' }));
                node.appendChild(el('span', { class: 'wf-port-label wf-no', text: 'Tidak' }));
            } else {
                node.appendChild(portButton('wf-out', 'next', 'Berikutnya: tarik ke langkah lain'));
            }
            node.style.left = s.x + 'px';
            node.style.top = s.y + 'px';
        }
        if (selected === s) node.classList.add('is-selected');
        return node;
    }

    function renderNodes() {
        nodesHost.replaceChildren(buildNode('start'), ...steps.map(buildNode));
        resizeCanvas();
    }

    function drawEdges() {
        edgeLayer.replaceChildren();
        edges().forEach((e) => {
            const a = outPoint(e.from, e.kind);
            const b = inPoint(byUid(e.to));
            const color = e.kind === 'yes' ? '#198754' : e.kind === 'no' ? '#dc3545' : '#6c757d';
            const marker = e.kind === 'yes' ? 'wf-arrow-yes' : e.kind === 'no' ? 'wf-arrow-no' : 'wf-arrow';
            const isSelected = selectedEdge && selectedEdge.from === e.from && selectedEdge.kind === e.kind;
            const d = curve(a, b);
            const visible = svgEl('path', { d: d, stroke: color, class: 'wf-edge' + (isSelected ? ' is-selected' : ''), 'marker-end': 'url(#' + marker + ')' });
            const hit = svgEl('path', { d: d, class: 'wf-edge-hit' });
            const pick = (ev) => { ev.stopPropagation(); selectedEdge = { from: e.from, kind: e.kind }; selected = null; renderPanel(); drawEdges(); renderNodes(); canvas.focus(); };
            hit.addEventListener('click', pick);
            visible.addEventListener('click', pick);
            edgeLayer.appendChild(visible);
            edgeLayer.appendChild(hit);
        });
    }

    function renderAll() {
        renderNodes();
        drawEdges();
        renderPanel();
    }

    // ---- canvas interaction: select, drag, link ----
    let drag = null;
    let linking = null;

    function nodeOf(target) { return target.closest ? target.closest('.wf-node') : null; }

    nodesHost.addEventListener('pointerdown', (ev) => {
        const nodeEl = nodeOf(ev.target);
        if (!nodeEl) return;
        const uid = nodeEl.dataset.uid;
        const port = ev.target.closest('.wf-port');

        if (port && port.classList.contains('wf-out')) {
            ev.preventDefault();
            ev.stopPropagation();
            linking = { from: uid, kind: port.dataset.port === 'start' ? 'start' : port.dataset.port, temp: svgEl('path', { class: 'wf-edge', stroke: '#2a78d6', 'stroke-dasharray': '6 4', fill: 'none' }) };
            edgeLayer.appendChild(linking.temp);
            return;
        }
        if (port) return;

        selected = uid === 'start' ? 'start' : byUid(uid);
        selectedEdge = null;
        if (uid !== 'start') {
            const s = byUid(uid);
            drag = { s: s, nodeEl: nodeEl, px: ev.clientX, py: ev.clientY, ox: s.x, oy: s.y, moved: false };
            nodeEl.classList.add('is-dragging');
        }
        nodesHost.querySelectorAll('.wf-node.is-selected').forEach((n) => n.classList.remove('is-selected'));
        nodeEl.classList.add('is-selected');
        renderPanel();
    });

    window.addEventListener('pointermove', (ev) => {
        if (drag) {
            const s = drag.s;
            s.x = Math.max(0, Math.round(drag.ox + ev.clientX - drag.px));
            s.y = Math.max(0, Math.round(drag.oy + ev.clientY - drag.py));
            drag.moved = true;
            drag.nodeEl.style.left = s.x + 'px';
            drag.nodeEl.style.top = s.y + 'px';
            drawEdges();
            resizeCanvas();
        } else if (linking) {
            const box = canvas.getBoundingClientRect();
            const p = { x: ev.clientX - box.left + canvas.scrollLeft, y: ev.clientY - box.top + canvas.scrollTop };
            linking.temp.setAttribute('d', curve(outPoint(linking.from, linking.kind), p));
            const over = document.elementFromPoint(ev.clientX, ev.clientY);
            nodesHost.querySelectorAll('.wf-port.is-target').forEach((n) => n.classList.remove('is-target'));
            const target = over && nodeOf(over);
            if (target && target.dataset.uid !== 'start') target.querySelector('.wf-in').classList.add('is-target');
        }
    });

    window.addEventListener('pointerup', (ev) => {
        if (drag) {
            drag.nodeEl.classList.remove('is-dragging');
            drag = null;
        } else if (linking) {
            const over = document.elementFromPoint(ev.clientX, ev.clientY);
            const target = over && nodeOf(over);
            const link = linking;
            linking.temp.remove();
            linking = null;
            nodesHost.querySelectorAll('.wf-port.is-target').forEach((n) => n.classList.remove('is-target'));
            if (target && target.dataset.uid !== 'start') setLink(link.from, link.kind, target.dataset.uid);
        }
    });

    canvas.addEventListener('pointerdown', (ev) => {
        if (ev.target === canvas || ev.target === nodesHost || ev.target === svg) {
            selected = null;
            selectedEdge = null;
            renderAll();
        }
    });

    canvas.addEventListener('keydown', (ev) => {
        if (ev.key === 'Delete' || ev.key === 'Backspace') {
            if (selectedEdge && selectedEdge.from !== 'start') {
                ev.preventDefault();
                clearLink(selectedEdge.from, selectedEdge.kind);
                selectedEdge = null;
                renderAll();
            } else if (selected && selected !== 'start') {
                ev.preventDefault();
                removeStep(selected);
            }
        } else if (ev.key === 'Enter' || ev.key === ' ') {
            const nodeEl = nodeOf(ev.target);
            if (nodeEl && ev.target === nodeEl) {
                ev.preventDefault();
                selected = nodeEl.dataset.uid === 'start' ? 'start' : byUid(nodeEl.dataset.uid);
                selectedEdge = null;
                renderAll();
                const again = nodesHost.querySelector('[data-uid="' + nodeEl.dataset.uid + '"]');
                if (again) again.focus();
            }
        }
    });

    // ---- the side panel ----
    function field(label, control, help) {
        return el('div', { class: 'mb-2' }, [
            el('label', { class: 'form-label small mb-1', text: label }), control, help ? el('div', { class: 'form-text', text: help }) : null,
        ]);
    }
    function input(obj, prop, attrs, onChange) {
        const node = el('input', Object.assign({ class: 'form-control form-control-sm', value: obj[prop] == null ? '' : obj[prop] }, attrs || {}));
        node.addEventListener('input', () => { obj[prop] = node.value; if (onChange) onChange(node); });
        return node;
    }
    function textarea(obj, prop, rows, attrs) {
        const node = el('textarea', Object.assign({ class: 'form-control form-control-sm font-monospace', rows: String(rows), spellcheck: 'false' }, attrs || {}));
        node.value = obj[prop] || '';
        node.addEventListener('input', () => { obj[prop] = node.value; });
        return node;
    }
    function select(options, current, onChange, emptyLabel) {
        const node = el('select', { class: 'form-select form-select-sm' });
        if (emptyLabel != null) node.appendChild(el('option', { value: '', text: emptyLabel }));
        options.forEach(([value, text]) => node.appendChild(el('option', { value: value, text: text })));
        node.value = current == null ? '' : current;
        if (current && node.value !== current) { // a value that is not an option (a deleted data source): keep it visible
            node.appendChild(el('option', { value: current, text: '(tidak ditemukan) ' + current }));
            node.value = current;
        }
        node.addEventListener('change', () => onChange(node.value));
        return node;
    }

    function refreshNode(step) {
        const nodeEl = nodesHost.querySelector('[data-uid="' + step.uid + '"]');
        if (!nodeEl) return;
        nodeEl.querySelector('.wf-title').textContent = labelOf(step);
        const code = nodeEl.querySelector('.wf-code');
        if (step.name && step.name.trim()) {
            if (code) code.textContent = step.code;
            else nodeEl.appendChild(el('span', { class: 'wf-code', text: step.code }));
        } else if (code) code.remove();
        nodeEl.setAttribute('aria-label', TYPES[step.type] + ': ' + labelOf(step));
    }

    function linkSelect(step, current, apply) {
        const options = steps.filter((s) => s !== step).map((s) => [s.uid, labelOf(s) + ' (' + s.code + ')']);
        return select(options, current, (uid) => {
            if (!uid) { apply(null); renderAll(); return; }
            if (reachable(uid).has(step.uid)) {
                $('check-status').textContent = 'Sambungan itu membuat alur berputar (loop), jadi ditolak.';
                renderPanel();
                return;
            }
            $('check-status').textContent = '';
            apply(uid);
            renderAll();
        }, '(selesai)');
    }


    // ---- the Javascript step: an Ace editor and "Uji skrip" ----
    let activeEditor = null;
    const isDark = () => document.documentElement.getAttribute('data-bs-theme') === 'dark';
    window.addEventListener('rakit-theme', () => {
        if (activeEditor) activeEditor.setTheme(isDark() ? 'ace/theme/tomorrow_night' : 'ace/theme/chrome');
    });
    function destroyEditor() {
        if (activeEditor) { activeEditor.destroy(); activeEditor = null; }
    }

    // sample data for "Uji skrip": remembered while the page is open, edited by the builder
    let sampleContext = JSON.stringify({ form: { Jumlah: '3' }, key: null, claims: { name: 'tester' }, now: '2026-01-01 10:00:00', steps: {} }, null, 2);

    function renderScriptPanel(s) {
        const host = el('div', { class: 'wf-script-editor', 'aria-label': 'Editor skrip' });
        panel.appendChild(field('Skrip', host,
            'Isi fungsi function (ctx) { ... }: baca ctx.form, ctx.key, ctx.claims, ctx.now, ctx.steps.kode.row.Kolom; kembalikan hasil dengan return. '
            + 'Hasilnya dibaca langkah lain sebagai {{kode.json.jalur}}. throw new Error("teks") menghentikan langkah dengan pesan itu. '
            + 'Berjalan di server dalam kotak pasir: tanpa akses file, jaringan, atau .NET, dengan batas waktu dan memori.'));

        if (typeof ace !== 'undefined') {
            ace.config.set('basePath', '/lib/ace');
            const editor = ace.edit(host);
            editor.session.setMode('ace/mode/javascript');
            editor.session.setUseWorker(false); // a top-level return is normal here; the syntax is judged by Periksa and Uji skrip
            editor.setOptions({ fontSize: 13, showPrintMargin: false, tabSize: 2, useSoftTabs: true });
            editor.setTheme(isDark() ? 'ace/theme/tomorrow_night' : 'ace/theme/chrome');
            editor.setValue(s.script.source || '', -1);
            editor.session.on('change', () => { s.script.source = editor.getValue(); });
            activeEditor = editor;
        } else {
            host.replaceWith(textarea(s.script, 'source', 12));
        }

        const context = el('textarea', { class: 'form-control form-control-sm font-monospace', rows: '6', spellcheck: 'false', 'aria-label': 'Data contoh ctx' });
        context.value = sampleContext;
        context.addEventListener('input', () => { sampleContext = context.value; });
        const result = el('div', { class: 'mt-2', 'aria-live': 'polite' });
        const run = el('button', { type: 'button', class: 'btn btn-sm btn-outline-primary', text: 'Uji skrip' });
        run.addEventListener('click', async () => {
            run.disabled = true;
            result.replaceChildren(el('span', { class: 'small text-muted', text: 'menjalankan...' }));
            try {
                const res = await fetch('/Builder/Workflow/TestScript', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: token() },
                    body: JSON.stringify({ source: s.script.source || '', context: context.value }),
                });
                let data = null;
                try { data = await res.json(); } catch (e) { /* non-JSON body */ }
                if (!res.ok || !data) {
                    result.replaceChildren(el('div', { class: 'alert alert-danger small mb-0', role: 'alert', text: (data && data.message) || 'Uji skrip gagal (HTTP ' + res.status + ').' }));
                } else if (data.ok) {
                    let shown = '(skrip tidak mengembalikan nilai)';
                    if (data.result != null) {
                        try { shown = JSON.stringify(JSON.parse(data.result), null, 2); } catch (e) { shown = data.result; }
                    }
                    result.replaceChildren(
                        el('div', { class: 'small text-success mb-1', text: 'Berhasil dalam ' + data.durationMs + ' ms. Hasil:' }),
                        el('pre', { class: 'wf-script-result border rounded p-2 mb-0', text: shown }));
                } else {
                    const lines = [data.kind === 'thrown' ? 'Skrip melempar galat: ' + data.message : 'Skrip gagal (' + data.kind + '): ' + (data.detail || data.message)];
                    result.replaceChildren(
                        el('div', { class: 'alert alert-danger small mb-0', role: 'alert', text: lines[0] + ' (' + data.durationMs + ' ms)' }),
                        data.kind === 'thrown' ? null : el('div', { class: 'form-text', text: 'Pengunjung hanya melihat pesan umum; detail ini hanya tampil di sini.' }));
                }
            } catch (e) {
                result.replaceChildren(el('div', { class: 'alert alert-danger small mb-0', text: 'Uji skrip gagal: server tidak dapat dihubungi.' }));
            } finally {
                run.disabled = false;
            }
        });
        const details = el('details', { class: 'mb-2' }, [
            el('summary', { class: 'small', text: 'Data contoh untuk uji (ctx, JSON)' }),
            context,
        ]);
        panel.appendChild(details);
        panel.appendChild(run);
        panel.appendChild(result);
    }

    // ---- the Action step: title, fields and buttons ----
    const FIELD_TYPES = [['text', 'Teks'], ['textarea', 'Teks panjang'], ['number', 'Angka'], ['date', 'Tanggal'], ['dateTime', 'Tanggal dan waktu'],
        ['boolean', 'Ya / Tidak'], ['select', 'Pilihan'], ['email', 'Email'], ['url', 'URL'], ['section', 'Judul seksi']];
    const BUTTON_STYLES = ['primary', 'secondary', 'success', 'danger', 'warning', 'info', 'dark'];
    const MAX_FIELDS = 20, MAX_BUTTONS = 4;

    const newField = () => ({ name: '', label: '', type: 'text', required: false, default: '', placeholder: '', help: '', colSize: '', min: '', max: '', step: '', maxLength: '', optionsText: '' });

    function check(obj, prop, label) {
        const id = 'chk-' + Math.random().toString(36).slice(2, 8);
        const box = el('input', { type: 'checkbox', class: 'form-check-input', id: id });
        box.checked = !!obj[prop];
        box.addEventListener('change', () => { obj[prop] = box.checked; });
        return el('div', { class: 'form-check' }, [box, el('label', { class: 'form-check-label small', for: id, text: label })]);
    }

    function moveItem(list, index, delta) {
        const to = index + delta;
        if (to < 0 || to >= list.length) return;
        [list[index], list[to]] = [list[to], list[index]];
        renderPanel();
    }

    function fieldCard(a, f, index) {
        const isSection = f.type === 'section';
        const typeSelect = select(FIELD_TYPES, f.type, (v) => { f.type = v; renderPanel(); });
        const actions = el('div', { class: 'btn-group btn-group-sm' });
        [['▲', 'Naik', () => moveItem(a.fields, index, -1)], ['▼', 'Turun', () => moveItem(a.fields, index, 1)],
            ['×', 'Hapus field', () => { a.fields.splice(index, 1); renderPanel(); }]].forEach(([text, title, run]) => {
            const b = el('button', { type: 'button', class: 'btn btn-outline-secondary', text: text, 'aria-label': title, title: title });
            b.addEventListener('click', run);
            actions.appendChild(b);
        });

        const more = el('details', { class: 'mt-1' }, [el('summary', { class: 'small', text: 'Lanjutan' })]);
        more.appendChild(field('Label', input(f, 'label', { maxlength: '200', placeholder: 'kosong = nama field' })));
        if (isSection) {
            more.appendChild(field('Teks di bawah judul', input(f, 'help', { maxlength: '300' })));
        } else {
            if (f.type !== 'boolean') more.appendChild(field('Placeholder', input(f, 'placeholder', { maxlength: '200' })));
            more.appendChild(field('Teks bantuan', input(f, 'help', { maxlength: '300' })));
            more.appendChild(field('Nilai awal', input(f, 'default', { maxlength: '500' })));
            more.appendChild(field('Ukuran kolom (1-12)', input(f, 'colSize', { type: 'number', min: '1', max: '12', placeholder: '12' })));
            if (['number', 'date', 'dateTime'].includes(f.type)) {
                more.appendChild(el('div', { class: 'row g-2' }, [
                    el('div', { class: 'col-6' }, [field('Min', input(f, 'min', { maxlength: '40' }))]),
                    el('div', { class: 'col-6' }, [field('Maks', input(f, 'max', { maxlength: '40' }))]),
                ]));
            }
            if (f.type === 'number') more.appendChild(field('Step', input(f, 'step', { maxlength: '20', placeholder: 'any' })));
            if (['text', 'textarea', 'email', 'url', 'select'].includes(f.type)) more.appendChild(field('Panjang maks', input(f, 'maxLength', { type: 'number', min: '1', max: '4000' })));
            if (f.type === 'select') more.appendChild(field('Opsi (satu per baris)', textarea(f, 'optionsText', 4, { placeholder: 'nilai|teks\ndua|Dua' }), 'Format nilai|teks; teks boleh dikosongkan.'));
        }

        return el('div', { class: 'wf-mini' }, [
            el('div', { class: 'd-flex gap-1 align-items-center mb-1' }, [
                el('div', { class: 'flex-grow-1' }, [input(f, 'name', { maxlength: '128', placeholder: 'NamaField', 'aria-label': 'Nama field' })]),
                actions,
            ]),
            el('div', { class: 'd-flex gap-2 align-items-center' }, [
                el('div', { class: 'flex-grow-1' }, [typeSelect]),
                isSection ? null : check(f, 'required', 'Wajib'),
            ]),
            more,
        ]);
    }

    function buttonCard(a, b, index) {
        const remove = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: '×', 'aria-label': 'Hapus tombol', title: 'Hapus tombol' });
        remove.addEventListener('click', () => { a.buttons.splice(index, 1); renderPanel(); });
        return el('div', { class: 'wf-mini' }, [
            el('div', { class: 'd-flex gap-1 mb-1' }, [
                el('div', { class: 'flex-grow-1' }, [input(b, 'code', { maxlength: '30', placeholder: 'kode', 'aria-label': 'Kode tombol' })]),
                el('div', { class: 'flex-grow-1' }, [input(b, 'label', { maxlength: '50', placeholder: 'Label', 'aria-label': 'Label tombol' })]),
                remove,
            ]),
            el('div', { class: 'd-flex gap-2 align-items-center' }, [
                el('div', { class: 'flex-grow-1' }, [select(BUTTON_STYLES.map((x) => [x, x]), b.style || 'primary', (v) => { b.style = v; })]),
                check(b, 'cancel', 'Batal (akhiri alur)'),
            ]),
        ]);
    }

    function renderActionPanel(s) {
        const a = s.action;
        panel.appendChild(field('Judul', input(a, 'title', { maxlength: '100', placeholder: 'Konfirmasi' }), 'Boleh memuat {{kunci}}.'));
        panel.appendChild(field('Teks pengantar (opsional)', textarea(a, 'intro', 2, { maxlength: '500' })));

        panel.appendChild(el('h4', { class: 'h6 mt-3', text: 'Field (' + a.fields.length + '/' + MAX_FIELDS + ')' }));
        a.fields.forEach((f, i) => panel.appendChild(fieldCard(a, f, i)));
        const addField = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: '+ Field' });
        addField.disabled = a.fields.length >= MAX_FIELDS;
        addField.addEventListener('click', () => { a.fields.push(newField()); renderPanel(); });
        panel.appendChild(addField);

        panel.appendChild(el('h4', { class: 'h6 mt-3', text: 'Tombol (' + a.buttons.length + '/' + MAX_BUTTONS + ')' }));
        a.buttons.forEach((b, i) => panel.appendChild(buttonCard(a, b, i)));
        const addButton = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: '+ Tombol' });
        addButton.disabled = a.buttons.length >= MAX_BUTTONS;
        addButton.addEventListener('click', () => { a.buttons.push({ code: '', label: '', style: 'primary', cancel: false }); renderPanel(); });
        panel.appendChild(addButton);

        panel.appendChild(el('p', { class: 'small text-muted mt-2 mb-0',
            text: 'Alur berhenti di sini dan pengunjung melihat form ini. Langkah sesudahnya membaca {{kode.button}} (kode tombol yang ditekan) dan '
                + '{{kode.form.NamaField}}. Untuk bercabang per tombol, pakai langkah Kondisi dengan {{kode.button}}. Tombol batal mengakhiri alur tanpa '
                + 'menjalankan langkah sesudahnya. Form menunggu paling lama 30 menit dan tidak bisa dipakai bersama opsi satu transaksi.' }));
    }

    function optionsFromText(text) {
        return (text || '').split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
            const cut = l.indexOf('|');
            return cut < 0 ? { value: l } : { value: l.slice(0, cut).trim(), text: l.slice(cut + 1).trim() || undefined };
        });
    }
    const optionsToText = (options) => (options || []).map((o) => (o.text && o.text !== o.value ? o.value + '|' + o.text : o.value)).join('\n');

    function actionFromServer(a) {
        const out = { title: a.title || '', intro: a.intro || '', fields: [], buttons: [] };
        (a.fields || []).forEach((f) => out.fields.push(Object.assign(newField(), {
            name: f.name || '', label: f.label || '', type: f.type || 'text', required: !!f.required, default: f.default || '',
            placeholder: f.placeholder || '', help: f.help || '', colSize: f.colSize == null ? '' : String(f.colSize), min: f.min || '', max: f.max || '',
            step: f.step || '', maxLength: f.maxLength == null ? '' : String(f.maxLength), optionsText: optionsToText(f.options),
        })));
        (a.buttons || []).forEach((b) => out.buttons.push({ code: b.code || '', label: b.label || '', style: b.style || 'primary', cancel: !!b.cancel }));
        return out;
    }

    function actionToServer(a) {
        const num = (v) => { const n = parseInt(v, 10); return Number.isFinite(n) ? n : undefined; };
        const out = { title: (a.title || '').trim(), fields: [], buttons: [] };
        if ((a.intro || '').trim()) out.intro = a.intro.trim();
        a.fields.forEach((f) => {
            const o = { name: (f.name || '').trim(), type: f.type };
            if ((f.label || '').trim()) o.label = f.label.trim();
            if (f.type === 'section') {
                if ((f.help || '').trim()) o.help = f.help.trim();
                out.fields.push(o);
                return;
            }
            if (f.required) o.required = true;
            if ((f.default || '').trim()) o.default = f.default.trim();
            if ((f.placeholder || '').trim() && f.type !== 'boolean') o.placeholder = f.placeholder.trim();
            if ((f.help || '').trim()) o.help = f.help.trim();
            if (num(f.colSize) !== undefined) o.colSize = num(f.colSize);
            if (['number', 'date', 'dateTime'].includes(f.type)) {
                if ((f.min || '').trim()) o.min = f.min.trim();
                if ((f.max || '').trim()) o.max = f.max.trim();
            }
            if (f.type === 'number' && (f.step || '').trim()) o.step = f.step.trim();
            if (['text', 'textarea', 'email', 'url', 'select'].includes(f.type) && num(f.maxLength) !== undefined) o.maxLength = num(f.maxLength);
            if (f.type === 'select') o.options = optionsFromText(f.optionsText);
            out.fields.push(o);
        });
        a.buttons.forEach((b) => {
            const o = { code: (b.code || '').trim(), label: (b.label || '').trim() };
            if (b.style && b.style !== 'primary') o.style = b.style;
            if (b.cancel) o.cancel = true;
            out.buttons.push(o);
        });
        return out;
    }

    function renderPanel() {
        destroyEditor();
        panel.replaceChildren();

        if (selectedEdge) {
            const from = selectedEdge.from === 'start' ? null : byUid(selectedEdge.from);
            const edge = edges().find((e) => e.from === selectedEdge.from && e.kind === selectedEdge.kind);
            const to = edge ? byUid(edge.to) : null;
            panel.appendChild(el('h3', { class: 'h6', text: 'Sambungan' }));
            panel.appendChild(el('p', { class: 'small', text: (from ? labelOf(from) : 'Mulai') + (selectedEdge.kind === 'yes' ? ' (Ya)' : selectedEdge.kind === 'no' ? ' (Tidak)' : '') + ' → ' + (to ? labelOf(to) : '?') }));
            if (selectedEdge.from === 'start') {
                panel.appendChild(el('p', { class: 'small text-muted', text: 'Langkah pertama diganti dengan menarik dari titik Mulai ke langkah lain, atau lewat tombol "Jadikan langkah pertama" pada sebuah langkah.' }));
            } else {
                const remove = el('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', text: 'Lepas sambungan' });
                remove.addEventListener('click', () => { clearLink(selectedEdge.from, selectedEdge.kind); selectedEdge = null; renderAll(); });
                panel.appendChild(remove);
            }
            return;
        }

        if (selected === 'start') {
            panel.appendChild(el('h3', { class: 'h6', text: 'Mulai' }));
            panel.appendChild(el('p', { class: 'small text-muted', text: 'Workflow berjalan dari langkah yang disambung ke sini.' }));
            panel.appendChild(field('Langkah pertama', select(steps.map((s) => [s.uid, labelOf(s) + ' (' + s.code + ')']), steps[0] && steps[0].uid, (uid) => setEntry(byUid(uid)))));
            return;
        }

        if (!selected) {
            panel.appendChild(el('h3', { class: 'h6', text: 'Panel langkah' }));
            panel.appendChild(el('p', { class: 'small text-muted', text: steps.length ? 'Pilih sebuah langkah untuk mengubah isinya.' : 'Belum ada langkah: tambahkan dari tombol di atas kanvas.' }));
            panel.appendChild(el('p', { class: 'small text-muted', text: HELP_KEYS }));
            return;
        }

        const s = selected;
        panel.appendChild(el('h3', { class: 'h6', text: TYPES[s.type] + (steps[0] === s ? ' (langkah pertama)' : '') }));

        const codeInput = input(s, 'code', { maxlength: '50', 'aria-label': 'Kode langkah' }, (node) => {
            s.code = node.value.trim();
            const dup = steps.some((o) => o !== s && o.code.toLowerCase() === s.code.toLowerCase());
            node.classList.toggle('is-invalid', !CODE_PATTERN.test(s.code) || dup);
            refreshNode(s);
        });
        codeInput.classList.toggle('is-invalid', !CODE_PATTERN.test(s.code));
        panel.appendChild(field('Kode', codeInput, 'Huruf, angka, _; dipakai di {{kode.…}} dan di riwayat.'));
        panel.appendChild(field('Nama (opsional)', input(s, 'name', { maxlength: '100', placeholder: 'tampil di pesan' }, () => refreshNode(s))));

        switch (s.type) {
            case 'dataSource':
                panel.appendChild(field('Data source', select(sources.map((o) => [o.id, o.label]), s.dataSourceId, (v) => { s.dataSourceId = v; }, '- pilih data source -'),
                    'Nilai parameter diatur di mapping data source-nya (FormField, RouteValue, StepOutput, …).'));
                break;
            case 'rest': {
                const r = s.rest;
                panel.appendChild(field('Method', select(['GET', 'POST', 'PUT', 'PATCH', 'DELETE'].map((m) => [m, m]), r.method, (v) => { r.method = v; })));
                panel.appendChild(field('URL', input(r, 'url', { maxlength: '2000', placeholder: 'https://api.contoh.com/pelanggan/{{key}}' }),
                    'Skema dan host harus ditulis lengkap dan terdaftar di Rakit:Workflow:AllowedHosts; {{…}} hanya di path/query.'));
                panel.appendChild(field('Header (satu per baris)', textarea(r, 'headersText', 3, { placeholder: 'Authorization: Bearer …\nX-Contoh: {{form.Nama}}' })));
                panel.appendChild(field('Body', textarea(r, 'body', 4, { placeholder: '{"nama": "{{form.Nama}}"}' }), 'Tidak dipakai oleh GET. Nilai {{…}} di-escape otomatis sesuai content type: JSON, form-urlencoded, atau XML (teks biasa apa adanya).'));
                panel.appendChild(field('Content type (opsional)', input(r, 'contentType', { placeholder: 'application/json' })));
                panel.appendChild(el('div', { class: 'row g-2' }, [
                    el('div', { class: 'col-6' }, [field('Timeout (detik)', input(r, 'timeoutSeconds', { type: 'number', min: '1', max: '60' }))]),
                    el('div', { class: 'col-6' }, [field('Coba ulang', input(r, 'retries', { type: 'number', min: '0', max: '3' }))]),
                ]));
                break;
            }
            case 'email': {
                const m = s.email;
                panel.appendChild(field('Kepada', input(m, 'to', { maxlength: '500', placeholder: '{{form.Email}}; admin@contoh.com' }), 'Pisahkan dengan koma atau titik koma (maks 10).'));
                panel.appendChild(field('Subjek', input(m, 'subject', { maxlength: '200' })));
                panel.appendChild(field('Isi', textarea(m, 'body', 6)));
                const html = el('input', { type: 'checkbox', class: 'form-check-input', id: 'email-html' });
                html.checked = !!m.isHtml;
                html.addEventListener('change', () => { m.isHtml = html.checked; });
                panel.appendChild(el('div', { class: 'form-check mb-2' }, [html, el('label', { class: 'form-check-label small', for: 'email-html', text: 'Isi berupa HTML (nilai di-encode, hasil disanitasi)' })]));
                panel.appendChild(el('p', { class: 'small text-muted', text: 'Tanpa Rakit:Mail (host dan pengirim) email hanya dicatat di log dan riwayat, tidak dikirim.' }));
                break;
            }
            case 'page':
                panel.appendChild(field('Isi halaman (HTML)', textarea(s.page, 'html', 8, { placeholder: '<h4>Terima kasih {{form.Nama}}</h4>' }),
                    'Ditampilkan di jendela setelah workflow selesai. HTML disanitasi; nilai {{…}} di-encode.'));
                break;
            case 'module':
                panel.appendChild(field('Module tujuan', select(moduleOptions.map((o) => [o.id, o.label]), s.module.moduleId, (v) => { s.module.moduleId = v; }, '- pilih module -')));
                panel.appendChild(field('Parameter (satu per baris)', textarea(s.module, 'paramsText', 3, { placeholder: 'id={{a.row.Id}}' }),
                    'Masuk ke alamat module sebagai query string (bisa dibaca data source lewat QueryString). Pengunjung dibawa ke sana setelah selesai.'));
                break;
            case 'condition': {
                const c = s.condition;
                const right = input(c, 'right', { maxlength: '500', placeholder: 'nilai pembanding' });
                const syncRight = () => { right.disabled = c.operator === 'empty' || c.operator === 'notEmpty'; };
                panel.appendChild(field('Nilai', input(c, 'left', { maxlength: '500', placeholder: '{{a.row.Status}}' }), 'Dua angka dibandingkan sebagai angka; selain itu sebagai teks (tanpa beda huruf besar).'));
                panel.appendChild(field('Operator', select(OPERATORS, c.operator, (v) => { c.operator = v; syncRight(); })));
                syncRight();
                panel.appendChild(field('Pembanding', right));
                break;
            }
            case 'javascript':
                renderScriptPanel(s);
                break;
            case 'action':
                renderActionPanel(s);
                break;
        }

        // links
        panel.appendChild(el('hr', { class: 'my-2' }));
        if (s.type === 'condition') {
            panel.appendChild(field('Jika ya, lanjut ke', linkSelect(s, s.condition.onTrue, (u) => { s.condition.onTrue = u; })));
            panel.appendChild(field('Jika tidak, lanjut ke', linkSelect(s, s.condition.onFalse, (u) => { s.condition.onFalse = u; })));
        } else {
            panel.appendChild(field('Berikutnya', linkSelect(s, s.next, (u) => { s.next = u; })));
        }

        const buttons = el('div', { class: 'd-flex gap-1 flex-wrap mt-2' });
        if (steps[0] !== s) {
            const first = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', text: 'Jadikan langkah pertama' });
            first.addEventListener('click', () => setEntry(s));
            buttons.appendChild(first);
        }
        const remove = el('button', { type: 'button', class: 'btn btn-sm btn-outline-danger', text: 'Hapus langkah' });
        remove.addEventListener('click', () => removeStep(s));
        buttons.appendChild(remove);
        panel.appendChild(buttons);
        if (['rest', 'email', 'page', 'module', 'condition', 'action'].includes(s.type)) panel.appendChild(el('p', { class: 'small text-muted mt-3 mb-0', text: HELP_KEYS }));
    }

    // ---- toolbar ----
    document.querySelectorAll('[data-add]').forEach((button) => button.addEventListener('click', () => addStep(button.dataset.add)));
    $('wf-layout').addEventListener('click', autoLayout);

    // ---- submit and check ----
    form.addEventListener('submit', () => { stepsInput.value = JSON.stringify(collect()); });

    function token() {
        const tokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
        return tokenInput ? tokenInput.value : '';
    }
    $('check-steps').addEventListener('click', async () => {
        const status = $('check-status');
        const result = $('check-result');
        status.textContent = 'memeriksa...';
        result.replaceChildren();
        try {
            const res = await fetch('/Builder/Workflow/Check', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', Accept: 'application/json', RequestVerificationToken: token() },
                body: JSON.stringify({ steps: collect(), transaction: $('Transaction').checked }),
            });
            let data = null;
            try { data = await res.json(); } catch (e) { /* non-JSON body */ }
            if (!res.ok || !data || !Array.isArray(data.issues)) {
                result.replaceChildren(el('div', { class: 'alert alert-danger mb-0', role: 'alert', text: (data && data.message) || 'Pemeriksaan gagal (HTTP ' + res.status + ').' }));
                return;
            }
            if (data.issues.length === 0) {
                result.replaceChildren(el('div', { class: 'alert alert-success mb-0', role: 'status', text: 'Tidak ada masalah ditemukan.' }));
                return;
            }
            const errors = data.issues.filter((i) => i.isError);
            const warnings = data.issues.filter((i) => !i.isError);
            const box = (kind, title, list) => el('div', { class: 'alert alert-' + kind + ' mb-2', role: kind === 'danger' ? 'alert' : 'status' }, [
                el('div', { class: 'fw-semibold', text: title }),
                el('ul', { class: 'mb-0 ps-3' }, list.map((i) => el('li', { text: i.message }))),
            ]);
            const parts = [];
            if (errors.length) parts.push(box('danger', errors.length + ' masalah yang menghalangi publish:', errors));
            if (warnings.length) parts.push(box('warning', warnings.length + ' peringatan:', warnings));
            result.replaceChildren(...parts);
        } catch (e) {
            result.replaceChildren(el('div', { class: 'alert alert-danger mb-0', text: 'Pemeriksaan gagal: server tidak dapat dihubungi.' }));
        } finally {
            status.textContent = '';
        }
    });

    // ---- start ----
    let loaded = [];
    try { loaded = JSON.parse(stepsInput.value || '[]') || []; } catch (e) { loaded = []; }
    steps = fromServer(loaded);
    if (steps.some((s) => s.x == null || s.y == null)) {
        steps.forEach((s) => { if (s.x == null) s.x = 0; if (s.y == null) s.y = 0; });
        autoLayout();
    } else {
        renderAll();
    }
})();

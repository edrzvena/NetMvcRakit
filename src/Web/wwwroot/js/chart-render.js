// Draws the data of a Chart or Widget module. Shared by the runtime page (visual-page.js) and the Builder preview, so what the
// builder previews is what the visitor gets. Input is the JSON of /object/page/{id}/data (Chart: kind, labels, series, stacked,
// horizontal, legend, title, truncated; Widget: value, text, label, color, refreshSeconds). Everything that comes from the
// database goes in through textContent or Chart.js options, never as HTML. Chart.js (lib/chart.js) is only needed by renderChart.
(function () {
    'use strict';

    // Categorical palette in its fixed order (series 1..8, never cycled). A series may bring its own #RRGGBB.
    const PALETTE = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6', '#e34948'];
    const SURFACE = '#ffffff';
    const INK = '#0b0b0b';
    const INK_SECONDARY = '#52514e';
    const GRID = '#e6e5e1';
    const WIDGET_COLORS = ['primary', 'success', 'warning', 'danger', 'info', 'secondary', 'dark'];
    const MAX_SLICES = 8; // a pie / doughnut shows 7 slices and folds the rest into one "Lainnya" slice
    const HEX = /^#[0-9a-fA-F]{6}$/;

    const number = new Intl.NumberFormat('id-ID', { maximumFractionDigits: 6 });
    const formatNumber = (v) => (v == null ? '—' : number.format(v));
    const instances = new WeakMap(); // host element -> its Chart.js instance (destroyed before a redraw)

    function el(tag, attrs, children) {
        const node = document.createElement(tag);
        Object.entries(attrs || {}).forEach(([k, v]) => {
            if (k === 'class') node.className = v;
            else if (k === 'text') node.textContent = v;
            else if (v === true) node.setAttribute(k, '');
            else if (v !== false && v != null) node.setAttribute(k, v);
        });
        (children || []).forEach((c) => node.appendChild(c));
        return node;
    }

    function release(host) {
        const old = instances.get(host);
        if (old) { old.destroy(); instances.delete(host); }
        host.replaceChildren();
    }

    // ---- Widget ----------------------------------------------------------------------------------------------

    function renderWidget(host, data) {
        release(host);
        const color = WIDGET_COLORS.includes(data.color) ? data.color : 'primary';
        const hasValue = data.text != null && data.text !== '';
        const body = el('div', { class: 'card-body' }, [
            el('div', { class: 'small', text: data.label || '' }),
            el('div', { class: 'display-5 fw-semibold lh-1 mt-1', text: hasValue ? data.text : '—' }),
        ]);
        if (!data.label) body.firstChild.classList.add('d-none');
        host.appendChild(el('div', { class: 'card text-bg-' + color + ' shadow-sm' }, [body]));
    }

    // ---- Chart -----------------------------------------------------------------------------------------------

    function colorOf(series, index) {
        return series.color && HEX.test(series.color) ? series.color : PALETTE[index % PALETTE.length];
    }

    // A pie reads badly past a handful of slices: keep the first 7 and add up the rest (the table view keeps every row).
    function foldSlices(labels, values) {
        if (labels.length <= MAX_SLICES) return { labels: labels, values: values };
        const keep = MAX_SLICES - 1;
        const rest = values.slice(keep).reduce((sum, v) => sum + (v == null ? 0 : v), 0);
        return { labels: labels.slice(0, keep).concat('Lainnya'), values: values.slice(0, keep).concat(rest) };
    }

    function buildTable(data) {
        const head = el('tr', {}, [el('th', { scope: 'col', text: '' })].concat(
            data.series.map((s) => el('th', { scope: 'col', class: 'text-end', text: s.label }))));
        const rows = data.labels.map((label, i) => el('tr', {}, [el('th', { scope: 'row', class: 'fw-normal', text: label })].concat(
            data.series.map((s) => el('td', { class: 'text-end', text: formatNumber(s.data[i]) })))));
        return el('div', { class: 'table-responsive', style: 'max-height: 320px' }, [
            el('table', { class: 'table table-sm table-striped mb-0' }, [el('thead', {}, [head]), el('tbody', {}, rows)]),
        ]);
    }

    function chartHeight(data) {
        if (data.kind === 'bar' && data.horizontal) {
            const per = data.stacked ? 30 : data.series.length * 22 + 10;
            return Math.min(Math.max(320, data.labels.length * per + 100), 2400);
        }
        return 360;
    }

    function renderChart(host, data) {
        release(host);
        if (!data.labels || data.labels.length === 0 || !data.series || data.series.length === 0) {
            host.appendChild(el('p', { class: 'text-muted mb-0', text: 'Tidak ada data untuk ditampilkan.' }));
            return;
        }
        if (!window.Chart) {
            host.appendChild(el('div', { class: 'alert alert-danger mb-0', role: 'alert', text: 'Pustaka grafik tidak termuat.' }));
            return;
        }

        const round = data.kind === 'pie' || data.kind === 'doughnut';
        const horizontal = data.kind === 'bar' && !!data.horizontal;
        const stacked = !round && !!data.stacked;
        const reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        const canvas = el('canvas', { role: 'img', 'aria-label': data.title || 'Grafik' });
        host.appendChild(el('div', { style: 'position: relative; height: ' + chartHeight(data) + 'px' }, [canvas]));

        let labels = data.labels;
        let datasets;
        if (round) {
            const folded = foldSlices(data.labels, data.series[0].data);
            labels = folded.labels;
            datasets = [{
                label: data.series[0].label,
                data: folded.values,
                backgroundColor: folded.labels.map((_, i) => PALETTE[i % PALETTE.length]),
                borderColor: SURFACE,
                borderWidth: 2,
            }];
        } else {
            const dense = data.labels.length > 60; // markers on every point of a long series are noise
            datasets = data.series.map((s, i) => {
                const color = colorOf(s, i);
                if (data.kind === 'bar') {
                    return {
                        label: s.label, data: s.data, backgroundColor: color,
                        borderColor: SURFACE, borderWidth: stacked ? 2 : 0,
                        borderRadius: stacked ? 0 : 4, borderSkipped: 'start',
                        categoryPercentage: 0.8, barPercentage: 0.9,
                    };
                }
                return {
                    label: s.label, data: s.data, borderColor: color, borderWidth: 2, tension: 0, spanGaps: false,
                    fill: data.kind === 'area', backgroundColor: data.kind === 'area' ? color + '33' : color,
                    pointRadius: dense ? 0 : 4, pointHoverRadius: 6, pointBackgroundColor: color,
                    pointBorderColor: SURFACE, pointBorderWidth: 2,
                };
            });
        }

        const showLegend = !!data.legend && (round || data.series.length > 1);
        // The value axis carries the (recessive) grid and the numbers; the category axis only its labels.
        const valueAxis = {
            beginAtZero: true, stacked: stacked, grid: { color: GRID }, border: { display: false },
            ticks: { color: INK_SECONDARY, callback: (v) => number.format(v) },
        };
        const categoryAxis = {
            stacked: stacked, grid: { display: false }, border: { display: false }, ticks: { color: INK_SECONDARY },
        };

        const chart = new window.Chart(canvas, {
            type: data.kind === 'area' ? 'line' : data.kind,
            data: { labels: labels, datasets: datasets },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: reduceMotion ? false : { duration: 300 },
                indexAxis: horizontal ? 'y' : 'x',
                cutout: data.kind === 'doughnut' ? '60%' : undefined,
                interaction: round ? undefined : { mode: 'index', intersect: false },
                plugins: {
                    legend: {
                        display: showLegend, position: 'bottom',
                        labels: { color: INK_SECONDARY, usePointStyle: true, boxWidth: 8, boxHeight: 8 },
                    },
                    title: {
                        display: !!data.title, text: data.title || '', align: 'start',
                        color: INK, font: { size: 14, weight: '600' }, padding: { bottom: 12 },
                    },
                    tooltip: {
                        callbacks: {
                            label: (ctx) => {
                                const raw = round ? ctx.raw : (horizontal ? ctx.parsed.x : ctx.parsed.y);
                                return (ctx.dataset.label ? ctx.dataset.label + ': ' : '') + formatNumber(raw);
                            },
                        },
                    },
                },
                scales: round ? {} : (horizontal ? { x: valueAxis, y: categoryAxis } : { x: categoryAxis, y: valueAxis }),
            },
        });
        instances.set(host, chart);

        if (data.truncated) {
            host.appendChild(el('div', { class: 'small text-muted mt-1', text: 'Hanya sebagian baris yang ditampilkan.' }));
        }

        // The same numbers as a table (screen readers, copying); built when it is opened so a big chart costs nothing extra.
        const details = el('details', { class: 'small mt-2' }, [el('summary', { class: 'text-secondary', text: 'Lihat sebagai tabel' })]);
        details.addEventListener('toggle', () => {
            if (details.open && details.children.length === 1) details.appendChild(buildTable(data));
        });
        host.appendChild(details);
    }

    window.RakitVisual = { renderChart: renderChart, renderWidget: renderWidget };
})();

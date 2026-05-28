'use strict';

(function () {
    let latestCharts = null;

    const C = {
        entitlement: 'rgba(139,148,158,0.35)',
        entitlementB: 'rgba(139,148,158,0.7)',
        used: '#f85149',
        usedB: '#b91c1c',
        remaining: '#2ea043',
        remainingB: '#196c2e',
        currLine: '#1f6feb',
        currFill: 'rgba(31,111,235,0.12)',
        prevLine: '#e3b341',
        prevFill: 'rgba(227,179,65,0.12)',
        pie: ['#1f6feb', '#e3b341', '#f85149', '#2ea043', '#a371f7', '#fd8c73', '#79c0ff', '#56d364', '#ffa657', '#8b949e'],
        pieRemain: 'rgba(46,160,67,0.3)',
        grid: 'rgba(255,255,255,0.07)',
        tick: '#8b949e',
        tooltip: '#1c2230'
    };

    const baseOpt = (unit = 'บาท') => ({
        responsive: true,
        maintainAspectRatio: false,
        animation: { duration: 650 },
        plugins: {
            legend: {
                labels: { color: C.tick, font: { family: 'Sarabun', size: 11 }, boxWidth: 11, padding: 10 }
            },
            tooltip: {
                backgroundColor: C.tooltip,
                titleColor: '#e6edf3',
                bodyColor: '#8b949e',
                borderColor: '#30363d',
                borderWidth: 1,
                padding: 10,
                callbacks: {
                    label: (ctx) => ` ${ctx.dataset.label ?? ctx.label}: ${Number(ctx.parsed.y ?? ctx.parsed).toLocaleString('th-TH')} ${unit}`
                }
            }
        },
        scales: {
            x: { ticks: { color: C.tick, font: { size: 11 } }, grid: { color: C.grid } },
            y: {
                beginAtZero: true,
                ticks: {
                    color: C.tick,
                    font: { size: 11 },
                    precision: 0,
                    callback: (v) => `${Number(v).toLocaleString('th-TH')} ${unit}`
                },
                grid: { color: C.grid }
            }
        }
    });

    function detectMedicalChartIntent(msg) {
        const t = (msg || '').toLowerCase();
        const hasChart = t.includes('กราฟ');
        const hasMedical = ['เบิก', 'รักษา', 'ค่ารักษา', 'medical', 'claim', 'โรงพยาบาล'].some((k) => t.includes(k));
        if (!hasChart || !hasMedical) return { show: false, year: null };

        const yearMatch = msg.match(/(\d{2,4})/);
        let year = null;
        if (yearMatch) {
            let raw = parseInt(yearMatch[1], 10);
            if (Number.isFinite(raw)) {
                if (raw < 100) raw += 2500;
                if (raw >= 2400) year = raw - 543;
                else if (raw >= 1900 && raw <= 2300) year = raw;
            }
        }

        return { show: true, year };
    }

    window.checkMedicalChartTrigger = function (msg) {
        const intent = detectMedicalChartIntent(msg);
        if (intent.show) {
            showMedicalChart(intent.year);
            return true;
        }
        return false;
    };

    window.showMedicalChart = async function (year = null) {
        const cfg = window.chatConfig;
        if (!cfg) return;

        const yrParam = year ? `?year=${year}` : '';
        const base = (cfg.apiBaseUrl ?? '/api/chat').replace('/api/chat', '');

        const wc = document.getElementById('welcomeCard');
        if (wc) wc.remove();

        const bubble = buildMedicalBubble();
        bubble.dataset.chartType = 'medical';
        if (year) bubble.dataset.chartYear = String(year);
        const msgs = document.getElementById('chatMessages');
        msgs.appendChild(bubble);
        msgs.scrollTop = msgs.scrollHeight;

        try {
            const [balRes, hospRes, mthRes] = await Promise.all([
                fetch(`${base}/api/medical-chart${yrParam}`),
                fetch(`${base}/api/medical-chart/hospital${yrParam}`),
                fetch(`${base}/api/medical-chart/monthly${yrParam}`)
            ]);

            if (!balRes.ok || !hospRes.ok || !mthRes.ok) throw new Error('API error');

            const bal = await balRes.json();
            const hosp = await hospRes.json();
            const mth = await mthRes.json();

            bubble.querySelector('.med-year-label').textContent = `📅 สรุปค่ารักษาพยาบาลปี ${bal.buddhistYear}`;

            const chartBar = renderBarChart(bubble, bal);
            const chartPie = renderPieChart(bubble, hosp);
            const chartLine = renderLineChart(bubble, mth);
            latestCharts = { chartBar, chartPie, chartLine };

            bubble.querySelector('.chart-loading').style.display = 'none';
            bubble.querySelector('.chart-actions').style.display = 'flex';
        } catch (e) {
            console.error('Medical chart error:', e);
            bubble.querySelector('.chart-loading').innerHTML = '<span style="color:#f85149">โหลดข้อมูลไม่สำเร็จ กรุณาลองใหม่</span>';
        }
    };

    function buildMedicalBubble() {
        const wrap = document.createElement('div');
        wrap.className = 'msg-wrap ai';
        wrap.innerHTML = `
            <div class="msg-header">
                <div class="msg-avatar ai-av"><i class="fa-solid fa-robot"></i></div>
                <span class="msg-sender">HR AI</span>
                <span class="msg-time">${nowTime()}</span>
            </div>
            <div class="msg-bubble chart-bubble" style="max-width:100%;width:100%;padding:1rem">
                <div class="chart-title-row">
                    <span class="chart-main-title med-year-label">📅 สรุปค่ารักษาพยาบาล</span>
                </div>

                <div class="chart-loading" style="padding:.8rem 0">
                    <span class="typing-dot"></span><span class="typing-dot"></span><span class="typing-dot"></span>
                    <span style="margin-left:.5rem;color:var(--text-muted);font-size:.85rem">กำลังโหลดข้อมูล...</span>
                </div>

                <div class="chart-section">
                    <div class="chart-section-label">วงเงินสิทธิ์ / เบิกแล้ว / คงเหลือ</div>
                    <div style="position:relative;height:180px"><canvas id="medBarChart"></canvas></div>
                </div>

                <div class="chart-section" style="margin-top:1.2rem">
                    <div class="chart-section-label">สัดส่วนการใช้จ่ายแยกสถานพยาบาล</div>
                    <div style="display:flex;gap:12px;align-items:center;flex-wrap:wrap">
                        <div style="position:relative;height:220px;flex:0 0 220px"><canvas id="medPieChart"></canvas></div>
                        <div id="pieDetailPanel" style="flex:1;min-width:180px;font-size:.82rem;color:var(--text-muted);line-height:1.9"></div>
                    </div>
                </div>

                <div class="chart-section" style="margin-top:1.2rem">
                    <div class="chart-section-label" id="lineChartLabel">ยอดเบิกค่ารักษารายเดือน เปรียบเทียบ 2 ปี</div>
                    <div style="position:relative;height:200px"><canvas id="medLineChart"></canvas></div>
                </div>

                <div class="chart-actions" style="display:none">
                    <button onclick="downloadMedicalPNG()" class="btn-chart-dl"><i class="fa-solid fa-image"></i> PNG</button>
                    <button onclick="downloadMedicalCSV()" class="btn-chart-dl"><i class="fa-solid fa-file-csv"></i> CSV</button>
                </div>
            </div>`;
        return wrap;
    }

    function renderBarChart(bubble, bal) {
        const ctx = bubble.querySelector('#medBarChart').getContext('2d');
        const maxValue = Math.max(
            Number(bal.entitlement || 0),
            Number(bal.used || 0),
            Number(bal.remaining || 0),
            1
        );
        const yAxisMax = Math.ceil((maxValue * 1.15) / 1000) * 1000;
        return new Chart(ctx, {
            type: 'bar',
            data: {
                labels: ['ค่ารักษาพยาบาล'],
                datasets: [
                    { label: 'วงเงินสิทธิ์ที่ได้รับ', data: [bal.entitlement], backgroundColor: C.entitlement, borderColor: C.entitlementB, borderWidth: 1, borderRadius: 5 },
                    { label: 'เบิกแล้ว', data: [bal.used], backgroundColor: C.used, borderColor: C.usedB, borderWidth: 1, borderRadius: 5 },
                    { label: 'คงเหลือ', data: [bal.remaining], backgroundColor: C.remaining, borderColor: C.remainingB, borderWidth: 1, borderRadius: 5 }
                ]
            },
            options: {
                ...baseOpt(),
                scales: {
                    ...baseOpt().scales,
                    y: {
                        ...baseOpt().scales.y,
                        max: yAxisMax
                    }
                }
            }
        });
    }

    function renderPieChart(bubble, hosp) {
        const ctx = bubble.querySelector('#medPieChart').getContext('2d');
        const bgColors = hosp.labels.map((_, i) => i === hosp.labels.length - 1 ? C.pieRemain : C.pie[i % C.pie.length]);

        const chartPie = new Chart(ctx, {
            type: 'doughnut',
            data: { labels: hosp.labels, datasets: [{ data: hosp.amounts, backgroundColor: bgColors, borderColor: '#0d1117', borderWidth: 2, hoverOffset: 6 }] },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                cutout: '55%',
                animation: { duration: 700 },
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: C.tooltip,
                        titleColor: '#e6edf3',
                        bodyColor: '#8b949e',
                        borderColor: '#30363d',
                        borderWidth: 1,
                        callbacks: { label: (ctx) => ` ${Number(ctx.parsed).toLocaleString('th-TH')} บาท` }
                    }
                }
            }
        });

        const panel = bubble.querySelector('#pieDetailPanel');
        const total = hosp.amounts.reduce((a, b) => a + b, 0) || 1;
        panel.innerHTML = hosp.labels.map((lbl, i) => {
            const amt = hosp.amounts[i];
            const pct = ((amt / total) * 100).toFixed(1);
            const isRemain = i === hosp.labels.length - 1;
            const color = isRemain ? C.remaining : C.pie[i % C.pie.length];
            return `<div style="display:flex;align-items:center;gap:6px;margin-bottom:2px">
                <span style="width:10px;height:10px;border-radius:2px;background:${color};flex-shrink:0"></span>
                <span style="color:${isRemain ? '#2ea043' : 'var(--text)'};flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap" title="${lbl}">${lbl}</span>
                <span style="color:var(--text-muted);white-space:nowrap">${Number(amt).toLocaleString('th-TH')} บ. (${pct}%)</span>
            </div>`;
        }).join('');
        return chartPie;
    }

    function renderLineChart(bubble, mth) {
        bubble.querySelector('#lineChartLabel').textContent = `ยอดเบิกค่ารักษารายเดือน - เปรียบเทียบ พ.ศ. ${mth.prevBuddhistYear} vs ${mth.buddhistYear}`;

        const ctx = bubble.querySelector('#medLineChart').getContext('2d');
        return new Chart(ctx, {
            type: 'line',
            data: {
                labels: mth.labels,
                datasets: [
                    { label: `พ.ศ. ${mth.prevBuddhistYear}`, data: mth.prevYear2, borderColor: C.prevLine, backgroundColor: C.prevFill, borderWidth: 2, pointRadius: 5, pointHoverRadius: 7, pointStyle: 'circle', tension: 0.35, fill: true },
                    { label: `พ.ศ. ${mth.buddhistYear}`, data: mth.currentYear, borderColor: C.currLine, backgroundColor: C.currFill, borderWidth: 2, pointRadius: 5, pointHoverRadius: 7, pointStyle: 'circle', tension: 0.35, fill: true }
                ]
            },
            options: baseOpt()
        });
    }

    window.downloadMedicalPNG = function () {
        const charts = latestCharts;
        if (!charts?.chartBar || !charts?.chartPie || !charts?.chartLine) return;
        const c1 = charts.chartBar.canvas;
        const c2 = charts.chartPie.canvas;
        const c3 = charts.chartLine.canvas;

        const pad = 20;
        const w = Math.max(c1.width, c3.width) + pad * 2;
        const h = c1.height + c2.height + c3.height + pad * 4 + 30;

        const out = document.createElement('canvas');
        out.width = w;
        out.height = h;
        const ctx = out.getContext('2d');

        ctx.fillStyle = '#0d1117';
        ctx.fillRect(0, 0, w, h);
        ctx.fillStyle = '#e6edf3';
        ctx.font = '13px Sarabun, sans-serif';
        ctx.fillText(`📅 สรุปค่ารักษาพยาบาล - ${todayTH()}`, pad, pad + 4);

        let y = pad + 20;
        ctx.drawImage(c1, pad, y); y += c1.height + pad;
        ctx.drawImage(c2, pad, y); y += c2.height + pad;
        ctx.drawImage(c3, pad, y);

        const a = document.createElement('a');
        a.href = out.toDataURL('image/png');
        a.download = `medical-chart-${today()}.png`;
        a.click();
    };

    window.downloadMedicalCSV = function () {
        const charts = latestCharts;
        if (!charts?.chartBar || !charts?.chartPie || !charts?.chartLine) return;
        const chartBar = charts.chartBar;
        const chartPie = charts.chartPie;
        const chartLine = charts.chartLine;

        const BOM = '\uFEFF';
        const rows = [];

        rows.push(['=== วงเงินค่ารักษาพยาบาล ===']);
        rows.push(['รายการ', 'จำนวนเงิน (บาท)']);
        chartBar.data.datasets.forEach((ds) => rows.push([ds.label, ds.data[0]]));

        rows.push([]);
        rows.push(['=== ยอดใช้จ่ายแยกสถานพยาบาล ===']);
        rows.push(['สถานพยาบาล', 'จำนวนเงิน (บาท)']);
        chartPie.data.labels.forEach((lbl, i) => rows.push([lbl, chartPie.data.datasets[0].data[i]]));

        rows.push([]);
        rows.push(['=== ยอดเบิกรายเดือน ===']);
        const [ds0, ds1] = chartLine.data.datasets;
        rows.push(['เดือน', ds0.label, ds1.label]);
        chartLine.data.labels.forEach((lbl, i) => rows.push([lbl, ds0.data[i], ds1.data[i]]));

        const csv = BOM + rows.map((r) => r.map((c) => `"${c}"`).join(',')).join('\r\n');

        const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = `medical-chart-${today()}.csv`;
        a.click();
        URL.revokeObjectURL(a.href);
    };

    function nowTime() {
        return new Date().toLocaleTimeString('th-TH', { hour: '2-digit', minute: '2-digit' });
    }

    function today() {
        return new Date().toISOString().slice(0, 10);
    }

    function todayTH() {
        return new Date().toLocaleDateString('th-TH', { year: 'numeric', month: 'long', day: 'numeric' });
    }
})();

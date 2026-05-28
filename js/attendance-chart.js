'use strict';

(function () {
    let latestAttendanceCharts = null;

    const C = {
        worked: '#2ea043',
        workedB: '#196c2e',
        single: '#e3b341',
        singleB: '#9a6a00',
        absent: '#f85149',
        absentB: '#b91c1c',
        currLine: '#1f6feb',
        currFill: 'rgba(31,111,235,0.12)',
        prevLine: '#a371f7',
        prevFill: 'rgba(163,113,247,0.12)',
        pie: ['#1f6feb', '#e3b341', '#2ea043', '#f85149', '#79c0ff', '#8b949e'],
        grid: 'rgba(255,255,255,0.07)',
        tick: '#8b949e',
        tooltip: '#1c2230'
    };

    const baseOpt = (unit = 'วัน') => ({
        responsive: true,
        maintainAspectRatio: false,
        animation: { duration: 650 },
        plugins: {
            legend: { labels: { color: C.tick, font: { family: 'Sarabun', size: 11 }, boxWidth: 11, padding: 10 } },
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
            y: { beginAtZero: true, ticks: { color: C.tick, font: { size: 11 }, precision: 0 }, grid: { color: C.grid } }
        }
    });

    window.showAttendanceChart = async function (year = null) {
        const cfg = window.chatConfig;
        if (!cfg) return;

        const yrParam = year ? `?year=${year}` : '';
        const base = (cfg.apiBaseUrl ?? '/api/chat').replace('/api/chat', '');

        const wc = document.getElementById('welcomeCard');
        if (wc) wc.remove();

        const bubble = buildAttendanceBubble();
        bubble.dataset.chartType = 'attendance';
        if (year) bubble.dataset.chartYear = String(year);
        const msgs = document.getElementById('chatMessages');
        msgs.appendChild(bubble);
        msgs.scrollTop = msgs.scrollHeight;

        try {
            const [sumRes, srcRes, mthRes] = await Promise.all([
                fetch(`${base}/api/attendance-chart${yrParam}`),
                fetch(`${base}/api/attendance-chart/source${yrParam}`),
                fetch(`${base}/api/attendance-chart/monthly${yrParam}`)
            ]);
            if (!sumRes.ok || !srcRes.ok || !mthRes.ok) throw new Error('API error');

            const sum = await sumRes.json();
            const src = await srcRes.json();
            const mth = await mthRes.json();

            bubble.querySelector('.att-year-label').textContent = `🕒 สรุปเวลาเข้า-ออกงานปี ${sum.buddhistYear}`;

            const chartBar = renderBarChart(bubble, sum);
            const chartPie = renderPieChart(bubble, src);
            const chartLine = renderLineChart(bubble, mth);
            latestAttendanceCharts = { chartBar, chartPie, chartLine };

            bubble.querySelector('.chart-loading').style.display = 'none';
            bubble.querySelector('.chart-actions').style.display = 'flex';
        } catch (e) {
            console.error('Attendance chart error:', e);
            bubble.querySelector('.chart-loading').innerHTML = '<span style="color:#f85149">โหลดข้อมูลไม่สำเร็จ กรุณาลองใหม่</span>';
        }
    };

    function buildAttendanceBubble() {
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
                    <span class="chart-main-title att-year-label">🕒 สรุปเวลาเข้า-ออกงาน</span>
                </div>
                <div class="chart-loading" style="padding:.8rem 0">
                    <span class="typing-dot"></span><span class="typing-dot"></span><span class="typing-dot"></span>
                    <span style="margin-left:.5rem;color:var(--text-muted);font-size:.85rem">กำลังโหลดข้อมูล...</span>
                </div>
                <div class="chart-section">
                    <div class="chart-section-label">วันมาทำงาน / วันสแกนครั้งเดียว / วันไม่พบสแกน</div>
                    <div style="position:relative;height:180px"><canvas id="attBarChart"></canvas></div>
                </div>
                <div class="chart-section" style="margin-top:1.2rem">
                    <div class="chart-section-label">สัดส่วนช่องทางเครื่องสแกน</div>
                    <div style="display:flex;gap:12px;align-items:center;flex-wrap:wrap">
                        <div style="position:relative;height:220px;flex:0 0 220px"><canvas id="attPieChart"></canvas></div>
                        <div id="attPieDetailPanel" style="flex:1;min-width:180px;font-size:.82rem;color:var(--text-muted);line-height:1.9"></div>
                    </div>
                </div>
                <div class="chart-section" style="margin-top:1.2rem">
                    <div class="chart-section-label" id="attLineLabel">จำนวนวันที่มีการสแกนรายเดือน เปรียบเทียบ 2 ปี</div>
                    <div style="position:relative;height:200px"><canvas id="attLineChart"></canvas></div>
                </div>
                <div class="chart-actions" style="display:none">
                    <button onclick="downloadAttendancePNG()" class="btn-chart-dl"><i class="fa-solid fa-image"></i> PNG</button>
                    <button onclick="downloadAttendanceCSV()" class="btn-chart-dl"><i class="fa-solid fa-file-csv"></i> CSV</button>
                </div>
            </div>`;
        return wrap;
    }

    function renderBarChart(bubble, sum) {
        const ctx = bubble.querySelector('#attBarChart').getContext('2d');
        const maxValue = Math.max(Number(sum.workedDays || 0), Number(sum.singleScanDays || 0), Number(sum.noScanDays || 0), 1);
        const yAxisMax = Math.ceil((maxValue * 1.15) / 10) * 10;

        return new Chart(ctx, {
            type: 'bar',
            data: {
                labels: ['วันทำงาน'],
                datasets: [
                    { label: 'มาทำงาน', data: [sum.workedDays], backgroundColor: C.worked, borderColor: C.workedB, borderWidth: 1, borderRadius: 5 },
                    { label: 'สแกนครั้งเดียว', data: [sum.singleScanDays], backgroundColor: C.single, borderColor: C.singleB, borderWidth: 1, borderRadius: 5 },
                    { label: 'ไม่พบสแกน', data: [sum.noScanDays], backgroundColor: C.absent, borderColor: C.absentB, borderWidth: 1, borderRadius: 5 }
                ]
            },
            options: {
                ...baseOpt('วัน'),
                scales: { ...baseOpt('วัน').scales, y: { ...baseOpt('วัน').scales.y, max: yAxisMax } }
            }
        });
    }

    function renderPieChart(bubble, src) {
        const ctx = bubble.querySelector('#attPieChart').getContext('2d');
        const chartPie = new Chart(ctx, {
            type: 'doughnut',
            data: {
                labels: src.labels,
                datasets: [{ data: src.amounts, backgroundColor: src.labels.map((_, i) => C.pie[i % C.pie.length]), borderColor: '#0d1117', borderWidth: 2, hoverOffset: 6 }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                cutout: '55%',
                animation: { duration: 700 },
                plugins: { legend: { display: false } }
            }
        });

        const panel = bubble.querySelector('#attPieDetailPanel');
        const total = src.amounts.reduce((a, b) => a + b, 0) || 1;
        panel.innerHTML = src.labels.map((lbl, i) => {
            const amt = src.amounts[i];
            const pct = ((amt / total) * 100).toFixed(1);
            return `<div style="display:flex;align-items:center;gap:6px;margin-bottom:2px">
                <span style="width:10px;height:10px;border-radius:2px;background:${C.pie[i % C.pie.length]};flex-shrink:0"></span>
                <span style="flex:1">${lbl}</span>
                <span style="white-space:nowrap">${Number(amt).toLocaleString('th-TH')} ครั้ง (${pct}%)</span>
            </div>`;
        }).join('');
        return chartPie;
    }

    function renderLineChart(bubble, mth) {
        bubble.querySelector('#attLineLabel').textContent = `จำนวนวันที่มีการสแกนรายเดือน - เปรียบเทียบ พ.ศ. ${mth.prevBuddhistYear} vs ${mth.buddhistYear}`;
        const ctx = bubble.querySelector('#attLineChart').getContext('2d');

        return new Chart(ctx, {
            type: 'line',
            data: {
                labels: mth.labels,
                datasets: [
                    { label: `พ.ศ. ${mth.prevBuddhistYear}`, data: mth.prevYear2, borderColor: C.prevLine, backgroundColor: C.prevFill, borderWidth: 2, pointRadius: 4, tension: 0.35, fill: true },
                    { label: `พ.ศ. ${mth.buddhistYear}`, data: mth.currentYear, borderColor: C.currLine, backgroundColor: C.currFill, borderWidth: 2, pointRadius: 4, tension: 0.35, fill: true }
                ]
            },
            options: baseOpt('วัน')
        });
    }

    window.downloadAttendancePNG = function () {
        const charts = latestAttendanceCharts;
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
        ctx.fillText(`🕒 สรุปเวลาเข้า-ออกงาน - ${todayTH()}`, pad, pad + 4);
        let y = pad + 20;
        ctx.drawImage(c1, pad, y); y += c1.height + pad;
        ctx.drawImage(c2, pad, y); y += c2.height + pad;
        ctx.drawImage(c3, pad, y);
        const a = document.createElement('a');
        a.href = out.toDataURL('image/png');
        a.download = `attendance-chart-${today()}.png`;
        a.click();
    };

    window.downloadAttendanceCSV = function () {
        const charts = latestAttendanceCharts;
        if (!charts?.chartBar || !charts?.chartPie || !charts?.chartLine) return;
        const rows = [];
        const BOM = '\uFEFF';
        rows.push(['=== สรุปวันทำงาน ===']);
        rows.push(['รายการ', 'จำนวน (วัน)']);
        charts.chartBar.data.datasets.forEach(ds => rows.push([ds.label, ds.data[0]]));
        rows.push([]);
        rows.push(['=== สัดส่วนช่องทางสแกน ===']);
        rows.push(['ช่องทาง', 'จำนวน (ครั้ง)']);
        charts.chartPie.data.labels.forEach((lbl, i) => rows.push([lbl, charts.chartPie.data.datasets[0].data[i]]));
        rows.push([]);
        rows.push(['=== รายเดือน ===']);
        const [ds0, ds1] = charts.chartLine.data.datasets;
        rows.push(['เดือน', ds0.label, ds1.label]);
        charts.chartLine.data.labels.forEach((lbl, i) => rows.push([lbl, ds0.data[i], ds1.data[i]]));
        const csv = BOM + rows.map(r => r.map(c => `"${c}"`).join(',')).join('\r\n');
        const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = `attendance-chart-${today()}.csv`;
        a.click();
        URL.revokeObjectURL(a.href);
    };

    function nowTime() { return new Date().toLocaleTimeString('th-TH', { hour: '2-digit', minute: '2-digit' }); }
    function today() { return new Date().toISOString().slice(0, 10); }
    function todayTH() { return new Date().toLocaleDateString('th-TH', { year: 'numeric', month: 'long', day: 'numeric' }); }
})();

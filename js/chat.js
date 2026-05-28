//           Chart Keyword Detection (Leave + Medical)
'use strict';

function initializeMobileSidebar() {
    const sidebar = document.getElementById('sidebar');
    const toggleBtn = document.getElementById('btnToggleSidebar');
    const chatMain = document.querySelector('.chat-main');
    if (!sidebar || !toggleBtn) return;

    const isMobile = () => window.innerWidth <= 768;

    sidebar.classList.toggle('collapsed', isMobile());

    toggleBtn.addEventListener('click', e => {
        e.stopPropagation();
        sidebar.classList.toggle('collapsed');
    });

    chatMain?.addEventListener('click', () => {
        if (isMobile() && !sidebar.classList.contains('collapsed'))
            sidebar.classList.add('collapsed');
    });

    // Swipe gesture
    let touchStartX = 0;
    document.addEventListener('touchstart', e => {
        touchStartX = e.changedTouches[0].screenX;
    }, { passive: true });

    document.addEventListener('touchend', e => {
        if (!isMobile()) return;
        const diff = e.changedTouches[0].screenX - touchStartX;
        if (Math.abs(diff) < 50) return;
        if (diff > 0 && touchStartX < 100) sidebar.classList.remove('collapsed');
        else if (diff < 0) sidebar.classList.add('collapsed');
    }, { passive: true });

    // Resize
    window.addEventListener('resize', () => {
        sidebar.classList.toggle('collapsed', isMobile());
    });
}

//  Chart Keyword Detection
const ChartDetector = {
    LEAVE_KEYWORDS: ['กราฟวันลา', 'กราฟการลา', 'กราฟลาพักร้อน', 'กราฟลาป่วย'],
    MEDICAL_KEYWORDS: [
        'กราฟการเบิกค่ารักษา', 'กราฟเบิกค่ารักษา', 'กราฟค่ารักษา', 'กราฟรักษา', 'กราฟการรักษา',
        'กราฟการเคลม', 'กราฟเคลมค่ารักษา', 'กราฟเบิก', 'กราฟการเบิก', 'กราฟโรงพยาบาล',
        'กราฟค่าพยาบาล', 'กราฟค่ายา', 'กราฟค่าหมอ', 'กราฟประกันสุขภาพ',
        'medical chart', 'medical claim chart', 'claim chart'
    ],
    ATTENDANCE_KEYWORDS: [
        'กราฟเข้างาน', 'กราฟออกงาน', 'กราฟเวลาเข้าออกงาน', 'กราฟลงเวลา',
        'กราฟตอกบัตร', 'กราฟสแกนหน้า', 'กราฟ attendance', 'attendance chart'
    ],

    resolveYear(text) {
        const m = (text || '').match(/(\d{2,4})/);
        if (!m) return null;

        let raw = parseInt(m[1], 10);
        if (!Number.isFinite(raw)) return null;

        if (raw < 100) raw += 2500;      // 68 => 2568
        if (raw >= 2400) return raw - 543; // พ.ศ. => ค.ศ.
        if (raw >= 1900 && raw <= 2300) return raw; // ค.ศ.
        return null;
    },

    detect(msg) {
        const t = (msg || '').toLowerCase();

        if (this.LEAVE_KEYWORDS.some(k => t.includes(k.toLowerCase()))) {
            return { type: 'leave', year: this.resolveYear(msg) };
        }

        const hasChart = t.includes('กราฟ') || t.includes('chart') || t.includes('graph');
        const hasMedical = [
            'เบิก', 'รักษา', 'ค่ารักษา', 'เคลม', 'ค่าพยาบาล', 'ค่ายา', 'ค่าหมอ', 'โรงพยาบาล',
            'ประกันสุขภาพ', 'medical', 'claim', 'hospital'
        ].some(k => t.includes(k));

        if (hasChart && hasMedical) {
            return { type: 'medical', year: this.resolveYear(msg) };
        }

        const hasAttendance = [
            'เข้างาน', 'ออกงาน', 'ลงเวลา', 'ตอกบัตร', 'สแกนหน้า', 'attendance', 'timescan'
        ].some(k => t.includes(k));
        if (hasChart && hasAttendance) {
            return { type: 'attendance', year: this.resolveYear(msg) };
        }

        return null;
    },

    run(msg) {
        const intent = this.detect(msg);
        if (!intent) return false;

        if (intent.type === 'leave') {
            if (typeof window.showLeaveChart === 'function') window.showLeaveChart(intent.year);
        } else if (intent.type === 'attendance') {
            if (typeof window.showAttendanceChart === 'function') window.showAttendanceChart(intent.year);
        } else {
            if (typeof window.showMedicalChart === 'function') window.showMedicalChart(intent.year);
        }

        return true;
    }
};

//  Main Application
document.addEventListener('DOMContentLoaded', () => {
    initializeMobileSidebar();

    const $ = id => document.getElementById(id);
    const chatMessages = $('chatMessages');
    const messageInput = $('messageInput');
    const sendBtn = $('sendBtn');
    const sendBtnIcon = $('sendBtnIcon');
    const voicePanel = $('voicePanel');
    const voicePanelText = $('voicePanelText');
    const voiceCancelBtn = $('voiceCancelBtn');
    const voiceHoldBtn = $('voiceHoldBtn');
    const statusDot = $('statusDot');
    const statusText = $('statusText');
    const connBadge = $('connBadge');
    const toastBox = $('toastBox');
    const welcomeCard = $('welcomeCard');
    const btnNewChat = $('btnNewChat');

    const cfg = window.chatConfig || {
        hubUrl: '/chathub',
        apiBaseUrl: '/api/chat',
        employeeId: 'EMP001',
        employeeName: 'พนักงาน',
        sessionId: '00000000-0000-0000-0000-000000000000'
    };

    const appBasePath = window.location.pathname.replace(/\/Chat$/i, '');

    let connection = null;
    let isConnected = false;
    let isStreaming = false;
    let isVoiceOpen = false;
    let isRecording = false;
    let voiceText = '';
    let voiceEndRes = null;   // promise resolver for recognition.onend

    const SpeechCtor = window.SpeechRecognition || window.webkitSpeechRecognition || null;
    let recognition = null;

    if (SpeechCtor) {
        recognition = new SpeechCtor();
        recognition.lang = 'th-TH';
        recognition.continuous = true;
        recognition.interimResults = true;

        recognition.onresult = e => {
            voiceText = Array.from(e.results)
                .map(r => r[0].transcript)
                .join(' ')
                .trim();
        };
        recognition.onerror = () => {
            isRecording = false;
            syncVoiceUI();
        };
        recognition.onend = () => {
            isRecording = false;
            voiceEndRes?.();
            voiceEndRes = null;
            syncVoiceUI();
        };
    }

    //  SignalR
    function getHubUrl() {
        if (cfg.hubUrl) return cfg.hubUrl;
        const p = window.location.pathname;
        return p.substring(0, p.lastIndexOf('/')) + '/chathub';
    }

    async function initConnection() {
        try {
            connection = new signalR.HubConnectionBuilder()
                .withUrl(getHubUrl())
                .withAutomaticReconnect([0, 2000, 5000, 10000])
                .configureLogging(signalR.LogLevel.Warning)
                .build();

            connection.on('ReceiveChunk', onChunk);
            connection.on('ReceiveComplete', onComplete);
            connection.on('ReceiveStatus', onStatus);
            connection.on('ReceiveError', onError);

            connection.onreconnecting(() => setConnStatus(false, 'กำลังเชื่อมต่อใหม่...'));
            connection.onreconnected(() => {
                setConnStatus(true, 'เชื่อมต่อแล้ว');
                toast('เชื่อมต่อใหม่สำเร็จ', 'success');
            });
            connection.onclose(() => setConnStatus(false, 'การเชื่อมต่อขาดหาย'));

            await connection.start();
            setConnStatus(true, 'พร้อมใช้งาน');

            const validSession = cfg.sessionId &&
                cfg.sessionId !== '00000000-0000-0000-0000-000000000000';
            if (validSession) await loadSessionMessages();

        } catch (err) {
            console.error('SignalR init failed:', err);
            setConnStatus(false, 'ออฟไลน์ (กำลังลองใหม่)');
            setTimeout(initConnection, 5000);
        }
    }

    async function loadSessionMessages() {
        try {
            const res = await fetch(`${cfg.apiBaseUrl}/sessions/${cfg.sessionId}/messages`);
            if (!res.ok) return;
            const msgs = await res.json();
            if (!msgs?.length) return;
            hideWelcome();
            for (const m of msgs) {
                if (m.role === 'assistant' && renderChartMessageIfAny(m.content)) {
                    continue;
                }
                appendMsg(m.role, m.content, m.createdAt);
            }
            scrollBottom();
        } catch (e) { console.error('Load session failed:', e); }
    }

    function renderChartMessageIfAny(content) {
        if (!content || typeof content !== 'string') return false;

        const medicalTag = content.match(/^\[MEDICAL_CHART(?::(\d{4}))?\]$/);
        if (medicalTag) {
            const y = medicalTag[1] ? parseInt(medicalTag[1], 10) : null;
            if (typeof window.showMedicalChart === 'function') {
                window.showMedicalChart(Number.isFinite(y) ? y : null);
                return true;
            }
        }
        const attendanceTag = content.match(/^\[ATTENDANCE_CHART(?::(\d{4}))?\]$/);
        if (attendanceTag) {
            const y = attendanceTag[1] ? parseInt(attendanceTag[1], 10) : null;
            if (typeof window.showAttendanceChart === 'function') {
                window.showAttendanceChart(Number.isFinite(y) ? y : null);
                return true;
            }
        }

        if (runAssistantActionIfAny(content, false)) {
            return true;
        }

        return false;
    }

    async function sendProgrammaticUserMessage(msg) {
        if (!msg || !isConnected || isStreaming) return;
        isStreaming = true;
        syncVoiceUI();
        hideWelcome();
        appendMsg('user', msg, new Date());
        try {
            await saveMsg('user', msg);
            await connection.invoke('SendMessageStreaming', cfg.employeeId, msg);
        } catch (e) {
            console.error('Programmatic send failed:', e);
            toast('ส่งคำสั่งอัตโนมัติไม่สำเร็จ', 'error');
            isStreaming = false;
            removeTyping();
            syncVoiceUI();
        }
    }

    function runAssistantActionIfAny(content, allowSideEffects = true) {
        const t = (content || '').trim();
        
        const medicalTag = t.match(/^\[MEDICAL_CHART(?::(\d{4}))?\]$/);
        if (medicalTag) {
            const y = medicalTag[1] ? parseInt(medicalTag[1], 10) : null;
            if (typeof window.showMedicalChart === 'function') window.showMedicalChart(Number.isFinite(y) ? y : null);
            return true;
        }

        const attendanceTag = t.match(/^\[ATTENDANCE_CHART(?::(\d{4}))?\]$/);
        if (attendanceTag) {
            const y = attendanceTag[1] ? parseInt(attendanceTag[1], 10) : null;
            if (typeof window.showAttendanceChart === 'function') window.showAttendanceChart(Number.isFinite(y) ? y : null);
            return true;
        }

        if (t === '[ACTION:SHOW_MEDICAL_CHART_CURRENT]') {
            if (typeof window.showMedicalChart === 'function') window.showMedicalChart();
            return true;
        }
        if (t === '[ACTION:SHOW_ATTENDANCE_CHART_CURRENT]') {
            if (typeof window.showAttendanceChart === 'function') window.showAttendanceChart();
            return true;
        }
        if (t === '[ACTION:RUN_CLAIM_STATUS]') {
            if (allowSideEffects) void sendProgrammaticUserMessage('สถานะเคลมค่ารักษา');
            return true;
        }
        if (t === '[ACTION:FALLBACK]') {
            if (allowSideEffects) {
                appendMsg('assistant', 'ขออภัยครับ กรุณาเลือกตัวเลขที่กำหนด หรือพิมพ์คำถามใหม่อีกครั้ง', new Date());
            }
            return true;
        }
        return false;
    }

    function onChunk(chunk) {
        let last = chatMessages?.querySelector('.msg-wrap.ai:last-child');
        if (!last || last.dataset.streaming !== 'true') {
            hideWelcome();
            last = appendMsg('assistant', '', new Date(), true);
        }
        const bubble = last?.querySelector('.msg-bubble');
        if (!bubble) return;
        const raw = bubble.dataset.rawText || '';
        bubble.dataset.rawText = raw + chunk;
        bubble.innerHTML = fmtMarkdown(raw + chunk);
        scrollBottom();
    }

    async function onComplete() {
        const last = chatMessages?.querySelector('.msg-wrap.ai:last-child');
        if (last?.dataset.streaming === 'true') {
            last.removeAttribute('data-streaming');
            const content = last.querySelector('.msg-bubble')?.dataset.rawText || '';
            if (content) {
                if (runAssistantActionIfAny(content, true)) {
                    last.remove();
                    await saveMsg('assistant', content);
                } else {
                    await saveMsg('assistant', content);
                }
            }
        }
        isStreaming = false;
        removeTyping();
        syncVoiceUI();
    }

    function onStatus(s) {
        s === 'typing' ? showTyping() : removeTyping();
    }
    function onError(e) {
        removeTyping();
        toast(e || 'เกิดข้อผิดพลาด', 'error');
        isStreaming = false;
        syncVoiceUI();
    }

    async function sendMessage() {
        const msg = messageInput?.value.trim();
        if (!msg || !isConnected || isStreaming) return;

        if (ChartDetector.run(msg)) {
            hideWelcome();
            appendMsg('user', msg, new Date());
            await saveMsg('user', msg);
            const intent = ChartDetector.detect(msg);
            if (intent?.type === 'medical') {
                const tag = intent.year ? `[MEDICAL_CHART:${intent.year}]` : '[MEDICAL_CHART]';
                await saveMsg('assistant', tag);
            } else if (intent?.type === 'attendance') {
                const tag = intent.year ? `[ATTENDANCE_CHART:${intent.year}]` : '[ATTENDANCE_CHART]';
                await saveMsg('assistant', tag);
            }
            messageInput.value = '';
            messageInput.style.height = 'auto';
            syncVoiceUI();
            return;
        }

        isStreaming = true;
        syncVoiceUI();
        hideWelcome();
        appendMsg('user', msg, new Date());
        messageInput.value = '';
        messageInput.style.height = 'auto';
        syncVoiceUI();

        try {
            await saveMsg('user', msg);
            await connection.invoke('SendMessageStreaming', cfg.employeeId, msg);
        } catch (e) {
            console.error('Send failed:', e);
            toast('ส่งข้อความขัดข้อง', 'error');
            isStreaming = false;
            removeTyping();
            syncVoiceUI();
        }
    }

    async function saveMsg(role, content) {
        try {
            await fetch(`${cfg.apiBaseUrl}/sessions/${cfg.sessionId}/messages`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ employeeId: cfg.employeeId, role, content })
            });
        } catch (e) { console.warn('Save msg failed:', e); }
    }

    //  UI Helpers
    function appendMsg(role, content, timestamp, streaming = false) {
        if (!chatMessages) return null;
        const isUser = role === 'user';
        const wrap = document.createElement('div');
        wrap.className = `msg-wrap ${isUser ? 'user' : 'ai'}`;
        if (streaming) wrap.dataset.streaming = 'true';

        const time = timestamp
            ? new Date(timestamp).toLocaleTimeString('th-TH',
                { hour: '2-digit', minute: '2-digit' })
            : '';
        const icon = isUser ? 'fa-user' : 'fa-robot';
        const avClass = isUser ? 'user-av' : 'ai-av';
        const sender = isUser ? (cfg.employeeName || 'คุณ') : 'HR AI';

        wrap.innerHTML = `
            <div class="msg-header">
                <div class="msg-avatar ${avClass}">
                    <i class="fa-solid ${icon}"></i>
                </div>
                <span class="msg-sender">${sender}</span>
                <span class="msg-time">${time}</span>
            </div>
            <div class="msg-bubble"
                 data-raw-text="${escHtml(content)}">${fmtMarkdown(content)}</div>`;

        chatMessages.appendChild(wrap);
        scrollBottom();
        return wrap;
    }

    function showTyping() {
        if ($('typingIndicatorElement') || !chatMessages) return;
        const el = document.createElement('div');
        el.className = 'msg-wrap ai';
        el.id = 'typingIndicatorElement';
        el.innerHTML = `
            <div class="msg-header">
                <div class="msg-avatar ai-av">
                    <i class="fa-solid fa-robot"></i>
                </div>
                <span class="msg-sender">HR AI</span>
            </div>
            <div class="typing-bubble">
                <span class="typing-dot"></span>
                <span class="typing-dot"></span>
                <span class="typing-dot"></span>
                <span class="typing-text">กำลังพิมพ์...</span>
            </div>`;
        chatMessages.appendChild(el);
        scrollBottom();
    }

    function removeTyping() { $('typingIndicatorElement')?.remove(); }
    function hideWelcome() {
        if (welcomeCard && welcomeCard.style.display !== 'none')
            welcomeCard.style.display = 'none';
    }
    function scrollBottom() { if (chatMessages) chatMessages.scrollTop = chatMessages.scrollHeight; }

    function setConnStatus(ok, txt) {
        isConnected = ok;
        if (statusText) statusText.textContent = txt;
        if (statusDot) statusDot.style.background = ok ? 'var(--primary,#238636)' : 'var(--danger,#f85149)';
        if (connBadge) {
            connBadge.textContent = ok ? 'ออนไลน์' : 'ออฟไลน์';
            connBadge.classList.toggle('disconnected', !ok);
        }
        syncVoiceUI();
    }

    function toast(msg, type = 'info') {
        if (!toastBox) return;
        toastBox.textContent = msg;
        toastBox.className = `toast-box ${type} show`;
        clearTimeout(toastBox._t);
        toastBox._t = setTimeout(() => toastBox.classList.remove('show'), 4000);
    }

    function escHtml(t) {
        return t
            ? t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;').replace(/'/g, '&#039;')
            : '';
    }
    function fmtMarkdown(t) {
        if (!t) return '';
        return escHtml(t)
            .replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>')
            .replace(/`([^`]+)`/g, '<code>$1</code>')
            .replace(/\n/g, '<br>');
    }

    //  Voice UI
    function syncVoiceUI() {
        if (!sendBtn || !sendBtnIcon || !messageInput) return;
        const hasText = !!messageInput.value.trim();
        const voiceMode = !hasText;

        sendBtn.disabled = !isConnected || isStreaming;
        sendBtn.classList.toggle('voice-mode', voiceMode);
        sendBtn.classList.toggle('recording', isRecording);

        sendBtnIcon.className = voiceMode
            ? `fa-solid ${isRecording ? 'fa-stop' : 'fa-microphone'}`
            : 'fa-solid fa-paper-plane';
        sendBtn.title = voiceMode ? 'ไมโครโฟน' : 'ส่ง';

        if (voicePanel) voicePanel.classList.toggle('active', isVoiceOpen);
        if (voicePanelText) {
            voicePanelText.textContent = isRecording
                ? 'กำลังฟัง... ปล่อยเพื่อส่งข้อความ'
                : 'แตะเพื่อบันทึกข้อความเสียง';
        }
    }

    function openVoice() {
        if (!SpeechCtor || !isConnected || isStreaming) return;
        isVoiceOpen = true;
        syncVoiceUI();
    }
    function closeVoice() {
        if (isRecording) try { recognition?.stop(); } catch (_) { }
        isRecording = false;
        isVoiceOpen = false;
        voiceText = '';
        syncVoiceUI();
    }
    function startRecording() {
        if (!recognition || !isVoiceOpen || isRecording || isStreaming) return;
        voiceText = '';
        try { recognition.start(); isRecording = true; syncVoiceUI(); } catch (_) { }
    }
    async function stopRecordingAndSend() {
        if (!isRecording) return;
        isRecording = false;
        await new Promise(res => {
            voiceEndRes = res;
            setTimeout(res, 700);
            try { recognition?.stop(); } catch (_) { }
        });
        syncVoiceUI();
        if (!voiceText) return;
        messageInput.value = voiceText;
        messageInput.dispatchEvent(new Event('input'));
        await sendMessage();
        closeVoice();
    }

    //  Session Management
    btnNewChat?.addEventListener('click', async () => {
        try {
            toast('กำลังสร้างห้องแชตใหม่...', 'info');
            const res = await fetch(`${cfg.apiBaseUrl}/sessions`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ employeeId: cfg.employeeId })
            });
            if (res.ok) {
                const data = await res.json();
                window.location.href = `?sessionId=${data.sessionId}`;
            } else {
                toast('ไม่สามารถสร้างห้องแชตได้', 'error');
            }
        } catch (e) {
            console.error('New session error:', e);
            toast('เกิดข้อผิดพลาดในการสร้างเซสชัน', 'error');
        }
    });

    window.quickAsk = (t) => {
        if (!messageInput || isStreaming || !isConnected) return;
        messageInput.value = t;
        messageInput.dispatchEvent(new Event('input'));
        messageInput.focus();
        setTimeout(sendMessage, 50);
    };

    window.requestMedicalChartFromQuickButton = async () => {
        if (isStreaming) return;
        hideWelcome();
        appendMsg('user', 'กราฟค่ารักษา', new Date());
        await saveMsg('user', 'กราฟค่ารักษา');
        await saveMsg('assistant', '[MEDICAL_CHART]');
        if (typeof window.showMedicalChart === 'function') {
            window.showMedicalChart();
        }
    };

    window.requestAttendanceChartFromQuickButton = async () => {
        if (isStreaming) return;
        hideWelcome();
        appendMsg('user', 'กราฟเวลาเข้าออกงาน', new Date());
        await saveMsg('user', 'กราฟเวลาเข้าออกงาน');
        await saveMsg('assistant', '[ATTENDANCE_CHART]');
        if (typeof window.showAttendanceChart === 'function') {
            window.showAttendanceChart();
        }
    };

    window.loadSession = id => window.location.href = `?sessionId=${id}`;

    window.deleteSession = async (ev, id) => {
        ev.stopPropagation();
        if (!confirm('ยืนยันลบห้องสนทนานี้?')) return;
        try {
            const res = await fetch(`${cfg.apiBaseUrl}/sessions/${id}`, { method: 'DELETE' });
            if (res.ok) {
                toast('ลบเรียบร้อย', 'success');
                window.location.href = id === cfg.sessionId
                    ? `${appBasePath}/Chat`
                    : window.location.href;
            } else toast('ลบไม่สำเร็จ', 'error');
        } catch { toast('เกิดข้อผิดพลาด', 'error'); }
    };

    messageInput?.addEventListener('input', function () {
        this.style.height = 'auto';
        this.style.height = (this.scrollHeight - 4) + 'px';
        if (this.value.trim() && isVoiceOpen) closeVoice();
        syncVoiceUI();
    });

    messageInput?.addEventListener('keydown', e => {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            if (!isStreaming && isConnected) sendMessage();
        }
    });

    sendBtn?.addEventListener('click', () => {
        if (!messageInput) return;
        if (messageInput.value.trim()) { sendMessage(); return; }
        if (isVoiceOpen) return;
        openVoice();
    });

    voiceCancelBtn?.addEventListener('click', closeVoice);

    if (voiceHoldBtn) {
        const stopHold = () => stopRecordingAndSend();
        voiceHoldBtn.addEventListener('pointerdown', e => { e.preventDefault(); startRecording(); });
        voiceHoldBtn.addEventListener('pointerup', stopHold);
        voiceHoldBtn.addEventListener('pointerleave', stopHold);
        voiceHoldBtn.addEventListener('pointercancel', stopHold);
    }

    window.addEventListener('focus', async () => {
        if (!isConnected && connection) {
            try {
                await connection.start();
                setConnStatus(true, 'พร้อมใช้งาน');
            } catch { /* reconnect handled by withAutomaticReconnect */ }
        }
    });

    initConnection();
    syncVoiceUI();
});


const API = '/api/ApiWorkProgress';
let currentData = null;
let currentImage = null;
let html5QrCode = null;
let cameraStream = null;
let colorProfileColors = { normal: [], ot: [] };

document.addEventListener('DOMContentLoaded', () => {
    hideLoading();
    initEvents();
});

function hideLoading() {
    const el = document.getElementById('loading-overlay');
    el.classList.add('hidden');
    setTimeout(() => el.style.display = 'none', 300);
}

function showLoading() {
    const el = document.getElementById('loading-overlay');
    el.style.display = 'flex';
    el.classList.remove('hidden');
}

function initEvents() {
    const input = document.getElementById('barcodeInput');
    const btnScan = document.getElementById('btnScan');

    input.addEventListener('keydown', e => {
        if (e.key === 'Enter') {
            e.preventDefault();
            doScan();
        }
    });
    btnScan.addEventListener('click', doScan);

    document.getElementById('btnCameraScan').addEventListener('click', startBarcodeScanner);
    document.getElementById('btnStopScan').addEventListener('click', stopBarcodeScanner);

    document.getElementById('btnHistory').addEventListener('click', showHistory);
    document.getElementById('btnUpdate').addEventListener('click', showUploadSection);
    initHistoryFilter();

    document.getElementById('btnTakePhoto').addEventListener('click', openLiveCamera);
    document.getElementById('btnChooseFile').addEventListener('click', () => {
        document.getElementById('fileGalleryInput').click();
    });
    document.getElementById('btnRetake').addEventListener('click', () => {
        resetUpload();
    });
    document.getElementById('btnCapture').addEventListener('click', capturePhoto);
    document.getElementById('btnCloseCam').addEventListener('click', closeLiveCamera);
    document.getElementById('fileGalleryInput').addEventListener('change', handleFileSelect);

    document.getElementById('uploadModal').addEventListener('hidden.bs.modal', () => {
        closeLiveCamera();
    });

    document.getElementById('progressRings').addEventListener('click', () => {
        if (currentData) showUploadSection();
    });

    document.getElementById('resNormal').addEventListener('input', calcTotal);
    document.getElementById('resOt').addEventListener('input', calcTotal);

    document.getElementById('btnSave').addEventListener('click', saveProgress);

    document.getElementById('btnColorSettings').addEventListener('click', openColorSettings);
    document.getElementById('btnTemplateSettings').addEventListener('click', openTemplateSettings);
    document.getElementById('btnTemplateTakePhoto').addEventListener('click', openTemplateLiveCamera);
    document.getElementById('btnTemplateChooseFile').addEventListener('click', () => {
        document.getElementById('templateFileInput').click();
    });
    document.getElementById('templateFileInput').addEventListener('change', handleTemplateFileSelect);
    document.getElementById('btnDeleteTemplate').addEventListener('click', deleteTemplate);
    document.querySelectorAll('.btn-add-color').forEach(btn => {
        btn.addEventListener('click', () => openColorPicker(btn.dataset.group));
    });
    document.getElementById('toleranceSlider').addEventListener('input', e => {
        document.getElementById('toleranceValue').textContent = e.target.value;
    });
    document.getElementById('btnSaveColorProfile').addEventListener('click', saveColorProfile);
    document.getElementById('btnResetColorProfile').addEventListener('click', resetColorProfile);
}

async function doScan() {
    const barcode = document.getElementById('barcodeInput').value.trim();
    if (!barcode) {
        toast('กรุณากรอก QR Code', 'error');
        return;
    }

    showLoading();
    try {
        const res = await fetch(`${API}/scan/${encodeURIComponent(barcode)}`);
        if (!res.ok) {
            const err = await res.json().catch(() => ({}));
            toast(err.message || 'ไม่พบข้อมูล', 'error');
            document.getElementById('orderInfo').classList.remove('active');
            return;
        }
        currentData = await res.json();
        renderOrderInfo(currentData);
    } catch (e) {
        toast('เกิดข้อผิดพลาดในการเชื่อมต่อ', 'error');
    } finally {
        hideLoading();
    }
}

function renderOrderInfo(data) {
    const item = data.barcodeItem;
    document.getElementById('infoOrno').textContent = item.orno?.trim() || '-';
    document.getElementById('infoDesign').textContent = item.designName || '-';
    document.getElementById('infoBarcode').textContent = item.barcodeNo?.trim() || '-';
    document.getElementById('infoType').textContent = item.orderType || '-';
    document.getElementById('infoCnv').textContent = item.cnvDesc || item.cnvId || '-';
    document.getElementById('infoSize').textContent = `${item.width} x ${item.length}`;
    document.getElementById('infoSqm').textContent = item.sqm ?? '-';
    document.getElementById('infoQty').textContent = item.qty ?? '-';

    document.getElementById('cumNormal').textContent = data.cumulativeNormal + '%';
    document.getElementById('cumOt').textContent = data.cumulativeOt + '%';
    document.getElementById('cumTotal').textContent = data.cumulativeTotal + '%';

    setDailyDelta('deltaNormal', data.dailyDeltaNormal);
    setDailyDelta('deltaOt', data.dailyDeltaOt);
    setDailyDelta('deltaTotal', data.dailyDeltaTotal);

    const circumference = 2 * Math.PI * 15.5;
    setRing('ringNormal', data.cumulativeNormal, circumference);
    setRing('ringOt', data.cumulativeOt, circumference);
    setRing('ringTotal', data.cumulativeTotal, circumference);

    updateStatus(data.cumulativeTotal, data.cumulativeIsComplete);
    updateLastUpdated(data.progressHistory);

    document.getElementById('orderInfo').classList.add('active');

    const btnColor = document.getElementById('btnColorSettings');
    btnColor.style.display = 'flex';
    if (data.hasColorProfile) {
        btnColor.innerHTML = '<i class="bi bi-palette-fill"></i> ตั้งค่าสี <span class="profile-dot"></span>';
    } else {
        btnColor.innerHTML = '<i class="bi bi-palette"></i> ตั้งค่าสี';
    }

    const btnTemplate = document.getElementById('btnTemplateSettings');
    btnTemplate.style.display = 'flex';
    if (data.hasTemplate) {
        btnTemplate.innerHTML = '<i class="bi bi-grid-3x3-gap-fill"></i> Template <span class="profile-dot"></span>';
    } else {
        btnTemplate.innerHTML = '<i class="bi bi-grid-3x3"></i> Template';
    }

    if (data.cumulativeIsComplete) {
        setTimeout(() => launchConfetti(), 400);
    }
}

function setDailyDelta(id, val) {
    const el = document.getElementById(id);
    if (!el) return;
    if (val > 0) {
        el.textContent = `+${val}%`;
        el.className = 'daily-delta up';
    } else if (val < 0) {
        el.textContent = `${val}%`;
        el.className = 'daily-delta down';
    } else {
        el.textContent = '0%';
        el.className = 'daily-delta same';
    }
}

function setRing(id, pct, circ) {
    const val = Math.min(Math.max(pct, 0), 100);
    const dash = (val / 100) * circ;
    const el = document.getElementById(id);
    if (val === 0) {
        el.style.strokeDasharray = `0 ${circ}`;
        el.style.transition = 'none';
        return;
    }
    el.style.transition = 'none';
    el.style.strokeDasharray = `0 ${circ}`;
    requestAnimationFrame(() => {
        requestAnimationFrame(() => {
            el.style.transition = 'stroke-dasharray 0.8s ease';
            el.style.strokeDasharray = `${dash} ${circ}`;
        });
    });
}

function updateStatus(total, isComplete) {
    const badge = document.getElementById('statusBadge');
    if (isComplete) {
        badge.textContent = 'เสร็จแล้ว';
        badge.className = 'status-badge done';
    } else if (total >= 80) {
        badge.textContent = 'เกือบเสร็จ';
        badge.className = 'status-badge almost';
    } else if (total > 0) {
        badge.textContent = 'กำลังดำเนินการ';
        badge.className = 'status-badge progress';
    } else {
        badge.textContent = 'ยังไม่เริ่ม';
        badge.className = 'status-badge idle';
    }
}

function updateLastUpdated(history) {
    const el = document.getElementById('lastUpdated');
    if (!history || history.length === 0) {
        el.textContent = '';
        return;
    }
    const latest = history[0].createdAt;
    const d = new Date(latest);
    const now = new Date();
    const diff = Math.floor((now - d) / 60000);
    let text;
    if (diff < 1) text = 'เมื่อสักครู่';
    else if (diff < 60) text = `${diff} นาทีที่แล้ว`;
    else if (diff < 1440) text = `${Math.floor(diff / 60)} ชม.ที่แล้ว`;
    else text = d.toLocaleDateString('th-TH', { day: '2-digit', month: 'short' });
    el.innerHTML = `<i class="bi bi-clock"></i> ${text}`;
}

function launchConfetti() {
    const container = document.getElementById('confettiContainer');
    const colors = ['#6366f1', '#8b5cf6', '#10b981', '#f59e0b', '#ef4444', '#ec4899'];
    for (let i = 0; i < 50; i++) {
        const piece = document.createElement('div');
        piece.className = 'confetti-piece';
        piece.style.left = Math.random() * 100 + '%';
        piece.style.background = colors[Math.floor(Math.random() * colors.length)];
        piece.style.animationDelay = Math.random() * 0.5 + 's';
        piece.style.animationDuration = (1.5 + Math.random()) + 's';
        container.appendChild(piece);
    }
    setTimeout(() => { container.innerHTML = ''; }, 3000);
}

let historyFilterDate = 'all';
let historyAllRecords = [];
let historyTotalCount = 0;
const HISTORY_PAGE_SIZE = 7;

function showHistory() {
    if (!currentData) return;
    const history = currentData.progressHistory;
    historyTotalCount = currentData.progressTotalCount || history.length;
    historyAllRecords = [...history];

    if (!history || history.length === 0) {
        document.getElementById('historyFilter').style.display = 'none';
        document.getElementById('historyBody').innerHTML = '';
        document.getElementById('noHistory').style.display = 'block';
        document.activeElement?.blur();
        bootstrap.Modal.getOrCreateInstance(document.getElementById('historyModal')).show();
        return;
    }

    document.getElementById('noHistory').style.display = 'none';
    historyFilterDate = 'all';
    document.getElementById('filterDateInput').value = '';
    rebuildHistoryView();

    document.activeElement?.blur();
    bootstrap.Modal.getOrCreateInstance(document.getElementById('historyModal')).show();
}

function initHistoryFilter() {
    document.getElementById('filterDateInput').addEventListener('change', (e) => {
        const val = e.target.value;
        if (val) {
            historyFilterDate = val;
            document.getElementById('filterResetBtn').classList.remove('active');
        } else {
            historyFilterDate = 'all';
            document.getElementById('filterResetBtn').classList.add('active');
        }
        renderFilteredHistory();
    });

    document.getElementById('filterResetBtn').addEventListener('click', () => {
        historyFilterDate = 'all';
        document.getElementById('filterDateInput').value = '';
        document.getElementById('filterResetBtn').classList.add('active');
        renderFilteredHistory();
    });
}

function rebuildHistoryView() {
    const filterEl = document.getElementById('historyFilter');
    filterEl.style.display = 'block';

    const resetBtn = document.getElementById('filterResetBtn');
    resetBtn.classList.toggle('active', historyFilterDate === 'all');

    renderFilteredHistory();
}

function renderFilteredHistory() {
    const dayGroups = groupByDate(historyAllRecords);
    const dayKeys = Object.keys(dayGroups).sort((a, b) => new Date(b) - new Date(a));

    const keysToRender = historyFilterDate === 'all'
        ? dayKeys
        : dayKeys.filter(k => k === historyFilterDate);

    const totalRecords = historyAllRecords.length;
    const filteredRecords = keysToRender.reduce((s, k) => s + (dayGroups[k]?.length || 0), 0);

    const infoEl = document.getElementById('filterInfo');
    if (historyFilterDate === 'all') {
        infoEl.textContent = `${dayKeys.length} วัน, ${totalRecords} รายการ`;
    } else {
        const dateLabel = new Date(historyFilterDate + 'T00:00:00').toLocaleDateString('th-TH', { day: '2-digit', month: 'short', year: '2-digit' });
        infoEl.textContent = filteredRecords > 0
            ? `${dateLabel} — ${filteredRecords} รายการ`
            : `ไม่พบข้อมูลวันที่ ${dateLabel}`;
    }

    renderHistoryRecords(dayGroups, keysToRender);
}

async function loadMoreHistory() {
    if (!currentData) return;
    const barcode = currentData.barcodeItem?.barcodeNo?.trim();
    if (!barcode) return;

    const btn = document.getElementById('btnLoadMore');
    btn.disabled = true;
    btn.innerHTML = '<span class="inline-spinner"></span> กำลังโหลด...';

    try {
        const offset = historyAllRecords.length;
        const res = await fetch(`${API}/history/${encodeURIComponent(barcode)}?limit=${HISTORY_PAGE_SIZE}&offset=${offset}`);
        if (res.ok) {
            const data = await res.json();
            historyAllRecords.push(...data.records);
            historyTotalCount = data.totalCount;
            rebuildHistoryView();
        }
    } catch {
        toast('โหลดข้อมูลไม่สำเร็จ', 'error');
    } finally {
        btn.disabled = false;
        btn.innerHTML = '<i class="bi bi-arrow-down-circle"></i> โหลดเพิ่ม';
    }
}

function renderHistoryRecords(dayGroups, keysToRender) {
    const container = document.getElementById('historyBody');
    container.innerHTML = '';

    keysToRender.forEach((dateKey, idx) => {
        const records = dayGroups[dateKey];
        const bestNormal = Math.max(...records.map(h => h.computedNormalPercent));
        const bestOt = Math.max(...records.map(h => h.computedOtPercent));
        let bestTotal = Math.max(...records.map(h => (h.computedTotalPercent)));
        if (bestTotal > 100) bestTotal = 100;

        // Use stored deltas from DB (sum if multiple records per day)
        const deltaNormal = records.reduce((s, h) => s + (h.deltaNormalPercent || 0), 0);
        const deltaOt = records.reduce((s, h) => s + (h.deltaOtPercent || 0), 0);
        const deltaTotal = records.reduce((s, h) => s + (h.deltaTotalPercent || 0), 0);

        const dateLabel = new Date(dateKey + 'T00:00:00').toLocaleDateString('th-TH', { day: '2-digit', month: 'short', year: '2-digit' });

        const group = document.createElement('div');
        group.className = 'day-group';

        const deltaTag = (val) => {
            if (val > 0) return `<span class="day-stat-delta up"><i class="bi bi-caret-up-fill"></i> +${val}%</span>`;
            if (val < 0) return `<span class="day-stat-delta down"><i class="bi bi-caret-down-fill"></i> ${val}%</span>`;
            return `<span class="day-stat-delta same">— 0%</span>`;
        };

        const header = document.createElement('div');
        header.className = 'day-header';
        header.innerHTML = `
            <div class="day-header-top">
                <span class="day-header-date"><i class="bi bi-calendar-event"></i> ${dateLabel}</span>
                <span class="day-header-count">${records.length} รายการ</span>
            </div>
            <div class="day-stats">
                <div class="day-stat">
                    <span class="day-stat-label">ปกติ</span>
                    <span class="day-stat-val normal">${bestNormal}%</span>
                    ${deltaTag(deltaNormal)}
                </div>
                <div class="day-stat">
                    <span class="day-stat-label">OT</span>
                    <span class="day-stat-val ot">${bestOt}%</span>
                    ${deltaTag(deltaOt)}
                </div>
                <div class="day-stat">
                    <span class="day-stat-label">รวม</span>
                    <span class="day-stat-val total">${bestTotal}%</span>
                    ${deltaTag(deltaTotal)}
                </div>
            </div>`;
        group.appendChild(header);

        records.forEach(h => {
            const d = new Date(h.createdAt);
            const time = d.toLocaleTimeString('th-TH', { hour: '2-digit', minute: '2-digit' });
            const normalPct = h.computedNormalPercent;
            const otPct = h.computedOtPercent;
            let totalPct = h.computedTotalPercent;
            if (totalPct > 100) totalPct = 100;
            const img = h.evidenceImagePath
                ? `<img class="history-card-img" src="${h.evidenceImagePath}" onclick="viewImage('${h.evidenceImagePath}')" />`
                : `<div class="history-card-img-empty"><i class="bi bi-image"></i></div>`;

            const card = document.createElement('div');
            card.className = 'history-card';
            card.innerHTML = `
                ${img}
                <div class="history-card-body">
                    <div class="history-card-top">
                        <span class="history-date"><i class="bi bi-clock"></i> ${time}</span>
                        <button class="btn-icon-sm danger" onclick="deleteProgress(${h.id})" title="ลบ">
                            <i class="bi bi-trash3"></i>
                        </button>
                    </div>
                    <div class="history-card-bars">
                        <div class="history-bar">
                            <div class="history-bar-label">ปกติ</div>
                            <div class="history-bar-track"><div class="history-bar-fill normal" style="width:${Math.min(normalPct, 100)}%"></div></div>
                            <div class="history-bar-val normal">${normalPct}%</div>
                        </div>
                        <div class="history-bar">
                            <div class="history-bar-label">OT</div>
                            <div class="history-bar-track"><div class="history-bar-fill ot" style="width:${Math.min(otPct, 100)}%"></div></div>
                            <div class="history-bar-val ot">${otPct}%</div>
                        </div>
                        <div class="history-bar">
                            <div class="history-bar-label">รวม</div>
                            <div class="history-bar-track"><div class="history-bar-fill total" style="width:${Math.min(totalPct, 100)}%"></div></div>
                            <div class="history-bar-val total">${totalPct}%</div>
                        </div>
                    </div>
                </div>`;
            group.appendChild(card);
        });

        container.appendChild(group);
    });

    // Load more button
    const oldBtn = document.getElementById('btnLoadMore');
    if (oldBtn) oldBtn.remove();

    if (historyFilterDate === 'all' && historyAllRecords.length < historyTotalCount) {
        const remaining = historyTotalCount - historyAllRecords.length;
        const btn = document.createElement('button');
        btn.id = 'btnLoadMore';
        btn.className = 'btn-load-more';
        btn.innerHTML = `<i class="bi bi-arrow-down-circle"></i> โหลดเพิ่ม (เหลืออีก ${remaining} วัน)`;
        btn.addEventListener('click', loadMoreHistory);
        container.appendChild(btn);
    }
}

function groupByDate(records) {
    const groups = {};
    records.forEach(h => {
        const key = h.workDate
            ? h.workDate.split('T')[0]
            : new Date(h.createdAt).toISOString().split('T')[0];
        if (!groups[key]) groups[key] = [];
        groups[key].push(h);
    });
    return groups;
}

function viewImage(src) {
    document.getElementById('imageViewFull').src = src;
    document.activeElement?.blur();
    bootstrap.Modal.getOrCreateInstance(document.getElementById('imageViewModal')).show();
}

async function deleteProgress(id) {
    const confirmed = await showConfirm({
        icon: 'bi-trash3',
        title: 'ต้องการลบรายการนี้?',
        desc: 'ข้อมูลที่ลบจะไม่สามารถกู้คืนได้',
        okText: 'ลบเลย',
        okClass: 'danger'
    });
    if (!confirmed) return;
    showLoading();
    try {
        await fetch(`${API}/delete/${id}`, { method: 'DELETE' });
        toast('ลบสำเร็จ', 'success');
        document.activeElement?.blur();
        bootstrap.Modal.getOrCreateInstance(document.getElementById('historyModal'))?.hide();
        doScan();
    } catch {
        toast('เกิดข้อผิดพลาด', 'error');
        hideLoading();
    }
}

function showConfirm({ icon, title, desc, okText, okClass }) {
    return new Promise(resolve => {
        const overlay = document.getElementById('confirmOverlay');
        const iconEl = document.getElementById('confirmIcon');
        const titleEl = document.getElementById('confirmTitle');
        const descEl = document.getElementById('confirmDesc');
        const okBtn = document.getElementById('confirmOk');
        const cancelBtn = document.getElementById('confirmCancel');

        iconEl.innerHTML = `<i class="bi ${icon}"></i>`;
        iconEl.className = `confirm-icon ${okClass || ''}`;
        titleEl.textContent = title;
        descEl.textContent = desc || '';
        descEl.style.display = desc ? 'block' : 'none';
        okBtn.textContent = okText || 'ตกลง';
        okBtn.className = `confirm-btn ok ${okClass || ''}`;

        overlay.classList.add('active');

        function cleanup(result) {
            overlay.classList.remove('active');
            okBtn.removeEventListener('click', onOk);
            cancelBtn.removeEventListener('click', onCancel);
            overlay.removeEventListener('click', onOverlay);
            resolve(result);
        }

        function onOk() { cleanup(true); }
        function onCancel() { cleanup(false); }
        function onOverlay(e) { if (e.target === overlay) cleanup(false); }

        okBtn.addEventListener('click', onOk);
        cancelBtn.addEventListener('click', onCancel);
        overlay.addEventListener('click', onOverlay);
    });
}

function showUploadSection() {
    resetUpload();
    document.activeElement?.blur();
    bootstrap.Modal.getOrCreateInstance(document.getElementById('uploadModal')).show();
}

function resetUpload() {
    closeLiveCamera();
    currentImage = null;
    document.getElementById('uploadButtons').style.display = 'grid';
    document.getElementById('resultFields').style.display = 'none';
    document.getElementById('resNormal').value = '0';
    document.getElementById('resOt').value = '0';
    document.getElementById('resTotal').value = '0';
    document.getElementById('noteInput').value = '';
    document.getElementById('fileGalleryInput').value = '';
    document.getElementById('recordDate').value = new Date().toISOString().split('T')[0];
}

async function openLiveCamera() {
    document.getElementById('cameraLive').style.display = 'block';
    const video = document.getElementById('cameraVideo');
    try {
        cameraStream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: 'environment', width: { ideal: 1920 }, height: { ideal: 1440 } }
        });
        video.srcObject = cameraStream;
    } catch {
        toast('ไม่สามารถเปิดกล้องได้', 'error');
        closeLiveCamera();
    }
}

function capturePhoto() {
    const video = document.getElementById('cameraVideo');
    const canvas = document.getElementById('cameraCanvas');
    canvas.width = video.videoWidth;
    canvas.height = video.videoHeight;
    canvas.getContext('2d').drawImage(video, 0, 0);
    currentImage = canvas.toDataURL('image/jpeg', 0.85);

    closeLiveCamera();
    document.getElementById('uploadButtons').style.display = 'none';
    document.getElementById('resultFields').style.display = 'flex';
    document.getElementById('previewImg').src = currentImage;
    analyzeImage(currentImage);
}

function closeLiveCamera() {
    if (cameraStream) {
        cameraStream.getTracks().forEach(t => t.stop());
        cameraStream = null;
    }
    const video = document.getElementById('cameraVideo');
    if (video) video.srcObject = null;
    document.getElementById('cameraLive').style.display = 'none';
}

function handleFileSelect(e) {
    const file = e.target.files[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = async (ev) => {
        currentImage = ev.target.result;
        document.getElementById('uploadButtons').style.display = 'none';
        document.getElementById('resultFields').style.display = 'flex';
        document.getElementById('previewImg').src = currentImage;

        await analyzeImage(currentImage);
    };
    reader.readAsDataURL(file);
}

async function analyzeImage(base64) {
    showLoading();
    try {
        const res = await fetch(`${API}/analyze`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ imageBase64: base64, orderNo: currentData?.barcodeItem?.barcodeNo?.trim() || null })
        });
        const result = await res.json();
        document.getElementById('resNormal').value = result.normalPercent;
        document.getElementById('resOt').value = result.otPercent;
        document.getElementById('resTotal').value = result.totalPercent;
        document.getElementById('resultFields').style.display = 'flex';
    } catch {
        toast('วิเคราะห์รูปไม่สำเร็จ', 'error');
    } finally {
        hideLoading();
    }
}

function calcTotal() {
    const n = parseFloat(document.getElementById('resNormal').value) || 0;
    const o = parseFloat(document.getElementById('resOt').value) || 0;
    document.getElementById('resTotal').value = Math.min(Math.round((n + o) * 100) / 100, 100);
}

async function saveProgress() {
    if (!currentData || !currentImage) {
        toast('กรุณาถ่ายรูปก่อน', 'error');
        return;
    }

    const btn = document.getElementById('btnSave');
    btn.disabled = true;
    btn.innerHTML = '<span class="inline-spinner"></span> กำลังบันทึก...';

    try {
        const item = currentData.barcodeItem;
        const dateVal = document.getElementById('recordDate').value || null;
        const body = {
            barcodeNo: item.barcodeNo.trim(),
            orno: item.orno.trim(),
            normalPercent: parseFloat(document.getElementById('resNormal').value) || 0,
            otPercent: parseFloat(document.getElementById('resOt').value) || 0,
            totalPercent: parseFloat(document.getElementById('resTotal').value) || 0,
            qualityScore: 0,
            imageBase64: currentImage,
            note: document.getElementById('noteInput').value.trim() || null,
            recordDate: dateVal
        };

        const res = await fetch(`${API}/save`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });

        if (res.ok) {
            toast('บันทึกสำเร็จ', 'success');
            document.activeElement?.blur();
            bootstrap.Modal.getOrCreateInstance(document.getElementById('uploadModal'))?.hide();
            doScan();
        } else {
            toast('บันทึกไม่สำเร็จ', 'error');
        }
    } catch {
        toast('เกิดข้อผิดพลาด', 'error');
    } finally {
        btn.disabled = false;
        btn.innerHTML = '<i class="bi bi-check-circle"></i> บันทึก';
    }
}

async function startBarcodeScanner() {
    const container = document.getElementById('scannerContainer');
    const btnOpen = document.getElementById('btnCameraScan');
    container.style.display = 'block';
    btnOpen.style.display = 'none';

    html5QrCode = new Html5Qrcode('barcodeReader');

    try {
        await html5QrCode.start(
            { facingMode: 'environment' },
            {
                fps: 15,
                qrbox: { width: 260, height: 260 },
                aspectRatio: 1.0,
                disableFlip: false,
                formatsToSupport: [
                    Html5QrcodeSupportedFormats.QR_CODE,
                    Html5QrcodeSupportedFormats.CODE_128,
                    Html5QrcodeSupportedFormats.CODE_39,
                    Html5QrcodeSupportedFormats.EAN_13
                ]
            },
            (decodedText) => {
                document.getElementById('barcodeInput').value = decodedText.trim();
                stopBarcodeScanner();
                doScan();
            },
            () => {}
        );
    } catch (err) {
        toast('ไม่สามารถเปิดกล้องได้', 'error');
        stopBarcodeScanner();
    }
}

async function stopBarcodeScanner() {
    const container = document.getElementById('scannerContainer');
    const btnOpen = document.getElementById('btnCameraScan');

    if (html5QrCode) {
        try {
            await html5QrCode.stop();
        } catch {}
        html5QrCode.clear();
        html5QrCode = null;
    }

    container.style.display = 'none';
    btnOpen.style.display = 'flex';
}

function toast(msg, type = '') {
    const container = document.getElementById('toastContainer');
    const el = document.createElement('div');
    el.className = `toast-msg ${type}`;
    el.textContent = msg;
    container.appendChild(el);
    setTimeout(() => {
        el.style.opacity = '0';
        el.style.transform = 'translateY(12px)';
        el.style.transition = 'all 0.3s';
        setTimeout(() => el.remove(), 300);
    }, 2500);
}

async function openColorSettings() {
    if (!currentData) return;
    const orderNo = currentData.barcodeItem?.barcodeNo?.trim();
    if (!orderNo) return;

    colorProfileColors = { normal: [], ot: [] };
    document.getElementById('toleranceSlider').value = 30;
    document.getElementById('toleranceValue').textContent = '30';

    showLoading();
    try {
        const res = await fetch(`${API}/color-profile/${encodeURIComponent(orderNo)}`);
        if (res.ok) {
            const profile = await res.json();
            if (profile && profile.colors) {
                document.getElementById('toleranceSlider').value = profile.tolerance;
                document.getElementById('toleranceValue').textContent = profile.tolerance;
                profile.colors.forEach(c => {
                    colorProfileColors[c.colorGroup].push(c.hexColor);
                });
            }
        }
    } catch {}
    hideLoading();

    renderSwatches('normal');
    renderSwatches('ot');
    document.activeElement?.blur();
    const modal = document.getElementById('colorSettingsModal');
    bootstrap.Modal.getOrCreateInstance(modal).show();
}

function renderSwatches(group) {
    const container = document.getElementById(group === 'normal' ? 'normalSwatches' : 'otSwatches');
    container.innerHTML = '';

    colorProfileColors[group].forEach((hex, i) => {
        const swatch = document.createElement('div');
        swatch.className = 'color-swatch';
        swatch.innerHTML = `
            <div class="swatch-preview" style="background:${hex}"></div>
            <span class="swatch-hex">${hex}</span>
            <button class="swatch-remove"><i class="bi bi-x"></i></button>`;

        swatch.querySelector('.swatch-preview').addEventListener('click', () => {
            openColorPicker(group, i);
        });
        swatch.querySelector('.swatch-remove').addEventListener('click', (e) => {
            e.stopPropagation();
            colorProfileColors[group].splice(i, 1);
            renderSwatches(group);
        });

        container.appendChild(swatch);
    });
}

const PALETTE_COLORS = [
    '#000000', '#1a1a1a', '#333333', '#4d4d4d', '#666666', '#808080',
    '#999999', '#b3b3b3', '#cccccc', '#e6e6e6',
    '#4a2c17', '#5c3a1e', '#6b4226', '#8b5e3c', '#a0522d', '#b8860b',
    '#d2a679', '#deb887', '#c4a882', '#e8d5b7',
    '#2d0000', '#590000', '#8b0000', '#b22222', '#cc0000', '#ef4444',
    '#ff4444', '#ff6b6b', '#ff9999', '#ffcccc',
    '#cc5500', '#e67300', '#ff8c00', '#ffa500', '#ffb347', '#ffd700',
    '#ffe135', '#ffed4a', '#fff3a0', '#fffacd',
    '#003300', '#004d00', '#006600', '#008000', '#10b981', '#22c55e',
    '#4ade80', '#6ee7b7', '#86efac', '#bbf7d0',
    '#000066', '#000099', '#0000cc', '#2563eb', '#3b82f6', '#6366f1',
    '#7c3aed', '#8b5cf6', '#a78bfa', '#c4b5fd',
];

let pickerGroup = null;
let pickerEditIndex = null;
let pickerSelectedColor = '#000000';

let pickerInitialized = false;

function ensurePickerInit() {
    if (pickerInitialized) return;
    const palette = document.getElementById('pickerPalette');
    if (!palette) return;
    pickerInitialized = true;

    PALETTE_COLORS.forEach(color => {
        const cell = document.createElement('div');
        cell.className = 'palette-cell';
        cell.style.background = color;
        cell.dataset.color = color;
        cell.addEventListener('click', () => selectPickerColor(color));
        palette.appendChild(cell);
    });

    document.getElementById('pickerHexInput').addEventListener('input', (e) => {
        let val = e.target.value.trim();
        if (!val.startsWith('#')) val = '#' + val;
        if (/^#[0-9a-fA-F]{6}$/.test(val)) {
            selectPickerColor(val, true);
        }
    });

    const nativeInput = document.getElementById('pickerNativeInput');
    nativeInput.addEventListener('click', () => {
        document.getElementById('colorPickerOverlay')?.classList.add('native-open');
    });
    nativeInput.addEventListener('change', (e) => {
        const color = e.target.value;
        if (pickerEditIndex !== null) {
            colorProfileColors[pickerGroup][pickerEditIndex] = color;
        } else {
            colorProfileColors[pickerGroup].push(color);
        }
        renderSwatches(pickerGroup);
        closeColorPicker();
    });
    nativeInput.addEventListener('blur', () => {
        document.getElementById('colorPickerOverlay')?.classList.remove('native-open');
    });

    document.getElementById('pickerConfirm').addEventListener('click', () => {
        if (pickerEditIndex !== null) {
            colorProfileColors[pickerGroup][pickerEditIndex] = pickerSelectedColor;
        } else {
            colorProfileColors[pickerGroup].push(pickerSelectedColor);
        }
        renderSwatches(pickerGroup);
        closeColorPicker();
    });

    const overlay = document.getElementById('colorPickerOverlay');
    document.getElementById('pickerClose').addEventListener('click', closeColorPicker);
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) closeColorPicker();
    });
}

function selectPickerColor(color, fromHex) {
    pickerSelectedColor = color.toLowerCase();
    const preview = document.getElementById('pickerPreview');
    const hexInput = document.getElementById('pickerHexInput');
    const nativeInput = document.getElementById('pickerNativeInput');
    if (!preview) return;

    preview.style.background = color;
    if (!fromHex) hexInput.value = color.toLowerCase();
    nativeInput.value = color;

    document.querySelectorAll('.palette-cell').forEach(cell => {
        cell.classList.toggle('active', cell.dataset.color === color.toLowerCase());
    });
}

function openColorPicker(group, editIndex) {
    ensurePickerInit();
    pickerGroup = group;
    pickerEditIndex = editIndex !== undefined ? editIndex : null;

    const initial = pickerEditIndex !== null
        ? colorProfileColors[group][pickerEditIndex]
        : (group === 'ot' ? '#cc0000' : '#000000');

    selectPickerColor(initial);

    const overlay = document.getElementById('colorPickerOverlay');
    if (overlay) overlay.classList.add('show');
}

function closeColorPicker() {
    const overlay = document.getElementById('colorPickerOverlay');
    if (overlay) overlay.classList.remove('show');
    pickerGroup = null;
    pickerEditIndex = null;
}

async function saveColorProfile() {
    if (!currentData) return;
    const orderNo = currentData.barcodeItem?.barcodeNo?.trim();
    if (!orderNo) return;

    const allColors = [];
    colorProfileColors.normal.forEach(hex => allColors.push({ colorGroup: 'normal', hexColor: hex }));
    colorProfileColors.ot.forEach(hex => allColors.push({ colorGroup: 'ot', hexColor: hex }));

    if (allColors.length === 0) {
        toast('กรุณาเลือกสีอย่างน้อย 1 สี', 'error');
        return;
    }

    showLoading();
    try {
        const res = await fetch(`${API}/color-profile`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                orderNo,
                tolerance: parseInt(document.getElementById('toleranceSlider').value),
                colors: allColors
            })
        });

        if (res.ok) {
            toast('บันทึกการตั้งค่าสีสำเร็จ', 'success');
            document.activeElement?.blur();
            bootstrap.Modal.getOrCreateInstance(document.getElementById('colorSettingsModal'))?.hide();
            currentData.hasColorProfile = true;
            const btnColor = document.getElementById('btnColorSettings');
            btnColor.innerHTML = '<i class="bi bi-palette-fill"></i> ตั้งค่าสี <span class="profile-dot"></span>';
        } else {
            const err = await res.json().catch(() => ({}));
            toast(err.message || 'บันทึกไม่สำเร็จ', 'error');
        }
    } catch {
        toast('เกิดข้อผิดพลาด', 'error');
    } finally {
        hideLoading();
    }
}

async function resetColorProfile() {
    if (!currentData) return;
    const orderNo = currentData.barcodeItem?.barcodeNo?.trim();
    if (!orderNo) return;

    const confirmed = await showConfirm({
        icon: 'bi-arrow-counterclockwise',
        title: 'ใช้ค่าเริ่มต้น?',
        desc: 'จะลบการตั้งค่าสีของ Order นี้ และกลับไปใช้ค่าเริ่มต้น (แดง=OT, ที่เหลือ=ปกติ)',
        okText: 'ยืนยัน',
        okClass: ''
    });
    if (!confirmed) return;

    showLoading();
    try {
        await fetch(`${API}/color-profile/${encodeURIComponent(orderNo)}`, { method: 'DELETE' });
        toast('กลับไปใช้ค่าเริ่มต้นแล้ว', 'success');
        document.activeElement?.blur();
        bootstrap.Modal.getOrCreateInstance(document.getElementById('colorSettingsModal'))?.hide();
        currentData.hasColorProfile = false;
        const btnColor = document.getElementById('btnColorSettings');
        btnColor.innerHTML = '<i class="bi bi-palette"></i> ตั้งค่าสี';
    } catch {
        toast('เกิดข้อผิดพลาด', 'error');
    } finally {
        hideLoading();
    }
}

async function openTemplateSettings() {
    if (!currentData) return;
    const orderNo = currentData.barcodeItem?.barcodeNo?.trim();
    if (!orderNo) return;

    document.getElementById('templateStatus').style.display = 'none';
    document.getElementById('templateUpload').style.display = 'block';
    document.getElementById('templateProcessing').style.display = 'none';

    showLoading();
    try {
        const res = await fetch(`${API}/template/${encodeURIComponent(orderNo)}`);
        if (res.ok) {
            const template = await res.json();
            if (template && template.templateImagePath) {
                document.getElementById('templatePreviewImg').src = template.templateImagePath;
                document.getElementById('templatePreviewMask').src = template.paintableMaskPath;
                document.getElementById('templatePixelInfo').textContent =
                    `PaintablePixels: ${template.paintablePixels.toLocaleString()} | ${template.templateWidth}x${template.templateHeight}`;
                document.getElementById('templateStatus').style.display = 'block';
                document.getElementById('templateUpload').style.display = 'none';
            }
        }
    } catch {}
    hideLoading();

    document.activeElement?.blur();
    bootstrap.Modal.getOrCreateInstance(document.getElementById('templateModal')).show();
}

function openTemplateLiveCamera() {
    bootstrap.Modal.getOrCreateInstance(document.getElementById('templateModal'))?.hide();
    document.getElementById('cameraLive').style.display = 'block';
    const video = document.getElementById('cameraVideo');

    navigator.mediaDevices.getUserMedia({
        video: { facingMode: 'environment', width: { ideal: 1920 }, height: { ideal: 1440 } }
    }).then(stream => {
        cameraStream = stream;
        video.srcObject = stream;

        const origCapture = document.getElementById('btnCapture');
        const handler = () => {
            const canvas = document.getElementById('cameraCanvas');
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            canvas.getContext('2d').drawImage(video, 0, 0);
            const base64 = canvas.toDataURL('image/jpeg', 0.9);
            closeLiveCamera();
            origCapture.removeEventListener('click', handler);
            uploadTemplate(base64);
        };
        origCapture.addEventListener('click', handler, { once: true });
    }).catch(() => {
        toast('ไม่สามารถเปิดกล้องได้', 'error');
        closeLiveCamera();
    });
}

function handleTemplateFileSelect(e) {
    const file = e.target.files[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = (ev) => {
        uploadTemplate(ev.target.result);
    };
    reader.readAsDataURL(file);
    e.target.value = '';
}

async function uploadTemplate(base64) {
    if (!currentData) return;
    const orderNo = currentData.barcodeItem?.barcodeNo?.trim();
    if (!orderNo) return;

    const modal = bootstrap.Modal.getOrCreateInstance(document.getElementById('templateModal'));
    modal.show();

    document.getElementById('templateUpload').style.display = 'none';
    document.getElementById('templateStatus').style.display = 'none';
    document.getElementById('templateProcessing').style.display = 'block';

    try {
        const res = await fetch(`${API}/template`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ orderNo, imageBase64: base64 })
        });

        if (res.ok) {
            const template = await res.json();
            toast('สร้าง Template สำเร็จ', 'success');
            currentData.hasTemplate = true;
            const btn = document.getElementById('btnTemplateSettings');
            btn.innerHTML = '<i class="bi bi-grid-3x3-gap-fill"></i> Template <span class="profile-dot"></span>';

            document.getElementById('templatePreviewImg').src = template.templateImagePath;
            document.getElementById('templatePreviewMask').src = template.paintableMaskPath;
            document.getElementById('templatePixelInfo').textContent =
                `PaintablePixels: ${template.paintablePixels.toLocaleString()} | ${template.templateWidth}x${template.templateHeight}`;
            document.getElementById('templateStatus').style.display = 'block';
            document.getElementById('templateProcessing').style.display = 'none';
        } else {
            const err = await res.json().catch(() => ({}));
            toast(err.message || 'สร้าง Template ไม่สำเร็จ', 'error');
            document.getElementById('templateUpload').style.display = 'block';
            document.getElementById('templateProcessing').style.display = 'none';
        }
    } catch {
        toast('เกิดข้อผิดพลาด', 'error');
        document.getElementById('templateUpload').style.display = 'block';
        document.getElementById('templateProcessing').style.display = 'none';
    }
}

async function deleteTemplate() {
    if (!currentData) return;
    const orderNo = currentData.barcodeItem?.barcodeNo?.trim();
    if (!orderNo) return;

    const confirmed = await showConfirm({
        icon: 'bi-trash3',
        title: 'ลบ Template?',
        desc: 'ระบบจะกลับไปคำนวณจาก planMask ภาพปัจจุบัน',
        okText: 'ลบเลย',
        okClass: 'danger'
    });
    if (!confirmed) return;

    showLoading();
    try {
        await fetch(`${API}/template/${encodeURIComponent(orderNo)}`, { method: 'DELETE' });
        toast('ลบ Template สำเร็จ', 'success');
        currentData.hasTemplate = false;
        const btn = document.getElementById('btnTemplateSettings');
        btn.innerHTML = '<i class="bi bi-grid-3x3"></i> Template';
        document.getElementById('templateStatus').style.display = 'none';
        document.getElementById('templateUpload').style.display = 'block';
    } catch {
        toast('เกิดข้อผิดพลาด', 'error');
    } finally {
        hideLoading();
    }
}

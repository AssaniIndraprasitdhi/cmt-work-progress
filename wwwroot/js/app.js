const API = '/api/ApiWorkProgress';
let currentData = null;
let currentImage = null;
let html5QrCode = null;
let cameraStream = null;

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

    const circumference = 2 * Math.PI * 15.5;
    setRing('ringNormal', data.cumulativeNormal, circumference);
    setRing('ringOt', data.cumulativeOt, circumference);
    setRing('ringTotal', data.cumulativeTotal, circumference);

    updateStatus(data.cumulativeTotal);
    updateLastUpdated(data.progressHistory);

    document.getElementById('orderInfo').classList.add('active');

    if (data.cumulativeTotal >= 100) {
        setTimeout(() => launchConfetti(), 400);
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

function updateStatus(total) {
    const badge = document.getElementById('statusBadge');
    if (total >= 100) {
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

function showHistory() {
    if (!currentData) return;
    const history = currentData.progressHistory;

    if (!history || history.length === 0) {
        document.getElementById('dateFilter').style.display = 'none';
        document.getElementById('historyBody').innerHTML = '';
        document.getElementById('noHistory').style.display = 'block';
        new bootstrap.Modal(document.getElementById('historyModal')).show();
        return;
    }

    document.getElementById('noHistory').style.display = 'none';

    const dayGroups = groupByDate(history);
    const dayKeys = Object.keys(dayGroups).sort((a, b) => new Date(b) - new Date(a));

    buildDateFilterChips(dayGroups, dayKeys);
    historyFilterDate = 'all';
    renderHistoryRecords(dayGroups, dayKeys, 'all');

    new bootstrap.Modal(document.getElementById('historyModal')).show();
}

function buildDateFilterChips(dayGroups, dayKeys) {
    const filterEl = document.getElementById('dateFilter');
    const chipsEl = document.getElementById('dateFilterChips');
    filterEl.style.display = 'block';
    chipsEl.innerHTML = '';

    const allChip = document.createElement('button');
    allChip.className = 'date-chip active';
    allChip.dataset.date = 'all';
    const totalCount = Object.values(dayGroups).reduce((s, r) => s + r.length, 0);
    allChip.innerHTML = `ทั้งหมด <span class="chip-count">${totalCount}</span>`;
    allChip.addEventListener('click', () => selectDateFilter('all', dayGroups, dayKeys));
    chipsEl.appendChild(allChip);

    dayKeys.forEach(dateKey => {
        const label = new Date(dateKey + 'T00:00:00').toLocaleDateString('th-TH', { day: '2-digit', month: 'short' });
        const chip = document.createElement('button');
        chip.className = 'date-chip';
        chip.dataset.date = dateKey;
        chip.innerHTML = `${label} <span class="chip-count">${dayGroups[dateKey].length}</span>`;
        chip.addEventListener('click', () => selectDateFilter(dateKey, dayGroups, dayKeys));
        chipsEl.appendChild(chip);
    });
}

function selectDateFilter(dateKey, dayGroups, dayKeys) {
    historyFilterDate = dateKey;
    document.querySelectorAll('.date-chip').forEach(c => c.classList.remove('active'));
    document.querySelector(`.date-chip[data-date="${dateKey}"]`).classList.add('active');
    renderHistoryRecords(dayGroups, dayKeys, dateKey);
}

function renderHistoryRecords(dayGroups, dayKeys, filterDate) {
    const container = document.getElementById('historyBody');
    container.innerHTML = '';

    const keysToRender = filterDate === 'all' ? dayKeys : dayKeys.filter(k => k === filterDate);

    keysToRender.forEach((dateKey, idx) => {
        const records = dayGroups[dateKey];
        const bestNormal = Math.max(...records.map(h => h.finalNormalPercent ?? h.computedNormalPercent));
        const bestOt = Math.max(...records.map(h => h.finalOtPercent ?? h.computedOtPercent));
        const bestTotal = Math.max(...records.map(h => (h.finalTotalPercent ?? h.computedTotalPercent)));

        const allIdx = dayKeys.indexOf(dateKey);
        const prevKey = dayKeys[allIdx + 1];
        let deltaNormal = 0, deltaOt = 0, deltaTotal = 0;
        if (prevKey) {
            const prevRecords = dayGroups[prevKey];
            const prevNormal = Math.max(...prevRecords.map(h => h.finalNormalPercent ?? h.computedNormalPercent));
            const prevOt = Math.max(...prevRecords.map(h => h.finalOtPercent ?? h.computedOtPercent));
            const prevTotal = Math.max(...prevRecords.map(h => (h.finalTotalPercent ?? h.computedTotalPercent)));
            deltaNormal = Math.round((bestNormal - prevNormal) * 100) / 100;
            deltaOt = Math.round((bestOt - prevOt) * 100) / 100;
            deltaTotal = Math.round((bestTotal - prevTotal) * 100) / 100;
        }

        const dateLabel = new Date(dateKey + 'T00:00:00').toLocaleDateString('th-TH', { day: '2-digit', month: 'short', year: '2-digit' });

        const header = document.createElement('div');
        header.className = 'day-header';
        const deltaTag = (val) => {
            if (val > 0) return `<span class="day-stat-delta up"><i class="bi bi-caret-up-fill"></i> +${val}%</span>`;
            if (val < 0) return `<span class="day-stat-delta down"><i class="bi bi-caret-down-fill"></i> ${val}%</span>`;
            return `<span class="day-stat-delta same">— 0%</span>`;
        };

        header.innerHTML = `
            <div class="day-header-top">
                <span class="day-header-date"><i class="bi bi-calendar-event"></i> ${dateLabel}</span>
                <span class="day-header-count">${records.length} รายการ</span>
            </div>
            <div class="day-stats">
                <div class="day-stat">
                    <span class="day-stat-label">ปกติ</span>
                    <span class="day-stat-val normal">${bestNormal}%</span>
                    ${prevKey ? deltaTag(deltaNormal) : ''}
                </div>
                <div class="day-stat">
                    <span class="day-stat-label">OT</span>
                    <span class="day-stat-val ot">${bestOt}%</span>
                    ${prevKey ? deltaTag(deltaOt) : ''}
                </div>
                <div class="day-stat">
                    <span class="day-stat-label">รวม</span>
                    <span class="day-stat-val total">${bestTotal}%</span>
                    ${prevKey ? deltaTag(deltaTotal) : ''}
                </div>
            </div>`;
        container.appendChild(header);

        records.forEach(h => {
            const d = new Date(h.createdAt);
            const time = d.toLocaleTimeString('th-TH', { hour: '2-digit', minute: '2-digit' });
            const normalPct = h.finalNormalPercent ?? h.computedNormalPercent;
            const otPct = h.finalOtPercent ?? h.computedOtPercent;
            const totalPct = h.finalTotalPercent ?? h.computedTotalPercent;
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
            container.appendChild(card);
        });
    });
}

function groupByDate(records) {
    const groups = {};
    records.forEach(h => {
        const key = new Date(h.createdAt).toISOString().split('T')[0];
        if (!groups[key]) groups[key] = [];
        groups[key].push(h);
    });
    return groups;
}

function viewImage(src) {
    document.getElementById('imageViewFull').src = src;
    new bootstrap.Modal(document.getElementById('imageViewModal')).show();
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
        bootstrap.Modal.getInstance(document.getElementById('historyModal'))?.hide();
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
    new bootstrap.Modal(document.getElementById('uploadModal')).show();
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
}

async function openLiveCamera() {
    document.getElementById('cameraLive').style.display = 'block';
    const video = document.getElementById('cameraVideo');
    try {
        cameraStream = await navigator.mediaDevices.getUserMedia({
            video: { facingMode: 'environment', width: { ideal: 1280 }, height: { ideal: 960 } }
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
            body: JSON.stringify({ imageBase64: base64 })
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
    document.getElementById('resTotal').value = Math.round((n + o) * 100) / 100;
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
        const body = {
            barcodeNo: item.barcodeNo.trim(),
            orno: item.orno.trim(),
            normalPercent: parseFloat(document.getElementById('resNormal').value) || 0,
            otPercent: parseFloat(document.getElementById('resOt').value) || 0,
            totalPercent: parseFloat(document.getElementById('resTotal').value) || 0,
            qualityScore: 0,
            imageBase64: currentImage,
            note: document.getElementById('noteInput').value.trim() || null
        };

        const res = await fetch(`${API}/save`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });

        if (res.ok) {
            toast('บันทึกสำเร็จ', 'success');
            bootstrap.Modal.getInstance(document.getElementById('uploadModal'))?.hide();
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
                fps: 10,
                qrbox: { width: 220, height: 220 },
                formatsToSupport: [
                    Html5QrcodeSupportedFormats.QR_CODE
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

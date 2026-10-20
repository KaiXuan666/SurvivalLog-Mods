(function () {
    'use strict';

    const styleId = 'hb-auto-classify-style';
    const css = `
      .hb-auto-wrap { display:inline-flex; align-items:center; margin:0 8px; vertical-align:middle; }
      .hb-auto-button { min-height:42px; padding:8px 13px; border:1px solid rgba(255,193,7,.48); border-radius:8px; color:#ffe49a; background:rgba(48,42,32,.94); cursor:pointer; font:inherit; }
      .hb-auto-button:hover { background:#6f5526; color:#fff; }
      .hb-auto-button:disabled { opacity:.55; cursor:default; }
      .hb-auto-toast { position:fixed; z-index:20000; left:50%; top:12%; transform:translate(-50%,-8px); width:max-content; max-width:min(660px,86vw); padding:12px 18px; box-sizing:border-box; border:1px solid rgba(255,193,7,.58); border-radius:9px; color:#eee; background:rgba(33,31,32,.96); box-shadow:0 6px 22px #0009; font:15px/1.45 sans-serif; text-align:center; white-space:pre-line; pointer-events:none; opacity:0; transition:opacity .2s ease,transform .2s ease; }
      .hb-auto-toast.show { opacity:1; transform:translate(-50%,0); }
      .hb-auto-toast.success { border-color:rgba(102,190,112,.75); }
      .hb-auto-toast.failure { border-color:rgba(230,126,74,.78); }
    `;
    if (!document.getElementById(styleId)) {
        const style = document.createElement('style'); style.id = styleId; style.textContent = css; document.head.appendChild(style);
    }

    let running = false;
    let responseTimer = null;
    let toastTimer = null;
    let toastQueue = [];
    let pendingRequestId = null;
    let requestSequence = 0;
    const parseData = value => {
        if (typeof value !== 'string') return value || {};
        try { return JSON.parse(value); } catch (_) { return {}; }
    };

    function showToast(message, kind, duration) {
        let toast = document.querySelector('.hb-auto-toast');
        if (!toast) {
            toast = document.createElement('div');
            toast.className = 'hb-auto-toast';
            toast.setAttribute('role', 'status');
            toast.setAttribute('aria-live', 'polite');
            document.body.appendChild(toast);
        }
        clearTimeout(toastTimer);
        toast.className = 'hb-auto-toast ' + kind;
        toast.textContent = message;
        requestAnimationFrame(() => toast.classList.add('show'));
        toastTimer = setTimeout(() => {
            toast.classList.remove('show');
            toastTimer = setTimeout(() => {
                toast.remove();
                if (toastQueue.length) {
                    const next = toastQueue.shift();
                    showToast(next.message, next.kind, next.duration);
                }
            }, 220);
        }, duration);
    }

    function clearToast() {
        clearTimeout(toastTimer);
        toastTimer = null;
        toastQueue = [];
        document.querySelector('.hb-auto-toast')?.remove();
    }

    function showResult(result) {
        const failures = Array.isArray(result.failures) ? result.failures : [];
        const moved = Number(result.movedItems) || 0;
        const messages = [];
        if (result.error) {
            messages.push({ message: result.error, kind: result.ok ? 'success' : 'failure', duration: 4200 });
            if (!moved || !failures.length) {
                toastQueue = [];
                showToast(messages[0].message, messages[0].kind, messages[0].duration);
                return;
            }
        }
        messages.push({
            message: failures.length ? `已转移 ${moved} 件物品，${failures.length} 类物品未能转移。` : `自动分类成功，共转移 ${moved} 件物品。`,
            kind: failures.length ? 'failure' : 'success',
            duration: 3000
        });
        failures.forEach((failure, index) => messages.push({
            message: `未转移 ${index + 1}/${failures.length}：${failure.name || '未命名物品'} × ${Number(failure.count) || 0}\n${failure.reason || '目标容器未能接收。'}`,
            kind: 'failure',
            duration: 3200
        }));
        clearTimeout(toastTimer);
        document.querySelector('.hb-auto-toast')?.remove();
        toastQueue = messages.slice(1);
        if (messages.length) showToast(messages[0].message, messages[0].kind, messages[0].duration);
    }

    window.addEventListener('message', event => {
        let message = event.data;
        if (typeof message === 'string') { try { message = JSON.parse(message); } catch (_) { return; } }
        if (!message || message.type !== 'HB_AUTO_CLASSIFY_RESULT') return;
        const result = parseData(message.data);
        // Results are broadcast to WebUI pages; ignore stale/replayed results
        // after the Backpack page is reopened or a newer request has started.
        if (!pendingRequestId || result.requestId !== pendingRequestId) return;
        pendingRequestId = null;
        clearTimeout(responseTimer); responseTimer = null;
        running = false;
        document.querySelectorAll('.hb-auto-button').forEach(button => { button.disabled = false; button.textContent = '自动归类'; });
        showResult(result);
    });

    function autoClassify(side) {
        if (running) return;
        running = true;
        const requestId = `${Date.now()}-${++requestSequence}`;
        pendingRequestId = requestId;
        document.querySelectorAll('.hb-auto-button').forEach(button => { button.disabled = true; button.textContent = '归类中…'; });
        window.parent.postMessage({ type: 'HB_AUTO_CLASSIFY', data: { side: side || 'A', requestId }, sourcePageId: 'Backpack' }, '*');
        responseTimer = setTimeout(() => {
            pendingRequestId = null;
            running = false;
            document.querySelectorAll('.hb-auto-button').forEach(current => { current.disabled = false; current.textContent = '自动归类'; });
            showResult({ ok: false, error: '没有收到游戏自动归类服务的响应。请确认插件已更新并重新启动游戏。' });
        }, 30000);
    }

    window.addEventListener('pagehide', () => {
        pendingRequestId = null;
        running = false;
        clearTimeout(responseTimer);
        responseTimer = null;
        clearToast();
    });

    function decorate() {
        const slots = [...document.querySelectorAll('.backpack-container .bag-slot')];
        slots.forEach((slot, index) => {
            const side = index === 0 ? 'A' : 'B';
            slot.dataset.hbAutoSide = side;
            const sortButton = [...slot.querySelectorAll('.action-btn')].find(button => /一键整理|Sort|整理/.test(button.textContent || ''));
            if (!sortButton || sortButton.parentElement.querySelector('.hb-auto-wrap')) return;
            const wrap = document.createElement('span'); wrap.className = 'hb-auto-wrap unity-interactive';
            const button = document.createElement('button'); button.type = 'button'; button.className = 'hb-auto-button'; button.textContent = '自动归类';
            button.title = '整理当前容器，并按其他容器的官方收纳标签规则转移物品';
            button.onclick = () => autoClassify(side);
            wrap.appendChild(button);
            sortButton.parentElement.insertBefore(wrap, sortButton);
        });
    }

    new MutationObserver(decorate).observe(document.body, { childList: true, subtree: true });
    decorate();
})();

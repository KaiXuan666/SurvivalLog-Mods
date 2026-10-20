(function () {
    'use strict';
    if (window.__hbNavigation100) return;
    window.__hbNavigation100 = true;
    const page = window.HOME_PAGE;
    let custom = false, ready = false, queue = [], waiting = null, scheduled = false;
    function send(value) {
        window.parent.postMessage({ type: 'HB_NAV_READY', data: { ready: value }, sourcePageId: page }, '*');
    }
    // Walk flex children in CSS order, preserving pinned groups and native click handlers.
    // Screen coordinates cannot be used: scrolled tabs may sit behind pinned tabs.
    function tabs() {
        const root = document.querySelector(page === 'ToolTable' ? '.left-tabs' : '.bag-tabs');
        if (!root) return [];
        const selector = page === 'ToolTable' ? '.left-tab' : '.bag-tab';
        const result = [];
        function visit(el) {
            const style = getComputedStyle(el);
            if (style.display === 'none' || style.visibility === 'hidden') return;
            if (el.matches(selector)) {
                if (!el.disabled && !el.classList.contains('locked')) result.push(el);
                return;
            }
            const children = [...el.children];
            if (style.display === 'flex' || style.display === 'inline-flex')
                children.sort((a, b) => (Number(getComputedStyle(a).order) || 0) - (Number(getComputedStyle(b).order) || 0));
            children.forEach(visit);
        }
        visit(root);
        return result;
    }
    function identity(el) {
        return el.dataset.hbOwner || el.dataset.owner || el.id || el;
    }
    function schedule() {
        if (!scheduled) { scheduled = true; requestAnimationFrame(update); }
    }
    function update() {
        scheduled = false;
        const list = tabs(), active = list.findIndex(el => el.classList.contains('active'));
        const canNavigate = custom && list.length > 1 && active >= 0 && !document.querySelector('.hb-overlay');
        if (ready !== canNavigate) { ready = canNavigate; send(ready); }
        if (!canNavigate) { queue = []; waiting = null; return; }
        if (waiting) {
            if (identity(list[active]) === waiting.id) waiting = null;
            else if (performance.now() - waiting.time > 1500) { waiting = null; queue = []; }
            else { schedule(); return; }
        }
        if (!queue.length) return;
        const direction = queue.shift();
        const next = list[(active + direction + list.length) % list.length];
        waiting = { id: identity(next), time: performance.now() };
        next.click();
        // Only move the tab strip; scrollIntoView can scroll the entire inventory panel.
        const scroller = next.closest('.bag-tabs-scroll,.left-tabs-scroll');
        if (scroller) {
            const r = next.getBoundingClientRect(), s = scroller.getBoundingClientRect();
            if (r.left < s.left) scroller.scrollLeft += r.left - s.left;
            else if (r.right > s.right) scroller.scrollLeft += r.right - s.right;
        }
        schedule();
    }
    window.addEventListener('message', function (event) {
        let message = event.data;
        try {
            if (typeof message === 'string') message = JSON.parse(message);
            if (!message) return;
            let data = message.data || {};
            if (typeof data === 'string') data = JSON.parse(data);
            if (data.page !== page) return;
            if (message.type === 'HOME_CATALOG') {
                custom = data.sort === 'name' || data.sort === 'rank';
                // Existing sorting script schedules its render first.
                schedule();
            } else if (message.type === 'HB_NAV_STEP' && custom && data.direction) {
                if (queue.length < 64) queue.push(data.direction > 0 ? 1 : -1);
                schedule();
            }
        } catch (_) { /* Ignore unrelated malformed UI messages. */ }
    });
    new MutationObserver(schedule).observe(document.body, {
        subtree: true, childList: true, attributes: true, attributeFilter: ['class', 'style', 'disabled']
    });
    window.addEventListener('pagehide', () => { queue = []; waiting = null; send(false); });
    send(false);
})();

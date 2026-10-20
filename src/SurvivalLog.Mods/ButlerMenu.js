// 生存助手：主界面右侧栏的模组按钮 + 设置面板。
//
// 注入到 MainUI.html 末尾。侧栏是静态 HTML（不走 Vue），所以可以直接往里追加节点；
// 外观完全沿用游戏自带的 .side-btn / .side-dot / data-tooltip 样式与 animateSideClick 反馈，
// 只额外加面板本身的样式，避免和游戏 UI 风格脱节。
//
// 三点容易踩的坑，改之前先看：
// 1) 面板用 hidden 属性控制显隐，不用 style.display —— 下方 CSS 里有带 !important 的
//    初始态，行内样式压不过它。
// 2) 按钮插在侧栏里、而面板挂在 body 上：面板要自己计算坐标贴着侧栏左侧弹出，
//    不能写死 right/bottom（分辨率与缩放一变就飘）。
// 3) 页面运行在 iframe 里，Unity 广播回来的消息不带 sourcePageId，
//    所以只能靠 message.type 判断，不能按来源过滤。
(function () {
    'use strict';

    var BTN_ID = 'sl-butler-btn';
    var PANEL_ID = 'sl-butler-panel';
    var STYLE_ID = 'sl-butler-style';

    var state = {
        open: false,
        enabled: true,
        speed: 3,
        exposure: 5,
        min: 1,
        max: 20,
        ready: false
    };

    function send(type, data) {
        // 主界面本身就跑在 webui-core 里，优先用 core 的通道（core 在页面 head 后的
        // 内联脚本里创建，和本脚本同一作用域）；拿不到时退回 postMessage 兜底。
        try {
            if (typeof core !== 'undefined' && core && typeof core.UnitySendEvent === 'function') {
                core.UnitySendEvent(type, data || {});
                return;
            }
        } catch (e) { /* core 尚未就绪时走下面的兜底 */ }
        var message = { type: type, data: data || {}, sourcePageId: 'MainUI' };
        if (window.parent) window.parent.postMessage(message, '*');
    }

    // ---- 面板样式：只定义自己的类，复用游戏配色（琥珀 #EBA308 + 深棕底）----
    // 前缀统一 slb-，避免和页面已有类名（.sl-* 可能被他处使用）打架。
    var CSS = [
        '#sl-butler-panel, #sl-butler-panel * { box-sizing: border-box; }',
        // 初始态用 !important：页面里 body 上的通用规则可能会给后代元素指定 display。
        '#sl-butler-panel[hidden] { display: none !important; }',
        '#sl-butler-panel {',
        '  position: fixed; right: auto; bottom: auto;',
        '  width: 320px; z-index: 900; pointer-events: auto;',
        '  background: rgba(12, 9, 6, 0.94); border: 1px solid rgba(235,163,8,0.42);',
        '  border-radius: 8px; padding: 14px 16px 12px; color: #EDE6D8;',
        '  font-family: "Microsoft YaHei", "Noto Sans SC", sans-serif; font-size: 13px;',
        '  line-height: 1.5; box-shadow: 0 6px 24px rgba(0,0,0,0.65);',
        '}',
        '#sl-butler-panel .slb-title {',
        '  display: flex; align-items: center; justify-content: space-between;',
        '  font-size: 14px; font-weight: 700; color: #EBA308; letter-spacing: 1px;',
        '  padding-bottom: 10px; margin-bottom: 12px; border-bottom: 1px solid rgba(235,163,8,0.22);',
        '}',
        '#sl-butler-panel .slb-close {',
        '  cursor: pointer; color: #9a927f; font-size: 15px; line-height: 1; padding: 2px 7px;',
        '  border-radius: 4px; background: transparent; border: none; font-family: inherit;',
        '}',
        '#sl-butler-panel .slb-close:hover { color: #EBA308; background: rgba(235,163,8,0.12); }',
        '#sl-butler-panel .slb-row { margin-bottom: 13px; }',
        '#sl-butler-panel .slb-row:last-of-type { margin-bottom: 0; }',
        '#sl-butler-panel .slb-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 6px; }',
        '#sl-butler-panel .slb-label { color: #CFC6B4; }',
        '#sl-butler-panel .slb-value { color: #EBA308; font-weight: 700; font-variant-numeric: tabular-nums; }',
        '#sl-butler-panel .slb-hint { color: #857D6D; font-size: 11px; margin-top: 5px; line-height: 1.45; }',
        '#sl-butler-panel .slb-dim { opacity: 0.38; pointer-events: none; }',

        '#sl-butler-panel .slb-switch {',
        '  position: relative; width: 40px; height: 20px; border-radius: 10px; cursor: pointer;',
        '  background: #4a4438; transition: background 0.15s ease; flex: 0 0 auto;',
        '}',
        '#sl-butler-panel .slb-switch.slb-on { background: #4E9A51; }',
        '#sl-butler-panel .slb-switch i {',
        '  position: absolute; top: 2px; left: 2px; width: 16px; height: 16px; border-radius: 50%;',
        '  background: #F5F0E4; transition: transform 0.15s ease;',
        '}',
        '#sl-butler-panel .slb-switch.slb-on i { transform: translateX(20px); }',

        '#sl-butler-panel input[type=range] {',
        '  -webkit-appearance: none; appearance: none; width: 100%; height: 4px; border-radius: 2px;',
        '  background: #4a4438; outline: none; cursor: pointer; display: block; margin: 0; padding: 0;',
        '}',
        '#sl-butler-panel input[type=range]::-webkit-slider-thumb {',
        '  -webkit-appearance: none; appearance: none; width: 14px; height: 14px; border-radius: 50%;',
        '  background: #EBA308; border: 1px solid rgba(0,0,0,0.45); cursor: pointer;',
        '}',
        '#sl-butler-panel .slb-toast {',
        '  margin-top: 11px; padding-top: 8px; border-top: 1px solid rgba(235,163,8,0.18);',
        '  color: #6BCB77; font-size: 11px; height: 14px; opacity: 0; transition: opacity 0.25s ease;',
        '}',
        '#sl-butler-panel .slb-toast.slb-show { opacity: 1; }'
    ].join('\n');

    // 与侧栏其它图标同一套线性描边风格。
    var ICON_SVG = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" ' +
        'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
        '<circle cx="12" cy="12" r="3"/>' +
        '<path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/>' +
        '</svg>';

    function ensureStyle() {
        if (document.getElementById(STYLE_ID)) return;
        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = CSS;
        document.head.appendChild(style);
    }

    function sidebar() {
        return document.querySelector('.sidebar');
    }

    // ---- 侧栏按钮 ----
    function ensureButton() {
        ensureStyle();
        var bar = sidebar();
        if (!bar) return false;   // 页面还没渲染完，下次再试

        var btn = document.getElementById(BTN_ID);
        if (!btn) {
            btn = document.createElement('div');
            btn.id = BTN_ID;
            // 复用游戏自己的类，分隔线、悬停效果、tooltip 气泡都是现成的。
            btn.className = 'side-btn';
            btn.setAttribute('data-tooltip', '生存助手');
            btn.innerHTML = ICON_SVG + '<span class="side-dot" style="display:none"></span>';
            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                // 沿用游戏的按下动画；不传 data-act，避免它额外发一条 Unity 消息。
                if (typeof animateSideClick === 'function') animateSideClick(btn);
                togglePanel();
            });
        }
        // 挂到侧栏末尾：游戏后续若动态增删按钮，这里每次都会把它补回最后。
        if (btn.parentNode !== bar) bar.appendChild(btn);
        applyButtonState();
        return true;
    }

    function applyButtonState() {
        var btn = document.getElementById(BTN_ID);
        if (!btn) return;
        // 关闭时把图标压暗，一眼能看出人物增强没开；开启时点一颗小绿点。
        btn.style.opacity = state.enabled ? '1' : '0.55';
        btn.setAttribute('data-tooltip', state.enabled ? '生存助手（已开启）' : '生存助手（已关闭）');
        var dot = btn.querySelector('.side-dot');
        if (dot) {
            dot.style.display = state.enabled ? 'block' : 'none';
            dot.style.background = '#6BCB77';
            dot.style.boxShadow = '0 0 8px rgba(107, 203, 119, 0.9)';
        }
    }

    // ---- 面板 ----
    function buildPanel() {
        var panel = document.createElement('div');
        panel.id = PANEL_ID;
        panel.hidden = true;
        panel.innerHTML = [
            '<div class="slb-title"><span>生存助手</span><button class="slb-close" type="button">✕</button></div>',
            '<div class="slb-row">',
            '  <div class="slb-head"><span class="slb-label">人物增强</span>',
            '    <div class="slb-switch" data-role="toggle"><i></i></div>',
            '  </div>',
            '  <div class="slb-hint">关闭后移动速度与探索暴露度恢复原版数值，立即生效。</div>',
            '</div>',
            '<div class="slb-row" data-role="body">',
            '  <div class="slb-head"><span class="slb-label">移动速度</span><span class="slb-value" data-role="speed-text">x3</span></div>',
            '  <input type="range" min="1" max="20" step="1" value="3" data-role="speed">',
            '</div>',
            '<div class="slb-row" data-role="body">',
            '  <div class="slb-head"><span class="slb-label">探索暴露度上限</span><span class="slb-value" data-role="exposure-text">x5</span></div>',
            '  <input type="range" min="1" max="20" step="1" value="5" data-role="exposure">',
            '</div>',
            '<div class="slb-toast" data-role="toast"></div>'
        ].join('');
        document.body.appendChild(panel);

        panel.querySelector('.slb-close').addEventListener('click', function (e) {
            e.stopPropagation();
            setPanelOpen(false);
        });
        // 面板内的点击不要冒泡到游戏背景（点空白处可能触发别的交互）。
        panel.addEventListener('mousedown', function (e) { e.stopPropagation(); });
        panel.addEventListener('click', function (e) { e.stopPropagation(); });

        var toggle = panel.querySelector('[data-role=toggle]');
        toggle.addEventListener('click', function (e) {
            e.stopPropagation();
            state.enabled = !state.enabled;
            render();
            commit();
        });

        ['speed', 'exposure'].forEach(function (key) {
            var input = panel.querySelector('[data-role=' + key + ']');
            input.addEventListener('input', function () {
                state[key] = parseFloat(input.value);
                render();
            });
            // 松手才提交：拖动过程中不必每帧写 cfg 文件。
            input.addEventListener('change', function () { commit(); });
        });

        return panel;
    }

    function ensurePanel() {
        return document.getElementById(PANEL_ID) || buildPanel();
    }

    function render() {
        applyButtonState();
        var panel = ensurePanel();
        var toggle = panel.querySelector('[data-role=toggle]');
        if (state.enabled) toggle.classList.add('slb-on');
        else toggle.classList.remove('slb-on');

        var speed = panel.querySelector('[data-role=speed]');
        var exposure = panel.querySelector('[data-role=exposure]');
        speed.min = state.min; speed.max = state.max;
        exposure.min = state.min; exposure.max = state.max;
        speed.value = state.speed;
        exposure.value = state.exposure;
        panel.querySelector('[data-role=speed-text]').textContent = 'x' + fmt(state.speed);
        panel.querySelector('[data-role=exposure-text]').textContent = 'x' + fmt(state.exposure);

        var rows = panel.querySelectorAll('[data-role=body]');
        for (var i = 0; i < rows.length; i++) {
            if (state.enabled) rows[i].classList.remove('slb-dim');
            else rows[i].classList.add('slb-dim');
        }
    }

    function fmt(v) {
        return (Math.round(v * 100) / 100).toString();
    }

    // 贴着侧栏左侧弹出：面板挂 body，坐标得自己算，分辨率/缩放变了也跟着对。
    function placePanel() {
        var panel = ensurePanel();
        var btn = document.getElementById(BTN_ID);
        var bar = sidebar();
        var anchor = (btn || bar);
        if (!anchor || !anchor.getBoundingClientRect) return;

        var rect = anchor.getBoundingClientRect();
        var width = panel.offsetWidth || 320;
        var height = panel.offsetHeight || 240;
        var gap = 14;

        // 水平：侧栏在右侧，面板往左展开；贴边时夹回视口内。
        var left = rect.left - width - gap;
        if (left < 8) left = Math.min(rect.right + gap, window.innerWidth - width - 8);
        // 垂直：尽量和按钮底边对齐，超出视口就上移。
        var top = rect.bottom - height;
        if (top < 8) top = 8;
        if (top + height > window.innerHeight - 8) top = Math.max(8, window.innerHeight - height - 8);

        panel.style.left = Math.round(left) + 'px';
        panel.style.top = Math.round(top) + 'px';
    }

    function setPanelOpen(open) {
        state.open = open;
        var panel = ensurePanel();
        if (open) {
            // 先显示再量尺寸：hidden 状态下 offsetWidth/Height 是 0。
            panel.hidden = false;
            render();
            placePanel();
            // 每次打开都重新要一份：cfg 可能在游戏外被改过。
            send('SL_BUTLER_QUERY');
        } else {
            panel.hidden = true;
        }
    }

    function togglePanel() {
        setPanelOpen(!state.open);
    }

    var toastTimer = null;
    function toast(text) {
        var panel = ensurePanel();
        var el = panel.querySelector('[data-role=toast]');
        if (!text) { el.classList.remove('slb-show'); return; }
        el.textContent = text;
        el.classList.add('slb-show');
        if (toastTimer) clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { el.classList.remove('slb-show'); }, 1600);
    }

    function commit() {
        send('SL_BUTLER_APPLY', {
            enabled: !!state.enabled,
            speed: state.speed,
            exposure: state.exposure
        });
    }

    // ---- 与 Unity 通信 ----
    // Unity 的 BroadcastToAllPages 走的是 Root 的协议 10：整页广播，且不带 sourcePageId。
    // 这里只按 type 判断，任意页面发来的都收得到。
    function onMessage(event) {
        var msg = event.data;
        if (!msg || msg.type !== 'SL_BUTLER_STATE') return;
        var d = msg.data;
        if (typeof d === 'string') d = safeParse(d);
        if (!d || typeof d !== 'object') return;
        state.enabled = !!d.enabled;
        if (typeof d.speed === 'number') state.speed = d.speed;
        if (typeof d.exposure === 'number') state.exposure = d.exposure;
        if (typeof d.min === 'number') state.min = d.min;
        if (typeof d.max === 'number') state.max = d.max;
        state.ready = true;
        stopQuerying();
        render();
        if (state.open && d.applied) toast('已保存');
    }

    function safeParse(text) {
        try { return JSON.parse(text); } catch (e) { return null; }
    }

    // ---- 启动 ----
    var queryTimer = null;
    var queryTries = 0;

    // Unity 那边只在收到 QUERY 时才广播当前设置，所以要等页面就绪再问。
    // 页面在 core.notifyReady() 之后才注入本脚本（注入点在 </body> 前，notifyReady 在同页内联脚本末尾），
    // 所以此时发出去的消息已经能到达 C#。留几次补问只是兜底，不指望它。
    function queryUntilReady() {
        if (state.ready || queryTimer) return;
        send('SL_BUTLER_QUERY');
        queryTries++;
        if (queryTries >= 3) return;
        queryTimer = setTimeout(function () {
            queryTimer = null;
            queryUntilReady();
        }, 400);
    }

    function stopQuerying() {
        if (queryTimer) { clearTimeout(queryTimer); queryTimer = null; }
    }

    function boot() {
        if (!document.body) return;
        ensureButton();
        render();
        queryUntilReady();

        // 侧栏可能随语言/状态重绘，定期确认按钮还在（一次 getElementById，开销可忽略）。
        setInterval(ensureButton, 1000);
        // 窗口尺寸变化时，开着的面板要重新贴回侧栏旁边。
        window.addEventListener('resize', function () {
            if (state.open) placePanel();
        });
    }

    if (typeof core !== 'undefined' && core && typeof core.registerHandlers === 'function') {
        // 广播回来的消息在 webui-core 里是以 window message 派发的，
        // 这里同时注册一次，保证两种通道都能收到。
        try { core.registerHandlers({ SL_BUTLER_STATE: function (data) { onMessage({ data: { type: 'SL_BUTLER_STATE', data: data } }); } }); } catch (e) { }
    }
    window.addEventListener('message', onMessage);

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
    else boot();
})();

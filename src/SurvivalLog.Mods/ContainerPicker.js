(function () {
    'use strict';
    const marker = 'hb-container-picker';
    const styleId = marker + '-style';

    if (!document.getElementById(styleId)) {
        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = `
          .hb-container-picker { position:relative; flex:0 0 auto; z-index:10020; }
          .hb-container-picker-button { width:36px; height:36px; padding:0; border-radius:8px; cursor:pointer; color:#fff;
            font:600 20px/1 sans-serif; background:rgba(10,10,12,.78); border:1px solid rgba(255,255,255,.2);
            box-shadow:0 2px 8px rgba(0,0,0,.35); }
          .hb-container-picker-button:hover,.hb-container-picker-button[aria-expanded="true"] { color:#ffd45d; border-color:rgba(255,193,7,.75); background:rgba(42,36,25,.96); }
          .hb-container-picker-menu { position:absolute; top:calc(100% + 7px); right:0; min-width:190px; max-width:min(300px,70vw); max-height:48vh;
            overflow:auto; padding:5px; border-radius:8px; border:1px solid rgba(255,193,7,.55); background:rgba(24,23,23,.98);
            box-shadow:0 8px 24px rgba(0,0,0,.55); }
          .hb-container-picker-option { display:block; width:100%; box-sizing:border-box; border:0; border-radius:5px; padding:9px 12px; text-align:left;
            color:#eee; background:transparent; font:inherit; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; cursor:pointer; }
          .hb-container-picker-option:hover { background:rgba(255,193,7,.2); color:#fff; }
          .hb-container-picker-option.active { color:#ffd45d; background:rgba(255,193,7,.15); }
          .hb-container-picker-empty { padding:10px 12px; color:#aaa; font:14px sans-serif; }
        `;
        document.head.appendChild(style);
    }

    function closeMenus(except) {
        document.querySelectorAll('.hb-container-picker-menu').forEach(menu => {
            const picker = menu.parentElement;
            if (picker !== except) {
                menu.hidden = true;
                picker.querySelector('button')?.setAttribute('aria-expanded', 'false');
            }
        });
    }

    function decorate() {
        const tabStrip = document.querySelector('.bag-panel .header .bag-tabs');
        if (!tabStrip) return;
        const header = tabStrip.closest('.header');
        if (!header) return;
        const tabs = [...tabStrip.querySelectorAll('.bag-tab')];
        if (tabs.length < 2) {
            header.querySelector('.' + marker)?.remove();
            return;
        }
        const anchor = header.querySelector('.tag-title-sep') || header.querySelector('.tag-chips');

        let picker = header.querySelector('.' + marker);
        if (!picker) {
            picker = document.createElement('div');
            picker.className = marker + ' unity-interactive';
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'hb-container-picker-button';
            button.textContent = '▾';
            button.title = '选择其他容器';
            button.setAttribute('aria-label', '选择其他容器');
            button.setAttribute('aria-expanded', 'false');
            button.onclick = event => {
                event.stopPropagation();
                const menu = picker.querySelector('.hb-container-picker-menu');
                const opening = menu.hidden;
                closeMenus(picker);
                menu.hidden = !opening;
                button.setAttribute('aria-expanded', String(opening));
            };
            const menu = document.createElement('div');
            menu.className = 'hb-container-picker-menu';
            menu.hidden = true;
            menu.onclick = event => event.stopPropagation();
            picker.append(button, menu);
            if (anchor) header.insertBefore(picker, anchor);
            else tabStrip.insertAdjacentElement('afterend', picker);
        }

        const menu = picker.querySelector('.hb-container-picker-menu');
        const customNames = window.__hbCustomNames || {};
        const items = tabs.slice(1).map(tab => {
            const owner = tab.dataset.hbOwner;
            let label = (tab.textContent || '').trim();
            if (owner && customNames[owner]) {
                label = customNames[owner];
            }
            return {
                owner: owner,
                label: label,
                active: tab.classList.contains('active')
            };
        }).filter(item => item.owner && item.label);
        const key = JSON.stringify(items.map(item => [item.owner, item.label, item.active]));
        if (menu.dataset.key === key) return;
        menu.dataset.key = key;
        menu.replaceChildren();
        if (!items.length) {
            const empty = document.createElement('div');
            empty.className = 'hb-container-picker-empty';
            empty.textContent = '没有可选容器';
            menu.appendChild(empty);
            return;
        }
        items.forEach(item => {
            const option = document.createElement('button');
            option.type = 'button';
            option.className = 'hb-container-picker-option' + (item.active ? ' active' : '');
            option.dataset.hbOwner = item.owner;
            option.textContent = item.label;
            option.title = item.label;
            option.onclick = event => {
                event.stopPropagation();
                const target = [...document.querySelectorAll('.bag-tabs .bag-tab')]
                    .find(tab => tab.dataset.hbOwner === item.owner);
                if (target) target.click();
                menu.hidden = true;
                picker.querySelector('button').setAttribute('aria-expanded', 'false');
            };
            menu.appendChild(option);
        });
    }

    let pointerDownPosition = null;
    let dragOpenedPicker = null;
    document.addEventListener('pointerdown', event => {
        pointerDownPosition = { x: event.clientX, y: event.clientY };
        dragOpenedPicker = null;
        if (!event.target.closest('.' + marker)) closeMenus(null);
    }, true);
    document.addEventListener('mousemove', event => {
        if (!event.buttons || (event.buttons & 1) === 0) return;
        if (!pointerDownPosition) return;
        const dx = event.clientX - pointerDownPosition.x;
        const dy = event.clientY - pointerDownPosition.y;
        // A normal arrow click can emit a few mousemove events. Treat it as a
        // drag only after the pointer has moved far enough to be intentional.
        if (dx * dx + dy * dy < 36) return;
        const arrow = event.target.closest('.hb-container-picker-button');
        if (!arrow) return;
        const picker = arrow.closest('.' + marker);
        const menu = picker && picker.querySelector('.hb-container-picker-menu');
        if (!menu || !menu.hidden) return;
        closeMenus(picker);
        menu.hidden = false;
        dragOpenedPicker = picker;
        arrow.setAttribute('aria-expanded', 'true');
        // The game's tab-drop helper caches target rectangles at drag start.
        // Re-arm it now that the menu options have real, visible rectangles.
        if (typeof window.__hbContainerPickerRearmTabDrop === 'function')
            window.__hbContainerPickerRearmTabDrop();
    }, true);
    window.addEventListener('mouseup', () => {
        pointerDownPosition = null;
        const picker = dragOpenedPicker;
        dragOpenedPicker = null;
        // Menus opened by a drag stay available while hovering over targets,
        // then close on release. A normal click-opened menu remains visible.
        if (picker) setTimeout(() => closeMenus(null), 0);
    }, true);
    new MutationObserver(decorate).observe(document.body, { childList:true, subtree:true, attributes:true, attributeFilter:['class','data-hb-owner'] });
    decorate();
})();

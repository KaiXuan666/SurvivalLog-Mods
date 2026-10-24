(function () {
    'use strict';
    const marker = 'hb-container-rename';
    const styleId = marker + '-style';

    if (!document.getElementById(styleId)) {
        const style = document.createElement('style');
        style.id = styleId;
        style.textContent = `
          .hb-rename-btn {
            display: inline-flex; align-items: center; justify-content: center;
            width: 24px; height: 24px; margin-left: 6px; vertical-align: middle;
            background: rgba(255, 255, 255, 0.08); border: 1px solid rgba(255, 255, 255, 0.25);
            border-radius: 5px; color: #ffd578; cursor: pointer; font-size: 13px;
            transition: all 0.15s ease; padding: 0; box-sizing: border-box; flex-shrink: 0;
          }
          .hb-rename-btn:hover {
            background: rgba(255, 193, 7, 0.25); border-color: rgba(255, 193, 7, 0.7);
            color: #fff; transform: scale(1.05);
          }
          .hb-rename-input {
            font: inherit; font-size: 16px; font-weight: bold; color: #ffd578;
            background: rgba(20, 18, 16, 0.98); border: 1.5px solid rgba(255, 193, 7, 0.9);
            border-radius: 5px; padding: 2px 8px; width: 170px; max-width: 65%;
            outline: none; box-shadow: 0 0 10px rgba(255, 193, 7, 0.4); box-sizing: border-box;
            z-index: 10002; position: relative;
          }
          .hb-tab-rename-input {
            font: inherit; font-size: 13px; font-weight: bold; color: #ffd578;
            background: rgba(20, 18, 16, 0.98); border: 1.5px solid rgba(255, 193, 7, 0.9);
            border-radius: 4px; padding: 1px 6px; width: 110px; max-width: 140px;
            outline: none; box-shadow: 0 0 8px rgba(255, 193, 7, 0.4); box-sizing: border-box;
            z-index: 10002; position: relative;
          }
          .hb-tabs-rename-btn {
            display: inline-flex; align-items: center; justify-content: center;
            width: 28px; height: 28px; margin-left: 6px; margin-right: 4px; vertical-align: middle;
            background: rgba(255, 255, 255, 0.08); border: 1px solid rgba(255, 255, 255, 0.25);
            border-radius: 6px; color: #ffd578; cursor: pointer; font-size: 14px;
            transition: all 0.15s ease; padding: 0; box-sizing: border-box; flex-shrink: 0;
          }
          .hb-tabs-rename-btn:hover {
            background: rgba(255, 193, 7, 0.25); border-color: rgba(255, 193, 7, 0.7);
            color: #fff; transform: scale(1.05);
          }
          .hb-rename-shield {
            position: fixed; inset: 0; z-index: 10001; background: transparent;
            display: block; pointer-events: auto;
          }
        `;
        document.head.appendChild(style);
    }

    window.__hbCustomNames = window.__hbCustomNames || {};
    const customNames = window.__hbCustomNames;

    let activeEditing = null; // { ownerId, input, save, cancel }
    let shieldEl = null;
    let composing = false;
    let compositionEnded = 0;

    function sendEvent(type, data) {
        if (window.core && typeof window.core.UnitySendEvent === 'function') {
            window.core.UnitySendEvent(type, typeof data === 'string' ? data : JSON.stringify(data));
        } else {
            window.parent.postMessage({ type: type, data: data, sourcePageId: 'BackpackUI' }, '*');
        }
    }

    function setKeyboardState(enabled) {
        sendEvent('HB_NAME_EDIT_FOCUS', { focused: !!enabled });
        sendEvent('Btn_NameEditFocus', JSON.stringify({ focused: !!enabled }));
    }

    function createShield() {
        if (!shieldEl) {
            shieldEl = document.createElement('div');
            shieldEl.className = 'hb-rename-shield unity-interactive';
            document.body.appendChild(shieldEl);
        }
    }

    function removeShield() {
        if (shieldEl) {
            shieldEl.remove();
            shieldEl = null;
        }
    }

    function stopEditing() {
        if (!activeEditing) return;
        setKeyboardState(false);
        removeShield();
        activeEditing = null;
    }

    function commitRename(ownerId, newName, callback) {
        const trimmed = (newName || '').trim();
        stopEditing();

        if (!trimmed) {
            if (typeof callback === 'function') callback(null);
            return;
        }

        const safeOwner = ownerId || 'backpack';
        sendEvent('HB_RENAME_CONTAINER', { ownerId: String(safeOwner), name: trimmed });
        sendEvent('SET_FURNITURE_NAME', { name: trimmed });

        if (typeof callback === 'function') callback(trimmed);
        updateAllNames(safeOwner, trimmed);
    }

    function syncDomNames() {
        // 1. 同步左侧所有页签
        const tabs = document.querySelectorAll('.bag-tabs .bag-tab');
        tabs.forEach((tab, index) => {
            let target = null;
            if (index === 0 && (customNames['backpack'] || customNames['0'])) {
                target = customNames['backpack'] || customNames['0'];
            } else {
                const owner = tab.dataset.hbOwner;
                if (owner && customNames[owner]) {
                    target = customNames[owner];
                }
            }
            if (target) {
                applyTextToNode(tab, target);
            }
        });

        // 2. 同步下拉菜单选项（若已渲染）
        const options = document.querySelectorAll('.hb-container-picker-option');
        options.forEach((opt) => {
            const owner = opt.dataset.hbOwner;
            if (owner && customNames[owner]) {
                const target = customNames[owner];
                if (opt.textContent !== target) {
                    opt.textContent = target;
                    opt.title = target;
                }
            }
        });

        // 3. 同步面板主标题
        if ((customNames['backpack'] || customNames['0']) && !activeEditing) {
            const bName = customNames['backpack'] || customNames['0'];
            const panelA = document.querySelector('.bag-slot:first-child .panel-title');
            if (panelA && panelA.textContent !== bName) {
                panelA.textContent = bName;
            }
        }
        if (window.__hbRightBag && window.__hbRightBag.ownerId && !activeEditing) {
            const rightOwner = String(window.__hbRightBag.ownerId);
            if (customNames[rightOwner]) {
                const panelB = document.querySelector('.is-storage .panel-title');
                if (panelB && panelB.textContent !== customNames[rightOwner]) {
                    panelB.textContent = customNames[rightOwner];
                }
            }
        }
    }

    function updateAllNames(ownerId, name) {
        if (!name) return;
        const safeOwner = String(ownerId || 'backpack');
        customNames[safeOwner] = name;
        if (safeOwner === 'backpack' || safeOwner === '0') {
            customNames['backpack'] = name;
            customNames['0'] = name;
        }
        syncDomNames();
    }

    function applyTextToNode(el, text) {
        if (!text) return;
        const textNode = [...el.childNodes].find(n => n.nodeType === 3);
        if (textNode) {
            if (textNode.nodeValue !== text) {
                textNode.nodeValue = text;
            }
        } else {
            if (el.textContent !== text) {
                el.textContent = text;
            }
        }
        el.title = text;
    }

    // 绑定输入法放行与按键拦截逻辑
    function bindImeAndKeyTrap(input, onSave, onCancel) {
        input.addEventListener('compositionstart', () => { composing = true; });
        input.addEventListener('compositionend', () => {
            composing = false;
            compositionEnded = performance.now();
        });

        const keyHandler = (e) => {
            if (!activeEditing) return;
            // 关键：若正在输入法选词/拼音中或虚拟键 229，放行输入法
            if (composing || e.isComposing || e.keyCode === 229) {
                return;
            }

            // 拦截非输入法状态的按键冒泡，防止触发游戏背包快捷键（如 A/D 切换容器）
            e.stopImmediatePropagation();

            if (e.key === 'Escape') {
                e.preventDefault();
                onCancel();
            } else if (e.key === 'Enter' && performance.now() - compositionEnded > 250) {
                e.preventDefault();
                onSave();
            }
        };

        document.addEventListener('keydown', keyHandler, true);

        // 点击外部遮罩保存并关闭
        const clickOutsideHandler = (e) => {
            if (!activeEditing) return;
            if (!input.contains(e.target)) {
                e.preventDefault();
                e.stopImmediatePropagation();
                onSave();
            }
        };

        for (const evt of ['mousedown', 'mouseup', 'click', 'pointerdown', 'pointerup']) {
            input.addEventListener(evt, (e) => e.stopPropagation());
            document.addEventListener(evt, clickOutsideHandler, true);
        }

        return () => {
            document.removeEventListener('keydown', keyHandler, true);
            for (const evt of ['mousedown', 'mouseup', 'click', 'pointerdown', 'pointerup']) {
                document.removeEventListener(evt, clickOutsideHandler, true);
            }
        };
    }

    // 1. 就地编辑右侧或单背包面板的主标题
    function startTitleEdit(panel, titleEl, ownerId) {
        if (activeEditing) activeEditing.save();

        const currentName = (titleEl.textContent || '').trim();
        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'hb-rename-input unity-interactive';
        input.value = currentName;
        input.maxLength = 16;
        input.placeholder = '输入容器名称...';

        // 仅隐藏当前面板标题，并在原地插入输入框
        titleEl.style.display = 'none';
        titleEl.insertAdjacentElement('afterend', input);

        createShield();
        setKeyboardState(true);
        input.focus();
        input.select();

        let cleaned = false;
        let cleanupTrap = null;

        const save = () => {
            if (cleaned) return;
            cleaned = true;
            if (cleanupTrap) cleanupTrap();
            const val = input.value;
            input.remove();
            titleEl.style.display = '';
            commitRename(ownerId, val, (res) => {
                if (res) titleEl.textContent = res;
            });
        };

        const cancel = () => {
            if (cleaned) return;
            cleaned = true;
            if (cleanupTrap) cleanupTrap();
            input.remove();
            titleEl.style.display = '';
            stopEditing();
        };

        cleanupTrap = bindImeAndKeyTrap(input, save, cancel);
        activeEditing = { ownerId, input, save, cancel };
    }

    // 2. 就地编辑左侧某个页签（原地将页签文字替换为微型输入框）
    function startTabEdit(tabEl) {
        if (activeEditing) activeEditing.save();

        const tabs = [...document.querySelectorAll('.bag-tabs .bag-tab')];
        const tabIndex = tabs.indexOf(tabEl);
        const ownerId = (tabIndex === 0) ? 'backpack' : (tabEl.dataset.hbOwner || '');

        const textNode = [...tabEl.childNodes].find(n => n.nodeType === 3);
        const currentName = (textNode ? textNode.nodeValue : tabEl.textContent || '').trim();

        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'hb-tab-rename-input unity-interactive';
        input.value = currentName;
        input.maxLength = 16;
        input.placeholder = '名称...';

        // 隐藏原文字节点，在页签内部原地挂载输入框
        if (textNode) textNode.nodeValue = '';
        tabEl.appendChild(input);

        createShield();
        setKeyboardState(true);
        input.focus();
        input.select();

        let cleaned = false;
        let cleanupTrap = null;

        const restore = (val) => {
            input.remove();
            applyTextToNode(tabEl, val);
        };

        const save = () => {
            if (cleaned) return;
            cleaned = true;
            if (cleanupTrap) cleanupTrap();
            const val = input.value;
            commitRename(ownerId, val, (res) => {
                restore(res || currentName);
            });
        };

        const cancel = () => {
            if (cleaned) return;
            cleaned = true;
            if (cleanupTrap) cleanupTrap();
            stopEditing();
            restore(currentName);
        };

        cleanupTrap = bindImeAndKeyTrap(input, save, cancel);
        activeEditing = { ownerId, input, save, cancel };
    }

    function decorate() {
        // 1. 处理右侧面板（B 面板已打开的容器）和左侧单背包面板（无页签时）
        const panels = document.querySelectorAll('.bag-panel');
        panels.forEach((panel, panelIndex) => {
            const header = panel.querySelector('.header');
            if (!header) return;

            const title = header.querySelector('.panel-title');
            if (title && title.offsetParent !== null) {
                let ownerId = '';
                const isRightPanel = panel.classList.contains('is-storage');
                if (isRightPanel) {
                    ownerId = String(window.__hbRightBag?.ownerId || '');
                } else if (panelIndex === 0) {
                    ownerId = 'backpack';
                }

                let btn = header.querySelector('.' + marker);
                if (!btn) {
                    btn = document.createElement('button');
                    btn.type = 'button';
                    btn.className = marker + ' hb-rename-btn unity-interactive';
                    btn.title = isRightPanel ? '修改已打开容器名称' : '修改背包名称';
                    btn.innerHTML = '✎';
                    title.insertAdjacentElement('afterend', btn);
                }

                btn.onclick = (e) => {
                    e.stopPropagation();
                    let curOwner = ownerId;
                    if (isRightPanel) {
                        curOwner = String(window.__hbRightBag?.ownerId || curOwner);
                    } else if (!curOwner) {
                        curOwner = 'backpack';
                    }
                    // 在本面板的标题处原地展开输入框
                    startTitleEdit(panel, title, curOwner);
                };
            } else {
                header.querySelector('.' + marker)?.remove();
            }

            // 2. 处理左侧多页签模式（扩展的其他容器）
            const tabStrip = header.querySelector('.bag-tabs');
            if (tabStrip) {
                const tabs = tabStrip.querySelectorAll('.bag-tab');
                tabs.forEach((tab) => {
                    if (!tab.dataset.hbRenameBound) {
                        tab.dataset.hbRenameBound = '1';
                        // 支持直接双击任意页签原地进入编辑
                        tab.addEventListener('dblclick', (e) => {
                            e.stopPropagation();
                            startTabEdit(tab);
                        });
                    }
                });

                // 在页签工具区放置“✎”按钮（紧邻下拉选择箭头）
                let tabsRenameBtn = header.querySelector('.hb-tabs-rename-btn');
                if (!tabsRenameBtn) {
                    tabsRenameBtn = document.createElement('button');
                    tabsRenameBtn.type = 'button';
                    tabsRenameBtn.className = 'hb-tabs-rename-btn unity-interactive';
                    tabsRenameBtn.title = '修改当前页签容器名称 (或双击页签)';
                    tabsRenameBtn.innerHTML = '✎';

                    const anchor = header.querySelector('.hb-container-picker') ||
                                   header.querySelector('.tag-title-sep') ||
                                   header.querySelector('.tag-chips');
                    if (anchor) {
                        header.insertBefore(tabsRenameBtn, anchor);
                    } else {
                        tabStrip.insertAdjacentElement('afterend', tabsRenameBtn);
                    }
                }

                tabsRenameBtn.onclick = (e) => {
                    e.stopPropagation();
                    const activeTab = tabStrip.querySelector('.bag-tab.active');
                    if (activeTab) {
                        // 在当前激活的左侧页签处原地展开输入框
                        startTabEdit(activeTab);
                    }
                };
            }
        });

        // 每次 DOM 扫描后，持续将自定义名称同步至所有页签和下拉菜单项
        syncDomNames();
    }

    // 监听后端的改名结果广播与全量名称同步广播
    window.addEventListener('message', function (ev) {
        let m = ev.data;
        if (typeof m === 'string') {
            try { m = JSON.parse(m); } catch (e) { return; }
        }
        if (!m || !m.type) return;

        if (m.type === 'HB_RENAME_CONTAINER_RESULT') {
            let d = m.data || {};
            if (typeof d === 'string') {
                try { d = JSON.parse(d); } catch (e) { d = {}; }
            }
            if (d.ownerId && d.name) {
                updateAllNames(d.ownerId, d.name);
            }
        } else if (m.type === 'HB_CONTAINER_NAMES_SYNC') {
            let d = m.data || {};
            if (typeof d === 'string') {
                try { d = JSON.parse(d); } catch (e) { d = {}; }
            }
            if (typeof d === 'object') {
                for (const k of Object.keys(d)) {
                    if (d[k]) {
                        customNames[String(k)] = d[k];
                    }
                }
                syncDomNames();
            }
        }
    });

    // 页面启动时向后端请求全量名称列表
    sendEvent('HB_REQUEST_CONTAINER_NAMES', {});
    setTimeout(() => sendEvent('HB_REQUEST_CONTAINER_NAMES', {}), 300);

    new MutationObserver(decorate).observe(document.body, { childList: true, subtree: true });
})();

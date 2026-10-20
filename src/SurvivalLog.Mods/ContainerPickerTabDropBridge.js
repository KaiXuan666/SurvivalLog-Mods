(function (global) {
    'use strict';
    if (typeof global.createTabDrop !== 'function' || global.__hbContainerPickerTabDropWrapped) return;

    const createOriginal = global.createTabDrop;
    const instances = [];
    global.__hbContainerPickerTabDropWrapped = true;
    global.createTabDrop = function (opts) {
        const originalGetTabs = opts && opts.getTabs;
        if (!originalGetTabs) return createOriginal(opts);
        const wrappedOpts = Object.assign({}, opts, {
            getTabs: function () {
                const tabs = originalGetTabs() || [];
                const byOwner = new Map();
                tabs.forEach(tab => {
                    const owner = tab && tab.el && tab.el.dataset && tab.el.dataset.hbOwner;
                    if (owner !== undefined) byOwner.set(String(owner), tab);
                });
                document.querySelectorAll('.hb-container-picker-option[data-hb-owner]').forEach(option => {
                    const nativeTab = byOwner.get(option.dataset.hbOwner);
                    if (!nativeTab) return;
                    tabs.push({ el: option, key: nativeTab.key, blocked: nativeTab.blocked });
                });
                return tabs;
            }
        });
        const instance = createOriginal(wrappedOpts);
        const armOriginal = instance.arm;
        instance.arm = function () {
            armOriginal();
            return instance;
        };
        instances.push(instance);
        return instance;
    };
    global.__hbContainerPickerRearmTabDrop = function () {
        instances.forEach(instance => { if (instance.isArmed()) instance.arm(); });
    };
})(window);

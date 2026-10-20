(function () {
    'use strict';

    // 制作台右键配方 = 把配方装上工作台，但不开始制作。
    // 原生点击（RECIPE_CLICK）在部分配方上会紧接着触发 CRAFT，玩家就来不及在
    // 预览区选染色，所以先告诉游戏「这一次是右键」，由 C# 侧把随之而来的制作拦掉。
    document.addEventListener('contextmenu', event => {
        const recipe = event.target && event.target.closest
            ? event.target.closest('#recipe-list .recipe-item')
            : null;
        if (!recipe || (event.target.closest && event.target.closest('.recipe-expand'))) return;

        event.preventDefault();
        event.stopPropagation();

        try {
            core.UnitySendEvent('SL_RECIPE_PEEK', { source: 'contextmenu' });
        } catch (e) { /* 老版本页面没有 core 时退化为普通选择 */ }

        recipe.dispatchEvent(new MouseEvent('click', {
            bubbles: true,
            cancelable: true,
            view: window,
            button: 0,
            buttons: 1
        }));
    }, true);
})();

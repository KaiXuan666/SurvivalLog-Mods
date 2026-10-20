(function () {
    'use strict';

    // 制作台右键配方 = 把配方装上工作台，但不开始制作，好让玩家先选染色。
    //
    // 两条并行的信号通路，任一到达即可：
    //   1) 往父页面发 SL_RECIPE_PEEK（Root.html 会把任意消息类型转给 Unity，
    //      不依赖本页面里的 core 是不是拿得到）；
    //   2) 给紧接着那一次 RECIPE_CLICK 的 json 加一个 slPeek 标记。
    var PAGE_ID = 'ToolTable';
    var peekArmedUntil = 0;

    function sendPeek() {
        try {
            window.parent.postMessage(
                { type: 'SL_RECIPE_PEEK', data: { source: 'rightclick' }, sourcePageId: PAGE_ID },
                '*');
        } catch (e) { /* 没有父框架时退化，只靠第二条通路 */ }
    }

    // 拿得到页面的 core 就把 UnitySendEvent 包一层，给 RECIPE_CLICK 打标记。
    function wrapCore() {
        try {
            if (typeof core === 'undefined' || !core || core.__slPeekWrapped) return;
            var original = core.UnitySendEvent;
            if (typeof original !== 'function') return;
            core.__slPeekWrapped = true;
            core.UnitySendEvent = function (action, data) {
                if (action === 'RECIPE_CLICK' && Date.now() < peekArmedUntil) {
                    data = data || {};
                    try { data.slPeek = true; } catch (e) { }
                }
                return original.call(core, action, data);
            };
        } catch (e) { }
    }

    function recipeFrom(target) {
        if (!target || !target.closest) return null;
        return target.closest('.recipe-item');
    }

    function loadWithoutCrafting(recipe) {
        peekArmedUntil = Date.now() + 5000;
        wrapCore();
        sendPeek();
        recipe.dispatchEvent(new MouseEvent('click', {
            bubbles: true,
            cancelable: true,
            view: window,
            button: 0,
            buttons: 1
        }));
    }

    // 用 mousedown 而不是 contextmenu：嵌入式 WebView 里右键菜单事件不一定派发得出来，
    // 但 mousedown 一定会。contextmenu 只负责把原生菜单压掉。
    document.addEventListener('mousedown', function (event) {
        if (event.button !== 2) return;
        var recipe = recipeFrom(event.target);
        if (!recipe) return;

        event.preventDefault();
        event.stopPropagation();
        loadWithoutCrafting(recipe);
    }, true);

    document.addEventListener('contextmenu', function (event) {
        if (!recipeFrom(event.target)) return;
        event.preventDefault();
        event.stopPropagation();
    }, true);
})();

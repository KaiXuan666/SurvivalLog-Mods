(function () {
    'use strict';

    // ToolTable's native recipe selection populates the workbench preview,
    // including its native dye swatches (DYE_PICK). Right-click a recipe to
    // select it without changing the existing primary-click behavior.
    document.addEventListener('contextmenu', event => {
        const recipe = event.target && event.target.closest
            ? event.target.closest('#recipe-list .recipe-item')
            : null;
        if (!recipe || (event.target.closest && event.target.closest('.recipe-expand'))) return;

        event.preventDefault();
        event.stopPropagation();
        recipe.dispatchEvent(new MouseEvent('click', {
            bubbles: true,
            cancelable: true,
            view: window,
            button: 0,
            buttons: 1
        }));
    }, true);
})();

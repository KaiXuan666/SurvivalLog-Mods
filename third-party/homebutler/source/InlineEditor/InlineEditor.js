(function () {
    'use strict';
    if (window.HOME_PAGE !== 'BackpackUI' || window.__hbInline111) return;
    window.__hbInline111 = true;
    const page = 'BackpackUI';
    let catalog = { containers: [], session: '', sort: 'native' };
    let editing = null, saving = false, shield = null, scheduled = false, timer;
    let rightOwner='', rightOriginal='', composing=false, compositionEnded=0;
    const bar = document.createElement('div');
    bar.className = 'hb-inline unity-interactive';
    bar.hidden = true;
    bar.innerHTML = '<span class="hi-title">当前容器</span><input class="hi-name" aria-label="当前容器名称" maxlength="40" placeholder="名称（留空恢复）"><input class="hi-rank" aria-label="排序序号" type="number" min="-9999" max="9999" step="1" placeholder="序号"><button class="hi-edit">修改</button><button class="hi-save" hidden>保存</button><button class="hi-cancel" hidden>取消</button><span class="hi-status"></span>';
    const style = document.createElement('style');
    style.textContent = '.hb-inline{position:fixed;z-index:10001;display:flex;align-items:center;gap:7px;box-sizing:border-box;padding:5px 9px;border:1px solid #a6905a;border-radius:7px;background:#27231ef5;color:#ffd578;font:15px sans-serif;pointer-events:auto}.hb-inline[hidden],.hb-inline [hidden]{display:none!important}.hb-inline input,.hb-inline button{font:inherit;box-sizing:border-box;border:1px solid #978663;border-radius:4px;padding:4px 6px;background:#40392e;color:#fff;min-width:0}.hb-inline button{cursor:pointer;white-space:nowrap}.hb-inline .hi-name{flex:1;width:100px}.hb-inline .hi-rank{width:78px}.hb-inline .hi-title{max-width:24%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.hb-inline .hi-status{font-size:12px;max-width:28%}.hb-inline input:read-only{opacity:.8}';
    document.head.appendChild(style); document.body.appendChild(bar);
    const field = bar.querySelector('.hi-name'), rank = bar.querySelector('.hi-rank');
    const edit = bar.querySelector('.hi-edit'), save = bar.querySelector('.hi-save'), cancel = bar.querySelector('.hi-cancel'), status = bar.querySelector('.hi-status');
    function send(type, data) { window.parent.postMessage({type, data:{session:catalog.session,...data},sourcePageId:page},'*'); }
    function selected() {
        const tab = document.querySelector('.bag-tabs .bag-tab.active');
        const owner = tab && tab.dataset.hbOwner;
        const entry = catalog.containers.find(e=>String(e.owner)===owner);
        return entry && tab.getBoundingClientRect().width ? {tab,entry} : null;
    }
    function stop(message) {
        clearTimeout(timer); saving=false;
        if(editing) send('HOME_EDIT',{open:false});
        editing=null; if(shield)shield.remove(); shield=null;
        status.textContent=message||''; schedule();
    }
    function render() {
        scheduled=false;
        renderRightTitle();
        const current=selected();
        if(editing && (!current || String(current.entry.owner)!==editing.owner || catalog.session!==editing.session)) stop('');
        const manager=document.querySelector('.hb-overlay:not(.hi-shield)');
        bar.hidden=!current || !!manager;
        if(!current)return;
        const r=current.tab.closest('.header').getBoundingClientRect();
        const width=Math.min(Math.max(r.width,540),window.innerWidth-16);
        bar.style.width=width+'px';
        bar.style.left=Math.max(8,Math.min(r.left,window.innerWidth-width-8))+'px';
        bar.style.top=Math.max(4,r.top-bar.offsetHeight-6)+'px';
        bar.querySelector('.hi-title').textContent='当前：'+current.entry.original;
        bar.querySelector('.hi-title').title=current.entry.original;
        if(!editing){field.value=current.entry.name||'';rank.value=current.entry.rank==null?'':current.entry.rank;}
        field.readOnly=rank.readOnly=!editing||saving;
        edit.hidden=!!editing;save.hidden=cancel.hidden=!editing;
        edit.disabled=!catalog.editable;save.disabled=saving;cancel.disabled=false;
    }
    function schedule(){if(!scheduled){scheduled=true;requestAnimationFrame(render);}}
    function renderRightTitle(){
        // Read Vue's actual bag state, including information received before this script loaded.
        const bag=window.__hbRightBag;
        if(bag){rightOwner=String(bag.ownerId||'');rightOriginal=bag.backpackName||'';}
        const entry=catalog.containers.find(e=>String(e.owner)===rightOwner);
        const label=entry ? (entry.rank==null?'':'['+entry.rank+'] ')+(entry.name||entry.original) : rightOriginal;
        // Keep the underlying reactive title in sync so native re-renders use the same label.
        if(bag&&entry&&label&&bag.backpackName!==label)bag.backpackName=label;
        const title=document.querySelector('.bag-panel.is-storage .header .panel-title');
        if(!title)return;
        if(label && title.textContent!==label)title.textContent=label;
        title.title=entry ? entry.original : rightOriginal;
    }
    edit.onclick=()=>{
        const current=selected();if(!current||!catalog.editable||editing)return;
        editing={owner:String(current.entry.owner),session:catalog.session};
        shield=document.createElement('div');shield.className='hb-overlay hi-shield unity-interactive';
        shield.style.cssText='background:transparent;display:block;z-index:10000';
        document.body.appendChild(shield);
        status.textContent='只修改当前容器';send('HOME_EDIT',{open:true});render();field.focus();field.select();
    };
    cancel.onclick=()=>stop('已取消');
    save.onclick=()=>{
        if(!editing||saving)return;
        const number=rank.value===''?null:Number(rank.value);
        if(rank.validity.badInput || (number!==null&&(!Number.isInteger(number)||Math.abs(number)>9999))){status.textContent='序号须为 -9999～9999 的整数';return;}
        const name=field.value.trim();
        if(name.length>40 || /[\r\n\0]/.test(name)){status.textContent='名称最多40字，不能含换行';return;}
        const current=selected();
        if(!current||String(current.entry.owner)!==editing.owner){stop('');return;}
        editing.expected={name,rank:number};saving=true;status.textContent='正在保存…';render();
        send('HOME_SAVE',{sort:catalog.sort,entries:[{owner:editing.owner,name,rank:number}]});
        timer=setTimeout(()=>{if(editing&&saving){saving=false;status.textContent='未收到保存确认，请重试';schedule();}},5000);
    };
    window.addEventListener('message',ev=>{
        let m=ev.data,d;try{if(typeof m==='string')m=JSON.parse(m);if(!m)return;d=m.data||{};if(typeof d==='string')d=JSON.parse(d);}catch{return;}
        if(m.type==='HOME_CATALOG'&&d.page===page){
            if(catalog.session&&catalog.session!==d.session){rightOwner='';rightOriginal='';}
            if(editing&&d.session!==editing.session)stop('');
            catalog=d;
            const e=editing&&catalog.containers.find(x=>String(x.owner)===editing.owner);
            if(saving&&e&&e.name===editing.expected.name&&e.rank===editing.expected.rank)stop('已保存');
        } else if(m.type==='WebUI_Backpack_InfoBMsg'){
            if(d.ownerId!==undefined){
                if(rightOwner!==String(d.ownerId))rightOriginal='';
                rightOwner=String(d.ownerId);
            }
            if(d.backpackName!==undefined)rightOriginal=d.backpackName;
        } else if(m.type==='HOME_ERROR'&&(d.page===page||d.page==='Backpack')&&editing){clearTimeout(timer);saving=false;status.textContent='保存失败：'+d.message;}
        schedule();
    });
    field.addEventListener('compositionstart',()=>{composing=true;});
    field.addEventListener('compositionend',()=>{composing=false;compositionEnded=performance.now();});
    document.addEventListener('keydown',e=>{
        if(!editing)return;
        if(composing||e.isComposing||e.keyCode===229)return;
        e.stopImmediatePropagation();
        if(e.key==='Escape'){e.preventDefault();stop('已取消');}
        else if(e.key==='Enter'&&performance.now()-compositionEnded>250){e.preventDefault();save.click();}
    },true);
    // Block game outside-click handlers, but let input/button target handlers run.
    for(const type of ['mousedown','mouseup','click','pointerdown','pointerup']){
        bar.addEventListener(type,e=>e.stopPropagation());
        document.addEventListener(type,e=>{if(editing&&!bar.contains(e.target)){e.preventDefault();e.stopImmediatePropagation();}},true);
    }
    new MutationObserver(records=>{if(records.some(r=>!bar.contains(r.target)))schedule();}).observe(document.body,{subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:['class','data-hb-owner']});
    window.addEventListener('resize',schedule);window.addEventListener('pagehide',()=>stop(''));
    send('HOME_REQUEST',{});schedule();
})();

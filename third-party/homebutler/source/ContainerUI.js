(function () {
    'use strict';
    const page = window.HOME_PAGE;
    if (window.__homeButler120) return;
    window.__homeButler120 = true;
    let catalog = { containers: [], sort: 'native', session: '' }, nativeTabs = [], cabinetTabs = [], backpackTabs = [];
    let currentOwner = '', fuelBar, dialog, pending = false;
    const styles = document.createElement('style');
    styles.textContent = `
      .bag-tabs-scroll,.left-tabs-scroll{overflow-x:auto!important;overflow-y:hidden!important;scrollbar-width:auto!important;padding-bottom:7px!important;min-width:0!important;scroll-behavior:auto!important;}
      .bag-tabs-scroll::-webkit-scrollbar,.left-tabs-scroll::-webkit-scrollbar,.hb-tabs::-webkit-scrollbar{display:block!important;height:15px!important;}
      .bag-tabs-scroll::-webkit-scrollbar-track,.left-tabs-scroll::-webkit-scrollbar-track,.hb-tabs::-webkit-scrollbar-track{background:#ddd!important;border-radius:5px;}
      .bag-tabs-scroll::-webkit-scrollbar-thumb,.left-tabs-scroll::-webkit-scrollbar-thumb,.hb-tabs::-webkit-scrollbar-thumb{background:#80775f!important;border:2px solid #ddd;border-radius:7px;min-width:32px;}
      .hb-tabs{display:flex;gap:6px;width:0;min-width:100%;max-width:100%;contain:inline-size;flex-shrink:0;overflow-x:scroll;overflow-y:hidden;padding:4px 0 9px;box-sizing:border-box;}
      .hb-tab{flex:0 0 auto;background:#39342c;color:#eee;border:1px solid #8b805e;padding:8px 12px;border-radius:5px;font:inherit;white-space:nowrap;cursor:pointer;}
      .hb-tab.active{background:#80622b;border-color:#e7b34c;color:#fff;}
      .hb-tools{position:fixed;bottom:10px;right:20px;z-index:8000;font:16px sans-serif;}
      .hb-tools button{background:#302a20;color:#ffd578;border:1px solid #a6905a;border-radius:6px;padding:7px 12px;cursor:pointer;}
      .hb-overlay{position:fixed;inset:0;z-index:10000;background:#0009;display:flex;align-items:center;justify-content:center;font:18px sans-serif;color:#eee;}
      .hb-dialog{width:min(900px,90vw);max-height:85vh;background:#27231e;border:2px solid #bba574;border-radius:12px;padding:20px;box-sizing:border-box;display:flex;flex-direction:column;gap:12px;}
      .hb-list{overflow:auto;min-height:0;display:flex;flex-direction:column;gap:8px;}
      .hb-row{display:grid;grid-template-columns:minmax(120px,1fr) minmax(140px,1.5fr) 95px;gap:10px;align-items:center;}
      .hb-dialog input,.hb-dialog select,.hb-dialog button{font:inherit;background:#40392e;color:white;border:1px solid #978663;border-radius:4px;padding:7px;min-width:0;}
      .hb-dialog button{cursor:pointer;}.hb-dialog h2{margin:0;font-size:24px;color:#ffd578}.hb-status{min-height:22px;color:#ffd578;font-size:15px;}
    `;
    document.head.appendChild(styles);
    function send(type, data) { window.parent.postMessage({ type, data: Object.assign({session:catalog.session},data||{}), sourcePageId:page }, '*'); }
    function owner(t) { return String(t.hbOwner || t.ownerId || t.owner || ''); }
    function entry(id) { return catalog.containers.find(t => t.owner === String(id)); }
    function name(t) { const e=entry(owner(t)); return e && e.name ? e.name : t.name || (e&&e.original) || '容器'; }
    const compare = new Intl.Collator('zh-CN',{numeric:true,sensitivity:'base'}).compare;
    function ordered(items) {
        if(catalog.sort==='native') return items.slice();
        return items.map((t,i)=>({t,i})).sort((a,b)=>{
            if(catalog.sort==='rank') {
                const ea=entry(owner(a.t)),eb=entry(owner(b.t));
                const ra=ea&&ea.rank!=null?ea.rank:10000, rb=eb&&eb.rank!=null?eb.rank:10000;
                if(ra!==rb)return ra-rb;
            }
            return compare(name(a.t),name(b.t))||a.i-b.i;
        }).map(x=>x.t);
    }
    function rename(el,label) {
        if(!label)return;
        const node=[...el.childNodes].find(n=>n.nodeType===3);
        if(node && node.nodeValue!==label)node.nodeValue=label;
        el.title=label;
    }
    // CSS order preserves native DOM indices, event handlers and drag target IDs.
    function decorate(elements,tabs,pinned) {
        const ranks=new Map(ordered(tabs.slice(pinned)).map((t,i)=>[owner(t),i+1]));
        elements.forEach((el,i)=>{
            const t=tabs[i];if(!t)return;
            const id=owner(t),e=entry(id); el.dataset.hbOwner=id;
            if(e&&e.name)rename(el,e.name); else if(t.name)rename(el,t.name);
            el.style.order=i<pinned?'0':String(ranks.get(id)||i+1);
        });
    }
    function renderFuel() {
        if(!catalog.fuel)return;
        if(!fuelBar) {
            const grid=document.getElementById('backpackGrid'); if(!grid)return;
            fuelBar=document.createElement('div');fuelBar.className='hb-tabs unity-interactive';
            const anchor=grid.parentElement.querySelector('.panel-title');
            (anchor||grid).insertAdjacentElement('afterend',fuelBar);
            grid.parentElement.style.minWidth='0';
        }
        const state=catalog.fuel, tabs=state.sources||[];
        const sorted=tabs.length?[tabs[0],...ordered(tabs.slice(1))]:[];
        const key=JSON.stringify([sorted.map(t=>[owner(t),name(t)]),state.owner]);
        if(fuelBar.dataset.key===key)return;
        fuelBar.dataset.key=key; const left=fuelBar.scrollLeft;fuelBar.replaceChildren();
        sorted.forEach(t=>{
            const b=document.createElement('button');b.type='button';b.className='hb-tab'+(owner(t)===state.owner?' active':'');b.textContent=name(t);
            b.onclick=()=>send('HOME_FUEL_SELECT',{owner:owner(t)});fuelBar.appendChild(b);
        });
        fuelBar.scrollLeft=left;
        const active=fuelBar.querySelector('.active');
        if(active){ if(active.offsetLeft<fuelBar.scrollLeft)fuelBar.scrollLeft=active.offsetLeft;else if(active.offsetLeft+active.offsetWidth>fuelBar.scrollLeft+fuelBar.clientWidth)fuelBar.scrollLeft=active.offsetLeft+active.offsetWidth-fuelBar.clientWidth; }
        // The native viewport fitter listens to resize and reserves the added strip.
        window.dispatchEvent(new Event('resize'));
    }
    function render() {
        pending=false;
        if(page==='BackpackUI')decorate([...document.querySelectorAll('.bag-tabs .bag-tab')],backpackTabs,1);
        else if(page==='ToolTable')decorate([...document.querySelectorAll('.cabinet-tab')],cabinetTabs,0);
        else if(page!=='Heater'&&page!=='FuelGenerator')decorate([...document.querySelectorAll('.bag-tabs .bag-tab')],nativeTabs,1);
        renderFuel();
    }
    function schedule(){if(!pending){pending=true;requestAnimationFrame(render);}}
    new MutationObserver(schedule).observe(document.body,{childList:true,subtree:true});
    window.addEventListener('message',function(ev){
        let m=ev.data;if(typeof m==='string'){try{m=JSON.parse(m);}catch{return;}}if(!m||!m.type)return;
        let d=m.data||{};if(typeof d==='string'){try{d=JSON.parse(d);}catch{return;}}
        if(m.type==='HOME_CATALOG') {
            if(d.page!==page)return;const oldSession=catalog.session;catalog=d;
            if(dialog&&oldSession&&oldSession!==d.session)closeManager();
            if(dialog){dialog.querySelector('.hb-status').textContent='已保存；关闭后可看到更新的名称和顺序。';dialog.querySelector('.hb-save').disabled=false;}
        } else if(m.type==='HOME_ERROR'&&d.page===page&&dialog){dialog.querySelector('.hb-status').textContent='保存失败：'+d.message;dialog.querySelector('.hb-save').disabled=false;}
        else {
            if(Array.isArray(d.bagTabs))nativeTabs=d.bagTabs;
            if(Array.isArray(d.cabinetTabs))cabinetTabs=d.cabinetTabs;
            if(d.aTabsJson!==undefined){try{backpackTabs=JSON.parse(d.aTabsJson||'[]');}catch{backpackTabs=[];}}
            if(d.bagOwnerId!==undefined)currentOwner=String(d.bagOwnerId);
        }
        schedule();
    });
    const tools=document.createElement('div');tools.className='hb-tools unity-interactive';
    const manage=document.createElement('button');manage.textContent='容器命名 / 排序';manage.onclick=openManager;tools.appendChild(manage);document.body.appendChild(tools);
    function closeManager(){if(!dialog)return;dialog.remove();dialog=null;send('HOME_EDIT',{open:false});}
    function openManager(){
        if(dialog)return;
        dialog=document.createElement('div');dialog.className='hb-overlay unity-interactive';
        dialog.innerHTML='<div class="hb-dialog"><h2>家中容器</h2><label>显示顺序 <select class="hb-sort"><option value="native">原版顺序</option><option value="name">名称（A–Z / 0–9，自然排序）</option><option value="rank">数字序号优先，再按名称</option></select></label><div class="hb-row"><span>原名称</span><span>自定义名称（留空恢复）</span><span>排序数字</span></div><div class="hb-list"></div><div class="hb-status"></div><div><button class="hb-save">保存</button> <button class="hb-close">关闭</button></div></div>';
        document.body.appendChild(dialog);dialog.querySelector('.hb-sort').value=catalog.sort;
        const list=dialog.querySelector('.hb-list');
        ordered(catalog.containers.map(t=>({...t,name:t.name||t.original}))).forEach(t=>{
            const row=document.createElement('div');row.className='hb-row';row.dataset.owner=t.owner;
            const label=document.createElement('span');label.textContent=t.original;label.title=t.owner;
            const input=document.createElement('input');input.type='text';input.maxLength=40;input.value=entry(t.owner).name||'';input.placeholder=t.original;input.className='hb-name';
            const rank=document.createElement('input');rank.type='number';rank.min='-9999';rank.max='9999';rank.step='1';rank.value=t.rank==null?'':t.rank;rank.className='hb-rank';
            row.append(label,input,rank);list.appendChild(row);
        });
        dialog.querySelector('.hb-close').onclick=closeManager;
        dialog.querySelector('.hb-save').onclick=function(){
            const entries=[...list.children].map(row=>({owner:row.dataset.owner,name:row.querySelector('.hb-name').value,rank:row.querySelector('.hb-rank').value===''?null:Number(row.querySelector('.hb-rank').value)}));
            if(entries.some(e=>e.rank!==null&&(!Number.isInteger(e.rank)||Math.abs(e.rank)>9999))){dialog.querySelector('.hb-status').textContent='排序数字请填写 -9999 至 9999 的整数。';return;}
            this.disabled=true;dialog.querySelector('.hb-status').textContent='正在保存…';send('HOME_SAVE',{sort:dialog.querySelector('.hb-sort').value,entries});
        };
        dialog.querySelector('.hb-status').textContent=catalog.editable?'按存档分别保存；背包等固定标签仍放在最前面。':'尚未获得存档信息，请关闭后重新打开面板。';
        dialog.querySelector('.hb-save').disabled=!catalog.editable;
        send('HOME_EDIT',{open:true});
    }
    document.addEventListener('keydown',e=>{if(dialog){e.stopImmediatePropagation();if(e.key==='Escape'){e.preventDefault();closeManager();}}},true);
    // Do not let native outside-click handlers close the game panel under the editor.
    document.addEventListener('mousedown',e=>{if(dialog&&dialog.contains(e.target))e.stopPropagation();},true);
    let tries=0;const request=setInterval(()=>{send('HOME_REQUEST');if(++tries>=8||catalog.session)clearInterval(request);},500);
    send('HOME_REQUEST');
})();

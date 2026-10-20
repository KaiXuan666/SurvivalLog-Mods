const {test} = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const script = fs.readFileSync(require('node:path').join(__dirname,'Navigation.js'),'utf8');
function setup(page='BackpackUI', delayed=false) {
  let frames=[], events={}, sent=[], clicks=[], now=0, pending=null, overlay=false;
  function el(id, order=0, display='block', classes=[]) {
    const e={id,children:[],dataset:{hbOwner:id},disabled:false,style:{order,display,visibility:'visible'},
      classList:{contains:c=>classes.includes(c)},matches:s=>s==='.bag-tab'||s==='.left-tab'?classes.includes('tab'):false,
      closest:()=>null,click(){clicks.push(id);if(delayed)pending=e;else activate(e);}};
    e.classes=classes; return e;
  }
  const tab=(id,order=0)=>el(id,order,'block',['tab']);
  const bag=tab('bag'), two=tab('box2',1), ten=tab('box10',2), one=tab('box1',0);
  const pinned=el('pinned',0,'flex'), scrolling=el('scroll',0,'flex'),root=el('root',0,'flex');
  pinned.children=[bag];scrolling.children=[ten,two,one];root.children=[pinned,scrolling];
  const all=[bag,ten,two,one];
  function activate(e){all.forEach(t=>{const i=t.classes.indexOf('active');if(i>=0)t.classes.splice(i,1);});e.classes.push('active');}
  activate(bag);
  const context={window:{HOME_PAGE:page,parent:{postMessage:m=>sent.push(m)},addEventListener:(n,fn)=>events[n]=fn},
    document:{body:{},querySelector:s=>s==='.hb-overlay'?(overlay?{}:null):root},getComputedStyle:e=>e.style,
    MutationObserver:class{constructor(fn){this.fn=fn;}observe(){}},requestAnimationFrame:fn=>frames.push(fn),performance:{now:()=>now}};
  vm.runInNewContext(script,context);
  function message(type,data,string=false){const m={type,data:{page,...data}};events.message({data:string?JSON.stringify(m):m});}
  function tick(){now+=16;const f=frames;frames=[];f.forEach(fn=>fn());}
  function catalog(sort){message('HOME_CATALOG',{sort});tick();}
  function step(direction){message('HB_NAV_STEP',{direction});tick();}
  return {bag,one,two,ten,scrolling,all,sent,clicks,events,catalog,step,tick,message,activate,
    ack(){if(pending){activate(pending);pending=null;}tick();},overlay(v){overlay=v;}, advance(ms){now+=ms;tick();}};
}
test('display order, pinned bag, forward and backward wrap',()=>{
 const s=setup();s.catalog('name');
 for(let i=0;i<5;i++)s.step(1);
 assert.deepEqual(s.clicks,['box1','box2','box10','bag','box1']);
 s.step(-1);s.step(-1);assert.deepEqual(s.clicks.slice(-2),['bag','box10']);
});
test('numeric priority uses actual CSS order and stable ties',()=>{
 const s=setup();s.ten.style.order=-2;s.one.style.order=2;s.two.style.order=2;s.catalog('rank');
 s.step(1);s.step(1);s.step(1);assert.deepEqual(s.clicks,['box10','box2','box1']);
});
test('native order does not intercept; manager disables navigation',()=>{
 const s=setup();s.catalog('native');s.step(1);assert.equal(s.clicks.length,0);
 s.catalog('name');s.overlay(true);s.step(1);assert.equal(s.clicks.length,0);
 assert.equal(s.sent.at(-1).data.ready,false);
});
test('rapid repeated keys wait for game acknowledgement',()=>{
 const s=setup('Cooking',true);s.catalog('name');s.step(1);s.step(1);s.step(1);
 assert.deepEqual(s.clicks,['box1']);s.ack();assert.deepEqual(s.clicks,['box1','box2']);
 s.ack();assert.deepEqual(s.clicks,['box1','box2','box10']);
});
test('rejected switch times out and clears pending keys',()=>{
 const s=setup('Cooking',true);s.catalog('rank');s.step(1);s.step(1);s.advance(1600);s.ack();
 assert.deepEqual(s.clicks,['box1']);
});
test('hidden and disabled tool tabs skipped',()=>{
 const s=setup('ToolTable');s.one.style.display='none';s.two.disabled=true;s.catalog('name');
 s.step(1);assert.deepEqual(s.clicks,['box10']);
});
test('string messages accepted and wrong-page events ignored',()=>{
 const s=setup();s.message('HOME_CATALOG',{sort:'name'},true);s.tick();
 s.message('HB_NAV_STEP',{page:'Cooking',direction:1});s.tick();assert.equal(s.clicks.length,0);
 s.message('HB_NAV_STEP',{direction:1},true);s.tick();assert.deepEqual(s.clicks,['box1']);
});
test('sort change while panel open updates next target',()=>{
 const s=setup();s.catalog('name');s.step(1);s.tick();s.ten.style.order=-1;s.catalog('rank');s.step(-1);
 assert.deepEqual(s.clicks,['box1','box10']);
});

'use strict';
const test=require('node:test');const assert=require('node:assert/strict');
const fs=require('node:fs');const path=require('node:path');const os=require('node:os');
const {CatalogStore}=require('./catalog.cjs');
function item(index,available=true){return {track:{_uri:'spotify:track:'+String(index).padStart(22,'0'),data:available?{name:'Song '+index,artists:{items:[{profile:{name:'Artist'}}]}}:{__typename:'NotFound'}}};}
function page(total,offset,items,limit=100){return {me:{library:{tracks:{items,totalCount:total,pagingInfo:{offset,limit}}}}};}
function setup(t,respond){
  const prefix=path.join(os.tmpdir(),'yspotify-catalog-'),directory=fs.mkdtempSync(prefix),calls=[];
  t.mock.method(globalThis,'fetch',async(url,options)=>{const body=JSON.parse(options.body);assert.equal(body.operationName,'fetchLibraryTracks');calls.push(body.variables.offset);const data=await respond(body.variables.offset);return new Response(JSON.stringify({data}),{status:200});});
  t.after(()=>{const absolute=path.resolve(directory);if(path.dirname(absolute)!==path.resolve(os.tmpdir())||!absolute.startsWith(path.resolve(prefix)))throw new Error('Unexpected test path');fs.rmSync(absolute,{recursive:true,force:true});});
  return {store:new CatalogStore({bearer:'Bearer fixture',client:'fixture'},null,directory),calls};
}
test('unavailable first-page tracks do not hide later favorite pages',async t=>{
  const {store,calls}=setup(t,offset=>page(256,offset,Array.from({length:Math.min(100,256-offset)},(_,i)=>item(offset+i,offset+i!==30))));
  const result=await store.query('liked');assert.equal(result.rows.length,255);assert.equal(result.rows.at(-1).name,'Song 255');assert.equal(result.sourceCount,256);assert.deepEqual(calls,[0,100,200]);
});
test('an entirely unavailable middle page still advances to the final page',async t=>{
  const {store,calls}=setup(t,offset=>page(256,offset,Array.from({length:Math.min(100,256-offset)},(_,i)=>item(offset+i,offset!==100))));
  const result=await store.query('liked');assert.equal(result.rows.length,156);assert.equal(result.rows.at(-1).name,'Song 255');assert.deepEqual(calls,[0,100,200]);
});
test('server page-size limits do not truncate the library',async t=>{
  const {store,calls}=setup(t,offset=>page(126,offset,Array.from({length:Math.min(50,126-offset)},(_,i)=>item(offset+i)),50));
  assert.equal((await store.query('liked')).rows.length,126);assert.deepEqual(calls,[0,50,100]);
});
test('a network failure never overwrites the complete cached favorites',async t=>{
  let fail=false;const {store}=setup(t,offset=>{if(fail&&offset===100)throw new Error('offline');return page(256,offset,Array.from({length:Math.min(100,256-offset)},(_,i)=>item(offset+i)));});
  await store.query('liked');const before=fs.readFileSync(store.file,'utf8');fail=true;
  await assert.rejects(store.query('liked',null,{force:true}),/offline/);assert.equal((await store.query('liked',null,{cacheOnly:true})).rows.length,256);assert.equal(fs.readFileSync(store.file,'utf8'),before);
});
test('an incomplete response is retried without replacing the full cached list',async t=>{
  let partial=false;const {store,calls}=setup(t,offset=>page(256,offset,Array.from({length:partial&&offset===100?20:Math.min(100,256-offset)},(_,i)=>item(offset+i))));
  await store.query('liked');partial=true;
  await assert.rejects(store.query('liked',null,{force:true}),/CatalogIncomplete/);assert.equal(store.cached('liked').length,256);assert.deepEqual(calls.slice(3),[0,100,0,100]);
});
test('a changed total during loading restarts the snapshot',async t=>{
  let changed=true;const {store,calls}=setup(t,offset=>{if(offset===100&&changed){changed=false;return page(255,offset,Array.from({length:100},(_,i)=>item(offset+i)));}return page(256,offset,Array.from({length:Math.min(100,256-offset)},(_,i)=>item(offset+i)));});
  assert.equal((await store.query('liked')).rows.length,256);assert.deepEqual(calls,[0,100,0,100,200]);
});
test('complete server-side deletions may legitimately shorten favorites',async t=>{
  let total=256;const {store}=setup(t,offset=>page(total,offset,Array.from({length:Math.min(100,total-offset)},(_,i)=>item(offset+i))));
  await store.query('liked');total=12;assert.equal((await store.query('liked',null,{force:true})).rows.length,12);total=0;assert.equal((await store.query('liked',null,{force:true})).rows.length,0);
});
test('legacy caches are revalidated even when their timestamp is fresh',async t=>{
  const {store,calls}=setup(t,offset=>page(256,offset,Array.from({length:Math.min(100,256-offset)},(_,i)=>item(offset+i))));
  store.entries['liked:']={at:Date.now(),rows:[{uri:'legacy',name:'Old partial cache'}]};assert.equal((await store.query('liked')).rows.length,256);assert.deepEqual(calls,[0,100,200]);
});

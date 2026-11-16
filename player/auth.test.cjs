'use strict';
const test=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs');const path=require('node:path');const os=require('node:os');const vm=require('node:vm');
test('renewal retains rotated cookies for the next protected session save',async()=>{
  const prefix=path.join(os.tmpdir(),'yspotify-auth-test-'),directory=fs.mkdtempSync(prefix);
  try{
    const output={exports:{}};let current=[{name:'sp_dc',value:'old-fixture',domain:'.spotify.com',path:'/'}];
    const sandbox={module:output,require:name=>name==='electron'?{BrowserWindow:class{},app:{getPath:()=>directory}}:require(name),setTimeout,clearTimeout,AbortSignal,Buffer,URL,fetch:async()=>({status:403,ok:false,headers:{get:()=>null}})};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname,'auth.cjs'),'utf8'),sandbox);
    const session=new output.exports.SpotifySession({cookies:{get:async()=>current},getUserAgent:()=>''});
    session.auth={bearer:'Bearer fixture',cookies:current};
    session.refreshInBrowserSession=async()=>{current=[{name:'sp_dc',value:'rotated-fixture',domain:'.spotify.com',path:'/',httpOnly:true}];return {bearer:'Bearer renewed-fixture',validUntil:Date.now()+300000};};
    await session.refresh();
    assert.equal(session.auth.cookies[0].value,'rotated-fixture');assert.equal(session.auth.bearer,'Bearer renewed-fixture');assert.equal(session.changed,true);
  }finally{
    if(!path.resolve(directory).startsWith(path.resolve(prefix)))throw new Error('Unexpected test directory');
    fs.rmSync(directory,{recursive:true,force:true});
  }
});

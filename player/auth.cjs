'use strict';
const {BrowserWindow}=require('electron');
const fs=require('node:fs');const path=require('node:path');const {app}=require('electron');

class SpotifySession {
  constructor(browserSession){this.browserSession=browserSession;this.auth=null;this.changed=false;this.refreshing=null;this.lastRefresh=null;}
  async restore(value){
    this.auth=value;
    for(const cookie of value?.cookies||[]) {
      if(!['sp_dc','sp_key'].includes(cookie.name)||!/(^|\.)spotify\.com$/.test(cookie.domain||''))continue;
      const options={url:'https://open.spotify.com',name:cookie.name,value:cookie.value,domain:cookie.domain,path:cookie.path||'/',secure:true,httpOnly:!!cookie.httpOnly};
      if(Number(cookie.expirationDate)>Date.now()/1000)options.expirationDate=Number(cookie.expirationDate);
      await this.browserSession.cookies.set(options);
    }
    if(value && Number(value.validUntil)<Date.now()+60000)await this.refresh();
  }
  async ensureFresh(){if(this.auth && Number(this.auth.validUntil)<Date.now()+90000)await this.refresh();}
  async refresh(){
    if(this.refreshing)return this.refreshing;
    this.refreshing=this.fetchFresh();
    try{return await this.refreshing;}finally{this.refreshing=null;try{fs.writeFileSync(path.join(app.getPath('userData'),'auth-diagnostics.json'),JSON.stringify(this.diagnostics||{}));}catch{}}
  }
  async fetchFresh(){
    if(!this.auth)throw new Error('SessionExpired');
    this.diagnostics={at:new Date().toISOString(),responses:[]};
    const note=phase=>{this.diagnostics.phase=phase;try{fs.writeFileSync(path.join(app.getPath('userData'),'auth-diagnostics.json'),JSON.stringify(this.diagnostics));}catch{}};
    note('cookie-session');
    const cookieValues=await Promise.race([this.browserSession.cookies.get({url:'https://open.spotify.com'}),new Promise((_,reject)=>setTimeout(()=>reject(new Error('CookieTimeout')),3000))]);
    const cookieHeader=cookieValues.filter(cookie=>['sp_dc','sp_key','sp_t'].includes(cookie.name)).map(cookie=>cookie.name+'='+cookie.value).join('; ');
    const headers={cookie:cookieHeader,'user-agent':this.browserSession.getUserAgent()};
    let responseDate=null;
    const read=value=>{
      if(!value||typeof value!=='object')return null;
      const token=value.accessToken||value.access_token;
      if(typeof token!=='string'||value.isAnonymous===true)return null;
      const expiry=Number(value.accessTokenExpirationTimestampMs||value.expiresAt||value.expirationTimestampMs||(value.expires_in?Date.now()+value.expires_in*1000:0));
      const serverNow=Date.parse(responseDate||'');
      const ttl=Number.isFinite(serverNow)?expiry-serverNow:0;
      return {bearer:'Bearer '+token,validUntil:ttl>0&&ttl<86400000?Date.now()+ttl:(expiry>Date.now()?expiry:Date.now()+3*60000)};
    };
    let fresh=null;
    note('request-bootstrap');
    let page;try{page=await fetch('https://open.spotify.com/',{headers,cache:'no-store',signal:AbortSignal.timeout(5000)});}catch(error){this.diagnostics.bootstrapError=error.name;}
    this.diagnostics.rootStatus=page?.status;
    if(page?.ok){
      responseDate=page.headers.get('date');
      note('read-bootstrap');const html=await page.text();
      this.diagnostics.scriptIds=[...html.matchAll(/<script\b[^>]*\bid=["']([^"']+)["']/g)].map(value=>value[1]).filter(value=>/^[a-zA-Z0-9_-]{1,60}$/.test(value)).slice(0,20);
      let anonymous=false;
      const session=html.match(/<script\b[^>]*\bid=["']session["'][^>]*>([\s\S]*?)<\/script>/i);
      if(session)try{const data=JSON.parse(session[1]);this.diagnostics.rootSessionKeys=Object.keys(data);anonymous=data.isAnonymous===true;this.diagnostics.rootAnonymous=anonymous;fresh=read(data);}catch{}
      if(!fresh&&!anonymous){
        const match=html.match(/"accessToken"\s*:\s*("(?:[^"\\]|\\.)*")[\s\S]{0,300}?"accessTokenExpirationTimestampMs"\s*:\s*(\d+)/);
        if(match && !/"isAnonymous"\s*:\s*true/.test(match[0]))try{fresh=read({accessToken:JSON.parse(match[1]),accessTokenExpirationTimestampMs:Number(match[2])});}catch{}
      }
    }
    if(!fresh){
      note('request-token');try{const reply=await fetch('https://open.spotify.com/get_access_token?reason=transport&productType=web_player',{headers,cache:'no-store',signal:AbortSignal.timeout(5000)});
      this.diagnostics.oldEndpointStatus=reply.status;responseDate=reply.headers.get('date');
      if(reply.ok)try{fresh=read(await reply.json());}catch{}}catch(error){this.diagnostics.tokenError=error.name;}
    }
    if(!fresh){note('browser-token');fresh=await this.refreshInBrowserSession((value,date)=>{if(date)responseDate=date;return read(value);},note);}
    if(!fresh){this.lastRefresh='unavailable';throw new Error('SessionExpired');}
    const retained=await this.browserSession.cookies.get({url:'https://open.spotify.com'});
    const cookies=retained.filter(cookie=>['sp_dc','sp_key'].includes(cookie.name)).map(cookie=>({name:cookie.name,value:cookie.value,domain:cookie.domain,path:cookie.path,httpOnly:cookie.httpOnly,expirationDate:cookie.expirationDate}));
    Object.assign(this.auth,fresh,{cookies});this.changed=true;this.lastRefresh='restored';note('restored');return this.auth;
  }
  async refreshInBrowserSession(read,note){
    note('browser-cookies');
    const ownCookies=await Promise.race([this.browserSession.cookies.get({url:'https://open.spotify.com',name:'sp_dc'}),new Promise(resolve=>setTimeout(()=>resolve([]),3000))]);
    this.diagnostics.ownCookieCount=ownCookies.length;
    note('browser-window');
    if(!ownCookies.length)return null;
    const window=new BrowserWindow({show:false,width:480,height:320,webPreferences:{session:this.browserSession,nodeIntegration:false,contextIsolation:true,sandbox:true,images:false,webgl:false,backgroundThrottling:false,autoplayPolicy:'user-gesture-required'}});
    const debuggerClient=window.webContents.debugger;
    window.webContents.setUserAgent(this.browserSession.getUserAgent().replace(/\sElectron\/\S+/g,'').replace(/\sYSpotify\/\S+/g,''));
    return new Promise(resolve=>{
      let completed=false;const requests=new Map();
      const finish=value=>{if(completed)return;completed=true;clearTimeout(timeout);try{debuggerClient.detach();}catch{}if(!window.isDestroyed())window.destroy();resolve(value);};
      const timeout=setTimeout(()=>{note('browser-timeout');finish(null);},16000);
      window.webContents.on('dom-ready',()=>{note('browser-dom');window.webContents.executeJavaScript("({host:location.hostname,path:location.pathname,ready:document.readyState,scripts:document.scripts.length,loggedIn:!!document.querySelector('[data-testid=\"user-widget-link\"],[data-testid=\"user-widget-avatar\"]'),unsupported:/browser.{0,20}(unsupported|not supported)/i.test(document.body?.textContent||'')})").then(value=>{this.diagnostics.document=value;note('browser-document');}).catch(()=>{});});
      try{
        debuggerClient.attach('1.3');
        debuggerClient.on('message',async(_,method,params)=>{
          if(completed)return;
          if(method==='Network.responseReceived'){
            try{const url=new URL(params.response.url);if(/(^|\.)spotify\.com$/.test(url.hostname)&&/token|access_token/.test(url.pathname))requests.set(params.requestId,{headers:params.response.headers,status:params.response.status,path:url.pathname});}catch{}
          }else if(method==='Network.loadingFinished'&&requests.has(params.requestId)){
            const response=requests.get(params.requestId);requests.delete(params.requestId);
            try{
              const body=await debuggerClient.sendCommand('Network.getResponseBody',{requestId:params.requestId});
              const raw=body.base64Encoded?Buffer.from(body.body,'base64').toString('utf8'):body.body;
              const value=JSON.parse(raw);if(this.diagnostics.responses.length<10)this.diagnostics.responses.push({path:response.path,status:response.status,keys:Object.keys(value),dataKeys:value.data&&typeof value.data==='object'?Object.keys(value.data):[]});
              if(value.granted_token?.token){this.auth.client=value.granted_token.token;this.changed=true;}
              const date=Object.entries(response.headers).find(([key])=>key.toLowerCase()==='date')?.[1];
              const token=read(value,date)||read(value.data,date);
              note('browser-response');if(token){finish(token);}
            }catch(error){this.diagnostics.bodyError=error.name;}
          }
        });
        note('browser-network');debuggerClient.sendCommand('Network.enable',{maxTotalBufferSize:1048576,maxResourceBufferSize:262144}).catch(()=>{});
        note('browser-navigate');window.loadURL('https://open.spotify.com/search').catch(error=>{if(completed)return;this.diagnostics.navigationError=error.code||error.name;note('browser-error');finish(null);});
      }catch{finish(null);}
      window.on('closed',()=>{if(!completed)finish(null);});
    });
  }
}
module.exports={SpotifySession};

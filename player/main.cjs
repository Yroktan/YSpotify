'use strict';
const { app, components, BrowserWindow } = require('electron');
const net = require('node:net');
const path = require('node:path');
const { SpotifyService } = require('./service.cjs');
const { CatalogStore } = require('./catalog.cjs');
const { SpotifySession } = require('./auth.cjs');
const { DiscordPresence } = require('./discord.cjs');
const config=require('./config.json');
const discord=new DiscordPresence(config.discordApplicationId,{enabled:config.discordEnabled});
const pipeName = process.argv.find(a => a.startsWith('--yspotify-pipe='))?.slice(16);
if (!pipeName || !/^YSpotifyNative-[a-f0-9]+$/.test(pipeName)) app.exit(2);
app.setName('YSpotify');
app.setPath('userData', path.join(process.env.LOCALAPPDATA, 'YSpotify', 'PlayerProfile'));
app.disableHardwareAcceleration();
app.enableSandbox();
app.commandLine.appendSwitch('autoplay-policy', 'no-user-gesture-required');
let window, service, auth = null, login = false, candidate = {}, authUpdated = false, initializing = false;
let session,library,chosenQueue=null;
function isMusic(){return service?.state.track?.uri?.startsWith('spotify:track:');}
function localPrevious(){const reasons=service?.active()?.state?.restrictions?.disallow_skipping_prev_reasons||[];return !!chosenQueue&&isMusic()&&reasons.length>0&&reasons.every(reason=>reason==='no_prev_track');}
function arrangeQueue(){if(!chosenQueue)return;const order=chosenQueue.tracks.map((_,index)=>index);if(service?.state.shuffle){for(let index=order.length-1;index>0;index--){const other=Math.floor(Math.random()*(index+1));[order[index],order[other]]=[order[other],order[index]];}const current=order.indexOf(chosenQueue.index);order.splice(current,1);order.unshift(chosenQueue.index);}chosenQueue.order=order;}
function followingIndex(direction){if(!chosenQueue)return -1;const order=chosenQueue.order||chosenQueue.tracks.map((_,index)=>index);let position=order.indexOf(chosenQueue.index)+direction;if(position<0||position>=order.length){if(service.state.repeatMode==='context')position=(position+order.length)%order.length;else return -1;}return order[position];}
function rememberQueue(request){
  if(request.action!=='play')return;
  if(!request.queue?.length){chosenQueue=null;return;}
  if(chosenQueue?.tracks===request.queue){chosenQueue.index=request.queue.findIndex(value=>value.uri===request.uri);chosenQueue.started=false;return;}
  chosenQueue={tracks:request.queue,index:request.queue.findIndex(value=>value.uri===request.uri),context:request.context,started:false};arrangeQueue();
}
let playbackVersion=0,pendingPlayback=null,playbackPump=null,activePlaybackKey='',lastPlaybackKey='',lastPlaybackAt=0;
function queuePlayback(request){
  if(request.automatic&&(pendingPlayback||playbackPump))return Promise.resolve({superseded:true});
  const key=request.action+':'+(request.uri||'');
  if(request.action==='play'&&(key===pendingPlayback?.key||(!pendingPlayback&&key===activePlaybackKey)||(key===lastPlaybackKey&&Date.now()-lastPlaybackAt<800)))return Promise.resolve({coalesced:true});
  lastPlaybackKey=key;lastPlaybackAt=Date.now();
  rememberQueue(request);
  const version=++playbackVersion;
  service.cancelPreparation();
  if(pendingPlayback)pendingPlayback.resolve({superseded:true});
  const promise=new Promise((resolve,reject)=>{pendingPlayback={request,key,version,resolve,reject};});
  if(!playbackPump)playbackPump=(async()=>{
    try{await new Promise(resolve=>setTimeout(resolve,20));while(pendingPlayback){
      const item=pendingPlayback;pendingPlayback=null;activePlaybackKey=item.key;
      try{const action=item.request.action==='play'?'play':item.request.action==='next'?'skip_next':'skip_prev';const played=await service.command(action,item.request.uri,()=>item.version===playbackVersion,item.request.pressedAt,{...item.request,expected:library?.track(item.request.uri)});if(chosenQueue)chosenQueue.started=service.state.track?.requestedUri===chosenQueue.tracks[chosenQueue.index]?.uri||service.state.track?.uri===chosenQueue.tracks[chosenQueue.index]?.uri||service.state.track?.linkedFromUri===chosenQueue.tracks[chosenQueue.index]?.uri;item.resolve({played:!!played,superseded:item.version!==playbackVersion});}
      catch(error){if(item.version!==playbackVersion)item.resolve({superseded:true});else item.reject(error);}
      activePlaybackKey='';
    }}finally{playbackPump=null;}
  })();
  return promise;
}
const idleUrl = 'https://open.spotify.com/robots.txt';
const welcomeUrl = 'https://accounts.spotify.com/login?continue=' + encodeURIComponent('https://open.spotify.com/search');

async function setSession(value) {
  if (service) await service.stop();
  auth = value;
  await session.restore(value);
  library=auth?.bearer?new CatalogStore(auth,session,app.getPath('userData')):null;
  service = auth?.bearer && auth?.client ? new SpotifyService(auth, window, session) : null;
  if(service)service.onAdvance=async(currentUri,nextUri)=>{
    if(!chosenQueue||!nextUri?.startsWith('spotify:track:')||chosenQueue.index<0)return false;
    let index=chosenQueue.index;
    if(currentUri?.startsWith('spotify:track:')&&chosenQueue.started&&service.state.repeatMode!=='track')index=followingIndex(1);
    if(index<0||index>=chosenQueue.tracks.length)return false;
    chosenQueue.index=index;const target=chosenQueue.tracks[index].uri;
    if(target===nextUri){chosenQueue.started=true;return false;}
    await queuePlayback({action:'play',uri:target,context:chosenQueue.context,queue:chosenQueue.tracks,automatic:true,pressedAt:Date.now()});return true;
  };
  if (service) await service.initialize();
}
async function finishLogin() {
  const signedIn = await window.webContents.executeJavaScript("location.hostname==='open.spotify.com' && !!document.querySelector('[data-testid=\"user-widget-link\"],[data-testid=\"user-widget-avatar\"]')");
  if (!signedIn || !candidate.bearer || !candidate.client) return false;
  const cookies=(await window.webContents.session.cookies.get({url:'https://open.spotify.com'})).filter(cookie=>['sp_dc','sp_key'].includes(cookie.name)).map(cookie=>({name:cookie.name,value:cookie.value,domain:cookie.domain,path:cookie.path,httpOnly:cookie.httpOnly,expirationDate:cookie.expirationDate}));
  const next = { ...candidate,cookies,validUntil:Date.now()+5*60000 };
  login = false; window.hide();
  await window.loadURL(idleUrl);
  await setSession(next);authUpdated = true;
  return true;
}
async function dispatch(request) {
  if (request.action === 'start') {
    initializing = true;
    try { discord.configure(config.discordEnabled===true&&request.discordEnabled!==false);await window.loadURL(idleUrl);await setSession(request.auth || null);if(service)await service.setVolume(request.volume??0.8);return { loggedIn: !!service }; }
    finally { initializing = false; }
  }
  if (request.action === 'state') {const state=service?await service.snapshot():{loggedIn:false,phase:'idle'};if(state.track){const cover=library?.cover(state.track.requestedUri||state.track.uri);if(cover)state.track.cover=cover;}if(chosenQueue&&isMusic()){state.canShuffle=true;state.canRepeat=true;state.canPrevious=state.canPrevious||localPrevious();}const opening=!!playbackPump||!!pendingPlayback||!!service?.loading||!!service?.commandPending||!!state.track&&!!state.audio&&state.audio.ready<3&&!state.audio.error;discord.update(state).catch(()=>{});return {...state,opening,authenticating:login,authUpdated:authUpdated||session.changed,sessionRefresh:session.lastRefresh,discord:discord.snapshot()};}
  if (request.action === 'refreshSession') {await session.refresh();authUpdated=true;if(service){service.state.loggedIn=true;service.state.error=null;}return {restored:session.lastRefresh==='restored',expiresInSeconds:Math.floor((auth.validUntil-Date.now())/1000)};}
  if (request.action === 'discord') {discord.configure(request.enabled);return discord.snapshot();}
  if (request.action === 'session') { authUpdated=false;session.changed=false;return auth; }
  if (request.action === 'login') {
    if(login)return { completed:await finishLogin() };
    login=true;candidate={};window.setSize(1050,800);window.center();window.show();
    await window.loadURL(welcomeUrl);return { authenticating:true };
  }
  if (request.action === 'finishLogin') return { completed:await finishLogin() };
  if (request.action === 'query') {
    if(!service)throw new Error('SessionExpired');
    return library.query(request.kind,request.argument,request);
  }
  if(request.action==='saved'){if(!library)throw new Error('SessionExpired');return {saved:await library.saved(request.uri)};}
  if(request.action==='mutate'){if(!library)throw new Error('SessionExpired');return library.mutate(request);}
  if (request.action === 'play') {
    if(!/^spotify:(track|playlist):[a-zA-Z0-9]+$/.test(request.uri || ''))throw new Error('InvalidTrack');
    if(!service)throw new Error('SessionExpired');
    if(request.queue){if(!Array.isArray(request.queue)||request.queue.length>1000)throw new Error('InvalidQueue');request.queue=request.queue.filter(value=>/^spotify:track:[a-zA-Z0-9]{22}$/.test(value?.uri||'')).map(value=>({uri:value.uri,uid:typeof value.uid==='string'?value.uid:''}));}
    if(request.context&&!/^spotify:((playlist|track):[a-zA-Z0-9]{22}|search:.{0,600}|collection:tracks)$/.test(request.context))throw new Error('InvalidContext');
    return queuePlayback(request);
  }
  if (request.action === 'toggle') { if(service)await service.toggle();return true; }
  if(request.action==='volume'){if(service)await service.setVolume(request.value);return true;}
  if(request.action==='seek'){if(service)await service.seekFraction(request.fraction);return true;}
  if(request.action==='mode'){
    if(chosenQueue&&isMusic()){
      if(request.kind==='shuffle'){service.state.shuffle=!service.state.shuffle;arrangeQueue();}
      else if(request.kind==='repeat')service.state.repeatMode=service.state.repeatMode==='off'?'context':service.state.repeatMode==='context'?'track':'off';
      else throw new Error('UnsupportedMode');
    }else if(service)await service.mode(request.kind);return true;
  }
  if(request.action==='inspectPlayback')return service?.inspect()||null;
  if (request.action === 'next' || request.action === 'previous') {
    if(!service)return false;
    if(request.action==='next'&&chosenQueue&&service.state.canNext){
      const index=followingIndex(1);
      if(index>=0&&index<chosenQueue.tracks.length)return queuePlayback({...request,action:'play',uri:chosenQueue.tracks[index].uri,context:chosenQueue.context,queue:chosenQueue.tracks});
    }
    if(request.action==='previous'&&chosenQueue&&isMusic()&&(service.state.canPrevious||localPrevious())){
      const audio=await service.audio();
      const previous=audio?.time<=3?followingIndex(-1):-1;const index=previous>=0?previous:chosenQueue.index;
      return queuePlayback({...request,action:'play',uri:chosenQueue.tracks[index].uri,context:chosenQueue.context,queue:chosenQueue.tracks});
    }
    return queuePlayback(request);
  }
  if (request.action === 'quit') {discord.close();if(service)await service.stop();return { quitting:true }; }
  throw new Error('UnknownAction');
}
app.whenReady().then(async () => {
  await components.whenReady();
  window = new BrowserWindow({ show:false,width:640,height:360,autoHideMenuBar:true,title:'YSpotify · вход в Spotify',webPreferences:{nodeIntegration:false,contextIsolation:true,sandbox:true,webgl:false,partition:'persist:yspotify'} });
  const ownSession=window.webContents.session;
  session=new SpotifySession(ownSession);
  ownSession.webRequest.onBeforeRequest({urls:['*://*/*']},(details,callback)=>callback({cancel:!login && ['image','font'].includes(details.resourceType)}));
  ownSession.webRequest.onBeforeSendHeaders({urls:['https://*.spotify.com/*']},(details,callback)=>{
    if(login)for(const [name,value] of Object.entries(details.requestHeaders)) {
      if(name.toLowerCase()==='authorization' && value.startsWith('Bearer '))candidate.bearer=value;
      if(name.toLowerCase()==='client-token')candidate.client=value;
      if(name.toLowerCase()==='spotify-app-version')candidate.version=value;
    }
    callback({requestHeaders:details.requestHeaders});
  });
  window.webContents.setWindowOpenHandler(({url})=>{
    try { const host=new URL(url).hostname;if(login && ['accounts.spotify.com','accounts.google.com'].includes(host))window.loadURL(url).catch(()=>{}); }catch{}
    return {action:'deny'};
  });
  window.on('close',event=>{
    if(login){event.preventDefault();finishLogin().then(completed=>{if(!completed){window.hide();login=false;window.loadURL(idleUrl).catch(()=>{});}}).catch(()=>{window.hide();login=false;});}
  });
  setInterval(()=>{if(login && !initializing)finishLogin().catch(()=>{});},1500).unref();
  const connection=net.createConnection('\\\\.\\pipe\\'+pipeName);connection.setEncoding('utf8');
  let pending='',sequence=Promise.resolve();
  connection.on('data',data=>{
    pending+=data;
    if(pending.length>1024*1024){connection.destroy();app.quit();return;}
    for(;;){
      const end=pending.indexOf('\n');if(end<0)break;
      const line=pending.slice(0,end);pending=pending.slice(end+1);
      let incoming;try{incoming=JSON.parse(line);}catch{continue;}
      const handle=async()=>{
        let request;
        try{
          request=incoming;const result=await dispatch(request);
          connection.write(JSON.stringify({id:request.id,result})+'\n');
          if(request.action==='quit')setTimeout(()=>app.quit(),50);
        }catch(error){if(error.message==='SessionExpired'&&service){service.state.loggedIn=false;service.state.error='SessionExpired';}connection.write(JSON.stringify({id:request?.id,error:error.message||error.name})+'\n');}
      };
      if(['query','state','play','next','previous','toggle','volume'].includes(incoming.action))handle().catch(()=>app.quit());
      else sequence=sequence.then(handle).catch(()=>app.quit());
    }
  });
  connection.on('error',()=>{discord.close();app.quit();});connection.on('close',()=>{discord.close();app.quit();});
}).catch(()=>app.exit(1));

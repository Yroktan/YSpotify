'use strict';
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const fs = require('node:fs');
const path = require('node:path');
const worker = fs.readFileSync(path.join(__dirname, 'audio-worker.js'), 'utf8');
const defaultEndpoint = 'https://spclient.wg.spotify.com';
async function resolvePlaybackEndpoint(fetcher=fetch){
  try{
    const response=await fetcher('https://apresolve.spotify.com/?type=spclient',{signal:AbortSignal.timeout(4000)});
    if(!response.ok)return defaultEndpoint;
    const servers=(await response.json()).spclient;
    const host=Array.isArray(servers)&&servers.find(value=>typeof value==='string'&&/^[a-z0-9]+-spclient\.spotify\.com:443$/.test(value));
    return host?'https://'+host.slice(0,-4):defaultEndpoint;
  }catch{return defaultEndpoint;}
}

class SpotifyService {
  constructor(auth, window, session) {
    this.session=session;
    this.endpoint=defaultEndpoint;
    this.auth = auth; this.window = window; this.device = crypto.randomBytes(20).toString('hex');
    this.state = { loggedIn: !!auth?.bearer, registered: false, phase: 'idle', track: null, canNext: false, canPrevious: false,volume:0.8,shuffle:false,repeatMode:'off',canSeek:false,canShuffle:false,canRepeat:false };
    this.resolved=new Map();
    this.aliases=new Map();
    this.revision = 0; this.sequence = 0; this.loading = false; this.stopped = false;this.controlGeneration=0;
    this.headers = { authorization: auth.bearer, 'client-token': auth.client, 'app-platform': 'WebPlayer', origin: 'https://open.spotify.com', referer: 'https://open.spotify.com/', ...(auth.version ? { 'spotify-app-version': auth.version } : {}) };
  }
  async request(route, method = 'GET', body, cancellation) {
    if(this.session)await this.session.ensureFresh();
    this.headers.authorization=this.auth.bearer;this.headers['client-token']=this.auth.client;
    const perform=()=>fetch(this.endpoint + route, { method, headers: { ...this.headers, ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined, signal: cancellation?AbortSignal.any([AbortSignal.timeout(12000),cancellation]):AbortSignal.timeout(12000) });
    let reply=await perform();
    if(reply.status===401 && this.session){await this.session.refresh();this.headers.authorization=this.auth.bearer;this.headers['client-token']=this.auth.client;reply=await perform();}
    if (reply.status === 401) { this.state.loggedIn = false; throw new Error('SessionExpired'); }
    let data = null; try { data = await reply.json(); } catch {}
    if (!reply.ok) {
      if(route==='/track-playback/v1/devices') {
        let detail=JSON.stringify(data||{}).split(this.auth.bearer).join('[session]').split(this.auth.bearer.slice(7)).join('[session]').split(this.auth.client).join('[client]');
        detail=detail.replace(/[A-Za-z0-9+/=_-]{100,}/g,'[value]').slice(0,400);
        throw new Error('DeviceHttp'+reply.status+': '+detail);
      }
      throw new Error('SpotifyHttp' + reply.status);
    }
    this.state.loggedIn=true;if(this.state.error==='SessionExpired')this.state.error=null;
    return data;
  }
  accept(payload) {
    if (!payload?.state_machine) return;
    const changed=this.machine?.state_machine_id!==payload.state_machine.state_machine_id;
    this.machine = payload.state_machine;
    this.reference = payload.updated_state_ref || payload.state_ref || (changed?{state_index:0}:this.reference) || {};
    this.revision++;
  }
  unpack(message) {
    const results = [];
    for (const value of message.payloads || []) try {
      if (typeof value === 'object') results.push(value);
      else if (value.trim().startsWith('{')) results.push(JSON.parse(value));
      else {
        let bytes = Buffer.from(value, 'base64');
        if ((message.headers?.['Transfer-Encoding'] || '').toLowerCase() === 'gzip') bytes = zlib.gunzipSync(bytes);
        results.push(JSON.parse(bytes.toString('utf8')));
      }
    } catch {}
    return results;
  }
  async initialize() {
    const preferredEndpoint=resolvePlaybackEndpoint();
    if(this.session)await this.session.ensureFresh();
    let resolveConnection;
    const connected = new Promise(resolve => { resolveConnection = resolve; });
    this.dealer = new WebSocket('wss://dealer.spotify.com/?access_token=' + encodeURIComponent(this.auth.bearer.slice(7)));
    this.dealer.addEventListener('open', () => { this.ping = setInterval(() => { if(this.dealer.readyState === WebSocket.OPEN)this.dealer.send('{"type":"ping"}'); }, 25000); });
    this.dealer.addEventListener('message', event => {
      try {
        const message = JSON.parse(event.data);
        const connection = message.headers?.['Spotify-Connection-Id'] || message.headers?.['x-spotify-connection-id'];
        if (connection) resolveConnection(connection);
        for (const payload of this.unpack(message)) this.accept(payload);
      } catch {}
    });
    this.dealer.addEventListener('error', () => { this.state.phase = 'connection-error'; });
    const connection = await Promise.race([connected, new Promise(resolve => setTimeout(() => resolve(null), 10000))]);
    if (!connection) throw new Error('DealerTimeout');
    this.endpoint=await preferredEndpoint;
    const registration = this.auth.registration ? structuredClone(this.auth.registration) : { device: { brand: 'spotify', capabilities: { change_volume: true, enable_play_token: true, supports_file_media_type: true, play_token_lost_behavior: 'pause', disable_connect: false, audio_podcasts: true, video_playback: false, manifest_formats: ['file_ids_mp4', 'file_ids_mp4_dual'], supports_preferred_media_type: true, supports_playback_offsets: true, supports_playback_speed: true }, device_type: 'computer', metadata: {}, model: 'web_player', is_group: false, is_public: true }, outro_endcontent_snooping: false, volume: 65535 };
    Object.assign(registration.device, { device_id: this.device, name: 'YSpotify', platform_identifier: 'web_player windows 10;chrome ' + process.versions.chrome + ';desktop' });
    registration.device.client_version ||= 'harmony:4.80.0-3cdf02c91';
    registration.client_version ||= registration.device.client_version;
    registration.connection_id = connection;
    const result = await this.request('/track-playback/v1/devices', 'POST', registration);
    this.sequence = result?.initial_seq_num || 0;
    this.state.registered = true;
    await this.window.webContents.executeJavaScript('('+worker+')('+JSON.stringify({prepareOnly:true,headers:this.headers})+')');
    this.audioEvents=details=>{if(details.message==='YSpotifyAudioEnded')this.tick().catch(error=>{this.state.error=error.message;});if(details.message==='YSpotifyAudioStarted')this.preloadNext().catch(()=>{});};
    this.window.webContents.on('console-message',this.audioEvents);
    this.reportTimer = setInterval(() => this.tick().catch(error => { this.state.error = error.message; }), 5000);
  }
  active() {
    if (!this.machine) return null;
    let index = this.reference?.state_id?this.machine.states?.findIndex(s => s.state_id === this.reference.state_id):this.reference?.state_index;
    if (!Number.isInteger(index) || index < 0) index = 0;
    const state = this.machine.states?.[index];
    const track = this.machine.tracks?.[state?.track ?? 0];
    return { state, track, index };
  }
  cancelPreparation(){this.commandAbort?.abort();}
  async command(action, uri, valid=()=>true, pressedAt,options={}) {
    if(!valid())return false;
    if(action==='skip_next'&&!this.state.canNext)throw new Error('PlaybackRestricted');
    if(action==='skip_prev'){
      const audio=await this.audio();
      if(audio?.time>3&&this.state.canSeek){await this.seekSeconds(0);return true;}
      if(!this.state.canPrevious)throw new Error('PlaybackRestricted');
      if(audio?.time>3)await this.tick();
    }
    this.commandPending=true;
    this.controlGeneration++;
    const cancellation=this.commandAbort=new AbortController();
    try{
    this.requestedAt=Number(pressedAt)||Date.now();
    this.state.timing={endpoint:this.endpoint,pressedToCommandMs:Date.now()-this.requestedAt};const commandStarted=Date.now();
    const revision = this.revision;
    const command = { endpoint: action, options: {}, logging_params: { page_instance_ids: [], interaction_ids: [], command_id: crypto.randomUUID().replaceAll('-', '') } };
    if(uri){
      const contextUri=/^spotify:(playlist:[a-zA-Z0-9]{22}|collection:tracks)$/.test(options.context||'')?options.context:uri;
      const context={uri:contextUri,url:'context://'+contextUri,metadata:{}};
      Object.assign(command,{context,play_origin:{feature_identifier:contextUri.startsWith('spotify:playlist:')?'playlist':'search',referrer_identifier:'yspotify'}});
      if(contextUri!==uri)command.options.skip_to={track_uri:uri};
    }
    const previousUri=this.active()?.track?.metadata?.uri;
    const previousState=this.active()?.state?.state_id;
    const response=await this.request('/connect-state/v1/player/command/from/' + this.device + '/to/' + this.device, 'POST', { command });
    this.state.timing.commandAckMs=Date.now()-commandStarted;
    this.accept(response);
    const matches=()=>{
      if(this.revision===revision)return false;
      if(action!=='play')return this.active()?.track?.metadata?.uri!==previousUri||this.active()?.state?.state_id!==previousState;
      if(!uri?.startsWith('spotify:track:'))return true;
      const meta=this.active()?.track?.metadata;
      const expected=options.expected;const normalize=value=>String(value||'').normalize('NFKC').trim().toLowerCase();
      const sameRecording=expected&&normalize(meta?.name)===normalize(expected.name)&&(meta?.authors||[]).some(author=>normalize(expected.artist).split(', ').includes(normalize(author.name)));
      return meta?.uri===uri||meta?.linked_from_uri===uri||meta?.context_uri===uri||sameRecording||String(meta?.uri||'').startsWith('spotify:ad:');
    };
    for(let n=0;n<150 && !matches() && valid();n++)await new Promise(resolve => setTimeout(resolve,40));
    if(!valid())return false;
    if(this.revision === revision)throw new Error('PlaybackStateTimeout');
    if(!matches()){try{fs.writeFileSync(path.join(require('electron').app.getPath('userData'),'playback-diagnostics.json'),JSON.stringify({requestedUri:uri,action,previousUri,revisionBefore:revision,revisionAfter:this.revision,info:this.inspect()}));}catch{}throw new Error('PlaybackStateTimeout');}
    if(action==='play'&&String(this.active()?.track?.metadata?.uri||'').startsWith('spotify:track:')){if(this.aliases.size>=64)this.aliases.delete(this.aliases.keys().next().value);this.aliases.set(this.active().track.metadata.uri,uri);}
    this.state.timing.stateAfterMs=Date.now()-this.requestedAt;
    await this.loadActive(valid,cancellation.signal);return true;
    }finally{this.commandPending=false;}
  }
  async resolveFile(file,cancellation){
    const cached=this.resolved.get(file);if(cached&&cached.until>Date.now())return cached.url;
    const value=await this.request('/storage-resolve/v2/files/audio/interactive/10/'+encodeURIComponent(file)+'?version=10000000&product=9&platform=39&alt=json','GET',undefined,cancellation);
    if(!value?.cdnurl?.[0])throw new Error('MissingAudioUrl');
    if(this.resolved.size>=16)this.resolved.delete(this.resolved.keys().next().value);
    this.resolved.set(file,{url:value.cdnurl[0],until:Date.now()+120000});return value.cdnurl[0];
  }
  async loadActive(valid=()=>true,cancellation,startAt=0) {
    if(this.loading || this.stopped)return;
    this.loading = true;
    try {
      const current = this.active();
      const meta = current?.track?.metadata;
      const file = current?.track?.manifest?.file_ids_mp4?.[0];
      if(!file?.file_id)throw new Error('UnsupportedCurrentMedia');
      this.state.phase = 'loading'; this.state.error = null;
      this.state.track = { uri: meta?.uri || '',requestedUri:this.aliases.get(meta?.uri)||meta?.linked_from_uri||meta?.uri||'',linkedFromUri:meta?.linked_from_uri||'', name: meta?.name || '', artist: (meta?.authors || []).map(a=>a.name).filter(Boolean).join(', '), duration: meta?.duration || 0,cover:meta?.images?.[0]?.url||meta?.image_url||'' };
      const disallowed=name=>current.state?.[name]===true||current.state?.restrictions?.[name]===true||(current.state?.restrictions?.[name+'_reasons']?.length||0)>0;
      this.state.canNext = !disallowed('disallow_skipping_next');
      this.state.canPrevious = !disallowed('disallow_skipping_prev');
      const ad=!String(meta?.uri||'').startsWith('spotify:track:');
      this.state.canSeek=!ad&&!disallowed('disallow_seeking');
      this.state.canShuffle=!ad&&!disallowed('disallow_toggling_shuffle');
      this.state.canRepeat=!ad&&!disallowed('disallow_toggling_repeat_context')&&!disallowed('disallow_toggling_repeat_track');
      const resolveStarted=Date.now();const url=await this.resolveFile(file.file_id,cancellation);if(this.state.timing)this.state.timing.resolveMs=Date.now()-resolveStarted;
      if(!valid())return;
      await this.window.webContents.executeJavaScript('(' + worker + ')(' + JSON.stringify({ url, headers: this.headers, license: this.endpoint + '/widevine-license/v1/audio/license',requestedAt:this.requestedAt,volume:this.state.volume,startAt }) + ')',true);
      this.lastEnded = null; this.state.phase = 'ready';
    } finally { this.loading=false; }
  }
  async preloadNext(){
    const generation=this.controlGeneration,current=this.active();
    const nextIndex=current?.state?.transitions?.advance?.state_index;
    const next=this.machine?.tracks?.[this.machine?.states?.[nextIndex]?.track];
    const file=next?.manifest?.file_ids_mp4?.[0]?.file_id;
    if(!file||file===current?.track?.manifest?.file_ids_mp4?.[0]?.file_id)return;
    const url=await this.resolveFile(file);
    if(this.stopped||generation!==this.controlGeneration)return;
    await this.window.webContents.executeJavaScript('window.yspotifyWarmAudio&&window.yspotifyWarmAudio('+JSON.stringify(url)+')');
  }
  async audio() { return this.window.webContents.executeJavaScript('window.yspotifyAudioState ? window.yspotifyAudioState() : null'); }
  inspect(){const active=this.active();return {stateIndex:active?.index,trackIndex:active?.state?.track,trackUris:(this.machine?.tracks||[]).map(track=>track.metadata?.uri),trackLinks:(this.machine?.tracks||[]).map(track=>({uri:track.metadata?.uri,linked:track.metadata?.linked_from_uri,name:track.metadata?.name})),machineKeys:Object.keys(this.machine||{}),stateKeys:Object.keys(active?.state||{}),metaKeys:Object.keys(active?.track?.metadata||{}),restrictions:active?.state?.restrictions,transitions:active?.state?.transitions,states:(this.machine?.states||[]).map((value,index)=>({index,track:value.track,initial:value.initial_playback_position,start:value.segment_start_position,stop:value.segment_stop_position,duration:value.duration_override,offset:value.position_offset,transitions:value.transitions}))};}
  async snapshot() { return { ...this.state, audio: await this.audio() }; }
  async toggle() { this.controlGeneration++;await this.window.webContents.executeJavaScript('window.yspotifyToggle ? window.yspotifyToggle() : false',true);this.tick().catch(error=>{this.state.error=error.message;}); }
  async setVolume(value){
    const volume=Math.max(0,Math.min(1,Number(value)));if(!Number.isFinite(volume))throw new Error('InvalidVolume');
    this.state.volume=volume;await this.window.webContents.executeJavaScript('window.yspotifyVolume='+JSON.stringify(volume)+';document.querySelector("audio")&&(document.querySelector("audio").volume=window.yspotifyVolume)');
  }
  async sendControl(action,value){
    const command={endpoint:action,value,logging_params:{page_instance_ids:[],interaction_ids:[],command_id:crypto.randomUUID().replaceAll('-','')}};
    const response=await this.request('/connect-state/v1/player/command/from/'+this.device+'/to/'+this.device,'POST',{command});this.accept(response);return response;
  }
  async seekFraction(fraction){const value=Number(fraction);if(!Number.isFinite(value)||value<0||value>1)throw new Error('InvalidSeek');const audio=await this.audio();if(audio?.duration)await this.seekSeconds(Math.min(audio.duration-0.1,audio.duration*value));}
  async seekSeconds(seconds){
    if(!this.state.canSeek)throw new Error('PlaybackRestricted');
    await this.sendControl('seek_to',Math.round(Math.max(0,seconds)*1000));
    const moved=await this.window.webContents.executeJavaScript('window.yspotifySeek&&window.yspotifySeek('+JSON.stringify(seconds)+')',true);
    if(!moved)await this.loadActive(()=>true,undefined,seconds);
    await this.tick();
  }
  async mode(kind){
    if(kind==='shuffle'){
      if(!this.state.canShuffle)throw new Error('PlaybackRestricted');
      const value=!this.state.shuffle;await this.sendControl('set_shuffling_context',value);this.state.shuffle=value;
    }else if(kind==='repeat'){
      if(!this.state.canRepeat)throw new Error('PlaybackRestricted');
      const mode=this.state.repeatMode==='off'?'context':this.state.repeatMode==='context'?'track':'off';
      await this.sendControl('set_repeating_context',mode==='context');await this.sendControl('set_repeating_track',mode==='track');this.state.repeatMode=mode;
    }else throw new Error('UnsupportedMode');
  }
  async tick() {
    if(this.stopped || this.reporting || this.loading || this.commandPending || !this.machine || !this.state.loggedIn)return;
    this.reporting=true;
    try {
      const generation=this.controlGeneration;
      const audio=await this.audio(), current=this.active();
      if(!audio || !current?.state || audio.error)return;
      const advertisement=String(current.track?.metadata?.uri||'').startsWith('spotify:ad:');
      if(!advertisement&&audio.license!==200)return;
      const stateRef={ state_machine_id:this.machine.state_machine_id,state_id:current.state.state_id,paused:audio.paused };
      const result=await this.request('/track-playback/v1/devices/'+this.device+'/state','PUT',{seq_num:++this.sequence,state_ref:stateRef,sub_state:{playback_speed:audio.paused?0:1,position:Math.round(audio.time*1000),duration:Math.round((audio.duration||0)*1000),media_type:'AUDIO',bitrate:128000,audio_quality:'HIGH',format:10,is_video_on:false},debug_source:audio.ended?'track_data_finalized':'playback_progress'});
      if(generation!==this.controlGeneration)return;
      this.accept(result);
      if(audio.ended && this.lastEnded !== current.state.state_id) {
        this.lastEnded=current.state.state_id;
        this.state.completedTracks=(this.state.completedTracks||0)+1;this.state.lastCompletedUri=this.aliases.get(current.track?.metadata?.uri)||current.track?.metadata?.uri;
        const next=current.state.transitions?.advance;
        if(this.active()?.state?.state_id===current.state.state_id&&Number.isInteger(next?.state_index) && this.machine.states?.[next.state_index]) {
          this.reference={state_index:next.state_index};
        }
        if(this.active()?.state?.state_id!==current.state.state_id){const handled=this.onAdvance&&await this.onAdvance(current.track?.metadata?.uri,this.active()?.track?.metadata?.uri);if(!handled)await this.loadActive();}
      }
    } finally {this.reporting=false;}
  }
  async stop() { this.stopped=true;this.cancelPreparation();clearInterval(this.ping);clearInterval(this.reportTimer);this.dealer?.close();if(this.audioEvents)this.window.webContents.removeListener('console-message',this.audioEvents);try {await this.window.webContents.executeJavaScript('window.yspotifyDispose && window.yspotifyDispose()');}catch{} }
}
module.exports={SpotifyService,resolvePlaybackEndpoint};

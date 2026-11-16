async function startAudio(options) {
  if(!window.yspotifyMediaEngine)window.yspotifyMediaEngine=(async()=>{
    const configuration=[{initDataTypes:['cenc'],audioCapabilities:[{contentType:'audio/mp4; codecs="mp4a.40.2"'}],distinctiveIdentifier:'optional',persistentState:'optional',sessionTypes:['temporary']}];
    const access=await navigator.requestMediaKeySystemAccess('com.widevine.alpha',configuration);
    const keys=await access.createMediaKeys();
    const info={keys,certificate:null,certificateAccepted:false};
    try{const certificate=await fetch('https://spclient.wg.spotify.com/widevine-license/v1/application-certificate',{headers:options.headers});info.certificate=certificate.status;if(certificate.ok)info.certificateAccepted=await keys.setServerCertificate(await certificate.arrayBuffer());}catch{}
    return info;
  })();
  const engine=await window.yspotifyMediaEngine;
  if(!window.yspotifyWarmAudio){
    let warm=null;const stats={phase:'idle'};window.yspotifyWarmState=()=>({...stats});
    window.yspotifyWarmAudio=url=>{
      if(warm?.url===url)return true;
      warm?.abort.abort();
      const entry={url,abort:new AbortController()};warm=entry;
      stats.phase='fetching';stats.bytes=0;
      entry.ready=(async()=>{
        try{
          const response=await fetch(url,{headers:{Range:'bytes=0-131071'},signal:AbortSignal.any([entry.abort.signal,AbortSignal.timeout(2500)])});
          const range=response.headers.get('content-range')||'';
          stats.status=response.status;stats.rangeVisible=!!range;
          if(response.status!==206||(range&&!/^bytes 0-\d+\/\d+$/.test(range))){stats.phase='range-unavailable';await response.body?.cancel();return null;}
          const reader=response.body.getReader(),parts=[];let size=0;
          for(;;){const part=await reader.read();if(part.done)break;size+=part.value.byteLength;if(size>131072){await reader.cancel();return null;}parts.push(part.value);}
          if(!size||entry.abort.signal.aborted)return null;
          const bytes=new Uint8Array(size);let offset=0;for(const part of parts){bytes.set(part,offset);offset+=part.byteLength;}entry.bytes=bytes;stats.phase='ready';stats.bytes=size;return bytes;
        }catch(error){stats.phase=error.name;return null;}
      })();return true;
    };
    window.yspotifyTakeWarmAudio=url=>{const entry=warm;warm=null;window.yspotifyLastWarmSelection=!entry?'empty':entry.url!==url?'different-url':!entry.bytes?'not-ready':'hit';if(!entry||entry.url!==url||!entry.bytes){entry?.abort.abort();return null;}return entry;};
  }
  if(options.prepareOnly)return true;
  if(window.yspotifyDispose)await window.yspotifyDispose();
  const abort=new AbortController(),sessions=new Set();let objectUrl;
  const audio = document.createElement('audio');
  audio.preload = 'auto';
  audio.volume=window.yspotifyVolume??options.volume??0.8;
  audio.addEventListener('loadedmetadata',()=>{if(options.startAt>0)audio.currentTime=options.startAt;},{once:true});
  document.body.replaceChildren(audio);
  const state = { phase: 'initializing', license: null, keyStatuses: [], error: null,certificate:engine.certificate,certificateAccepted:engine.certificateAccepted,startedAfterMs:null };
  const warm=window.yspotifyTakeWarmAudio(options.url);
  state.warmSelection=window.yspotifyLastWarmSelection;
  const elapsed=()=>Math.max(0,Date.now()-(options.requestedAt||Date.now()));state.workerReadyAfterMs=elapsed();
  window.yspotifyAudioState = () => ({ ...state,warm:window.yspotifyWarmState(),volume:audio.volume, paused: audio.paused, ended: audio.ended, time: audio.currentTime, duration: Number.isFinite(audio.duration) ? audio.duration : null, ready: audio.readyState, errorCode: audio.error?.code ?? null });
  window.yspotifyToggle=()=>{if(audio.paused)audio.play().catch(error=>{if(error.name!=='AbortError')state.error=error.name;});else audio.pause();return true;};
  window.yspotifySeek=seconds=>{for(let i=0;i<audio.buffered.length;i++)if(seconds>=audio.buffered.start(i)&&seconds<audio.buffered.end(i)){audio.currentTime=seconds;return true;}return false;};
  window.yspotifyDispose=async()=>{abort.abort();audio.pause();audio.removeAttribute('src');audio.load();for(const session of sessions)try{await session.close();}catch{}await audio.setMediaKeys(null);if(objectUrl)URL.revokeObjectURL(objectUrl);window.yspotifyToggle=null;window.yspotifyAudioState=null;window.yspotifyDispose=null;};
  const keys=engine.keys;
  await audio.setMediaKeys(keys);
  audio.addEventListener('encrypted', async event => {
    try {
      const session = keys.createSession('temporary');
      sessions.add(session);
      session.addEventListener('keystatuseschange', () => { state.keyStatuses = Array.from(session.keyStatuses.values()); });
      session.addEventListener('message', async message => {
        try {
          const response = await fetch(options.license, { method: 'POST', headers: options.headers, body: message.message,signal:abort.signal });
          state.licenseAfterMs=elapsed();
          state.license = response.status;
          if (!response.ok) { state.phase = 'license-rejected'; audio.pause(); return; }
          await session.update(await response.arrayBuffer());
          state.phase = !audio.paused&&audio.readyState>=3?'playing':'licensed';
        } catch (error) { state.error = error.name; state.phase = 'license-error'; audio.pause(); }
      });
      await session.generateRequest(event.initDataType, event.initData);
    } catch (error) { state.error = error.name; state.phase = 'eme-error'; }
  });
  audio.addEventListener('playing', () => { state.phase = 'playing';if(state.startedAfterMs===null){state.startedAfterMs=Math.max(0,Date.now()-(options.requestedAt||Date.now()));console.info('YSpotifyAudioStarted');} });
  audio.addEventListener('ended', () => { state.phase = 'ended';console.info('YSpotifyAudioEnded'); });
  audio.addEventListener('error', () => { state.phase = 'media-error'; });
  const source = new MediaSource();
  objectUrl=URL.createObjectURL(source);audio.src=objectUrl;
  source.addEventListener('sourceopen', async () => {
    try {
      const buffer = source.addSourceBuffer('audio/mp4; codecs="mp4a.40.2"');
      const update = action => new Promise((resolve, reject) => {
        const complete=()=>{cleanup();resolve();};const failed=()=>{cleanup();reject(new Error('SourceBuffer'));};
        const cleanup=()=>{buffer.removeEventListener('updateend',complete);buffer.removeEventListener('error',failed);};
        buffer.addEventListener('updateend', complete, { once: true });
        buffer.addEventListener('error', failed, { once: true });
        action();
      });
      const seed=warm?await warm.ready:null;
      const responsePending=fetch(options.url,{signal:abort.signal,...(seed?{headers:{Range:'bytes='+seed.byteLength+'-'}}:{})});
      if(seed){state.warmedBytes=seed.byteLength;state.firstBytesAfterMs=elapsed();await update(()=>buffer.appendBuffer(seed));}
      const response=await responsePending;state.cdnHeadersAfterMs=elapsed();
      if(!response.ok)throw new Error('MediaFetch');
      let skip=seed&&response.status===200?seed.byteLength:0;
      const reader=response.body.getReader();
      for (;;) {
        const part = await reader.read();
        if(state.firstBytesAfterMs===undefined&&!part.done)state.firstBytesAfterMs=elapsed();
        if (part.done) break;
        if (state.phase.includes('error') || state.phase === 'license-rejected') { await reader.cancel(); break; }
        while (!abort.signal.aborted && buffer.buffered.length && buffer.buffered.end(buffer.buffered.length - 1) - audio.currentTime > 45) {
          await new Promise(resolve => setTimeout(resolve, 250));
          if (audio.error || state.phase.includes('error') || state.phase === 'license-rejected') { await reader.cancel(); return; }
        }
        if(abort.signal.aborted){await reader.cancel();return;}
        let bytes=part.value;if(skip){const removed=Math.min(skip,bytes.byteLength);skip-=removed;bytes=bytes.subarray(removed);if(!bytes.byteLength)continue;}
        if (audio.currentTime > 25 && buffer.buffered.length && buffer.buffered.start(0) < audio.currentTime - 25) await update(() => buffer.remove(0, audio.currentTime - 20));
        await update(() => buffer.appendBuffer(bytes));
      }
      if (source.readyState === 'open' && !buffer.updating) source.endOfStream();
    } catch (error) { state.phase = 'stream-error'; state.error = error.name; }
  }, { once: true });
  audio.play().catch(error => { state.error = error.name; });
  state.phase = 'loading';
  return true;
}

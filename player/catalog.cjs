'use strict';
const fs=require('node:fs');const path=require('node:path');const crypto=require('node:crypto');
const definitions = {
  search: ['searchTopResultsList', 'dd78eaff943eba629ed70ee25517b9cea0dcaa41193e2592ae2727660b21892c'],
  liked: ['fetchLibraryTracks', '087278b20b743578a6262c2b0b4bcd20d879c503cc359a2285baf083ef944240'],
  library: ['libraryV3', '973e511ca44261fda7eebac8b653155e7caee3675abb4fb110cc1b8c78b091c3'],
  playlist: ['fetchPlaylist', '8964e8eafb21aa992a7d951d256d83285c04be2105d209262901de70cb97584a'],
  saved:['areEntitiesInLibrary','134337999233cc6fdd6b1e6dbf94841409f04a946c5c7b744b09ba0dfe5a85ed'],
  profile:['profileAttributes','08ffb4730af3746e04a8301396f20875dbbce10c75243803091a9274eacc8ac0'],
  addFavorite:['addToLibrary','896ebcb47815681340860d121cb5d494e157e2a78d3950385cd54e0393c67148'],
  removeFavorite:['removeFromLibrary','896ebcb47815681340860d121cb5d494e157e2a78d3950385cd54e0393c67148'],
  addPlaylist:['addToPlaylist','b907ad6b088d5bf7680fa81198996d58398c2d55dfcdf59fcc959e0be98a418a'],
  removePlaylist:['removeFromPlaylist','b907ad6b088d5bf7680fa81198996d58398c2d55dfcdf59fcc959e0be98a418a']
};
function variables(kind, argument,offset=0) {
  if (kind === 'search') return { query: argument || '', limit: 50, offset: 0, numberOfTopResults: 50, includeArtistHasConcertsField: false, includeAudiobooks: true, includeAuthors: true, includePreReleases: true, includeAlbumPreReleases: false, includeEpisodeContentRatingsV2: true, isPrefix: null, sectionFilters: ['GENERIC', 'VIDEO_CONTENT'] };
  if (kind === 'library') return { filters: [], order: null, textFilter: '', features: ['LIKED_SONGS', 'YOUR_EPISODES_V2', 'PRERELEASES', 'EVENTS'], limit: 100, offset, flatten: true, expandedFolders: [], folderUri: null, includeFoldersWhenFlattening: true };
  if (kind === 'playlist') return { uri: argument, offset, limit: 100, enableWatchFeedEntrypoint: true, includeEpisodeContentRatingsV2: true };
  return { offset, limit: 100 };
}
function coverFrom(item){
  const sources=item.albumOfTrack?.coverArt?.sources||item.album?.images||item.images?.items?.flatMap(value=>value.sources||[])||[];
  return sources.filter(value=>typeof value.url==='string'&&/^https:\/\/([a-z0-9-]+\.)?scdn\.co\//.test(value.url)).sort((a,b)=>Math.abs((a.width||64)-64)-Math.abs((b.width||64)-64))[0]?.url||'';
}
function rowsFrom(data, kind) {
  const pending = [data], found = new Map(),playlistRefs=[],playlistUids=new Set();
  for (let seen = 0; pending.length && seen < 20000 && found.size <= 100; seen++) {
    const item = pending.pop();
    if (!item || typeof item !== 'object') continue;
    if(item.track?.data||item.itemV2?.data||item.item?.data){
      const wrapper=item.track||item.itemV2||item.item,data=wrapper.data,id=wrapper.id||data.id;
      const uri=data.uri||wrapper._uri||wrapper.uri||(id?'spotify:track:'+id:'');
      if(uri)pending.push({...data,uri,uid:item.uid||wrapper.uid||data.uid});
      const uid=item.uid||wrapper.uid||data.uid;if(kind==='playlist'&&uid&&uri?.startsWith('spotify:track:')&&!playlistUids.has(uid)){playlistUids.add(uid);playlistRefs.push({uri,uid});}
    }
    const accepted = kind === 'library' ? /^spotify:playlist:/ : /^spotify:track:/;
    if(item.uid&&found.has(item.uri)&&!found.get(item.uri).uid)found.get(item.uri).uid=item.uid;
    if (typeof item.uri === 'string' && accepted.test(item.uri) && typeof item.name === 'string' && !found.has(item.uri)) {
      const artists = item.artists?.items || item.artistsV2?.items || (Array.isArray(item.artists) ? item.artists : []);
      const artist = artists.map(a => a.profile?.name || a.name || a.data?.profile?.name || '').filter(Boolean).join(', ');
      const duration = Number(item.duration?.totalMilliseconds || item.trackDuration?.totalMilliseconds || (typeof item.duration==='number'?item.duration:0) || item.duration_ms || 0);
      found.set(item.uri, { uri: item.uri,uid:item.uid||'',owner:item.ownerV2?.data?.username||item.owner?.username||'',canEdit:item.isOwnedBySelf===true||item.permissions?.canAdd===true||item.permissions?.canEdit===true,name: item.name, artist: kind === 'library' ? 'Плейлист' : artist, album: item.albumOfTrack?.name || item.album?.name || '',cover:coverFrom(item),saved:kind==='liked'||item.saved===true, duration: duration ? Math.floor(duration / 60000) + ':' + String(Math.floor(duration / 1000) % 60).padStart(2, '0') : '' });
    }
    const values = Array.isArray(item) ? item : Object.values(item);
    for (let n = values.length - 1; n >= 0; n--) if (values[n] && typeof values[n] === 'object') pending.push(values[n]);
  }
  if(kind==='playlist'&&playlistRefs.length)return playlistRefs.map(ref=>found.has(ref.uri)?{...found.get(ref.uri),uid:ref.uid}:null).filter(Boolean).slice(0,100);
  return [...found.values()].slice(0,100);
}
async function operation(auth,kind,vars,session) {
  const definition=definitions[kind];
  if (!definition) throw new Error('UnsupportedQuery');
  if(session)await session.ensureFresh();
  const perform=()=>fetch('https://api-partner.spotify.com/pathfinder/v2/query', {
    method: 'POST', headers: { authorization: auth.bearer, 'client-token': auth.client, 'app-platform': 'WebPlayer', 'content-type': 'application/json', origin: 'https://open.spotify.com', referer: 'https://open.spotify.com/', ...(auth.version ? { 'spotify-app-version': auth.version } : {}) },
    body: JSON.stringify({ operationName: definition[0], variables: vars, extensions: { persistedQuery: { version: 1, sha256Hash: definition[1] } } }), signal: AbortSignal.timeout(15000)
  });
  let response=await perform();
  if(response.status===401 && session){await session.refresh();response=await perform();}
  if(response.status===401)throw new Error('SessionExpired');
  if (!response.ok) throw new Error('CatalogHttp' + response.status);
  const data = await response.json();
  if (data.errors?.length) {const codes=data.errors.map(value=>value.extensions?.code||value.message?.split(':')[0]).map(value=>String(value).replace(/[^a-zA-Z0-9_ ]/g,'').slice(0,70));throw new Error('CatalogQueryChanged:'+codes.join(','));}
  return data.data;
}
class CatalogStore {
  constructor(auth,session,directory){
    this.auth=auth;this.session=session;this.entries={};this.pending=new Map();
    this.directory=directory;
    const identity=(auth.cookies||[]).find(value=>value.name==='sp_dc')?.value||auth.bearer;
    const account=crypto.createHash('sha256').update(identity).digest('hex').slice(0,24);
    this.file=path.join(directory,'catalog-'+account+'.json');
    try{if(fs.statSync(this.file).size<8*1024*1024)this.entries=JSON.parse(fs.readFileSync(this.file,'utf8')).entries||{};}catch{}
  }
  key(kind,argument){return kind+':'+(argument||'');}
  cached(kind,argument){return this.entries[this.key(kind,argument)]?.rows||null;}
  cover(uri){for(const entry of Object.values(this.entries)){const row=entry.rows.find(row=>row.uri===uri&&row.cover);if(row)return row.cover;}return '';}
  track(uri){for(const entry of Object.values(this.entries)){const row=entry.rows.find(row=>row.uri===uri);if(row)return row;}return null;}
  async ownProfile(){if(this.username!==undefined)return this.username;const data=await operation(this.auth,'profile',{},this.session);this.username=data?.me?.profile?.username||'';return this.username;}
  write(){
    const keys=Object.keys(this.entries).sort((a,b)=>this.entries[b].at-this.entries[a].at);for(const key of keys.slice(48))delete this.entries[key];
    const temporary=this.file+'.tmp';try{fs.writeFileSync(temporary,JSON.stringify({version:1,entries:this.entries}));fs.renameSync(temporary,this.file);}catch{}
  }
  async likedPages(){
    const rows=new Map(),pages=[];let total=null,offset=0;
    for(let n=0;n<200;n++){
      const data=await operation(this.auth,'liked',variables('liked',null,offset),this.session);
      const page=data?.me?.library?.tracks;
      if(!page||!Array.isArray(page.items)||!Number.isInteger(page.totalCount)||page.totalCount<0)throw new Error('CatalogPageInvalid');
      if(total===null)total=page.totalCount;
      if(total!==page.totalCount)throw new Error('CatalogChangedDuringLoad');
      if(total>10000)throw new Error('CatalogTooLarge');
      const limit=Number(page.pagingInfo?.limit)||100;
      if(!Number.isInteger(limit)||limit<1||limit>1000||page.pagingInfo?.offset!==undefined&&Number(page.pagingInfo.offset)!==offset)throw new Error('CatalogPageInvalid');
      const count=Math.min(limit,total-offset);
      if(page.items.length!==count)throw new Error('CatalogIncomplete');
      const parsed=rowsFrom(data,'liked');for(const row of parsed)rows.set(row.uri,row);
      pages.push({offset,received:page.items.length,displayed:parsed.length});offset+=count;
      if(offset===total)return {rows:[...rows.values()],totalCount:total,sourceCount:offset,complete:true,pages};
      if(count===0)throw new Error('CatalogIncomplete');
    }
    throw new Error('CatalogIncomplete');
  }
  async query(kind,argument,options={}){
    const key=this.key(kind,argument),cached=this.entries[key];
    if(options.cacheOnly)return {rows:cached?.rows||[],cached:!!cached,stale:cached?Date.now()-cached.at>60000:true};
    if(cached&&!options.force&&Date.now()-cached.at<45000&&(kind!=='liked'||cached.complete===true&&cached.sourceCount===cached.totalCount))return {rows:cached.rows,cached:true,stale:false};
    if(this.pending.has(key))return this.pending.get(key);
    const task=(async()=>{
      if(kind==='liked'){
        let result;
        for(let attempt=0;attempt<2;attempt++)try{result=await this.likedPages();break;}catch(error){if(attempt===1||!['CatalogIncomplete','CatalogChangedDuringLoad'].includes(error.message))throw error;}
        this.entries[key]={at:Date.now(),...result};this.write();return {...result,cached:false,stale:false};
      }
      const data=await operation(this.auth,kind,variables(kind,argument),this.session);const rows=rowsFrom(data,kind);
      if(kind==='library'){const username=await this.ownProfile();for(const row of rows)row.canEdit=row.canEdit||!!username&&row.owner===username;try{fs.writeFileSync(path.join(this.directory,'library-info.json'),JSON.stringify({hasProfile:!!username,count:rows.length,withOwner:rows.filter(row=>row.owner).length,editable:rows.filter(row=>row.canEdit).length}));}catch{}}
      if(kind==='playlist'){const playlist=data?.playlistV2||data?.playlist||{};try{fs.writeFileSync(path.join(this.directory,'playlist-info.json'),JSON.stringify({keys:Object.keys(playlist),ownerKeys:Object.keys(playlist.ownerV2?.data||playlist.owner||{}),permissions:playlist.permissions||null}));}catch{}}
      if(kind!=='search'&&rows.length===100){
        const rowKey=row=>kind==='playlist'?row.uri+'#'+row.uid:row.uri;const seen=new Set(rows.map(rowKey));
        for(let offset=100;offset<10000;offset+=100){
          const page=rowsFrom(await operation(this.auth,kind,variables(kind,argument,offset),this.session),kind);let added=0;
          for(const row of page)if(!seen.has(rowKey(row))){seen.add(rowKey(row));rows.push(row);added++;}
          if(page.length<100||added===0)break;
        }
      }
      const result={rows,cached:false,stale:false};this.entries[key]={at:Date.now(),rows};this.write();return result;
    })();this.pending.set(key,task);try{return await task;}finally{this.pending.delete(key);}
  }
  async saved(uri){
    const data=await operation(this.auth,'saved',{uris:[uri]},this.session);
    return !!data?.lookup?.[0]?.data?.saved;
  }
  async mutate(request){
    const {kind,uri,playlist,uid}=request;
    if(!/^spotify:track:[a-zA-Z0-9]{22}$/.test(uri||''))throw new Error('InvalidTrack');
    if(kind==='addFavorite'||kind==='removeFavorite'){
      await operation(this.auth,kind,{libraryItemUris:[uri],...(kind==='addFavorite'?{interactionId:null}:{})},this.session);
      const saved=await this.saved(uri);if(saved!==(kind==='addFavorite'))throw new Error('LibraryChangeNotConfirmed');
      for(const entry of Object.values(this.entries))for(const row of entry.rows)if(row.uri===uri)row.saved=saved;
      delete this.entries['liked:'];this.write();return {saved};
    }
    if(!/^spotify:playlist:[a-zA-Z0-9]{22}$/.test(playlist||''))throw new Error('InvalidPlaylist');
    const before=await this.query('playlist',playlist,{force:true});const previousUids=new Set(before.rows.map(row=>row.uid).filter(Boolean)),expected=this.track(uri);
    const isAdded=row=>row.uid&&!previousUids.has(row.uid)&&(row.uri===uri||expected&&row.name===expected.name&&row.artist===expected.artist);
    let mutationResult;
    if(kind==='addPlaylist')mutationResult=await operation(this.auth,kind,{playlistItemUris:[uri],playlistUri:playlist,newPosition:{moveType:'BOTTOM_OF_PLAYLIST',fromUid:null},interactionId:null},this.session);
    else if(kind==='removePlaylist'){
      if(typeof uid!=='string'||!uid||uid.length>200)throw new Error('PlaylistItemMissing');
      mutationResult=await operation(this.auth,kind,{playlistUri:playlist,uids:[uid]},this.session);
    }else throw new Error('UnsupportedMutation');
    const summarize=(value,depth=0,key='')=>{if(depth>5)return '<object>';if(value===null||typeof value==='boolean'||typeof value==='number')return value;if(typeof value==='string')return /typename|error|code|status|message/i.test(key)?value.slice(0,150):'<text>';if(Array.isArray(value))return value.slice(0,5).map(item=>summarize(item,depth+1));return Object.fromEntries(Object.entries(value||{}).map(([name,item])=>[name,summarize(item,depth+1,name)]));};
    try{fs.writeFileSync(path.join(this.directory,'playlist-mutation-diagnostics.json'),JSON.stringify({kind,beforeCount:before.rows.length,result:summarize(mutationResult)}));}catch{}
    const rawResult=JSON.stringify(mutationResult||{});if(/NOT_AUTHORIZED|PERMISSION_DENIED|NotAuthorized|PermissionDenied|Forbidden|"NotFound"/.test(rawResult))throw new Error('PlaylistPermissionDenied');
    delete this.entries['playlist:'+playlist];this.write();
    let updated=await this.query('playlist',playlist,{force:true});
    for(let retry=0;retry<3;retry++){
      const pending=kind==='removePlaylist'?updated.rows.some(row=>row.uid===uid):!updated.rows.some(isAdded);if(!pending)break;
      await new Promise(resolve=>setTimeout(resolve,500));updated=await this.query('playlist',playlist,{force:true});
    }
    if(kind==='removePlaylist'){if(updated.rows.some(row=>row.uid===uid))throw new Error('PlaylistChangeNotConfirmed');return {changed:true};}
    const added=updated.rows.find(isAdded);if(!added)throw new Error('PlaylistChangeNotConfirmed');
    return {changed:true,addedUid:added.uid};
  }
}
async function catalog(auth,kind,argument,session){return rowsFrom(await operation(auth,kind,variables(kind,argument),session),kind);}
module.exports = {catalog,CatalogStore,rowsFrom};

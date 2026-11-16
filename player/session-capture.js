(() => {
  if (location.hostname !== 'open.spotify.com') return;
  const own = window.yspotifyProbeAuth = { bearer: '', client: '', version: '', registration: null };
  function capture(url, headers, body) {
    try {
      const target = new URL(url, location.href);
      if (!target.hostname.endsWith('.spotify.com')) return;
      const h = new Headers(headers || {});
      if (h.get('authorization')?.startsWith('Bearer ')) own.bearer = h.get('authorization');
      if (h.get('client-token')) own.client = h.get('client-token');
      if (h.get('spotify-app-version')) own.version = h.get('spotify-app-version');
      if (target.pathname === '/track-playback/v1/devices' && typeof body === 'string') {
        try { own.registration = JSON.parse(body); } catch {}
      }
    } catch {}
  }
  const actualFetch = window.fetch;
  window.fetch = function(input, options) {
    capture(input instanceof Request ? input.url : String(input), options?.headers || (input instanceof Request ? input.headers : {}), options?.body);
    const promise=actualFetch.apply(this, arguments);
    try{
      const target=new URL(input instanceof Request?input.url:String(input),location.href);
      if(['open.spotify.com','webapi-authorization.spotify.com'].includes(target.hostname)&&/token|access_token/.test(target.pathname))promise.then(async response=>{const data=await response.clone().json();const expiry=Number(data.accessTokenExpirationTimestampMs||data.expiresAt||data.expirationTimestampMs);const serverNow=Date.parse(response.headers.get('date')||'');const ttl=expiry-serverNow;if(ttl>0&&ttl<86400000)own.expiresAt=Date.now()+ttl;else if(expiry>Date.now())own.expiresAt=expiry;}).catch(()=>{});
    }catch{}
    return promise;
  };
  const actualOpen = XMLHttpRequest.prototype.open;
  const actualHeader = XMLHttpRequest.prototype.setRequestHeader;
  const actualSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function(method, url) { this.yspotifyRequest = { url, headers: {} }; return actualOpen.apply(this, arguments); };
  XMLHttpRequest.prototype.setRequestHeader = function(name, value) { if (this.yspotifyRequest) this.yspotifyRequest.headers[name] = value; return actualHeader.apply(this, arguments); };
  XMLHttpRequest.prototype.send = function(body) { const r = this.yspotifyRequest; if(r) capture(r.url, r.headers, body); return actualSend.apply(this, arguments); };
})();

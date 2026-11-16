(() => {
  if (location.hostname !== 'open.spotify.com') return;
  const text = el => el ? (el.innerText || el.textContent || '').trim() : '';
  const uri = a => {
    if (!a) return '';
    const m = new URL(a.href, location.href).pathname.match(/^\/(track|playlist|episode)\/([a-zA-Z0-9]+)/);
    return m ? `spotify:${m[1]}:${m[2]}` : '';
  };
  const trackRows = () => [...document.querySelectorAll('[data-testid="tracklist-row"]')];
  const button = id => document.querySelector(`[data-testid="${id}"]`);
  const enabled = el => !!el && !el.disabled && el.getAttribute('aria-disabled') !== 'true';
  const secs = s => { const n = s.split(':').map(Number); return n.length === 2 ? n[0] * 60 + n[1] : 0; };
  window.yspotifyBridge = {
    read(playlists) {
      let rows;
      if (playlists) {
        const unique = new Map();
        for (const a of document.querySelectorAll('a[href*="/playlist/"]')) {
          const u = uri(a), name = text(a) || a.getAttribute('aria-label');
          if (u && name && !unique.has(u)) unique.set(u, {uri:u, name, artist:'Плейлист', album:'', duration:''});
        }
        rows = [...unique.values()].slice(0, 100);
      } else rows = trackRows().slice(0, 100).map(row => {
        const a = row.querySelector('a[href*="/track/"]'), artists = [...row.querySelectorAll('a[href*="/artist/"]')];
        const bits = text(row).split('\n').map(s => s.trim()).filter(Boolean);
        return {uri:uri(a), name:text(a), artist:[...new Set(artists.map(text))].join(', '), album:text(row.querySelector('a[href*="/album/"]')), duration:bits.find(s => /^\d+:\d{2}$/.test(s)) || ''};
      }).filter(row => row.uri && row.name);
      const widget = document.querySelector('[data-testid="now-playing-widget"]') || document.querySelector('[data-testid="now-playing-bar"]');
      const position = text(button('playback-position')), duration = text(button('playback-duration'));
      const play = button('control-button-playpause');
      return {
        origin: location.hostname,
        path: location.pathname,
        eme: window.yspotifyEme || '',
        loggedIn: !!document.querySelector('[data-testid="user-widget-link"], [data-testid="user-widget-avatar"]'),
        rows,
        player: {
          name: widget ? text(widget.querySelector('a[href*="/track/"], a[href*="/episode/"]')) : '',
          artist: widget ? [...new Set([...widget.querySelectorAll('a[href*="/artist/"]')].map(text))].join(', ') : '',
          playing: !!play && /pause|пауза|приостанов/i.test(play.getAttribute('aria-label') || ''),
          canPlay: enabled(play), canPrevious: enabled(button('control-button-skip-back')), canNext: enabled(button('control-button-skip-forward')),
          fraction: secs(duration) ? secs(position) / secs(duration) : 0,
          time: `${position || '0:00'} / ${duration || '0:00'}`
        }
      };
    },
    play(target) {
      const row = trackRows().find(row => uri(row.querySelector('a[href*="/track/"]')) === target);
      if (!row) return false;
      row.dispatchEvent(new MouseEvent('dblclick', {bubbles:true, cancelable:true, view:window}));
      return true;
    }
  };
})();

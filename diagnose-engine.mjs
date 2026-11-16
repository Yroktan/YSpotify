import fs from 'node:fs';
const port = Number(process.argv[2]);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw Error('Supply the local debug port');
const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
const page = pages.find(p => p.type === 'page' && p.url.startsWith('https://open.spotify.com/'));
if (!page) throw Error('Spotify page is not open');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
let seq = 0;
const pending = new Map();
ws.onmessage = event => { const message = JSON.parse(event.data); const handler = pending.get(message.id); if(handler) { pending.delete(message.id); handler(message); } };
async function call(method, params = {}) {
 const id = ++seq;
 return await new Promise((resolve,reject) => {const timer=setTimeout(()=>{pending.delete(id);reject(Error('CDP timeout'));},8000);pending.set(id,result=>{clearTimeout(timer);resolve(result);});ws.send(JSON.stringify({id,method,params}));});
}
try {
 const expression = `JSON.stringify({bridge:typeof window.yspotifyBridge,rows:document.querySelectorAll('[data-testid="tracklist-row"]').length,loggedIn:!!document.querySelector('[data-testid="user-widget-link"], [data-testid="user-widget-avatar"]'),ready:document.readyState,origin:location.hostname})`;
 const result=await call('Runtime.evaluate',{expression,returnByValue:true});
 console.log(result.result?.result?.value ?? result.error);
 if(process.argv.includes('--install')){
  const source=fs.readFileSync(new URL('./bridge.js',import.meta.url),'utf8');
  await call('Page.addScriptToEvaluateOnNewDocument',{source});
  const injection=await call('Runtime.evaluate',{expression:source,returnByValue:true});
  console.log(JSON.stringify({injectionError:injection.result?.exceptionDetails?.text??null}));
 }
} finally { ws.close(); }

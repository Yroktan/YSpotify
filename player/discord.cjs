'use strict';
const net=require('node:net');
const crypto=require('node:crypto');
function clipped(value){let result='';for(const c of String(value||'')){if(Buffer.byteLength(result+c,'utf8')>120)break;result+=c;}return result;}
class DiscordPresence {
  constructor(clientId,options={}){
    this.clientId=clientId;this.enabled=options.enabled!==false;this.paths=options.paths||Array.from({length:10},(_,n)=>'\\\\.\\pipe\\discord-ipc-'+n);
    this.status=this.enabled?'disconnected':'disabled';this.buffer=Buffer.alloc(0);this.retryAt=0;this.lastKey=null;this.lastActivityType=null;
  }
  packet(opcode,payload){if(!this.socket||this.socket.destroyed)return;const body=Buffer.from(JSON.stringify(payload),'utf8'),header=Buffer.alloc(8);header.writeUInt32LE(opcode);header.writeUInt32LE(body.length,4);this.socket.write(Buffer.concat([header,body]));}
  async connect(){
    if(!this.enabled||!/^\d{17,22}$/.test(this.clientId||'')||this.ready||Date.now()<this.retryAt)return;
    if(this.connecting)return this.connecting;
    this.connecting=(async()=>{
      for(const ipc of this.paths){
        if(!this.enabled)break;
        const connected=await new Promise(resolve=>{
          const socket=net.createConnection(ipc);let finished=false;
          const done=ok=>{if(finished)return;finished=true;clearTimeout(timer);resolve(ok);};
          const timer=setTimeout(()=>{socket.destroy();done(false);},1200);
          this.socket=socket;this.buffer=Buffer.alloc(0);
          socket.on('connect',()=>this.packet(0,{v:1,client_id:this.clientId}));
          socket.on('data',part=>{
            this.buffer=Buffer.concat([this.buffer,part]);
            if(this.buffer.length>1048576){socket.destroy();done(false);return;}
            while(this.buffer.length>=8){
              const opcode=this.buffer.readUInt32LE(0),size=this.buffer.readUInt32LE(4);
              if(size>1048576){socket.destroy();done(false);return;}if(this.buffer.length<8+size)break;
              const bytes=this.buffer.subarray(8,8+size);this.buffer=this.buffer.subarray(8+size);
              let message;try{message=JSON.parse(bytes.toString('utf8'));}catch{continue;}
              if(opcode===3){this.packet(4,message);continue;}
              if(opcode===2){socket.destroy();done(false);continue;}
              if(message.evt==='READY'){this.ready=true;this.status='connected';this.lastKey=null;done(true);}
              else if(message.evt==='ERROR'){this.status='error';this.lastErrorCode=message.data?.code||null;}
              else if(message.cmd==='SET_ACTIVITY'){this.lastActivityType=message.data?.type??message.data?.activity?.type??null;this.acknowledged=true;this.activityKeys=Object.keys(message.data||{});}
            }
          });
          socket.on('error',()=>{this.ready=false;done(false);});
          socket.on('close',()=>{this.ready=false;if(this.status!=='disabled')this.status='disconnected';done(false);});
        });
        if(connected)return;
      }
      this.socket=null;this.retryAt=Date.now()+30000;
    })();
    try{await this.connecting;}finally{this.connecting=null;}
  }
  async update(snapshot){
    if(!this.enabled)return;
    await this.connect();if(!this.ready)return;
    const track=snapshot?.track,audio=snapshot?.audio;
    const playing=track?.uri?.startsWith('spotify:track:')&&audio&&!audio.paused&&audio.ready>=3&&audio.license===200;
    const key=playing?track.uri+':'+Math.floor((Date.now()/1000-audio.time)/5):'paused';
    if(key===this.lastKey)return;this.lastKey=key;
    let activity=null;
    if(playing){
      const start=Math.floor(Date.now()/1000-audio.time);
      activity={type:2,details:clipped(track.name),state:clipped(track.artist),timestamps:{start,end:start+Math.ceil(audio.duration||0)},buttons:[{label:'Открыть в Spotify',url:'https://open.spotify.com/track/'+track.uri.split(':')[2]}],instance:false};
    }
    this.packet(1,{cmd:'SET_ACTIVITY',args:{pid:process.pid,activity},nonce:crypto.randomUUID()});
  }
  configure(enabled){this.enabled=!!enabled;if(!enabled){this.packet(1,{cmd:'SET_ACTIVITY',args:{pid:process.pid,activity:null},nonce:crypto.randomUUID()});this.socket?.destroy();this.socket=null;this.ready=false;this.status='disabled';}else{this.status='disconnected';this.retryAt=0;this.connect().catch(()=>{});}}
  close(){this.configure(false);}
  snapshot(){return {enabled:this.enabled,status:this.status,activityType:this.lastActivityType,acknowledged:!!this.acknowledged,activityKeys:this.activityKeys||[],errorCode:this.lastErrorCode||null};}
}
module.exports={DiscordPresence};

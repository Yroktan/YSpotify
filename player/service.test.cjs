'use strict';
const test=require('node:test');const assert=require('node:assert/strict');
const {SpotifyService,resolvePlaybackEndpoint}=require('./service.cjs');
test('playback resolver accepts official hosts and rejects untrusted destinations',async()=>{
  const response=servers=>async()=>({ok:true,json:async()=>({spclient:servers})});
  assert.equal(await resolvePlaybackEndpoint(response(['gew4-spclient.spotify.com:443'])),'https://gew4-spclient.spotify.com');
  assert.equal(await resolvePlaybackEndpoint(response(['gew4-spclient.spotify.com.attacker.test:443','attacker.test:443','user@gew4-spclient.spotify.com:443'])),'https://spclient.wg.spotify.com');
  assert.equal(await resolvePlaybackEndpoint(async()=>{throw new Error('offline');}),'https://spclient.wg.spotify.com');
});
function fixture(uri,license){
  const service=new SpotifyService({bearer:'Bearer test',client:'test'},{},null);
  service.machine={state_machine_id:'fixture',tracks:[{metadata:{uri}},{metadata:{uri:'spotify:track:next'}}],states:[{state_id:'first',track:0,transitions:{advance:{state_index:1}}},{state_id:'second',track:1}]};
  service.reference={state_index:0};service.audio=async()=>({ended:true,paused:true,time:10,duration:10,license,error:null});
  let reported=0,loaded=0;service.request=async()=>{reported++;return null;};service.loadActive=async()=>{loaded++;};
  return {service,counts:()=>({reported,loaded})};
}
test('a completed unencrypted advertisement reports its state and advances',async()=>{
  const {service,counts}=fixture('spotify:ad:fixture',null);await service.tick();
  assert.deepEqual(counts(),{reported:1,loaded:1});assert.equal(service.active().state.state_id,'second');
});
test('unlicensed music is never reported as completed',async()=>{
  const {service,counts}=fixture('spotify:track:fixture',null);await service.tick();
  assert.deepEqual(counts(),{reported:0,loaded:0});assert.equal(service.active().state.state_id,'first');
});
test('licensed music reports completion and advances once',async()=>{
  const {service,counts}=fixture('spotify:track:fixture',200);await service.tick();
  assert.deepEqual(counts(),{reported:1,loaded:1});assert.equal(service.state.completedTracks,1);
});

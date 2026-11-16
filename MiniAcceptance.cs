using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace YSpotify {
    static class MiniAcceptance {
        static bool Passed(Dictionary<string,object> report){return report!=null&&report.ContainsKey("passed")&&Convert.ToBoolean(report["passed"]);}
        [STAThread]static void Main(string[] args){
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var window=new MainWindow(false,true,true)){
                window.Text="YSpotify · автоматическая проверка";window.ShowInTaskbar=false;window.Opacity=0;
                window.Shown+=async delegate{
                    try{if(args.Contains("--latency-only")){await window.RunMiniLatencyChecks();}else{await window.RunMiniQuickChecks();if(!args.Contains("--quick-only")){await window.RunMiniEndurance();var endurance=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(Program.Data,"endurance-check.json"))) as Dictionary<string,object>;if(!Passed(endurance))Environment.ExitCode=1;}var checks=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(Program.Data,"quick-check.json"))) as Dictionary<string,object>;if(!Passed(checks))Environment.ExitCode=1;}Console.WriteLine("MINI_ACCEPTANCE=FINISHED: see local validation reports");}
                    catch(Exception ex){Console.WriteLine("MINI_ACCEPTANCE=FAIL "+ex.Message);Environment.ExitCode=1;}
                    window.Close();
                };
                Application.Run(window);
            }
        }
    }
    partial class MainWindow {
        internal async Task RunMiniLatencyChecks(){
            var records=new List<object>();var latencies=new List<double>();bool passed=false;long peak=0;
            var sampler=new System.Windows.Forms.Timer{Interval=250};
            sampler.Tick+=delegate{long bytes=PrivateWorkingSet(Process.GetCurrentProcess().Id);foreach(int id in OwnedProcessIds())try{bytes+=PrivateWorkingSet(id);}catch(ArgumentException){}peak=Math.Max(peak,bytes);};sampler.Start();
            try{
                if(!await EnsureEngine())throw new Exception("Engine failed: "+status.Text);
                var liked=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","liked"}}) as Dictionary<string,object>;
                var rows=(liked["rows"] as object[]).Cast<Dictionary<string,object>>().Take(8).ToArray();if(rows.Length<4)throw new Exception("Need four songs");
                var queue=rows.Select(row=>new{uri=Value(row,"uri"),uid=""}).ToArray();
                for(int test=0;test<5;test++){
                    string uri=Value(rows[test<4?test:0],"uri");long pressed=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await MiniCall(new Dictionary<string,object>{{"action","play"},{"uri",uri},{"pressedAt",pressed},{"queue",queue},{"context","spotify:collection:tracks"}});
                    Dictionary<string,object> result=null,last=null;
                    for(int n=0;n<240;n++){
                        var state=await MiniCall("state") as Dictionary<string,object>;last=state;var sound=state["audio"] as Dictionary<string,object>;var track=state["track"] as Dictionary<string,object>;
                        bool selected=Value(track,"uri")==uri||Value(track,"requestedUri")==uri||Value(track,"linkedFromUri")==uri;
                        if(selected&&sound!=null&&Value(sound,"phase")=="playing"&&Convert.ToDouble(sound["time"])>0.15){result=state;if(sound["license"]!=null&&Convert.ToInt32(sound["license"])==200)break;}
                        if(sound!=null&&Value(sound,"error").Length>0)throw new Exception("Audio error: "+Value(sound,"error"));
                        await Task.Delay(50);
                    }
                    if(result==null){records.Add(new{test=test,failedState=last,inspect=await MiniCall("inspectPlayback")});throw new Exception("No sound before timeout");}
                    var audio=result["audio"] as Dictionary<string,object>;
                    records.Add(new{test=test,uri=uri,timing=result.ContainsKey("timing")?result["timing"]:null,audio=audio});
                    if(audio["license"]==null||Convert.ToInt32(audio["license"])!=200)throw new Exception("Music license was not confirmed");
                    latencies.Add(Convert.ToDouble(audio["startedAfterMs"]));
                    if(test==3){
                        bool continued=false;
                        for(int n=0;n<80;n++){
                            var tail=await MiniCall("state") as Dictionary<string,object>;var sound=tail["audio"] as Dictionary<string,object>;
                            if(sound!=null&&Value(sound,"error").Length>0)throw new Exception("Cached prefix tail: "+Value(sound,"error"));
                            if(sound!=null&&Convert.ToDouble(sound["time"])>12&&!Flag(sound,"paused")){continued=true;records.Add(new{test="past-cached-prefix",pass=true,audio=sound});break;}
                            await Task.Delay(250);
                        }
                        if(!continued)throw new Exception("Playback stalled after the initial cached bytes");
                    }
                    await Task.Delay(400);
                }
                var pause=Stopwatch.StartNew();await MiniCall("toggle");pause.Stop();var paused=await MiniCall("state") as Dictionary<string,object>;
                if(!Flag(paused["audio"] as Dictionary<string,object>,"paused"))throw new Exception("Pause failed");
                var resume=Stopwatch.StartNew();await MiniCall("toggle");resume.Stop();
                records.Add(new{test="pause-resume",pauseMs=pause.ElapsedMilliseconds,resumeMs=resume.ElapsedMilliseconds});passed=true;
            }finally{
                sampler.Stop();sampler.Dispose();
                Program.StoreLocalFile("latency-check.json",System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{passed=passed,at=DateTime.UtcNow.ToString("o"),startMetric="native request timestamp to audio playing event; license and progress checked separately",targetMs=1000,targetMet=passed&&latencies.Count==5&&latencies.All(value=>value<=1000),maxStartMs=latencies.Count>0?latencies.Max():0,peakPrivateMiB=Math.Round(peak/1048576.0,1),records=records})));
            }
        }
        internal async Task RunMiniQuickChecks(){
            var records=new List<object>();string reportPath=Path.Combine(Program.Data,"quick-check.json"),addedForTest=null,cleanupPlaylist=null,cleanupUid=null,cleanupTrack=null;
            long peak=0;var sampler=new System.Windows.Forms.Timer{Interval=200};
            sampler.Tick+=delegate{long bytes=PrivateWorkingSet(Process.GetCurrentProcess().Id);foreach(int id in OwnedProcessIds())try{bytes+=PrivateWorkingSet(id);}catch(ArgumentException){}peak=Math.Max(peak,bytes);};sampler.Start();
            try{
                Program.StoreLocalFile("quick-check.json",System.Text.Encoding.UTF8.GetBytes("{\"running\":true}"));
                SetStatus("Проверяю сохранённый вход…");
                if(!await EnsureEngine())throw new Exception("Engine failed: "+status.Text);
                var initial=await MiniCall("state") as Dictionary<string,object>;
                if(!Flag(initial,"loggedIn"))throw new Exception("Login not restored");
                records.Add(new{test="restored-login",pass=true});
                SetStatus("Проверяю обновление токена…");
                var renewed=await MiniCall("refreshSession") as Dictionary<string,object>;
                if(!Flag(renewed,"restored"))throw new Exception("Refresh did not restore session");
                SaveMiniSession(await MiniCall("session"));records.Add(new{test="forced-token-refresh",pass=true,expiresInSeconds=renewed["expiresInSeconds"]});
                var liked=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","liked"},{"force",true}}) as Dictionary<string,object>;
                int count=(liked["rows"] as object[]).Length;if(count==0)throw new Exception("Liked songs empty");records.Add(new{test="liked-songs",pass=true,count=count});
                var likedRow=(liked["rows"] as object[])[0] as Dictionary<string,object>;
                var membership=await MiniCall(new Dictionary<string,object>{{"action","saved"},{"uri",Value(likedRow,"uri")}}) as Dictionary<string,object>;
                if(!Flag(membership,"saved"))throw new Exception("Saved membership lookup failed");
                await MiniCall(new Dictionary<string,object>{{"action","mutate"},{"kind","addFavorite"},{"uri",Value(likedRow,"uri")}});records.Add(new{test="favorite-save-idempotent",pass=true});
                var results=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","search"},{"argument","Daft Punk"}}) as Dictionary<string,object>;
                var rows=(results["rows"] as object[]).Cast<Dictionary<string,object>>().ToArray();if(rows.Length<3)throw new Exception("Need three search results");
                foreach(var candidate in rows.Take(5)){
                    string candidateUri=Value(candidate,"uri");var member=await MiniCall(new Dictionary<string,object>{{"action","saved"},{"uri",candidateUri}}) as Dictionary<string,object>;
                    if(Flag(member,"saved"))continue;
                    await MiniCall(new Dictionary<string,object>{{"action","mutate"},{"kind","addFavorite"},{"uri",candidateUri}});addedForTest=candidateUri;
                    await MiniCall(new Dictionary<string,object>{{"action","mutate"},{"kind","removeFavorite"},{"uri",candidateUri}});addedForTest=null;records.Add(new{test="favorite-add-remove-roundtrip",pass=true});break;
                }
                SetStatus("Проверяю быстрые нажатия и запуск звука…");
                var requests=new List<Task<object>>();
                var queue=rows.Take(8).Select(row=>new{uri=Value(row,"uri"),uid=""}).ToArray();
                foreach(var row in new[]{rows[0],rows[0],rows[1],rows[2]})requests.Add(MiniCall(new Dictionary<string,object>{{"action","play"},{"uri",Value(row,"uri")},{"pressedAt",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{"queue",queue},{"context","spotify:search:Daft%20Punk"}}));
                await Task.WhenAll(requests);
                bool heard=false,licensed=false;double latency=0;
                for(int n=0;n<30;n++){
                    var state=await MiniCall("state") as Dictionary<string,object>;var audio=state["audio"] as Dictionary<string,object>;var track=state["track"] as Dictionary<string,object>;
                    long bytes=PrivateWorkingSet(Process.GetCurrentProcess().Id);foreach(int id in OwnedProcessIds())try{bytes+=PrivateWorkingSet(id);}catch(ArgumentException){}peak=Math.Max(peak,bytes);
                    if(audio!=null&&!Flag(audio,"paused")&&Convert.ToDouble(audio["time"])>0.1){
                        if(Value(track,"uri")!=Value(rows[2],"uri"))throw new Exception("An obsolete click started playing");
                        heard=true;latency=Convert.ToDouble(audio["startedAfterMs"]);
                        if(audio["license"]!=null&&Convert.ToInt32(audio["license"])==200){licensed=true;break;}
                    }
                    await Task.Delay(300);
                }
                if(!heard||!licensed)throw new Exception("No licensed audio after last click");
                records.Add(new{test="last-click-wins",pass=true,startedAfterMs=latency});
                await MiniCall(new Dictionary<string,object>{{"action","volume"},{"value",0.42}});
                var volumeState=await MiniCall("state") as Dictionary<string,object>;
                if(Math.Abs(Convert.ToDouble(volumeState["volume"])-0.42)>0.001||Math.Abs(Convert.ToDouble((volumeState["audio"] as Dictionary<string,object>)["volume"])-0.42)>0.001)throw new Exception("Volume failed");
                await MiniCall(new Dictionary<string,object>{{"action","volume"},{"value",settings.Volume}});records.Add(new{test="volume",pass=true});
                await MiniCall("next");await Task.Delay(1500);
                var skipped=await MiniCall("state") as Dictionary<string,object>;var skippedTrack=skipped["track"] as Dictionary<string,object>;
                if(Value(skippedTrack,"uri")!=Value(rows[3],"uri")&&Value(skippedTrack,"requestedUri")!=Value(rows[3],"uri")&&Value(skippedTrack,"linkedFromUri")!=Value(rows[3],"uri")){records.Add(new{test="next-diagnostics",actualIndex=Array.FindIndex(rows,row=>Value(row,"uri")==Value(skippedTrack,"uri")),info=await MiniCall("inspectPlayback")});throw new Exception("Next did not follow the selected list");}records.Add(new{test="next-in-order",pass=true});
                for(int n=0;n<50;n++){var waiting=await MiniCall("state") as Dictionary<string,object>;var sound=waiting["audio"] as Dictionary<string,object>;if(sound!=null&&Convert.ToDouble(sound["time"])>3.2)break;await Task.Delay(200);}
                await MiniCall("previous");await Task.Delay(1000);
                var restarted=await MiniCall("state") as Dictionary<string,object>;var restartedAudio=restarted["audio"] as Dictionary<string,object>;
                var restartedTrack=restarted["track"] as Dictionary<string,object>;
                if((Value(restartedTrack,"uri")!=Value(rows[3],"uri")&&Value(restartedTrack,"requestedUri")!=Value(rows[3],"uri"))||Convert.ToDouble(restartedAudio["time"])>3)throw new Exception("Previous did not restart current track");records.Add(new{test="previous-restart",pass=true});
                await MiniCall("previous");var previousState=await MiniCall("state") as Dictionary<string,object>;var previousTrack=previousState["track"] as Dictionary<string,object>;if(Value(previousTrack,"uri")!=Value(rows[2],"uri")&&Value(previousTrack,"requestedUri")!=Value(rows[2],"uri"))throw new Exception("Previous did not select the previous song");records.Add(new{test="previous-track",pass=true});
                for(int n=0;n<40;n++){var audible=await MiniCall("state") as Dictionary<string,object>;var sound=audible["audio"] as Dictionary<string,object>;if(sound!=null&&Convert.ToInt32(sound["ready"])>=3&&!Flag(sound,"paused"))break;await Task.Delay(200);}
                await MiniCall("toggle");var paused=await MiniCall("state") as Dictionary<string,object>;if(!Flag(paused["audio"] as Dictionary<string,object>,"paused"))throw new Exception("Pause failed");await MiniCall("toggle");var resumed=await MiniCall("state") as Dictionary<string,object>;if(Flag(resumed["audio"] as Dictionary<string,object>,"paused"))throw new Exception("Resume failed");records.Add(new{test="pause-resume",pass=true});
                await MiniCall(new Dictionary<string,object>{{"action","mode"},{"kind","shuffle"}});var mixed=await MiniCall("state") as Dictionary<string,object>;if(!Flag(mixed,"shuffle"))throw new Exception("Shuffle failed");await MiniCall(new Dictionary<string,object>{{"action","mode"},{"kind","shuffle"}});
                foreach(string mode in new[]{"context","track","off"}){await MiniCall(new Dictionary<string,object>{{"action","mode"},{"kind","repeat"}});var repeated=await MiniCall("state") as Dictionary<string,object>;if(Value(repeated,"repeatMode")!=mode)throw new Exception("Repeat failed");}records.Add(new{test="shuffle-repeat-modes",pass=true});
                var library=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","library"},{"force",true}}) as Dictionary<string,object>;
                var allLists=(library["rows"] as object[]).Cast<Dictionary<string,object>>().ToArray();var lists=allLists.OrderByDescending(row=>Flag(row,"canEdit")).ToArray();
                if(lists.Length>0){
                    string playlistUri=Value(lists[0],"uri");var playlist=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","playlist"},{"argument",playlistUri},{"force",true}}) as Dictionary<string,object>;var playlistRows=(playlist["rows"] as object[]).Cast<Dictionary<string,object>>().ToArray();records.Add(new{test="playlist-catalog",pass=true,count=playlistRows.Length,withUid=playlistRows.Count(row=>Value(row,"uid").Length>0)});
                    var candidate=rows.FirstOrDefault(row=>!playlistRows.Any(existing=>Value(existing,"uri")==Value(row,"uri")||(Value(existing,"name")==Value(row,"name")&&Value(existing,"artist")==Value(row,"artist"))));
                    if(candidate!=null){
                        string trackUri=Value(candidate,"uri");var added=await MiniCall(new Dictionary<string,object>{{"action","mutate"},{"kind","addPlaylist"},{"uri",trackUri},{"playlist",playlistUri}}) as Dictionary<string,object>;
                        cleanupPlaylist=playlistUri;cleanupTrack=trackUri;cleanupUid=Value(added,"addedUid");if(string.IsNullOrEmpty(cleanupUid))throw new Exception("Added playlist UID missing");
                        await MiniCall(new Dictionary<string,object>{{"action","mutate"},{"kind","removePlaylist"},{"uri",trackUri},{"playlist",playlistUri},{"uid",cleanupUid}});cleanupUid=null;
                        var restoredList=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","playlist"},{"argument",playlistUri},{"force",true}}) as Dictionary<string,object>;if((restoredList["rows"] as object[]).Length!=playlistRows.Length)throw new Exception("Playlist not restored after test");records.Add(new{test="playlist-add-remove-roundtrip",pass=true});
                    }
                }
                await MiniRows("liked",null,false);welcome.SetHeading("Любимое");
                await Task.Delay(1500);await ReadMiniState();
                records.Add(new{test="memory",peakPrivateMiB=peak/1048576,pass=peak<=300L*1048576});
                if(peak>300L*1048576)throw new Exception("300 MiB memory budget exceeded");
                Program.StoreLocalFile("quick-check.json",System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{passed=true,records=records})));
                SetStatus("Проверка пройдена · вход, избранное и переключение работают");
            }catch(Exception ex){
                Program.StoreLocalFile("quick-check.json",System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{passed=false,error=ex.Message,peakPrivateMiB=peak/1048576,records=records})));
                SetStatus("Проверка: "+MiniMessage(ex.Message));
            }finally{sampler.Stop();sampler.Dispose();}
            if(addedForTest!=null)try{await MiniCall(new Dictionary<string,object>{{"action","mutate"},{"kind","removeFavorite"},{"uri",addedForTest}});}catch(Exception error){Program.StoreLocalFile("library-test-cleanup.json",System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{uri=addedForTest,error=error.Message})));}
            if(cleanupUid!=null)try{await MiniCall(new Dictionary<string,object>{{"action","mutate"},{"kind","removePlaylist"},{"uri",cleanupTrack},{"playlist",cleanupPlaylist},{"uid",cleanupUid}});}catch(Exception error){Program.StoreLocalFile("playlist-test-cleanup.json",System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{uri=cleanupTrack,playlist=cleanupPlaylist,uid=cleanupUid,error=error.Message})));}
            await ReadMiniState();
        }
        internal async Task RunMiniEndurance(){
            const string target="spotify:track:6f3Slt0GbA2bPZlz0aIFXN";
            long peak=0,commitPeak=0;bool passed=false;string failure=null;double maximumTime=0;var samples=new List<object>();
            try{
                Program.StoreLocalFile("endurance-check.json",System.Text.Encoding.UTF8.GetBytes("{\"running\":true}"));
                if(!await EnsureEngine())throw new Exception("Engine unavailable");
                var liked=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","liked"}}) as Dictionary<string,object>;var next=((liked["rows"] as object[])[0]) as Dictionary<string,object>;string nextUri=Value(next,"uri");
                await MiniCall(new Dictionary<string,object>{{"action","play"},{"uri",target},{"queue",new[]{new{uri=target,uid=""},new{uri=nextUri,uid=""}}},{"context",target},{"pressedAt",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}});
                for(int n=0;n<225;n++){
                    var state=await MiniCall("state") as Dictionary<string,object>;var audio=state["audio"] as Dictionary<string,object>;var track=state["track"] as Dictionary<string,object>;
                    long bytes=PrivateWorkingSet(Process.GetCurrentProcess().Id),commit=Process.GetCurrentProcess().PrivateMemorySize64;foreach(int id in OwnedProcessIds())try{bytes+=PrivateWorkingSet(id);using(var process=Process.GetProcessById(id))commit+=process.PrivateMemorySize64;}catch(ArgumentException){}peak=Math.Max(peak,bytes);commitPeak=Math.Max(commitPeak,commit);
                    if(bytes>300L*1048576)throw new Exception("300 MiB budget exceeded");
                    if(!string.IsNullOrEmpty(Value(state,"error")))throw new Exception(Value(state,"error"));
                    if(audio!=null){
                        double position=Convert.ToDouble(audio["time"]);if(Value(track,"uri")==target)maximumTime=Math.Max(maximumTime,position);
                        if(n%10==0)samples.Add(new{second=n,privateMiB=bytes/1048576,commitMiB=commit/1048576,position=position,license=audio["license"],phase=Value(audio,"phase")});
                        if(Value(state,"lastCompletedUri")==target&&Value(track,"uri")==nextUri&&!Flag(audio,"paused")&&position>0.1&&audio["license"]!=null&&Convert.ToInt32(audio["license"])==200){passed=true;break;}
                        if(Value(audio,"phase").EndsWith("error")||Value(audio,"phase")=="license-rejected")throw new Exception("Audio: "+Value(audio,"phase"));
                    }
                    await Task.Delay(1000);
                }
                if(!passed)throw new Exception("Full track with automatic next was not observed");
                SetStatus("Полный трек и автоматический переход проверены");
            }catch(Exception error){failure=error.Message;SetStatus("Проверка воспроизведения: "+MiniMessage(error.Message));}
            Program.StoreLocalFile("endurance-check.json",System.Text.Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{passed=passed,error=failure,maximumTime=maximumTime,peakPrivateMiB=peak/1048576,peakCommitMiB=commitPeak/1048576,samples=samples})));
        }
        public async Task RunMiniAcceptance(){
            var saved=LoadMiniSession();if(saved!=null)Console.WriteLine("SESSION_META="+new JavaScriptSerializer().Serialize(new{version=Value(saved,"version"),hasRegistration=saved.ContainsKey("registration")&&saved["registration"]!=null}));
            if(!await EnsureEngine())throw new Exception("Engine failed to start: "+status.Text);
            await ReadMiniState();
            if(!currentLoggedIn)throw new Exception("Requires an authenticated own Spotify session");
            Console.WriteLine("PASS: signed engine, own session, native window");
            foreach(string kind in new[]{"search","liked","library"}){
                var reply=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind",kind},{"argument",kind=="search"?"Daft Punk":null}}) as Dictionary<string,object>;
                var rows=reply["rows"] as object[];
                Console.WriteLine("CATALOG_"+kind.ToUpperInvariant()+"_COUNT="+rows.Length);
                if(kind=="search"&&rows.Length==0)throw new Exception("Search returned no results");
                bool hasDuration=rows.Cast<Dictionary<string,object>>().Any(r=>Value(r,"duration").Length>0);
                if(kind=="search"&&!hasDuration)throw new Exception("Search durations are missing");
                if(kind=="library"&&rows.Length>0){
                    var playlist=rows[0] as Dictionary<string,object>;
                    var tracks=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","playlist"},{"argument",Value(playlist,"uri")}}) as Dictionary<string,object>;
                    var playlistTracks=tracks["rows"] as object[];
                    Console.WriteLine("PLAYLIST_TRACK_COUNT="+playlistTracks.Length);
                    bool playlistHasDuration=playlistTracks.Cast<Dictionary<string,object>>().Any(r=>Value(r,"duration").Length>0);
                    if(playlistTracks.Length>0&&!playlistHasDuration)throw new Exception("Playlist durations are missing");
                }
            }
            await MiniRows("search","Daft Punk",false);
            const string target="spotify:track:6f3Slt0GbA2bPZlz0aIFXN";
            await MiniCall(new Dictionary<string,object>{{"action","play"},{"uri",target}});
            long peak=0,commitPeak=0;bool licensed=false,fullTrack=false;double lastPosition=0;
            for(int n=0;n<205;n++){
                await ReadMiniState();await UpdateStatus();
                var state=await MiniCall("state") as Dictionary<string,object>;
                if(!string.IsNullOrEmpty(Value(state,"error")))throw new Exception("Playback reporting: "+Value(state,"error"));
                var audio=state["audio"] as Dictionary<string,object>;
                if(audio==null){await Task.Delay(1000);continue;}
                string phase=Value(audio,"phase");
                if(phase=="license-rejected"||phase=="media-error"||phase=="stream-error")throw new Exception("Audio failed: "+phase);
                double position=Convert.ToDouble(audio["time"]),duration=Convert.ToDouble(audio["duration"]);
                licensed|=audio["license"]!=null&&Convert.ToInt32(audio["license"])==200;
                fullTrack|=duration>0&&position>=duration-1;
                long bytes=PrivateWorkingSet(Process.GetCurrentProcess().Id),commit=Process.GetCurrentProcess().PrivateMemorySize64;
                foreach(int id in OwnedProcessIds())try{bytes+=PrivateWorkingSet(id);using(var p=Process.GetProcessById(id))commit+=p.PrivateMemorySize64;}catch(ArgumentException){}
                peak=Math.Max(peak,bytes);commitPeak=Math.Max(commitPeak,commit);
                Console.WriteLine(new JavaScriptSerializer().Serialize(new{second=n,privateMiB=bytes/1048576,peakPrivateMiB=peak/1048576,commitMiB=commit/1048576,peakCommitMiB=commitPeak/1048576,position=position,duration=duration,phase=phase,license=audio["license"],paused=Flag(audio,"paused"),ended=Flag(audio,"ended")}));
                if(bytes>300L*1024*1024)throw new Exception("300 MiB budget exceeded");
                if(n==20){
                    await MiniCall("toggle");var stopped=await MiniCall("state") as Dictionary<string,object>;
                    if(!Flag(stopped["audio"] as Dictionary<string,object>,"paused"))throw new Exception("Pause failed");
                    await Task.Delay(1000);await MiniCall("toggle");Console.WriteLine("PASS: pause and resume");
                }
                if(n==45){await MiniRows("search","HOME",false);Console.WriteLine("PASS: native search while music continues");}
                if(fullTrack&&position<lastPosition-10){Console.WriteLine("PASS: full track and automatic next track");break;}
                if(fullTrack&&Flag(audio,"ended")){Console.WriteLine("PASS: full track ended");break;}
                lastPosition=position;await Task.Delay(1000);
            }
            if(!licensed||!fullTrack)throw new Exception("A complete licensed track was not observed");
            Console.WriteLine("NATIVE_PEAK_PRIVATE_MIB="+peak/1048576+" NATIVE_PEAK_COMMIT_MIB="+commitPeak/1048576);
            await StopBrowserAsync();Console.WriteLine("OWNED_AFTER_STOP="+OwnedProcessIds().Count);
        }
    }
}

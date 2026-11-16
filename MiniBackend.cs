using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace YSpotify {
    partial class MainWindow {
        readonly bool miniPlayer;
        NamedPipeServerStream miniPipe;
        StreamReader miniReader;
        StreamWriter miniWriter;
        long miniSerial,miniQueryVersion;
        bool readingMini;
        bool miniExternalLogin,miniForceSessionImport;
        string miniRowsKind="search",miniRowsArgument,miniCover="";
        CoverCache miniCovers;
        bool miniPlaylistEditable;
        DateTime miniNextRecovery=DateTime.MinValue;
        readonly ConcurrentDictionary<long,TaskCompletionSource<object>> miniPending=new ConcurrentDictionary<long,TaskCompletionSource<object>>();
        readonly SemaphoreSlim miniWriteLock=new SemaphoreSlim(1,1);
        Task miniReadTask;
        readonly JavaScriptSerializer miniJson=new JavaScriptSerializer();
        string MiniSessionPath {get{return Path.Combine(Program.Data,"player-session.bin");}}
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeClientProcessId(IntPtr pipe,out uint id);
        Dictionary<string,object> LoadMiniSession(){
            try {
                byte[] plain=ProtectedData.Unprotect(File.ReadAllBytes(MiniSessionPath),null,DataProtectionScope.CurrentUser);
                var data=miniJson.DeserializeObject(Encoding.UTF8.GetString(plain)) as Dictionary<string,object>;
                Array.Clear(plain,0,plain.Length);
                if(data==null||!data.ContainsKey("validUntil"))return null;
                if(Convert.ToDouble(data["validUntil"])<DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000&&!data.ContainsKey("cookies"))return null;
                return data;
            }catch{return null;}
        }
        void SaveMiniSession(object session){
            if(session==null)return;
            byte[] plain=Encoding.UTF8.GetBytes(miniJson.Serialize(session));
            try{Program.StoreLocalFile("player-session.bin",ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser));}
            finally{Array.Clear(plain,0,plain.Length);}
        }
        async Task<object> MiniBrowserValue(string expression){
            var reply=ProtocolResult(await Send("script.evaluate",new{expression=expression,target=new{context=firefoxContext},awaitPromise=false,userActivation=false}));
            if(Convert.ToString(reply["type"])!="success")return null;
            var value=reply["result"] as Dictionary<string,object>;
            return value!=null&&value.ContainsKey("value")?value["value"]:null;
        }
        async Task<Dictionary<string,object>> ImportOwnFirefoxSession(){
            if(!Directory.Exists(FirefoxProfile)||!settings.RememberLogin)return null;
            Dictionary<string,object> session=null;
            try {
                string executable=FindBrowser();
                SetStatus("Подтверждаю сохранённый вход в Spotify…");
                ConfigureFirefoxProfile(false);
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                browser=LaunchOwnedBrowser(executable,FirefoxArguments(false,"about:blank"),false);
                await Connect();
                string capture=File.ReadAllText(Path.Combine(Program.Root,"player","session-capture.js"));
                ProtocolResult(await Send("script.addPreloadScript",new{functionDeclaration="() => {"+capture+"}"}));
                await Send("Page.navigate",new{url="https://open.spotify.com/search"});
                for(int n=0;n<60&&!closing;n++){
                    string serialized=Convert.ToString(await MiniBrowserValue("(()=>{const a=window.yspotifyProbeAuth;if(!a?.bearer||!a.client||!document.querySelector('[data-testid=\"user-widget-link\"],[data-testid=\"user-widget-avatar\"]'))return '';let expiry=a.expiresAt;try{const s=JSON.parse(document.getElementById('session')?.textContent||'{}');expiry=expiry||s.accessTokenExpirationTimestampMs;}catch{}return JSON.stringify({...a,validUntil:expiry||Date.now()+7*60000})})()"));
                    if(!string.IsNullOrEmpty(serialized)){session=miniJson.DeserializeObject(serialized) as Dictionary<string,object>;break;}
                    await Task.Delay(500);
                }
                if(session!=null){
                    var retained=new List<object>();
                    foreach(string cookieName in new[]{"sp_dc","sp_key"}){
                        var response=ProtocolResult(await Send("storage.getCookies",new{filter=new{name=cookieName},partition=new{type="context",context=firefoxContext}}));
                        foreach(var item in response["cookies"] as object[]){
                            var cookie=item as Dictionary<string,object>;string domain=Value(cookie,"domain");
                            if(domain!="spotify.com"&&!domain.EndsWith(".spotify.com",StringComparison.OrdinalIgnoreCase))continue;
                            var value=cookie["value"] as Dictionary<string,object>;
                            if(Value(value,"type")!="string")continue;
                            retained.Add(new{name=cookieName,value=Value(value,"value"),domain=domain,path=Value(cookie,"path"),httpOnly=Flag(cookie,"httpOnly"),expirationDate=cookie.ContainsKey("expiry")?cookie["expiry"]:null});
                        }
                    }
                    session["cookies"]=retained;
                }
            }catch{session=null;}
            await StopBrowserAsync();
            return session;
        }
        async Task StartMiniPlayer(){
            if(starting||closing||BrowserAlive())return;
            bool retryWithBrowser=false;
            starting=true;
            try {
                string executable=Path.Combine(Program.Root,"runtime","ECS","electron.exe");
                if(!File.Exists(executable))throw new InvalidOperationException("Не найден движок YSpotify. Распакуй приложение целиком.");
                foreach(string file in new[]{"main.cjs","auth.cjs","catalog.cjs","service.cjs","audio-worker.js","discord.cjs","config.json"})if(!File.Exists(Path.Combine(Program.Root,"player",file)))throw new InvalidOperationException("Не хватает файлов YSpotify. Распакуй приложение целиком.");
                var session=miniForceSessionImport?null:LoadMiniSession();
                if(session==null)session=await ImportOwnFirefoxSession();
                if(closing)return;
                if(session!=null){SaveMiniSession(session);miniForceSessionImport=false;}
                SetStatus("Запускаю аудиоплеер…");
                string name="YSpotifyNative-"+Guid.NewGuid().ToString("N");
                var security=new PipeSecurity();
                security.SetAccessRuleProtection(true,false);
                security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
                miniPipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,security);
                var connected=Task.Factory.FromAsync(miniPipe.BeginWaitForConnection,miniPipe.EndWaitForConnection,null);
                browser=LaunchOwnedBrowser(executable,"\""+Path.Combine(Program.Root,"player","main.cjs")+"\" --yspotify-pipe="+name,false);
                if(await Task.WhenAny(connected,Task.Delay(120000))!=connected)throw new TimeoutException("Не удалось запустить аудиоплеер.");
                await connected;
                uint client;
                if(!GetNamedPipeClientProcessId(miniPipe.SafePipeHandle.DangerousGetHandle(),out client)||!OwnedProcessIds().Contains((int)client))throw new InvalidOperationException("Неверный процесс аудиоплеера.");
                miniReader=new StreamReader(miniPipe,new UTF8Encoding(false),false,4096,true);
                miniWriter=new StreamWriter(miniPipe,new UTF8Encoding(false),4096,true){AutoFlush=true};
                miniReadTask=ReadMiniReplies(miniReader);
                var started=await MiniCall(new Dictionary<string,object>{{"action","start"},{"auth",session},{"discordEnabled",settings.DiscordEnabled},{"volume",settings.Volume}}) as Dictionary<string,object>;
                currentLoggedIn=Flag(started,"loggedIn");
                if(currentLoggedIn&&!settings.RememberLogin){settings.RememberLogin=true;settings.Save();}
                session=null;
                SetStatus("Готово · найди трек или открой свою музыку");
            }catch(Exception ex){
                retryWithBrowser=ex.Message.Contains("SessionExpired")&&!miniForceSessionImport&&DateTime.UtcNow>miniNextRecovery;
                if(retryWithBrowser){miniNextRecovery=DateTime.UtcNow.AddMinutes(2);SetStatus("Восстанавливаю сохранённый вход…");}else SetStatus(MiniMessage(ex.Message));
                CleanupFailedStart();
            }
            finally{starting=false;}
            if(retryWithBrowser){miniForceSessionImport=true;await StartMiniPlayer();return;}
            if(miniWriter!=null){await ReadMiniState();if(currentLoggedIn)await LoadMiniSidebar();}
        }
        async Task LoadMiniSidebar(){
            try{
                var reply=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","library"},{"cacheOnly",true}}) as Dictionary<string,object>;
                if(!closing&&Flag(reply,"cached"))welcome.SetPlaylists(MiniSongRows(reply));
                if(!Flag(reply,"cached")||Flag(reply,"stale")){reply=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","library"}}) as Dictionary<string,object>;if(!closing)welcome.SetPlaylists(MiniSongRows(reply));}
            }catch{}
        }
        async Task ReadMiniReplies(StreamReader reader){
            try{
                while(true){
                    string line=await reader.ReadLineAsync().ConfigureAwait(false);
                    if(line==null||line.Length>2*1024*1024)break;
                    var reply=new JavaScriptSerializer().DeserializeObject(line) as Dictionary<string,object>;
                    if(reply==null||!reply.ContainsKey("id"))continue;
                    TaskCompletionSource<object> pending;
                    if(miniPending.TryRemove(Convert.ToInt64(reply["id"]),out pending)){
                        if(reply.ContainsKey("error"))pending.TrySetException(new InvalidOperationException(Convert.ToString(reply["error"])));
                        else pending.TrySetResult(reply.ContainsKey("result")?reply["result"]:null);
                    }
                }
            }catch(IOException){}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            finally{if(object.ReferenceEquals(reader,miniReader))FailMiniPending();}
        }
        void FailMiniPending(){foreach(var pair in miniPending){TaskCompletionSource<object> pending;if(miniPending.TryRemove(pair.Key,out pending))pending.TrySetException(new IOException("Плеер отключился."));}}
        async Task<object> MiniCall(Dictionary<string,object> request){
            if(miniWriter==null||miniReader==null)throw new InvalidOperationException("Плеер ещё не запущен.");
            var writer=miniWriter;
            long id=Interlocked.Increment(ref miniSerial);request["id"]=id;
            var completion=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            miniPending[id]=completion;
            try {
                string serialized=miniJson.Serialize(request);
                await miniWriteLock.WaitAsync(lifetime.Token);
                try{if(!object.ReferenceEquals(writer,miniWriter))throw new IOException("Плеер перезапускается.");await writer.WriteLineAsync(serialized);}finally{miniWriteLock.Release();}
                if(await Task.WhenAny(completion.Task,Task.Delay(35000,lifetime.Token))!=completion.Task)throw new TimeoutException("Плеер не ответил.");
                return await completion.Task;
            }finally{TaskCompletionSource<object> ignored;miniPending.TryRemove(id,out ignored);}
        }
        Task<object> MiniCall(string action){return MiniCall(new Dictionary<string,object>{{"action",action}});}
        void DisposeMiniChannel(){
            FailMiniPending();
            if(miniReader!=null){miniReader.Dispose();miniReader=null;}
            if(miniWriter!=null){miniWriter.Dispose();miniWriter=null;}
            if(miniPipe!=null){miniPipe.Dispose();miniPipe=null;}
        }
        async Task StopMiniChannel(){
            if(miniWriteLock.Wait(0))try{if(miniWriter!=null&&miniPipe!=null&&miniPipe.IsConnected)await miniWriter.WriteLineAsync(miniJson.Serialize(new{id=Interlocked.Increment(ref miniSerial),action="quit"}));}catch{}finally{miniWriteLock.Release();}
            DisposeMiniChannel();
        }
        string MiniMessage(string code){
            if(code.Contains("SessionExpired")||code.Contains("401"))return "Сеанс Spotify истёк. Войди снова.";
            if(code.Contains("CatalogQueryChanged"))return "Spotify изменил поиск. Требуется обновление клиента.";
            if(code.Contains("CatalogHttp429"))return "Spotify временно ограничил запросы. Попробуй позже.";
            if(code.Contains("CatalogIncomplete")||code.Contains("CatalogChangedDuringLoad")||code.Contains("CatalogPageInvalid"))return "Spotify вернул неполный список. Попробуй обновить его ещё раз.";
            if(code.Contains("CatalogTooLarge"))return "Список слишком большой для текущего лимита клиента.";
            if(code.Contains("403"))return "Spotify отклонил действие для этого аккаунта.";
            if(code.Contains("PlaybackRestricted"))return "Это действие сейчас ограничено Spotify для этого трека.";
            if(code.Contains("PlaylistItemMissing"))return "Обнови плейлист перед удалением трека.";
            if(code.Contains("LibraryChangeNotConfirmed"))return "Spotify не подтвердил изменение. Список не изменён.";
            if(code.Contains("PlaylistChangeNotConfirmed"))return "Spotify не подтвердил правку плейлиста. Обнови список.";
            if(code.Contains("PlaylistPermissionDenied"))return "Этот плейлист нельзя редактировать. Выбери свой плейлист.";
            return code;
        }
        void BindMiniView(){
            miniCovers=new CoverCache(welcome);welcome.CoverImage=miniCovers.Get;welcome.CachedCoverImage=miniCovers.Peek;
            miniCovers.Ready+=async delegate(string url){if(closing)return;welcome.RefreshCovers();if(url==miniCover)await ReadMiniState();};
            welcome.SongMenuRequested+=ShowSongMenu;
            welcome.VolumeRequested+=async delegate(double value){settings.Volume=value;settings.Save();try{if(await EnsureEngine())await MiniCall(new Dictionary<string,object>{{"action","volume"},{"value",value}});}catch(Exception ex){SetStatus(MiniMessage(ex.Message));}};
            welcome.SeekRequested+=async delegate(double value){try{if(await EnsureEngine()){await MiniCall(new Dictionary<string,object>{{"action","seek"},{"fraction",value}});await ReadMiniState();}}catch(Exception ex){SetStatus(MiniMessage(ex.Message));}};
            welcome.ModeRequested+=async delegate(string mode){try{if(await EnsureEngine()){await MiniCall(new Dictionary<string,object>{{"action","mode"},{"kind",mode}});await ReadMiniState();}}catch(Exception ex){SetStatus(MiniMessage(ex.Message));}};
            welcome.SearchRequested+=async delegate(string query){view="Поиск";welcome.SetHeading("Результаты поиска");await MiniRows("search",query,false);};
            welcome.PageRequested+=async delegate(string page){view=page;welcome.SetHeading(page);if(page=="Поиск"){welcome.Loading("Введи название трека или исполнителя");return;}await MiniRows(page=="Любимое"?"liked":"library",null,page=="Плейлисты");};
            welcome.SongRequested+=async delegate(SongRow song){
                if(song==null)return;
                if(song.Uri.StartsWith("spotify:playlist:")){miniPlaylistEditable=song.CanEdit;view="Треки";welcome.SetHeading(song.Name);await MiniRows("playlist",song.Uri,false);return;}
                long pressedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                welcome.BeginTrackPlayback(song.Uri);MiniPlaybackBusy("play",true);SetStatus("Открываю трек…");
                var queue=welcome.CurrentRows.Where(row=>row.Uri.StartsWith("spotify:track:")).Select(row=>new{uri=row.Uri,uid=row.Uid??""}).Take(1000).ToArray();
                string context=miniRowsKind=="playlist"?miniRowsArgument:miniRowsKind=="search"?"spotify:search:"+Uri.EscapeDataString(miniRowsArgument??""):"spotify:collection:tracks";
                try{if(await EnsureEngine()){await MiniCall(new Dictionary<string,object>{{"action","play"},{"uri",song.Uri},{"pressedAt",pressedAt},{"queue",queue},{"context",context}});await ReadMiniState();}else MiniPlaybackBusy("",false);}catch(Exception ex){MiniPlaybackBusy("",false);SetStatus(MiniMessage(ex.Message));}
            };
            welcome.PlayRequested+=async delegate{await MiniControl("toggle");};
            welcome.NextRequested+=async delegate{await MiniControl("next");};
            welcome.PreviousRequested+=async delegate{await MiniControl("previous");};
        }
        void MiniPlaybackBusy(string role,bool value){welcome.SetPlaybackBusy(role,value);poll.Interval=value?150:WindowState==System.Windows.Forms.FormWindowState.Minimized?10000:3000;}
        async Task MiniControl(string command){
            if(command=="next"||command=="previous"){MiniPlaybackBusy(command,true);SetStatus("Открываю трек…");}
            try{if(await EnsureEngine()){await MiniCall(new Dictionary<string,object>{{"action",command},{"pressedAt",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}});await ReadMiniState();}else MiniPlaybackBusy("",false);}catch(Exception ex){MiniPlaybackBusy("",false);SetStatus(MiniMessage(ex.Message));}
        }
        async Task MiniRows(string kind,string argument,bool playlists){
            bool keepVisible=kind==miniRowsKind&&argument==miniRowsArgument&&welcome.HasRows;
            miniRowsKind=kind;miniRowsArgument=argument;
            long version=++miniQueryVersion;bool haveCache=keepVisible;if(!keepVisible)welcome.Loading("Загружаю…");else SetStatus("Обновляю список…");
            try {
                if(!await EnsureEngine())return;
                var cached=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind",kind},{"argument",argument},{"cacheOnly",true}}) as Dictionary<string,object>;
                if(version!=miniQueryVersion||closing)return;
                haveCache=haveCache||Flag(cached,"cached");if(Flag(cached,"cached")&&!keepVisible){var local=MiniSongRows(cached);welcome.SetRows(local);if(playlists)welcome.SetPlaylists(local);SetStatus(local.Count+" · из кэша · обновляю…");}
                var reply=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind",kind},{"argument",argument}}) as Dictionary<string,object>;
                if(version!=miniQueryVersion||closing)return;
                var rows=MiniSongRows(reply);
                welcome.SetRows(rows);if(playlists)welcome.SetPlaylists(rows);SetStatus(rows.Count+" · загружено");
            }catch(Exception ex){if(version==miniQueryVersion){if(!haveCache)welcome.Loading("Не удалось загрузить список");SetStatus(haveCache?"Показан сохранённый список · "+MiniMessage(ex.Message):MiniMessage(ex.Message));}}
        }
        List<SongRow> MiniSongRows(Dictionary<string,object> reply){var rows=new List<SongRow>();if(reply==null||!reply.ContainsKey("rows"))return rows;foreach(var item in reply["rows"] as object[]){var row=item as Dictionary<string,object>;rows.Add(new SongRow{Uri=Value(row,"uri"),Name=Value(row,"name"),Artist=Value(row,"artist"),Album=Value(row,"album"),Duration=Value(row,"duration"),Cover=Value(row,"cover"),Uid=Value(row,"uid"),Saved=Flag(row,"saved"),CanEdit=Flag(row,"canEdit")});}return rows;}
        async void ShowSongMenu(SongRow song,System.Drawing.Point point){
            if(song==null||!song.Uri.StartsWith("spotify:track:"))return;
            string playlist=miniRowsKind=="playlist"?miniRowsArgument:null;
            var menu=new System.Windows.Forms.ContextMenuStrip{BackColor=bg,ForeColor=ForeColor,ShowImageMargin=false};
            var favorite=new System.Windows.Forms.ToolStripMenuItem("Проверяю избранное…"){Enabled=false};menu.Items.Add(favorite);
            var add=new System.Windows.Forms.ToolStripMenuItem("Добавить в плейлист");menu.Items.Add(add);add.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("Загружаю…"){Enabled=false});
            if(playlist!=null&&miniPlaylistEditable){var remove=new System.Windows.Forms.ToolStripMenuItem("Убрать из этого плейлиста"){Enabled=!string.IsNullOrEmpty(song.Uid)};remove.Click+=async delegate{await MiniMutation("removePlaylist",song,playlist);};menu.Items.Add(remove);}
            menu.Closed+=delegate{menu.Dispose();};menu.Show(point);
            try{
                var saved=await MiniCall(new Dictionary<string,object>{{"action","saved"},{"uri",song.Uri}}) as Dictionary<string,object>;
                song.Saved=Flag(saved,"saved");if(menu.IsDisposed)return;favorite.Text=song.Saved?"Убрать из избранного":"Добавить в избранное";favorite.Enabled=true;
                favorite.Click+=async delegate{await MiniMutation(song.Saved?"removeFavorite":"addFavorite",song,null);};
                var list=await MiniCall(new Dictionary<string,object>{{"action","query"},{"kind","library"}}) as Dictionary<string,object>;if(menu.IsDisposed)return;
                add.DropDownItems.Clear();var rows=MiniSongRows(list);welcome.SetPlaylists(rows);
                foreach(var row in rows.Where(row=>row.CanEdit)){var chosen=row;var item=new System.Windows.Forms.ToolStripMenuItem(row.Name);item.Click+=async delegate{await MiniMutation("addPlaylist",song,chosen.Uri);};add.DropDownItems.Add(item);}
                if(add.DropDownItems.Count==0)add.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("Нет плейлистов для добавления"){Enabled=false});
            }catch(Exception ex){if(!menu.IsDisposed){favorite.Text=MiniMessage(ex.Message);favorite.Enabled=false;}}
        }
        async Task MiniMutation(string kind,SongRow song,string playlist){
            try{SetStatus("Сохраняю в Spotify…");await MiniCall(new Dictionary<string,object>{{"action","mutate"},{"kind",kind},{"uri",song.Uri},{"playlist",playlist},{"uid",song.Uid}});SetStatus("Сохранено в Spotify");await MiniRows(miniRowsKind,miniRowsArgument,miniRowsKind=="library");}
            catch(Exception ex){SetStatus(MiniMessage(ex.Message));}
        }
        async Task MiniLogin(){
            if(preview||closing)return;
            if(startupTask!=null&&!startupTask.IsCompleted)await startupTask;
            if(miniWriter!=null&&!authenticating)await ReadMiniState();
            if(currentLoggedIn&&!authenticating){SetStatus("Ты уже вошёл в Spotify. Можно слушать музыку.");return;}
            try{
                if(miniExternalLogin&&authenticating){await CompleteBrowserLogin();return;}
                if(!authenticating){
                    bool firefoxAvailable=false;try{firefoxAvailable=File.Exists(FindBrowser());}catch{}
                    if(firefoxAvailable){miniExternalLogin=true;await ShowFirefoxLogin();return;}
                }
                if(!authenticating&&!await EnsureEngine())return;
                await MiniCall(authenticating?"finishLogin":"login");await ReadMiniState();
            }
            catch(Exception ex){SetStatus(MiniMessage(ex.Message));}
        }
        static string MiniTime(double seconds){int n=(int)Math.Max(0,seconds);return (n/60)+":"+(n%60).ToString("00");}
        async Task ReadMiniState(){
            if(readingMini||starting||closing||miniWriter==null)return;readingMini=true;
            try {
                var data=await MiniCall("state") as Dictionary<string,object>;if(data==null)return;
                if(Flag(data,"authUpdated")){var session=await MiniCall("session");SaveMiniSession(session);settings.RememberLogin=true;settings.Save();}
                currentLoggedIn=Flag(data,"loggedIn");authenticating=Flag(data,"authenticating");
                if(!authenticating&&Value(data,"error").Contains("SessionExpired")&&DateTime.UtcNow>miniNextRecovery){
                    miniNextRecovery=DateTime.UtcNow.AddMinutes(2);SetStatus("Восстанавливаю сохранённый вход…");miniForceSessionImport=true;
                    await StopBrowserAsync();startupTask=StartBrowser();await startupTask;return;
                }
                var track=data.ContainsKey("track")?data["track"] as Dictionary<string,object>:null;
                var audio=data.ContainsKey("audio")?data["audio"] as Dictionary<string,object>:null;
                double position=audio!=null&&audio.ContainsKey("time")?Convert.ToDouble(audio["time"]):0,duration=audio!=null&&audio.ContainsKey("duration")?Convert.ToDouble(audio["duration"]):0;
                currentPlaying=audio!=null&&!Flag(audio,"paused")&&Convert.ToInt32(audio["ready"])>=3;
                MiniPlaybackBusy("",Flag(data,"opening"));
                welcome.SetRowPlayback(string.IsNullOrEmpty(Value(track,"requestedUri"))?Value(track,"uri"):Value(track,"requestedUri"),currentPlaying,Flag(data,"opening"));
                emeState=audio!=null&&audio.ContainsKey("license")?Convert.ToString(audio["license"]):"";
                welcome.SetPlayback(Value(track,"name"),Value(track,"artist"),currentPlaying,track!=null,Flag(data,"canPrevious"),Flag(data,"canNext"),duration>0?position/duration:0,MiniTime(position)+" / "+MiniTime(duration),currentLoggedIn);
                miniCover=Value(track,"cover");if(string.IsNullOrEmpty(miniCover)){var row=welcome.CurrentRows.FirstOrDefault(item=>item.Uri==Value(track,"uri"));if(row!=null)miniCover=row.Cover;}
                welcome.SetExtraPlayback(miniCover,data.ContainsKey("volume")?Convert.ToDouble(data["volume"]):settings.Volume,Flag(data,"canSeek"),Flag(data,"canShuffle"),Flag(data,"canRepeat"),Flag(data,"shuffle"),Value(data,"repeatMode"));
                if(authenticating)welcome.SetAuthentication(true);
                location.Text=currentLoggedIn?"Spotify · подключено":"Spotify · вход не выполнен";
                if(audio!=null&&Value(audio,"phase")=="license-rejected")SetStatus("Spotify отклонил воспроизведение. Обнови плеер через меню.");
                else if(!string.IsNullOrEmpty(Value(data,"error")))SetStatus(MiniMessage(Value(data,"error")));
                else if(authenticating)SetStatus("Заверши вход в открытом окне и нажми «Я вошёл».");
                else if(currentPlaying&&(status.Text=="Открываю трек…"||status.Text=="Пауза"))SetStatus("Воспроизводится");
                else if(!currentPlaying&&track!=null&&audio!=null&&Flag(audio,"paused")&&status.Text=="Воспроизводится")SetStatus("Пауза");
            }catch(Exception ex){if(!closing)SetStatus(MiniMessage(ex.Message));}
            finally{readingMini=false;}
        }
        internal void RequireSessionRenewal(){miniForceSessionImport=true;}
    }
}

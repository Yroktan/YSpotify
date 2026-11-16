using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Net.WebSockets;

namespace YSpotify {
    partial class MainWindow {
        bool reading;
        bool currentLoggedIn,currentPlaying,emeChecked;
        string emeState="";
        string view="Поиск",route="https://open.spotify.com/search";
        void BindNativeView(){
            if(miniPlayer){BindMiniView();return;}
            welcome.SearchRequested+=async delegate(string query){view="Поиск";welcome.SetHeading("Результаты поиска");welcome.Loading("Ищу «"+query+"»…");await NativeNavigate("https://open.spotify.com/search/"+Uri.EscapeDataString(query));};
            welcome.PageRequested+=async delegate(string name){view=name;welcome.SetHeading(name);welcome.Loading("Загружаю…");await NativeNavigate(name=="Любимое"?"https://open.spotify.com/collection/tracks":name=="Плейлисты"?"https://open.spotify.com/collection":"https://open.spotify.com/search");};
            welcome.SongRequested+=async delegate(SongRow song){
                if(song==null)return;
                if(song.Uri.StartsWith("spotify:playlist:")){view="Треки";welcome.SetHeading(song.Name);welcome.Loading("Открываю плейлист…");await NativeNavigate("https://open.spotify.com/playlist/"+song.Uri.Substring(17));return;}
                if(!await EnsureEngine())return;
                await Eval("window.yspotifyBridge && window.yspotifyBridge.play("+new JavaScriptSerializer().Serialize(song.Uri)+")");await ReadNativeState();
            };
            welcome.PlayRequested+=async delegate{await NativeControl("control-button-playpause");};
            welcome.PreviousRequested+=async delegate{await NativeControl("control-button-skip-back");};
            welcome.NextRequested+=async delegate{await NativeControl("control-button-skip-forward");};
        }
        async Task NativeControl(string testId){if(!await EnsureEngine())return;await Eval("(()=>{const b=document.querySelector('[data-testid=\""+testId+"\"]');if(b&&!b.disabled&&b.getAttribute('aria-disabled')!=='true')b.click()})()");await ReadNativeState();}
        async Task<bool> EnsureEngine(){
            if(preview||closing||shuttingDown)return false;
            if(miniPlayer){
                if(authenticating){SetStatus("Сначала заверши вход в Spotify.");return false;}
                if(startupTask!=null&&!startupTask.IsCompleted)await startupTask;
                if(!BrowserAlive()||miniWriter==null){await StopBrowserAsync();startupTask=StartBrowser();await startupTask;}
                return BrowserAlive()&&miniWriter!=null;
            }
            if(authenticating){SetStatus("Заверши вход в браузере и нажми «Я вошёл».");return false;}
            if(startupTask!=null&&!startupTask.IsCompleted)await startupTask;
            if(!BrowserAlive()||socket==null||socket.State!=WebSocketState.Open){await StopBrowserAsync();startupTask=StartBrowser();await startupTask;}
            return BrowserAlive()&&socket!=null&&socket.State==WebSocketState.Open;
        }
        async Task NativeNavigate(string url){route=url;if(!await EnsureEngine())return;await Send("Page.navigate",new{url=url});}
        async Task ShowLogin(){
            if(miniPlayer){await MiniLogin();return;}
            await ShowFirefoxLogin();
        }
        async Task ShowFirefoxLogin(){
            if(preview||closing||shuttingDown||openingLogin)return;
            if(authenticating){await CompleteBrowserLogin();return;}
            openingLogin=true;
            if(startupTask!=null&&!startupTask.IsCompleted)await startupTask;
            SetStatus("Открываю обычный браузер для входа…");
            try{
                await StopBrowserAsync();
                string url="https://accounts.spotify.com/login?continue="+Uri.EscapeDataString("https://open.spotify.com/search");
                ConfigureFirefoxProfile(true);string arguments=FirefoxArguments(true,url);
                browser=LaunchOwnedBrowser(FindBrowser(),arguments,true);authenticating=true;welcome.SetAuthentication(true);
                location.Text="Вход в браузере";
                SetStatus("В браузере выбери «Продолжить с Google». После входа закрой его окно или нажми «Я вошёл» здесь.");
            }catch(Exception){authenticating=false;welcome.SetAuthentication(false);SetStatus("Не удалось открыть Firefox. Проверь его установку и попробуй ещё раз.");CleanupFailedStart();}
            finally{openingLogin=false;}
        }
        async Task ReadNativeState(){
            if(reading||starting||authenticating||closing||socket==null||socket.State!=WebSocketState.Open)return;reading=true;
            try {
                string expression="(()=>{if(!window.yspotifyBridge){"+bridgeSource+"}return JSON.stringify({origin:location.hostname,data:window.yspotifyBridge?window.yspotifyBridge.read("+(view=="Плейлисты"?"true":"false")+"):null})})()";
                string response=await Eval(expression);if(response==null||closing)return;
                var json=new JavaScriptSerializer();var outer=json.DeserializeObject(response) as Dictionary<string,object>;
                if(outer.ContainsKey("error")){SetStatus("Плеер не ответил. В меню можно перезапустить его.");return;}
                var result=outer["result"] as Dictionary<string,object>;var remote=result["result"] as Dictionary<string,object>;if(!remote.ContainsKey("value"))return;
                var envelope=json.DeserializeObject(Convert.ToString(remote["value"])) as Dictionary<string,object>;if(envelope==null)return;
                var data=envelope["data"] as Dictionary<string,object>;if(data==null)return;
                var rows=new List<SongRow>();foreach(var obj in data["rows"] as object[]){var r=obj as Dictionary<string,object>;rows.Add(new SongRow{Uri=Value(r,"uri"),Name=Value(r,"name"),Artist=Value(r,"artist"),Album=Value(r,"album"),Duration=Value(r,"duration")});}
                if(Value(data,"path")==new Uri(route).AbsolutePath)welcome.SetRows(rows);
                if(view=="Плейлисты"&&rows.Count>0)welcome.SetPlaylists(rows);
                var player=data["player"] as Dictionary<string,object>;
                welcome.SetPlayback(Value(player,"name"),Value(player,"artist"),Flag(player,"playing"),Flag(player,"canPlay"),Flag(player,"canPrevious"),Flag(player,"canNext"),Convert.ToDouble(player["fraction"]),Value(player,"time"),Flag(data,"loggedIn"));
                bool loggedIn=Flag(data,"loggedIn");location.Text=loggedIn?"Spotify · подключено":"Spotify · вход не выполнен";
                currentLoggedIn=loggedIn;currentPlaying=Flag(player,"playing");emeState=Value(data,"eme");
                if(loggedIn&&headlessPlayback&&!emeChecked){
                    emeChecked=true;
                    await Eval("window.yspotifyEme='checking';navigator.requestMediaKeySystemAccess('com.widevine.alpha',[{initDataTypes:['cenc'],audioCapabilities:[{contentType:'audio/mp4; codecs=\"mp4a.40.2\"'}]}]).then(()=>window.yspotifyEme='supported',e=>window.yspotifyEme=e.name)");
                }
                if(loggedIn&&!settings.RememberLogin){settings.RememberLogin=true;settings.Save();SetStatus("Вход выполнен · можно слушать музыку");}
            }catch(Exception){if(!closing)SetStatus("Страница ещё загружается. Если список не появился, обнови плеер через меню.");}
            finally{reading=false;}
        }
        static string Value(Dictionary<string,object> d,string key){return d!=null&&d.ContainsKey(key)?Convert.ToString(d[key]):"";}
        static bool Flag(Dictionary<string,object>d,string key){return d!=null&&d.ContainsKey(key)&&object.Equals(d[key],true);}
    }
}

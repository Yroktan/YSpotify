using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace YSpotify {
    partial class MainWindow {
        string firefoxContext;
        string firefoxProfileOverride=null;
        int firefoxContextCount;
        string FirefoxProfile{get{return firefoxProfileOverride??Path.Combine(Program.Data,"FirefoxProfile");}}
        string FindBrowser(){
            foreach(string root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}){
                string path=Path.Combine(root,"Mozilla Firefox","firefox.exe");if(File.Exists(path))return path;
            }
            throw new InvalidOperationException("Не найден Firefox. Установи Firefox и запусти YSpotify снова.");
        }
        string FirefoxArguments(bool visible,string url){
            return "--no-remote --profile \""+FirefoxProfile+"\" "+(visible?"--new-window ":(headlessPlayback?"--headless ":"")+"--remote-debugging-port "+port+" ")+"\""+url+"\"";
        }
        void ConfigureFirefoxProfile(bool login){
            Directory.CreateDirectory(FirefoxProfile);
            var prefs=new Dictionary<string,object>{
                {"browser.shell.checkDefaultBrowser",false},{"browser.startup.page",0},{"browser.startup.homepage","about:blank"},
                {"browser.aboutwelcome.enabled",false},{"browser.newtabpage.enabled",false},{"browser.newtab.preload",false},
                {"browser.sessionstore.resume_from_crash",false},{"browser.sessionstore.max_tabs_undo",0},{"browser.sessionstore.max_windows_undo",0},
                {"dom.ipc.processPrelaunch.enabled",false},{"dom.ipc.processCount",1},
                {"browser.cache.memory.capacity",16384},{"browser.cache.disk.capacity",32768},{"image.mem.surfacecache.max_size_kb",8192},
                {"permissions.default.image",login?1:2},
                {"media.eme.enabled",true},{"media.gmp-widevinecdm.enabled",true},
                {"remote.prefs.recommended",false},{"browser.tabs.warnOnClose",false},{"browser.warnOnQuit",false},
                {"network.proxy.type",5}
            };
            if(!string.IsNullOrWhiteSpace(settings.Proxy)){
                var proxy=new Uri(settings.Proxy);prefs["network.proxy.type"]=1;
                if(proxy.Scheme=="socks5"){
                    prefs["network.proxy.socks"]=proxy.Host;prefs["network.proxy.socks_port"]=proxy.Port;prefs["network.proxy.socks_version"]=5;prefs["network.proxy.socks_remote_dns"]=true;
                }else{
                    prefs["network.proxy.http"]=proxy.Host;prefs["network.proxy.http_port"]=proxy.Port;
                    prefs["network.proxy.ssl"]=proxy.Host;prefs["network.proxy.ssl_port"]=proxy.Port;
                }
            }
            var json=new JavaScriptSerializer();var text=new StringBuilder("// YSpotify private profile; does not change your regular Firefox profile.\n");
            foreach(var pair in prefs)text.Append("user_pref(").Append(json.Serialize(pair.Key)).Append(", ").Append(json.Serialize(pair.Value)).Append(");\n");
            File.WriteAllText(Path.Combine(FirefoxProfile,"user.js"),text.ToString(),new UTF8Encoding(false));
        }
        async Task Connect(){
            Exception last=null;
            for(int n=0;n<60&&!closing;n++){
                try{
                    socket=new ClientWebSocket();
                    using(var timeout=System.Threading.CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)){
                        timeout.CancelAfter(2000);await socket.ConnectAsync(new Uri("ws://127.0.0.1:"+port+"/session"),timeout.Token);
                    }
                    var session=ProtocolResult(await Send("session.new",new{capabilities=new{alwaysMatch=new{browserName="firefox",acceptInsecureCerts=false}}}));
                    var tree=ProtocolResult(await Send("browsingContext.getTree",new{maxDepth=0}));
                    var contexts=tree["contexts"] as object[];
                    if(contexts==null||contexts.Length==0)throw new InvalidOperationException("Firefox ещё не открыл вкладку.");
                    firefoxContextCount=contexts.Length;
                    firefoxContext=Convert.ToString((contexts[0] as Dictionary<string,object>)["context"]);
                    ProtocolResult(await Send("script.addPreloadScript",new{functionDeclaration="() => {"+bridgeSource+"}"}));
                    return;
                }catch(Exception ex){last=ex;if(socket!=null){socket.Dispose();socket=null;}}
                if(lifetime.IsCancellationRequested)return;
                await Task.Delay(150);
            }
            throw new InvalidOperationException("Не удалось подключиться к Firefox: "+(last==null?"таймаут":last.Message));
        }
        Dictionary<string,object> ProtocolResult(string response){
            if(response==null)throw new InvalidOperationException("Firefox не ответил.");
            var data=new JavaScriptSerializer().DeserializeObject(response) as Dictionary<string,object>;
            if(data==null||data.ContainsKey("error"))throw new InvalidOperationException(data!=null&&data.ContainsKey("error")?Convert.ToString(data["error"]):"Некорректный ответ Firefox.");
            return data["result"] as Dictionary<string,object>;
        }
        void MapFirefoxCommand(ref string method,ref object parameters){
            if(method=="Runtime.evaluate"){
                var p=new JavaScriptSerializer().DeserializeObject(new JavaScriptSerializer().Serialize(parameters)) as Dictionary<string,object>;
                method="script.evaluate";parameters=new{expression=Convert.ToString(p["expression"]),target=new{context=firefoxContext},awaitPromise=false,userActivation=true};
            }else if(method=="Page.navigate"){
                var p=new JavaScriptSerializer().DeserializeObject(new JavaScriptSerializer().Serialize(parameters)) as Dictionary<string,object>;
                method="browsingContext.navigate";parameters=new{context=firefoxContext,url=Convert.ToString(p["url"]),wait="none"};
            }else if(method=="Page.reload"){
                method="browsingContext.reload";parameters=new{context=firefoxContext,ignoreCache=false,wait="none"};
            }else if(method=="Browser.close"){method="browser.close";parameters=new{};}
        }
    }
}

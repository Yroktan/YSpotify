using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.WebSockets;
using System.Web.Script.Serialization;
using System.Collections.Generic;

namespace YSpotify {
    static class Program {
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YSpotify");
        internal static void StoreLocalFile(string name,byte[] bytes){
            Directory.CreateDirectory(Data);string path=Path.Combine(Data,name),temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllBytes(temporary,bytes);if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        [STAThread] static void Main(string[] args) {
            using (var mutex = new Mutex(false, "Local\\YSpotify-Desktop-v1")) {
                if (!mutex.WaitOne(0, false)) {
                    Native.EnumWindows(delegate(IntPtr w, IntPtr p) { var t = new StringBuilder(256); Native.GetWindowText(w,t,t.Capacity); if(t.ToString()=="YSpotify") { Native.ShowWindow(w,9); Native.SetForegroundWindow(w); } return true; },IntPtr.Zero);
                    return;
                }
                try {
                    ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
                    Directory.CreateDirectory(Data);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    bool design=args.Contains("--design-preview");
                    bool mini=args.Contains("--mini-player")||!args.Contains("--firefox-player");
                    var window=new MainWindow(design||args.Contains("--preview"),args.Contains("--headless-player")||mini,mini);
                    if(args.Contains("--refresh-session"))window.RequireSessionRenewal();
                    if(args.Contains("--self-test")||args.Contains("--endurance-test"))window.Shown+=async delegate{if(args.Contains("--self-test"))await window.RunMiniQuickChecks();if(args.Contains("--endurance-test"))await window.RunMiniEndurance();};
                    if(design)window.ShowDesignPreview();
                    Application.Run(window);
                } catch (Exception ex) { try{File.WriteAllText(Path.Combine(Root,"startup-error.txt"),ex.ToString());}catch{} MessageBox.Show(ex.Message,"YSpotify",MessageBoxButtons.OK,MessageBoxIcon.Error); }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
    static class Native {
        public delegate bool EnumProc(IntPtr h, IntPtr p);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f,IntPtr p);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
        [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h,StringBuilder t,int n);
        [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h,StringBuilder t,int n);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr c,IntPtr p);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h,int n);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h,int n,int v);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int z,bool r);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int n);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr a,IntPtr b);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr a,string n);
        [DllImport("kernel32.dll")] public static extern bool SetInformationJobObject(IntPtr j,int c,IntPtr p,uint l);
        [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr j,IntPtr p);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    }
    class Settings {
        public string Proxy = "";
        public bool AutoOpen = false;
        public bool RememberLogin = false;
        public bool DiscordEnabled = false;
        public double Volume = 0.8;
        public static Settings Read() {
            try { return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Path.Combine(Program.Data,"settings.json"))) ?? new Settings(); }
            catch { return new Settings(); }
        }
        public void Save() { Program.StoreLocalFile("settings.json",Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(this))); }
    }
    class FlatButton : Button {
        public FlatButton(string text) {
            Text=text; FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0;
            FlatAppearance.MouseOverBackColor=Color.FromArgb(45,65,50);
            FlatAppearance.MouseDownBackColor=Color.FromArgb(62,101,73);
            ForeColor=Color.FromArgb(237,225,222); BackColor=Color.FromArgb(28,24,34);
            Font=new Font("Segoe UI",10); Cursor=Cursors.Hand; Height=42; TabStop=true;
        }
    }
    partial class MainWindow : Form {
        readonly Color bg=Color.FromArgb(14,19,17),muted=Color.FromArgb(146,164,151),red=Color.FromArgb(156,218,170);
        Panel host,side,bar; Welcome welcome; Label status,memory,location; Process browser; IntPtr child,job;
        int port; bool closing,starting,preview; ClientWebSocket socket; readonly SemaphoreSlim wsLock=new SemaphoreSlim(1,1);
        CancellationTokenSource lifetime=new CancellationTokenSource(); Settings settings=Settings.Read();
        System.Windows.Forms.Timer poll=new System.Windows.Forms.Timer(); long serial;
        readonly bool headlessPlayback;
        public MainWindow(bool previewOnly,bool headless=false,bool mini=false) {
            miniPlayer=mini;
            headlessPlayback=headless;
            preview=previewOnly; Text="YSpotify"; BackColor=bg; ForeColor=Color.FromArgb(231,239,232);
            var workArea=Screen.PrimaryScreen.WorkingArea;
            ClientSize=new Size(Math.Min(1240,workArea.Width-64),Math.Min(900,workArea.Height-80)); MinimumSize=new Size(880,640); StartPosition=FormStartPosition.CenterScreen; Font=new Font("Segoe UI",10);
            string appIcon=Path.Combine(Program.Root,"branding","yspotify.ico");if(File.Exists(appIcon))Icon=new Icon(appIcon);
            BuildUI();
            BindNativeView();
            Resize+=delegate { poll.Interval=welcome.PlaybackBusy?150:WindowState==FormWindowState.Minimized?10000:3000; };
            FormClosing+=async delegate(object s,FormClosingEventArgs e){if(allowClose)return;e.Cancel=true;if(shuttingDown)return;shuttingDown=true;Enabled=false;await ShutdownAsync();};
            poll.Interval=3000; poll.Tick+=async delegate { await PollAsync(); }; poll.Start();
            Shown+=async delegate { if(!preview&&(settings.RememberLogin||(miniPlayer&&File.Exists(MiniSessionPath)))){startupTask=StartBrowser();await startupTask;} };
        }
        Label LabelAt(Control parent,string text,int x,int y,int w,int h,float size,Color color) {
            var l=new Label {Text=text,Location=new Point(x,y),Size=new Size(w,h),Font=new Font("Segoe UI",size),ForeColor=color,BackColor=Color.Transparent};parent.Controls.Add(l);return l;
        }
        FlatButton Nav(string text,int y,Action action) { var b=new FlatButton(text){Location=new Point(18,y),Size=new Size(178,42),TextAlign=ContentAlignment.MiddleLeft};b.Click+=delegate{action();};side.Controls.Add(b);return b; }
        void BuildUI() {
            side=new Panel {Visible=false};
            bar=new BrandHeader {Dock=DockStyle.Top,Height=142,Padding=new Padding(12,0,12,0)};
            Controls.Add(bar);
            location=LabelAt(bar,"Spotify",210,64,320,22,9,muted);
            location.AutoEllipsis=true;location.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            memory=LabelAt(bar,"",975,65,180,22,8,muted);memory.TextAlign=ContentAlignment.TopRight;memory.Anchor=AnchorStyles.Top|AnchorStyles.Right;
            var menuButton=new FlatButton("⋯") {Location=new Point(1176,52),Size=new Size(38,36),BackColor=bar.BackColor,Font=new Font("Segoe UI",17),Anchor=AnchorStyles.Top|AnchorStyles.Right};
            var menu=new ContextMenuStrip {BackColor=bg,ForeColor=ForeColor,ShowImageMargin=false};
            menu.Items.Add("Обновить плеер",null,async delegate{if(miniPlayer)await ReadMiniState();else await Send("Page.reload",new {ignoreCache=false});});
            menu.Items.Add("Перезапустить плеер",null,async delegate{if(authenticating){SetStatus("Сначала заверши вход в браузере.");return;}await StopBrowserAsync();startupTask=StartBrowser();await startupTask;});
            if(!miniPlayer)menu.Items.Add("Подключение…",null,delegate{ShowSettings();});
            var discordItem=new ToolStripMenuItem("Показывать трек в Discord"){CheckOnClick=true,Checked=settings.DiscordEnabled};
            discordItem.CheckedChanged+=async delegate{settings.DiscordEnabled=discordItem.Checked;settings.Save();if(miniWriter!=null)try{await MiniCall(new Dictionary<string,object>{{"action","discord"},{"enabled",settings.DiscordEnabled}});}catch{}};if(settings.DiscordEnabled)menu.Items.Add(discordItem);
            menu.Items.Add("Открыть папку приложения",null,delegate{Process.Start(Program.Root);});
            menuButton.Click+=delegate{menu.Show(menuButton,new Point(0,menuButton.Height));};bar.Controls.Add(menuButton);
            var footer=new Panel {Dock=DockStyle.Bottom,Height=23,BackColor=bg};Controls.Add(footer);
            status=LabelAt(footer,"",14,4,1212,18,8,muted);status.AutoEllipsis=true;status.Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right;
            host=new Panel {Dock=DockStyle.Fill,BackColor=bg};Controls.Add(host);host.BringToFront();
            welcome=new Welcome {Dock=DockStyle.Fill};welcome.Launch+=async delegate{await ShowLogin();};host.Controls.Add(welcome);
        }
        async Task StartBrowser() {
            if(miniPlayer){await StartMiniPlayer();return;}
            if(starting||closing)return;
            if(child!=IntPtr.Zero)return;
            starting=true;SetStatus("Запускаю отдельный плеер…");
            try {
                string exe=FindBrowser();
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                ConfigureFirefoxProfile(false);
                string arguments=FirefoxArguments(false,"about:blank");

                browser=LaunchOwnedBrowser(exe,arguments,false);
                for(int attempt=0;attempt<160&&!closing&&!headlessPlayback;attempt++) {
                    var ownedIds=OwnedProcessIds();Native.EnumWindows(delegate(IntPtr h,IntPtr p){uint id;Native.GetWindowThreadProcessId(h,out id);if(ownedIds.Contains((int)id)){var title=new StringBuilder(512);var cls=new StringBuilder(128);Native.GetWindowText(h,title,512);Native.GetClassName(h,cls,128);if(title.Length>0&&cls.ToString()=="MozillaWindowClass"){child=h;return false;}}return true;},IntPtr.Zero);
                    if(child!=IntPtr.Zero)break;
                    if(!BrowserAlive())throw new Exception("Firefox exited before its window was ready.");
                    await Task.Delay(100,lifetime.Token);
                }
                if(closing)return;
                if(!headlessPlayback&&child==IntPtr.Zero)throw new Exception("Не удалось открыть окно движка Spotify.");
                if(child!=IntPtr.Zero)Native.ShowWindow(child,0);
                welcome.Visible=true;welcome.BringToFront();
                settings.AutoOpen=true;settings.Save();
                try {await Connect();await Send("Page.navigate",new{url=route});SetStatus("Готово · войди в Spotify или найди трек");}catch(Exception ex){SetStatus("Не подключился к движку: "+ex.Message);}
                await UpdateStatus();
            } catch(OperationCanceledException) {} catch(Exception ex){SetStatus(ex.Message);welcome.Visible=true;CleanupFailedStart();}
            finally {starting=false;}
        }
        void CleanupFailedStart() { DisposeMiniChannel();child=IntPtr.Zero;if(socket!=null){socket.Dispose();socket=null;}if(job!=IntPtr.Zero){Native.CloseHandle(job);job=IntPtr.Zero;} }
        void SetStatus(string text){if(!closing)status.Text=text;}
        async Task<string> Send(string method,object parameters) {
            if(socket==null||socket.State!=WebSocketState.Open||closing)return null;
            MapFirefoxCommand(ref method,ref parameters);
            try {await wsLock.WaitAsync(lifetime.Token);}catch(OperationCanceledException){return null;}
            try {
                long id=++serial;string request=new JavaScriptSerializer().Serialize(new {id=id,method=method,@params=parameters});
                byte[] data=Encoding.UTF8.GetBytes(request);
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                    timeout.CancelAfter(8000);
                    await socket.SendAsync(new ArraySegment<byte>(data),WebSocketMessageType.Text,true,timeout.Token);
                    var buffer=new byte[32768];
                    while(!closing) {
                        using(var stream=new MemoryStream()) {
                            WebSocketReceiveResult result;
                            do{result=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),timeout.Token);if(result.MessageType==WebSocketMessageType.Close)return null;stream.Write(buffer,0,result.Count);if(stream.Length>2*1024*1024)throw new Exception("Слишком большой ответ плеера.");}while(!result.EndOfMessage);
                            string answer=Encoding.UTF8.GetString(stream.ToArray());var obj=new JavaScriptSerializer().DeserializeObject(answer) as Dictionary<string,object>;
                            if(obj!=null&&obj.ContainsKey("id")&&Convert.ToInt64(obj["id"])==id)return answer;
                        }
                    }
                }
            } catch(OperationCanceledException){}catch(Exception ex){SetStatus("Канал плеера: "+ex.Message);}finally{wsLock.Release();}
            return null;
        }
        Task<string> Eval(string expression){return Send("Runtime.evaluate",new {expression=expression,returnByValue=true,userGesture=true});}
        async Task Navigate(string url) {
            if(child==IntPtr.Zero){await StartBrowser();}
            await Send("Page.navigate",new {url=url});
        }
        bool measuringMemory;
        DateTime lastMemorySample=DateTime.MinValue;
        async Task UpdateStatus() {
            if(closing||measuringMemory||DateTime.UtcNow-lastMemorySample<TimeSpan.FromSeconds(3))return;measuringMemory=true;lastMemorySample=DateTime.UtcNow;
            try{
                var sampledJob=job;var ids=OwnedProcessIds();ids.Insert(0,Process.GetCurrentProcess().Id);
                long bytes=await Task.Run(delegate{long total=0;foreach(int id in ids)try{total+=PrivateWorkingSet(id);}catch(ArgumentException){}return total;});
                if(closing||sampledJob!=job)return;
                memory.Text=(bytes/1024/1024)+(job!=IntPtr.Zero?" МБ · все процессы":" МБ · плеер выключен");
                await Task.Run(delegate{SaveMemorySnapshot(ids,bytes);});
            }finally{measuringMemory=false;}
        }
        [DllImport("kernel32.dll")] static extern bool QueryInformationJobObject(IntPtr j,int c,IntPtr p,uint n,out uint r);
        void ShowSettings() {
            using(var dialog=new Form {Text="YSpotify · Подключение",ClientSize=new Size(530,355),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,BackColor=bg,ForeColor=ForeColor,Font=Font}) {
                LabelAt(dialog,"Подключение к Spotify",24,22,480,32,17,ForeColor);
                LabelAt(dialog,"Пустое поле — обычное подключение через системную сеть.\nПрокси применяется только к YSpotify после перезапуска.",25,69,480,49,10,muted);
                LabelAt(dialog,"Прокси: http://адрес:порт или socks5://адрес:порт",25,132,480,25,9,muted);
                var input=new TextBox {Text=settings.Proxy,Location=new Point(25,164),Width=476,BackColor=Color.FromArgb(39,31,43),ForeColor=ForeColor,BorderStyle=BorderStyle.FixedSingle,Font=new Font("Segoe UI",12)};dialog.Controls.Add(input);
                LabelAt(dialog,"Прокси не меняет подписку и не гарантирует отсутствие рекламы.\nАвторизация прокси по логину и паролю в этой версии не поддерживается.",25,213,480,50,8,muted);
                var save=new FlatButton("Сохранить"){Location=new Point(25,289),Size=new Size(160,42),BackColor=red};dialog.Controls.Add(save);
                save.Click+=delegate {
                    string value=input.Text.Trim();Uri uri;
                    if(value.Length>0&&(!Uri.TryCreate(value,UriKind.Absolute,out uri)||(uri.Scheme!="http"&&uri.Scheme!="https"&&uri.Scheme!="socks5")||string.IsNullOrWhiteSpace(uri.Host)||uri.Port<1||uri.UserInfo.Length>0||uri.AbsolutePath!="/"||uri.Query.Length>0||uri.Fragment.Length>0)) {
                        MessageBox.Show(dialog,"Укажи только адрес и порт: socks5://127.0.0.1:1080\nБез логина, пароля, пути и параметров.","Проверь адрес");return;
                    }
                    settings.Proxy=value;settings.Save();SetStatus("Настройки сохранены. Закрой и открой YSpotify, чтобы применить подключение.");dialog.Close();
                };
                dialog.ShowDialog(this);
            }
        }
    }
}

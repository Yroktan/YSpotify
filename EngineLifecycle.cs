using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace YSpotify {
    partial class MainWindow {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct StartupInfo {
            public int cb;public string reserved,desktop,title;
            public int x,y,width,height,xChars,yChars,fill,flags;
            public short show,reservedBytes;public IntPtr reservedPtr,input,output,error;
        }
        [StructLayout(LayoutKind.Sequential)]struct ProcessInfo {public IntPtr process,thread;public int id,threadId;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool CreateProcess(string app,StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref StartupInfo start,out ProcessInfo info);
        [DllImport("kernel32.dll",SetLastError=true)]static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool TerminateProcess(IntPtr process,uint code);
        bool authenticating,finishingLogin,ticking,allowClose,shuttingDown,openingLogin;
        Task startupTask;
        readonly string bridgeSource=File.ReadAllText(Path.Combine(Program.Root,"bridge.js"),Encoding.UTF8);
        Process LaunchOwnedBrowser(string exe,string arguments,bool visible){
            if(job!=IntPtr.Zero)throw new InvalidOperationException("Предыдущий плеер ещё не остановлен.");
            IntPtr nextJob=Native.CreateJobObject(IntPtr.Zero,null);
            if(nextJob==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            int bytes=IntPtr.Size==8?144:112;IntPtr limits=Marshal.AllocHGlobal(bytes);ProcessInfo info=new ProcessInfo();bool success=false;
            try{
                for(int n=0;n<bytes;n++)Marshal.WriteByte(limits,n,0);
                Marshal.WriteInt32(limits,16,0x2000);
                if(!Native.SetInformationJobObject(nextJob,9,limits,(uint)bytes))throw new Win32Exception(Marshal.GetLastWin32Error());
                var start=new StartupInfo{cb=Marshal.SizeOf(typeof(StartupInfo)),flags=1,show=(short)(visible?1:0)};
                var command=new StringBuilder("\""+exe+"\" "+arguments);
                if(!CreateProcess(exe,command,IntPtr.Zero,IntPtr.Zero,false,4,IntPtr.Zero,Program.Root,ref start,out info))throw new Win32Exception(Marshal.GetLastWin32Error());
                if(!Native.AssignProcessToJobObject(nextJob,info.process))throw new Win32Exception(Marshal.GetLastWin32Error());
                var process=Process.GetProcessById(info.id);
                if(ResumeThread(info.thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
                job=nextJob;success=true;return process;
            }finally{
                Marshal.FreeHGlobal(limits);
                if(!success){if(info.process!=IntPtr.Zero)TerminateProcess(info.process,1);Native.CloseHandle(nextJob);}
                if(info.thread!=IntPtr.Zero)Native.CloseHandle(info.thread);
                if(info.process!=IntPtr.Zero)Native.CloseHandle(info.process);
            }
        }
        List<int> OwnedProcessIds(){
            var ids=new List<int>();if(job==IntPtr.Zero)return ids;
            int size=8+IntPtr.Size*256;IntPtr info=Marshal.AllocHGlobal(size);
            try{uint returned;if(QueryInformationJobObject(job,3,info,(uint)size,out returned)){int count=Math.Min(256,Marshal.ReadInt32(info,4));for(int i=0;i<count;i++)ids.Add(Marshal.ReadIntPtr(info,8+i*IntPtr.Size).ToInt32());}}
            finally{Marshal.FreeHGlobal(info);}return ids;
        }
        bool BrowserAlive(){try{return job!=IntPtr.Zero?OwnedProcessIds().Count>0:browser!=null&&!browser.HasExited;}catch{return false;}}
        void CloseOwnedFirefoxWindows(){
            var ids=OwnedProcessIds();
            Native.EnumWindows(delegate(IntPtr w,IntPtr unused){uint id;Native.GetWindowThreadProcessId(w,out id);if(ids.Contains((int)id)){var c=new StringBuilder(128);Native.GetClassName(w,c,128);if(c.ToString()=="MozillaWindowClass")Native.PostMessage(w,0x10,IntPtr.Zero,IntPtr.Zero);}return true;},IntPtr.Zero);
        }
        async Task StopBrowserAsync(){
            if(miniPipe!=null)await StopMiniChannel();
            if(socket!=null){
                try{if(socket.State==System.Net.WebSockets.WebSocketState.Open)await Send("Browser.close",new{});}catch{}
            }
            if(BrowserAlive())try{CloseOwnedFirefoxWindows();}catch{}
            for(int n=0;n<20&&BrowserAlive();n++)await Task.Delay(100);
            var remaining=OwnedProcessIds();CleanupFailedStart();
            for(int n=0;n<20;n++){
                bool alive=false;foreach(int id in remaining)try{using(var process=Process.GetProcessById(id))if(!process.HasExited)alive=true;}catch(ArgumentException){}
                if(!alive)break;await Task.Delay(100);
            }
            if(browser!=null){browser.Dispose();browser=null;}
        }
        async Task ShutdownAsync(){
            poll.Stop();
            try{await StopBrowserAsync();}catch{}
            closing=true;lifetime.Cancel();if(miniCovers!=null)miniCovers.Dispose();CleanupFailedStart();allowClose=true;Close();
        }
        async Task CompleteBrowserLogin(){
            if(!authenticating||finishingLogin||closing)return;finishingLogin=true;
            SetStatus("Сохраняю вход и открываю твою музыку…");
            try{
                await StopBrowserAsync();authenticating=false;welcome.SetAuthentication(false);
                if(miniExternalLogin){miniExternalLogin=false;miniForceSessionImport=true;settings.RememberLogin=true;settings.Save();}
                startupTask=StartBrowser();await startupTask;
            }finally{finishingLogin=false;}
        }
        async Task PollAsync(){
            if(ticking||preview||closing||shuttingDown)return;ticking=true;
            try{
                if(miniPlayer){if(miniExternalLogin&&authenticating&&!BrowserAlive())await CompleteBrowserLogin();await UpdateStatus();if(!miniExternalLogin)await ReadMiniState();return;}
                if(authenticating&&!BrowserAlive())await CompleteBrowserLogin();
                await UpdateStatus();
                if(!authenticating)await ReadNativeState();
            }catch(Exception){if(!closing)SetStatus("Плеер временно недоступен. Открой меню для перезапуска.");}
            finally{ticking=false;}
        }
    }
}

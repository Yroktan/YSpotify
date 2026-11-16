using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace YSpotify {
    partial class MainWindow {
        void SaveMemorySnapshot(List<int> ids,long bytes){
            try{Program.StoreLocalFile("runtime-metrics.json",Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new{capturedUtc=DateTime.UtcNow.ToString("o"),clientProcessId=Process.GetCurrentProcess().Id,processIds=ids,privateWorkingSetBytes=bytes,loginInBrowser=authenticating,headless=headlessPlayback,contexts=firefoxContextCount,loggedIn=currentLoggedIn,playing=currentPlaying,widevine=emeState})));}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,int id);
        [DllImport("psapi.dll",SetLastError=true)] static extern bool QueryWorkingSet(IntPtr process,IntPtr buffer,int bytes);
        static long PrivateWorkingSet(int id){
            IntPtr process=OpenProcess(0x410,false,id);
            if(process==IntPtr.Zero){using(var p=Process.GetProcessById(id))return p.WorkingSet64;}
            try {
                int capacity=262144;
                for(int attempt=0;attempt<3;attempt++){
                    IntPtr buffer=Marshal.AllocHGlobal(capacity);
                    try{
                        if(QueryWorkingSet(process,buffer,capacity)){
                            int count=Marshal.ReadIntPtr(buffer).ToInt32();long pages=0;
                            for(int n=0;n<count;n++)if((Marshal.ReadIntPtr(buffer,(n+1)*IntPtr.Size).ToInt64()&256)==0)pages++;
                            return pages*Environment.SystemPageSize;
                        }
                    }finally{Marshal.FreeHGlobal(buffer);}
                    capacity*=4;
                }
                using(var p=Process.GetProcessById(id))return p.WorkingSet64;
            }finally{Native.CloseHandle(process);}
        }
    }
}

using System;
using System.Runtime.InteropServices;

namespace YSpotify {
    partial class MainWindow {
        public void ShowDesignPreview(){
            welcome.DesignPreview();location.Text="Предпросмотр дизайна";memory.Text="Плеер выключен";
            status.Text="Макет интерфейса · треки и плейлисты приведены для примера";
        }
        protected override void OnHandleCreated(EventArgs e){
            base.OnHandleCreated(e);
            try{int dark=1;if(DwmSetWindowAttribute(Handle,20,ref dark,4)!=0)DwmSetWindowAttribute(Handle,19,ref dark,4);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
        }
        [DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    }
}

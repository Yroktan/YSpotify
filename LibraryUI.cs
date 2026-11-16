using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YSpotify {
    sealed class CoverCache:IDisposable {
        readonly Dictionary<string,Bitmap> images=new Dictionary<string,Bitmap>();
        readonly LinkedList<string> order=new LinkedList<string>();
        readonly HashSet<string> pending=new HashSet<string>(),failed=new HashSet<string>();
        readonly SemaphoreSlim slots=new SemaphoreSlim(2,2);
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly HttpClient http=new HttpClient(new HttpClientHandler{AutomaticDecompression=System.Net.DecompressionMethods.GZip|System.Net.DecompressionMethods.Deflate}){Timeout=TimeSpan.FromSeconds(12)};
        readonly string directory=Path.Combine(Program.Data,"Covers");
        readonly Control owner;bool disposed;
        public event Action<string> Ready;
        public CoverCache(Control owner){this.owner=owner;Directory.CreateDirectory(directory);}
        public Image Get(string url){
            if(string.IsNullOrEmpty(url)||disposed)return null;
            Bitmap image;if(images.TryGetValue(url,out image)){order.Remove(url);order.AddFirst(url);return image;}
            if(!pending.Contains(url)&&!failed.Contains(url)){pending.Add(url);Load(url);}
            return null;
        }
        public Image Peek(string url){Bitmap image;return !disposed&&!string.IsNullOrEmpty(url)&&images.TryGetValue(url,out image)?image:null;}
        async void Load(string url){
            Bitmap result=null;bool acquired=false;string stage="queue";
            try{
                await slots.WaitAsync(cancel.Token);acquired=true;stage="url";
                result=await Task.Run(async delegate{
                    Uri target;if(!Uri.TryCreate(url,UriKind.Absolute,out target)||target.Scheme!="https"||!(target.Host=="scdn.co"||target.Host.EndsWith(".scdn.co",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException();
                    string key;using(var hash=SHA256.Create())key=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-","");
                    string file=Path.Combine(directory,key+".jpg");byte[] bytes;
                    stage="download";if(File.Exists(file))bytes=File.ReadAllBytes(file);else bytes=await http.GetByteArrayAsync(url).ConfigureAwait(false);
                    if(bytes.Length>512*1024)throw new InvalidDataException();stage="decode";Bitmap thumbnail=null;
                    try{
                        using(var input=new MemoryStream(bytes))using(var source=Image.FromStream(input)){
                            if(source.Width>2048||source.Height>2048)throw new InvalidDataException();
                            thumbnail=new Bitmap(64,64);using(var g=Graphics.FromImage(thumbnail)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.DrawImage(source,0,0,64,64);}
                        }
                        stage="save";if(!File.Exists(file))thumbnail.Save(file,System.Drawing.Imaging.ImageFormat.Jpeg);
                        string diagnostic=Path.Combine(Program.Data,"cover-diagnostics.json");if(File.Exists(diagnostic))File.Delete(diagnostic);
                        return thumbnail;
                    }catch{if(thumbnail!=null)thumbnail.Dispose();throw;}
                },cancel.Token);
                if(disposed){result.Dispose();return;}
                images[url]=result;order.AddFirst(url);result=null;
                while(order.Count>96){string oldest=order.Last.Value;order.RemoveLast();images[oldest].Dispose();images.Remove(oldest);}
                stage="notify";if(Ready!=null)Ready(url);
            }catch(OperationCanceledException){}catch(Exception error){failed.Add(url);try{Program.StoreLocalFile("cover-diagnostics.json",Encoding.UTF8.GetBytes(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{stage=stage,failure=error.GetBaseException().GetType().Name,message=error.GetBaseException().Message,tls=System.Net.ServicePointManager.SecurityProtocol.ToString(),urlHost=new Uri(url).Host})));}catch{}}finally{if(result!=null)result.Dispose();pending.Remove(url);if(acquired)slots.Release();}
        }
        public void Dispose(){disposed=true;cancel.Cancel();http.Dispose();foreach(var image in images.Values)image.Dispose();images.Clear();}
    }
    sealed class ListScroll:Control {
        readonly SongList list;bool dragging;int dragOffset,dragIndex,pendingIndex=-1;
        readonly System.Windows.Forms.Timer throttle=new System.Windows.Forms.Timer{Interval=32};
        public ListScroll(SongList list){this.list=list;DoubleBuffered=true;Width=14;BackColor=list.BackColor;Cursor=Cursors.Hand;TabStop=false;list.ScrollChanged+=delegate{Invalidate();};throttle.Tick+=delegate{Flush();};}
        int Page {get{return list.PageSize;}}
        int Maximum {get{return Math.Max(0,list.Items.Count-Page);}}
        int Position {get{return dragging?dragIndex:list.FirstVisibleIndex;}}
        Rectangle Thumb {get{int height=Maximum==0?Height:Math.Max(32,Height*Page/Math.Max(1,list.Items.Count));return new Rectangle(4,(Height-height)*Position/Math.Max(1,Maximum),6,height);}}
        protected override void OnPaint(PaintEventArgs e){e.Graphics.Clear(BackColor);if(Maximum==0)return;var thumb=Thumb;using(var brush=new SolidBrush(dragging?Color.FromArgb(155,226,174):Color.FromArgb(72,108,83)))e.Graphics.FillRectangle(brush,thumb);}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||Maximum==0)return;list.Focus();var thumb=Thumb;dragOffset=thumb.Contains(e.Location)?e.Y-thumb.Top:thumb.Height/2;dragIndex=list.FirstVisibleIndex;dragging=true;Capture=true;Drag(e.Y);}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging)Drag(e.Y);}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(dragging)Drag(e.Y);Flush();dragging=false;Capture=false;Invalidate();}
        protected override void OnMouseWheel(MouseEventArgs e){list.ScrollWheel(e.Delta);var handled=e as HandledMouseEventArgs;if(handled!=null)handled.Handled=true;}
        void Drag(int y){var thumb=Thumb;int range=Math.Max(1,Height-thumb.Height);dragIndex=(int)Math.Round((double)Math.Max(0,Math.Min(range,y-dragOffset))*Maximum/range);pendingIndex=dragIndex;if(!throttle.Enabled)throttle.Start();Invalidate();}
        void Flush(){throttle.Stop();if(pendingIndex<0)return;int index=pendingIndex;pendingIndex=-1;list.ScrollTo(index);}
        protected override void Dispose(bool disposing){if(disposing){throttle.Stop();throttle.Dispose();}base.Dispose(disposing);}
    }
}

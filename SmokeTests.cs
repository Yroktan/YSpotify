using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Linq;

namespace YSpotify {
    sealed class TestWindow:MainWindow{
        protected override bool ShowWithoutActivation{get{return true;}}
        public TestWindow():base(true){ShowInTaskbar=false;Opacity=0;StartPosition=FormStartPosition.Manual;Location=new Point(-20000,-20000);}
    }
    static class SmokeTests {
        [STAThread]static void Main(string[] args){
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var window=new TestWindow()){
                window.Shown+=async delegate{
                    try{if(args.Contains("--layout-only"))window.RunLayoutChecks();else await window.RunSmokeTests();Console.WriteLine("PASS: requested checks.");}
                    catch(Exception ex){Console.Error.WriteLine("FAIL: "+ex);Environment.ExitCode=1;}
                    window.Close();
                };
                Application.Run(window);
            }
        }
    }
    partial class MainWindow {
        void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        async Task<object> TestValue(string expression){
            var result=ProtocolResult(await Eval(expression));
            Require(Convert.ToString(result["type"])=="success","JavaScript evaluation failed");
            var value=result["result"] as Dictionary<string,object>;return value.ContainsKey("value")?value["value"]:null;
        }
        public async Task RunSmokeTests(){
            firefoxProfileOverride=Path.Combine(Program.Data,"Tests","Firefox-"+Guid.NewGuid().ToString("N"));
            ConfigureFirefoxProfile(false);
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
            browser=LaunchOwnedBrowser(FindBrowser(),FirefoxArguments(false,"about:blank")+" --headless",false);
            await Connect();
            Require(Convert.ToString(await TestValue("navigator.userAgent")).Contains("Firefox/"),"The backend must be Firefox");
            long baseline=PrivateWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Id);
            foreach(int ownedId in OwnedProcessIds())try{baseline+=PrivateWorkingSet(ownedId);}catch(ArgumentException){}
            Console.WriteLine("MINIMAL_FIREFOX_PRIVATE_MB="+(baseline/1024/1024));
            Console.WriteLine("PASS: isolated Firefox profile and BiDi session");
            string fixture="<main><div data-testid='tracklist-row' id='row'><a href='https://open.spotify.com/track/abc123'> Test song </a><a href='https://open.spotify.com/artist/a'>Artist</a><a href='https://open.spotify.com/artist/a'>Artist</a><a href='https://open.spotify.com/album/a'>Album</a><div>3:00</div></div><div data-testid='now-playing-widget'><a href='https://open.spotify.com/track/abc123'>Test song</a><a href='https://open.spotify.com/artist/a'>Artist</a></div><button data-testid='control-button-playpause' aria-label='Pause'></button><button data-testid='control-button-skip-forward' disabled></button><span data-testid='playback-position'>0:45</span><span data-testid='playback-duration'>3:00</span><div data-testid='user-widget-link'></div></main>";
            var json=new JavaScriptSerializer();await TestValue("document.body.innerHTML="+json.Serialize(fixture));
            string fixtureBridge=bridgeSource.Replace("if (location.hostname !== 'open.spotify.com') return;","");
            await TestValue(fixtureBridge);
            var state=json.DeserializeObject(Convert.ToString(await TestValue("JSON.stringify(window.yspotifyBridge.read(false))"))) as Dictionary<string,object>;
            var rows=state["rows"] as object[];Require(rows.Length==1,"Expected one track");
            var track=rows[0] as Dictionary<string,object>;Require(Value(track,"name")=="Test song","Track title is wrong");Require(Value(track,"artist")=="Artist","Artists must be deduplicated");Require(Value(track,"duration")=="3:00","Duration is missing");
            var player=state["player"] as Dictionary<string,object>;Require(Flag(player,"playing"),"Pause button must indicate playing");Require(!Flag(player,"canNext"),"A disabled skip button must stay disabled");Require(Math.Abs(Convert.ToDouble(player["fraction"])-0.25)<0.001,"Playback progress is wrong");
            await TestValue("window.fixturePlayed=false;document.getElementById('row').addEventListener('dblclick',()=>window.fixturePlayed=true);window.yspotifyBridge.play('spotify:track:abc123')");
            Require(object.Equals(await TestValue("window.fixturePlayed"),true),"Track activation was not delivered");
            Require(object.Equals(await TestValue("window.yspotifyBridge.play('spotify:track:missing')"),false),"Stale track references must not activate a different track");
            Console.WriteLine("PASS: real Firefox DOM fixture, playback state and track activation");
            await StopBrowserAsync();Require(OwnedProcessIds().Count==0,"Browser processes remain after shutdown");
            RunLayoutChecks();
            Console.WriteLine("PASS: owned browser processes stopped");
        }
        public void RunLayoutChecks(){
            welcome.DesignPreview();
            welcome.SetPlayback(new string('W',160),new string('W',200),true,true,true,true,0.5,"123:45 / 130:00",true);
            using(var image=new Bitmap(64,64)){
            welcome.CoverImage=delegate{return image;};welcome.SetExtraPlayback("fixture",0.8,true,true,true,false,"off");
            foreach(var size in new[]{new Size(880,640),new Size(1100,740),new Size(1240,840),new Size(1440,900),new Size(880,640)}){
                ClientSize=size;PerformLayout();welcome.PerformLayout();Application.DoEvents();Require(welcome.Width>0&&welcome.Height>0,"Invalid view bounds after resize");
                var list=ChildControls(welcome).OfType<SongList>().First();var media=ChildControls(welcome).OfType<MediaButton>().First();var footer=media.Parent.Parent;
                Require(list.RectangleToScreen(list.ClientRectangle).Bottom<=footer.RectangleToScreen(footer.ClientRectangle).Top,"Track list overlaps playback controls at "+size);
                welcome.CheckPlayerBounds();
            }
            var longList=ChildControls(welcome).OfType<SongList>().First();welcome.SetRows(Enumerable.Range(0,500).Select(index=>new SongRow{Uri="spotify:track:"+index.ToString("D22"),Name="Scroll fixture "+index}).ToList());Application.DoEvents();
            longList.ScrollTo(longList.Items.Count-1);Application.DoEvents();Require(longList.Items[longList.Items.Count-1].Bounds.Bottom<=longList.ClientSize.Height,"Last track is clipped after scrolling to the end");
            longList.ScrollTo(0);Require(longList.TopItem.Index==0,"Cannot scroll back to the first track");
            Native.PostMessage(longList.Handle,0x20a,new IntPtr(unchecked((int)0xff880000u)),IntPtr.Zero);Application.DoEvents();Require(longList.FirstVisibleIndex>0,"Mouse wheel down does not scroll");
            Native.PostMessage(longList.Handle,0x20a,new IntPtr(0x00780000),IntPtr.Zero);Application.DoEvents();Require(longList.FirstVisibleIndex==0,"Mouse wheel up does not return to the beginning");
            foreach(int position in new[]{0,200,499}){
                longList.ScrollTo(position);Application.DoEvents();using(var capture=new Bitmap(longList.ClientSize.Width,longList.ClientSize.Height)){
                    longList.DrawToBitmap(capture,longList.ClientRectangle);
                    for(int y=longList.RowsBottom;y<capture.Height;y+=2)for(int x=0;x<capture.Width;x+=3)Require(capture.GetPixel(x,y).ToArgb()==longList.BackColor.ToArgb(),"Partial or stale row painted below the last complete row");
                }
            }
            longList.ScrollTo(200);Application.DoEvents();string anchored=(longList.TopItem.Tag as SongRow).Uri;
            var refreshed=welcome.CurrentRows.ToList();refreshed.Insert(0,new SongRow{Uri="spotify:track:9999999999999999999999",Name="New favorite"});welcome.SetRows(refreshed);Application.DoEvents();
            Require((longList.TopItem.Tag as SongRow).Uri==anchored,"Refreshing favorites changed the visible song");
            welcome.CoverImage=null;
            }
            Console.WriteLine("PASS: long names, fixed covers, player controls, resize at five sizes, complete scrolling");
        }
        IEnumerable<Control> ChildControls(Control root){foreach(Control child in root.Controls){yield return child;foreach(var nested in ChildControls(child))yield return nested;}}
    }
}

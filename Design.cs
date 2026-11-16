using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YSpotify {
    sealed class SongRow {
        public string Uri,Name,Artist,Album,Duration,Cover,Uid;
        public bool Saved,CanEdit;
        public override string ToString(){return Name;}
    }
    sealed class SongList : ListView,IMessageFilter {
        public event Action<SongRow> ListenRequested;
        public event Action ScrollChanged;
        public Func<string,Image> CoverImage;
        public Func<string,Image> CachedCoverImage;
        public Control WheelSibling;
        int headerHeight=24,rowHeight=47,pageSize=1,rowsBottom,wheelRemainder;
        bool scrolling;
        readonly System.Windows.Forms.Timer settled=new System.Windows.Forms.Timer{Interval=120};
        [DllImport("user32.dll")]static extern bool ShowScrollBar(IntPtr h,int bar,bool show);
        [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,int message,IntPtr a,IntPtr b);
        [StructLayout(LayoutKind.Sequential)]struct ClientBounds{public int Left,Top,Right,Bottom;}
        [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr h,out ClientBounds bounds);
        [DllImport("user32.dll")]static extern int GetWindowLong(IntPtr h,int index);
        public int PageSize{get{return pageSize;}}
        internal int RowsBottom{get{return rowsBottom;}}
        internal int FirstVisibleIndex{get{return IsHandleCreated&&Items.Count>0?SendMessage(Handle,0x1027,IntPtr.Zero,IntPtr.Zero).ToInt32():0;}}
        void CalculatePage(){int height=Math.Max(1,rowHeight);int full=Math.Max(0,(ClientSize.Height-headerHeight)/height);pageSize=Math.Max(1,full);rowsBottom=Math.Min(ClientSize.Height,headerHeight+full*height);}
        public void RefreshViewport(){
            if(IsHandleCreated){var header=SendMessage(Handle,0x101f,IntPtr.Zero,IntPtr.Zero);ClientBounds bounds;if(header!=IntPtr.Zero&&GetClientRect(header,out bounds))headerHeight=Math.Max(0,bounds.Bottom-bounds.Top);if(Items.Count>0){int actual=Items[0].Bounds.Height;if(actual>0)rowHeight=actual;}}
            CalculatePage();if(ScrollChanged!=null)ScrollChanged();Invalidate();
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);RefreshViewport();}
        protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);CalculatePage();if(ScrollChanged!=null)ScrollChanged();}
        public void ScrollTo(int index){
            if(Items.Count==0||!IsHandleCreated)return;int target=Math.Max(0,Math.Min(Math.Max(0,Items.Count-PageSize),index)),before=FirstVisibleIndex;if(target==before)return;
            scrolling=true;settled.Stop();settled.Start();SendMessage(Handle,0x1014,IntPtr.Zero,new IntPtr((target-before)*Math.Max(1,rowHeight)));if(ScrollChanged!=null)ScrollChanged();
        }
        public void ScrollWheel(int delta){wheelRemainder+=delta;int steps=wheelRemainder/120;if(steps==0)return;wheelRemainder-=steps*120;int lines=SystemInformation.MouseWheelScrollLines;if(lines<0)lines=Math.Max(1,PageSize-1);ScrollTo(FirstVisibleIndex-steps*Math.Max(1,lines));}
        public bool PreFilterMessage(ref Message message){
            if(message.Msg!=0x20a||IsDisposed||!IsHandleCreated||!Visible||!Enabled)return false;
            var form=FindForm();var target=Control.FromChildHandle(message.HWnd);if(form==null||!form.Enabled||form.WindowState==FormWindowState.Minimized||target==null||target.FindForm()!=form)return false;
            var point=System.Windows.Forms.Cursor.Position;bool inside=RectangleToScreen(ClientRectangle).Contains(point)||WheelSibling!=null&&WheelSibling.Visible&&WheelSibling.RectangleToScreen(WheelSibling.ClientRectangle).Contains(point);if(!inside)return false;
            ScrollWheel(unchecked((short)((message.WParam.ToInt64()>>16)&0xffff)));return true;
        }
        void ClearTrailingArea(Graphics graphics){
            if(ClientSize.Width<=0||ClientSize.Height<=0)return;
            int visible=Math.Min(PageSize,Math.Max(0,Items.Count-FirstVisibleIndex));int bottom=Math.Min(RowsBottom,headerHeight+visible*Math.Max(1,rowHeight));
            if(bottom<ClientSize.Height)using(var brush=new SolidBrush(BackColor))graphics.FillRectangle(brush,0,bottom,ClientSize.Width,ClientSize.Height-bottom);
        }
        protected override void WndProc(ref Message message){
            int kind=message.Msg;
            if(kind==0x20a){ScrollWheel(unchecked((short)((message.WParam.ToInt64()>>16)&0xffff)));message.Result=IntPtr.Zero;return;}
            if((kind==0x201||kind==0x203||kind==0x204||kind==0x206)&&((short)((message.LParam.ToInt64()>>16)&0xffff))>=RowsBottom)return;
            base.WndProc(ref message);
            if(kind==0x83||kind==0x5||kind==0x115||kind==0x100||kind==0x1014){if(ScrollChanged!=null)ScrollChanged();}
            if(kind==0x115||kind==0x1014)Invalidate(true);
            if(kind==0xf){using(var graphics=CreateGraphics())ClearTrailingArea(graphics);}
            else if((kind==0x317||kind==0x318)&&message.WParam!=IntPtr.Zero){using(var graphics=Graphics.FromHdc(message.WParam))ClearTrailingArea(graphics);}
        }
        ListViewItem hoveredPlay,pressedPlay;
        string pendingUri="",playingUri="";bool rowPlaying;float rowPressure,targetRowPressure;int rowAngle;
        readonly System.Windows.Forms.Timer rowFeedback=new System.Windows.Forms.Timer{Interval=16};
        void InvalidateRow(ListViewItem item){if(item!=null&&item.ListView==this&&item.Bounds.Bottom>headerHeight&&item.Bounds.Top<RowsBottom)Invalidate(item.Bounds);}
        void InvalidateUri(string uri){if(string.IsNullOrEmpty(uri))return;foreach(ListViewItem item in Items)if((item.Tag as SongRow)!=null&&(item.Tag as SongRow).Uri==uri){InvalidateRow(item);break;}}
        void AnimateRow(){rowFeedback.Interval=Math.Abs(rowPressure-targetRowPressure)>0.01f?16:80;if(pendingUri.Length>0||Math.Abs(rowPressure-targetRowPressure)>0.01f)rowFeedback.Start();else rowFeedback.Stop();}
        public void BeginPlayback(string uri){var old=pendingUri;pendingUri=uri??"";InvalidateUri(old);InvalidateUri(pendingUri);AnimateRow();}
        public void SetRowPlayback(string uri,bool playing,bool opening){var old=playingUri;playingUri=uri??"";rowPlaying=playing;if(!opening){var pending=pendingUri;pendingUri="";InvalidateUri(pending);}InvalidateUri(old);InvalidateUri(playingUri);AnimateRow();}
        static Rectangle PlayBounds(Rectangle row){return new Rectangle(row.Left+8,row.Top+(row.Height-30)/2,30,30);}
        bool CanListen(ListViewItem item){var song=item==null?null:item.Tag as SongRow;return song!=null&&song.Uri.StartsWith("spotify:track:");}
        readonly Font titleFont=new Font("Segoe UI Semibold",10.5f);
        readonly Font detailFont=new Font("Segoe UI",9.5f);
        readonly Font headerFont=new Font("Segoe UI",8.5f);
        readonly ImageList spacing=new ImageList();
        readonly Bitmap spacer=new Bitmap(1,46);
        public SongList(){
            DoubleBuffered=true;OwnerDraw=true;
            rowFeedback.Tick+=delegate{rowPressure+=(targetRowPressure-rowPressure)*0.4f;if(Math.Abs(rowPressure-targetRowPressure)<0.01f)rowPressure=targetRowPressure;rowAngle=(rowAngle+24)%360;InvalidateRow(pressedPlay);InvalidateUri(pendingUri);AnimateRow();};
            Application.AddMessageFilter(this);settled.Tick+=delegate{settled.Stop();scrolling=false;Invalidate();};
            spacing.ImageSize=new Size(1,46);spacing.ColorDepth=ColorDepth.Depth32Bit;
            spacing.Images.Add(spacer);
            SmallImageList=spacing;
            DrawColumnHeader+=delegate(object s,DrawListViewColumnHeaderEventArgs e){
                if(headerHeight!=e.Bounds.Bottom){headerHeight=e.Bounds.Bottom;CalculatePage();}
                using(var b=new SolidBrush(Color.FromArgb(21,32,25)))e.Graphics.FillRectangle(b,e.Bounds);
                var rect=e.Bounds;rect.Inflate(-14,0);
                TextRenderer.DrawText(e.Graphics,e.Header.Text.ToUpperInvariant(),headerFont,rect,Color.FromArgb(126,150,134),TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            };
            DrawItem+=delegate(object s,DrawListViewItemEventArgs e){if(View!=View.Details)e.DrawDefault=true;};
            DrawSubItem+=delegate(object s,DrawListViewSubItemEventArgs e){
                if(e.Bounds.Bottom>RowsBottom){using(var blank=new SolidBrush(BackColor))e.Graphics.FillRectangle(blank,e.Bounds);return;}
                bool selected=e.Item.Selected;
                using(var b=new SolidBrush(selected?Color.FromArgb(33,57,40):BackColor))e.Graphics.FillRectangle(b,e.Bounds);
                if(e.ColumnIndex==0&&selected)using(var b=new SolidBrush(Color.FromArgb(143,216,163)))e.Graphics.FillRectangle(b,e.Bounds.X,e.Bounds.Y+14,3,e.Bounds.Height-28);
                var rect=e.Bounds;rect.Inflate(-14,0);
                if(e.ColumnIndex==0&&CanListen(e.Item)){
                    var button=PlayBounds(e.Bounds);int x=button.Left+button.Width/2,y=button.Top+button.Height/2;
                    e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
                    var row=e.Item.Tag as SongRow;bool opening=row.Uri==pendingUri;float pressure=e.Item==pressedPlay?rowPressure:0;
                    if(hoveredPlay==e.Item||opening||pressure>0||row.Uri==playingUri&&rowPlaying)using(var b=new SolidBrush(pressure>0?Color.FromArgb(69,116,82):Color.FromArgb(48,81,57)))e.Graphics.FillEllipse(b,button.X+pressure*2,button.Y+pressure*2,button.Width-pressure*4,button.Height-pressure*4);
                    if(opening)using(var pen=new Pen(Color.FromArgb(144,218,165),2))e.Graphics.DrawArc(pen,x-7,y-7,14,14,rowAngle,250);
                    else using(var b=new SolidBrush(Color.FromArgb(144,218,165))){int extent=pressure>0.3f?5:6;e.Graphics.FillPolygon(b,new[]{new Point(x-3,y-extent),new Point(x-3,y+extent),new Point(x+extent,y)});}
                    int left=e.Bounds.Left+46,top=e.Bounds.Top+(e.Bounds.Height-32)/2;
                    var song=e.Item.Tag as SongRow;var loader=scrolling&&CachedCoverImage!=null?CachedCoverImage:CoverImage;var cover=loader==null?null:loader(song.Cover);
                    if(cover!=null)e.Graphics.DrawImage(cover,new Rectangle(left,top,32,32));
                    else using(var brush=new SolidBrush(Color.FromArgb(33,53,40)))e.Graphics.FillRectangle(brush,left,top,32,32);
                    rect.X+=76;rect.Width=Math.Max(0,rect.Width-76);
                }
                var flags=TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine;
                if(e.ColumnIndex==3)flags|=TextFormatFlags.Right;
                TextRenderer.DrawText(e.Graphics,e.SubItem.Text,e.ColumnIndex==0?titleFont:detailFont,rect,e.ColumnIndex==0?Color.FromArgb(229,239,231):Color.FromArgb(144,166,151),flags);
                using(var p=new Pen(Color.FromArgb(26,40,31)))e.Graphics.DrawLine(p,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
            };
        }
        protected override void OnMouseMove(MouseEventArgs e){
            base.OnMouseMove(e);var item=HitTest(e.Location).Item;
            var next=e.Y<RowsBottom&&CanListen(item)&&PlayBounds(item.Bounds).Contains(e.Location)?item:null;
            if(next!=hoveredPlay){var old=hoveredPlay;hoveredPlay=next;if(old!=null)Invalidate(old.Bounds);if(next!=null)Invalidate(next.Bounds);Cursor=next==null?Cursors.Default:Cursors.Hand;}
        }
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);if(hoveredPlay!=null){var old=hoveredPlay;hoveredPlay=null;InvalidateRow(old);}targetRowPressure=0;AnimateRow();Cursor=Cursors.Default;}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);var item=e.Y<RowsBottom?HitTest(e.Location).Item:null;if(e.Button==MouseButtons.Left&&CanListen(item)&&PlayBounds(item.Bounds).Contains(e.Location)){pressedPlay=item;rowPressure=0.7f;targetRowPressure=1;AnimateRow();InvalidateRow(item);}}
        protected override void OnMouseUp(MouseEventArgs e){
            targetRowPressure=0;AnimateRow();
            if(e.Y>=RowsBottom)return;base.OnMouseUp(e);if(e.Button!=MouseButtons.Left||e.Clicks>1)return;
            var item=HitTest(e.Location).Item;if(CanListen(item)&&PlayBounds(item.Bounds).Contains(e.Location)&&ListenRequested!=null)ListenRequested(item.Tag as SongRow);
        }
        protected override void OnDoubleClick(EventArgs e){var point=PointToClient(System.Windows.Forms.Cursor.Position);if(point.Y>=RowsBottom)return;var item=HitTest(point).Item;if(CanListen(item)&&PlayBounds(item.Bounds).Contains(point))return;base.OnDoubleClick(e);}
        protected override void Dispose(bool disposing){if(disposing){Application.RemoveMessageFilter(this);rowFeedback.Stop();rowFeedback.Dispose();settled.Stop();settled.Dispose();titleFont.Dispose();detailFont.Dispose();headerFont.Dispose();spacing.Dispose();spacer.Dispose();}base.Dispose(disposing);}
    }
    sealed class SeekLine : Control {
        int value;
        bool dragging;
        public event Action<int> Changed;
        public bool Interactive=true;
        public int Value{get{return value;}set{if(!dragging){this.value=Math.Max(0,Math.Min(1000,value));Invalidate();}}}
        public SeekLine(){DoubleBuffered=true;SetStyle(ControlStyles.ResizeRedraw,true);Height=16;Cursor=Cursors.Hand;TabStop=true;AccessibleRole=AccessibleRole.Slider;}
        void SetPointer(int x,bool emit){value=Math.Max(0,Math.Min(1000,(x-3)*1000/Math.Max(1,Width-7)));Invalidate();if(emit&&Changed!=null)Changed(value);}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(Interactive&&Enabled&&e.Button==MouseButtons.Left){Focus();dragging=true;Capture=true;SetPointer(e.X,false);}}
        protected override void OnMouseMove(MouseEventArgs e){base.OnMouseMove(e);if(dragging)SetPointer(e.X,false);}
        protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(dragging){dragging=false;Capture=false;SetPointer(e.X,true);}}
        protected override bool IsInputKey(Keys key){return key==Keys.Left||key==Keys.Right||base.IsInputKey(key);}
        protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(!Interactive||!Enabled)return;if(e.KeyCode==Keys.Left||e.KeyCode==Keys.Right){Value+=e.KeyCode==Keys.Right?25:-25;if(Changed!=null)Changed(Value);e.Handled=true;}}
        protected override void OnPaint(PaintEventArgs e){
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;int y=Height/2;
            using(var p=new Pen(Color.FromArgb(48,70,55),4)){p.StartCap=p.EndCap=LineCap.Round;e.Graphics.DrawLine(p,3,y,Math.Max(3,Width-4),y);}
            if(value>0)using(var p=new Pen(Color.FromArgb(141,216,162),4)){p.StartCap=p.EndCap=LineCap.Round;e.Graphics.DrawLine(p,3,y,Math.Max(3,(Width-4)*value/1000),y);}
            if(Interactive&&Enabled)using(var b=new SolidBrush(Color.FromArgb(179,238,195)))e.Graphics.FillEllipse(b,Math.Max(0,(Width-7)*value/1000),y-3,6,6);
        }
    }
    sealed class MediaButton : Button {
        readonly string role;
        readonly System.Windows.Forms.Timer feedback=new System.Windows.Forms.Timer{Interval=16};
        bool hovered,busy;float pressure,targetPressure;int angle;
        public bool Busy{get{return busy;}set{busy=value;Animate();Invalidate();}}
        void Animate(){feedback.Interval=Math.Abs(pressure-targetPressure)>0.01f?16:80;if(busy||Math.Abs(pressure-targetPressure)>0.01f)feedback.Start();else feedback.Stop();}
        public MediaButton(string role){
            this.role=role;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
            FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;Size=new Size(role=="play"?48:36,48);
            AccessibleName=role=="play"?"Воспроизведение / пауза":role=="previous"?"Предыдущий трек":"Следующий трек";
            feedback.Tick+=delegate{pressure+=(targetPressure-pressure)*0.4f;if(Math.Abs(pressure-targetPressure)<0.01f)pressure=targetPressure;if(busy)angle=(angle+24)%360;Invalidate();Animate();};
        }
        protected override void OnMouseEnter(EventArgs e){base.OnMouseEnter(e);hovered=true;Invalidate();}
        protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);hovered=false;targetPressure=0;Animate();Invalidate();}
        protected override void OnMouseDown(MouseEventArgs e){base.OnMouseDown(e);if(Enabled&&e.Button==MouseButtons.Left){pressure=0.7f;targetPressure=1;Animate();Invalidate();}}
        protected override void OnMouseUp(MouseEventArgs e){targetPressure=0;Animate();Invalidate();base.OnMouseUp(e);}
        protected override void OnKeyDown(KeyEventArgs e){if(e.KeyCode==Keys.Space||e.KeyCode==Keys.Enter){pressure=0.7f;targetPressure=1;Animate();Invalidate();}base.OnKeyDown(e);}
        protected override void OnKeyUp(KeyEventArgs e){targetPressure=0;Animate();Invalidate();base.OnKeyUp(e);}
        protected override void OnPaint(PaintEventArgs e){
            var g=e.Graphics;g.Clear(BackColor);g.SmoothingMode=SmoothingMode.AntiAlias;
            var original=g.Save();float scale=1-pressure*0.09f;g.TranslateTransform(Width/2f,Height/2f);g.ScaleTransform(scale,scale);g.TranslateTransform(-Width/2f,-Height/2f);
            Color ink=Enabled?(hovered||pressure>0?Color.FromArgb(153,232,179):Color.FromArgb(209,227,214)):Color.FromArgb(78,102,86);
            if(role!="play"&&hovered&&Enabled)using(var hover=new SolidBrush(Color.FromArgb(36,62,44)))g.FillEllipse(hover,Width/2-15,Height/2-15,30,30);
            if(role=="play"){
                using(var b=new SolidBrush(Enabled?(hovered?Color.FromArgb(170,235,188):Color.FromArgb(146,219,166)):Color.FromArgb(70,104,80)))g.FillEllipse(b,2,2,Width-4,Height-4);
                using(var b=new SolidBrush(Color.FromArgb(17,38,24))){
                    if(busy){using(var spinner=new Pen(Color.FromArgb(17,38,24),2.5f))g.DrawArc(spinner,15,15,18,18,angle,250);}
                    else if(Text=="Ⅱ"){g.FillRectangle(b,18,16,4,16);g.FillRectangle(b,27,16,4,16);}
                    else g.FillPolygon(b,new[]{new Point(20,15),new Point(20,33),new Point(33,24)});
                }
            }else using(var p=new Pen(ink,2)){
                int x=Width/2,y=Height/2;
                if(role=="previous"){g.DrawLine(p,x-7,y-7,x-7,y+7);g.DrawPolygon(p,new[]{new Point(x+5,y-7),new Point(x-4,y),new Point(x+5,y+7)});}
                else{g.DrawLine(p,x+7,y-7,x+7,y+7);g.DrawPolygon(p,new[]{new Point(x-5,y-7),new Point(x+4,y),new Point(x-5,y+7)});}
            }
            g.Restore(original);
            if(Focused)ControlPaint.DrawFocusRectangle(g,new Rectangle(0,0,Width-1,Height-1));
        }
        protected override void Dispose(bool disposing){if(disposing){feedback.Stop();feedback.Dispose();}base.Dispose(disposing);}
    }
    class Welcome : Panel {
        public event EventHandler Launch;
        public event Action<string> SearchRequested,PageRequested;
        public event Action<SongRow> SongRequested;
        public event Action PlayRequested,NextRequested,PreviousRequested;
        public event Action<double> VolumeRequested,SeekRequested;
        public event Action<string> ModeRequested;
        public event Action<SongRow,Point> SongMenuRequested;
        public Func<string,Image> CoverImage;
        public Func<string,Image> CachedCoverImage;
        readonly Color accent=Color.FromArgb(142,217,164),bg=Color.FromArgb(17,26,20),muted=Color.FromArgb(134,158,141);
        readonly TextBox search;
        readonly SongList songs;
        readonly Label heading,description,empty,now,artist,time;
        readonly FlatButton account;
        readonly MediaButton play,previous,next;
        readonly SeekLine progress,volume;
        readonly PictureBox cover;
        string displayedCover;
        readonly FlatButton shuffle,repeat;
        readonly FlowLayoutPanel playlists;
        readonly Dictionary<string,FlatButton> tabs=new Dictionary<string,FlatButton>();
        string signature="";
        bool previewMode;
        string playlistSignature="";
        List<SongRow> currentRows=new List<SongRow>();
        public List<SongRow> CurrentRows{get{return currentRows;}}
        public bool HasRows{get{return songs.Items.Count>0;}}
        public bool PlaybackBusy{get{return play.Busy;}}
        public void SetPlaybackBusy(string role,bool value){play.Busy=value;previous.Busy=value&&role=="previous";next.Busy=value&&role=="next";if(!value)songs.BeginPlayback("");}
        public void BeginTrackPlayback(string uri){songs.BeginPlayback(uri);}
        public void SetRowPlayback(string uri,bool playing,bool opening){songs.SetRowPlayback(uri,playing,opening);}
        public Welcome(){
            DoubleBuffered=true;BackColor=bg;ForeColor=Color.FromArgb(231,239,232);
            var rail=new Panel{Dock=DockStyle.Left,Width=198,BackColor=Color.FromArgb(12,20,16),Padding=new Padding(16,22,16,16)};Controls.Add(rail);
            var stack=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};rail.Controls.Add(stack);
            stack.Controls.Add(Caption("МОЯ МУЗЫКА",new Padding(11,0,0,16)));
            foreach(string text in new[]{"Поиск","Любимое","Плейлисты"}){
                var button=new FlatButton(text){Width=164,Height=42,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,0,0),BackColor=rail.BackColor,Margin=new Padding(0,0,0,6)};
                tabs.Add(text,button);button.Click+=delegate{SelectTab(text);if(!previewMode&&PageRequested!=null)PageRequested(text);};stack.Controls.Add(button);
            }
            stack.Controls.Add(Caption("ПЛЕЙЛИСТЫ",new Padding(11,34,0,13)));
            playlists=new FlowLayoutPanel{Width=166,Height=240,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=Padding.Empty};stack.Controls.Add(playlists);
            var playlistHint=new Label{Width=152,Height=58,Text="Здесь появятся\nтвои плейлисты",ForeColor=Color.FromArgb(91,118,100),Font=new Font("Segoe UI",9),Padding=new Padding(11,0,0,0)};playlists.Controls.Add(playlistHint);
            var body=new Panel{Dock=DockStyle.Fill,Padding=new Padding(32,22,32,12)};Controls.Add(body);body.BringToFront();
            var top=new TableLayoutPanel{Dock=DockStyle.Top,Height=55,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,89));top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,148));
            search=new TextBox{Dock=DockStyle.Fill,BackColor=Color.FromArgb(25,40,30),ForeColor=ForeColor,BorderStyle=BorderStyle.FixedSingle,Font=new Font("Segoe UI",12),Margin=new Padding(0,5,12,0)};
            search.HandleCreated+=delegate{SetCue(search.Handle,0x1501,new IntPtr(1),"Трек, исполнитель или альбом");};
            search.KeyDown+=delegate(object s,KeyEventArgs e){if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;DoSearch();}};
            var find=new FlatButton("Найти"){Dock=DockStyle.Top,Height=36,BackColor=Color.FromArgb(35,57,42),Margin=new Padding(0,3,12,0)};find.Click+=delegate{DoSearch();};
            account=new FlatButton("Войти в браузере"){Dock=DockStyle.Top,Height=36,BackColor=accent,ForeColor=Color.FromArgb(15,37,23),Margin=new Padding(0,3,0,0),Font=new Font("Segoe UI Semibold",9.5f)};account.Click+=delegate{if(!previewMode&&Launch!=null)Launch(this,EventArgs.Empty);};
            top.Controls.Add(search,0,0);top.Controls.Add(find,1,0);top.Controls.Add(account,2,0);body.Controls.Add(top);
            var headingArea=new Panel{Dock=DockStyle.Top,Height=101,Padding=new Padding(0,13,0,12)};body.Controls.Add(headingArea);headingArea.BringToFront();
            heading=new Label{Dock=DockStyle.Top,Height=43,Text="Поиск",Font=new Font("Segoe UI Semibold",24),TextAlign=ContentAlignment.MiddleLeft};
            description=new Label{Dock=DockStyle.Top,Height=26,Text="Найди то, что хочется послушать",ForeColor=muted,Font=new Font("Segoe UI",10),TextAlign=ContentAlignment.MiddleLeft};headingArea.Controls.Add(description);headingArea.Controls.Add(heading);
            body.Controls.SetChildIndex(top,body.Controls.Count-1);
            var listArea=new Panel{Dock=DockStyle.Fill};body.Controls.Add(listArea);listArea.BringToFront();
            songs=new SongList{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,BorderStyle=BorderStyle.None,BackColor=bg,ForeColor=ForeColor,HeaderStyle=ColumnHeaderStyle.Nonclickable};
            songs.Columns.Add("Трек",250);songs.Columns.Add("Исполнитель",180);songs.Columns.Add("Альбом",170);songs.Columns.Add("Время",62,HorizontalAlignment.Right);
            songs.ListenRequested+=delegate(SongRow song){if(!previewMode&&SongRequested!=null)SongRequested(song);};
            songs.CoverImage=delegate(string url){return CoverImage==null?null:CoverImage(url);};
            songs.CachedCoverImage=delegate(string url){return CachedCoverImage==null?null:CachedCoverImage(url);};
            songs.MouseUp+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Right&&!previewMode&&SongMenuRequested!=null){var item=songs.HitTest(e.Location).Item;if(item!=null){item.Selected=true;SongMenuRequested(item.Tag as SongRow,songs.PointToScreen(e.Location));}}};
            songs.KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Apps&&songs.SelectedItems.Count>0&&SongMenuRequested!=null){SongMenuRequested(songs.SelectedItems[0].Tag as SongRow,songs.PointToScreen(songs.SelectedItems[0].Bounds.Location));e.Handled=true;}};
            songs.DoubleClick+=delegate{PlaySelection();};songs.KeyDown+=delegate(object s,KeyEventArgs e){if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;PlaySelection();}};
            var viewport=new Panel{Dock=DockStyle.Fill,BackColor=bg,Margin=Padding.Empty};listArea.Controls.Add(viewport);viewport.Controls.Add(songs);songs.Dock=DockStyle.None;
            viewport.SizeChanged+=delegate{songs.SetBounds(0,0,viewport.ClientSize.Width+SystemInformation.VerticalScrollBarWidth,viewport.ClientSize.Height);FitColumns();songs.RefreshViewport();};
            empty=new Label{Dock=DockStyle.Fill,Text="Войди, чтобы открыть свою музыку.\nИли начни с поиска.",TextAlign=ContentAlignment.MiddleCenter,ForeColor=muted,Font=new Font("Segoe UI",11)};listArea.Controls.Add(empty);empty.BringToFront();
            var scroll=new ListScroll(songs){Dock=DockStyle.Right};songs.WheelSibling=scroll;listArea.Controls.Add(scroll);scroll.BringToFront();
            listArea.SizeChanged+=delegate{FitColumns();};
            var footer=new TableLayoutPanel{Dock=DockStyle.Bottom,Height=104,ColumnCount=3,RowCount=1,Padding=new Padding(28,20,28,14),BackColor=Color.FromArgb(20,35,25)};
            footer.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,43));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,220));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,57));Controls.Add(footer);footer.BringToFront();
            var info=new Panel{Dock=DockStyle.Fill,Margin=Padding.Empty};
            now=new Label{Text="Ничего не играет",AutoEllipsis=true,Font=new Font("Segoe UI Semibold",12),TextAlign=ContentAlignment.MiddleLeft};
            artist=new Label{Text="",AutoEllipsis=true,ForeColor=muted,Font=new Font("Segoe UI",9.5f),TextAlign=ContentAlignment.MiddleLeft};
            cover=new PictureBox{Size=new Size(48,48),SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(31,51,38),TabStop=false};info.Controls.Add(cover);info.Controls.Add(now);info.Controls.Add(artist);footer.Controls.Add(info,0,0);
            info.SizeChanged+=delegate{int coverY=Math.Max(0,(info.ClientSize.Height-48)/2),textWidth=Math.Max(0,info.ClientSize.Width-62);cover.SetBounds(0,coverY,48,48);now.SetBounds(62,coverY,textWidth,26);artist.SetBounds(62,coverY+27,textWidth,21);};
            var controls=new FlowLayoutPanel{Size=new Size(176,48),Anchor=AnchorStyles.None,Margin=Padding.Empty,WrapContents=false,Padding=Padding.Empty};
            previous=new MediaButton("previous"){BackColor=footer.BackColor,Enabled=false};previous.Click+=delegate{if(!previewMode&&PreviousRequested!=null)PreviousRequested();};
            play=new MediaButton("play"){BackColor=footer.BackColor,Enabled=false};play.Click+=delegate{if(!previewMode&&PlayRequested!=null)PlayRequested();};
            next=new MediaButton("next"){BackColor=footer.BackColor,Enabled=false};next.Click+=delegate{if(!previewMode&&NextRequested!=null)NextRequested();};
            shuffle=new FlatButton("⤨"){Size=new Size(28,48),Margin=Padding.Empty,BackColor=footer.BackColor,Font=new Font("Segoe UI",17),AccessibleName="Перемешать",Enabled=false};shuffle.Click+=delegate{if(!previewMode&&ModeRequested!=null)ModeRequested("shuffle");};
            repeat=new FlatButton("↻"){Size=new Size(28,48),Margin=Padding.Empty,BackColor=footer.BackColor,Font=new Font("Segoe UI",17),AccessibleName="Повтор",Enabled=false};repeat.Click+=delegate{if(!previewMode&&ModeRequested!=null)ModeRequested("repeat");};
            previous.Margin=play.Margin=next.Margin=Padding.Empty;
            controls.Controls.Add(shuffle);controls.Controls.Add(previous);controls.Controls.Add(play);controls.Controls.Add(next);controls.Controls.Add(repeat);footer.Controls.Add(controls,1,0);
            var timeline=new Panel{Dock=DockStyle.Fill,Padding=new Padding(28,11,0,0)};
            progress=new SeekLine{Dock=DockStyle.Top,BackColor=footer.BackColor,AccessibleName="Позиция трека",Enabled=false};progress.Changed+=delegate(int value){if(!previewMode&&SeekRequested!=null)SeekRequested(value/1000.0);};
            var volumeRow=new Panel{Dock=DockStyle.Top,Height=26};time=new Label{Dock=DockStyle.Right,Width=94,Text="0:00 / 0:00",ForeColor=muted,TextAlign=ContentAlignment.MiddleRight,Font=new Font("Segoe UI",8.5f)};
            volume=new SeekLine{Dock=DockStyle.Fill,BackColor=footer.BackColor,Value=800,AccessibleName="Громкость"};volume.Changed+=delegate(int value){if(!previewMode&&VolumeRequested!=null)VolumeRequested(value/1000.0);};
            var volumeLabel=new Label{Dock=DockStyle.Left,Width=26,Text="♪",ForeColor=muted,TextAlign=ContentAlignment.MiddleLeft};volumeRow.Controls.Add(volume);volumeRow.Controls.Add(time);volumeRow.Controls.Add(volumeLabel);timeline.Controls.Add(volumeRow);timeline.Controls.Add(progress);footer.Controls.Add(timeline,2,0);
            SelectTab("Поиск");
            body.BringToFront();
        }
        Label Caption(string text,Padding margin){return new Label{Text=text,ForeColor=Color.FromArgb(98,126,107),Font=new Font("Segoe UI Semibold",8),Width=155,Height=18,Margin=margin};}
        void SelectTab(string name){foreach(var pair in tabs)pair.Value.BackColor=pair.Key==name?Color.FromArgb(30,55,38):Color.FromArgb(12,20,16);}
        void DoSearch(){if(!previewMode&&!string.IsNullOrWhiteSpace(search.Text)&&SearchRequested!=null)SearchRequested(search.Text.Trim());}
        void PlaySelection(){if(!previewMode&&songs.SelectedItems.Count>0&&SongRequested!=null)SongRequested(songs.SelectedItems[0].Tag as SongRow);}
        void FitColumns(){int w=Math.Max(280,songs.Parent==null?songs.ClientSize.Width:songs.Parent.ClientSize.Width);songs.Columns[0].Width=(w-84)*37/100;songs.Columns[1].Width=(w-84)*27/100;songs.Columns[2].Width=(w-84)*36/100;songs.Columns[3].Width=w-songs.Columns[0].Width-songs.Columns[1].Width-songs.Columns[2].Width;}
        public void SetHeading(string text){heading.Text=text;description.Text=text=="Поиск"?"Найди то, что хочется послушать":"Твоя музыка";}
        public void Loading(string message){signature="";songs.Items.Clear();empty.Text=message;empty.Visible=true;}
        public void SetRows(List<SongRow> rows){
            currentRows=rows;
            string nextSignature=string.Join("|",rows.ConvertAll(x=>x.Uri+":"+x.Name+":"+x.Artist+":"+x.Album+":"+x.Duration+":"+x.Cover+":"+x.Uid+":"+x.Saved+":"+x.CanEdit).ToArray());if(nextSignature==signature&&rows.Count>0)return;signature=nextSignature;
            string selected=songs.SelectedItems.Count>0?(songs.SelectedItems[0].Tag as SongRow).Uri:null;
            string top=songs.Items.Count>0&&songs.TopItem!=null?(songs.TopItem.Tag as SongRow).Uri:null;
            songs.BeginUpdate();try{songs.Items.Clear();foreach(var row in rows){var item=new ListViewItem(new[]{row.Name,row.Artist,row.Album,row.Duration}){Tag=row};songs.Items.Add(item);if(row.Uri==selected)item.Selected=true;}}finally{songs.EndUpdate();}
            empty.Visible=rows.Count==0;if(rows.Count==0)empty.Text=heading.Text=="Результаты поиска"?"Ничего не нашлось.\nПопробуй другой запрос.":"Здесь пока нет треков";FitColumns();songs.RefreshViewport();
            if(top!=null){int index=rows.FindIndex(row=>row.Uri==top);if(index>=0)songs.ScrollTo(index);}
        }
        public void SetPlayback(string name,string performer,bool playing,bool canPlay,bool canPrevious,bool canNext,double fraction,string timestamps,bool loggedIn){now.Text=string.IsNullOrWhiteSpace(name)?"Ничего не играет":name;artist.Text=performer;play.Text=playing?"Ⅱ":"▶";play.Enabled=canPlay;previous.Enabled=canPrevious;next.Enabled=canNext;progress.Value=Math.Max(0,Math.Min(1000,(int)(fraction*1000)));time.Text=timestamps;account.Text=loggedIn?"Аккаунт":"Войти в браузере";}
        public void SetAuthentication(bool pending){account.Text=pending?"Я вошёл":"Войти в браузере";}
        public void SetExtraPlayback(string image,double loudness,bool canSeek,bool canShuffle,bool canRepeat,bool shuffled,string repeatMode){
            var thumbnail=CoverImage==null?null:CoverImage(image);
            if(displayedCover!=image||cover.Image==null){var previous=cover.Image;cover.Image=thumbnail==null?null:new Bitmap(thumbnail);displayedCover=image;if(previous!=null)previous.Dispose();}
            volume.Value=(int)(loudness*1000);progress.Enabled=canSeek;shuffle.Enabled=canShuffle;repeat.Enabled=canRepeat;shuffle.ForeColor=shuffled?accent:muted;repeat.ForeColor=repeatMode!="off"?accent:muted;repeat.Text=repeatMode=="track"?"↻¹":"↻";
        }
        protected override void Dispose(bool disposing){if(disposing&&cover!=null&&cover.Image!=null){var image=cover.Image;cover.Image=null;image.Dispose();}base.Dispose(disposing);}
        public void RefreshCovers(){songs.Invalidate();}
        internal void CheckPlayerBounds(){
            if(cover.Width!=48||cover.Height!=48)throw new InvalidOperationException("Cover size changed");
            foreach(Control child in new Control[]{cover,now,artist})if(!child.Parent.ClientRectangle.Contains(child.Bounds))throw new InvalidOperationException("Player metadata overflow");
            if(cover.Bounds.IntersectsWith(now.Bounds)||cover.Bounds.IntersectsWith(artist.Bounds)||now.Bounds.IntersectsWith(artist.Bounds))throw new InvalidOperationException("Player metadata overlap");
            foreach(Control child in new Control[]{play,previous,next,shuffle,repeat,progress,volume})if(!child.Parent.ClientRectangle.Contains(child.Bounds))throw new InvalidOperationException("Player control overflow: "+child.AccessibleName);
            var coverBox=cover.RectangleToScreen(cover.ClientRectangle);var pauseBox=play.RectangleToScreen(play.ClientRectangle);
            if(Math.Abs(coverBox.Top+coverBox.Height/2-pauseBox.Top-pauseBox.Height/2)>1)throw new InvalidOperationException("Cover and pause are not aligned");
            foreach(Control cell in cover.Parent.Parent.Controls)if(!cell.Parent.DisplayRectangle.Contains(cell.Bounds))throw new InvalidOperationException("Player cell overflows footer");
        }
        public void SetPlaylists(List<SongRow> rows){
            string next=string.Join("|",rows.ConvertAll(x=>x.Uri+":"+x.Name+":"+x.CanEdit).ToArray());if(next==playlistSignature)return;playlistSignature=next;
            while(playlists.Controls.Count>0)playlists.Controls[0].Dispose();
            int count=0;foreach(var row in rows.OrderByDescending(row=>row.CanEdit)){if(!row.Uri.StartsWith("spotify:playlist:"))continue;if(count++>=6)break;
                var item=new FlatButton(row.Name){Width=166,Height=36,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,0,0),Margin=new Padding(0,0,0,4),BackColor=Color.FromArgb(12,20,16),ForeColor=muted,Font=new Font("Segoe UI",9.5f),AutoEllipsis=true};
                var chosen=row;item.Click+=delegate{if(SongRequested!=null)SongRequested(chosen);};playlists.Controls.Add(item);
            }
        }
        public void DesignPreview(){
            previewMode=true;SelectTab("Плейлисты");heading.Text="Вечерний поток";description.Text="Плейлист  ·  8 треков";
            playlists.Controls.Clear();foreach(string name in new[]{"Вечерний поток","В дорогу","Без слов"}){
                var item=new FlatButton(name){Width=166,Height=38,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,0,0),Margin=new Padding(0,0,0,4),BackColor=name=="Вечерний поток"?Color.FromArgb(23,42,30):Color.FromArgb(12,20,16),ForeColor=name=="Вечерний поток"?accent:muted,Font=new Font("Segoe UI",9.5f)};playlists.Controls.Add(item);
            }
            string[,] data={{"After Dark","Mr.Kitty","Time","4:17"},{"Resonance","HOME","Odyssey","3:32"},{"A Walk","Tycho","Dive","5:17"},{"Nightcall","Kavinsky","OutRun","4:18"},{"Weightless","Marconi Union","Weightless","8:09"},{"Sunset Lover","Petit Biscuit","Petit Biscuit","3:58"},{"Nights","Frank Ocean","Blonde","5:07"},{"Awake","Tycho","Awake","4:43"}};
            var rows=new List<SongRow>();for(int n=0;n<data.GetLength(0);n++)rows.Add(new SongRow{Uri="preview:"+n,Name=data[n,0],Artist=data[n,1],Album=data[n,2],Duration=data[n,3]});SetRows(rows);
            songs.Items[1].Selected=true;SetPlayback("Resonance","HOME",false,true,true,true,0.34,"1:12 / 3:32",false);
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]static extern IntPtr SetCue(IntPtr window,uint message,IntPtr wParam,string text);
    }
}

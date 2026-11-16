using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace YSpotify {
    sealed class BrandHeader : Panel {
        readonly Bitmap outline;
        public BrandHeader(){
            DoubleBuffered=true;SetStyle(ControlStyles.ResizeRedraw,true);BackColor=Color.FromArgb(12,20,16);
            string path=Path.Combine(Program.Root,"branding","utsuho-outline-v2.png");
            if(File.Exists(path))using(var source=Image.FromFile(path)){
                int width=Math.Min(1000,source.Width),height=(int)Math.Round(source.Height*(width/(double)source.Width));
                outline=new Bitmap(width,height,PixelFormat.Format32bppPArgb);
                using(var canvas=Graphics.FromImage(outline)){
                    canvas.CompositingMode=CompositingMode.SourceCopy;canvas.InterpolationMode=InterpolationMode.HighQualityBicubic;
                    canvas.DrawImage(source,new Rectangle(0,0,width,height));
                }
            }
        }
        protected override void OnPaint(PaintEventArgs e){
            base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var line=new Pen(Color.FromArgb(32,47,37)))g.DrawLine(line,0,Height-1,Width,Height-1);
            using(var f=new Font("Segoe UI Semibold",23))TextRenderer.DrawText(g,"Y",f,new Point(26,46),Color.FromArgb(145,217,165),TextFormatFlags.NoPadding);
            using(var f=new Font("Segoe UI Semibold",23))TextRenderer.DrawText(g,"Spotify",f,new Point(47,46),Color.FromArgb(232,240,234),TextFormatFlags.NoPadding);
            if(outline==null||Width<990)return;
            int imageHeight=Height-8,imageWidth=(int)Math.Round(imageHeight*(outline.Width/(double)outline.Height));
            var target=new Rectangle(Width/2-75,4,imageWidth,imageHeight);
            using(var ink=new ImageAttributes()){
                var tint=new ColorMatrix();tint.Matrix33=0.64f;ink.SetColorMatrix(tint);
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                g.DrawImage(outline,target,0,0,outline.Width,outline.Height,GraphicsUnit.Pixel,ink);
            }
        }
        protected override void Dispose(bool disposing){if(disposing&&outline!=null)outline.Dispose();base.Dispose(disposing);}
    }
}

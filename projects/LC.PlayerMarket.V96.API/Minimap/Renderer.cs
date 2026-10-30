using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace LocalMiniMap;
public static class Renderer
{
 public static Bitmap Bitmap(View view,TerrainPatch terrain,double dpi,DateTimeOffset now){var b=new Bitmap((int)Math.Round(320*dpi),(int)Math.Round(240*dpi));using var g=Graphics.FromImage(b);Paint(g,b.Size,view,terrain,dpi,now);return b;}
 public static void Paint(Graphics g,Size size,View view,TerrainPatch terrain,double dpi,DateTimeOffset now)
 {
  g.Clear(Color.FromArgb(18,25,31));using var font=new Font("Microsoft JhengHei UI",(float)(11*dpi),FontStyle.Regular,GraphicsUnit.Pixel);
  if(!view.Available){g.DrawString(view.Reason,font,Brushes.White,8,8);return;}
  var saved=g.Save();g.SetClip(Projection.Plot(dpi));
  using var unknown=new SolidBrush(Color.FromArgb(60,63,67));using var ground=new SolidBrush(Color.FromArgb(163,155,132));using var wall=new SolidBrush(Color.FromArgb(37,40,45));
  foreach(var cell in terrain.Cells){var p=Projection.Project(new(cell.X,cell.Y),view.Center,dpi);var q=Projection.Project(new(cell.X+1,cell.Y+1),view.Center,dpi);if(Math.Max(p.X,q.X)<0||Math.Min(p.X,q.X)>size.Width||p.Y+4*dpi<24*dpi||p.Y-4*dpi>size.Height)continue;
   PointF[] corners=[p,Projection.Project(new(cell.X+1,cell.Y),view.Center,dpi),q,Projection.Project(new(cell.X,cell.Y+1),view.Center,dpi)];g.FillPolygon(!cell.Known?unknown:cell.Enclosed?wall:ground,corners);}
  if(view.Route.Length>1){using var route=new Pen(Color.Cyan,(float)(2*dpi));g.DrawLines(route,view.Route);}
  foreach(var dot in view.Dots){using var brush=new SolidBrush(dot.Color);float r=(float)(3*dpi);g.FillEllipse(brush,dot.Point.X-r,dot.Point.Y-r,2*r,2*r);}
  var self=Projection.Project(view.Center,view.Center,dpi);float sr=(float)(4*dpi);g.FillEllipse(Brushes.White,self.X-sr,self.Y-sr,2*sr,2*sr);
  if(view.Target is{} t&&view.TargetVisible){using var pen=new Pen(Color.Gold,(float)(2*dpi));float tr=(float)(8*dpi);g.DrawEllipse(pen,t.X-tr,t.Y-tr,2*tr,2*tr);}
  g.Restore(saved);g.DrawString(view.Reason,font,Brushes.White,(float)(6*dpi),(float)(4*dpi));
  if(view.Dots.Where(d=>d.LastQueriedAt.HasValue).Select(d=>d.LastQueriedAt).Max() is{} queried){g.FillRectangle(Brushes.Black,0,(float)(222*dpi),size.Width,(float)(18*dpi));g.DrawString($"本輪最後查詢 {queried.ToLocalTime():HH:mm:ss}",font,Brushes.White,(float)(6*dpi),(float)(224*dpi));}
 }
}
// Host-owned presentation only. No LC readers, broker, command sender, mutex, focus request or standalone GUI entry.
public sealed class LocalOverlay:Form
{
 public const int ExtendedStyles=0x20|0x80|0x80000|0x08000000;public const double GlobalOpacity=.30;
 View? view;TerrainPatch? terrain;double dpi;DateTimeOffset at;Frame? frame;Scope? expected;Ledger? ledger;
 readonly System.Windows.Forms.Timer refresh=new(){Interval=100};
 protected override bool ShowWithoutActivation=>true;
 protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=ExtendedStyles;return p;}}
 public LocalOverlay(){Text="LC 周邊近似小地圖";FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;AutoScaleMode=AutoScaleMode.None;StartPosition=FormStartPosition.Manual;BackColor=Color.Black;Opacity=GlobalOpacity;TopMost=true;DoubleBuffered=true;refresh.Tick+=(_,_)=>RefreshFrame(DateTimeOffset.UtcNow);}
 public void Present(Frame value,Scope scope,TerrainPatch patch,Ledger state,DateTimeOffset now){frame=value;expected=scope;terrain=patch;ledger=state;refresh.Start();RefreshFrame(now);}
 public void Clear(){refresh.Stop();frame=null;view=null;if(Visible)Hide();}
 void RefreshFrame(DateTimeOffset now){if(frame is null||expected is null||terrain is null||ledger is null)return;var value=Projection.Build(frame,expected,terrain,ledger,now);if(!value.Available){view=null;refresh.Stop();if(Visible)Hide();return;}view=value;dpi=frame.Dpi;at=now;Bounds=value.Slot;if(!Visible)Show();if(!SetWindowPos(Handle,(nint)(-1),Bounds.X,Bounds.Y,Bounds.Width,Bounds.Height,0x0010|0x0040|0x0200)){Hide();view=null;refresh.Stop();throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}Invalidate();}
 protected override void Dispose(bool disposing){if(disposing)refresh.Dispose();base.Dispose(disposing);}
 protected override void OnPaint(PaintEventArgs e){if(view is{} v&&terrain is{} t)Renderer.Paint(e.Graphics,ClientSize,v,t,dpi,at);}
 protected override void WndProc(ref Message m){if(m.Msg==0x84){m.Result=(nint)(-1);return;}if(m.Msg==0x21){m.Result=(nint)3;return;}base.WndProc(ref m);}
 [DllImport("user32.dll",SetLastError=true)]static extern bool SetWindowPos(nint h,nint after,int x,int y,int w,int height,uint flags);
}

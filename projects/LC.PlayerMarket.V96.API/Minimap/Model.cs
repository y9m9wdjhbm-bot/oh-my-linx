using System.Drawing;
namespace LocalMiniMap;
public sealed record Scope(int Pid,long Start,string Token,ulong Self,int Map,string? Scene)
{
 public bool Valid=>Pid>0&&Start>0&&!string.IsNullOrWhiteSpace(Token)&&Token!="0"&&Self>0&&Map>=0&&(Scene is null||!string.IsNullOrWhiteSpace(Scene)&&Scene!="0");
}
public readonly record struct World(double X,double Y)
{public bool Valid=>double.IsFinite(X)&&double.IsFinite(Y)&&X>=0&&Y>=0&&X<=65535&&Y<=65535;}
public sealed record Merchant(ulong Id,string Identity,Scope Scope,World Position,DateTimeOffset At,bool ShopObserved,DateTimeOffset ShopAt);
public sealed record Target(Scope Scope,World Position,string Token,DateTimeOffset At);
public sealed record Frame(Scope Scope,DateTimeOffset At,World Self,Rectangle Client,double Dpi,Merchant[] Merchants,Target? Target,World[] Route,DateTimeOffset RouteAt,Scope? RouteScope);
public sealed record BoundPose(Scope Scope,World Self,DateTimeOffset At,Target? Target,World[] Route);
public enum Page {Buy,Sell}
public sealed record Request(Scope Scope,ulong Owner,string Identity,Page Page,string Snapshot,string Child,long Generation,DateTimeOffset At);
public sealed record Receipt(Request Request,bool Confirmed,DateTimeOffset At);
public sealed class Ledger(Scope scope)
{
 public Scope Scope{get;private set;}=scope;public long Generation{get;private set;}public Request? Active{get;private set;}
 readonly Dictionary<(ulong,string,Page),Request> requests=[];readonly Dictionary<(ulong,string,Page),Receipt> receipts=[];
 (ulong Owner,string Identity,DateTimeOffset At)? scanOwner;
 public static bool Fresh(DateTimeOffset at,DateTimeOffset now)=>at<=now&&now-at<=TimeSpan.FromSeconds(2);
 public void Change(Scope next){if(Scope!=next){Scope=next;Generation++;Active=null;scanOwner=null;requests.Clear();receipts.Clear();}}
 public void Cancel(){Generation++;Active=null;scanOwner=null;requests.Clear();receipts.Clear();}
 internal void ClearActive(){Active=null;scanOwner=null;}
 internal void ForgetOwner(ulong id){foreach(var k in requests.Keys.Where(k=>k.Item1==id).ToArray())requests.Remove(k);foreach(var k in receipts.Keys.Where(k=>k.Item1==id).ToArray())receipts.Remove(k);if(Active?.Owner==id)Active=null;if(scanOwner?.Owner==id)scanOwner=null;}
 public bool Begin(Request r,DateTimeOffset now){if(!Scope.Valid||r.Scope!=Scope||r.Generation!=Generation||r.Owner==0||string.IsNullOrWhiteSpace(r.Identity)||string.IsNullOrWhiteSpace(r.Child)||string.IsNullOrWhiteSpace(r.Snapshot)||!Fresh(r.At,now))return false;Active=r;requests[(r.Owner,r.Identity,r.Page)]=r;receipts.Remove((r.Owner,r.Identity,r.Page));return true;}
 public bool Receive(Receipt r,DateTimeOffset now){var q=r.Request;var key=(q.Owner,q.Identity,q.Page);if(q.Scope!=Scope||q.Generation!=Generation||!Fresh(r.At,now)||!requests.TryGetValue(key,out var expected)||q!=expected)return false;receipts[key]=r;if(Active==q)Active=null;return true;}
 public bool Complete(Merchant m,DateTimeOffset now)=>Enum.GetValues<Page>().All(p=>receipts.TryGetValue((m.Id,m.Identity,p),out var r)&&r.Confirmed&&r.Request.Scope==Scope&&r.Request.Generation==Generation&&r.At<=now);
 public DateTimeOffset? LastQuery(Merchant m,DateTimeOffset now)=>Complete(m,now)?Enum.GetValues<Page>().Max(p=>receipts[(m.Id,m.Identity,p)].At):null;
 public bool Querying(Merchant m,DateTimeOffset now)=>(Active is{} a&&a.Scope==Scope&&a.Generation==Generation&&a.Owner==m.Id&&a.Identity==m.Identity&&Fresh(a.At,now))||(scanOwner is{} s&&s.Owner==m.Id&&s.Identity==m.Identity&&Fresh(s.At,now));
 internal void MatchedListing(Merchant m,Page page,string snapshot,string child,DateTimeOffset captured,DateTimeOffset now){if(m.Scope!=Scope||captured>now||string.IsNullOrWhiteSpace(child)||string.IsNullOrWhiteSpace(snapshot))return;var r=new Request(Scope,m.Id,m.Identity,page,snapshot,child,Generation,captured);requests[(m.Id,m.Identity,page)]=r;receipts[(m.Id,m.Identity,page)]=new(r,true,captured);}
 internal void ScanOwner(Merchant m,string snapshot,DateTimeOffset responseAt){Active=null;scanOwner=(m.Id,m.Identity,responseAt);}
}
public sealed record TerrainCell(int X,int Y,bool Known,bool Enclosed);
public sealed record TerrainPatch(int Map,int CenterX,int CenterY,int Radius,string SourceSha256,TerrainCell[] Cells);
public sealed record Dot(ulong Id,PointF Point,Color Color,bool Querying,bool Complete,DateTimeOffset? LastQueriedAt=null);
public sealed record View(bool Available,string Reason,Rectangle Slot,World Center,double Scale,Dot[] Dots,PointF? Target,bool TargetVisible,PointF[] Route,int Skipped,int Cells);
public static class Projection
{
 public const int WidthDip=320,HeightDip=240,MarginDip=12;public const double Zoom=4;
 public static PointF Project(World point,World self,double dpi)=>new((float)((160+Zoom*((point.X-self.X)+(point.Y-self.Y)))*dpi),(float)((132+Zoom*(-.5*(point.X-self.X)+.5*(point.Y-self.Y)))*dpi));
 public static World Inverse(PointF p,World self,double dpi){double u=p.X/dpi-160,v=p.Y/dpi-132;return new(self.X+u/(2*Zoom)-v/Zoom,self.Y+u/(2*Zoom)+v/Zoom);}
 public static Rectangle Plot(double dpi)=>new(0,(int)Math.Round(24*dpi),(int)Math.Round(320*dpi),(int)Math.Round(216*dpi));
 public static bool Inside(PointF p,double dpi)=>p.X>=0&&p.X<320*dpi&&p.Y>=24*dpi&&p.Y<240*dpi;
 public static bool TargetOn(DateTimeOffset now)=>now.ToUnixTimeMilliseconds()/450%2==0;
 public static View Build(Frame f,Scope expected,TerrainPatch terrain,Ledger state,DateTimeOffset now)
 {
  View Missing(string s)=>new(false,s,Rectangle.Empty,f.Self,0,[],null,false,[],0,0);
  if(!f.Scope.Valid||f.Scope!=expected||state.Scope!=expected)return Missing("來源／地圖未知或不一致");
  if(!f.Self.Valid||!Ledger.Fresh(f.At,now))return Missing("角色位置已過期");
  if(!double.IsFinite(f.Dpi)||f.Dpi<.5||f.Dpi>8||f.Client.Width<=0||f.Client.Height<=0)return Missing("視窗資料無效");
  int w=(int)Math.Round(WidthDip*f.Dpi),h=(int)Math.Round(HeightDip*f.Dpi),margin=(int)Math.Round(MarginDip*f.Dpi);
  if(f.Client.Width<w+2*margin||f.Client.Height<h+2*margin)return Missing("視窗空間不足");
  if(terrain.Map!=f.Scope.Map||terrain.Radius<52||string.IsNullOrWhiteSpace(terrain.SourceSha256)||terrain.Cells.Length>20000)return Missing("當前局部地形不可用");
  if(Math.Abs(f.Self.X-terrain.CenterX)>terrain.Radius-52||Math.Abs(f.Self.Y-terrain.CenterY)>terrain.Radius-52)return Missing("角色已離開局部地形資料");
  if(f.Merchants.Length>8192)return Missing("商人資料超限");
  var slot=new Rectangle(f.Client.Right-margin-w,f.Client.Top+margin,w,h);List<Dot> dots=[];int skipped=0;
  var duplicated=f.Merchants.GroupBy(m=>m.Id).Where(g=>g.Count()!=1).Select(g=>g.Key).ToHashSet();
  foreach(var m in f.Merchants){if(m.Id==0||string.IsNullOrWhiteSpace(m.Identity)||duplicated.Contains(m.Id)||m.Scope!=expected||!m.Position.Valid||!m.ShopObserved||!Ledger.Fresh(m.At,now)||!Ledger.Fresh(m.ShopAt,now)){skipped++;continue;}
   var p=Project(new(m.Position.X+.5,m.Position.Y+.5),new(f.Self.X+.5,f.Self.Y+.5),f.Dpi);if(!Inside(p,f.Dpi)){skipped++;continue;}
   bool query=state.Querying(m,now),complete=state.Complete(m,now);bool green=query?now.ToUnixTimeMilliseconds()/300%2==1:complete;
   dots.Add(new(m.Id,p,green?Color.LimeGreen:Color.Red,query,complete,state.LastQuery(m,now)));}
  PointF? target=null;if(f.Target is{} t&&t.Scope==expected&&t.Position.Valid&&!string.IsNullOrWhiteSpace(t.Token)&&Ledger.Fresh(t.At,now)){var p=Project(new(t.Position.X+.5,t.Position.Y+.5),new(f.Self.X+.5,f.Self.Y+.5),f.Dpi);if(Inside(p,f.Dpi))target=p;}
  // Out-of-range vertices stay unmodified: Graphics clips real segments, never invents clamped targets.
  var route=f.RouteScope==expected&&Ledger.Fresh(f.RouteAt,now)&&f.Route.Length<=4096&&f.Route.All(p=>p.Valid)?f.Route.Select(p=>Project(new(p.X+.5,p.Y+.5),new(f.Self.X+.5,f.Self.Y+.5),f.Dpi)).ToArray():[];
  return new(true,"周邊地圖（近似範圍）",slot,new(f.Self.X+.5,f.Self.Y+.5),Zoom*f.Dpi,dots.Take(512).ToArray(),target,TargetOn(now),route,skipped+Math.Max(0,dots.Count-512),terrain.Cells.Length);
 }
}

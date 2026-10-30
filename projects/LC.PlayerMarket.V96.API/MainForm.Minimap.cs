using LocalMiniMap;
namespace LcPlayerMarket;
internal sealed partial class MainForm
{
 private readonly CheckBox miniMapEnabled=Check("右上周邊地圖",true);
 private readonly Label miniMapStatus=Label("等待地圖資料");
 private readonly MarketMiniMapHost miniMapHost=new(new LocalTerrainProvider(Path.Combine(AppContext.BaseDirectory,"Navigation","_internal","resources")));
 private LocalOverlay? miniMapOverlay;private bool miniMapBusy;
 private void InitializeMiniMap(){miniMapEnabled.CheckedChanged+=(_,_)=>{if(!miniMapEnabled.Checked){miniMapHost.Reset();miniMapOverlay?.Clear();miniMapStatus.Text="周邊地圖已隱藏";}};}
 private async void UpdateMiniMap()
 {
  if(miniMapBusy)return;if(closing||!miniMapEnabled.Checked){miniMapOverlay?.Clear();return;}
  miniMapBusy=true;try
  {
   var hub=snapshot;var pose=patrolNavigation.DisplaySnapshot;var source=hub?.Source;
   // Existing OS-only reader verifies source PID + process creation and client geometry, never activates LC.
   var frame=source is null?null:WindowFrameReader.Read(source);
   MiniMapSelection? selected=currentTargetKey is{} key&&history.TryGetValue(key,out var listing)&&navigationEnabled.Checked
      ?new(listing.SourceSessionKey,listing.Owner.Id,listing.Owner.LocalIdentity,key+":"+listing.Revision):null;
   int displayGeneration=miniMapHost.Generation;
   var result=await miniMapHost.BuildAsync(pose,hub,broker,frame,selected,lifetime.Token);
   if(closing||!miniMapEnabled.Checked)return;
   if(!miniMapHost.IsCurrentGeneration(displayGeneration)||source!=snapshot?.Source||pose!=patrolNavigation.DisplaySnapshot||frame is not null&&WindowFrameReader.Read(source!)!=frame)
   {miniMapOverlay?.Clear();miniMapStatus.Text="等待同來源地圖資料";return;}
   miniMapStatus.Text=result.Status;
   if(result.View is null||result.Frame is null||result.Terrain is null){miniMapOverlay?.Clear();return;}
   miniMapOverlay??=new();miniMapOverlay.Present(result.Frame,result.Frame.Scope,result.Terrain,miniMapHost.LedgerForPresentation!,DateTimeOffset.UtcNow);
  }
  catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
  catch(Exception e){if(!closing&&!IsDisposed){miniMapOverlay?.Clear();miniMapStatus.Text="周邊地圖暫停";log.Write("MINIMAP_DISPLAY_ERROR",e.Message);}}
  finally{miniMapBusy=false;}
 }
}

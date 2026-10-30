using System.Security.Cryptography;

namespace MemoryHub.Service;

internal sealed record ShopPackageReadiness(bool Ready,string Code,string Detail);

/// <summary>Local package/build check before accepting a batch or taking native ownership.</summary>
internal static class ShopNativePackage
{
 internal const string DisplayName="商店元件 V17";
 internal const string SupportedLcVersion="B47CD077／PE 6AE08E04";
 internal const string LegacyStrictV16Policy="Current 38A7 exact seven A/B bodies; every invoked body must be currently readable/executable; no deferred NOACCESS call; immediate strict full postbody; maxmask0";
 internal const string LegacyStrictV16HookSha="124E5B0AD3D3D109C52BBC297670C9879A4655D4C67C2B9DFCEAD3BB9A7E836E";
 internal const string DeferredCodePolicy=ShopNativeB47CContract.Policy;
 internal const string HelperFile="LC_PlayerShopListActionV17.exe";
 internal const string NativePayloadFile="LC_PlayerShopListActionHookV17.dll";
 // Exact hashes from the administrator-built V17 release; historical V16 identity stays separate.
 internal const string NativePayloadSha256="DEC39B4297EF9823927C56FDADE5038A91A4E358C65E26EEA415BA21282EDB16";
 internal const string SupportedSha="B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC";
 internal const uint SupportedTimestamp=0x6AE08E04,SupportedImageSize=0x3F86000;
 internal static readonly (string Name,string Sha256)[] ExpectedFiles = [
  ("LC_PlayerShopListActionHookV17.dll","DEC39B4297EF9823927C56FDADE5038A91A4E358C65E26EEA415BA21282EDB16"),
  ("LC_PlayerShopListActionV17.deps.json","A9C30C804628CC257DE8BDE70F3C834D3612C5F4E7F34BA4892C4CA5F88AFEBC"),
  ("LC_PlayerShopListActionV17.dll","D3712C633FF4E88C464E57FB08F654D5E49FF9D14D6F5D371B1E73B33720B2AB"),
  ("LC_PlayerShopListActionV17.exe","3E9BD7C3C22C762417356F1300FB4BE541CCC32E4A9544159541F33327A5313C"),
  ("LC_PlayerShopListActionV17.runtimeconfig.json","E5C4815FB92106CBFB99851CC57775222698CC0010EB773616D7707709944D3F")
 ];
 internal static bool SupportsBuild(SourceIdentity identity)=>
  string.Equals(identity.DiskSha256,SupportedSha,StringComparison.OrdinalIgnoreCase)
  && identity.PeTimestamp==SupportedTimestamp && identity.ImageSize==SupportedImageSize;
 internal static ShopPackageReadiness Check(SourceIdentity identity,string directory)
 {
  if(!SupportsBuild(identity))
   return new(false,"NativeBuildUnsupported",$"{DisplayName} 僅支援已核對的 LC {SupportedLcVersion} 完整 SHA／PE；本次尚未送出請求。");
  if(ExpectedFiles.Any(x=>x.Sha256.Length!=64))
   return new(false,"NativeHelperNotCertified","V17 元件尚未完成管理員建置雜湊綁定；未授予原生搜尋資格。");
  foreach(var expected in ExpectedFiles)
  {
   string path=Path.Combine(directory,expected.Name);
   if(!File.Exists(path))return new(false,"NativeHelperMissing","完整商店元件缺少 ShopControl/"+expected.Name+"；本批尚未開始，未送出原生請求。");
   try
   {
    using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
    if(!string.Equals(Convert.ToHexString(SHA256.HashData(file)),expected.Sha256,StringComparison.Ordinal))
     return new(false,"NativeHelperMismatch","商店元件內容不符："+expected.Name+"；請使用完整新版發布資料夾，本次未送出請求。");
   }
   catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
   {return new(false,"NativeHelperMismatch","商店元件無法核對："+expected.Name+"；"+ex.Message);}
  }
  return new(true,"Ready",$"{DisplayName} 配套與 LC 版本相符；每次送出前仍須核對當前來源、已認證政策與所有權；返回後仍須完整程式碼核對。");
 }
}


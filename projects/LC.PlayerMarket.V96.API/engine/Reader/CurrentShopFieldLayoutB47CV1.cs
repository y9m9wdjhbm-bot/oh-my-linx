namespace MemoryHub.Reader;

/// <summary>Build-specific data layout only. No native dispatch permission.</summary>
internal static class CurrentShopFieldLayoutB47CV1
{
    internal const string Sha256="B47CD077B4250FAC772F264A748DB75DC8C0075DDCC2ED475CC5F247CC9945FC";
    internal static readonly LcBuildIdentity Identity=new(Sha256,39478048,352,1793101316,66609152,0x8664);
    internal const uint DirectActionOffset=200;
    internal static bool IsBuild(LcBuildIdentity identity)=>identity==Identity;
}

using System.Security.Cryptography;

namespace MemoryHub.Reader;

internal sealed partial class PersonalShopSampler
{
    private PersonalShopItemStateEvidence? itemStateEvidence;
    private ItemStateSchemaProof? itemStateProof;
    private SourceIdentity? itemStateProofSource;
    private bool pendingItemStateContradiction;

    private SourceIdentity ItemStateSource()
    {
        string own=journal.SelfId?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"unknown";
        string session=$"{memory.ProcessId}:{memory.CreationFileTime}:{memory.Image:X}:{director:X}:{scene:X}:{own}";
        return new(memory.ProcessId,memory.CreationFileTime,Probe.ExePath,Probe.Hex(memory.Image),memory.Profile.Identity.PeTimestamp,
            memory.Profile.Identity.ImageSize,memory.Profile.Sha256,session);
    }
    private ItemStatePage? ItemPage(ulong address)
    {
        if(address<memory.Image || address-memory.Image>uint.MaxValue || !memory.Query((uint)(address-memory.Image),out var page))return null;
        return new(page.BaseAddress,page.AllocationBase,page.RegionSize,page.State,page.Protect,page.Type,page.AllocationProtect,page.PartitionId);
    }
    private bool FreshCertificateDiskAndType()
    {
        if(memory.Profile.Identity.PeTimestamp!=0x6ACEDB9A || memory.Profile.Identity.ImageSize!=0x3F5F000 ||
            memory.Profile.Identity.Machine!=0x8664 || !memory.Profile.TypeBindings.TryGetValue(ShopItemControlName,out var type) ||
            type.Descriptor!=0x17A1FD0 || type.Col!=0x1338730 || type.Vtable!=0x12265C8)return false;
        memory.Verify();
        using var file=new FileStream(Probe.ExePath,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length!=memory.Profile.Identity.FileLength || file.Length is <=0 or >256*1024*1024)return false;
        using var hasher=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] block=new byte[65536];long readTotal=0;
        while(readTotal<file.Length)
        {
            budget.Check();int read=file.Read(block,0,(int)Math.Min(block.Length,file.Length-readTotal));
            if(read==0)return false;hasher.AppendData(block,0,read);readTotal+=read;
        }
        budget.Check();memory.Verify();return Convert.ToHexString(hasher.GetHashAndReset())==PersonalShopBuildCertificate.BuildSha256;
    }
    private bool CurrentBuildItemKindCodeVerified()
    {
        itemStateProofSource=ItemStateSource();
        var contract=PersonalShopItemCodeContracts.ForBuild(memory.Profile.Sha256);
        if(contract is null){LogItemStateProof("UnsupportedBuild");return false;}
        if(contradictions.IsRejected(itemStateProofSource)){LogItemStateProof("SourceContradictionLatched");return false;}
        try
        {
            itemStateProof=PersonalShopSchemaVerifier.Capture(memory.Profile.Sha256,memory.Image,
                (address,count)=>Meta(address,count,"item-state current code or metadata proof"),ItemPage);
            pendingItemStateContradiction=itemStateProof.StableContradiction;
            if(itemStateProof.RuntimeVerified)
                itemStateEvidence=new(){Mode="CurrentRuntimeCodeVerified",SourceBuildSha256=memory.Profile.Sha256,
                    CurrentRuntimeCodeVerified=true,CurrentMetadataVerified=true};
            else if(itemStateProof.MetadataVerified && !itemStateProof.StableContradiction)
            {
                if(FreshCertificateDiskAndType())itemStateEvidence=PersonalShopBuildCertificate.Evidence();
                else {LogItemStateProof("FreshDiskOrTypeUnverified");return false;}
            }
            LogItemStateProof(itemStateEvidence?.Mode??itemStateProof.Reason);
            return itemStateEvidence?.CurrentRuntimeCodeVerified==true;
        }
        catch(ProbeFailure failure)
        {
            memory.Verify();itemStateEvidence=null;pendingItemStateContradiction=false;itemStateProof=null;
            LogItemStateProof(failure.Code);return false;
        }
    }
    private void LogItemStateProof(string reason)
    {
        var contract=PersonalShopItemCodeContracts.ForBuild(memory.Profile.Sha256);
        journal.Write("ITEM_KIND_CURRENT_CODE_PROOF",new {Verified=itemStateEvidence?.CurrentRuntimeCodeVerified==true,
            CurrentRuntimeCodeVerified=itemStateEvidence?.CurrentRuntimeCodeVerified==true,
            CurrentMetadataVerified=itemStateEvidence?.CurrentMetadataVerified==true,
            EvidenceMode=itemStateEvidence?.Mode??"Unknown",Build=memory.Profile.Sha256,Reason=reason,
            CodeAvailability=itemStateProof?.Pages.FirstOrDefault()?.Page.Protect==1?"NOACCESS":itemStateProof?.RuntimeVerified==true?"ReadableMatched":"Unknown",
            MetadataMatched=itemStateProof?.MetadataVerified==true,BuildCertified=itemStateEvidence?.Mode=="BuildCertified",
            CertificateSHA=itemStateEvidence?.CertificateSha256??"",FormatterRva=contract is null?null:Probe.Hex(contract.FormatterRva),
            PdataRva=contract is null?null:Probe.Hex(contract.PdataRva),FormatterBodyBytes=contract?.CodeSpans[0].Hex.Length/2,
            PrefixDescriptorsVerified=itemStateEvidence?.CurrentMetadataVerified==true,CodeEvidenceSHA=contract?.CodeEvidenceSha256,
            BridgeSHA=contract?.SemanticBridgeSha256,HumanSemanticEvidenceSHA=contract?.HistoricalSemanticEvidenceSha256,
            SemanticEvidenceMode=itemStateEvidence?.Mode=="BuildCertified"?"BuildCertifiedHistoricalCodeWithCurrentMetadata":contract?.SemanticEvidenceMode,
            HumanCalibrationBuild=contract?.HumanCalibrationBuild,CurrentHumanCorroboration=false,
            StableContradictionPendingSourceClosure=pendingItemStateContradiction,FinalABPending=true,NativeCalls=0});
    }
    private void CloseItemStateProof()
    {
        if(itemStateProof is not null && !PersonalShopSchemaVerifier.ClosePages(itemStateProof,ItemPage))
            throw new ProbeFailure("ITEM_STATE_PAGE_CHANGED","道具狀態驗證頁面在前後核對時變更，本輪不交付語意。");
        if(itemStateProofSource is not null && contradictions.IsRejected(itemStateProofSource) && itemStateEvidence is not null)
            throw new ProbeFailure("ITEM_STATE_CONTRADICTION","同來源已有穩定語意矛盾，本輪不交付語意。");
    }
    private void AttachItemStateContradictionCommit()
    {
        if(!pendingItemStateContradiction || itemStateProofSource is null)return;
        var expected=itemStateProofSource;var prior=journal.SourceCommit;
        journal.SourceCommit=verified=>
        {
            prior?.Invoke(verified);
            if(verified!=expected)throw new ProbeFailure("GROUP_SOURCE_CHANGED","矛盾紀錄的最終來源不一致。");
            contradictions.RecordClosedContradiction(verified,itemStateProof!.Reason);
        };
    }
    private void AttachItemStatePublication(PersonalShopListing listing)
    {
        var rows=listing.Rows.Where(r=>r.ItemStateEvidence is not null).ToArray();
        if(rows.Length==0 || listing.PageModeState!="MatchedExclusiveBankAndRows")return;
        var prior=journal.SourceCommit;
        journal.SourceCommit=source=>
        {
            prior?.Invoke(source);
            if(source!=itemStateProofSource)throw new ProbeFailure("GROUP_SOURCE_CHANGED","交付道具語意的最終來源不一致。");
            CloseItemStateProof();
            journal.Write("ITEM_KIND_DATA_PUBLISHED",new{Source=source,Rows=rows.Length,ItemStateEvidence=itemStateEvidence,
                CodeAvailability=itemStateEvidence?.Mode=="BuildCertified"?"NOACCESS":"ReadableMatched",
                MetadataMatched=itemStateEvidence?.CurrentMetadataVerified==true,BuildCertified=itemStateEvidence?.Mode=="BuildCertified",
                CurrentRuntimeCodeVerified=itemStateEvidence?.CurrentRuntimeCodeVerified==true,FinalABPending=false,
                FullDataAndSourceClosed=true,NativeCalls=0,CurrentHumanCorroboration=false});
        };
    }
    private bool CurrentBuildItemIdentificationVerified() => currentItemKindCodeVerified
        && PersonalShopItemCodeContracts.ForBuild(memory.Profile.Sha256)?.IdentificationSemanticsVerified==true;
}

using System.Globalization;
using System.Text;


namespace MemoryHub.Reader;

internal sealed partial class PersonalShopSampler
{
    // These are emitted by Run only after its final source/catalog/A-B closure.
    private readonly List<object> pendingPersonalShopRows = [];
    private const int PersonalShopMaximumRows = 24;
    private const int PersonalShopMaximumChildren = 64;
    private const int PersonalShopMaximumNodes = 176; // Five ancestors plus seven nodes per row.
    private const string PersonalShopWrapper = ".?AVPlayerShopLayoutPCImpl@ui@lineage@@";
    private const string PersonalShopNodeType = ".?AVNode@cocos2d@@";
    private const string PersonalShopLayoutType = ".?AVLayout@ui@cocos2d@@";
    private const string PersonalShopListType = ".?AVListView@ui@cocos2d@@";
    private const string PersonalShopTextType = ".?AVText@ui@cocos2d@@";
    private const string PersonalShopLabelType = ".?AVLabel@cocos2d@@";

    private sealed record PersonalShopNode(ulong Object, TypeInfo Type, ulong Parent,
        ulong[] Ordinary, ulong[] Protected, ulong OrdinaryBegin, ulong ProtectedBegin, string Path);
    private sealed record PersonalShopLabel(string Value, string Address, string DescriptorAddress,
        string Path, string DescriptorHex, int Utf8ByteLength);

    private void InspectPersonalShopRows(CatalogEntry owner)
    {
        var sampledAt = DateTimeOffset.UtcNow;
        var localVisited = new HashSet<ulong>();
        var localParents = new Dictionary<ulong, ulong>();
        var nodeEvidence = new List<object>();
        var observations = new List<object>();
        var reasons = new List<string>();
        ulong root = 0, container = 0;
        int? declaredRows = null;
        int inspectedRows = 0, namedRows = 0, emptyRows = 0, failedRows = 0;
        bool hierarchyVerified = false, routeVerified = false;
        object? wrapperEvidence = null;

        try
        {
            Need(owner.Key == "PersonalShopLayout", "PERSONAL_SHOP_KEY", "必須是 fresh catalog 的 PersonalShopLayout。");
            Ptr(owner.Object); Ptr(owner.Control); Ptr(owner.Node);
            wrapperEvidence = VerifyPersonalShopWrapper(owner);
            hierarchyVerified = true;
            root = U64(Read(checked(owner.Object + 0x30), 8, "PersonalShop exact wrapper +30 Node candidate"));
            Need(root != owner.Object && root != owner.Control && root != director && root != scene,
                "PERSONAL_SHOP_ROOT_ALIAS", "玩家商店根與來源／控制物件別名。");

            var node = ReadNode(root, null, PersonalShopNodeType, "PersonalShopLayout/owner+0x30");
            node = Descend(node, false, 0, PersonalShopNodeType);
            node = Descend(node, false, 16, PersonalShopNodeType);
            node = Descend(node, false, 0, PersonalShopListType);
            var rowContainer = Descend(node, true, 0, PersonalShopLayoutType);
            container = rowContainer.Object;
            declaredRows = rowContainer.Ordinary.Length;
            routeVerified = true;

            int take = Math.Min(declaredRows.Value, PersonalShopMaximumRows);
            if (take != declaredRows.Value)
                reasons.Add($"列容器有 {declaredRows} 個 child，本次只檢查前 {PersonalShopMaximumRows} 列；未宣稱完整商品清單。");
            for (int index = 0; index < take; index++)
            {
                inspectedRows++;
                ulong rowAddress = rowContainer.Ordinary[index];
                string rowPath = rowContainer.Path + $"/ordinary[{index}]";
                PersonalShopLabel? name = null, price = null, unit = null;
                try
                {
                    var row = Descend(rowContainer, false, index, PersonalShopLayoutType);
                    name = ReadLabel(row, 4);
                    price = ReadLabel(row, 3);
                    unit = ReadLabel(row, 6);
                    bool emptyName = string.IsNullOrWhiteSpace(name.Value);
                    bool template = PersonalShopTextRules.IsTemplate(name.Value, price.Value);
                    var decoded = PersonalShopTextRules.Decode(index, name.Value, price.Value, unit.Value);
                    bool empty = emptyName || template;
                    bool completeText = !string.IsNullOrWhiteSpace(price.Value) && !string.IsNullOrWhiteSpace(unit.Value);
                    bool included = decoded.IncludedInListing;
                    if (empty) emptyRows++;
                    if (included) namedRows++;
                    if (!empty && !completeText)
                    {
                        failedRows++;
                        reasons.Add($"row {index}: 品名非空，但價格或計價文字為空，保留未完整列。");
                    }
                    observations.Add(new
                    {
                        rowIndex = index, rowAddress = Hex(rowAddress), path = rowPath,
                        state = decoded.State,
                        includedInCandidateList = included, emptyName, templateCandidate = template,
                        templateClassificationIsInference = template,
                        nameRaw = name.Value, priceRaw = price.Value, unitRaw = unit.Value,
                        numericPriceCandidate = ParsePersonalShopPrice(price.Value),
                        stockQuantity = (ulong?)null, shopOwnerId = (ulong?)null, buySellMode = (string?)null,
                        labels = new { name, price, unit }, sameRowPathVerified = true,
                        visibilityVerified = false, stockQuantityVerified = false, shopOwnerBindingVerified = false,
                        buySellModeVerified = false, productInformationVerified = false,
                        pendingFinalAB = true
                    });
                }
                catch (ProbeFailure error) when (PersonalShopRecoverable(error))
                {
                    memory.Verify();
                    failedRows++;
                    reasons.Add($"row {index}: {error.Code}: {error.Message}");
                    observations.Add(new
                    {
                        rowIndex = index, rowAddress = Hex(rowAddress), path = rowPath, state = "PartialUnsupported",
                        includedInCandidateList = false, errorCode = error.Code, detail = error.Message,
                        nameRaw = name?.Value, priceRaw = price?.Value, unitRaw = unit?.Value,
                        numericPriceCandidate = (ulong?)null, stockQuantity = (ulong?)null,
                        shopOwnerId = (ulong?)null, buySellMode = (string?)null,
                        labels = new { name, price, unit }, sameRowPathVerified = false,
                        visibilityVerified = false, stockQuantityVerified = false, shopOwnerBindingVerified = false,
                        buySellModeVerified = false, productInformationVerified = false, pendingFinalAB = true
                    });
                }
            }
        }
        catch (ProbeFailure error) when (PersonalShopRecoverable(error))
        {
            memory.Verify();
            reasons.Add(error.Code + ": " + error.Message);
        }

        foreach (string reason in reasons) Partial("PersonalShop: " + reason);
        pendingPersonalShopRows.Add(new
        {
            sampledAt, key = owner.Key, ownerAddress = Hex(owner.Object), ownerControl = Hex(owner.Control),
            catalogNode = Hex(owner.Node), rootAddress = root == 0 ? null : Hex(root),
            containerAddress = container == 0 ? null : Hex(container), director = Hex(director), scene = Hex(scene),
            wrapperEvidence, wrapperHierarchyVerified = hierarchyVerified, selectedRouteVerified = routeVerified,
            declaredRowContainerCount = declaredRows, inspectedRows, namedListingCandidates = namedRows,
            emptyOrTemplateCandidates = emptyRows, partialRows = failedRows,
            maximumRows = PersonalShopMaximumRows, maximumChildren = PersonalShopMaximumChildren,
            maximumNodes = PersonalShopMaximumNodes, nodes = nodeEvidence, rows = observations,
            completeForSelectedRowTextScope = routeVerified && reasons.Count == 0,
            truncatedOrPartial = reasons.Count > 0, incompleteReasons = reasons,
            containerCountIsProductCount = false, stockQuantity = (ulong?)null,
            shopOwnerId = (ulong?)null, buySellMode = (string?)null,
            visibilityVerified = false, rootSceneMembershipVerified = false, dataMayBeCachedOrHidden = true,
            shopOwnerBindingVerified = false, buySellModeVerified = false, stockQuantityVerified = false,
            productInformationVerified = false, wholeShopCompleteVerified = false,
            ownerOffset70Read = false, functionsCalled = false, inputSent = false, pendingFinalAB = true
        });

        PersonalShopNode ReadNode(ulong obj, ulong? expectedParent, string expectedType, string path)
        {
            Need(expectedType is PersonalShopNodeType or PersonalShopLayoutType or PersonalShopListType or
                PersonalShopTextType or PersonalShopLabelType, "PERSONAL_SHOP_NODE_TYPE", "只讀已知 Node/Layout/ListView/Text/Label。");
            Need(localVisited.Count < PersonalShopMaximumNodes && localVisited.Add(obj),
                "PERSONAL_SHOP_NODE_ALIAS", path + " 超出節點界限或重複指向已走訪節點。");
            int proofStart = proofs.Count;
            var type = Exact(obj, expectedType); NodeBase(type);
            byte[] links = Read(checked(obj + 0x178), 40, path + " full ordinary header/parent/Director");
            ulong parent = U64(links, 24), actualDirector = U64(links, 32);
            Need((parent == 0 || Pointer(parent)) && actualDirector == director &&
                (!expectedParent.HasValue || parent == expectedParent.Value),
                "PERSONAL_SHOP_NODE_OWNER", path + " parent/Director 回指不符。");
            bool terminal = expectedType == PersonalShopLabelType;
            int ordinaryCount = PersonalShopVectorCount(links, terminal ? 4096 : PersonalShopMaximumChildren, path);
            // Label glyph children are outside the item-text path: validate their
            // header and its B proof, but never traverse or count them as items.
            ulong[] ordinary = terminal ? [] : Vector(links, path + "/ordinary", maxCount: PersonalShopMaximumChildren);
            byte[]? protectedHeader = ProtectedTypes.Contains(expectedType)
                ? Read(checked(obj + 0x340), 24, path + " full protected header") : null;
            ulong[] protectedChildren = protectedHeader is null ? []
                : Vector(protectedHeader, path + "/protected", maxCount: PersonalShopMaximumChildren);
            ClosePersonalShopProofRange(proofStart, path + " per-node bounded B");
            nodeEvidence.Add(new
            {
                path, address = Hex(obj), type, parent = Hex(parent), director = Hex(actualDirector),
                parentMatched = expectedParent.HasValue ? (bool?)(parent == expectedParent.Value) : null,
                ordinaryCount, protectedCount = protectedChildren.Length, labelTerminal = terminal,
                ordinaryChildrenTraversed = !terminal, exactTypeVerified = true, nodeBaseVerified = true,
                localNodeABClosed = true, pendingFinalAB = true, visibilityVerified = false
            });
            return new(obj, type, parent, ordinary, protectedChildren, U64(links),
                protectedHeader is null ? 0 : U64(protectedHeader), path);
        }

        PersonalShopNode Descend(PersonalShopNode parent, bool useProtected, int index, string expectedType)
        {
            ulong[] children = useProtected ? parent.Protected : parent.Ordinary;
            string channel = useProtected ? "protected" : "ordinary";
            Need(index >= 0 && index < children.Length, "PERSONAL_SHOP_PATH_MISSING",
                parent.Path + $"/{channel}[{index}] 在本次完整容器中不存在。");
            ulong child = children[index], begin = useProtected ? parent.ProtectedBegin : parent.OrdinaryBegin;
            Need(U64(Read(checked(begin + (uint)index * 8), 8, parent.Path + " selected child slot")) == child,
                "GROUP_SOURCE_CHANGED", "玩家商店 child slot 在同輪讀取期間變更。");
            Need(child != owner.Object && child != owner.Control && child != director && child != scene &&
                !localVisited.Contains(child), "PERSONAL_SHOP_NODE_ALIAS", "商店列連回祖先／來源或其他已讀節點。");
            Need(!localParents.TryGetValue(child, out ulong priorParent) || priorParent == parent.Object,
                "PERSONAL_SHOP_NODE_OWNER", "同一商店子節點出現多個 parent。");
            localParents[child] = parent.Object;
            return ReadNode(child, parent.Object, expectedType, parent.Path + $"/{channel}[{index}]");
        }

        PersonalShopLabel ReadLabel(PersonalShopNode row, int childIndex)
        {
            var text = Descend(row, false, childIndex, PersonalShopTextType);
            var label = Descend(text, true, 0, PersonalShopLabelType);
            int proofStart = proofs.Count;
            ulong descriptorAt = checked(label.Object + 0x378);
            byte[] header = Read(descriptorAt, 32, label.Path + " full Label string descriptor");
            ulong size = U64(header, 16), cap = U64(header, 24);
            Need(size <= 256 && (cap != 15 || size <= 15), "STRING_LIMIT", "Label 字串長度不符合256B/SSO界限。");
            string value;
            try { value = String(header, 0, 256, label.Path + " Label raw text"); }
            catch (DecoderFallbackException) { throw new ProbeFailure("PERSONAL_SHOP_UTF8", "Label 不是完整 UTF-8，未交付部分文字。"); }
            Need(Read(descriptorAt, 32, label.Path + " descriptor after body").AsSpan().SequenceEqual(header),
                "GROUP_SOURCE_CHANGED", "商店 Label 描述子在讀取內容期間變更。");
            ClosePersonalShopProofRange(proofStart, label.Path + " text bounded B");
            return new(value, Hex(label.Object), Hex(descriptorAt), label.Path, Convert.ToHexString(header), (int)size);
        }
    }

    private object VerifyPersonalShopWrapper(CatalogEntry owner)
    {
        int proofStart = proofs.Count;
        // Type() reads complete RTTI independently of executable first slots.
        var type = Type(owner.Object);
        Need(type.Name == PersonalShopWrapper && type.ObjectOffset == 0 && type.ConstructionOffset == 0 &&
            owner.Type is not null && type == owner.Type, "PERSONAL_SHOP_WRAPPER", "fresh catalog wrapper 的完整 primary 型別不符。");
        string[] expected = [PersonalShopWrapper, ".?AVPlayerShopLayout@ui@lineage@@",
            ".?AVCreatableLayout@ui@lineage@@", ".?AVLayoutBase@ui@lineage@@",
            ".?AV?$enable_shared_from_this@VLayoutBase@ui@lineage@@@std@@"];
        Need((type.Hierarchy & 3) == 0, "PERSONAL_SHOP_HIERARCHY", "玩家商店 CHD 對齊不符。");
        byte[] chd = Meta(memory.Image + type.Hierarchy, 16, "PersonalShop complete CHD");
        uint count = U32(chd, 8), array = U32(chd, 12);
        Need(U32(chd) == 0 && U32(chd, 4) == 0 && count == expected.Length && (array & 3) == 0,
            "PERSONAL_SHOP_HIERARCHY", "玩家商店五基底完整階層不符。");
        byte[] entries = Meta(memory.Image + array, expected.Length * 4, "PersonalShop complete base-descriptor array");
        var bases = new List<object>();
        for (int index = 0; index < expected.Length; index++)
        {
            uint rva = U32(entries, index * 4);
            Need((rva & 3) == 0, "PERSONAL_SHOP_HIERARCHY", "玩家商店 BCD 未對齊。");
            byte[] bcd = Meta(memory.Image + rva, 28, "PersonalShop complete base descriptor");
            uint td = U32(bcd), contained = U32(bcd, 4), hierarchy = U32(bcd, 24);
            string name = Name(td);
            int mdisp = I32(bcd, 8), pdisp = I32(bcd, 12), vdisp = I32(bcd, 16);
            Need(name == expected[index] && contained == expected.Length - index - 1 &&
                mdisp == (index == 4 ? 8 : 0) && pdisp == -1 && vdisp == 0 && U32(bcd, 20) == 0x40 &&
                (index != 0 || td == type.Descriptor) &&
                (hierarchy == 0 || ((hierarchy & 3) == 0 && InImage(memory.Image + hierarchy, 16))),
                "PERSONAL_SHOP_HIERARCHY", "玩家商店基底名稱／PMD／contained／階層引用不符：" + index);
            bases.Add(new { index, name, contained, mdisp, pdisp, vdisp });
        }
        ClosePersonalShopProofRange(proofStart, "PersonalShop wrapper hierarchy bounded B");
        return new { type, bases, hierarchyVerified = true, firstTwoSlotsRequired = false,
            localHierarchyABClosed = true, pendingFinalAB = true, functionsCalled = false };
    }

    private void ClosePersonalShopProofRange(int start, string purpose)
    {
        int end = proofs.Count;
        Need(start >= 0 && start <= end && end - start <= 2048,
            "SHOP_PROOF_LIMIT", "單節點商店 A/B 證據超過界限。");
        for (int index = start; index < end; index++)
        {
            var proof = proofs[index];
            Need(Raw(proof.Address, proof.Bytes.Length, purpose + ": " + proof.Purpose).AsSpan().SequenceEqual(proof.Bytes),
                "GROUP_SOURCE_CHANGED", "玩家商店所选來源／型別／集合／文字在局部 B 核對時變更。");
        }
    }

    private static int PersonalShopVectorCount(byte[] header, int limit, string path)
    {
        ulong begin = U64(header), end = U64(header, 8), cap = U64(header, 16);
        if (begin == 0 || end == 0 || cap == 0)
        {
            Need(begin == 0 && end == 0 && cap == 0, "VECTOR", path + " 空 vector 必須全零。");
            return 0;
        }
        Ptr(begin); Ptr(end); Ptr(cap);
        Need(begin <= end && end <= cap && (end - begin) % 8 == 0 && (cap - begin) % 8 == 0 &&
            (end - begin) / 8 <= (uint)limit && (cap - begin) / 8 <= 4096,
            "VECTOR_LIMIT", path + " vector header 超出有界範圍。");
        return checked((int)((end - begin) / 8));
    }

    private static ulong? ParsePersonalShopPrice(string text)
    {
        // Only plain decimal or conventional comma groups; no currency/units or
        // sign stripping. This stays a textual numeric candidate, never stock.
        return PersonalShopTextRules.ParsePrice(text);
    }

    private static bool PersonalShopRecoverable(ProbeFailure error) => error.Code.StartsWith("PERSONAL_SHOP_", StringComparison.Ordinal) ||
        error.Code is "EXACT_TYPE" or "NODE_HIERARCHY" or "NODE_BASE_OFFSET" or "NODE_BASE" or
            "RTTI_VTABLE" or "RTTI_COL" or "RTTI_NAME" or "METADATA_RANGE" or "POINTER_RANGE" or
            "VECTOR" or "VECTOR_LIMIT" or "VECTOR_DUPLICATE" or "STRING_LIMIT" or "STRING_NUL" or
            "PAGE_UNREADABLE" or "READ_PARTIAL" or "READ_RANGE" or "SHOP_READ_FAILED";
    // Source/process/build changes, COLLECTION_CHANGED, time/read/proof budgets,
    // and unexpected failures deliberately propagate to Run; never a row fallback.
}

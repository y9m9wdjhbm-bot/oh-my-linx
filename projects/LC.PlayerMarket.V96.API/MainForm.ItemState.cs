using MemoryHub;
namespace LcPlayerMarket;
internal sealed partial class MainForm
{
    private readonly ComboBox itemStateFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=265};
    private readonly CheckBox groupStatePrices=Check("依物品分類分組比價",false);
    private bool updatingItemStateChoices;
    private sealed record ItemStateChoice(string? Key,string Label){public override string ToString()=>Label;}
    private void InitializeItemStateControls()
    {
        itemStateFilter.Items.Add(new ItemStateChoice(null,"全部狀態"));itemStateFilter.SelectedIndex=0;
        itemStateFilter.SelectedIndexChanged+=(_,_)=>{if(!updatingItemStateChoices)RefreshRows();};
        groupStatePrices.CheckedChanged+=(_,_)=>RefreshRows();
    }
    private void UpdateItemStateChoices()
    {
        var selected=itemStateFilter.SelectedItem as ItemStateChoice;
        var choices=history.Values.SelectMany(listing=>listing.Rows.Where(row=>row.IncludedInListing))
            .Select(row=>new ItemStateChoice(MarketItemStatePresentation.FilterKey(row),MarketItemStatePresentation.FilterLabel(row)))
            .DistinctBy(choice=>choice.Key).OrderBy(choice=>choice.Key,StringComparer.Ordinal).ToList();
        if(selected?.Key is not null&&!choices.Any(choice=>choice.Key==selected.Key))choices.Add(selected);
        choices.Insert(0,new(null,"全部狀態"));
        if(itemStateFilter.Items.Cast<ItemStateChoice>().SequenceEqual(choices))return;
        updatingItemStateChoices=true;itemStateFilter.BeginUpdate();
        try{itemStateFilter.Items.Clear();itemStateFilter.Items.AddRange(choices.Cast<object>().ToArray());itemStateFilter.SelectedIndex=Math.Max(0,choices.FindIndex(choice=>choice.Key==selected?.Key));}
        finally{itemStateFilter.EndUpdate();updatingItemStateChoices=false;}
    }
}

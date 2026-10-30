namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private void ConfigureItemStateColumnLayout()
    {
        foreach (var side in Sides)
        {
            void SizeStateColumn()
            {
                var column = side.Grid.Columns[nameof(MarketRowView.ItemKind)];
                if (column is null) return;
                // Keep the evidence qualifier legible at the actual inherited font.
                // The existing horizontal scrollbar remains available in narrow windows.
                int textWidth = TextRenderer.MeasureText("受祝福（已鑑定；版本認證）", side.Grid.Font).Width;
                column.Width = Math.Max(column.Width, Math.Max(265, textWidth + 24));
            }
            side.Grid.FontChanged += (_, _) => SizeStateColumn();
            SizeStateColumn();
        }
    }
}

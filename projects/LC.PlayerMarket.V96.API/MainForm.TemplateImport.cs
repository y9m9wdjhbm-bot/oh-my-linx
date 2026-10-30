using MemoryHub.Reader;

namespace LcPlayerMarket;

internal sealed partial class MainForm
{
    private void ImportAddressTemplate()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "選擇記憶體地址模板", Filter = "LC 地址模板 (*.json)|*.json", CheckFileExists = true,
            InitialDirectory = Directory.Exists(@"D:\lineage\LC_MemoryLocationTemplate_v1\profiles")
                ? @"D:\lineage\LC_MemoryLocationTemplate_v1\profiles" : AppContext.BaseDirectory
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var result = TemplateProfileImport.Import(dialog.FileName, Path.Combine(AppContext.BaseDirectory, "profiles"));
            Report("TEMPLATE_IMPORTED", "已保存新版地址模板，重新啟動後套用；每個讀值仍會核對目前 LC 來源與型別。", result);
            MessageBox.Show(this, $"已保存 {result.CandidateFields} 個候選欄位的地址模板。\n請重新啟動商店程式套用。\n\n地址必須通過目前 LC 的來源、型別與讀值核對後才會顯示；商店原生查詢相容性需另外完成驗證。",
                "地址模板已保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception error)
        {
            Report("TEMPLATE_IMPORT_REJECTED", "模板無法使用：" + error.Message);
            MessageBox.Show(this, error.Message, "模板無法使用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

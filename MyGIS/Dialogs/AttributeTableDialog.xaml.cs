using System.Data;
using System.Windows;
using Mapsui;

namespace MyGIS.Dialogs
{
    public partial class AttributeTableDialog : Window
    {
        public AttributeTableDialog(string layerName, int featureCount,
            IEnumerable<IFeature> features, string[] displayNames, string[] lookupKeys)
        {
            InitializeComponent();
            Title = $"属性表 - {layerName}";
            InfoText.Text = $"图层: {layerName}   要素数: {featureCount}   字段数: {lookupKeys.Length}";

            var table = new DataTable();
            table.Columns.Add("FID", typeof(int));

            for (int c = 0; c < lookupKeys.Length; c++)
                table.Columns.Add(displayNames[c], typeof(string));

            uint fid = 0;
            foreach (var f in features)
            {
                var row = table.NewRow();
                row["FID"] = (int)fid;
                for (int c = 0; c < lookupKeys.Length; c++)
                {
                    var val = f[lookupKeys[c]];
                    row[displayNames[c]] = val?.ToString() ?? "";
                }
                table.Rows.Add(row);
                fid++;
            }

            AttrGrid.ItemsSource = table.DefaultView;
            StatusText.Text = $"共 {featureCount} 条记录";
        }
    }
}

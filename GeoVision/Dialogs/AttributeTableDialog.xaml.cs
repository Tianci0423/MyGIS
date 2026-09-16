using System.Data;
using System.Windows;
using Mapsui;

namespace GeoVision.Dialogs
{
    public partial class AttributeTableDialog : Window
    {
        public AttributeTableDialog(string layerName, DataTable table, int featureCount)
        {
            InitializeComponent();
            Title = $"属性表 - {layerName}";
            InfoText.Text = $"图层: {layerName}   要素数: {featureCount}   字段数: {Math.Max(0, table.Columns.Count - 1)}";
            AttrGrid.ItemsSource = table.DefaultView;
            StatusText.Text = $"显示 {table.Rows.Count} 条记录，共 {featureCount} 条";
        }

        public AttributeTableDialog(string layerName, int featureCount,
            IEnumerable<IFeature> features, string[] displayNames, string[] lookupKeys)
            : this(layerName, BuildFeatureTable(features, displayNames, lookupKeys), featureCount)
        {
        }

        private static DataTable BuildFeatureTable(
            IEnumerable<IFeature> features,
            string[] displayNames,
            string[] lookupKeys)
        {
            var table = new DataTable();
            table.Columns.Add("FID", typeof(int));
            foreach (string name in displayNames)
                table.Columns.Add(name, typeof(string));

            int fid = 0;
            foreach (var feature in features)
            {
                var row = table.NewRow();
                row["FID"] = fid++;
                for (int c = 0; c < lookupKeys.Length; c++)
                    row[displayNames[c]] = feature[lookupKeys[c]]?.ToString() ?? "";
                table.Rows.Add(row);
            }

            return table;
        }
    }
}

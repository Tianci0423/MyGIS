using System.Windows;

namespace GeoVision.Dialogs
{
    public partial class LayerPropertiesDialog : Window
    {
        public LayerPropertiesDialog(
            string name, string layerType, string filePath, string crs,
            double? minX, double? minY, double? maxX, double? maxY,
            bool isLonLat = false,
            int? rasterWidth = null, int? rasterHeight = null,
            int? bandCount = null, string? renderer = null,
            int? overviewCount = null, string? stretchType = null,
            int? featureCount = null, string? encoding = null)
        {
            InitializeComponent();

            LayerNameText.Text = name;
            LayerTypeText.Text = layerType;
            FilePathText.Text = filePath;
            CrsText.Text = crs;

            if (isLonLat)
            {
                MinXLabel.Text = "最小经度"; MaxXLabel.Text = "最大经度";
                MinYLabel.Text = "最小纬度"; MaxYLabel.Text = "最大纬度";
            }
            else
            {
                MinXLabel.Text = "最小X"; MaxXLabel.Text = "最大X";
                MinYLabel.Text = "最小Y"; MaxYLabel.Text = "最大Y";
            }

            bool hasExtent = minX.HasValue && maxX.HasValue && minY.HasValue && maxY.HasValue;
            MinXText.Text = hasExtent ? minX!.Value.ToString("F6") : "—";
            MaxXText.Text = hasExtent ? maxX!.Value.ToString("F6") : "—";
            MinYText.Text = hasExtent ? minY!.Value.ToString("F6") : "—";
            MaxYText.Text = hasExtent ? maxY!.Value.ToString("F6") : "—";

            if (rasterWidth.HasValue)
            {
                RasterPanel.Visibility = Visibility.Visible;
                RasterWidthText.Text = rasterWidth.Value.ToString("N0");
                RasterHeightText.Text = rasterHeight!.Value.ToString("N0");
                BandCountText.Text = bandCount!.Value.ToString();
                RendererText.Text = renderer ?? "—";
                OverviewText.Text = overviewCount!.Value.ToString();
                StretchText.Text = stretchType ?? "—";
            }
            else if (featureCount.HasValue)
            {
                VectorPanel.Visibility = Visibility.Visible;
                FeatureCountText.Text = featureCount.Value.ToString("N0");
                EncodingText.Text = encoding ?? "—";
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    }
}

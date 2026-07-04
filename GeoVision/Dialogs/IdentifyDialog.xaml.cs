using System.Windows;
using System.Windows.Controls;

namespace GeoVision.Dialogs
{
    public partial class IdentifyDialog : Window
    {
        public class ResultItem
        {
            public string LayerName { get; init; } = string.Empty;
            public List<string> BandValues { get; init; } = new();
        }

        private double _lon, _lat;
        private string _layerMode = "Visible";

        public string LayerMode => _layerMode;
        public event Action? ModeChanged;
        public event Action? ClosedByUser;

        public IdentifyDialog()
        {
            InitializeComponent();
        }

        public void UpdateResults(double lon, double lat, string layerMode,
            List<(string name, List<string> bandValues)> results)
        {
            _lon = lon;
            _lat = lat;
            _layerMode = layerMode;

            LonText.Text = $"{lon:F6}°";
            LatText.Text = $"{lat:F6}°";

            for (int i = 0; i < LayerModeCombo.Items.Count; i++)
            {
                if (LayerModeCombo.Items[i] is ComboBoxItem item &&
                    item.Tag?.ToString() == layerMode)
                {
                    LayerModeCombo.SelectedIndex = i;
                    break;
                }
            }

            ResultList.Items.Clear();
            if (results.Count == 0)
            {
                NoDataHint.Visibility = Visibility.Visible;
                ResultList.Visibility = Visibility.Collapsed;
            }
            else
            {
                NoDataHint.Visibility = Visibility.Collapsed;
                ResultList.Visibility = Visibility.Visible;
                foreach (var r in results)
                    ResultList.Items.Add(new ResultItem { LayerName = r.name, BandValues = r.bandValues });
            }
        }

        private void OnLayerModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LayerModeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _layerMode = tag;
                ModeChanged?.Invoke();
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            ClosedByUser?.Invoke();
            Close();
        }
    }
}

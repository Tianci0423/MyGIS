using System.Windows;
using GeoVision.Providers;

namespace GeoVision.Dialogs
{
    public sealed record RasterSwipeLayerOption(string Name, GdalRasterProvider Provider);

    public partial class RasterSwipeDialog : Window
    {
        public RasterSwipeLayerOption? LeftLayer => LeftImageBox.SelectedItem as RasterSwipeLayerOption;
        public RasterSwipeLayerOption? RightLayer => RightImageBox.SelectedItem as RasterSwipeLayerOption;

        public RasterSwipeDialog(IReadOnlyList<RasterSwipeLayerOption> layers)
        {
            InitializeComponent();
            LeftImageBox.ItemsSource = layers;
            RightImageBox.ItemsSource = layers;
            if (layers.Count > 0)
                LeftImageBox.SelectedIndex = 0;
            if (layers.Count > 1)
                RightImageBox.SelectedIndex = 1;
        }

        private void OnSwapClick(object sender, RoutedEventArgs e)
        {
            int leftIndex = LeftImageBox.SelectedIndex;
            LeftImageBox.SelectedIndex = RightImageBox.SelectedIndex;
            RightImageBox.SelectedIndex = leftIndex;
            ValidationText.Visibility = Visibility.Collapsed;
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            if (LeftLayer == null || RightLayer == null)
            {
                ShowValidation("请选择左右两幅影像。");
                return;
            }

            if (ReferenceEquals(LeftLayer.Provider, RightLayer.Provider))
            {
                ShowValidation("左右两侧不能选择同一幅影像。");
                return;
            }

            DialogResult = true;
            Close();
        }

        private void ShowValidation(string message)
        {
            ValidationText.Text = message;
            ValidationText.Visibility = Visibility.Visible;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

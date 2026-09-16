using System.Windows;

namespace GeoVision.Dialogs
{
    public enum CrsMismatchAction
    {
        Reproject,
        Continue,
        Cancel
    }

    public partial class CrsMismatchDialog : Window
    {
        public CrsMismatchAction Action { get; private set; } = CrsMismatchAction.Cancel;

        public CrsMismatchDialog(
            string fileName,
            string mapCrsName,
            string layerCrsName,
            bool canReproject)
        {
            InitializeComponent();
            MessageText.Text =
                $"图层“{fileName}”的坐标系与当前地图不一致。\n\n" +
                $"当前地图：{mapCrsName}\n" +
                $"当前图层：{layerCrsName}";
            ReprojectButton.IsEnabled = canReproject;
            if (!canReproject)
            {
                ReprojectButton.ToolTip = "当前版本仅支持栅格图层立即重投影";
                ReprojectButton.Content = "暂不支持";
            }
        }

        private void OnReprojectClick(object sender, RoutedEventArgs e)
        {
            if (!ReprojectButton.IsEnabled)
                return;
            Action = CrsMismatchAction.Reproject;
            DialogResult = true;
        }

        private void OnContinueClick(object sender, RoutedEventArgs e)
        {
            Action = CrsMismatchAction.Continue;
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Action = CrsMismatchAction.Cancel;
            DialogResult = false;
        }
    }
}

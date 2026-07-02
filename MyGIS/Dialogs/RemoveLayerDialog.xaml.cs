using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace MyGIS.Dialogs
{
    public partial class RemoveLayerDialog : Window
    {
        public enum RemoveAction { Delete, SaveAs, Cancel }

        public RemoveAction Action { get; private set; } = RemoveAction.Cancel;
        public string? SaveAsPath { get; private set; }

        private readonly string? _filePath;

        public RemoveLayerDialog(string layerName, bool isTempFile, string? filePath)
        {
            InitializeComponent();
            _filePath = filePath;

            if (isTempFile)
            {
                MessageText.Text = $"确定要移除图层 \"{layerName}\" 吗？\n\n该图层是波段计算结果，当前文件位于：\n{filePath}";
                DeleteBtn.Visibility = Visibility.Visible;
                KeepBtn.Visibility = Visibility.Visible;
            }
            else
            {
                MessageText.Text = $"确定要移除图层 \"{layerName}\" 吗？";
                DeleteBtn.Visibility = Visibility.Collapsed;
                KeepBtn.Content = "确定";
            }
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            Action = RemoveAction.Delete;
            DialogResult = true;
            Close();
        }

        private void OnKeepClick(object sender, RoutedEventArgs e)
        {
            if (_filePath != null && DeleteBtn.Visibility == Visibility.Visible)
            {
                var dlg = new SaveFileDialog
                {
                    Title = "保存波段计算结果",
                    FileName = Path.GetFileName(_filePath),
                    Filter = "GeoTIFF|*.tif|所有文件|*.*",
                    DefaultExt = ".tif"
                };
                if (dlg.ShowDialog() == true)
                {
                    try
                    {
                        File.Copy(_filePath, dlg.FileName, overwrite: true);
                        var ovrPath = _filePath + ".ovr";
                        var destOvr = dlg.FileName + ".ovr";
                        if (File.Exists(ovrPath))
                            File.Copy(ovrPath, destOvr, overwrite: true);

                        SaveAsPath = dlg.FileName;
                        Action = RemoveAction.SaveAs;
                        DialogResult = true;
                        Close();
                        return;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"保存失败: {ex.Message}", "错误",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                return;
            }

            Action = RemoveAction.SaveAs;
            DialogResult = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Action = RemoveAction.Cancel;
            DialogResult = false;
            Close();
        }
    }
}

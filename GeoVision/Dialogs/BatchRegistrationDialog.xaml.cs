using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GeoVision.Dialogs
{
    public partial class BatchRegistrationDialog : Window
    {
        private readonly ObservableCollection<BatchRegistrationTask> _tasks = new();
        private readonly IReadOnlyList<RasterLayerInfo> _msLayers;
        private readonly IReadOnlyList<RasterLayerInfo> _panLayers;
        private const string RasterRolePlaceholder = "{RASTER_ROLE}";
        private static readonly Regex RasterRoleTokenRegex = new(
            @"(^|[^A-Za-z0-9])(MUL|MS|PAN)(?=$|[^A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RasterRolePrefixRegex = new(
            @"^(MUL|MS|PAN)(?=[A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RasterRoleSuffixRegex = new(
            @"(?<=[A-Za-z0-9])(MUL|MS|PAN)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public IReadOnlyList<RegistrationRequest> Requests { get; private set; } = Array.Empty<RegistrationRequest>();
        public bool ContinueOnError { get; private set; } = true;

        public BatchRegistrationDialog(IReadOnlyList<RasterLayerInfo>? availableLayers = null)
        {
            InitializeComponent();

            var layers = availableLayers ?? Array.Empty<RasterLayerInfo>();
            _msLayers = layers.Where(l => l.BandCount == 4).ToList();
            _panLayers = layers.Where(l => l.BandCount == 1).ToList();

            MsBox.ItemsSource = _msLayers;
            PanBox.ItemsSource = _panLayers;
            TaskGrid.ItemsSource = _tasks;

            UpdateSummary();
        }

        private void OnBrowseMs(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 MS/MUL 影像（可单选或多选）",
                Filter = "Raster|*.tif;*.tiff|All files|*.*",
                Multiselect = true
            };

            if (dlg.ShowDialog(this) == true)
            {
                if (dlg.FileNames.Length == 1)
                {
                    SetSingleRasterText(MsBox, dlg.FileName);
                }
                else
                {
                    AddBatchTasksFromSelections(dlg.FileNames);
                }
            }
        }

        private void OnBrowsePan(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择 PAN 影像",
                Filter = "Raster|*.tif;*.tiff|All files|*.*"
            };

            if (dlg.ShowDialog(this) == true)
            {
                SetSingleRasterText(PanBox, dlg.FileName);
            }
        }

        private void OnBrowseOutputDir(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "选择输出目录"
            };

            if (dlg.ShowDialog(this) == true)
            {
                OutputDirBox.Text = dlg.FolderName;
                foreach (var task in _tasks.Where(t => string.IsNullOrWhiteSpace(t.OutputDir)))
                    task.OutputDir = dlg.FolderName;
            }
        }

        private void OnAddTask(object sender, RoutedEventArgs e)
        {
            string ms = ResolveRasterPath(MsBox);
            string pan = ResolveRasterPath(PanBox);
            string dir = OutputDirBox.Text.Trim();

            TryAddTask(ms, pan, dir, true, out _);
        }

        private void AddBatchTasksFromSelections(IReadOnlyList<string> msPaths)
        {
            if (msPaths.Count == 0)
                return;

            var panDlg = new OpenFileDialog
            {
                Title = $"选择 PAN 影像（请多选，与 {msPaths.Count} 个 MS/MUL 按同名匹配）",
                Filter = "Raster|*.tif;*.tiff|All files|*.*",
                Multiselect = true
            };

            if (panDlg.ShowDialog(this) != true || panDlg.FileNames.Length == 0)
            {
                SetBatchSelectionSummary(msPaths, Array.Empty<string>(), 0, 0, 0, 0);
                return;
            }

            string outputDir = OutputDirBox.Text.Trim();
            var panByName = BuildRasterMap(panDlg.FileNames);
            int added = 0;
            int duplicates = 0;
            int missingPan = 0;
            int invalid = 0;
            var details = new List<string>();

            foreach (string ms in msPaths)
            {
                if (!TryFindRasterMatch(panByName, ms, out string? pan))
                {
                    missingPan++;
                    if (details.Count < 6)
                        details.Add($"{Path.GetFileName(ms)}：未找到同名 PAN");
                    continue;
                }

                var result = TryAddTask(ms, pan!, outputDir, false, out string message);
                if (result == AddTaskResult.Added)
                {
                    added++;
                }
                else if (result == AddTaskResult.Duplicate)
                {
                    duplicates++;
                }
                else
                {
                    invalid++;
                    if (details.Count < 6)
                        details.Add($"{Path.GetFileName(ms)}：{message}");
                }
            }

            UpdateSummary();
            SetBatchSelectionSummary(msPaths, panDlg.FileNames, added, duplicates, missingPan, invalid);

            string summary =
                $"批量加入完成。\n\n" +
                $"已加入：{added}\n" +
                $"重复跳过：{duplicates}\n" +
                $"未匹配 PAN：{missingPan}\n" +
                $"校验失败：{invalid}";
            if (details.Count > 0)
                summary += "\n\n" + string.Join("\n", details);

            MessageBox.Show(this, summary, "批量加入", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static Dictionary<string, string> BuildRasterMap(IEnumerable<string> paths)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                foreach (string key in GetRasterMatchKeys(path))
                {
                    if (!result.ContainsKey(key))
                        result[key] = path;
                }
            }

            return result;
        }

        private static bool TryFindRasterMatch(
            IReadOnlyDictionary<string, string> rasterByKey,
            string path,
            out string? match)
        {
            foreach (string key in GetRasterMatchKeys(path))
            {
                if (rasterByKey.TryGetValue(key, out match))
                    return true;
            }

            match = null;
            return false;
        }

        private static IReadOnlyList<string> GetRasterMatchKeys(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path).Trim();
            var keys = new List<string> { name };

            AddRasterMatchKey(keys, RasterRoleTokenRegex.Replace(
                name,
                match => $"{match.Groups[1].Value}{RasterRolePlaceholder}"));
            AddRasterMatchKey(keys, RasterRolePrefixRegex.Replace(name, RasterRolePlaceholder));
            AddRasterMatchKey(keys, RasterRoleSuffixRegex.Replace(name, RasterRolePlaceholder));

            return keys;
        }

        private static void AddRasterMatchKey(List<string> keys, string key)
        {
            if (!string.IsNullOrWhiteSpace(key) &&
                !keys.Any(existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(key);
            }
        }

        private AddTaskResult TryAddTask(string ms, string pan, string dir, bool showMessages, out string message)
        {
            message = string.Empty;

            if (string.IsNullOrWhiteSpace(ms))
            {
                message = "请选择 MS 影像。";
                ShowAddTaskMessage(message, MessageBoxImage.Warning, showMessages);
                return AddTaskResult.ValidationFailed;
            }

            if (string.IsNullOrWhiteSpace(pan))
            {
                message = "请选择 PAN 影像。";
                ShowAddTaskMessage(message, MessageBoxImage.Warning, showMessages);
                return AddTaskResult.ValidationFailed;
            }

            if (!File.Exists(ms))
            {
                message = "MS 影像文件不存在。";
                ShowAddTaskMessage(message, MessageBoxImage.Warning, showMessages);
                return AddTaskResult.ValidationFailed;
            }

            if (!File.Exists(pan))
            {
                message = "PAN 影像文件不存在。";
                ShowAddTaskMessage(message, MessageBoxImage.Warning, showMessages);
                return AddTaskResult.ValidationFailed;
            }

            if (!RegistrationDialog.ValidateRegistrationInputs(ms, pan, showMessages))
            {
                message = "输入影像不满足配准要求。";
                return AddTaskResult.ValidationFailed;
            }

            if (_tasks.Any(t =>
                    string.Equals(t.MsPath, ms, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.PanPath, pan, StringComparison.OrdinalIgnoreCase)))
            {
                message = "该 MS/PAN 组合已经在任务列表中。";
                ShowAddTaskMessage(message, MessageBoxImage.Information, showMessages);
                return AddTaskResult.Duplicate;
            }

            _tasks.Add(new BatchRegistrationTask
            {
                Index = _tasks.Count + 1,
                Status = "等待",
                MsPath = ms,
                PanPath = pan,
                OutputDir = dir
            });

            UpdateSummary();
            return AddTaskResult.Added;
        }

        private void ShowAddTaskMessage(string message, MessageBoxImage icon, bool showMessages)
        {
            if (showMessages)
                MessageBox.Show(this, message, "提示", MessageBoxButton.OK, icon);
        }

        private void OnRemoveSelected(object sender, RoutedEventArgs e)
        {
            var selected = TaskGrid.SelectedItems.Cast<BatchRegistrationTask>().ToList();
            if (selected.Count == 0)
                return;

            foreach (var task in selected)
                _tasks.Remove(task);

            ReindexTasks();
            UpdateSummary();
        }

        private void OnClearTasks(object sender, RoutedEventArgs e)
        {
            if (_tasks.Count == 0)
                return;

            _tasks.Clear();
            UpdateSummary();
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            if (_tasks.Count == 0)
            {
                MessageBox.Show(this, "请先加入至少一个配准任务。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string batchOutputDir = OutputDirBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(batchOutputDir) &&
                _tasks.Any(t => string.IsNullOrWhiteSpace(t.OutputDir)))
            {
                MessageBox.Show(this, "请选择输出目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string resample = "bilinear";
            if (ResampleBox.SelectedItem is ComboBoxItem item && item.Tag != null)
                resample = item.Tag.ToString()!;

            string batchId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var requests = new List<RegistrationRequest>(_tasks.Count);

            for (int i = 0; i < _tasks.Count; i++)
            {
                var task = _tasks[i];
                string outputDir = string.IsNullOrWhiteSpace(task.OutputDir) ? batchOutputDir : task.OutputDir;
                try
                {
                    Directory.CreateDirectory(outputDir);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"无法创建输出目录：\n{outputDir}\n\n{ex.Message}",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string suffix = $"{batchId}_{i + 1:D2}";
                string msOut = Path.Combine(
                    outputDir,
                    $"{Path.GetFileNameWithoutExtension(task.MsPath)}_aligned_ms_{suffix}.tif");
                string panOut = Path.Combine(
                    outputDir,
                    $"{Path.GetFileNameWithoutExtension(task.PanPath)}_aligned_pan_{suffix}.tif");

                requests.Add(new RegistrationRequest(
                    RegistrationDialog.GetPythonExe(),
                    RegistrationDialog.GetScriptPath(),
                    task.MsPath,
                    task.PanPath,
                    msOut,
                    panOut,
                    resample,
                    LoadAfterRegistrationBox.IsChecked == true,
                    KeepBlackBorderBox.IsChecked == true));
            }

            Requests = requests;
            ContinueOnError = ContinueOnErrorBox.IsChecked == true;
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void SetSingleRasterText(ComboBox box, string path)
        {
            box.SelectedItem = null;
            box.Text = path;
            box.ToolTip = path;
        }

        private void SetBatchSelectionSummary(
            IReadOnlyList<string> msPaths,
            IReadOnlyList<string> panPaths,
            int added,
            int duplicates,
            int missingPan,
            int invalid)
        {
            MsBox.SelectedItem = null;
            PanBox.SelectedItem = null;
            MsBox.Text = JoinPaths(msPaths);
            PanBox.Text = JoinPaths(panPaths);

            string tooltip =
                $"MS/MUL：{msPaths.Count} 个\n" +
                $"{JoinPathsForTooltip(msPaths)}\n\n" +
                $"PAN：{panPaths.Count} 个\n" +
                $"{JoinPathsForTooltip(panPaths)}\n\n" +
                $"已加入：{added}\n" +
                $"重复跳过：{duplicates}\n" +
                $"未匹配 PAN：{missingPan}\n" +
                $"校验失败：{invalid}";
            MsBox.ToolTip = tooltip;
            PanBox.ToolTip = tooltip;
        }

        private static string JoinPaths(IReadOnlyList<string> paths)
            => string.Join("，", paths);

        private static string JoinPathsForTooltip(IReadOnlyList<string> paths)
            => paths.Count > 0 ? string.Join("\n", paths) : "未选择";

        private static string ResolveRasterPath(ComboBox box)
        {
            if (box.SelectedItem is RasterLayerInfo info)
                return info.FilePath;

            string text = box.Text.Trim();
            return IsBatchSelectionSummary(text) ? string.Empty : text;
        }

        private static bool IsBatchSelectionSummary(string text)
            => text.StartsWith("已选择 ", StringComparison.Ordinal) ||
               text.StartsWith("未选择 PAN", StringComparison.Ordinal) ||
               (text.Contains('，') && !File.Exists(text));

        private void ReindexTasks()
        {
            for (int i = 0; i < _tasks.Count; i++)
                _tasks[i].Index = i + 1;
        }

        private void UpdateSummary()
        {
            SummaryText.Text = $"{_tasks.Count} 个任务";
        }

        private enum AddTaskResult
        {
            Added,
            Duplicate,
            ValidationFailed
        }

        private sealed class BatchRegistrationTask : INotifyPropertyChanged
        {
            private int _index;
            private string _status = string.Empty;
            private string _msPath = string.Empty;
            private string _panPath = string.Empty;
            private string _outputDir = string.Empty;

            public int Index
            {
                get => _index;
                set => SetField(ref _index, value);
            }

            public string Status
            {
                get => _status;
                set => SetField(ref _status, value);
            }

            public string MsPath
            {
                get => _msPath;
                set
                {
                    if (SetField(ref _msPath, value))
                        OnPropertyChanged(nameof(MsDisplay));
                }
            }

            public string PanPath
            {
                get => _panPath;
                set
                {
                    if (SetField(ref _panPath, value))
                        OnPropertyChanged(nameof(PanDisplay));
                }
            }

            public string OutputDir
            {
                get => _outputDir;
                set => SetField(ref _outputDir, value);
            }

            public string MsDisplay => Path.GetFileName(MsPath);
            public string PanDisplay => Path.GetFileName(PanPath);

            public event PropertyChangedEventHandler? PropertyChanged;

            private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
            {
                if (EqualityComparer<T>.Default.Equals(field, value))
                    return false;

                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

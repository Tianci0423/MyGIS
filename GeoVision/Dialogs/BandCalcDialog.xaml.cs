using System.IO;
using System.Windows;
using System.Windows.Controls;
using OSGeo.GDAL;
using GeoVision.Providers;

namespace GeoVision.Dialogs
{
    public partial class BandCalcDialog : Window
    {
        public string? ResultFilePath { get; private set; }
        public string? ResultLayerName { get; private set; }
        public string SourcePath { get; private set; } = string.Empty;
        public string Formula { get; private set; } = string.Empty;
        public int BandCount { get; private set; }

        private readonly List<(string Name, string FilePath, int Bands)> _rasters;

        public BandCalcDialog(List<(string Name, string FilePath, int Bands)> rasters)
        {
            InitializeComponent();
            _rasters = rasters;

            foreach (var r in rasters)
                SourceLayerCombo.Items.Add($"{r.Name} ({r.Bands} 波段)");

            if (SourceLayerCombo.Items.Count > 0)
                SourceLayerCombo.SelectedIndex = 0;
        }

        private void OnSourceLayerChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SourceLayerCombo.SelectedIndex < 0) return;
            var r = _rasters[SourceLayerCombo.SelectedIndex];
            BandInfoText.Text = $"可用波段: b1 ～ b{r.Bands}";
        }

        private void OnPresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;

            switch (tag)
            {
                case "NDVI":
                    FormulaBox.Text = "(b4-b3)/(b4+b3)";
                    OutputNameBox.Text = "NDVI";
                    break;
                case "NDWI":
                    FormulaBox.Text = "(b2-b4)/(b2+b4)";
                    OutputNameBox.Text = "NDWI";
                    break;
                case "EVI":
                    FormulaBox.Text = "2.5*(b4-b3)/(b4+6*b3-7.5*b1+1)";
                    OutputNameBox.Text = "EVI";
                    break;
                case "NDBI":
                    FormulaBox.Text = "(b5-b4)/(b5+b4)";
                    OutputNameBox.Text = "NDBI";
                    break;
                case "SAVI":
                    FormulaBox.Text = "1.5*(b4-b3)/(b4+b3+0.5)";
                    OutputNameBox.Text = "SAVI";
                    break;
            }
        }

        private void OnComputeClick(object sender, RoutedEventArgs e)
        {
            if (SourceLayerCombo.SelectedIndex < 0)
            {
                MessageBox.Show("请选择源图层。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string formula = FormulaBox.Text.Trim();
            if (string.IsNullOrEmpty(formula))
            {
                MessageBox.Show("请输入公式。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var raster = _rasters[SourceLayerCombo.SelectedIndex];
            string outName = OutputNameBox.Text.Trim();
            if (string.IsNullOrEmpty(outName)) outName = "result";

            SourcePath = raster.FilePath;
            Formula = formula;
            BandCount = raster.Bands;
            ResultLayerName = outName;
            DialogResult = true;
            Close();
        }

        public static string ComputeBandMath(string srcPath, string formula, int totalBands, string outName,
            IProgress<(int percent, string label)>? progress = null)
        {
            using var srcDs = Gdal.Open(srcPath, Access.GA_ReadOnly);
            if (srcDs == null) throw new Exception("无法打开源文件");

            int w = srcDs.RasterXSize, h = srcDs.RasterYSize;

            // Read all bands into memory
            int totalPixels = w * h;
            var bands = new float[totalBands][];
            var ndvs = new double[totalBands];
            var hasNdvs = new bool[totalBands];

            for (int b = 0; b < totalBands; b++)
            {
                bands[b] = new float[totalPixels];
                srcDs.GetRasterBand(b + 1).ReadRaster(0, 0, w, h, bands[b], w, h, 0, 0);
                srcDs.GetRasterBand(b + 1).GetNoDataValue(out double ndv, out int hasNdv);
                ndvs[b] = ndv;
                hasNdvs[b] = hasNdv != 0;
            }

            // Build expression evaluator
            var evaluator = BuildFormulaEvaluator(formula, totalBands);
            var result = new float[totalPixels];
            var noDataMask = new bool[totalPixels];

            progress?.Report((0, "波段计算中..."));

            int lastReport = 0;
            for (int i = 0; i < totalPixels; i++)
            {
                int pct = (int)((long)i * 100 / totalPixels);
                if (pct > lastReport) { lastReport = pct; progress?.Report((pct, $"波段计算中... {pct}%")); }

                // Check NoData
                bool isNoData = false;
                for (int b = 0; b < totalBands; b++)
                {
                    float v = bands[b][i];
                    if (float.IsNaN(v) || float.IsInfinity(v) ||
                        (hasNdvs[b] && Math.Abs(v - ndvs[b]) < 0.0001))
                    {
                        isNoData = true;
                        break;
                    }
                }
                if (isNoData)
                {
                    noDataMask[i] = true;
                    continue;
                }

                result[i] = evaluator(i, bands);
            }

            progress?.Report((100, "计算完成"));

            // Write to temp GeoTIFF
            string outDir = Path.Combine(Path.GetTempPath(), "GeoVision");
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, $"{outName}_{DateTime.Now:yyyyMMdd_HHmmss}.tif");

            using var outDs = Gdal.GetDriverByName("GTiff").Create(
                outPath, w, h, 1, DataType.GDT_Float32, new[] { "COMPRESS=NONE", "TILED=YES" });

            double[] gt = new double[6];
            srcDs.GetGeoTransform(gt);
            outDs.SetGeoTransform(gt);
            outDs.SetProjection(srcDs.GetProjection());

            var outBand = outDs.GetRasterBand(1);
            outBand.WriteRaster(0, 0, w, h, result, w, h, 0, 0);
            outBand.SetNoDataValue(-9999);
            outBand.ComputeStatistics(false, out _, out _, out _, out _, null, null);
            outDs.FlushCache();
            return outPath;
        }

        private static Func<int, float[][], float> BuildFormulaEvaluator(string formula, int totalBands)
        {
            // Simple expression evaluator supporting b1..bN, +, -, *, /, (, )
            return (i, bands) =>
            {
                int pos = 0;
                return ParseExpression(formula, ref pos, i, bands, totalBands);
            };
        }

        private static float ParseExpression(string expr, ref int pos, int i, float[][] bands, int totalBands)
        {
            float left = ParseTerm(expr, ref pos, i, bands, totalBands);
            while (pos < expr.Length)
            {
                SkipSpace(expr, ref pos);
                if (pos >= expr.Length) break;
                char op = expr[pos];
                if (op == '+') { pos++; float right = ParseTerm(expr, ref pos, i, bands, totalBands); left += right; }
                else if (op == '-') { pos++; float right = ParseTerm(expr, ref pos, i, bands, totalBands); left -= right; }
                else break;
            }
            return left;
        }

        private static float ParseTerm(string expr, ref int pos, int i, float[][] bands, int totalBands)
        {
            float left = ParseFactor(expr, ref pos, i, bands, totalBands);
            while (pos < expr.Length)
            {
                SkipSpace(expr, ref pos);
                if (pos >= expr.Length) break;
                char op = expr[pos];
                if (op == '*') { pos++; float right = ParseFactor(expr, ref pos, i, bands, totalBands); left *= right; }
                else if (op == '/') { pos++; float right = ParseFactor(expr, ref pos, i, bands, totalBands); left = (right != 0) ? left / right : 0; }
                else break;
            }
            return left;
        }

        private static float ParseFactor(string expr, ref int pos, int i, float[][] bands, int totalBands)
        {
            SkipSpace(expr, ref pos);
            if (pos >= expr.Length) return 0;

            if (expr[pos] == '(')
            {
                pos++;
                float result = ParseExpression(expr, ref pos, i, bands, totalBands);
                SkipSpace(expr, ref pos);
                if (pos < expr.Length && expr[pos] == ')') pos++;
                return result;
            }

            if (expr[pos] == 'b' || expr[pos] == 'B')
            {
                pos++;
                int bandNum = 0;
                while (pos < expr.Length && char.IsDigit(expr[pos]))
                {
                    bandNum = bandNum * 10 + (expr[pos] - '0');
                    pos++;
                }
                if (bandNum >= 1 && bandNum <= totalBands)
                    return bands[bandNum - 1][i];
                return 0;
            }

            // Parse number
            if (char.IsDigit(expr[pos]) || expr[pos] == '.')
            {
                int start = pos;
                while (pos < expr.Length && (char.IsDigit(expr[pos]) || expr[pos] == '.'))
                    pos++;
                if (float.TryParse(expr[start..pos],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float val))
                    return val;
            }

            pos++;
            return 0;
        }

        private static void SkipSpace(string expr, ref int pos)
        {
            while (pos < expr.Length && char.IsWhiteSpace(expr[pos])) pos++;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using GeoVision.Services;

namespace GeoVision.Dialogs
{
    public class RampItem
    {
        public string Name { get; init; } = string.Empty;
        public string Tag { get; init; } = string.Empty;
        public System.Windows.Media.Color C0 { get; init; }
        public System.Windows.Media.Color C1 { get; init; }
        public System.Windows.Media.Color C2 { get; init; }
        public System.Windows.Media.Color C3 { get; init; }
        public System.Windows.Media.Color C4 { get; init; }
    }

    public sealed class BandDisplayAppliedEventArgs : EventArgs
    {
        public BandDisplayAppliedEventArgs(
            StretchType selectedStretch,
            ColorRampType selectedColorRamp,
            int[] selectedBands,
            double selectedOpacity,
            bool stretchChanged,
            bool colorRampChanged,
            bool bandsChanged,
            bool opacityChanged)
        {
            SelectedStretch = selectedStretch;
            SelectedColorRamp = selectedColorRamp;
            SelectedBands = (int[])selectedBands.Clone();
            SelectedOpacity = selectedOpacity;
            StretchChanged = stretchChanged;
            ColorRampChanged = colorRampChanged;
            BandsChanged = bandsChanged;
            OpacityChanged = opacityChanged;
        }

        public StretchType SelectedStretch { get; }
        public ColorRampType SelectedColorRamp { get; }
        public int[] SelectedBands { get; }
        public double SelectedOpacity { get; }
        public bool StretchChanged { get; }
        public bool ColorRampChanged { get; }
        public bool BandsChanged { get; }
        public bool OpacityChanged { get; }
    }

    public partial class BandDisplayDialog : Window
    {
        private readonly int _totalBands;
        private int[] _currentBands;
        private StretchType _currentStretch;
        private ColorRampType _currentColorRamp;
        private double _currentOpacity;

        public StretchType SelectedStretch { get; private set; }
        public ColorRampType SelectedColorRamp { get; private set; }
        public int[] SelectedBands { get; private set; }
        public bool StretchChanged { get; private set; }
        public bool ColorRampChanged { get; private set; }
        public bool BandsChanged { get; private set; }
        public bool OpacityChanged { get; private set; }
        public event EventHandler<BandDisplayAppliedEventArgs>? SettingsApplied;

        public BandDisplayDialog(int totalBands, int[] currentBands,
            StretchType currentStretch, ColorRampType currentColorRamp,
            double currentOpacity, bool isMultiBand)
        {
            InitializeComponent();
            _totalBands = totalBands;
            _currentBands = (int[])currentBands.Clone();
            _currentStretch = currentStretch;
            _currentColorRamp = currentColorRamp;
            _currentOpacity = Math.Clamp(currentOpacity, 0d, 1d);
            SelectedBands = (int[])currentBands.Clone();
            SelectedStretch = currentStretch;
            SelectedColorRamp = currentColorRamp;
            OpacitySlider.Value = _currentOpacity * 100d;

            // Set current stretch
            for (int i = 0; i < StretchCombo.Items.Count; i++)
            {
                if (StretchCombo.Items[i] is ComboBoxItem item &&
                    item.Tag?.ToString() == currentStretch.ToString())
                {
                    StretchCombo.SelectedIndex = i;
                    break;
                }
            }

            // Populate color ramp with visual previews
            var C = System.Windows.Media.Color.FromRgb;
            var rampDefs = new[]
            {
                new RampItem { Name = "灰度", Tag = "Gray", C0 = C(0,0,0), C1 = C(64,64,64), C2 = C(128,128,128), C3 = C(192,192,192), C4 = C(255,255,255) },
                new RampItem { Name = "Jet", Tag = "Jet", C0 = C(0,0,143), C1 = C(0,0,255), C2 = C(0,255,255), C3 = C(255,255,0), C4 = C(128,0,0) },
                new RampItem { Name = "Viridis", Tag = "Viridis", C0 = C(68,1,84), C1 = C(59,82,139), C2 = C(33,145,140), C3 = C(94,201,98), C4 = C(253,231,37) },
                new RampItem { Name = "Plasma", Tag = "Plasma", C0 = C(13,8,135), C1 = C(126,3,168), C2 = C(204,71,120), C3 = C(248,149,64), C4 = C(240,249,33) },
                new RampItem { Name = "Inferno", Tag = "Inferno", C0 = C(0,0,4), C1 = C(87,16,110), C2 = C(188,55,84), C3 = C(249,142,9), C4 = C(252,255,164) },
                new RampItem { Name = "Magma", Tag = "Magma", C0 = C(0,0,4), C1 = C(81,18,124), C2 = C(183,55,121), C3 = C(253,136,54), C4 = C(252,253,191) },
                new RampItem { Name = "Turbo", Tag = "Turbo", C0 = C(48,18,59), C1 = C(40,96,216), C2 = C(33,185,119), C3 = C(211,219,49), C4 = C(122,4,3) },
                new RampItem { Name = "Cividis", Tag = "Cividis", C0 = C(0,32,77), C1 = C(55,92,120), C2 = C(124,138,101), C3 = C(199,174,42), C4 = C(254,221,61) },
            };
            foreach (var r in rampDefs)
                ColorRampCombo.Items.Add(r);

            for (int i = 0; i < ColorRampCombo.Items.Count; i++)
            {
                if (ColorRampCombo.Items[i] is RampItem ri && ri.Tag == currentColorRamp.ToString())
                {
                    ColorRampCombo.SelectedIndex = i;
                    break;
                }
            }

            // Populate band selectors
            var bandNames = new List<string>();
            for (int i = 1; i <= totalBands; i++)
                bandNames.Add($"波段 {i}");

            RedBandCombo.ItemsSource = bandNames;
            GreenBandCombo.ItemsSource = bandNames;
            BlueBandCombo.ItemsSource = bandNames;

            if (currentBands.Length >= 3)
            {
                RedBandCombo.SelectedIndex = currentBands[0] - 1;
                GreenBandCombo.SelectedIndex = currentBands[1] - 1;
                BlueBandCombo.SelectedIndex = currentBands[2] - 1;
            }

            // Show/hide groups
            ColorRampGroup.Visibility = isMultiBand ? Visibility.Collapsed : Visibility.Visible;
            RgbGroup.Visibility = isMultiBand ? Visibility.Visible : Visibility.Collapsed;

        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            if (!TryReadCurrentSettings(out var newStretch, out var newRamp, out var newBands, out var newOpacity))
                return;

            StretchChanged = newStretch != _currentStretch;
            ColorRampChanged = newRamp != _currentColorRamp;
            BandsChanged = !newBands.SequenceEqual(_currentBands);
            OpacityChanged = Math.Abs(newOpacity - _currentOpacity) > 0.0001d;

            SelectedStretch = newStretch;
            SelectedColorRamp = newRamp;
            SelectedBands = (int[])newBands.Clone();

            if (!StretchChanged && !ColorRampChanged && !BandsChanged && !OpacityChanged)
                return;

            SettingsApplied?.Invoke(this, new BandDisplayAppliedEventArgs(
                SelectedStretch,
                SelectedColorRamp,
                SelectedBands,
                newOpacity,
                StretchChanged,
                ColorRampChanged,
                BandsChanged,
                OpacityChanged));

            _currentStretch = SelectedStretch;
            _currentColorRamp = SelectedColorRamp;
            _currentBands = (int[])SelectedBands.Clone();
            _currentOpacity = newOpacity;
        }

        private bool TryReadCurrentSettings(
            out StretchType stretch,
            out ColorRampType colorRamp,
            out int[] bands,
            out double opacity)
        {
            stretch = _currentStretch;
            colorRamp = _currentColorRamp;
            bands = (int[])_currentBands.Clone();
            opacity = Math.Clamp(OpacitySlider.Value / 100d, 0d, 1d);

            if (StretchCombo.SelectedItem is ComboBoxItem stretchItem &&
                stretchItem.Tag is string tag &&
                Enum.TryParse<StretchType>(tag, out var parsedStretch))
            {
                stretch = parsedStretch;
            }
            else
            {
                return false;
            }

            if (ColorRampCombo.SelectedItem is RampItem rampItem &&
                Enum.TryParse<ColorRampType>(rampItem.Tag, out var parsedRamp))
            {
                colorRamp = parsedRamp;
            }

            if (RgbGroup.Visibility == Visibility.Visible)
            {
                bands =
                [
                    RedBandCombo.SelectedIndex + 1,
                    GreenBandCombo.SelectedIndex + 1,
                    BlueBandCombo.SelectedIndex + 1
                ];
            }

            return true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Mapsui.Layers;

namespace MyGIS.Models
{
    public class LayerItem : INotifyPropertyChanged
    {
        private bool _isVisible;
        private string _name = string.Empty;

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
        }

        public ILayer? Layer { get; set; }

        public string LayerGroup { get; set; } = "其他图层";

        public string LayerTypeLabel { get; set; } = string.Empty;

        public string Crs { get; set; } = string.Empty;

        public string FeatureCount { get; set; } = string.Empty;

        public IDisposable? RasterProviderHandle { get; set; }

        private LegendInfo? _legend;
        public LegendInfo? Legend
        {
            get => _legend;
            set
            {
                if (_legend != value)
                {
                    _legend = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasLegend));
                    OnPropertyChanged(nameof(ShowGray));
                    OnPropertyChanged(nameof(ShowRgb));
                }
            }
        }

        public bool HasLegend => _legend != null;
        public bool ShowGray => _legend?.IsGray == true;
        public bool ShowRgb => _legend?.IsRgb == true;

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
        }

        public override string ToString() => Name;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

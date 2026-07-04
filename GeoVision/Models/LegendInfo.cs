using System.Windows;
using System.Windows.Media;
using GeoVision.Services;

namespace GeoVision.Models
{
    public class LegendInfo
    {
        public bool IsGray { get; set; }
        public bool IsRgb { get; set; }
        public double MinValue { get; set; }
        public double MaxValue { get; set; }
        public string RedBand { get; set; } = string.Empty;
        public string GreenBand { get; set; } = string.Empty;
        public string BlueBand { get; set; } = string.Empty;
        public ColorRampType ColorRamp { get; set; } = ColorRampType.Gray;

        private LinearGradientBrush? _cachedBrush;
        private ColorRampType _cachedType = (ColorRampType)(-1);

        public LinearGradientBrush RampBrush
        {
            get
            {
                if (_cachedBrush != null && _cachedType == ColorRamp)
                    return _cachedBrush;

                _cachedType = ColorRamp;
                _cachedBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(0, 1)
                };
                for (int i = 0; i <= 5; i++)
                {
                    float t = 1f - i / 5f;
                    (byte r, byte g, byte b) = Services.ColorRamp.Sample(ColorRamp, t);
                    _cachedBrush.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), i / 5f));
                }
                return _cachedBrush;
            }
        }
    }
}

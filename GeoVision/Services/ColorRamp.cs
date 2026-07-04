namespace GeoVision.Services
{
    public enum ColorRampType
    {
        Gray,
        Jet,
        Viridis,
        Plasma,
        Inferno,
        Magma,
        Turbo,
        Cividis
    }

    public static class ColorRamp
    {
        private static readonly Dictionary<ColorRampType, (byte R, byte G, byte B)[]> _ramps = new();

        static ColorRamp()
        {
            _ramps[ColorRampType.Gray] = new (byte, byte, byte)[256];
            for (int i = 0; i < 256; i++)
                _ramps[ColorRampType.Gray][i] = ((byte)i, (byte)i, (byte)i);

            _ramps[ColorRampType.Jet] = BuildRamp(new[]
            {
                (0.0f, 0, 0, 143), (0.125f, 0, 0, 255), (0.375f, 0, 255, 255),
                (0.625f, 255, 255, 0), (0.875f, 255, 0, 0), (1.0f, 128, 0, 0)
            });

            _ramps[ColorRampType.Viridis] = BuildRamp(new[]
            {
                (0.0f, 68, 1, 84), (0.25f, 59, 82, 139), (0.5f, 33, 145, 140),
                (0.75f, 94, 201, 98), (1.0f, 253, 231, 37)
            });

            _ramps[ColorRampType.Plasma] = BuildRamp(new[]
            {
                (0.0f, 13, 8, 135), (0.25f, 126, 3, 168), (0.5f, 204, 71, 120),
                (0.75f, 248, 149, 64), (1.0f, 240, 249, 33)
            });

            _ramps[ColorRampType.Inferno] = BuildRamp(new[]
            {
                (0.0f, 0, 0, 4), (0.25f, 87, 16, 110), (0.5f, 188, 55, 84),
                (0.75f, 249, 142, 9), (1.0f, 252, 255, 164)
            });

            _ramps[ColorRampType.Magma] = BuildRamp(new[]
            {
                (0.0f, 0, 0, 4), (0.25f, 81, 18, 124), (0.5f, 183, 55, 121),
                (0.75f, 253, 136, 54), (1.0f, 252, 253, 191)
            });

            _ramps[ColorRampType.Turbo] = BuildRamp(new[]
            {
                (0.0f, 48, 18, 59), (0.2f, 40, 96, 216), (0.4f, 33, 185, 119),
                (0.6f, 211, 219, 49), (0.8f, 246, 109, 18), (1.0f, 122, 4, 3)
            });

            _ramps[ColorRampType.Cividis] = BuildRamp(new[]
            {
                (0.0f, 0, 32, 77), (0.25f, 55, 92, 120), (0.5f, 124, 138, 101),
                (0.75f, 199, 174, 42), (1.0f, 254, 221, 61)
            });
        }

        private static (byte R, byte G, byte B)[] BuildRamp((float pos, int r, int g, int b)[] stops)
        {
            var ramp = new (byte R, byte G, byte B)[256];
            for (int i = 0; i < 256; i++)
            {
                float t = i / 255f;
                int idx = 0;
                for (int j = 1; j < stops.Length; j++)
                {
                    if (t <= stops[j].pos) { idx = j - 1; break; }
                    if (j == stops.Length - 1) idx = j - 1;
                }
                float localT = (t - stops[idx].pos) / (stops[idx + 1].pos - stops[idx].pos);
                ramp[i] = (
                    (byte)(stops[idx].r + (stops[idx + 1].r - stops[idx].r) * localT),
                    (byte)(stops[idx].g + (stops[idx + 1].g - stops[idx].g) * localT),
                    (byte)(stops[idx].b + (stops[idx + 1].b - stops[idx].b) * localT)
                );
            }
            return ramp;
        }

        public static (byte R, byte G, byte B) Sample(ColorRampType type, float t)
        {
            int idx = (int)(Math.Clamp(t, 0, 1) * 255);
            return _ramps[type][idx];
        }

        public static void RenderColorBar(ColorRampType type, byte[] rgba, int startX, int startY, int width, int height, int stride)
        {
            for (int y = 0; y < height; y++)
            {
                float t = 1f - (float)y / (height - 1);
                var (r, g, b) = Sample(type, t);
                for (int x = 0; x < width; x++)
                {
                    int idx = ((startY + y) * stride + (startX + x)) * 4;
                    rgba[idx + 0] = r;
                    rgba[idx + 1] = g;
                    rgba[idx + 2] = b;
                    rgba[idx + 3] = 255;
                }
            }
        }
    }
}

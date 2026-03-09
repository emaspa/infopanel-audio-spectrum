using SkiaSharp;

namespace InfoPanel.AudioSpectrum
{
    internal enum SpectrumStyle
    {
        Bars,
        Rounded,
        Wave,
        Dots,
        Mirror
    }

    internal enum ColorScheme
    {
        Neon,
        Fire,
        Ice,
        Rainbow,
        Ocean,
        Monochrome,
        Custom
    }

    internal class SpectrumRenderer : IDisposable
    {
        private SKBitmap? _bitmap;
        private int _width;
        private int _height;
        private bool _disposed;

        public SpectrumStyle Style { get; set; } = SpectrumStyle.Bars;
        public ColorScheme Scheme { get; set; } = ColorScheme.Neon;
        public SKColor CustomColor1 { get; set; } = new(0, 255, 128);
        public SKColor CustomColor2 { get; set; } = new(0, 128, 255);
        public SKColor BackgroundColor { get; set; } = SKColors.Transparent;
        public float BarSpacing { get; set; } = 0.3f; // 0-1, fraction of bar width used as gap
        public float CornerRadius { get; set; } = 4f;
        public bool ShowPeaks { get; set; } = true;
        public bool ShowReflection { get; set; } = false;
        public float Brightness { get; set; } = 1.0f;

        public byte[]? Render(float[] bands, float[] peaks, int width, int height)
        {
            if (width <= 0 || height <= 0 || bands.Length == 0) return null;

            if (_bitmap == null || _width != width || _height != height)
            {
                _bitmap?.Dispose();
                _width = width;
                _height = height;
                _bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            }

            using var canvas = new SKCanvas(_bitmap);
            canvas.Clear(BackgroundColor);

            float drawHeight = ShowReflection ? height * 0.6f : height;

            switch (Style)
            {
                case SpectrumStyle.Bars:
                    DrawBars(canvas, bands, peaks, width, drawHeight, false);
                    break;
                case SpectrumStyle.Rounded:
                    DrawBars(canvas, bands, peaks, width, drawHeight, true);
                    break;
                case SpectrumStyle.Wave:
                    DrawWave(canvas, bands, width, drawHeight);
                    break;
                case SpectrumStyle.Dots:
                    DrawDots(canvas, bands, peaks, width, drawHeight);
                    break;
                case SpectrumStyle.Mirror:
                    DrawMirror(canvas, bands, peaks, width, height);
                    break;
            }

            if (ShowReflection && Style != SpectrumStyle.Mirror)
            {
                DrawReflection(canvas, width, drawHeight, height);
            }

            using var image = SKImage.FromBitmap(_bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }

        private void DrawBars(SKCanvas canvas, float[] bands, float[] peaks, float width, float height, bool rounded)
        {
            int count = bands.Length;
            float totalBarWidth = width / count;
            float gap = totalBarWidth * BarSpacing;
            float barWidth = totalBarWidth - gap;

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + gap / 2;
                float barHeight = (bands[i] / 100f) * height;
                float y = height - barHeight;

                var color = GetBandColor(i, count, bands[i] / 100f);

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Color = ApplyBrightness(color)
                };

                // Gradient fill
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(x, height),
                    new SKPoint(x, y),
                    [ApplyBrightness(GetBandColor(i, count, 0.2f)), ApplyBrightness(color)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp);
                paint.Shader = shader;

                var rect = new SKRect(x, y, x + barWidth, height);

                if (rounded && CornerRadius > 0)
                {
                    canvas.DrawRoundRect(rect, CornerRadius, CornerRadius, paint);
                }
                else
                {
                    canvas.DrawRect(rect, paint);
                }

                // Peak indicator
                if (ShowPeaks && peaks[i] > 2)
                {
                    float peakY = height - (peaks[i] / 100f) * height;
                    using var peakPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = ApplyBrightness(color).WithAlpha(200),
                        StrokeWidth = 2,
                        Style = SKPaintStyle.Fill
                    };
                    canvas.DrawRect(x, peakY - 2, barWidth, 3, peakPaint);
                }
            }
        }

        private void DrawWave(SKCanvas canvas, float[] bands, float width, float height)
        {
            int count = bands.Length;

            using var path = new SKPath();
            using var fillPath = new SKPath();

            float step = width / (count - 1);

            // Build smooth curve through band points
            fillPath.MoveTo(0, height);

            for (int i = 0; i < count; i++)
            {
                float x = i * step;
                float y = height - (bands[i] / 100f) * height;

                if (i == 0)
                {
                    path.MoveTo(x, y);
                    fillPath.LineTo(x, y);
                }
                else
                {
                    float prevX = (i - 1) * step;
                    float prevY = height - (bands[i - 1] / 100f) * height;
                    float cpX = (prevX + x) / 2;

                    path.CubicTo(cpX, prevY, cpX, y, x, y);
                    fillPath.CubicTo(cpX, prevY, cpX, y, x, y);
                }
            }

            fillPath.LineTo(width, height);
            fillPath.Close();

            // Gradient fill
            using var fillPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            var colors = GetGradientColors(5);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, height),
                new SKPoint(0, 0),
                [colors[0].WithAlpha(40), colors[2].WithAlpha(150), colors[4].WithAlpha(200)],
                [0f, 0.5f, 1f],
                SKShaderTileMode.Clamp);
            fillPaint.Shader = shader;
            canvas.DrawPath(fillPath, fillPaint);

            // Stroke
            using var strokePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.5f
            };

            using var strokeShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, 0),
                colors,
                null,
                SKShaderTileMode.Clamp);
            strokePaint.Shader = strokeShader;
            canvas.DrawPath(path, strokePaint);

            // Glow effect
            using var glowPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 6f,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4)
            };
            using var glowShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, 0),
                colors.Select(c => c.WithAlpha(80)).ToArray(),
                null,
                SKShaderTileMode.Clamp);
            glowPaint.Shader = glowShader;
            canvas.DrawPath(path, glowPaint);
        }

        private void DrawDots(SKCanvas canvas, float[] bands, float[] peaks, float width, float height)
        {
            int count = bands.Length;
            float totalBarWidth = width / count;
            int dotsPerColumn = 16;
            float dotSpacing = height / dotsPerColumn;
            float dotRadius = MathF.Min(totalBarWidth * 0.3f, dotSpacing * 0.35f);

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + totalBarWidth / 2;
                int activeDots = (int)(bands[i] / 100f * dotsPerColumn);
                int peakDot = (int)(peaks[i] / 100f * dotsPerColumn);

                for (int d = 0; d < dotsPerColumn; d++)
                {
                    float y = height - (d + 0.5f) * dotSpacing;
                    bool active = d < activeDots;
                    bool isPeak = ShowPeaks && d == peakDot && peakDot > 0;

                    var color = GetBandColor(i, count, (float)d / dotsPerColumn);

                    using var paint = new SKPaint
                    {
                        IsAntialias = true,
                        Style = SKPaintStyle.Fill
                    };

                    if (active)
                    {
                        paint.Color = ApplyBrightness(color);
                        canvas.DrawCircle(x, y, dotRadius, paint);

                        // Glow on active dots
                        using var glowPaint = new SKPaint
                        {
                            IsAntialias = true,
                            Color = ApplyBrightness(color).WithAlpha(60),
                            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3)
                        };
                        canvas.DrawCircle(x, y, dotRadius * 1.5f, glowPaint);
                    }
                    else if (isPeak)
                    {
                        paint.Color = ApplyBrightness(color).WithAlpha(200);
                        canvas.DrawCircle(x, y, dotRadius, paint);
                    }
                    else
                    {
                        paint.Color = new SKColor(60, 60, 60, 40);
                        canvas.DrawCircle(x, y, dotRadius * 0.6f, paint);
                    }
                }
            }
        }

        private void DrawMirror(SKCanvas canvas, float[] bands, float[] peaks, float width, float height)
        {
            int count = bands.Length;
            float totalBarWidth = width / count;
            float gap = totalBarWidth * BarSpacing;
            float barWidth = totalBarWidth - gap;
            float midY = height / 2;

            for (int i = 0; i < count; i++)
            {
                float x = i * totalBarWidth + gap / 2;
                float halfBarHeight = (bands[i] / 100f) * midY;

                var color = GetBandColor(i, count, bands[i] / 100f);

                // Top half (going up from center)
                using var topPaint = new SKPaint { IsAntialias = true };
                using var topShader = SKShader.CreateLinearGradient(
                    new SKPoint(x, midY),
                    new SKPoint(x, midY - halfBarHeight),
                    [ApplyBrightness(color).WithAlpha(80), ApplyBrightness(color)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp);
                topPaint.Shader = topShader;
                canvas.DrawRoundRect(new SKRect(x, midY - halfBarHeight, x + barWidth, midY), CornerRadius, CornerRadius, topPaint);

                // Bottom half (going down from center)
                using var botPaint = new SKPaint { IsAntialias = true };
                using var botShader = SKShader.CreateLinearGradient(
                    new SKPoint(x, midY),
                    new SKPoint(x, midY + halfBarHeight),
                    [ApplyBrightness(color).WithAlpha(80), ApplyBrightness(color)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp);
                botPaint.Shader = botShader;
                canvas.DrawRoundRect(new SKRect(x, midY, x + barWidth, midY + halfBarHeight), CornerRadius, CornerRadius, botPaint);

                // Peak indicators
                if (ShowPeaks && peaks[i] > 2)
                {
                    float peakOffset = (peaks[i] / 100f) * midY;
                    using var peakPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = ApplyBrightness(color).WithAlpha(200),
                        StrokeWidth = 2
                    };
                    canvas.DrawRect(x, midY - peakOffset - 2, barWidth, 3, peakPaint);
                    canvas.DrawRect(x, midY + peakOffset - 1, barWidth, 3, peakPaint);
                }
            }
        }

        private void DrawReflection(SKCanvas canvas, float width, float mainHeight, float totalHeight)
        {
            float reflectionHeight = totalHeight - mainHeight;

            // Create a faded copy of the main area, flipped
            using var snapshot = new SKBitmap((int)width, (int)mainHeight);
            using var snapshotCanvas = new SKCanvas(snapshot);

            // Read pixels from the main area
            var info = new SKImageInfo((int)width, (int)mainHeight);
            using var mainPixels = new SKBitmap(info);
            _bitmap!.ExtractSubset(mainPixels, new SKRectI(0, 0, (int)width, (int)mainHeight));

            // Draw flipped
            snapshotCanvas.Save();
            snapshotCanvas.Scale(1, -1, 0, mainHeight / 2);
            snapshotCanvas.DrawBitmap(mainPixels, 0, 0);
            snapshotCanvas.Restore();

            // Draw reflection with fade
            using var paint = new SKPaint
            {
                IsAntialias = true
            };
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, mainHeight),
                new SKPoint(0, totalHeight),
                [SKColors.White.WithAlpha(60), SKColors.White.WithAlpha(0)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            paint.Shader = shader;
            paint.BlendMode = SKBlendMode.DstIn;

            // Draw the flipped image
            canvas.DrawBitmap(snapshot, 0, mainHeight);

            // Apply fade mask
            using var fadeBitmap = new SKBitmap((int)width, (int)reflectionHeight);
            using var fadeCanvas = new SKCanvas(fadeBitmap);
            fadeCanvas.DrawBitmap(snapshot, new SKRect(0, 0, width, mainHeight),
                new SKRect(0, 0, width, reflectionHeight));

            // Simple alpha fade on the reflection area
            using var fadePaint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha(180)
            };
            canvas.DrawRect(0, mainHeight, width, reflectionHeight, fadePaint);
        }

        private SKColor GetBandColor(int bandIndex, int bandCount, float intensity)
        {
            float position = (float)bandIndex / bandCount;

            return Scheme switch
            {
                ColorScheme.Neon => InterpolateColors(position, intensity,
                    new SKColor(0, 255, 128), new SKColor(0, 200, 255), new SKColor(180, 0, 255)),
                ColorScheme.Fire => InterpolateColors(position, intensity,
                    new SKColor(255, 60, 0), new SKColor(255, 180, 0), new SKColor(255, 255, 100)),
                ColorScheme.Ice => InterpolateColors(position, intensity,
                    new SKColor(100, 200, 255), new SKColor(150, 230, 255), new SKColor(220, 240, 255)),
                ColorScheme.Rainbow => HSLtoRGB(position * 360f, 1f, 0.3f + intensity * 0.3f),
                ColorScheme.Ocean => InterpolateColors(position, intensity,
                    new SKColor(0, 50, 120), new SKColor(0, 150, 200), new SKColor(0, 220, 180)),
                ColorScheme.Monochrome => InterpolateColors(position, intensity,
                    new SKColor(80, 80, 80), new SKColor(180, 180, 180), new SKColor(255, 255, 255)),
                ColorScheme.Custom => InterpolateColor(CustomColor1, CustomColor2, position),
                _ => new SKColor(0, 255, 128)
            };
        }

        private SKColor[] GetGradientColors(int count)
        {
            var colors = new SKColor[count];
            for (int i = 0; i < count; i++)
            {
                colors[i] = ApplyBrightness(GetBandColor(i, count, 0.8f));
            }
            return colors;
        }

        private static SKColor InterpolateColors(float position, float intensity, SKColor low, SKColor mid, SKColor high)
        {
            // Blend based on intensity (low=quiet, high=loud)
            SKColor baseColor;
            if (intensity < 0.5f)
            {
                baseColor = InterpolateColor(low, mid, intensity * 2);
            }
            else
            {
                baseColor = InterpolateColor(mid, high, (intensity - 0.5f) * 2);
            }

            // Shift hue slightly based on position
            return InterpolateColor(baseColor, ShiftHue(baseColor, position * 60), position);
        }

        private static SKColor InterpolateColor(SKColor a, SKColor b, float t)
        {
            t = MathF.Max(0, MathF.Min(1, t));
            return new SKColor(
                (byte)(a.Red + (b.Red - a.Red) * t),
                (byte)(a.Green + (b.Green - a.Green) * t),
                (byte)(a.Blue + (b.Blue - a.Blue) * t),
                (byte)(a.Alpha + (b.Alpha - a.Alpha) * t)
            );
        }

        private static SKColor ShiftHue(SKColor color, float degrees)
        {
            color.ToHsl(out float h, out float s, out float l);
            h = (h + degrees) % 360;
            if (h < 0) h += 360;
            return SKColor.FromHsl(h, s, l);
        }

        private static SKColor HSLtoRGB(float h, float s, float l)
        {
            return SKColor.FromHsl(h % 360, s * 100, l * 100);
        }

        private SKColor ApplyBrightness(SKColor color)
        {
            return new SKColor(
                (byte)MathF.Min(255, color.Red * Brightness),
                (byte)MathF.Min(255, color.Green * Brightness),
                (byte)MathF.Min(255, color.Blue * Brightness),
                color.Alpha
            );
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _bitmap?.Dispose();
            }
        }
    }
}

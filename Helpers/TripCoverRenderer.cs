using Packo.Models.DTO;
using Packo.Models.Enums;
using SkiaSharp;

namespace Packo.Helpers
{
    public class TripCoverRenderer
    {
        public SKBitmap Render(TripDTO trip, string packingSummary)
        {
            var bitmap = new SKBitmap(1080, 600);
            using var canvas = new SKCanvas(bitmap);

            DrawBackground(canvas, trip.CoverTheme);
            DrawBottomPanel(canvas, bitmap.Width, bitmap.Height);
            DrawTitle(canvas, bitmap.Height, trip.Destination);
            DrawDates(canvas, bitmap.Height, trip.StartDate.Value, trip.EndDate.Value);
            DrawPackingSummary(canvas, bitmap.Height, packingSummary);
            return bitmap;
        }

        private void DrawBackground(SKCanvas canvas, CoverThemeType theme)
        {
            var imageName = theme switch
            {
                CoverThemeType.mountains_winter => "mountains_winter.png",
                CoverThemeType.mountains_summer_01 => "mountains_summer_01.png",
                CoverThemeType.mountains_summer_02 => "mountains_summer_02.png",
                CoverThemeType.road_trip => "road_trip.png",
                CoverThemeType.summer_beach_01 => "summer_beach_01.png",
                CoverThemeType.summer_beach_02 => "summer_beach_02.png",
                CoverThemeType.business_trip => "business_trip.png",
                CoverThemeType.weekend_city_break => "weekend_city_break.png",
                CoverThemeType.big_city => "big_city.png",
                CoverThemeType.camping => "camping.png",
                _ => null
            };

            if (string.IsNullOrWhiteSpace(imageName))
            {
                canvas.Clear(new SKColor(34, 64, 98));
                return;
            }

            try
            {
                using var stream = FileSystem.OpenAppPackageFileAsync(imageName).GetAwaiter().GetResult();
                using var bitmap = SKBitmap.Decode(stream);

                if (bitmap != null)
                {
                    canvas.DrawBitmap(bitmap, new SKRect(0, 0, 1080, 600));
                    return;
                }
            }
            catch (Exception ex)
            {
            }

            canvas.Clear(new SKColor(34, 64, 98));
        }

        private static void DrawTitle(SKCanvas canvas, int bitmapHeight, string text)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
            };

            var font = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 72);

            canvas.DrawText(text, 45, bitmapHeight - 130, SKTextAlign.Left, font, paint);
        }

        private static void DrawDates(SKCanvas canvas, int bitmapHeight, DateTime start, DateTime end)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(245),
                IsAntialias = true
            };

            using var font = new SKFont(SKTypeface.FromFamilyName(null), 32);

            canvas.DrawText($"{start:dd MMM} - {end:dd MMM yyyy}", 45, bitmapHeight - 82, SKTextAlign.Left, font, paint);
        }

        private static void DrawPackingSummary(SKCanvas canvas, int bitmapHeight, string summary)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(245),
                IsAntialias = true
            };

            using var font = new SKFont(SKTypeface.FromFamilyName(null), 28);

            canvas.DrawText(summary, 45, bitmapHeight - 40, SKTextAlign.Left, font, paint);
        }

        private void DrawBottomPanel(SKCanvas canvas, int bitmapWidth, int bitmapHeight)
        {
            const float panelHeight = 260;
            var top = bitmapHeight - panelHeight;

            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, top),
                new SKPoint(0, bitmapHeight),
                new[]
                {
            new SKColor(0, 0, 0, 0),
            new SKColor(0, 0, 0, 185)
                },
                null,
                SKShaderTileMode.Clamp);

            using var paint = new SKPaint
            {
                Shader = shader,
                IsAntialias = true
            };

            canvas.DrawRect(
                0,
                top,
                bitmapWidth,
                panelHeight,
                paint);
        }
    }
}

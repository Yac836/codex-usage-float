using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace CodexUsageFloat.Tests
{
    internal static partial class RegressionSuite
    {
        private static void PreviewCases()
        {
            Case("DOCS circle and card previews use actual drawing with synthetic data", delegate {
                foreach (bool circle in new[] { true, false })
                {
                    var example = Sample(); example.FiveHour.UsedPercent = 28; example.Weekly.UsedPercent = 54;
                    using (var form = FormForTest(new Preferences { CircularStyle = circle, SizePercent = 150, ShowRemaining = true, EdgeAutoHide = false }, token => example))
                    {
                        // Native child controls need a shown handle for WM_PRINT
                        // to include their text. No desktop pixels are captured.
                        if (!circle)
                        {
                            form.Show();
                            Check(WaitUntil(() => (int)Field(form, "reading") == 0, 3000, true), "preview reader did not finish");
                        }
                        Put(form, "snapshot", example); Call(form, "UpdateDisplay");
                        Check(form.AccessibleDescription.Contains("72%") && form.AccessibleDescription.Contains("46%"), "preview data did not reach actual UI");
                        using (var widget = circle ? form.RenderCircleBitmap() : new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                        {
                            if (!circle)
                            {
                                form.DrawToBitmap(widget, new Rectangle(Point.Empty, form.Size));
                                var heading = (Label)Field(form, "heading"); int textPixels = 0;
                                for (int x = heading.Left; x < heading.Right; x++) for (int y = heading.Top; y < heading.Bottom; y++)
                                {
                                    Color color = widget.GetPixel(x, y);
                                    if (color.R + color.G + color.B > 500) textPixels++;
                                }
                                Check(textPixels > 30, "native card preview omitted child-control text");
                            }
                            using (var canvas = new Bitmap(widget.Width + 48, widget.Height + 48, PixelFormat.Format32bppArgb))
                            using (var g = Graphics.FromImage(canvas))
                            {
                                g.Clear(Color.FromArgb(241, 244, 248));
                                g.TranslateTransform(24, 24);
                                if (!circle) g.SetClip(form.Region, System.Drawing.Drawing2D.CombineMode.Replace);
                                g.DrawImage(widget, new Rectangle(0, 0, widget.Width, widget.Height));
                                string path = Path.Combine(outputRoot, circle ? "preview-circle.png" : "preview-card.png");
                                canvas.Save(path, ImageFormat.Png);
                                Check(new FileInfo(path).Length > 3000, "preview image is empty");
                            }
                        }
                    }
                }
            });
        }
    }
}

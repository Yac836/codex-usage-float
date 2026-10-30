using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace CodexUsageFloat
{
    internal sealed partial class UsageForm
    {
        private bool circleLayerConfigured, circleLayerActive, drawingCircle, circleFallbackPending;
        private string circleFrameKey, circleHoverText;
        private int circleSurfaceError;

        private void LayoutCircle()
        {
            int size = CircleDesignSize();
            int center = size / 2;
            int quotas = (preferences.ShowFiveHour ? 1 : 0) + (preferences.ShowWeekly ? 1 : 0);
            bool cards = preferences.ShowResetCredits;
            ClientSize = new Size(S(size), S(size));
            heading.Text = "C O D E X";
            Place(heading, center - 45, quotas == 2 && cards ? 42 : 29, 90, 17, 7.5f, FontStyle.Bold);
            heading.ForeColor = Color.FromArgb(157, 172, 193);
            heading.TextAlign = ContentAlignment.MiddleCenter;
            for (int i = 0; i < 2; i++)
            {
                if (quotas == 2)
                {
                    int x = center - 83 + i * 86;
                    int top = cards ? 76 : 73;
                    Place(names[i], x, top, 80, 15, 8, FontStyle.Regular);
                    Place(values[i], x - 1, top + 18, 82, 34, 19, FontStyle.Bold);
                    Place(resets[i], x - 2, top + 54, 84, 15, 7, FontStyle.Regular);
                }
                else
                {
                    Place(names[i], center - 76, cards ? 54 : 53, 152, 16, 8.5f, FontStyle.Regular);
                    Place(values[i], center - 80, cards ? 73 : 72, 160, 42, 26, FontStyle.Bold);
                    Place(resets[i], center - 80, cards ? 116 : 116, 160, 16, 8, FontStyle.Regular);
                }
                names[i].TextAlign = values[i].TextAlign = resets[i].TextAlign = ContentAlignment.MiddleCenter;
            }
            if (quotas == 0)
            {
                Place(creditName, center - 75, 57, 150, 17, 8.5f, FontStyle.Regular);
                Place(creditValue, center - 75, 76, 150, 48, 25, FontStyle.Bold);
                creditName.TextAlign = creditValue.TextAlign = ContentAlignment.MiddleCenter;
                Place(creditExpiry, center - 87, 129, 174, 17, 7.2f, FontStyle.Regular);
            }
            else
            {
                int top = quotas == 2 ? 164 : 148;
                Place(creditName, center - 58, top + 3, 56, 22, 8.5f, FontStyle.Regular);
                Place(creditValue, center + 5, top - 2, 62, 28, 14, FontStyle.Bold);
                creditName.TextAlign = ContentAlignment.MiddleRight;
                creditValue.TextAlign = ContentAlignment.MiddleLeft;
                Place(creditExpiry, center - 98, top + 29, 196, 16, 7.5f, FontStyle.Regular);
            }
            creditExpiry.TextAlign = ContentAlignment.MiddleCenter;
            Place(emptyLabel, 10, 81, size - 20, 28, 9, FontStyle.Regular);
            emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            // The display mode is part of each quota caption, never a loose footer.
            Place(mode, 0, 0, 1, 1, 7, FontStyle.Regular);
        }

        private void ConfigureCircleWindow()
        {
            circleFrameKey = null;
            if (preferences.CircularStyle)
            {
                if (Opacity != 1) Opacity = 1;
                var old = Region;
                Region = null;
                if (old != null) old.Dispose();
                circleLayerConfigured = false;
                RefreshCircleSurface(true);
            }
            else
            {
                if (circleLayerActive && IsHandleCreated) LayeredSurface.Reset(Handle);
                circleLayerActive = circleLayerConfigured = false;
                Opacity = preferences.OpacityPercent / 100.0;
            }
        }

        private void RefreshCircleSurface(bool force)
        {
            if (!ready || closing || !preferences.CircularStyle || !IsHandleCreated || drawingCircle || Width <= 0 || Height <= 0) return;
            string key = String.Join("|", new[] {
                Width.ToString(), Height.ToString(), preferences.OpacityPercent.ToString(), dockCollapsed.ToString(),
                preferences.ShowFiveHour.ToString(), preferences.ShowWeekly.ToString(), preferences.ShowResetCredits.ToString(),
                snapshot == null ? "" : snapshot.RetrievedUtc.Ticks.ToString(), readError ?? "",
                heading.Text, names[0].Text, names[1].Text, values[0].Text, values[1].Text,
                resets[0].Text, resets[1].Text, creditValue.Text, creditExpiry.Text, status.Text });
            if (!force && key == circleFrameKey) return;
            drawingCircle = true;
            try
            {
                circleLayerActive = true;
                using (var bitmap = RenderCircleBitmap())
                    uploadCircle(Handle, bitmap, Location, (byte)Math.Round(preferences.OpacityPercent * 2.55), ref circleLayerConfigured);
                circleFrameKey = key;
                circleSurfaceError = 0;
                EnsureTopMost(false);
            }
            catch (Win32Exception error)
            {
                circleSurfaceError = error.NativeErrorCode;
                circleFrameKey = null;
                if (!circleFallbackPending)
                {
                    circleFallbackPending = true;
                    if (!PostToUi(delegate
                    {
                        circleFallbackPending = false;
                        if (closing || !preferences.CircularStyle) return;
                        SetCircularStyle(false);
                        drawingWarning = "圆形绘制失败，已切换为卡片";
                        UpdateDisplay();
                    })) circleFallbackPending = false;
                }
            }
            finally { drawingCircle = false; }
        }

        internal Bitmap RenderCircleBitmap()
        {
            const int supersampling = 2;
            var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            bitmap.SetResolution(96 * systemScale, 96 * systemScale);
            using (var large = new Bitmap(Width * supersampling, Height * supersampling, PixelFormat.Format32bppPArgb))
            {
                large.SetResolution(96 * systemScale, 96 * systemScale);
                using (var graphics = Graphics.FromImage(large))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.ScaleTransform(supersampling, supersampling);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    PaintCircleScene(graphics);
                }
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.DrawImage(large, new Rectangle(0, 0, Width, Height), 0, 0, large.Width, large.Height, GraphicsUnit.Pixel);
                }
            }
            return bitmap;
        }

        private float D(float value) { return value * scale; }
        private void PaintCircleScene(Graphics graphics)
        {
            if (dockCollapsed)
            {
                using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Math.Max(2, S(3))))
                using (var fill = new SolidBrush(background))
                using (var pen = new Pen(Color.FromArgb(90, 117, 150), 1))
                { graphics.FillPath(fill, path); graphics.DrawPath(pen, path); }
                using (var marker = new Pen(accents[0], Math.Max(2, D(2))))
                {
                    marker.StartCap = marker.EndCap = LineCap.Round;
                    if (Width < Height) graphics.DrawLine(marker, Width / 2f, Height / 3f, Width / 2f, Height * 2f / 3f);
                    else graphics.DrawLine(marker, Width / 3f, Height / 2f, Width * 2f / 3f, Height / 2f);
                }
                return;
            }
            for (int halo = 7; halo > 0; halo--)
            {
                float margin = D(6 - halo * .72f);
                using (var shadow = new SolidBrush(Color.FromArgb(3, 0, 0, 0)))
                    graphics.FillEllipse(shadow, margin, margin + D(1), Width - 2 * margin, Height - 2 * margin - D(1));
            }
            var face = new RectangleF(D(6), D(6), Width - D(12), Height - D(12));
            bool stale = snapshot == null || readError != null || DateTime.UtcNow - snapshot.RetrievedUtc > TimeSpan.FromMinutes(3);
            using (var fill = new LinearGradientBrush(face, Color.FromArgb(30, 39, 53), Color.FromArgb(17, 23, 33), LinearGradientMode.ForwardDiagonal))
                graphics.FillEllipse(fill, face);
            using (var rim = new Pen(Color.FromArgb(135, 64, 81, 105), Math.Max(1, D(.7f)))) graphics.DrawEllipse(rim, face);
            int ringIndex = 0;
            for (int i = 0; i < 2; i++)
            {
                if (!ItemVisible(i)) continue;
                float margin = D(ringIndex == 0 ? 18 : 31);
                var ring = new RectangleF(margin, margin, Width - 2 * margin, Height - 2 * margin);
                float stroke = D(ringIndex == 0 ? 4.2f : 3.7f);
                ringIndex++;
                using (var track = new Pen(Color.FromArgb(35, accents[i]), stroke)) graphics.DrawEllipse(track, ring);
                var window = snapshot == null ? null : i == 0 ? snapshot.FiveHour : snapshot.Weekly;
                if (window == null) continue;
                float percent = (float)(preferences.ShowRemaining ? 100 - window.UsedPercent : window.UsedPercent);
                if (percent <= 0) continue;
                Color color = stale ? muted : Tone(i, window.UsedPercent);
                Color end = Color.FromArgb(Math.Max(0, color.R - 30), Math.Max(0, color.G - 22), color.B);
                using (var gradient = new LinearGradientBrush(ring, color, end, LinearGradientMode.ForwardDiagonal))
                using (var arc = new Pen(gradient, stroke))
                {
                    arc.StartCap = arc.EndCap = LineCap.Round;
                    graphics.DrawArc(arc, ring, -90, Math.Min(360, percent * 3.6f));
                }
            }
            DrawCircleText(graphics, heading);
            for (int i = 0; i < 2; i++) if (ItemVisible(i))
            {
                DrawCircleText(graphics, names[i]);
                DrawCircleText(graphics, values[i]);
                DrawCircleText(graphics, resets[i]);
            }
            if (preferences.ShowResetCredits)
            {
                int quotas = (preferences.ShowFiveHour ? 1 : 0) + (preferences.ShowWeekly ? 1 : 0);
                if (quotas > 0)
                {
                    float y = D(quotas == 2 ? 153 : 137);
                    using (var line = new Pen(Color.FromArgb(50, 112, 131, 157), Math.Max(1, D(.65f))))
                        graphics.DrawLine(line, Width / 2f - D(44), y, Width / 2f + D(44), y);
                }
                DrawCircleText(graphics, creditName);
                DrawCircleText(graphics, creditValue);
                DrawCircleText(graphics, creditExpiry);
            }
            if (VisibleItemCount() == 0) DrawCircleText(graphics, emptyLabel);
            using (var dot = new SolidBrush(stale ? Color.FromArgb(217, 165, 98) : Color.FromArgb(94, 167, 154)))
                graphics.FillEllipse(dot, Width / 2f + D(39), heading.Top + D(7), D(3), D(3));
        }

        private void DrawCircleText(Graphics graphics, Label label)
        {
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            using (var brush = new SolidBrush(label.ForeColor))
            {
                format.FormatFlags |= StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.LineAlignment = StringAlignment.Center;
                format.Alignment = label.TextAlign == ContentAlignment.MiddleRight ? StringAlignment.Far :
                    label.TextAlign == ContentAlignment.MiddleLeft ? StringAlignment.Near : StringAlignment.Center;
                graphics.DrawString(label.Text, label.Font, brush, label.Bounds, format);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!preferences.CircularStyle || dockCollapsed) return;
            string text = status.Text + "\n双击刷新；右键设置";
            if (preferences.ShowResetCredits && (creditName.Bounds.Contains(e.Location) || creditValue.Bounds.Contains(e.Location) || creditExpiry.Bounds.Contains(e.Location)))
                text = DisplayText.CreditDetails(snapshot);
            for (int i = 0; i < 2; i++) if (ItemVisible(i) && (names[i].Bounds.Contains(e.Location) || values[i].Bounds.Contains(e.Location) || resets[i].Bounds.Contains(e.Location)))
                text = tip.GetToolTip(values[i]);
            if (text != circleHoverText) { circleHoverText = text; tip.SetToolTip(this, text); }
        }
    }
}

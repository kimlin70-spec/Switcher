using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetSwitch
{
    internal enum TrayIconKind
    {
        Wifi,           // 인터넷 모드, 정상
        WifiWarn,       // 인터넷 모드인데 와이파이 미연결 / IP 없음
        Lan,            // 업무망 모드, 정상
        LanWarn,        // 업무망 모드인데 랜선 없음 / IP 없음
        SwitchingA,     // 전환 중 (프레임 1)
        SwitchingB,     // 전환 중 (프레임 2)
        Neutral,        // 복구됨: 양쪽 모두 윈도우가 관리
        Error
    }

    /// <summary>Draws the tray icons at runtime so the exe needs no image resources.</summary>
    internal sealed class TrayIcons : IDisposable
    {
        private readonly Dictionary<TrayIconKind, Icon> _cache = new Dictionary<TrayIconKind, Icon>();
        private readonly Dictionary<TrayIconKind, IntPtr> _handles = new Dictionary<TrayIconKind, IntPtr>();
        private readonly int _size;

        private static readonly Color WifiBlue = Color.FromArgb(0x1E, 0x88, 0xE5);
        private static readonly Color LanGreen = Color.FromArgb(0x2E, 0x9E, 0x4F);
        private static readonly Color WarnOrange = Color.FromArgb(0xF5, 0x8A, 0x1F);
        private static readonly Color ErrRed = Color.FromArgb(0xD3, 0x2F, 0x2F);
        private static readonly Color Gray = Color.FromArgb(0x75, 0x75, 0x75);

        public TrayIcons()
        {
            int s = 16;
            try { s = Math.Max(16, SystemInformation.SmallIconSize.Width); } catch { }
            _size = s;
        }

        public Icon Get(TrayIconKind kind)
        {
            if (_cache.TryGetValue(kind, out var ic)) return ic;
            using (var bmp = Draw(kind))
            {
                IntPtr h = bmp.GetHicon();
                var icon = Icon.FromHandle(h);
                _cache[kind] = icon;
                _handles[kind] = h;
                return icon;
            }
        }

        private Bitmap Draw(TrayIconKind kind)
        {
            var bmp = new Bitmap(_size, _size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = _size;
                var rect = new RectangleF(0.5f, 0.5f, s - 1f, s - 1f);

                Color bg;
                switch (kind)
                {
                    case TrayIconKind.Wifi: bg = WifiBlue; break;
                    case TrayIconKind.Lan: bg = LanGreen; break;
                    case TrayIconKind.WifiWarn:
                    case TrayIconKind.LanWarn: bg = WarnOrange; break;
                    case TrayIconKind.Error: bg = ErrRed; break;
                    default: bg = Gray; break;
                }

                using (var b = new SolidBrush(bg))
                {
                    if (kind == TrayIconKind.Lan || kind == TrayIconKind.LanWarn)
                        FillRoundedRect(g, b, rect, s * 0.22f);
                    else
                        g.FillEllipse(b, rect);
                }

                using (var pen = new Pen(Color.White, Math.Max(1.5f, s / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var white = new SolidBrush(Color.White))
                {
                    switch (kind)
                    {
                        case TrayIconKind.Wifi:
                        case TrayIconKind.WifiWarn:
                            DrawWifi(g, pen, white, s);
                            break;
                        case TrayIconKind.Lan:
                        case TrayIconKind.LanWarn:
                            DrawPlug(g, pen, white, s, bg);
                            break;
                        case TrayIconKind.SwitchingA:
                            DrawArrows(g, pen, s, 0);
                            break;
                        case TrayIconKind.SwitchingB:
                            DrawArrows(g, pen, s, 1);
                            break;
                        case TrayIconKind.Error:
                            g.DrawLine(pen, s * 0.3f, s * 0.3f, s * 0.7f, s * 0.7f);
                            g.DrawLine(pen, s * 0.7f, s * 0.3f, s * 0.3f, s * 0.7f);
                            break;
                        case TrayIconKind.Neutral:
                            // two dots: both sides on
                            g.FillEllipse(white, s * 0.22f, s * 0.38f, s * 0.24f, s * 0.24f);
                            g.FillEllipse(white, s * 0.54f, s * 0.38f, s * 0.24f, s * 0.24f);
                            break;
                    }
                }

                if (kind == TrayIconKind.WifiWarn || kind == TrayIconKind.LanWarn)
                {
                    // small "!" badge bottom-right
                    float r = s * 0.42f;
                    var badge = new RectangleF(s - r - 0.5f, s - r - 0.5f, r, r);
                    using (var wb = new SolidBrush(Color.White)) g.FillEllipse(wb, badge);
                    using (var ob = new SolidBrush(WarnOrange))
                    using (var f = new Font(FontFamily.GenericSansSerif, r * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("!", f, ob, badge, sf);
                    }
                }
            }
            return bmp;
        }

        private static void DrawWifi(Graphics g, Pen pen, Brush dot, float s)
        {
            // three arcs + dot, centred
            float cx = s / 2f, cy = s * 0.70f;
            for (int i = 1; i <= 2; i++)
            {
                float r = s * (0.16f + 0.14f * i);
                g.DrawArc(pen, cx - r, cy - r, 2 * r, 2 * r, 215f, 110f);
            }
            float d = Math.Max(2f, s * 0.14f);
            g.FillEllipse(dot, cx - d / 2f, cy - d / 2f - s * 0.02f, d, d);
        }

        private static void DrawPlug(Graphics g, Pen pen, Brush fill, float s, Color bg)
        {
            // RJ45-ish: a body with a cable
            float w = s * 0.46f, h = s * 0.36f;
            float x = (s - w) / 2f, y = s * 0.22f;
            g.FillRectangle(fill, x, y, w, h);
            // notch
            float nw = w * 0.5f, nh = h * 0.3f;
            using (var bgBrush = new SolidBrush(bg)) g.FillRectangle(bgBrush, x + (w - nw) / 2f, y, nw, nh);
            // cable
            g.DrawLine(pen, s / 2f, y + h, s / 2f, s * 0.84f);
        }

        private static void DrawArrows(Graphics g, Pen pen, float s, int frame)
        {
            // two horizontal arrows, alternate which one is highlighted per frame
            float y1 = s * 0.38f, y2 = s * 0.62f, x0 = s * 0.22f, x1 = s * 0.78f;
            using (var faint = new Pen(Color.FromArgb(120, Color.White), pen.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var pA = frame == 0 ? pen : faint;
                var pB = frame == 0 ? faint : pen;
                g.DrawLine(pA, x0, y1, x1, y1);
                g.DrawLine(pA, x1, y1, x1 - s * 0.14f, y1 - s * 0.12f);
                g.DrawLine(pA, x1, y1, x1 - s * 0.14f, y1 + s * 0.12f);
                g.DrawLine(pB, x1, y2, x0, y2);
                g.DrawLine(pB, x0, y2, x0 + s * 0.14f, y2 - s * 0.12f);
                g.DrawLine(pB, x0, y2, x0 + s * 0.14f, y2 + s * 0.12f);
            }
        }

        private static void FillRoundedRect(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var path = new GraphicsPath())
            {
                float d = radius * 2f;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        public void Dispose()
        {
            foreach (var kv in _cache) { try { kv.Value.Dispose(); } catch { } }
            foreach (var kv in _handles) { try { Native.NativeMethods.DestroyIcon(kv.Value); } catch { } }
            _cache.Clear();
            _handles.Clear();
        }
    }
}

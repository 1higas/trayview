using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace TrayView
{
    // Tray and list icons: a page's favicon, or a globe in the taskbar's text color when the page
    // has no favicon (yet) or no page is set up.
    static class Icons
    {
        const string Globe = "";

        // Decodes a favicon; null if it is not a usable image.
        public static Bitmap Decode(byte[] png)
        {
            if (png == null || png.Length == 0)
                return null;
            try
            {
                using (MemoryStream stream = new MemoryStream(png))
                using (Bitmap source = new Bitmap(stream))
                    return new Bitmap(source); // detached from the stream
            }
            catch (ArgumentException) { return null; }
        }

        // An .ico (its largest image) or any other image GDI+ reads, as PNG; null if unreadable.
        public static byte[] ToPng(byte[] data)
        {
            if (data == null || data.Length == 0 || data.Length > 1 << 20)
                return null;
            Bitmap image = null;
            try
            {
                using (MemoryStream stream = new MemoryStream(data))
                using (Icon icon = new Icon(stream, 256, 256))
                    image = icon.ToBitmap();
            }
            catch (Exception) { image = Decode(data); }
            if (image == null)
                return null;
            using (image)
            using (MemoryStream png = new MemoryStream())
            {
                image.Save(png, ImageFormat.Png);
                return png.ToArray();
            }
        }

        public static Icon Favicon(byte[] png, int size)
        {
            using (Bitmap source = Decode(png))
            {
                if (source == null)
                    return null;
                using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(source, 0, 0, size, size);
                    }
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
        }

        // The placeholder, drawn like the system's own tray icons: a Segoe Fluent Icons glyph, white
        // on a dark taskbar and near-black on a light one.
        public static Icon Placeholder(int size)
        {
            Color color = Theme.IsLight("SystemUsesLightTheme") ? Theme.Rgb(0x1A1A1A) : Color.White;
            using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (GraphicsPath path = new GraphicsPath())
                using (SolidBrush brush = new SolidBrush(color))
                {
                    FontFamily family;
                    try { family = new FontFamily("Segoe Fluent Icons"); }
                    catch (ArgumentException) { family = new FontFamily("Segoe MDL2 Assets"); }
                    path.AddString(Globe, family, 0, 100, Point.Empty, StringFormat.GenericTypographic);
                    family.Dispose();
                    RectangleF b = path.GetBounds();
                    float scale = size * 0.875f / Math.Max(b.Width, b.Height);
                    using (Matrix m = new Matrix())
                    {
                        m.Translate(size / 2f, size / 2f);
                        m.Scale(scale, scale);
                        m.Translate(-b.X - b.Width / 2, -b.Y - b.Height / 2);
                        path.Transform(m);
                    }
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(brush, path);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        public static void Destroy(Icon icon)
        {
            if (icon == null)
                return;
            Native.DestroyIcon(icon.Handle);
            icon.Dispose();
        }
    }
}

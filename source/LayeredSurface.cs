using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexUsageFloat
{
    internal delegate void SurfaceUpload(IntPtr window, Bitmap bitmap, Point position, byte opacity, ref bool configured);

    // A premultiplied-alpha surface keeps the circle edge smooth without
    // making the center translucent or cutting it to an integer-pixel Region.
    internal static class LayeredSurface
    {
        private const long Layered = 0x80000;

        internal static void Reset(IntPtr window)
        {
            long style = Native.GetWindowLongPtr(window, -20).ToInt64();
            SetWindowLongPtr(window, -20, new IntPtr(style & ~Layered));
            Native.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x37);
        }

        internal static void Update(IntPtr window, Bitmap bitmap, Point position, byte opacity, ref bool configured)
        {
            long style = Native.GetWindowLongPtr(window, -20).ToInt64();
            if (!configured || (style & Layered) == 0)
            {
                // SetLayeredWindowAttributes (used by Form.Opacity) and
                // UpdateLayeredWindow cannot be mixed without resetting this bit.
                SetWindowLongPtr(window, -20, new IntPtr(style & ~Layered));
                SetWindowLongPtr(window, -20, new IntPtr(style | Layered));
                configured = true;
            }
            IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, dib = IntPtr.Zero, previous = IntPtr.Zero;
            try
            {
                screen = GetDC(IntPtr.Zero);
                memory = CreateCompatibleDC(screen);
                if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var info = new BitmapInfo();
                info.Header.Size = 40;
                info.Header.Width = bitmap.Width;
                info.Header.Height = -bitmap.Height;
                info.Header.Planes = 1;
                info.Header.BitCount = 32;
                info.Header.ImageSize = (uint)(bitmap.Width * bitmap.Height * 4);
                IntPtr pixels;
                dib = CreateDIBSection(memory, ref info, 0, out pixels, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var row = new byte[bitmap.Width * 4];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(pixels, y * row.Length), row.Length);
                    }
                }
                finally { bitmap.UnlockBits(data); }
                previous = SelectObject(memory, dib);
                var destination = new NativePoint { X = position.X, Y = position.Y };
                var source = new NativePoint();
                var size = new NativeSize { Width = bitmap.Width, Height = bitmap.Height };
                var blend = new Blend { ConstantAlpha = opacity, AlphaFormat = 1 };
                if (!UpdateLayeredWindow(window, screen, ref destination, ref size, memory, ref source, 0, ref blend, 2))
                {
                    configured = false;
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                if (previous != IntPtr.Zero && memory != IntPtr.Zero) SelectObject(memory, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { internal int Width, Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { internal byte Operation, Flags, ConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, BitCount;
            internal uint Compression, ImageSize;
            internal int XPixels, YPixels;
            internal uint ColorsUsed, ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { internal BitmapHeader Header; internal uint Color; }
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, ref NativePoint position,
            ref NativeSize size, IntPtr sourceDc, ref NativePoint source, uint colorKey, ref Blend blend, uint flags);
    }
}

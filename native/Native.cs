// Вызовы Windows, которых нет в WinForms: прозрачное окно с попиксельной альфой, глобальная клавиша, z-порядок.
using System.Runtime.InteropServices;

namespace PokerDice;

static class Native
{
    public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80;
    public const int WM_HOTKEY = 0x312, WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
    public const int MOD_NOREPEAT = 0x4000, VK_PRIOR = 0x21;
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2), HWND_BROADCAST = new(0xffff);
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pos, ref SIZE size,
        IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, int usage, out IntPtr bits, IntPtr section, int offset);

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, int mods, int vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
}

// Буфер кадра для прозрачного окна: пиксели BGRA с предумноженной альфой, их и рисуем, и показываем
sealed class LayeredBuffer : IDisposable
{
    public readonly int Width, Height;
    public readonly IntPtr Bits;
    readonly IntPtr dc, bmp, old;

    public LayeredBuffer(int w, int h)
    {
        Width = w; Height = h;
        var bi = new Native.BITMAPINFOHEADER { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
        var screen = Native.GetDC(IntPtr.Zero);
        dc = Native.CreateCompatibleDC(screen);
        Native.ReleaseDC(IntPtr.Zero, screen);
        bmp = Native.CreateDIBSection(dc, ref bi, 0, out Bits, IntPtr.Zero, 0);
        old = Native.SelectObject(dc, bmp);
    }

    public unsafe Span<uint> Pixels => new((void*)Bits, Width * Height);

    // GDI+ поверх тех же пикселей (для текста и плашек)
    public Bitmap AsBitmap() => new(Width, Height, Width * 4, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, Bits);

    public void Present(IntPtr hwnd, int x, int y, byte alpha = 255)
    {
        var pos = new Native.POINT { X = x, Y = y };
        var size = new Native.SIZE { W = Width, H = Height };
        var src = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = alpha, Format = 1 };
        Native.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref pos, ref size, dc, ref src, 0, ref blend, 2);
    }

    public void Dispose()
    {
        Native.SelectObject(dc, old);
        Native.DeleteObject(bmp);
        Native.DeleteDC(dc);
    }
}

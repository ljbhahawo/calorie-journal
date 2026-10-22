using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalorieJournal.Desktop.Tests;

internal static class IconChecks
{
    public static void Verify(Window window)
    {
        Assert.IsNotNull(window.Icon);
        var uri = new Uri("pack://application:,,,/CalorieJournal;component/Assets/app-icon.ico");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
        CollectionAssert.AreEquivalent(new[] { 16, 24, 32, 48, 64, 128, 256 }, frames.Select(x => x.PixelWidth).ToArray());
        var windowIcon = SendMessage(new WindowInteropHelper(window).Handle, 0x007F, 1, 0);
        Assert.AreNotEqual(0L, windowIcon.ToInt64(), "系统窗口大图标未设置。");
        var systemImage = Imaging.CreateBitmapSourceFromHIcon(windowIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        CollectionAssert.AreEqual(Pixels(frames.Single(frame => frame.PixelWidth == systemImage.PixelWidth)), Pixels(systemImage), "系统窗口大图标与ICO资源不一致。");
        var executable = Path.Combine(AppContext.BaseDirectory, "CalorieJournal.exe");
        var large = new nint[1];
        var extracted = ExtractIconEx(executable, 0, large, null, 1);
        try
        {
            Assert.AreEqual(1u, extracted);
            var actual = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var expected = frames.Single(frame => frame.PixelWidth == actual.PixelWidth);
            var actualBytes = Pixels(actual); var expectedBytes = Pixels(expected);
            CollectionAssert.AreEqual(expectedBytes, actualBytes, "EXE嵌入图标与发布资源不同。");
        }
        finally
        {
            if (large[0] != 0) DestroyIcon(large[0]);
        }
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return bytes;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, [Out] nint[] large, [Out] nint[]? small, uint count);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}

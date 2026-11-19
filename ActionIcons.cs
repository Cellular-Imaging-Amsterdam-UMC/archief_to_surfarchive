using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SurfArchive;

// The order matches the tiles in Assets/ActionIcons.png.
internal enum ActionIcon
{
    Archive, Upload, Download, Tape, FolderPlus, Trash, Up, Refresh, History,
    SignIn, SignOut, Pause, Play, Cancel, Check, Browse, Save, Recovery
}

internal static class ActionIcons
{
    private const int TileSize = 48;
    private static readonly Lazy<Bitmap> Sheet = new(LoadSheet);
    private static readonly Dictionary<(ActionIcon Icon, Color Color, int Size), Bitmap> Cache = [];

    public static Bitmap Get(ActionIcon icon, Color color, int size)
    {
        var key = (icon, color, size);
        if (Cache.TryGetValue(key, out var bitmap))
            return bitmap;

        bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var attributes = new ImageAttributes();
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        attributes.SetColorMatrix(new ColorMatrix
        {
            Matrix00 = color.R / 255f, Matrix11 = color.G / 255f, Matrix22 = color.B / 255f
        });
        graphics.DrawImage(Sheet.Value, new Rectangle(0, 0, size, size), (int)icon * TileSize, 0,
            TileSize, TileSize, GraphicsUnit.Pixel, attributes);
        Cache.Add(key, bitmap);
        return bitmap;
    }

    private static Bitmap LoadSheet()
    {
        using var stream = typeof(ActionIcons).Assembly.GetManifestResourceStream("SurfArchive.ActionIcons.png")
            ?? throw new InvalidOperationException("The app icon sheet is missing.");
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }
}

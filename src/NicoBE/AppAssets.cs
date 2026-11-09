using System.IO;
using System.Windows.Media.Imaging;

namespace NicoBE;
public static class AppAssets
{
    public static string IconPath=>Path.Combine(Paths.ExecutableDirectory,"assets","nicobe.ico");
    public static BitmapSource? WindowIcon
    {
        get
        {
            if(!File.Exists(IconPath))return null;
            var bitmap=BitmapFrame.Create(new Uri(IconPath),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);bitmap.Freeze();return bitmap;
        }
    }
}

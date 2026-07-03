using System.Drawing;
using System.Reflection;

namespace TextCascadeSharp.App;

internal static class AppIcons
{
    public static Icon App { get; } = LoadIcon("app.ico") ?? SystemIcons.Application;

    public static Icon Tray { get; } = LoadIcon("tray.ico") ?? App;

    private static Icon? LoadIcon(string fileName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }
}

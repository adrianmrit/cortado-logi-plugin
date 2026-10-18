using System.Collections.Concurrent;

using Cortado.Core;

namespace Loupedeck.CortadoPlugin
{
    internal static class CupRenderer
    {
        private static readonly ConcurrentDictionary<(PluginImageSize Size, Boolean Active), Lazy<Byte[]>> entries = new();

        public static BitmapImage Entry(PluginImageSize size, SessionSnapshot snapshot) =>
            entries.GetOrAdd((size, snapshot.IsActive), key => new(() => RenderEntry(key.Size, key.Active))).Value.ToImage();

        private static Byte[] RenderEntry(PluginImageSize size, Boolean active)
        {
            using var bitmap = new BitmapBuilder(size);
            bitmap.Clear(BitmapColor.Transparent);
            var width = bitmap.Width;
            using var cup = ReadCup(active);
            // Center the visible cup and handle, not the SVG's padded viewport.
            bitmap.DrawImage(cup, width / 50, bitmap.Height * 6 / 25, width * 9 / 10, bitmap.Height * 17 / 25);
            return bitmap.ToArray();
        }

        public static BitmapImage Icon()
        {
            using var bitmap = new BitmapBuilder(256, 256);
            bitmap.Clear(BitmapColor.Transparent);
            using var cup = ReadCup(true);
            bitmap.DrawImage(cup, -39, 22, 312, 218);
            return bitmap.ToImage();
        }

        private static BitmapImage ReadCup(Boolean active)
        {
            var resource = active ? "cup-on.svg" : "cup-off.svg";
            var resourceName = "Loupedeck.CortadoPlugin.Assets." + resource;
            using var stream = typeof(CupRenderer).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing coffee cup resource '{resourceName}' (active: {active}).");
            using var reader = new StreamReader(stream);
            return BitmapImage.FromSvg(reader.ReadToEnd());
        }

        public static BitmapImage Menu(PluginImageSize size, MenuItem item)
        {
            using var bitmap = new BitmapBuilder(size);
            bitmap.Clear(BitmapColor.Transparent);
            var verb = item.Parameter[(item.Parameter.IndexOf('|') + 1)..];
            var clock = "<circle cx='12' cy='12' r='9'/><path d='M12 6v6l4 2'/>";
            var hourglass = "<path d='M6 3h12M6 21h12M7 3v4c0 3 10 7 10 10v4M17 3v4c0 3-10 7-10 10v4'/>";
            var glyph = verb switch
            {
                "indefinite" => "<path d='M12 12C8 5 2 7 2 12s6 7 10 0 10-5 10 0-6 7-10 0Z'/>",
                "duration" or "minutes" or "hours" => hourglass,
                "time" or "change" or "value" => clock,
                "presets" => "<rect x='3' y='3' width='7' height='7' rx='1'/><rect x='14' y='3' width='7' height='7' rx='1'/><rect x='3' y='14' width='7' height='7' rx='1'/><rect x='14' y='14' width='7' height='7' rx='1'/>",
                "stop" => "<rect x='5' y='5' width='14' height='14' rx='2'/>",
                "extend5" or "extend60" => "<path d='M12 3a9 9 0 1 0 9 9M12 6v6l-4 2M19 2v8M15 6h8'/>",
                "settings" => "<path d='M4 3v4M4 11v10M12 3v11M12 18v3M20 3v2M20 9v12'/><rect x='1' y='7' width='6' height='4' rx='1'/><rect x='9' y='14' width='6' height='4' rx='1'/><rect x='17' y='5' width='6' height='4' rx='1'/>",
                "mac" => "<path d='M5 4h14v12H5zM3 20h18l-2-4H5z'/>",
                "display" => "<rect x='2' y='3' width='20' height='14' rx='2'/><path d='M12 17v4M8 21h8'/>",
                "hourUp" or "minuteUp" => "<circle cx='12' cy='12' r='9'/><path d='M12 7v10M7 12h10'/>",
                "hourDown" or "minuteDown" => "<circle cx='12' cy='12' r='9'/><path d='M7 12h10'/>",
                "done" => "<path d='m4 12 5 5L20 6'/>",
                "previous" => "<path d='m15 4-8 8 8 8'/>",
                "next" => "<path d='m9 4 8 8-8 8'/>",
                "error" => "<path d='m12 2 10 19H2Z M12 9v5M12 17v1'/>",
                "notice" => "<circle cx='12' cy='12' r='9'/><path d='M12 11v6M12 7v1'/>",
                _ when verb.StartsWith("confirm", StringComparison.Ordinal) => "<path d='m4 12 5 5L20 6'/>",
                _ when verb.StartsWith("preset", StringComparison.Ordinal) => hourglass,
                _ => throw new ArgumentException("No icon for this Cortado menu command.", nameof(item))
            };
            var selected = item.Label.StartsWith("[x] ", StringComparison.Ordinal)
                ? "<path d='m14 16 3 3 5-6' stroke='#f5b66b' stroke-width='3'/>" : "";
            var color = verb is "stop" or "error" ? "#ff9d8c" : "#dce1e8";
            using var icon = BitmapImage.FromSvg($"<svg xmlns='http://www.w3.org/2000/svg' width='96' height='96' viewBox='0 0 24 24'><g fill='none' stroke='{color}' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'>{glyph}{selected}</g></svg>");
            bitmap.DrawImage(icon, bitmap.Width / 6, bitmap.Height / 6, bitmap.Width * 2 / 3, bitmap.Height * 2 / 3);
            return bitmap.ToImage();
        }
    }
}
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

using Cortado.Core;

using Loupedeck;

namespace Cortado.Sdk.Tests;

[TestFixture]
[Platform("MacOsX")]
public sealed class SdkContractTests
{
    private const String DefaultSdk = "/Applications/Utilities/LogiPluginService.app/Contents/MonoBundle";
    private String directory = null!;
    private Assembly plugin = null!;
    private Assembly sdk = null!;
    private FieldInfo logDirectoryField = null!;
    private String? originalLogDirectory;
    private String testLogDirectory = null!;
    private Boolean logDirectoryChanged;

    [OneTimeSetUp]
    public void SetUp()
    {
        directory = Environment.GetEnvironmentVariable("CORTADO_SDK_DIR") ?? DefaultSdk;
        AssemblyLoadContext.Default.Resolving += ResolveAssembly;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNativeLibrary;
        sdk = Assembly.LoadFrom(Path.Combine(directory, "PluginApi.dll"));
        // SDK plugin construction deletes its previous log; isolate it from the running service.
        logDirectoryField = sdk.GetType("Loupedeck.PluginLogFile", throwOnError: true)!
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static).Single(field => field.FieldType == typeof(String));
        originalLogDirectory = (String?)logDirectoryField.GetValue(null);
        testLogDirectory = Path.Combine(Path.GetTempPath(), "CortadoSdkTests-" + Guid.NewGuid().ToString("N"));
        logDirectoryField.SetValue(null, testLogDirectory);
        logDirectoryChanged = true;
        plugin = Assembly.LoadFrom(Path.Combine(TestContext.CurrentContext.TestDirectory, "CortadoPlugin.dll"));
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        AssemblyLoadContext.Default.Resolving -= ResolveAssembly;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveNativeLibrary;
        if (logDirectoryChanged && logDirectoryField is not null)
        {
            logDirectoryField.SetValue(null, originalLogDirectory);
        }
        if (testLogDirectory is not null && Directory.Exists(testLogDirectory))
        {
            Directory.Delete(testLogDirectory, recursive: true);
        }
    }

    [Test]
    public void PluginConstruction_UsesIsolatedLogsRatherThanDeletingTheRunningPluginLog()
    {
        Directory.CreateDirectory(testLogDirectory);
        var log = Path.Combine(testLogDirectory, "Cortado.log");
        File.WriteAllText(log, "Test-only previous log.");
        using var owner = new Loupedeck.CortadoPlugin.CortadoPlugin();
        Assert.That(File.Exists(log), Is.False, "The SDK constructor must clear only the isolated test log.");
    }

    private Assembly? ResolveAssembly(AssemblyLoadContext context, AssemblyName name)
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        var path = new[] { directory, Path.Combine(directory, ".xamarin", architecture) }
            .Select(folder => Path.Combine(folder, name.Name + ".dll")).FirstOrDefault(File.Exists);
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }

    private IntPtr ResolveNativeLibrary(Assembly assembly, String name)
    {
        var path = Path.Combine(directory, name.EndsWith(".dylib", StringComparison.Ordinal) ? name : name + ".dylib");
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    [Test]
    public void Discovery_ExportsOneFolderAndNoStandaloneCommands()
    {
        var folder = sdk.GetType("Loupedeck.PluginDynamicFolder", throwOnError: true)!;
        var action = sdk.GetType("Loupedeck.PluginDynamicAction", throwOnError: true)!;
        var types = plugin.GetTypes().Where(type => !type.IsAbstract).ToArray();
        Assert.That(types.Where(folder.IsAssignableFrom).Select(type => type.Name), Is.EqualTo(new[] { "CortadoFolder" }));
        Assert.That(types.Where(action.IsAssignableFrom), Is.Empty);
    }

    [TestCase("Width60Pixels", 50)]
    [TestCase("Width90Pixels", 80)]
    [TestCase("Width116Pixels", 116)]
    [TestCase("PluginIcon", 256)]
    public void Entry_UsesOnlyActiveStateForTransparentArtwork(String sizeName, Int32 width)
    {
        var size = Enum.Parse(sdk.GetType("Loupedeck.PluginImageSize")!, sizeName);
        var renderer = plugin.GetType("Loupedeck.CortadoPlugin.CupRenderer")!;
        var method = renderer.GetMethod("Entry", BindingFlags.Public | BindingFlags.Static)!;
        var snapshots = new[]
        {
            new SessionSnapshot(0, KeepAwakeMode.MacOnly, null, null, null),
            new SessionSnapshot(1, KeepAwakeMode.MacOnly, new(SessionKind.Indefinite, KeepAwakeMode.MacOnly, null), null, null),
            new SessionSnapshot(2, KeepAwakeMode.MacOnly, new(SessionKind.Duration, KeepAwakeMode.MacOnly, DateTimeOffset.UtcNow.AddHours(12)), TimeSpan.FromHours(12), null),
            new SessionSnapshot(3, KeepAwakeMode.MacOnly, new(SessionKind.Until, KeepAwakeMode.MacOnly, DateTimeOffset.UtcNow.AddHours(1)), TimeSpan.FromHours(1), null),
            new SessionSnapshot(4, KeepAwakeMode.MacOnly, null, null, "A test error"),
            new SessionSnapshot(5, KeepAwakeMode.MacOnly, null, null, null)
            {
                ExternalSessions = [new(123, 100, 0, null)]
            },
            new SessionSnapshot(6, KeepAwakeMode.MacOnly, null, null, null)
            {
                ExternalSessions = [new(123, 100, 0, DateTimeOffset.UtcNow.AddMinutes(5))],
                ExternalRemaining = TimeSpan.FromMinutes(5)
            }
        };
        var images = new List<Byte[]>();
        foreach (var snapshot in snapshots)
        {
            using var image = (IDisposable)method.Invoke(null, [size, snapshot])!;
            var imageType = image.GetType();
            Assert.That(imageType.GetProperty("Width")!.GetValue(image), Is.EqualTo(width));
            Assert.That(imageType.GetProperty("Height")!.GetValue(image), Is.EqualTo(width));
            var bytes = (Byte[])imageType.GetMethod("ToArray", Type.EmptyTypes)!.Invoke(image, null)!;
            Assert.That(bytes.Length, Is.GreaterThan(100));
            AssertTransparentArtwork(bytes, width);
            images.Add(bytes);
            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Artwork");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, $"{sizeName}-{snapshot.Revision}.png"), bytes);
        }
        Assert.Multiple(() =>
        {
            Assert.That(images.Select(Convert.ToBase64String).Distinct().Count(), Is.EqualTo(2));
            Assert.That(images[0], Is.EqualTo(images[4]), "Error text belongs in the caption, not the icon.");
            Assert.That(images[1], Is.EqualTo(images[2]).And.EqualTo(images[3]), "Countdown and end-time text must not change the active artwork.");
            Assert.That(images[1], Is.EqualTo(images[5]).And.EqualTo(images[6]), "External sessions use the active cup, including unknown end times.");
            Assert.That(images[0], Is.Not.EqualTo(images[1]));
        });
    }

    [Test]
    public void Icon_RendersOriginalCupForPackaging()
    {
        var renderer = plugin.GetType("Loupedeck.CortadoPlugin.CupRenderer")!;
        using var image = (IDisposable)renderer.GetMethod("Icon")!.Invoke(null, null)!;
        var bytes = (Byte[])image.GetType().GetMethod("ToArray", Type.EmptyTypes)!.Invoke(image, null)!;
        var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Artwork");
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output, "Icon256x256.png"), bytes);
        Assert.That(image.GetType().GetProperty("Width")!.GetValue(image), Is.EqualTo(256));
        AssertTransparentArtwork(bytes, 256);
        var bounds = ArtworkBounds(bytes, 256);
        Assert.Multiple(() =>
        {
            Assert.That(Math.Max(bounds.Width, bounds.Height), Is.InRange(256 * 0.80, 256 * 0.85));
            Assert.That(bounds.CenterX, Is.EqualTo(128).Within(2));
            Assert.That(bounds.CenterY, Is.EqualTo(128).Within(4));
        });
    }

    [TestCase("Width60Pixels")]
    [TestCase("Width90Pixels")]
    [TestCase("Width116Pixels")]
    [TestCase("PluginIcon")]
    public void Entry_CupIsLargerLowerAndHorizontallyCentered(String sizeName)
    {
        var size = Enum.Parse<PluginImageSize>(sizeName);
        var renderer = plugin.GetType("Loupedeck.CortadoPlugin.CupRenderer")!;
        var entry = renderer.GetMethod("Entry")!;
        foreach (var active in new[] { false, true })
        {
            var snapshot = new SessionSnapshot(0, KeepAwakeMode.MacOnly,
                active ? new(SessionKind.Indefinite, KeepAwakeMode.MacOnly, null) : null, null, null);
            using var image = (BitmapImage)entry.Invoke(null, [size, snapshot])!;
            using var cup = (BitmapImage)renderer.GetMethod("ReadCup", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [active])!;
            using var previous = new BitmapBuilder(size);
            previous.Clear(BitmapColor.Transparent);
            previous.DrawImage(cup, previous.Width / 10, previous.Height / 5, previous.Width * 4 / 5, previous.Height * 3 / 5);
            var before = ArtworkBounds(previous.ToArray(), previous.Width);
            var after = ArtworkBounds(image.ToArray(), image.Width);
            Assert.Multiple(() =>
            {
                Assert.That((Double)after.Width / before.Width, Is.InRange(1.10, 1.16));
                Assert.That(after.Height, Is.InRange(before.Height * 1.10 - 1, before.Height * 1.15 + 1),
                    "Allow one pixel of rasterization rounding at the smallest sizes.");
                Assert.That(after.Top, Is.GreaterThan(before.Top));
                Assert.That(after.CenterX, Is.EqualTo(image.Width / 2.0).Within(1));
                Assert.That(after.Top, Is.GreaterThanOrEqualTo(image.Height * 0.20));
                Assert.That(after.Bottom, Is.LessThanOrEqualTo(image.Height * 0.90), "Leave room below the cup for the native caption.");
            });
        }
    }

    private static String PackageDirectory => typeof(SdkContractTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "CortadoPackageDirectory").Value!;

    [Test]
    public void Package_DropdownIconResolvesWithoutReplacingLiveButtonArtwork()
    {
        var package = new PluginPackage();
        typeof(PluginPackage).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(package, [PackageDirectory]);
        var folder = new Loupedeck.CortadoPlugin.CortadoFolder();
        var action = new ActionString("Cortado", GenericActionNames.DynamicFolder, folder.Name);
        Assert.That(package.TryGetActionIcon(action, out var image), Is.True);
        using (image)
        {
            var expected = File.ReadAllBytes(Path.Combine(PackageDirectory, "metadata", "Icon256x256.png"));
            Assert.That(image.ToArray(), Is.EqualTo(expected));
            var renderer = plugin.GetType("Loupedeck.CortadoPlugin.CupRenderer")!;
            using var rendered = (BitmapImage)renderer.GetMethod("Icon")!.Invoke(null, null)!;
            Assert.That(expected, Is.EqualTo(rendered.ToArray()), "The packaged PNG must be regenerated when Icon() changes.");
        }
        Assert.That(package.TryGetActionIcon(new ActionString("Cortado", GenericActionNames.DynamicFolder, null), out var dropdown), Is.True);
        dropdown.Dispose();
        Assert.That(package.TryGetActionSymbolBitmap(GenericActionNames.DynamicFolder, folder.Name, out var symbol), Is.True);
        using (symbol)
        {
            Assert.That(symbol.ToArray(), Is.EqualTo(File.ReadAllBytes(Path.Combine(PackageDirectory, "metadata", "Icon256x256.png"))));
        }

        Assert.That(package.TryGetActionSymbolBitmap(GenericActionNames.DynamicFolder, null, out var genericSymbol), Is.True);
        genericSymbol.Dispose();
        Assert.That(folder.GetButtonLibraryImage(), Is.Null, "A static library image would take priority over the live cup.");
        Assert.That(typeof(Loupedeck.CortadoPlugin.CortadoFolder).GetMethod("GetButtonImage")!.DeclaringType,
            Is.EqualTo(typeof(Loupedeck.CortadoPlugin.CortadoFolder)));
        Assert.That(package.TryGetActionIcon(new ActionString("Cortado", folder.CommandName, "1|stop"), out _), Is.False,
            "The outer action fallback must not replace sub-menu artwork.");
    }

    [Test]
    public void Package_DefaultBackgroundIsOpaqueBlack()
    {
        var shared = Assembly.LoadFrom(Path.Combine(directory, "LoupedeckShared.dll"));
        var metadataType = shared.GetType("Loupedeck.Service.PluginLibraryPackageMetadata", throwOnError: true)!;
        var baseType = metadataType.BaseType!;
        var parser = baseType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "TryReadFromFile" && method.IsGenericMethodDefinition)
            .MakeGenericMethod(metadataType);
        Object?[] arguments = [Path.Combine(PackageDirectory, "metadata", "LoupedeckPackage.yaml"), null];
        Assert.That(parser.Invoke(null, arguments), Is.True);
        Assert.That(metadataType.GetProperty("BackgroundColor")!.GetValue(arguments[1]), Is.EqualTo(0xff000000u));
    }

    private readonly record struct Bounds(Int32 Left, Int32 Top, Int32 Right, Int32 Bottom)
    {
        public Int32 Width => Right - Left;
        public Int32 Height => Bottom - Top;
        public Double CenterX => (Left + Right) / 2.0;
        public Double CenterY => (Top + Bottom) / 2.0;
    }

    private static Bounds ArtworkBounds(Byte[] bytes, Int32 width)
    {
        var type = Assembly.Load("SkiaSharp").GetType("SkiaSharp.SKBitmap", throwOnError: true)!;
        using var bitmap = (IDisposable)type.GetMethod("Decode", [typeof(Byte[])])!.Invoke(null, [bytes])!;
        var pixel = type.GetMethod("GetPixel", [typeof(Int32), typeof(Int32)])!;
        var alpha = pixel.ReturnType.GetProperty("Alpha")!;
        var visible = Enumerable.Range(0, width * width)
            .Where(index => (Byte)alpha.GetValue(pixel.Invoke(bitmap, [index % width, index / width]))! >= 16).ToArray();
        return new(visible.Min(index => index % width), visible.Min(index => index / width),
            visible.Max(index => index % width) + 1, visible.Max(index => index / width) + 1);
    }

    private static IEnumerable<TestCaseData> MenuArtworkCases()
    {
        var verbs = new[]
        {
            "indefinite", "duration", "time", "presets", "settings", "stop", "extend5", "extend60", "change",
            "minutes", "hours", "preset5", "preset60", "preset720", "mac", "display", "notice", "done",
            "previous", "next", "value", "hourUp", "hourDown", "minuteUp", "minuteDown", "confirm0", "error"
        };
        foreach (var size in new[] { "Width60Pixels", "Width90Pixels", "Width116Pixels" })
        {
            foreach (var verb in verbs)
            {
                yield return new TestCaseData(size, verb);
            }
        }
    }

    [TestCaseSource(nameof(MenuArtworkCases))]
    public void Menu_IconsAreTransparentAndIndependentOfCaption(String sizeName, String verb)
    {
        var size = Enum.Parse(sdk.GetType("Loupedeck.PluginImageSize")!, sizeName);
        var renderer = plugin.GetType("Loupedeck.CortadoPlugin.CupRenderer")!;
        var method = renderer.GetMethod("Menu", BindingFlags.Public | BindingFlags.Static)!;
        using var first = (BitmapImage)method.Invoke(null, [size, new MenuItem("1|" + verb, "First caption")])!;
        using var second = (BitmapImage)method.Invoke(null, [size, new MenuItem("1|" + verb, "Different\ncaption")])!;
        Assert.That(first.ToArray(), Is.EqualTo(second.ToArray()), "Only native captions should contain the label.");
        AssertTransparentArtwork(first.ToArray(), first.Width);
        if (sizeName == "Width90Pixels")
        {
            var output = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Artwork");
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, $"menu-{verb}.png"), first.ToArray());
        }
    }

    [TestCase("Width60Pixels")]
    [TestCase("Width90Pixels")]
    [TestCase("Width116Pixels")]
    public void Menu_PresetCategoriesShareTheHourglassIcon(String sizeName)
    {
        var size = Enum.Parse<PluginImageSize>(sizeName);
        var renderer = plugin.GetType("Loupedeck.CortadoPlugin.CupRenderer")!;
        var method = renderer.GetMethod("Menu")!;
        using var minutes = (BitmapImage)method.Invoke(null, [size, new MenuItem("1|minutes", "Minutes")])!;
        using var hours = (BitmapImage)method.Invoke(null, [size, new MenuItem("1|hours", "Hours")])!;
        using var duration = (BitmapImage)method.Invoke(null, [size, new MenuItem("1|duration", "Duration")])!;
        using var time = (BitmapImage)method.Invoke(null, [size, new MenuItem("1|time", "Time")])!;
        Assert.That(minutes.ToArray(), Is.EqualTo(hours.ToArray()).And.EqualTo(duration.ToArray()).And.Not.EqualTo(time.ToArray()));
    }

    private static void AssertTransparentArtwork(Byte[] bytes, Int32 width)
    {
        var type = Assembly.Load("SkiaSharp").GetType("SkiaSharp.SKBitmap", throwOnError: true)!;
        using var bitmap = (IDisposable)type.GetMethod("Decode", [typeof(Byte[])])!.Invoke(null, [bytes])!;
        var pixel = type.GetMethod("GetPixel", [typeof(Int32), typeof(Int32)])!;
        var alpha = pixel.ReturnType.GetProperty("Alpha")!;
        var pixels = Enumerable.Range(0, width * width)
            .Select(index => (Byte)alpha.GetValue(pixel.Invoke(bitmap, [index % width, index / width]))!)
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(pixels.Count(value => value == 0), Is.GreaterThan(width * width / 2));
            Assert.That(pixels.Any(value => value == 255), Is.True, "Artwork must contain a visible icon.");
            Assert.That(pixels.Where((_, index) => index / width == 0 || index / width == width - 1 ||
                index % width == 0 || index % width == width - 1), Is.All.Zero, "The full border must be transparent.");
        });
    }

    [Test]
    public void PluginRefresh_ProcessInspectionFailureStillPublishesTheErrorStatus()
    {
        var monitor = new TestExternalMonitor();
        using var sessions = new SessionController(new TestStore(), new TestFactory(), TimeProvider.System, _ => { }, monitor);
        sessions.Initialize();
        using var owner = new Loupedeck.CortadoPlugin.CortadoPlugin();
        owner.GetType().GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, sessions);
        using var timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        owner.GetType().GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, timer);
        String? observedStatus = null;
        owner.GetType().GetEvent("Refreshed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetAddMethod(nonPublic: true)!.Invoke(owner, [(Action)(() => observedStatus = sessions.Snapshot.Status)]);
        monitor.FailRead = true;
        owner.GetType().GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
        Assert.That(observedStatus, Is.EqualTo("Error"));
    }

    [Test]
    public void Refresh_TargetsOuterFolderIconAndSkipsUnchangedStatus()
    {
        var clock = new TestClock();
        using var sessions = new SessionController(new TestStore(), new TestFactory(), clock, _ => { });
        sessions.Initialize();
        using var owner = new Loupedeck.CortadoPlugin.CortadoPlugin();
        typeof(Plugin).GetProperty("Name")!.SetValue(owner, "Cortado");
        owner.GetType().GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, sessions);
        var folder = new Loupedeck.CortadoPlugin.CortadoFolder();
        typeof(PluginDynamicFolder).GetProperty("Plugin")!.SetValue(folder, owner);
        var events = new System.Collections.Concurrent.ConcurrentQueue<ActionImageChangedEventArgs>();
        owner.ActionImageChanged += (_, args) => events.Enqueue(args);
        folder.Load();
        var refresh = folder.GetType().GetMethod("OnRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            refresh.Invoke(folder, null);
            Assert.That(SpinWait.SpinUntil(() => events.Count == 1, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(events.Single().ActionName, Is.EqualTo(GenericActionNames.DynamicFolder));
            Assert.That(events.Single().ActionParameter, Is.EqualTo(folder.Name));
            refresh.Invoke(folder, null);
            Assert.That(SpinWait.SpinUntil(() => events.Count > 1, TimeSpan.FromSeconds(2)), Is.False);
            Assert.That(events, Has.Count.EqualTo(1));
            sessions.StartDuration(TimeSpan.FromMinutes(5), 0);
            refresh.Invoke(folder, null);
            Assert.That(SpinWait.SpinUntil(() => events.Count == 2, TimeSpan.FromSeconds(2)), Is.True);
            using var before = folder.GetButtonImage(PluginImageSize.Width90Pixels);
            Assert.That(folder.GetButtonDisplayName(PluginImageSize.Width90Pixels), Is.EqualTo("5:00"));
            clock.Seconds++;
            sessions.Tick();
            refresh.Invoke(folder, null);
            Assert.That(SpinWait.SpinUntil(() => events.Count == 3, TimeSpan.FromSeconds(2)), Is.True);
            using var after = folder.GetButtonImage(PluginImageSize.Width90Pixels);
            Assert.That(folder.GetButtonDisplayName(PluginImageSize.Width90Pixels), Is.EqualTo("4:59"));
            Assert.That(before.ToArray(), Is.EqualTo(after.ToArray()), "Countdown updates only the native caption.");
        }
        finally
        {
            folder.Unload();
        }
    }

    [Test]
    public void ApplyAdjustment_UpdatesBindingsSoTheCurrentDraftCanBeConfirmed()
    {
        using var sessions = new SessionController(new TestStore(), new TestFactory(), TimeProvider.System, _ => { });
        sessions.Initialize();
        using var owner = new Loupedeck.CortadoPlugin.CortadoPlugin();
        typeof(Plugin).GetProperty("Name")!.SetValue(owner, "Cortado");
        owner.GetType().GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, sessions);
        var folder = new Loupedeck.CortadoPlugin.CortadoFolder();
        var bindingChanges = 0;
        var bindingsAfterClose = 0;
        var closeCommands = new List<String>();
        typeof(PluginDynamicFolder).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(folder,
        [
            owner,
            (Action<String, String, DeviceControlType>)((_, _, _) =>
            {
                bindingChanges++;
                if (closeCommands.Count > 0) { bindingsAfterClose++; } }),
            (Action<String, String, String>)((_, _, _) => { }),
            (Action<String, String, String>)((_, _, _) => { }),
            (Action<String, String, Int32>)((name, _, _) => closeCommands.Add(name))
        ]);
        folder.Load();
        var menu = (CortadoMenu)folder.GetType().GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(folder)!;
        try
        {
            folder.Activate();
            folder.RunCommand(menu.Items().Single(item => item.Label == "Duration").Parameter);
            var valueParameter = menu.Items().Single(item => item.Parameter.EndsWith("|value", StringComparison.Ordinal)).Parameter;
            using var before = folder.GetCommandImage(valueParameter, PluginImageSize.Width90Pixels);
            var oldChanges = bindingChanges;
            folder.ApplyAdjustment(menu.AdjustmentParameter("minute"), 2);
            Assert.That(bindingChanges, Is.GreaterThan(oldChanges));
            using var after = folder.GetCommandImage(valueParameter, PluginImageSize.Width90Pixels);
            Assert.That(folder.GetCommandDisplayName(valueParameter, PluginImageSize.Width90Pixels), Does.StartWith("0h 32m\n"));
            Assert.That(before.ToArray(), Is.EqualTo(after.ToArray()), "Draft text belongs in the native caption.");
            folder.RunCommand(menu.Items().Single(item => item.Label == "Start").Parameter);
            Assert.That(sessions.Snapshot.Remaining, Is.GreaterThan(TimeSpan.FromMinutes(31)).And.LessThanOrEqualTo(TimeSpan.FromMinutes(32)));
            Assert.That(closeCommands, Does.Contain(GenericActionNames.DynamicFolderGoUp));
            Assert.That(bindingsAfterClose, Is.Zero, "Do not rebuild a folder that has just closed after Start/Stop.");
        }
        finally
        {
            folder.Unload();
        }
    }

    [TestCase("Indefinitely")]
    [TestCase("Start")]
    [TestCase("Stop")]
    public void SuccessfulCommand_ClosesWithoutHoldingTheUiLockOrWaitingForStatusNotifications(String label)
    {
        using var sessions = new SessionController(new TestStore(), new TestFactory(), TimeProvider.System, _ => { });
        sessions.Initialize();
        if (label == "Stop")
        {
            sessions.StartIndefinite(sessions.Snapshot.Revision);
        }

        using var owner = CreateOwner(sessions);
        var folder = new Loupedeck.CortadoPlugin.CortadoFolder();
        var syncRoot = owner.GetType().GetProperty("SyncRoot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        var closeRequested = false;
        var lockHeldDuringClose = false;
        var deactivatedWithoutWaiting = false;
        Task? deactivation = null;
        typeof(PluginDynamicFolder).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(folder,
        [
            owner,
            (Action<String, String, DeviceControlType>)((_, _, _) => { }),
            (Action<String, String, String>)((_, _, _) => { }),
            (Action<String, String, String>)((_, _, _) => { }),
            (Action<String, String, Int32>)((name, _, _) =>
            {
                if (name != GenericActionNames.DynamicFolderGoUp) { return; } closeRequested = true;
                lockHeldDuringClose = Monitor.IsEntered(syncRoot);
                deactivation = Task.Run(() => folder.Deactivate());
                deactivatedWithoutWaiting = deactivation.Wait(TimeSpan.FromMilliseconds(250));
            })
        ]);
        folder.Load();
        try
        {
            folder.Activate();
            var menu = (CortadoMenu)folder.GetType().GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(folder)!;
            if (label == "Start")
            {
                folder.RunCommand(menu.Items().Single(item => item.Label == "Duration").Parameter);
            }

            var statusNotifiedBeforeClose = false;
            owner.PluginStatusChanged += (_, _) => statusNotifiedBeforeClose |= !closeRequested;
            folder.RunCommand(menu.Items().Single(item => item.Label == label).Parameter);
            Assert.Multiple(() =>
            {
                Assert.That(closeRequested, Is.True);
                Assert.That(lockHeldDuringClose, Is.False, "The host can call Deactivate on a different thread while closing.");
                Assert.That(deactivatedWithoutWaiting, Is.True, "Host deactivation must not wait for the command's UI lock.");
                Assert.That(statusNotifiedBeforeClose, Is.False, "Navigation takes priority over plugin-status notifications.");
                Assert.That(sessions.Snapshot.IsActive, Is.EqualTo(label != "Stop"));
            });
        }
        finally
        {
            deactivation?.Wait(TimeSpan.FromSeconds(3));
            folder.Unload();
        }
    }

    [Test]
    public void PluginStatus_OnlyPublishesActualChangesAndDoesNotRequireForegroundActivation()
    {
        var monitor = new TestExternalMonitor();
        using var sessions = new SessionController(new TestStore(), new TestFactory(), TimeProvider.System, _ => { }, monitor);
        sessions.Initialize();
        using var owner = CreateOwner(sessions);
        var notifications = 0;
        owner.PluginStatusChanged += (_, _) => notifications++;
        var publish = owner.GetType().GetMethod("PublishStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
        publish.Invoke(owner, null);
        publish.Invoke(owner, null);
        Assert.That(notifications, Is.EqualTo(1));
        monitor.FailRead = true;
        sessions.Tick();
        publish.Invoke(owner, null);
        publish.Invoke(owner, null);
        Assert.That(notifications, Is.EqualTo(2));
        monitor.FailRead = false;
        sessions.Tick();
        publish.Invoke(owner, null);
        publish.Invoke(owner, null);
        Assert.That(notifications, Is.EqualTo(3));
        Assert.That(owner.HasNoApplication, Is.True);
        Assert.That(owner.UsesApplicationApiOnly, Is.True);
    }

    [Test]
    public void LateRefreshAfterUnload_DoesNotReinitializeTheCoordinator()
    {
        using var sessions = new SessionController(new TestStore(), new TestFactory(), TimeProvider.System, _ => { });
        sessions.Initialize();
        using var owner = CreateOwner(sessions);
        var folder = CreateFolder(owner);
        owner.Unload();
        try
        {
            folder.GetType().GetMethod("OnRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(folder, null);
            owner.GetType().GetMethod("PublishStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
            Assert.That(owner.GetType().GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner), Is.Null);
        }
        finally { folder.Unload(); }
    }

    [TestCase("Duration", false)]
    [TestCase("Time", false)]
    [TestCase("Change\nend time", true)]
    public void NativeBack_ReactivationDiscardsDraftAndPreservesSession(String label, Boolean active)
    {
        using var sessions = new SessionController(new TestStore(), new TestFactory(), TimeProvider.System, _ => { });
        sessions.Initialize();
        if (active)
        {
            sessions.StartDuration(TimeSpan.FromMinutes(30), 0);
        }

        var original = sessions.Snapshot;
        using var owner = new Loupedeck.CortadoPlugin.CortadoPlugin();
        typeof(Plugin).GetProperty("Name")!.SetValue(owner, "Cortado");
        owner.GetType().GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, sessions);
        var folder = new Loupedeck.CortadoPlugin.CortadoFolder();
        typeof(PluginDynamicFolder).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(folder,
        [
            owner,
            (Action<String, String, DeviceControlType>)((_, _, _) => { }),
            (Action<String, String, String>)((_, _, _) => { }),
            (Action<String, String, String>)((_, _, _) => { }),
            (Action<String, String, Int32>)((_, _, _) => { })
        ]);
        folder.Load();
        var menu = (CortadoMenu)folder.GetType().GetField("menu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(folder)!;
        try
        {
            folder.Activate();
            folder.RunCommand(menu.Items().Single(item => item.Label == label).Parameter);
            folder.ApplyAdjustment(menu.AdjustmentParameter("hour"), 1);
            Assert.That(menu.Items().Select(item => item.Label), Does.Not.Contain("Cancel").And.Not.Contain("Back"));
            Assert.That(folder.GetNavigationArea(DeviceType.Loupedeck70), Is.EqualTo(PluginDynamicFolderNavigation.ButtonArea));
            var slots = folder.GetButtonPressActionNames(DeviceType.Loupedeck70).ToArray();
            var createCommand = typeof(PluginDynamicFolder).GetMethod("CreateCommandName", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var byParameter = menu.Items().ToDictionary(item => (String)createCommand.Invoke(folder, [item.Parameter])!, item => item.Label);
            Assert.That(slots, Has.Length.EqualTo(8), "The host adds native Back as the ninth button at index zero.");
            Assert.That(slots.Select(slot => slot.Length == 0 ? "" : byParameter[slot]), Is.EqualTo(new[]
            {
                menu.DraftLabel + "\nShared draft", active ? "Save" : "Start",
                "Minute -", "", "Minute +",
                "Hour -", "", "Hour +"
            }));
            sessions.ReportFailure(new InvalidOperationException("A selector error."));
            var errorSlots = folder.GetButtonPressActionNames(DeviceType.Loupedeck70).ToArray();
            Assert.That(errorSlots[3], Is.EqualTo(createCommand.Invoke(folder,
                [menu.Items().Single(item => item.Label == "View error").Parameter])));
            Assert.That(errorSlots.Where((_, index) => index != 3), Is.EqualTo(slots.Where((_, index) => index != 3)),
                "Error details must use an empty center slot without shifting selector controls.");
            folder.Deactivate();
            folder.Activate();
            Assert.Multiple(() =>
            {
                Assert.That(menu.View, Is.EqualTo(MenuView.Root));
                Assert.That(menu.Items().Select(item => item.Label), Does.Not.Contain("Back").And.Not.Contain("Cancel"));
                Assert.That(sessions.Snapshot.Active, Is.EqualTo(original.Active));
                Assert.That(sessions.Snapshot.Revision, Is.EqualTo(original.Revision));
            });
        }
        finally
        {
            folder.Unload();
        }
    }

    [TestCase("Off")]
    [TestCase("Indefinitely")]
    [TestCase("5:00")]
    [TestCase("Unknown")]
    public void BindingReload_ReannouncesUnchangedLiveStateWithoutRestartingSessions(String status)
    {
        var clock = new TestClock();
        var factory = new TestFactory();
        var monitor = status == "Unknown"
            ? new TestExternalMonitor { Sessions = [new(123, 100, 0, null)] } : null;
        using var sessions = new SessionController(new TestStore(), factory, clock, _ => { }, monitor);
        sessions.Initialize();
        if (status == "Indefinitely")
        {
            sessions.StartIndefinite(sessions.Snapshot.Revision);
        }

        if (status == "5:00")
        {
            sessions.StartDuration(TimeSpan.FromMinutes(5), sessions.Snapshot.Revision);
        }

        var revision = sessions.Snapshot.Revision;
        var launches = factory.Starts;
        using var owner = CreateOwner(sessions);
        var events = new System.Collections.Concurrent.ConcurrentQueue<ActionImageChangedEventArgs>();
        owner.ActionImageChanged += (_, args) => events.Enqueue(args);
        var folder = CreateFolder(owner, clock);
        try
        {
            Assert.That(SpinWait.SpinUntil(() => events.Count == 1, TimeSpan.FromSeconds(2)), Is.True);
            folder.Unload();
            sessions.Tick();
            folder.Load();
            Assert.That(SpinWait.SpinUntil(() => events.Count == 2, TimeSpan.FromSeconds(2)), Is.True);
            folder.Activate();
            Assert.That(SpinWait.SpinUntil(() => events.Count == 3, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(folder.GetButtonDisplayName(PluginImageSize.Width90Pixels), Is.EqualTo(status));
            var secondBinding = CreateFolder(owner, clock);
            try
            {
                Assert.That(secondBinding.GetButtonDisplayName(PluginImageSize.Width90Pixels), Is.EqualTo(status));
                Assert.That(sessions.Snapshot.Revision, Is.EqualTo(revision));
                Assert.That(factory.Starts, Is.EqualTo(launches));
            }
            finally { secondBinding.Unload(); }
        }
        finally { folder.Unload(); }
    }

    [TestCase("Off")]
    [TestCase("Indefinitely")]
    [TestCase("Unknown")]
    public void Heartbeat_ReannouncesUnchangedStatusEveryFiveSeconds(String status)
    {
        var clock = new TestClock();
        var monitor = status == "Unknown"
            ? new TestExternalMonitor { Sessions = [new(123, 100, 0, null)] } : null;
        using var sessions = new SessionController(new TestStore(), new TestFactory(), clock, _ => { }, monitor);
        sessions.Initialize();
        if (status == "Indefinitely")
        {
            sessions.StartIndefinite(sessions.Snapshot.Revision);
        }

        using var owner = CreateOwner(sessions);
        var events = new System.Collections.Concurrent.ConcurrentQueue<ActionImageChangedEventArgs>();
        owner.ActionImageChanged += (_, args) => events.Enqueue(args);
        var folder = CreateFolder(owner, clock);
        try
        {
            Assert.That(SpinWait.SpinUntil(() => events.Count == 1, TimeSpan.FromSeconds(2)), Is.True);
            clock.Seconds = 4;
            sessions.Tick();
            folder.GetType().GetMethod("OnRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(folder, null);
            Assert.That(SpinWait.SpinUntil(() => events.Count > 1, TimeSpan.FromSeconds(2)), Is.False);
            Assert.That(events, Has.Count.EqualTo(1));
            clock.Seconds = 5;
            sessions.Tick();
            folder.GetType().GetMethod("OnRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(folder, null);
            Assert.That(SpinWait.SpinUntil(() => events.Count == 2, TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(folder.GetButtonDisplayName(PluginImageSize.Width90Pixels), Is.EqualTo(status));
        }
        finally { folder.Unload(); }
    }

    [Test]
    public void PluginPolling_WithNoBindings_DiscoversStartsAndExitsUsingOneTimer()
    {
        var monitor = new TestExternalMonitor();
        var factory = new TestFactory();
        using var sessions = new SessionController(new TestStore(), factory, TimeProvider.System, _ => { }, monitor);
        sessions.Initialize();
        using var owner = CreateOwner(sessions);
        try
        {
            owner.Load();
            var timer = owner.GetType().GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var originalTimer = timer.GetValue(owner);
            owner.Load();
            Assert.That(timer.GetValue(owner), Is.SameAs(originalTimer));
            monitor.Sessions = [new(123, 100, 0, null)];
            Assert.That(SpinWait.SpinUntil(() => sessions.Snapshot.Status == "Unknown", TimeSpan.FromSeconds(3)), Is.True);
            monitor.Sessions = [];
            Assert.That(SpinWait.SpinUntil(() => sessions.Snapshot.Status == "Off", TimeSpan.FromSeconds(3)), Is.True);
            Assert.That(factory.Starts, Is.Zero);
        }
        finally { owner.Unload(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BlockedInspection_DoesNotBlockRenderingOrOverlapPolling(Boolean uiLockHeld)
    {
        var monitor = new TestExternalMonitor();
        using var sessions = new SessionController(new TestStore(), new TestFactory(), TimeProvider.System, _ => { }, monitor);
        sessions.Initialize();
        using var owner = CreateOwner(sessions);
        using var timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        owner.GetType().GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, timer);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        monitor.BeforeRead = () => { entered.Set(); release.Wait(); };
        var folder = CreateFolder(owner);
        var refresh = owner.GetType().GetMethod("Refresh", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var syncRoot = owner.GetType().GetProperty("SyncRoot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        var polling = Task.Run(() =>
        {
            if (uiLockHeld)
            {
                lock (syncRoot)
                {
                    refresh.Invoke(owner, null);
                }
            }
            else
            {
                refresh.Invoke(owner, null);
            }
        });
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(3)), Is.True);
            var reads = monitor.Reads;
            refresh.Invoke(owner, null);
            Assert.That(monitor.Reads, Is.EqualTo(reads));
            var rendering = Task.Run(() =>
            {
                var caption = folder.GetButtonDisplayName(PluginImageSize.Width90Pixels);
                using var image = folder.GetButtonImage(PluginImageSize.Width90Pixels);
                return caption;
            });
            Assert.That(rendering.Wait(TimeSpan.FromSeconds(1)), Is.True, "Rendering must not wait for the native process scan.");
            Assert.That(rendering.Result, Is.EqualTo("Off"));
        }
        finally
        {
            release.Set();
            Assert.That(polling.Wait(TimeSpan.FromSeconds(3)), Is.True);
            monitor.BeforeRead = null;
            folder.Unload();
            owner.Unload();
        }
    }

    private static Loupedeck.CortadoPlugin.CortadoPlugin CreateOwner(SessionController sessions)
    {
        var owner = new Loupedeck.CortadoPlugin.CortadoPlugin();
        typeof(Plugin).GetProperty("Name")!.SetValue(owner, "Cortado");
        owner.GetType().GetField("sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(owner, sessions);
        return owner;
    }

    private static Loupedeck.CortadoPlugin.CortadoFolder CreateFolder(
        Loupedeck.CortadoPlugin.CortadoPlugin owner, TimeProvider? clock = null)
    {
        var folder = new Loupedeck.CortadoPlugin.CortadoFolder();
        typeof(PluginDynamicFolder).GetMethod("Init", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(folder,
        [
            owner,
            (Action<String, String, DeviceControlType>)((_, _, _) => { }),
            (Action<String, String, String>)((_, _, _) => { }),
            (Action<String, String, String>)((_, _, _) => { }),
            (Action<String, String, Int32>)((_, _, _) => { })
        ]);
        if (clock is not null)
        {
            folder.GetType().GetField("clock", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(folder, clock);
        }

        folder.Load();
        return folder;
    }

    private sealed class TestStore : IStateStore
    {
        public SavedState Load() => SavedState.Empty;
        public void Save(SavedState state) { }
    }

    private sealed class TestExternalMonitor : IExternalSessionMonitor
    {
        public Boolean FailRead { get; set; }
        public IReadOnlyList<ExternalCaffeinateSession> Sessions { get; set; } = [];
        public Action? BeforeRead { get; set; }
        public Int32 Reads;
        public IReadOnlyList<ExternalCaffeinateSession> Read(Int32? excludedPid)
        {
            Interlocked.Increment(ref Reads);
            BeforeRead?.Invoke();
            return FailRead ? throw new IOException("Process inspection failed.") : Sessions;
        }
        public void Stop(ExternalCaffeinateSession session) => throw new InvalidOperationException("No external test process to stop.");
    }

    private sealed class TestClock : TimeProvider
    {
        public Int64 Seconds { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
        public override Int64 GetTimestamp() => Seconds * TimeSpan.TicksPerSecond;
        public override Int64 TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class TestFactory : IAwakeProcessFactory
    {
        public String BootIdentity => "sdk-test-boot";
        public IAwakeProcess? Attach(ExternalCaffeinateSession identity) => null;
        public Int32 Starts { get; private set; }
        public IAwakeProcess Start(KeepAwakeMode mode, TimeSpan? remaining)
        {
            Starts++;
            return new TestProcess();
        }
    }

    private sealed class TestProcess : IAwakeProcess
    {
        private static Int32 nextPid = 1000;
        public ExternalCaffeinateSession Identity { get; } = new(Interlocked.Increment(ref nextPid), 100, 0, null);
        public Boolean HasExited { get; private set; }
        public void Stop() => HasExited = true;
        public void Dispose() { }
    }
}
using System.Linq;
using System.Text;
using Jellyfin.Plugin.JellyUsb.Configuration;
using Jellyfin.Plugin.JellyUsb.Playback;
using Jellyfin.Plugin.JellyUsb.TextRepair;
using Xunit;

namespace Jellyfin.Plugin.JellyUsb.Tests;

public class MojibakeTests
{
    [Theory]
    [InlineData("CapÃ­tulo 01", "Capítulo 01")]
    [InlineData(" L'HÃ©ritage", " L'Héritage")]
    [InlineData("Obi-Wan Confronts PadmÃ©", "Obi-Wan Confronts Padmé")]
    [InlineData("Lâ€™Ã©tÃ© meurtrier", "L’été meurtrier")]
    [InlineData("Ã€ bout de souffle", "À bout de souffle")]
    [InlineData("ÃƒÂ©tÃƒÂ©", "été")] // double-encoded twice
    public void FixDoubleEncoding_repairs(string broken, string expected)
        => Assert.Equal(expected, Mojibake.FixDoubleEncoding(broken));

    [Theory]
    [InlineData("Le Fabuleux Destin d'Amélie Poulain")]
    [InlineData("Les Garçons et Guillaume, à table !")]
    [InlineData("Sale temps à l'hôtel El Royale")]
    [InlineData("Ça « marche » — très bien… 100 °C, 2³, ½")]
    [InlineData("Œuvre, Ÿ, ß, ø, ñ, ü")]
    [InlineData("東京物語")]
    [InlineData("")]
    public void FixDoubleEncoding_leaves_correct_text(string text)
        => Assert.Equal(text, Mojibake.FixDoubleEncoding(text));

    [Fact]
    public void FixDoubleEncoding_mixed_correct_and_broken()
        => Assert.Equal("Été — été", Mojibake.FixDoubleEncoding("Été — Ã©tÃ©"));

    [Theory]
    [InlineData("Avril et le monde truqu�", "Avril et le monde truqué", "Avril et le monde truqué")]
    [InlineData("Les Gar�ons et Guillaume, � table !", "Les Garçons et Guillaume, à table !", "Les Garçons et Guillaume, à table !")]
    [InlineData("LARGU�ES", "Larguées", "LARGUÉES")] // case of the broken text is kept... except the filled letter
    public void FillReplacementChars_uses_matching_candidate(string broken, string candidate, string expected)
    {
        var result = Mojibake.FillReplacementChars(broken, [null, "Autre titre", candidate]);
        Assert.NotNull(result);
        Assert.Equal(expected.ToUpperInvariant(), result!.ToUpperInvariant());
        Assert.DoesNotContain('�', result);
    }

    [Fact]
    public void FillReplacementChars_rejects_non_matching_candidate()
        => Assert.Null(Mojibake.FillReplacementChars("Truth : Le Prix de la v�rit�", ["Truth"]));

    [Fact]
    public void Windows1252_roundtrip()
    {
        var bytes = new byte[] { 0x41, 0x6D, 0xE9, 0x6C, 0x69, 0x65, 0x20, 0x92, 0x80, 0x9C };
        Assert.Equal("Amélie ’€œ", Mojibake.DecodeWindows1252(bytes));
        Assert.False(Mojibake.IsValidUtf8(bytes));
        Assert.True(Mojibake.IsValidUtf8(Encoding.UTF8.GetBytes("Amélie")));
    }
}

public class LegacyNfoTests
{
    [Fact]
    public void Latin1_nfo_is_decoded_and_declaration_fixed()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"ISO-8859-1\" standalone=\"yes\"?>\n<movie><title>Larguées</title></movie>";
        var bytes = xml.Select(c => (byte)c).ToArray(); // Latin-1 bytes
        var text = LegacyNfo.TryDecodeLegacy(bytes);
        Assert.NotNull(text);
        Assert.Contains("<title>Larguées</title>", text, System.StringComparison.Ordinal);
        Assert.Contains("encoding=\"utf-8\"", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Utf8_nfo_is_left_to_the_standard_reader()
    {
        Assert.Null(LegacyNfo.TryDecodeLegacy(Encoding.UTF8.GetBytes("<movie><title>Larguées</title></movie>")));
        Assert.Null(LegacyNfo.TryDecodeLegacy([0xEF, 0xBB, 0xBF, (byte)'<', (byte)'a', (byte)'/', (byte)'>']));
        Assert.Null(LegacyNfo.TryDecodeLegacy(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<a>é</a>")).ToArray()));
    }
}

public class MountTableTests
{
    private const string Base = """
        22 1 252:0 / / rw,relatime shared:1 - ext4 /dev/mapper/ubuntu--vg-ubuntu--lv rw
        60 22 0:52 / /mnt/usb/MOVIES00 rw,relatime shared:30 - autofs systemd-1 rw,fd=50,pgrp=1
        61 22 0:53 / /mnt/usb/MOVIES01 rw,relatime shared:31 - autofs systemd-1 rw,fd=51,pgrp=1
        """;

    [Fact]
    public void Automount_without_drive_is_unmounted()
        => Assert.Equal(MountState.UnmountedAutomount, new MountTable(() => Base).GetState("/mnt/usb/MOVIES00"));

    [Fact]
    public void Automount_with_drive_is_mounted()
    {
        var table = Base + "\n90 60 8:18 / /mnt/usb/MOVIES00 rw,nosuid shared:40 - fuseblk /dev/sdb2 rw,user_id=0\n";
        Assert.Equal(MountState.Mounted, new MountTable(() => table).GetState("/mnt/usb/MOVIES00/"));
        Assert.Equal(MountState.UnmountedAutomount, new MountTable(() => table).GetState("/mnt/usb/MOVIES01"));
    }

    [Fact]
    public void Plain_folder_and_escapes()
    {
        var table = Base + "\n91 22 8:33 / /mnt/usb/MY\\040DISK rw - exfat /dev/sdc1 rw\n";
        var mounts = new MountTable(() => table);
        Assert.Equal(MountState.NotAMountPoint, mounts.GetState("/mnt/usb/MOVIES03"));
        Assert.Equal(MountState.Mounted, mounts.GetState("/mnt/usb/MY DISK"));
    }

    [Fact]
    public void Indirect_automount_parent_counts_as_unmounted()
    {
        var table = Base + "\n70 22 0:60 / /media/auto rw - autofs systemd-1 rw\n";
        Assert.Equal(MountState.UnmountedAutomount, new MountTable(() => table).GetState("/media/auto/MOVIES05"));
    }
}

public class PresenceTests
{
    [Theory]
    [InlineData("/mnt/usb/MOVIES03/MOVIES03/Film.mkv", "/mnt/usb/", "MOVIES03")]
    [InlineData("/mnt/usb/MOVIES99/Film.mkv", "/mnt/usb", "MOVIES99")]
    [InlineData("/mnt/usb/Disque Perso/a/b.mkv", "/mnt/usb/", "Disque Perso")]
    [InlineData("/srv/films/Film.mkv", "/mnt/usb/", null)]
    [InlineData("/mnt/usbx/MOVIES01/Film.mkv", "/mnt/usb/", null)]
    [InlineData(null, "/mnt/usb/", null)]
    public void GetMediaId(string? path, string basePath, string? expected)
        => Assert.Equal(expected, MediaPresenceChecker.GetMediaId(path, basePath));

    [Fact]
    public void Absent_automount_is_not_touched()
    {
        var checker = new MediaPresenceChecker(new MountTable(() => "60 22 0:52 / /mnt/usb/MOVIES00 rw - autofs systemd-1 rw"));
        var result = checker.Check("/mnt/usb/MOVIES00/MOVIES00/Film.mkv", "/mnt/usb/");
        Assert.Equal(PresenceStatus.MediaAbsent, result.Status);
        Assert.Equal("MOVIES00", result.MediaId);
    }

    [Fact]
    public void Missing_folder_empty_folder_missing_file_and_present_file()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellyusb-test-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "EMPTY01"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "MOVIES02", "MOVIES02"));
        var film = System.IO.Path.Combine(root, "MOVIES02", "MOVIES02", "Film.mkv");
        System.IO.File.WriteAllText(film, "x");
        try
        {
            var checker = new MediaPresenceChecker(new MountTable(() => string.Empty));
            Assert.Equal(PresenceStatus.MediaAbsent, checker.Check(root + "/MOVIES07/Film.mkv", root).Status);
            Assert.Equal(PresenceStatus.MediaAbsent, checker.Check(root + "/EMPTY01/Film.mkv", root).Status);
            Assert.Equal(PresenceStatus.FileMissing, checker.Check(root + "/MOVIES02/MOVIES02/Autre.mkv", root).Status);
            Assert.Equal(PresenceStatus.Present, checker.Check(film, root).Status);
            Assert.Equal(PresenceStatus.NotManaged, checker.Check("/srv/x.mkv", root).Status);
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }
}

public class ClientTests
{
    [Theory]
    [InlineData("Jellyfin Web", ClientKind.WebUi)]
    [InlineData("Jellyfin for Android", ClientKind.WebUi)]
    [InlineData("Jellyfin for Android TV", ClientKind.AndroidTv)]
    [InlineData("Jellyfin for Android TV (debug)", ClientKind.AndroidTv)]
    [InlineData("Kodi", ClientKind.Kodi)]
    [InlineData("Kodi JellyCon", ClientKind.Kodi)]
    [InlineData("Jellyfin Media Player", ClientKind.WebUi)]
    [InlineData("Swiftfin iOS", ClientKind.Other)]
    [InlineData("Findroid", ClientKind.Other)]
    [InlineData(null, ClientKind.Other)]
    public void Classify(string? client, ClientKind expected) => Assert.Equal(expected, ClientClassifier.Classify(client));
}

public class MessageTests
{
    [Fact]
    public void Templates_are_filled()
    {
        var config = new PluginConfiguration();
        var absent = MessageBuilder.Build(config, PresenceStatus.MediaAbsent, "Larguées", "MOVIES03", "/mnt/usb/MOVIES03/x.mkv");
        Assert.Equal("Support absent", absent.Header);
        Assert.Equal("« Larguées » n'est pas présent sur le serveur. Insérez le support MOVIES03 puis relancez la lecture.", absent.Text);

        var missing = MessageBuilder.Build(config, PresenceStatus.FileMissing, "Larguées", "MOVIES03", "/mnt/usb/MOVIES03/x.mkv");
        Assert.Equal("Fichier introuvable", missing.Header);
        Assert.Contains("introuvable sur le support MOVIES03", missing.Text, System.StringComparison.Ordinal);
    }

    /// <summary>Reproduces jellyfin-web 12.4: translate("PlaybackError." + code) with an unknown key.</summary>
    private static string WebShows(string errorCode)
    {
        var key = "PlaybackError." + errorCode;
        var parts = key.Split('#');
        return parts.Length > 1 ? parts[1] : key;
    }

    [Fact]
    public void Web_error_code_displays_the_whole_message()
    {
        var message = new UserMessage("Support absent", "« Tom & Jerry <3 #1 » n'est pas présent sur le serveur. Insérez le support MOVIES03.");
        var code = MessageBuilder.BuildWebErrorCode(message);
        Assert.StartsWith("#", code, System.StringComparison.Ordinal);
        Assert.Equal(1, code.Count(c => c == '#'));
        var shown = WebShows(code);
        Assert.Equal("Support absent — « Tom &amp; Jerry &lt;3 ♯1 » n'est pas présent sur le serveur. Insérez le support MOVIES03.", shown);
    }
}

public class LibraryGuardTests
{
    private const string Mounted02 = "36 35 8:17 / /mnt/usb/MOVIES02 rw,noatime shared:1 - fuseblk /dev/sdb1 rw\n";
    private const string Automount00 = "40 35 0:50 / /mnt/usb/MOVIES00 rw,relatime shared:9 - autofs systemd-1 rw\n";

    private static System.Collections.Generic.IEnumerable<string> Throwing(int after, System.Exception ex)
    {
        for (var i = 0; i < after; i++)
        {
            yield return "f" + i;
        }

        throw ex;
    }

    private static System.Collections.Generic.IEnumerable<string> Files(params string[] names) => names;

    /// <summary>A guard where /mnt/usb/MOVIES02 is mounted, MOVIES00 is an automount point, and plain folders have the given state.</summary>
    private static LibraryGuard Guard(System.Func<string, bool?>? hasEntries = null, string mountInfo = Mounted02 + Automount00)
        => new(new MountTable(() => mountInfo), () => "/mnt/usb/", hasEntries ?? (_ => null));

    private static System.IO.IOException Enodev() => new("No such device : '/mnt/usb/MOVIES02/MOVIES02'", 19);

    [Theory]
    [InlineData("/srv/films", GuardScope.Outside)]
    [InlineData("/mnt/usb", GuardScope.BasePath)]
    [InlineData("/mnt/usb/", GuardScope.BasePath)]
    [InlineData("/mnt/usb/MOVIES02", GuardScope.MediaRoot)]
    [InlineData("/mnt/usb/MOVIES02/", GuardScope.MediaRoot)]
    [InlineData("/mnt/usb/MOVIES02/MOVIES02", GuardScope.InsideMedia)]
    [InlineData("/mnt/usbx/MOVIES02", GuardScope.Outside)]
    public void Classifies_paths(string path, GuardScope expected)
        => Assert.Equal(expected, Guard().Classify(path).Scope);

    [Fact]
    public void Root_states()
    {
        var guard = Guard(p => p == "/mnt/usb/MOVIES05" ? true : p == "/mnt/usb/MOVIES06" ? false : null);
        Assert.Equal(MediaRootState.Present, guard.GetRootState("/mnt/usb/MOVIES02")); // mounted
        Assert.Equal(MediaRootState.Automount, guard.GetRootState("/mnt/usb/MOVIES00")); // autofs, not mounted
        Assert.Equal(MediaRootState.Present, guard.GetRootState("/mnt/usb/MOVIES05")); // plain folder with files
        Assert.Equal(MediaRootState.Absent, guard.GetRootState("/mnt/usb/MOVIES06")); // empty mount point
        Assert.Equal(MediaRootState.Absent, guard.GetRootState("/mnt/usb/MOVIES07")); // missing
    }

    [Fact]
    public void Present_medium_is_listed_in_full()
    {
        var result = Guard().List("/mnt/usb/MOVIES02/MOVIES02", false, () => Files("a.mkv", "a.nfo"));
        Assert.Equal(["a.mkv", "a.nfo"], result);
    }

    [Fact]
    public void Long_absent_library_root_is_quietly_empty()
    {
        var guard = Guard(_ => false); // /mnt/usb/MOVIES06 is an empty mount point, a library folder
        var called = false;
        Assert.Empty(guard.List("/mnt/usb/MOVIES06", true, () => { called = true; return Files("x"); }));
        Assert.Empty(guard.List("/mnt/usb/MOVIES06", false, () => { called = true; return Files("x"); }));
        Assert.False(called); // the drive is not touched
    }

    [Fact]
    public void Root_that_is_not_a_library_folder_throws_when_absent()
    {
        // library on /mnt/usb itself: MOVIES06 is a sub-folder, an empty answer would remove its movies
        var guard = new LibraryGuard(new MountTable(() => string.Empty), () => "/mnt/usb", _ => false, _ => false);
        Assert.Empty(guard.List("/mnt/usb/MOVIES06", true, () => Files("x")));
        Assert.Throws<System.IO.IOException>(() => guard.List("/mnt/usb/MOVIES06", false, () => Files("x")));
    }

    [Fact]
    public void Library_roots_come_from_the_mblink_files()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jellyusb-test-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "Films"));
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "Films", "MOVIES02.mblink"), "/mnt/usb/MOVIES02");
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "Films", "MOVIES03.mblink"), "/mnt/usb/MOVIES03/\n");
            var roots = LibraryRoots.Read(dir);
            Assert.Contains("/mnt/usb/MOVIES02", roots);
            Assert.Contains("/mnt/usb/MOVIES03", roots);
            Assert.Empty(LibraryRoots.Read(System.IO.Path.Combine(dir, "missing")));
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Root_of_a_drive_unplugged_a_moment_ago_throws()
    {
        var plugged = true;
        var now = new System.DateTime(2026, 11, 22, 2, 0, 0, System.DateTimeKind.Utc);
        var guard = new LibraryGuard(new MountTable(() => string.Empty), () => "/mnt/usb", _ => plugged, _ => true, () => now);
        Assert.Equal(["a"], guard.List("/mnt/usb/MOVIES06", false, () => Files("a")));
        plugged = false; // unplugged during the scan
        Assert.Empty(guard.List("/mnt/usb/MOVIES06", true, () => Files("a")));
        Assert.Throws<System.IO.IOException>(() => guard.List("/mnt/usb/MOVIES06", false, () => Files("a")));
        now += LibraryGuard.RecentWindow; // a while later: quiet again
        Assert.Empty(guard.List("/mnt/usb/MOVIES06", false, () => Files("a")));
    }

    [Fact]
    public void Inside_an_absent_medium_throws_instead_of_an_empty_listing()
    {
        // Jellyfin lists a missing folder as empty and would remove its movies; an I/O error keeps them.
        foreach (var state in new bool?[] { null, false })
        {
            var guard = Guard(_ => state);
            var ex = Assert.Throws<System.IO.IOException>(() => guard.List("/mnt/usb/MOVIES06/MOVIES06", false, () => Files()));
            Assert.IsNotType<System.IO.DirectoryNotFoundException>(ex);
            Assert.Contains("MOVIES06", ex.Message);
        }
    }

    [Fact]
    public void Drive_gone_while_listing_is_never_a_truncated_listing()
    {
        var guard = Guard();
        Assert.Throws<System.IO.IOException>(() => guard.List("/mnt/usb/MOVIES02/MOVIES02", false, () => Throwing(2, Enodev())));
        Assert.Throws<System.IO.IOException>(() => guard.List("/mnt/usb/MOVIES02/MOVIES02", false, () => Throwing(0, new System.IO.IOException("Transport endpoint is not connected", 107))));
        // the root: "inaccessible or empty" for the probe, an error for a real listing
        Assert.Empty(guard.List("/mnt/usb/MOVIES02", true, () => Throwing(2, Enodev())));
        Assert.Throws<System.IO.IOException>(() => guard.List("/mnt/usb/MOVIES02", false, () => Throwing(2, Enodev())));
    }

    [Fact]
    public void Unplugged_automount_root_is_tried_then_empty()
    {
        var guard = Guard();
        var tried = false;
        var result = guard.List("/mnt/usb/MOVIES00", true, () => { tried = true; return Throwing(0, new System.IO.IOException("No such device", 19)); });
        Assert.True(tried); // touching an automount point mounts the drive when it is there
        Assert.Empty(result);
    }

    [Fact]
    public void Folder_vanishing_with_its_drive_is_not_a_deletion()
    {
        var mounted = true;
        var guard = new LibraryGuard(new MountTable(() => mounted ? Mounted02 : string.Empty), () => "/mnt/usb", _ => false);
        var ex = Assert.Throws<System.IO.IOException>(() => guard.List<string>("/mnt/usb/MOVIES02/MOVIES02", false, () =>
        {
            mounted = false; // unmounted while listing
            throw new System.IO.DirectoryNotFoundException("gone");
        }));
        Assert.IsNotType<System.IO.DirectoryNotFoundException>(ex);
    }

    [Fact]
    public void Folder_deleted_from_a_present_drive_is_still_reported()
    {
        var guard = Guard();
        Assert.Throws<System.IO.DirectoryNotFoundException>(() => guard.List<string>("/mnt/usb/MOVIES02/MOVIES02/Old film", false, () => throw new System.IO.DirectoryNotFoundException("deleted")));
    }

    [Fact]
    public void Other_errors_and_other_paths_are_left_to_jellyfin()
    {
        var guard = Guard();
        Assert.Throws<System.IO.IOException>(() => guard.List("/mnt/usb/MOVIES02/MOVIES02", false, () => Throwing(0, new System.IO.IOException("Input/output error", 5))));
        Assert.Throws<System.UnauthorizedAccessException>(() => guard.List("/mnt/usb/MOVIES02/MOVIES02", false, () => Throwing(0, new System.UnauthorizedAccessException())));
        // outside the base path: unchanged and lazy
        var lazy = guard.List("/srv/films", false, () => Throwing(1, Enodev()));
        Assert.Throws<System.IO.IOException>(() => lazy.ToList());
        Assert.False(LibraryGuard.IsDeviceGone(new System.IO.FileNotFoundException("x")));
        Assert.False(LibraryGuard.IsDeviceGone(new System.IO.DirectoryNotFoundException("x")));
    }
}

public class WordFillTests
{
    [Theory]
    [InlineData("On a retrouv� la 7�me compagnie", "On a retrouvé la 7ème compagnie !", "On a retrouvé la 7ème compagnie")]
    [InlineData("Bridget Jones - L'�ge de raison", "Bridget Jones : L'Âge de raison", "Bridget Jones - L'Âge de raison")]
    [InlineData("Dans une galaxie pr�s de chez vous, le film", "Dans une galaxie près de chez vous", "Dans une galaxie près de chez vous, le film")]
    [InlineData("L'incroyable voyage II : � San Francisco", "L'Incroyable Voyage 2 : À San Francisco", "L'incroyable voyage II : À San Francisco")]
    public void Fills_word_by_word(string broken, string candidate, string expected)
        => Assert.Equal(expected, Mojibake.FillReplacementChars(broken, ["Other title", candidate]));

    [Fact]
    public void Ambiguous_or_missing_words_are_left_alone()
    {
        Assert.Null(Mojibake.FillReplacementChars("Le c\uFFFDur", ["Le cœur battant", "Le cæur fort"]));
        Assert.Null(Mojibake.FillReplacementChars("Truth : Le Prix de la v�rit�", ["Truth"]));
        Assert.Null(Mojibake.FillReplacementChars("� bout", ["Un film — 2010"]));
    }
}

public class MediaBackgroundTests
{
    [Fact]
    public void Neighbouring_media_get_clearly_different_colours()
    {
        for (var i = 0; i < 99; i++)
        {
            var a = Web.MediaBackground.Hue($"MOVIES{i:00}");
            var b = Web.MediaBackground.Hue($"MOVIES{i + 1:00}");
            var gap = System.Math.Abs(a - b);
            Assert.True(System.Math.Min(gap, 360 - gap) >= 40, $"MOVIES{i:00} / MOVIES{i + 1:00}: {a} vs {b}");
        }
    }

    [Fact]
    public void Hue_is_stable_and_in_range()
    {
        Assert.Equal(Web.MediaBackground.Hue("MOVIES02"), Web.MediaBackground.Hue("MOVIES02"));
        Assert.InRange(Web.MediaBackground.Hue("SANS-NUMERO"), 0, 359);
    }

    [Fact]
    public void Svg_shows_the_media_id_and_escapes_it()
    {
        var svg = Web.MediaBackground.Build("MOVIES02");
        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.Contains(">MOVIES02<", svg);
        var odd = Web.MediaBackground.Build("A<b>&\"c");
        Assert.DoesNotContain("<b>", odd);
        Assert.Contains("A&lt;b&gt;&amp;", odd);
        System.Xml.Linq.XDocument.Parse(odd); // well-formed
    }
}

public class DetailsMediaIdTests
{
    private static readonly PluginConfiguration Config = new();

    [Fact]
    public void Line_tells_the_disk_and_its_state()
    {
        Assert.Equal("🟢 Disque **MOVIES03** branché", DetailsMediaIdMiddleware.BuildLine(Config, new PresenceResult(PresenceStatus.Present, "MOVIES03")));
        Assert.StartsWith("🔴 Disque **MOVIES03** absent", DetailsMediaIdMiddleware.BuildLine(Config, new PresenceResult(PresenceStatus.MediaAbsent, "MOVIES03")));
        Assert.StartsWith("🟠 Disque **MOVIES03** branché, mais", DetailsMediaIdMiddleware.BuildLine(Config, new PresenceResult(PresenceStatus.FileMissing, "MOVIES03")));
        Assert.Null(DetailsMediaIdMiddleware.BuildLine(Config, new PresenceResult(PresenceStatus.NotManaged, null)));
    }

    [Fact]
    public void MediaId_cannot_inject_markdown_or_html()
    {
        var line = DetailsMediaIdMiddleware.BuildLine(Config, new PresenceResult(PresenceStatus.Present, "A*B_<i>"));
        Assert.Contains("A\\*B\\_\\<i\\>", line);
    }

    [Fact]
    public void Overview_gets_the_line_first_and_the_rest_is_kept()
    {
        var json = "{\"Name\":\"Amélie\",\"Id\":\"17e1d5152de898cef0826c63bc6c08ad\",\"Overview\":\"Une jeune femme…\",\"RunTimeTicks\":72000000000,\"UserData\":{\"Played\":false}}";
        var result = System.Text.Json.JsonDocument.Parse(DetailsMediaIdMiddleware.AddToOverview(json, "🟢 Disque **MOVIES03** branché")!).RootElement;
        Assert.Equal("🟢 Disque **MOVIES03** branché\n\nUne jeune femme…", result.GetProperty("Overview").GetString());
        Assert.Equal("Amélie", result.GetProperty("Name").GetString());
        Assert.Equal(72000000000, result.GetProperty("RunTimeTicks").GetInt64());
        Assert.False(result.GetProperty("UserData").GetProperty("Played").GetBoolean());
    }

    [Fact]
    public void Item_without_overview_gets_the_line_alone_and_non_items_are_left()
    {
        var result = System.Text.Json.JsonDocument.Parse(DetailsMediaIdMiddleware.AddToOverview("{\"Id\":\"x\"}", "L")!).RootElement;
        Assert.Equal("L", result.GetProperty("Overview").GetString());
        Assert.Null(DetailsMediaIdMiddleware.AddToOverview("[1,2]", "L"));
        Assert.Null(DetailsMediaIdMiddleware.AddToOverview("{\"Items\":[]}", "L"));
    }
}

public class FileSystemProtectionTests
{
    [Fact]
    public void Other_server_versions_are_not_protected_but_start()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var status = PluginServiceRegistrator.TryProtectFileSystem(services, new System.Version(12, 5, 0));
        Assert.StartsWith("désactivée", status);
        Assert.Empty(services);
    }

    [Fact]
    public void Tested_version_installs_the_protected_file_system()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Assert.Equal("active", PluginServiceRegistrator.TryProtectFileSystem(services, new System.Version(12, 4, 0)));
        Assert.Contains(services, d => d.ServiceType == typeof(MediaBrowser.Model.IO.IFileSystem) && d.ImplementationType == typeof(ResilientFileSystem));
    }

    [Fact]
    public void Server_class_still_has_what_is_overridden()
        => Assert.Null(PluginServiceRegistrator.CheckManagedFileSystem());
}

public class UninstallCleanerTests
{
    [Fact]
    public void Reader_is_removed_from_both_lists_and_others_are_kept()
    {
        var options = new MediaBrowser.Model.Configuration.LibraryOptions
        {
            LocalMetadataReaderOrder = ["JellyUSB NFO", "Nfo"],
            DisabledLocalMetadataReaders = ["JellyUSB NFO"]
        };
        Assert.True(UninstallCleaner.RemoveReader(options, "JellyUSB NFO"));
        Assert.Equal(["Nfo"], options.LocalMetadataReaderOrder);
        Assert.Empty(options.DisabledLocalMetadataReaders);
        Assert.False(UninstallCleaner.RemoveReader(options, "JellyUSB NFO"));
    }

    [Fact]
    public void Only_the_jellyusb_repository_is_removed()
    {
        var repos = new[]
        {
            new MediaBrowser.Model.Updates.RepositoryInfo { Name = "Jellyfin Stable", Url = "https://repo.jellyfin.org/files/plugin/manifest.json" },
            new MediaBrowser.Model.Updates.RepositoryInfo { Name = "JellyUSB", Url = "https://raw.githubusercontent.com/webbackuper/jellyusb/main/manifest.json" }
        };
        var kept = UninstallCleaner.WithoutJellyUsb(repos);
        Assert.Single(kept);
        Assert.Equal("Jellyfin Stable", kept[0].Name);
    }
}


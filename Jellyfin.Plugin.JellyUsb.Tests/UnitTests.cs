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

public class ResilientFileSystemTests
{
    private static System.Collections.Generic.IEnumerable<string> Throwing(int after, System.Exception ex)
    {
        for (var i = 0; i < after; i++)
        {
            yield return "f" + i;
        }

        throw ex;
    }

    [Fact]
    public void Device_gone_while_enumerating_ends_the_listing()
    {
        var gone = 0;
        var enodev = new System.IO.IOException("No such device : '/mnt/usb/MOVIES01'", 19);
        var result = ResilientFileSystem.Guard(() => Throwing(0, enodev), _ => gone++).ToList();
        Assert.Empty(result);
        Assert.Equal(1, gone);

        var partial = ResilientFileSystem.Guard(() => Throwing(2, new System.IO.IOException("Transport endpoint is not connected", 107)), _ => gone++).ToList();
        Assert.Equal(["f0", "f1"], partial);
    }

    [Fact]
    public void Device_gone_when_starting_is_empty()
    {
        var result = ResilientFileSystem.Guard<string>(() => throw new System.IO.IOException("No such device", 19), _ => { }).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void Other_errors_still_surface()
    {
        Assert.Throws<System.IO.IOException>(() => ResilientFileSystem.Guard(() => Throwing(0, new System.IO.IOException("Input/output error", 5)), _ => { }).ToList());
        Assert.Throws<System.UnauthorizedAccessException>(() => ResilientFileSystem.Guard(() => Throwing(0, new System.UnauthorizedAccessException()), _ => { }).ToList());
        Assert.False(ResilientFileSystem.IsDeviceGone(new System.IO.FileNotFoundException("x")));
    }
}

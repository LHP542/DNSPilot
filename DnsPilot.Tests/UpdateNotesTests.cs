using FluentAssertions;
using DnsPilot.Services;
using DnsPilot.ViewModels;

namespace DnsPilot.Tests;

/// <summary>Versionshinweise im Update-Ordner und im eingebauten Changelog.</summary>
public class UpdateNotesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-Notes-" + Guid.NewGuid().ToString("N"));

    public UpdateNotesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Liegengebliebener Temp-Ordner ist kein Testfehler.
        }
        GC.SuppressFinalize(this);
    }

    private void Put(string name, string content = "x") => File.WriteAllText(Path.Combine(_folder, name), content);

    [Fact]
    public void Hinweise_aller_uebersprungenen_Versionen_neueste_zuerst()
    {
        Put("DnsPilot-0.2.0-notes.md", "## 0.2.0\n- zwei");
        Put("DnsPilot-0.3.0-notes.md", "## 0.3.0\n- drei");
        Put("DnsPilot-0.1.0-notes.md", "## 0.1.0\n- alt");
        Put("DnsPilot-0.4.0-notes.md", "## 0.4.0\n- noch nicht im Ordner als Paket");

        string notes = UpdateChannel.CollectReleaseNotes(_folder, new Version(0, 1, 0), new Version(0, 3, 0))!;

        notes.Should().Contain("drei").And.Contain("zwei").And.NotContain("alt").And.NotContain("noch nicht");
        notes.IndexOf("drei", StringComparison.Ordinal).Should().BeLessThan(notes.IndexOf("zwei", StringComparison.Ordinal));
    }

    [Fact]
    public void Ohne_Notes_Datei_keine_Hinweise() =>
        UpdateChannel.CollectReleaseNotes(_folder, new Version(0, 1, 0), new Version(0, 2, 0)).Should().BeNull();

    [Fact]
    public void Pruefung_liefert_Paket_und_Hinweise()
    {
        Put("DnsPilot-0.3.0-win-x64.zip");
        Put("DnsPilot-0.3.0-notes.md", "## 0.3.0\n- neu");

        var result = UpdateService.CheckFolder(_folder, new Version(0, 2, 0));

        result.UpdateAvailable.Should().BeTrue();
        result.CanInstall.Should().BeTrue();
        result.ReleaseNotes.Should().Contain("neu");
    }

    [Fact]
    public void Aktuelle_Version_bekommt_keine_Hinweise()
    {
        Put("DnsPilot-0.3.0-win-x64.zip");
        Put("DnsPilot-0.3.0-notes.md", "## 0.3.0\n- neu");

        var result = UpdateService.CheckFolder(_folder, new Version(0, 3, 0));

        result.UpdateAvailable.Should().BeFalse();
        result.ReleaseNotes.Should().BeNull();
    }

    private const string Sample = """
        # Versionshinweise

        Vorspann, der nicht in den Hinweisen auftauchen soll.

        ## 0.3.0 — 2026-11-01

        - drei

        ## 0.2.0 — 2026-10-20

        - zwei

        ## 0.1.0 — 2026-10-13

        - eins
        """;

    [Fact]
    public void Changelog_zerfaellt_in_Abschnitte_ohne_Vorspann()
    {
        var sections = Changelog.Sections(Sample);

        sections.Select(s => s.Version.ToString()).Should().Equal("0.3.0", "0.2.0", "0.1.0");
        Changelog.All(Sample).Should().NotContain("Vorspann");
    }

    [Fact]
    public void Between_liefert_nur_was_seit_der_letzten_Version_neu_ist()
    {
        string notes = Changelog.Between(Sample, new Version(0, 1, 0), new Version(0, 3, 0));

        notes.Should().Contain("drei").And.Contain("zwei").And.NotContain("eins");
    }

    [Fact]
    public void Der_eingebaute_Changelog_ist_da_und_jeder_Abschnitt_hat_Inhalt()
    {
        // Ohne die Ressource zeigte „Was ist neu" ein leeres Fenster — und fiele
        // erst beim ersten Nutzer auf.
        string embedded = Changelog.LoadEmbedded();

        var sections = Changelog.Sections(embedded);
        sections.Should().NotBeEmpty();
        sections.Should().OnlyContain(s => s.Text.Split('\n').Length > 1, "jede Version braucht mindestens eine Zeile Inhalt");
        sections.Select(s => s.Version).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Der_eingebaute_Changelog_laesst_sich_darstellen()
    {
        var blocks = ReleaseNotesFormatter.Parse(Changelog.All(Changelog.LoadEmbedded()));

        blocks.Should().Contain(b => b.Kind == NoteBlockKind.Subheading);
        blocks.Should().Contain(b => b.Kind == NoteBlockKind.Bullet);
        // Nach dem Darstellen darf kein Sternchen übrig sein — sonst ist eine
        // Fettung im Changelog unpaarig und stünde roh im Dialog.
        var rendered = blocks.Where(b => b.Kind != NoteBlockKind.Code)
            .SelectMany(b => ReleaseNotesFormatter.Inline(b.Text))
            .Select(p => p.Text);
        rendered.Should().NotContain(t => t.Contains("**", StringComparison.Ordinal));
    }
}

/// <summary>Update-Hinweis, Überspringen, Hintergrundprüfung und „Was ist neu" im ViewModel.</summary>
public class UpdateFlowTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DnsPilot-UpdFlow-" + Guid.NewGuid().ToString("N"));
    private readonly string _channel;
    private readonly SettingsService _settings;
    private readonly RecordingShell _shell = new();
    private readonly MainWindowViewModel _vm;

    public UpdateFlowTests()
    {
        _channel = Path.Combine(_folder, "kanal");
        Directory.CreateDirectory(_channel);
        _settings = new SettingsService(Path.Combine(_folder, "settings.json"));
        _settings.Current.UpdateChannel = _channel;

        var server = new DemoDnsServer();
        var sightings = new SightingStore(Path.Combine(_folder, "sightings.json"));
        _vm = new MainWindowViewModel(server, _settings, new UpdateService(_settings),
            new ScanService(new DemoHostProbe(server), sightings, _settings), sightings,
            new DeletionBackup(Path.Combine(_folder, "geloescht")));
        _vm.AttachShell(_shell);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Liegengebliebener Temp-Ordner ist kein Testfehler.
        }
        GC.SuppressFinalize(this);
    }

    private void PublishVersion(string version)
    {
        File.WriteAllText(Path.Combine(_channel, $"DnsPilot-{version}-win-x64.zip"), "x");
        File.WriteAllText(Path.Combine(_channel, $"DnsPilot-{version}-notes.md"), $"## {version}\n- **neu** in {version}");
    }

    [Fact]
    public async Task Beim_Start_oeffnet_ein_Fund_den_Dialog()
    {
        PublishVersion("99.0.0");

        await _vm.CheckForUpdateAsync(atStartup: true);

        _vm.UpdateNotice.Should().Contain("99.0.0");
        _shell.UpdateDialogs.Should().ContainSingle().Which.ReleaseNotes.Should().Contain("neu");
    }

    [Fact]
    public async Task Im_laufenden_Betrieb_nur_der_Hinweis_kein_Dialog()
    {
        PublishVersion("99.0.0");

        await _vm.CheckForUpdateAsync(atStartup: false);

        _vm.UpdateNotice.Should().Contain("99.0.0");
        _vm.ShowUpdateCommand.CanExecute(null).Should().BeTrue();
        _shell.UpdateDialogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Uebersprungene_Version_meldet_sich_nicht_mehr_die_naechste_schon()
    {
        PublishVersion("99.0.0");
        _shell.Decision = Services.UpdateDialogResult.Skip;

        await _vm.CheckForUpdateAsync(atStartup: true);

        _vm.UpdateNotice.Should().BeNull();
        _settings.Current.SkippedUpdateVersion.Should().Be("99.0.0");

        await _vm.CheckForUpdateAsync(atStartup: true);
        _shell.UpdateDialogs.Should().HaveCount(1, "die übersprungene Version fragt nicht noch einmal");

        PublishVersion("99.1.0");
        await _vm.CheckForUpdateAsync(atStartup: false);
        _vm.UpdateNotice.Should().Contain("99.1.0");
    }

    [Fact]
    public async Task Hintergrundpruefung_findet_ein_spaeter_abgelegtes_Update()
    {
        _vm.UpdateCheckInterval = TimeSpan.FromMilliseconds(40);
        _vm.StartUpdateLoop();

        PublishVersion("99.0.0");
        for (int i = 0; i < 100 && _vm.UpdateNotice is null; i++) await Task.Delay(20, TestContext.Current.CancellationToken);

        _vm.UpdateNotice.Should().Contain("99.0.0");
        _shell.UpdateDialogs.Should().BeEmpty("die Hintergrundprüfung unterbricht nicht mit einem Dialog");
        _vm.PersistOnExit();
    }

    [Fact]
    public async Task Was_ist_neu_erscheint_nach_einem_Update_einmal_und_nie_beim_ersten_Start()
    {
        _settings.Current.LastSeenVersion = null;
        await _vm.ShowWhatsNewIfUpdatedAsync();
        _shell.WhatsNew.Should().BeEmpty("beim allerersten Start gibt es kein „seit“");

        _settings.Current.LastSeenVersion = "0.0.1";
        await _vm.ShowWhatsNewIfUpdatedAsync();
        _shell.WhatsNew.Should().ContainSingle().Which.Should().Contain("##");

        await _vm.ShowWhatsNewIfUpdatedAsync();
        _shell.WhatsNew.Should().HaveCount(1, "die gesehene Version ist jetzt gespeichert");
    }

    private sealed class RecordingShell : IShellServices
    {
        public List<UpdateCheckResult> UpdateDialogs { get; } = [];
        public List<string> WhatsNew { get; } = [];
        public Services.UpdateDialogResult Decision { get; set; } = Services.UpdateDialogResult.Later;

        public Task<Services.UpdateDialogResult> ShowUpdateDialogAsync(UpdateCheckResult check)
        {
            UpdateDialogs.Add(check);
            return Task.FromResult(Decision);
        }

        public Task ShowWhatsNewAsync(string headline, string subText, string markdown)
        {
            WhatsNew.Add(markdown);
            return Task.CompletedTask;
        }

        public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
        public Task<string?> SaveTextFileAsync(string suggestedFileName, string content) => Task.FromResult<string?>(null);
        public Task OpenExternalAsync(string target) => Task.CompletedTask;
        public Task ShowNewHostDialogAsync(NewHostWindowViewModel viewModel) => Task.CompletedTask;
        public Task ShowDeleteDialogAsync(DeleteWindowViewModel viewModel) => Task.CompletedTask;
        public Task ShowRetargetCnameDialogAsync(RetargetCnameWindowViewModel viewModel) => Task.CompletedTask;
    }
}

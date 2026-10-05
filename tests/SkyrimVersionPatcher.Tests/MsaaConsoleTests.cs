using System.Reflection;
using SkyrimVersionPatcher.App;
using SkyrimVersionPatcher.Core.Downloading;

public static class MsaaConsoleTests
{
    public static void Register(TestSuite suite)
    {
        var classes = new SteamConsoleCssClasses("exact-console", "exact-form", "exact-input");
        suite.Add("Steam MSAA: ignored DOM containers require the exact console document and input class", () =>
        {
            var input = new SteamMsaaConsole.InputProof("extra exact-input", "input", 42, 0x100000,
                "https://steamloopback.host/routes/console", false, false);
            Assert.True(SteamMsaaConsole.MatchesInput(input, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { DocumentUrl = "https://steamloopback.host/routes/login" }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { DocumentUrl = "https://steamloopback.host.evil.example/routes/console" }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { DocumentUrl = "https://user@steamloopback.host/routes/console" }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { Classes = "prefix-exact-input" }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { Tag = "textarea" }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { Role = 43 }, classes));
        });
        suite.Add("Steam MSAA: current Chromium input is recognized when the Steam shell omits DOM ancestors", () =>
        {
            // Actual Chromium 126 evidence: focused, focusable text input beneath index.html,
            // with the form present but the Console container omitted from the accessibility tree.
            var input = new SteamMsaaConsole.InputProof("extra exact-input", "input", 42, 0x00100004,
                "https://steamloopback.host/index.html", false, true);
            Assert.True(SteamMsaaConsole.MatchesInput(input, classes));
            Assert.True(SteamMsaaConsole.MatchesInput(input with { FormAncestor = false }, classes));
            Assert.True(SteamMsaaConsole.MatchesInput(input with
            {
                DocumentUrl = "https://steamloopback.host/index.html?IN_CLIENT=1#console"
            }, classes));
            Assert.True(!SteamConsoleIdentity.IsConsoleRoute(input.DocumentUrl));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { DocumentUrl = null }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { DocumentUrl = "https://steamloopback.host/routes/login" }, classes));
        });
        suite.Add("Steam MSAA: the Steam shell never authorizes an unrelated or unsafe edit", () =>
        {
            var input = new SteamMsaaConsole.InputProof("exact-input", "input", 42, 0x00100004,
                "https://steamloopback.host/index.html", false, false);
            foreach (var wrongClass in new[] { "", "search-input", "exact-input-extra", "prefix-exact-input", "EXACT-INPUT" })
                Assert.True(!SteamMsaaConsole.MatchesInput(input with { Classes = wrongClass }, classes));
            foreach (var wrongTag in new[] { "", "textarea", "div" })
                Assert.True(!SteamMsaaConsole.MatchesInput(input with { Tag = wrongTag }, classes));
            foreach (var wrongRole in new[] { 15, 20, 41, 43 })
                Assert.True(!SteamMsaaConsole.MatchesInput(input with { Role = wrongRole }, classes));
            foreach (var forbidden in new[] { 0x1, 0x40, 0x8000, 0x10000, 0x200000 })
                Assert.True(!SteamMsaaConsole.MatchesInput(input with { State = input.State | forbidden }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { State = 0x4 }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { Classes = "search-input", FormAncestor = true }, classes));
        });
        suite.Add("Steam MSAA: shell fallback rejects foreign origins credentials ports and unrelated routes", () =>
        {
            var input = new SteamMsaaConsole.InputProof("exact-input", "input", 42, 0x00100004,
                "https://steamloopback.host/index.html", false, false);
            foreach (var url in new[] { "https://steamloopback.host.evil/index.html", "https://evil.example/index.html",
                         "http://steamloopback.host/index.html", "https://user@steamloopback.host/index.html",
                         "https://steamloopback.host:8080/index.html", "https://steamloopback.host/routes/login",
                         "https://steamloopback.host/index.html/other", "https://steamloopback.host/other/index.html" })
                Assert.True(!SteamMsaaConsole.MatchesInput(input with { DocumentUrl = url }, classes));
        });
        suite.Add("Steam MSAA: disabled, password, readonly, invisible and unfocusable inputs never match", () =>
        {
            var input = new SteamMsaaConsole.InputProof("exact-input", "input", 42, 0x100000, null, true, true);
            Assert.True(SteamMsaaConsole.MatchesInput(input, classes));
            foreach (var forbidden in new[] { 0x1, 0x40, 0x8000, 0x10000, 0x200000 })
                Assert.True(!SteamMsaaConsole.MatchesInput(input with { State = 0x100000 | forbidden }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { State = 0 }, classes));
            Assert.True(!SteamMsaaConsole.MatchesInput(input with { ConsoleAncestor = false, FormAncestor = true }, classes));
        });
        suite.Add("Steam MSAA: IA2 attributes preserve escaped separators and reject ambiguity", () =>
        {
            var attributes = SteamMsaaConsole.ParseAttributes(@"tag:input;class:exact-input;description:a\;b\:c\\d;");
            Assert.Equal("input", attributes["tag"]);
            Assert.Equal("exact-input", attributes["class"]);
            Assert.Equal(@"a;b:c\d", attributes["description"]);
            foreach (var malformed in new[] { "tag:input;tag:textarea;", "class;", "class:abc\\", new string('x', 65537) })
                Assert.Throws<InvalidDataException>(() => SteamMsaaConsole.ParseAttributes(malformed));
        });
        suite.Add("Steam MSAA: unrelated console UI text never enters download history", () =>
        {
            var history = SteamMsaaConsole.FilterHistory(["Account menu\nSearch", "Downloading depot 489833 (1 MB)",
                "Depot download complete : \"D:\\Steam\\steamapps\\content\\app_489830\\depot_489833\" (1 files, manifest 10)", "Friends"]);
            Assert.True(history.Contains("Downloading depot 489833"));
            Assert.True(history.Contains("manifest 10"));
            Assert.True(!history.Contains("Account") && !history.Contains("Search") && !history.Contains("Friends"));
        });
        suite.Add("Steam MSAA: unknown console classes do not inspect native windows", () =>
        {
            var session = new SteamClientSession("unused", "unused", 1, 1);
            Assert.True(!SteamMsaaConsole.HasConsole(session, [], CancellationToken.None));
            Assert.Equal<string?>(null, SteamMsaaConsole.ReadConsole(session, [], CancellationToken.None));
            Assert.True(!SteamMsaaConsole.TryDispatch(session, [], "download_depot 489830 489833 10", CancellationToken.None));
            Assert.Throws<ArgumentException>(() => SteamMsaaConsole.TryDispatch(session, [], "quit", CancellationToken.None));
        });
        suite.Add("Steam MSAA: access denial propagates without treating the field as absent", () =>
        {
            var error = Assert.Throws<SteamConsoleUnavailableException>(() => SteamMsaaConsole.ThrowIfDenied(unchecked((int)0x80070005)));
            Assert.True(error.Message.Contains("одинаковыми правами"));
            SteamMsaaConsole.ThrowIfDenied(unchecked((int)0x80004002)); // E_NOINTERFACE is an unavailable provider, not denied access.
        });
        suite.Add("Steam MSAA: IA2 COM declaration retains the complete canonical inherited prefix", () =>
        {
            var interop = typeof(SteamMsaaConsole).GetNestedType("INativeAccessible2", BindingFlags.NonPublic)!;
            var methods = interop.GetMethods().OrderBy(method => method.MetadataToken).ToArray();
            Assert.Equal(43, methods.Length); // 4 IDispatch + 21 IAccessible + 18 IAccessible2, besides implicit IUnknown.
            Assert.Equal("GetTypeInfoCount", methods[0].Name);
            Assert.Equal("GetParent", methods[4].Name);
            Assert.Equal("PutValue", methods[24].Name);
            Assert.Equal("GetRelationCount", methods[25].Name);
            Assert.Equal("GetUniqueId", methods[38].Name);
            Assert.Equal("GetWindowHandle", methods[39].Name);
            Assert.Equal("GetAttributes", methods[42].Name);
            Assert.True(methods.All(method => (method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0));
        });
    }
}

using SkyrimVersionPatcher.Core.Catalog;

namespace SkyrimVersionPatcher.Tests;

public static class CatalogTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Catalog: built-in targets preserve supplied manifests", () =>
        {
            var all = VersionCatalogService.LoadBuiltIn();
            Assert.Equal(2, all.Count);
            Assert.True(all.Select(v => v.Id).Order(StringComparer.Ordinal)
                .SequenceEqual(new[] { "1.5.97", "1.6.1170" }));
            Check(all, "1.6.1170", "8442952117333549665", "8042843504692938467", "1914580699073641964", "3873588632592923754");
            Check(all, "1.5.97", "7848722008564294070", "8702665189575304780", "2289561010626853674", "6206633033379583541");
        });

        suite.Add("Catalog: Russian checkbox selects depot and guarantees last overlay", () =>
        {
            var version = Sample();
            version = version with { Depots = version.Depots.Reverse().ToArray() };
            var selected = VersionCatalogService.GetDepots(version, true);
            Assert.Equal(4, selected.Count);
            Assert.Equal(489838U, selected[^1].DepotId);
            var withoutRussian = VersionCatalogService.GetDepots(version, false);
            Assert.Equal(3, withoutRussian.Count);
            Assert.True(withoutRussian.All(d => d.DepotId != 489838));
            Assert.Equal(489831U, withoutRussian[0].DepotId);
        });

        suite.Add("Catalog: missing requested Russian manifest is an error", () =>
        {
            var version = Sample();
            version = version with { Depots = version.Depots.Where(d => !d.IsRussian).ToArray() };
            Assert.Equal(3, VersionCatalogService.GetDepots(version, false).Count);
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.GetDepots(version, true));
        });

        suite.Add("Catalog: large manifest strings survive import and export exactly", () =>
        {
            var version = Sample();
            version = version with { Depots = version.Depots.Select(d => d with { ManifestId = ulong.MaxValue.ToString() }).ToArray() };
            var json = VersionCatalogService.Serialize([version]);
            var restored = VersionCatalogService.Parse(json).Single();
            Assert.True(json.Contains("\"18446744073709551615\"", StringComparison.Ordinal));
            Assert.True(restored.Depots.All(d => d.ManifestId == "18446744073709551615"));
        });

        suite.Add("Catalog: rejects manifest overflow, injection, zero and noncanonical values", () =>
        {
            foreach (var invalid in new[] { "18446744073709551616", "1 -app 10", "1\nquit", "0", "01", "-1", "+1", "1.0", "١" })
            {
                var version = Sample();
                version = version with { Depots = version.Depots.Select(d => d with { ManifestId = invalid }).ToArray() };
                Assert.Throws<InvalidDataException>(() => VersionCatalogService.Validate(version));
            }
        });

        suite.Add("Catalog: rejects wrong app, schema and unknown members", () =>
        {
            var json = VersionCatalogService.Serialize([Sample()]);
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Parse(json.Replace("\"appId\": 489830", "\"appId\": 10")));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Parse(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2")));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Parse(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"run\": \"anything\"")));
        });

        suite.Add("Catalog: rejects numeric manifests to prevent lost precision", () =>
        {
            var json = VersionCatalogService.Serialize([Sample()]);
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Parse(json.Replace("\"8442952117333549665\"", "8442952117333549665")));
        });

        suite.Add("Catalog: rejects duplicate root and nested JSON fields", () =>
        {
            var json = VersionCatalogService.Serialize([Sample()]);
            foreach (var duplicate in new[]
            {
                json.Replace("\"appId\": 489830", "\"appId\": 1, \"appId\": 489830"),
                json.Replace("\"id\": \"1.6.1170\"", "\"id\": \"1.6.640\", \"id\": \"1.6.1170\""),
                json.Replace("\"manifestId\": \"8442952117333549665\"", "\"manifestId\": \"1\", \"manifestId\": \"8442952117333549665\"")
            })
                Assert.Throws<InvalidDataException>(() => VersionCatalogService.Parse(duplicate));
        });

        suite.Add("Catalog: rejects missing, repeated and foreign depots", () =>
        {
            var version = Sample();
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Validate(version with { Depots = version.Depots.Skip(1).ToArray() }));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Validate(version with { Depots = [version.Depots[0], version.Depots[0], version.Depots[2]] }));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Validate(version with { Depots = version.Depots.Select(d => d with { DepotId = 10 }).ToArray() }));
        });

        suite.Add("Catalog: rejects incorrect Russian flags and duplicate versions", () =>
        {
            var version = Sample();
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Validate(version with { Depots = version.Depots.Select(d => d with { IsRussian = false }).ToArray() }));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Serialize([version, version]));
        });

        suite.Add("Catalog: version IDs cannot contain paths or command syntax", () =>
        {
            foreach (var invalid in new[] { "../1.6.1170", "1.6.1170/other", "1.6.1170 -app 10", "1.6", "01.6.1170" })
                Assert.Throws<InvalidDataException>(() => VersionCatalogService.Validate(Sample() with { Id = invalid }));
        });

        suite.Add("Catalog: null values fail with validation errors", () =>
        {
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Parse("null"));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Parse("{\"schemaVersion\":1,\"appId\":489830,\"versions\":[null]}"));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Validate(Sample() with { Depots = null! }));
        });

        suite.Add("Catalog: custom file merges and invalid save preserves previous file", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SkyrimCatalogTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "custom.json");
            try
            {
                var custom = Sample() with { DisplayName = "Custom target" };
                VersionCatalogService.Save(path, [custom]);
                var before = File.ReadAllText(path);
                var merged = VersionCatalogService.LoadWithCustom(path);
                Assert.Equal(2, merged.Count);
                Assert.Equal("Custom target", merged.Single(v => v.Id == custom.Id).DisplayName);
                Assert.True(merged.Any(v => v.Id == "1.5.97"));
                Assert.Throws<InvalidDataException>(() => VersionCatalogService.Save(path, [custom with { Id = "invalid" }]));
                Assert.Equal(before, File.ReadAllText(path));
                Assert.Equal(1, Directory.GetFiles(folder).Length);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
        });

        suite.Add("Catalog: saves only actual overrides including notes and source links", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SkyrimCatalogTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "custom.json");
            try
            {
                var builtIns = VersionCatalogService.LoadBuiltIn();
                var changed = Sample() with { Notes = "Моё примечание\nВторая строка.", SourceUrl = "https://example.com/skyrim/build" };
                var custom = builtIns.Single(v => v.Id == "1.5.97") with { DisplayName = "Custom target", Notes = "Custom notes" };
                var merged = new[] { changed, custom };
                VersionCatalogService.SaveOverrides(path, merged);

                var saved = VersionCatalogService.Load(path);
                Assert.Equal(2, saved.Count);
                var savedChanged = saved.Single(v => v.Id == changed.Id);
                Assert.Equal(changed.Notes, savedChanged.Notes);
                Assert.Equal(changed.SourceUrl, savedChanged.SourceUrl);
                Assert.True(savedChanged.Depots.SequenceEqual(changed.Depots));
                Assert.Equal(2, VersionCatalogService.LoadWithCustom(path).Count);
                Assert.Equal("Custom notes", saved.Single(v => v.Id == custom.Id).Notes);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        });

        suite.Add("Catalog: reset removes override and empty overrides keep all built-ins", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SkyrimCatalogTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "custom.json");
            try
            {
                var builtIns = VersionCatalogService.LoadBuiltIn();
                var changed = Sample() with { DisplayName = "Changed target" };
                VersionCatalogService.SaveOverrides(path, builtIns.Where(v => v.Id != changed.Id).Append(changed));
                Assert.Equal("Changed target", VersionCatalogService.LoadWithCustom(path).Single(v => v.Id == changed.Id).DisplayName);
                VersionCatalogService.SaveOverrides(path, builtIns.Select(v => v with { Depots = v.Depots.Reverse().ToArray() }));
                Assert.Equal(0, VersionCatalogService.Load(path).Count);
                Assert.Equal(builtIns.Count, VersionCatalogService.LoadWithCustom(path).Count);
                Assert.Equal(Sample().DisplayName, VersionCatalogService.LoadWithCustom(path).Single(v => v.Id == changed.Id).DisplayName);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        });

        suite.Add("Catalog: legacy merged catalog migrates without losing custom metadata", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SkyrimCatalogTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "custom.json");
            try
            {
                var changed = Sample() with { Notes = "Сохранить при миграции", SourceUrl = "https://example.com/source" };
                Directory.CreateDirectory(folder);
                var oldMerged = VersionCatalogService.LoadBuiltIn().Where(v => v.Id != changed.Id).Append(changed)
                    .Append(Sample() with { Id = "1.6.640", DisplayName = "Removed legacy target" });
                File.WriteAllText(path, LegacyJson(oldMerged));
                var diagnostics = new List<string>();
                var loaded = VersionCatalogService.LoadWithCustom(path, diagnostics.Add);
                Assert.Equal(2, loaded.Count);
                Assert.True(loaded.All(v => VersionCatalogService.IsSupportedVersion(v.Id)));
                Assert.True(diagnostics.Single().Contains("1.6.640", StringComparison.Ordinal));
                VersionCatalogService.SaveOverrides(path, loaded);

                Assert.Equal(1, VersionCatalogService.Load(path).Count);
                var restored = VersionCatalogService.LoadWithCustom(path).Single(v => v.Id == changed.Id);
                Assert.Equal(changed.Notes, restored.Notes);
                Assert.Equal(changed.SourceUrl, restored.SourceUrl);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        });

        suite.Add("Catalog: invalid override save leaves previous custom data intact", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "SkyrimCatalogTests-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(folder, "custom.json");
            try
            {
                var changed = Sample() with { Notes = "Existing custom notes" };
                VersionCatalogService.SaveOverrides(path, [changed]);
                var before = File.ReadAllText(path);
                Assert.Throws<InvalidDataException>(() => VersionCatalogService.SaveOverrides(path, [changed with { SourceUrl = "file:///tmp/source" }]));
                Assert.Throws<InvalidDataException>(() => VersionCatalogService.SaveOverrides(path, [changed, changed]));
                Assert.Throws<InvalidDataException>(() => VersionCatalogService.Save(path, [changed with { Id = "1.6.640" }]));
                Assert.Throws<InvalidDataException>(() => VersionCatalogService.SaveOverrides(path, [changed with { Id = "1.6.640" }]));
                Assert.Equal(before, File.ReadAllText(path));
                Assert.Equal(1, Directory.GetFiles(folder).Length);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        });

        suite.Add("Catalog: removed targets cannot be imported, saved, normalized or downloaded", () =>
        {
            var removed = Sample() with { Id = "1.6.640", DisplayName = "Removed target" };
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Validate(removed));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Parse(LegacyJson([removed])));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.Serialize([removed]));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.NormalizeOverrides([removed]));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.GetDepots(removed, false));
            Assert.Throws<InvalidDataException>(() => VersionCatalogService.GetDepots(removed, true));
        });

        suite.Add("Catalog: partial or empty remote base retains both supported targets", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "SkyrimCatalogTests-" + Guid.NewGuid().ToString("N"), "absent.json");
            var update = Sample() with { Notes = "Remote update" };
            var versions = VersionCatalogService.LoadWithCustom([update], path);
            Assert.Equal(2, versions.Count);
            Assert.Equal("Remote update", versions.Single(v => v.Id == update.Id).Notes);
            Assert.Equal(2, VersionCatalogService.LoadWithCustom(Array.Empty<GameVersionDefinition>(), path).Count);
        });
    }

    private static string LegacyJson(IEnumerable<GameVersionDefinition> versions) =>
        System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, appId = VersionCatalogService.AppId, versions },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

    private static GameVersionDefinition Sample() => VersionCatalogService.LoadBuiltIn().Single(v => v.Id == "1.6.1170");

    private static void Check(IReadOnlyList<GameVersionDefinition> all, string versionId, params string[] manifests)
    {
        var depots = VersionCatalogService.GetDepots(all.Single(v => v.Id == versionId), true);
        for (var i = 0; i < manifests.Length; ++i)
            Assert.Equal(manifests[i], depots[i].ManifestId);
    }
}

using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using osu.Game.Beatmaps;
using osu.Game.Models;
using osu.Game.Rulesets;
using Realms;
using Waddamburo.Providers.OsuLazer;

namespace Waddamburo.Architecture.Tests;

public sealed class OsuLazerRealmReaderTests
{
    [Fact]
    public void ExistingRealmIsReadWithoutChangingItsFiles()
    {
        var directory = Directory.CreateTempSubdirectory("waddamburo-realm-");

        try
        {
            var databasePath = Path.Combine(directory.FullName, "client.realm");
            createSyntheticRealm(databasePath);
            removeRealmCoordinationFiles(databasePath);

            var before = snapshotDirectory(directory.FullName);
            var snapshot = OsuLazerRealmReader.Read(databasePath);
            var after = snapshotDirectory(directory.FullName);

            Assert.Equal(before, after);

            var set = Assert.Single(snapshot.BeatmapSets);
            Assert.False(set.DeletePending);
            Assert.Equal("set-hash", set.Hash);
            Assert.Contains(set.Files, file => file is { Filename: "song.ogg", Hash: "audio-hash" });

            var beatmap = Assert.Single(set.Beatmaps);
            Assert.Equal("taiko", beatmap.Ruleset);
            Assert.Equal("Synthetic Song", beatmap.Title);
            Assert.Equal("song.ogg", beatmap.AudioFilename);
            Assert.Equal("audio-hash", set.Files.Single(file => file.Filename == beatmap.AudioFilename).Hash);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void MissingRealmIsNotCreated()
    {
        var directory = Directory.CreateTempSubdirectory("waddamburo-realm-");

        try
        {
            var databasePath = Path.Combine(directory.FullName, "missing.realm");

            Assert.Throws<FileNotFoundException>(() => OsuLazerRealmReader.Read(databasePath));
            Assert.Empty(directory.EnumerateFileSystemInfos());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void OlderSchemaIsRejectedWithoutMigration()
    {
        var directory = Directory.CreateTempSubdirectory("waddamburo-realm-");

        try
        {
            var databasePath = Path.Combine(directory.FullName, "client.realm");
            createSyntheticRealm(databasePath, OsuLazerRealmReader.SupportedSchemaVersion - 1);
            removeRealmCoordinationFiles(databasePath);

            var before = snapshotDirectory(directory.FullName);
            Assert.Throws<OsuLazerRealmReadException>(() => OsuLazerRealmReader.Read(databasePath));
            Assert.Equal(before, snapshotDirectory(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void createSyntheticRealm(
        string databasePath,
        ulong schemaVersion = OsuLazerRealmReader.SupportedSchemaVersion)
    {
        RuntimeHelpers.RunModuleConstructor(typeof(BeatmapSetInfo).Module.ModuleHandle);

        var configuration = new RealmConfiguration(databasePath)
        {
            SchemaVersion = schemaVersion,
        };

        using var realm = Realm.GetInstance(configuration);
        realm.Write(() =>
        {
            var metadata = new BeatmapMetadata(new RealmUser { Username = "Fixture Mapper" })
            {
                Title = "Synthetic Song",
                Artist = "Fixture Artist",
                AudioFile = "song.ogg",
                BackgroundFile = "background.png",
            };
            var beatmap = new BeatmapInfo(
                new RulesetInfo { ShortName = "taiko", Name = "osu!taiko", OnlineID = 1 },
                metadata: metadata)
            {
                DifficultyName = "Fixture Oni",
                Hash = "chart-hash",
                MD5Hash = "synthetic-md5",
                Length = 123_000,
                BPM = 180,
            };
            var set = new BeatmapSetInfo([beatmap])
            {
                Hash = "set-hash",
                DateAdded = DateTimeOffset.UnixEpoch,
            };
            set.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "audio-hash" }, "song.ogg"));
            set.Files.Add(new RealmNamedFileUsage(new RealmFile { Hash = "background-hash" }, "background.png"));

            realm.Add(set);
        });
    }

    private static void removeRealmCoordinationFiles(string databasePath)
    {
        var lockPath = databasePath + ".lock";
        if (File.Exists(lockPath))
            File.Delete(lockPath);

        var managementPath = databasePath + ".management";
        if (Directory.Exists(managementPath))
            Directory.Delete(managementPath, recursive: true);

        var notePath = databasePath + ".note";
        if (File.Exists(notePath))
            File.Delete(notePath);
    }

    private static string[] snapshotDirectory(string directory) => Directory
        .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => $"{Path.GetRelativePath(directory, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .ToArray();
}

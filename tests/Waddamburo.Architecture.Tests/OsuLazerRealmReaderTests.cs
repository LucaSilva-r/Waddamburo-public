using System.Security.Cryptography;
using Realms;
using Realms.Schema;
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
            // Written elsewhere and copied: the writer's lock files and in-process coordinator stay with
            // the source, so the read meets a bare database like another process's (osu!'s) would be.
            var source = Directory.CreateTempSubdirectory("waddamburo-realm-source-");
            var databasePath = Path.Combine(directory.FullName, "client.realm");
            createSyntheticRealm(Path.Combine(source.FullName, "client.realm"));
            File.Copy(Path.Combine(source.FullName, "client.realm"), databasePath);

            var before = snapshotDirectory(directory.FullName);
            var snapshot = OsuLazerRealmReader.Read(databasePath);
            var after = snapshotDirectory(directory.FullName);

            Assert.Equal(before, after);

            var set = Assert.Single(snapshot.BeatmapSets);
            Assert.False(set.DeletePending);
            Assert.Equal(45, set.OnlineId);
            Assert.Contains(set.Files, file => file is { Filename: "song.ogg", Hash: "audio-hash" });

            var beatmap = Assert.Single(set.Beatmaps);
            Assert.Equal("taiko", beatmap.Ruleset);
            Assert.Equal(4.5, beatmap.StarRating);
            Assert.Equal(180, beatmap.Bpm);
            Assert.Equal(123, beatmap.OnlineId);
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

    /// <summary>Takes the change notifications Realm posts after a write and never delivers them.</summary>
    private sealed class DroppedNotifications : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    private static void createSyntheticRealm(string databasePath)
    {
        // Realm posts a notification after the write to the thread's SynchronizationContext; under
        // xunit's (or none) it ran on another thread and Realm aborted the process (verify_thread).
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DroppedNotifications());
        try
        {
            writeSyntheticRealm(databasePath);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    private static void writeSyntheticRealm(string databasePath)
    {
        // The slice of lazer's schema the reader uses, written through Realm's dynamic API.
        var schema = new RealmSchema.Builder
        {
            new ObjectSchema.Builder("File", ObjectSchema.ObjectType.RealmObject)
            {
                Property.Primitive("Hash", RealmValueType.String, isPrimaryKey: true),
            },
            new ObjectSchema.Builder("RealmNamedFileUsage", ObjectSchema.ObjectType.EmbeddedObject)
            {
                Property.Object("File", "File"),
                Property.Primitive("Filename", RealmValueType.String),
            },
            new ObjectSchema.Builder("RealmUser", ObjectSchema.ObjectType.EmbeddedObject)
            {
                Property.Primitive("Username", RealmValueType.String),
            },
            new ObjectSchema.Builder("BeatmapMetadata", ObjectSchema.ObjectType.RealmObject)
            {
                Property.Primitive("Title", RealmValueType.String),
                Property.Primitive("TitleUnicode", RealmValueType.String),
                Property.Primitive("Artist", RealmValueType.String),
                Property.Primitive("ArtistUnicode", RealmValueType.String),
                Property.Object("Author", "RealmUser"),
                Property.Primitive("AudioFile", RealmValueType.String),
                Property.Primitive("PreviewTime", RealmValueType.Int),
            },
            new ObjectSchema.Builder("Ruleset", ObjectSchema.ObjectType.RealmObject)
            {
                Property.Primitive("ShortName", RealmValueType.String, isPrimaryKey: true),
            },
            new ObjectSchema.Builder("Beatmap", ObjectSchema.ObjectType.RealmObject)
            {
                Property.Primitive("ID", RealmValueType.Guid, isPrimaryKey: true),
                Property.Primitive("DifficultyName", RealmValueType.String),
                Property.Object("Ruleset", "Ruleset"),
                Property.Object("Metadata", "BeatmapMetadata"),
                Property.Primitive("Hash", RealmValueType.String),
                Property.Primitive("MD5Hash", RealmValueType.String),
                Property.Primitive("StarRating", RealmValueType.Double),
                Property.Primitive("BPM", RealmValueType.Double),
                Property.Primitive("Length", RealmValueType.Double),
                Property.Primitive("Hidden", RealmValueType.Bool),
                Property.Primitive("OnlineID", RealmValueType.Int),
            },
            new ObjectSchema.Builder("BeatmapSet", ObjectSchema.ObjectType.RealmObject)
            {
                Property.Primitive("ID", RealmValueType.Guid, isPrimaryKey: true),
                Property.Primitive("DeletePending", RealmValueType.Bool),
                Property.Primitive("DateAdded", RealmValueType.Date),
                Property.Primitive("OnlineID", RealmValueType.Int),
                Property.ObjectList("Beatmaps", "Beatmap"),
                Property.ObjectList("Files", "RealmNamedFileUsage"),
            },
        }.Build();

        using var realm = Realm.GetInstance(new RealmConfiguration(databasePath) { IsDynamic = true, Schema = schema, SchemaVersion = 51 });
        realm.Write(() =>
        {
            var metadata = realm.DynamicApi.CreateObject("BeatmapMetadata");
            metadata.DynamicApi.Set("Title", "Synthetic Song");
            metadata.DynamicApi.Set("Artist", "Fixture Artist");
            metadata.DynamicApi.Set("AudioFile", "song.ogg");
            metadata.DynamicApi.Set("PreviewTime", -1);
            var beatmap = realm.DynamicApi.CreateObject("Beatmap", Guid.NewGuid());
            beatmap.DynamicApi.Set("DifficultyName", "Fixture Oni");
            beatmap.DynamicApi.Set("Ruleset", RealmValue.Object(realm.DynamicApi.CreateObject("Ruleset", "taiko")));
            beatmap.DynamicApi.Set("Metadata", RealmValue.Object(metadata));
            beatmap.DynamicApi.Set("Hash", "chart-hash");
            beatmap.DynamicApi.Set("StarRating", 4.5);
            beatmap.DynamicApi.Set("BPM", 180.0);
            beatmap.DynamicApi.Set("OnlineID", 123);
            var set = realm.DynamicApi.CreateObject("BeatmapSet", Guid.NewGuid());
            set.DynamicApi.Set("OnlineID", 45);
            set.DynamicApi.GetList<IRealmObjectBase>("Beatmaps").Add(beatmap);
            var usage = realm.DynamicApi.AddEmbeddedObjectToList(set.DynamicApi.GetList<IEmbeddedObject>("Files"));
            usage.DynamicApi.Set("Filename", "song.ogg");
            usage.DynamicApi.Set("File", RealmValue.Object(realm.DynamicApi.CreateObject("File", "audio-hash")));
        });
    }

    private static string[] snapshotDirectory(string directory) => Directory
        .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => $"{Path.GetRelativePath(directory, path)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")
        .ToArray();
}

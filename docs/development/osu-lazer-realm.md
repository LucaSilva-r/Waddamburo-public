# osu!lazer Realm access

The BLD-009 prototype uses osu!'s published managed model assembly rather than a
custom native bridge. The database models and `RealmAccess` implementation are in
`ppy.osu.Game`; `ppy.osu.Framework` is a transitive dependency and does not itself
provide the lazer database schema.

## Pinned compatibility boundary

| Component | Version | Purpose |
| --- | --- | --- |
| `ppy.osu.Game` | 2026.916.0, upstream commit `98fb49876c0242fcf649e0250d6f6b3458769a9e` | Official Realm model types |
| `ppy.osu.Framework` | 2026.914.0 | Transitive osu! framework dependency |
| `Realm` | 20.1.0 | Managed Realm API and platform-native wrapper |

`Waddamburo.Providers.OsuLazer` contains the dependency graph so the core game,
formats, Lumen host, SDL adapter, application, and tool remain independent of it.
The exact transitive graph is locked in that project's `packages.lock.json`.

The reader supports osu!'s schema version 52, paired with the pinned game package.
Updating `ppy.osu.Game` requires checking the schema version in the matching
`osu.Game/Database/RealmAccess.cs`, updating the constant if necessary, regenerating
the lock files, and rerunning the byte-preservation tests.

## Read-only contract

The reader:

- rejects a missing path before Realm can create a database;
- initializes osu!'s generated Realm models before opening the first Realm;
- sets `RealmConfiguration.IsReadOnly` and schema version 52;
- does not construct osu!'s `RealmAccess`, install a migration callback, compact,
  recover, or start a write transaction; and
- copies set, beatmap, metadata, and named-file values into Waddamburo records
  before disposing the Realm context.

Synthetic tests create an official-schema fixture, remove Realm coordination
artifacts, and compare every directory entry and SHA-256 before and after reading.
They also prove that a missing database is not created and that an older schema is
rejected without migration or filesystem changes. In the restricted development
sandbox, VSTest cannot open its localhost control socket, so the same test methods
were also executed directly through a temporary runner.

Schema incompatibility is an expected provider-level error. A future scan must
report it for the osu!lazer source without affecting other song providers.

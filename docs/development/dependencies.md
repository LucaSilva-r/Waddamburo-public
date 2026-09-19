# Dependency updates

Package versions are declared only in `Directory.Packages.props`. Every project has
a committed `packages.lock.json`, including projects whose current dependency graph
is empty. CI restores in locked mode.

To update a package deliberately:

1. Confirm the release and license from the upstream project and official package
   registry.
2. Change its central version in `Directory.Packages.props`.
3. Run `dotnet restore Waddamburo.slnx --force-evaluate`.
4. Review every lock-file change, including transitive native packages.
5. Update `THIRD_PARTY_NOTICES.md` with the exact version, source, license, link/use
   mode, and distribution obligations.
6. Run the locked verification commands:

   ```sh
   dotnet restore Waddamburo.slnx --locked-mode
   CI=true dotnet build Waddamburo.slnx --no-restore
   CI=true dotnet test Waddamburo.slnx --no-build --no-restore
   ```

Do not use floating versions or suppress a lock mismatch. Native packages require a
release-content inspection in addition to NuGet's dependency graph.

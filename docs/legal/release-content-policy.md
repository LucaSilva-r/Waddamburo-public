# Release content policy

Waddamburo uses a bring-your-own-assets model. A release contains the engine,
project-authored resources, required redistributable dependencies, licenses, and
notices. Users point it at their own compatible installation after launch.

## Allowed

- Waddamburo source and project-authored art, audio, shaders, and documentation;
- synthetic fixtures that do not derive from original content;
- separately redistributable third-party dependencies with all required license,
  copyright, notice, source-offer, and relinking material; and
- aggregate compatibility facts that cannot reconstruct supplied content.

## Forbidden

- game executables, firmware, keys, original archives, movies, models, charts,
  textures, audio, shaders, fonts, or extracted variants of them;
- generated source, decompilations, disassemblies, raw memory or graphics captures,
  traces containing original data, or original-content screenshots;
- user asset inventories, file hashes, save data, or library databases;
- credentials, tokens, private keys, machine-specific paths, build caches, editor
  state, crash dumps, or unapproved debug artifacts; and
- dependencies whose redistribution terms and exact versions are not recorded in
  `THIRD_PARTY_NOTICES.md`.

## Release procedure

1. Build source and binary packages from a clean checkout with no asset root
   available.
2. Audit both packages for forbidden file types, generated directories, private
   paths, credentials, unexpected binary resources, and missing dependency notices.
3. Verify the exact dependency inventory and authoritative license files.
4. Retain the machine-readable audit result with release records.
5. Smoke-test first-run asset selection on a clean machine.

Automated checks are a minimum gate, not proof that content is licensed. Reviewers
must inspect new binary resources, generated files, and dependency changes.

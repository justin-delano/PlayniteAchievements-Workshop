# Manifest and index schema changes

Every `manifest.json` carries `schemaVersion`; every `index/v<N>.json` carries `schemaVersion` too and is named by it.
The validator refuses a manifest that is not at the current version, so the tree is always uniform.
To change the schema: bump `ManifestSchema.Current` in `tools/Validator/Schema/ManifestSchema.cs`, add a migration under `tools/Validator/Schema/Migrations/`, add the new JSON Schema file here, run the `migrate` workflow (which opens one pull request rewriting every manifest), and keep the previous index writer in place for at least one extension release so older extensions keep working.

## v1 (2026-10-03)

Initial schema.
Kinds: `Colors`, `NotificationStyle`, `ScreenshotFrame`, `ShowcasePage`, `UnlockSounds`, `Theme`, `GameCustomData`. A theme is the only bundle; it embeds standalone `.pacolors`, `.pasounds`, `.panotif` and `.paframe` packages as parts.
Fields: `schemaVersion`, `id`, `kind`, `name`, `description`, `author` (display name), `authorGitHub` (login, issue-form submissions), `ownerHash` (submitter key hash, Playnite submissions), `maintainers`, `version`, `license`, `tags`, `minPluginVersion`, `created`, `updated`, `game` (game-data only), `contents` (computed), `package` (computed; file, formatKind, formatVersion, sizeBytes, sha256, release.tag, release.url), `preview`, `readme`.
Ownership for updates: the `authorGitHub` login or a listed maintainer, or a submitter whose key hashes to `ownerHash`.

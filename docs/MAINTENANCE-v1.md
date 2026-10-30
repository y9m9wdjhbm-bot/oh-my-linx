# Maintenance and contribution guide v1

## Published baseline

- Product: LC PlayerMarket V96 API, 96.0.1.
- Source baseline: `LC_PlayerMarket_v96_API/source-v4`.
- Repository owner: `y9m9wdjhbm-bot`.
- Initial source publication: [commit 2a430b7](https://github.com/y9m9wdjhbm-bot/oh-my-linx/commit/2a430b7781b94eb1c20b34e34b945417758eeb2e).
- Source hashes: [source-manifest-v1.json](source-manifest-v1.json).

The code contains shop search/history, display and export logic; shared Core/Reader/Broker components; patrol and recovery paths; and existing verification code. Captured test inputs and runtime packages are not included. No external usage or download statistics are currently asserted.

## Report a problem

Use the repository issue template. Include the source version, Windows/.NET version, steps to reproduce, expected behavior, actual behavior, and whether the problem is a source-inspection issue or requires local runtime dependencies.

Use a small synthetic example where possible. Remove account names, character names, machine/network details, credentials, local paths, and personal data before sharing any excerpt. Do not attach raw memory dumps, whole logs, game resources, or private certificates.

## Proposed maintenance workflow

1. Inspect the affected code and the original failure evidence locally.
2. Prepare changes in a new versioned source folder and preserve prior files.
3. Run the relevant source/contract checks. Keep source integrity, compilation, offline tests, and actual runtime effects as separate results.
4. Document dependencies and incomplete verification explicitly.
5. Publish reviewable source and release notes. Do not turn missing evidence into a passed check.

These are the proposed public contribution/release steps. They are not claims of existing external pull-request activity or community adoption. Changes that alter the application workflow require maintainer review. Submitted material must be yours to share; this repository is licensed under the [MIT License](../LICENSE).

## Planned use of Codex

Potential work includes C# review, issue triage, targeted regression analysis, documentation, dependency-impact checks, and release preparation. This is a proposed maintenance use, not a claim that an API integration is already deployed.

## Known publication limitations

See [source-publication-v1.md](source-publication-v1.md) for the eight explicit missing embedded resources, additional fixture directories, profiles, and native/navigation packages. The original project files intentionally remain unchanged. Source-integrity checks do not establish an independently buildable or game-verified release.


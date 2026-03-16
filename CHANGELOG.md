## [2.4.46] - 2026-03-16

- 99fb514 refactor config
- 9a78064 bump
- 48d9997 [2.4.39] | readme + changelog update
- d114e41 [2.4.39] | readme + changelog update
- 74f2250 updated readme
- 477222e bump
- 848c28b removed tests
- e7914c0 sync
- 6fc0cc9 [2.4.37] | readme + changelog update
- 98ed1d9 update
- 8f10f3e update
- eb32a0c [2.4.37] | readme + changelog update
- 854ec02 [2.4.37] | readme + changelog update
- 10bb8bb readme
- 72332bb sync 1.19.1
- a73666b 2.4.37
- 2569d5d SQLite dll
- f8d4a0f fix: duplicating faction forum, voice channel
- 5fb8999 Discord admin commands
- ee78235 SQLite
- a2e39bc [2.3.74] | readme + changelog update
- aec02f7 fix: duplicate factions
- ea0bfb4 fix: duplicated factions
- becb834 error, argument missing in CommandProcessor.cs(1219,52)...
- bf866d7 update
- 8099933 sync up
- 3cd3bb4 update
- 6611504 update
- c79a3c6 fix: faction channels creation on discord
- 249dcd9 update

## [2.4.39] - 2026-03-14

- 74f2250 updated readme
- 477222e bump
- 848c28b removed tests
- e7914c0 sync
- 6fc0cc9 [2.4.37] | readme + changelog update
- 98ed1d9 update
- 8f10f3e update
- eb32a0c [2.4.37] | readme + changelog update
- 10bb8bb readme
- 72332bb sync 1.19.1
- a73666b 2.4.37
- 2569d5d SQLite dll
- f8d4a0f fix: duplicating faction forum, voice channel
- 5fb8999 Discord admin commands
- ee78235 SQLite
- a2e39bc [2.3.74] | readme + changelog update
- aec02f7 fix: duplicate factions
- ea0bfb4 fix: duplicated factions
- becb834 error, argument missing in CommandProcessor.cs(1219,52)...
- bf866d7 update
- 8099933 sync up
- 3cd3bb4 update
- 6611504 update
- c79a3c6 fix: faction channels creation on discord
- 249dcd9 update
- 2aea93c test fix
- fb1d7ca test fix
- 7decd70 update local
- bc0ade8 v2.3.38 | auto sync
- 26fe9ed v2.3.34 | update

## [2.4.37] - 2026-03-14

- 10bb8bb readme
- 72332bb sync 1.19.1
- a73666b 2.4.37
- 2569d5d SQLite dll
- f8d4a0f fix: duplicating faction forum, voice channel
- 5fb8999 Discord admin commands
- ee78235 SQLite
- a2e39bc [2.3.74] | readme + changelog update
- aec02f7 fix: duplicate factions
- ea0bfb4 fix: duplicated factions
- becb834 error, argument missing in CommandProcessor.cs(1219,52)...
- bf866d7 update
- 8099933 sync up
- 3cd3bb4 update
- 6611504 update
- c79a3c6 fix: faction channels creation on discord
- 249dcd9 update
- 2aea93c test fix
- fb1d7ca test fix
- 7decd70 update local
- bc0ade8 v2.3.38 | auto sync
- 26fe9ed v2.3.34 | update
- fb2f5df v2.3.33 | update
- 4302736 v2.3.32 | update
- 96531f4 v2.3.31 | update
- a66d886 v2.3.30 | update
- 3ae2204 v2.3.29 | update
- 69c77c7 log
- ff707f9 v2.3.26 | Metadata update
- 4f13588 v2.3.26 | update

## [2.3.74] - 2026-02-20

- aec02f7 fix: duplicate factions
- ea0bfb4 fix: duplicated factions
- becb834 error, argument missing in CommandProcessor.cs(1219,52)...
- bf866d7 update
- 8099933 sync up
- 3cd3bb4 update
- 6611504 update
- c79a3c6 fix: faction channels creation on discord
- 249dcd9 update
- 2aea93c test fix
- fb1d7ca test fix
- 7decd70 update local
- bc0ade8 v2.3.38 | auto sync
- 26fe9ed v2.3.34 | update
- fb2f5df v2.3.33 | update
- 4302736 v2.3.32 | update
- 96531f4 v2.3.31 | update
- a66d886 v2.3.30 | update
- 3ae2204 v2.3.29 | update
- 69c77c7 log
- ff707f9 v2.3.26 | Metadata update
- 4f13588 v2.3.26 | update
- b221e67 v2.3.24 | Auto-changelog update
- 450749c v2.3.24 | novi push
- 2996cf1 v2.3.25 | auto sync
- 800ebe2 v2.3.25 | Auto-changelog update
- b223a11 v2.3.25 | auto sync
- d2d515e v2.3.25 | auto sync
- 6d86ec9 v2.3.19 | update
- b301154 v2.3.19 | update

# Changelog

## [2.4.2] - 2026-03-03

### Fixed
- **Forum channel duplicate fix**: `FindForumChannelByName()` dodan u `DiscordService` – forum kanal se sada
  provjerava kao i text/voice kanal prije kreiranja (reuse ako već postoji, skipanje duplikata).

### Added
- **SQLite podrška** (opt-in): Nova `SqliteDatabaseService` klasa implementira SQLite kao primarni storage.
  XML ostaje kao automatski fallback ako SQLite nije dostupan ili inicijalizacija ne uspije.
  - `config.DataStorage.UseSQLite = true` (default) — aktivira SQLite kada su DLL-ovi prisutni.
  - Detaljan `[SQLITE] DEBUG` log pri inicijalizaciji: otvaranje konekcije, kreiranje/provjera svake tablice.

### ⚠️ Instalacija SQLite DLL-ova (obavezno za SQLite storage)
Torch PluginManager učitava sve `.dll` datoteke iz plugin foldera kao managed assembly.
`SQLite.Core.dll` je mixed-mode assembly (native + managed) i ne smije biti u plugin folderu.

**SQLite DLL-ovi moraju biti u Torch server ROOT direktoriju** (npr. `C:\TorchServer\`):
```
TorchServer\
  ├── System.Data.SQLite.dll       ← managed wrapper
  └── x64\
      └── SQLite.Interop.dll       ← native SQLite
```
Preuzmite s: https://system.data.sqlite.org/index.html/doc/trunk/www/downloads.wiki  
(paket: `sqlite-netFx48-binary-bundle-x64-2013-1.0.118.0.zip`)

Ako DLL-ovi nisu pronađeni, plugin automatski koristi XML storage bez greške.

## [2.3.26] - 2026-02-15
- v2.3.26 | update
- v2.3.25 | auto sync
- v2.3.25 | Auto-changelog update

## [2.3.25] - 2026-02-15
- v2.3.25 | auto sync
- v2.3.25 | auto sync
- v2.3.19 | update
- v2.3.19 | update
- sync updated


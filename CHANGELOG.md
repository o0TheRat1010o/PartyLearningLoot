# Changelog

All notable changes to this mod are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).
The version in `PartyLearningLoot/ModInfo.xml` is the single source of truth.

## [2.0.0] - 2026-11-05

Built and checked against 7 Days to Die **V 3.4.0 (b10)**. The loot tables and perks the mod relies on are unchanged since
V 3.3.0 (b18).

### Changed
- **The party bonus no longer changes any loot weight.** Up to 1.1 the weights of the entries the party's perks favour
  were raised. Loot lists draw in proportion to the weights, so that also lowered the chance of every other entry of the
  list - including entries the opener's own perks favour (a measured two-player party found about 16 % fewer of those, and
  6 % fewer neutral items). Now the container is generated exactly as in vanilla with the opener's own perks, and the
  party's perks add **extra draws on top**, made by the game's own loot code with private random streams. No item is
  ever found less often than in vanilla, and an entry the party's perks favour is found at least as often as by one
  player holding all those perks. The rule per perk (strongest member counts, identical perks never stack) is unchanged.
- **More loot, by design.** Extra draws cannot come for free: for a party of two players roughly +7 % more finds overall with
  learning material only (the learning items themselves about +38 %) and about +15 % with `IncludeOtherLoot`. Zombie loot
  bags give 1 to 2 more items, cars and backpacks about half an item, junk piles, cupboards and barrels hardly anything.
- Lists whose entries are each rolled on their own (the skill magazines in food and medical piles, car parts ...) give a
  raised entry an extra chance of `max(0, min(party chance, 1) - min(vanilla chance, 1))`.
- `DryRun` now plans and logs the bonus but draws none (loot stays exactly vanilla).
- `pll simulate` compares every item of the paired runs (not only the learning items): it counts the runs in which any item
  is found less often with the bonus (must be 0), shows the five items with the lowest change and compares the bonus
  draws made with the number the plans expect. `item=<name>` follows any single item. `pll explain` shows the extra
  weight and, with a container name, shares, gains and the bonus of its loot lists. `pll status` shows the bonus counters.
- Audit log schema 2: spawn records carry a `bonus` object (lists, draws planned against expected, items added, skipped lists,
  at `Detailed` every plan with its weights and shares); the records of changed weights (`entries`) are gone.

### Added
- Self checks at every start: the mathematics of the bonus, and the whole mechanism through the game's own (patched) draw
  code with synthetic lists - the bonus result always contains the vanilla result, the game's random streams stay
  untouched, the number of extra draws and the finds per item match the expectation, a dry run changes nothing.
- A short live check when a party forms or changes: the bonus is observed with the real members and simulated once, and the
  verdict is written to the audit log.
- Safety: the vanilla result is verified after the bonus and restored if anything went wrong; at most 64 bonus calls per
  container; lists that cannot be planned reliably (random-roll requirements, independent entries mixed into a draw) and
  containers that draw without replacement are left alone and counted in the log.

## [1.1.0] - 2026-10-17

### Added
- **Other loot** (`IncludeOtherLoot`, off by default): the party rule also applies to every other loot tag a perk
  weights - ammo, weapon and tool classes, armor, medical supplies, vehicle and workstation parts, food, seeds and
  legendary parts (32 more tags and one more perk in V 3.3). Disabled learning categories never come back through it;
  `ExcludedLearningTags` can exclude single tags. Applies live with `pll reload`.
- `pll explain` lists other loot tags, `pll simulate` counts other perk loot, `pll status` and the selftest show the
  mode; the selftest checks the catalog against the V 3.3 expectation for the active mode.
- The audit log records the merge catalog in effect (at session start and after every `pll reload`) and marks other
  loot items, so every merged entry can be checked against it.

## [1.0.0] - 2026-10-12

First release, built and tested against 7 Days to Die **V 3.3.0 (b18)**.

### Added
- **Party-wide learning loot:** skill magazines and perk-book series in containers, zombie loot bags and airdrops are
  weighted by the learning perks of every active party member. For each perk a member only adds the part that is
  stronger than the opener's own version, so identical perks never stack and loot is never below vanilla; vanilla's
  "complete" weight rule for finished crafting skills is never multiplied into a member's bonus.
- **Shared Reading** (optional, off by default): when a party member reads a book, magazine or schematic, every active
  member receives its effect as if they had read it themselves (perk, crafting points, recipe, XP). Each member's own
  game applies the read through a hidden per-item buff delivered with the server configs, so it uses the member's own
  state (already read, finished skills, Nerd bonus) and never overwrites anything. Works without client mods; members
  get a toolbelt message; dead members learn after respawning.
- Settings in `Settings.xml` (most can be applied live with `pll reload`): per-feature switches, member distance,
  dead/spectator handling, extra/excluded learning tags, dry run, audit log level.
- Console command `pll` (`partylearningloot`): `status`, `reload`, `loglevel`, `selftest`, `explain`, `simulate`.
- JSON-lines audit log with automatic selftest at every game start, runtime invariants, client confirmations for
  Shared Reading and an optional detailed trace of every merge decision. Sequence numbers run on across the files of a
  session; files of the running session are never rotated away.
- Safety: all patches are verified before installation and roll back as a group on any failure; a circuit breaker
  returns to vanilla loot after repeated errors; singleplayer and players without a party stay exactly vanilla.

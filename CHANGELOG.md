# Changelog

All notable changes to this mod are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).
The version in `PartyLearningLoot/ModInfo.xml` is the single source of truth.

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

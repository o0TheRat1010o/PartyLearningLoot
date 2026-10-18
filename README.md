# Party Learning Loot

**A server-side mod for 7 Days to Die (V 3.3, b18) that lets the whole party benefit from learning loot.**

[![Latest release](https://img.shields.io/github/v/release/o0TheRat1010o/PartyLearningLoot?label=release)](https://github.com/o0TheRat1010o/PartyLearningLoot/releases/latest)
![7 Days to Die V 3.3 (b18)](https://img.shields.io/badge/7%20Days%20to%20Die-V%203.3%20%28b18%29-8b0000)
![Server-side only](https://img.shields.io/badge/clients-nothing%20to%20install-2e7d32)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

In vanilla, the chance to find skill magazines (e.g. *Rifle World*) and perk books (e.g. *Sniper, Vol. 1*) depends only on
the perks of the player who opens a container. Co-op groups that specialise - one loots, one mines, one crafts, one
builds - lose learning progress, because usually only one of them opens the containers.

With Party Learning Loot, **the learning perks of every active party member count** when a container is opened:

- For each perk only the strongest member counts, so identical perks **never stack**.
- Different perks of different members combine like the perks of a single player.
- The opener keeps everything vanilla gives them: loot is **never worse than vanilla**.
- Everything else stays vanilla: loot quantity, loot stage, Treasure Hunter / Bookworm rolls, traders, quest rewards,
  bundles. **Singleplayer and players without a party are exactly vanilla.**

Optional **other loot** (off by default): the same party rule for everything else perks weight - ammo, weapons,
tools, armor, medical supplies, food and seeds (e.g. a member's *Gunslinger* raises 9 mm and .44 ammo for everyone).

Optional **Shared Reading** (off by default): when a party member reads a book, magazine or schematic, every active
member learns it too - perk, crafting points, recipe and XP, exactly as if they had read it themselves.

Players do **not** need to install anything. The mod runs on the server (dedicated server or the host of a
listen game).

**[Download](https://github.com/o0TheRat1010o/PartyLearningLoot/releases/latest)** · [How it works](#how-it-works) · [Compatibility](#compatibility) ·
[Installation](#installation) · [Configuration](#configuration) · [Console commands](#console-commands-admins) ·
[FAQ](#faq) · [Building from source](#building-from-source)

---

## How it works

7 Days to Die weighs every loot entry with the `LootProb` passive effect of the opener's perks (for example
*Dead Eye* raises *Rifle World* magazines and the *Sniper* book series). Party Learning Loot hooks the evaluation of each
perk while a container's loot is generated:

```
extra(perk)  = max(0, Δ best member - Δ opener)          for every perk
loot weight  = vanilla weight + Σ extra(perk) × entry weight before the perks
```

Each Δ is the perk's bonus computed by the game's own code with that player's own perk level and requirements
(crafting skill below 100, book series not finished, ...). The opener keeps exactly what vanilla gives them; a member
only adds the part of a perk that is stronger than the opener's version of the same perk. Vanilla's "complete" rule (a
finished crafting skill sets an entry's weight to 1) is never multiplied into a member's bonus.

By default only learning material is affected: an entry is merged only if its tags belong to a skill magazine or
perk-book series and to nothing else (weapons, ammo, food ... stay vanilla). The set of learning tags is derived from the
game data at start-up (42 tags / 29 perks in V 3.3), so XML mods that add magazines or book series are picked up
automatically.

With `IncludeOtherLoot` every other tag a perk weights is merged by the same rule as well (32 more tags in V 3.3: ammo,
weapon and tool classes, armor, medical supplies, vehicle and workstation parts, food, seeds, legendary parts; 30 perks
take part). Only *percentage* bonuses of members are shared, never changes of an entry's base weight: special loot that a
perk unlocks from weight 0 (Master Chef rank 2: dumpster food and seeds) keeps depending on the opener, just like the
"complete" rule above.

**Shared Reading** extends the configs in memory when the server loads them (the files on disk are not changed): every
readable book, magazine and schematic gets a tiny, invisible marker, and for every item a hidden "apply" buff is defined
that holds the item's learning effects. The game sends these configs to every client, so when anyone reads an item, the
server learns which item was read - without any client-side code. For every other active party member the server then
adds that item's apply buff on the member's own game, which applies the learning exactly like reading it: with the
member's own state (a book they already read changes nothing, a finished crafting skill gets no more points, their own
Nerd outfit decides the bonus point). Nothing is ever overwritten with values from the server. XP is granted like the
XP of a read, and members who are dead learn after respawning.

## Compatibility

| | |
|---|---|
| Game version | 7 Days to Die **V 3.3.0 (b18)**. Other versions: the mod checks every hook at start-up and disables a feature cleanly if something changed (see `pll selftest`). |
| Dedicated server | Supported; DLL mods load even with EasyAntiCheat enabled. Players need nothing. |
| Listen server (host plays) | Supported; the **host** must start the game **without EasyAntiCheat** (DLL mods require it). Other players need nothing. |
| Singleplayer | Loads, but behaves exactly like vanilla (there is no party). |
| Other mods | Uses Harmony prefix/postfix/finalizer patches only (no transpilers). Shared Reading only extends the loaded configs in memory. XML mods that add learning items are supported. |
| Saves | No save data of its own. Removing the mod is safe at any time. |

## Installation

There is no installer: the mod is a folder that goes into the game's `Mods` folder.

1. Download `PartyLearningLoot-<version>.zip` from the [releases page](https://github.com/o0TheRat1010o/PartyLearningLoot/releases/latest).
2. Extract it into the `Mods` folder of the dedicated server, or of the host's game for a listen server
   (`%APPDATA%\7DaysToDie\Mods` works as well). The result must look like this:

   ```
   7 Days To Die/
   └── Mods/
       ├── 0_TFP_Harmony/            ships with the game, must stay
       └── PartyLearningLoot/
           ├── PartyLearningLoot.dll
           ├── ModInfo.xml
           ├── Settings.xml
           └── README.md, LICENSE, CHANGELOG.md
   ```

   The game only looks one folder deep: if the files end up in a nested folder such as
   `Mods/PartyLearningLoot-1.1.0/PartyLearningLoot/`, the mod is not loaded (the log then says
   `Folder ... does not contain a ModInfo.xml, ignoring`).
3. Start the server. The log shows `[PartyLearningLoot] ready: ... selftest PASS`, and `pll status` in the console (F1)
   shows the mod's state.

Listen server: the host must start the game **without EasyAntiCheat** (launcher option "Play without EAC"), because the
game only loads DLL mods without it. Joining players install nothing and can keep EAC on.

**Updating:** replace the files with those of the new version but keep your own `Settings.xml`; settings that a new
version adds use their default until you add them. **Uninstalling:** delete the `Mods/PartyLearningLoot` folder.

## Configuration

`Mods/PartyLearningLoot/Settings.xml`. Most values apply live with `pll reload`; those marked *restart* are read when
the server starts. Invalid values are reported in the log and fall back to the default.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch for party-wide weighting. |
| `DryRun` | `false` | Compute and log everything but keep vanilla loot. |
| `ApplyToContainers` | `true` | World containers (shelves, magazine racks, crates ...). |
| `ApplyToLootBags` | `true` | Zombie loot bags and airdrops. |
| `IncludeSkillMagazines` | `true` | Crafting skill magazines are learning material. |
| `IncludePerkBooks` | `true` | Perk book series are learning material. |
| `IncludeOtherLoot` | `false` | Also weight all other perk-boosted loot party-wide (ammo, weapons, tools, armor, medical, food, seeds, ...). |
| `MaxMemberDistance` | `0` | `0` = unlimited, `-1` = the server's `PartySharedKillRange`, `>0` = metres. |
| `IncludeDeadMembers` | `true` | Dead members still count (like vanilla party loot stage). |
| `IncludeSpectators` | `false` | Spectators do not count. |
| `ExtraLearningTags` | *(empty)* | Advanced: comma-separated existing loot tags to treat as learning material. |
| `ExcludedLearningTags` | *(empty)* | Advanced: comma-separated tags never to merge (learning or other loot, e.g. `ammoShells`). |
| `SharedReading` | `false` | *restart* - enable Shared Reading. |
| `SharedReadingBooks` / `Magazines` / `Schematics` | `true` | *restart* - which item kinds are shared. |
| `SharedReadingShareXp` | `true` | Members also get the XP of the read. |
| `SharedReadingMaxDistance` | `0` | Same convention as `MaxMemberDistance`. |
| `SharedReadingNotify` | `true` | Toolbelt message for members who learned something. |
| `SharedReadingMessage` | `Learned with {reader}: {item}` | Message text. |
| `LogLevel` | `Summary` | Audit log: `Off`, `Summary`, `Detailed`. |

## Console commands (admins)

| Command | |
|---|---|
| `pll status` | Version, settings, patches, catalog, statistics, audit log file. |
| `pll reload` | Re-read `Settings.xml`. |
| `pll loglevel off\|summary\|detailed` | Change the audit level for this session. |
| `pll selftest` | Run all self checks (PASS/WARN/FAIL). Run it while a party is online to test the live merge as well. |
| `pll explain [player]` | Vanilla vs. party weight per learning tag for a player's party, with the member and perk that wins. |
| `pll simulate <lootContainer> [runs] [player]` | Paired vanilla/party loot simulation of a container, e.g. `pll simulate cntBookcaseFull 2000`. Nothing is spawned. |

## Balance note

Perk bonuses for learning loot are large in vanilla (+200 % to +1000 % per perk, armor perks up to +2000 %), and they
are designed for one player. A party of specialists will therefore find noticeably more magazines and books - that is
the point of the mod, but expect faster progression. Different perks still combine (e.g. three armor specialists), only
identical perks are capped at the best member. Use `pll simulate` to see the effect for your party, or `DryRun` to
preview it in the log.

`IncludeOtherLoot` has a much bigger effect: the same +200 % to +1000 % per perk then applies party-wide to ammo,
weapons, tools, armor, food and seeds. Loot groups pick their items by weight, so a party finds clearly more of what its
specialists' perks cover - and correspondingly less of the other items of the same loot groups, exactly as a single
player with all those perks would. That is why it is off by default.

## Audit log and diagnostics

Every session writes `Mods/PartyLearningLoot/Logs/audit_<date>.jsonl` (files roll over at 50 MB; at most 10 are kept,
files of the running session are never deleted). At `Summary` it contains one line per merged container and per shared
read; `Detailed` adds every merge decision, the exact vanilla value for comparison and runtime invariant checks. Shared
reads are confirmed against the members' next data syncs. Warnings and errors also appear in the server log with the
prefix `[PartyLearningLoot]`. If something goes wrong repeatedly, a circuit breaker switches merging off (vanilla loot)
until `pll reload`.

## FAQ

**Do players need the mod?** No. Everything runs on the server; Shared Reading uses the configs the server already sends.

**Does it work with EasyAntiCheat?** On a dedicated server, yes. A listen-server host must play without EAC.

**What about books a member has already read?** Shared Reading gives them what reading it themselves would give: the XP
only. Finished crafting skills get no more points.

**Does Shared Reading work for offline members?** No, only for members who are online (and within
`SharedReadingMaxDistance`). Members who are dead at that moment learn it after respawning.

**Is the Nerd outfit / Intellect mastery bonus shared?** Yes, per member: each member's own game rolls the extra
magazine point with that member's own Nerd outfit and Intellect mastery, just like reading.

**Quest or challenge progress?** Not shared - only the learning effect of the item.

**Does it also cover ammo, weapons or food?** Only with `IncludeOtherLoot=true` (applies with `pll reload`). Then every
perk bonus on loot counts party-wide, with the same rule: per perk the strongest member, never stacked.

**Can a cheating player abuse Shared Reading?** The server trusts a client's report that it read an item, just as
vanilla trusts clients with their own buffs and progress. A modified client could therefore claim reads of items it
does not own and teach them to its party. The server limits this (party members only, known learning items only, a
burst of 30 reads and then one per second per reader) and logs every read, including whether the server saw the item in
the reader's inventory - but it cannot rule it out completely. Enable Shared Reading for groups you trust; on dedicated
servers EasyAntiCheat keeps modified clients out.

## Building from source

Requirements: .NET SDK 8 or newer, the .NET Framework 4.8 targeting pack, and an installed copy of the game (the build
references the game's DLLs; none are included in this repository).

```
copy GamePath.user.props.example GamePath.user.props   (and set your game path)
dotnet build -c Release
dotnet build -c Release -p:PllDeploy=true              (also copies the mod into <game>/Mods/PartyLearningLoot)
```

The game path can also be given with `-p:SdtdGamePath=...` or the environment variable `SDTD_GAME_PATH`.

## License

MIT - see [LICENSE](LICENSE). Made by softdot.studio.

7 Days to Die is a trademark of The Fun Pimps Entertainment LLC. This mod is an unofficial fan project and is not
affiliated with or endorsed by The Fun Pimps.

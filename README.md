# Roster Control: League of Legends roster manager

A free, native Windows app for managing your League of Legends champion pool: drag champions into roster groups, rate
how good you are on each one, save rune pages, item sets, summoner spells and skill orders, record matchups, and get
counter-pick suggestions from your own notes. It also has a **League of Legends randomizer** that rolls a random
champion from your roster groups or from every champion in the game.

**Website:** [tylersbreton.github.io/roster-control](https://tylersbreton.github.io/roster-control/)

**Uses only public, keyless data** (Riot's Data Dragon, Community Dragon and Meraki Analytics). It does **not** read
or control the League client, so there's nothing to log into and nothing that touches the game.

## Download

1. Get **RosterControl-win-Portable.zip** from the [latest release](https://github.com/tylersbreton/roster-control/releases/latest).
2. Unzip it anywhere you like (e.g. `Documents\Roster Control`) and run **Roster Control.exe**.

Windows 10/11 x64, no install or admin rights needed. Windows SmartScreen may warn about an unsigned app the first
time: *More info → Run anyway*. The first start downloads game data and images (internet needed once).

**Updates are automatic.** Roster Control checks GitHub for a new release at startup and every 4 hours, downloads it in
the background and shows **Restart to update** in the title bar when it's ready (also under *Settings → App updates*).
It never restarts on its own, and your data is untouched by updates.

![Rosters](docs/images/01b-rosters-1920x1080.png)

| Ability calculator | Items and gold efficiency |
|---|---|
| ![](docs/images/37-ability-calculator.png) | ![](docs/images/38-items.png) |

| Best next item and swaps | Power curve |
|---|---|
| ![](docs/images/41-item-choices.png) | ![](docs/images/42-power-curve.png) |

| Runes | Champion stats |
|---|---|
| ![](docs/images/45-runes.png) | ![](docs/images/46-champion-stats.png) |

| Build warnings | Compare with power curves |
|---|---|
| ![](docs/images/44-build-warnings.png) | ![](docs/images/43-compare-curve.png) |

| Random pick | Compare |
|---|---|
| ![](docs/images/47-random-pick.png) | ![](docs/images/15-compare.png) |

| Rune page | Item set |
|---|---|
| ![](docs/images/03-champion-runes.png) | ![](docs/images/04-champion-items.png) |

| Spells & skill order | Counter picks & suggested bans |
|---|---|
| ![](docs/images/05-champion-spells-skills.png) | ![](docs/images/08-counter-picks.png) |

| Notes with checklists | Quick look (always on top) |
|---|---|
| ![](docs/images/07b-notes-formatted.png) | ![](docs/images/35-quick-look.png) |

| Matchups per role | Item build path |
|---|---|
| ![](docs/images/06b-matchups-role-top.png) | ![](docs/images/04c-item-tooltip-build-path.png) |

| Light theme | Light theme: a champion |
|---|---|
| ![](docs/images/40-light-rosters.png) | ![](docs/images/40-light-champion.png) |

| Adding a shared roster | |
|---|---|
| ![](docs/images/39-import-roster.png) | |

## League of Legends randomizer

Can't decide what to play, or want a challenge? **Random pick** (Ctrl+8) is a random champion picker built into the
app:

- **Roll from your own pool or from every champion.** Tick one or more roster groups (Top, Jungle, your ARAM roster…)
  to roll only champions you've placed there, or switch to *All champions* to roll from the whole champion list.
- **Equal chances or weighted.** Every champion is equally likely by default; tick *Favour my Mains and higher stars*
  to make your best champions come up more often.
- **A spinning reel** of portraits slows down and lands on the pick, shown with its splash art, the groups it's in
  and its chance. Turn animations off with *Reduce motion*.
- **Straight to the build.** Open the champion page or quick look from the result, with the default build for that
  group's role. Press **R** to roll again; your last 12 picks stay below.

![League of Legends randomizer: Random pick rolling a champion](docs/images/47-random-pick.png)

## Features

- **Rosters** — groups shown as columns (one per role by default: Top, Jungle, Middle, Bottom, Support). Drag champions
  from the pool into a group; drag cards to reorder or to move them to another group (hold **Ctrl** to copy); drag a
  card back onto the pool to remove it. Or right-click: *Add to …* on a pool champion, *Move to / Copy to* on a card.
  Add, rename, recolour, re-role, sort, reorder and delete groups. A **Compact** toggle fits more cards per column.
- **Several rosters** — separate rosters for Solo/Duo, Flex, Clash or ARAM: click the roster name to switch, create
  (one group per role, empty, or a copy), rename or delete. Builds, matchups and notes are shared by every roster.
- **Multi-select** — Ctrl+click / Shift+click cards (Ctrl+A for all), then move, copy, rate, prioritise or remove them
  together from the selection bar; dragging a selected card moves the whole selection.
- **Pool sorting** — A–Z, newest first or by class; **Hide rostered** shows only champions not yet in a group.
- **Save as image** — the whole roster board as a PNG to share.
- **Share a roster as a file** — *Share as a file…* in the roster menu saves a `.rcroster` file: groups and champions,
  optionally proficiency/priority and the builds of those champions (notes are never included). *Import roster from
  file…* previews it, then adds it as a new roster or merges it into the current one (groups matched by name or role;
  imported builds are added next to yours and identical ones skipped, nothing is replaced).
- **Card preview** — hover a roster card to see the build that opens first for that group's role (runes, core items,
  spells).
- **Edge scrolling** — while dragging, the board and columns scroll near and past their edges, faster the further out.
- **Proficiency & priority per placement** — 1–5 stars and Main / Secondary / Pocket / Learning, separately for each
  group (great Pantheon support, weak Pantheon top). Hover a card and press **1–5** to rate it (0 clears); click the
  priority label to change it.
- **Quick look** — a small always-on-top window with one champion's build (runes, items, spells, skill order) and a
  switcher for your builds, for a glance during champ select. Open it from anywhere with **Ctrl+Shift+Space**
  (changeable or off in *Settings → Quick look*), from a champion's page, a roster card's right-click menu or Ctrl+K.
  You pick the champion yourself; nothing is read from the League client.
- **Abilities** — a tab on each champion with cooldown, cost and range (Data Dragon) and damage, shields and heals per
  rank with their ratios, read from the current patch's game files (Community Dragon). Hovering an ability in the
  skill order shows the same numbers.
- **Ability calculator** — on the Abilities tab, pick a build and a level and tick the items to count: your stats at
  that level (base stats grow along the game's per-level curve; item stats and stat shards added, adaptive force
  resolved) and each ability's actual numbers for the rank the build's skill order reaches, with cooldowns after
  ability haste. The build's keystone and runes with formulas in the game files (Electrocute, Comet, Aery, Grasp,
  Fleet Footwork…) are calculated too; ones without (e.g. Conqueror) say so. Item passives aren't counted, and damage
  is before the target's resistances.
- **Best next item and swaps** — under the calculator, pick a number to rank by (an ability's damage or cooldown, a
  keystone's damage, health, effective health against physical or magic damage, auto-attack DPS…) and see the finished
  items that add the most to it at your level, with the gain per 1,000 gold. The ⇄ button under a counted item ranks
  the items that could replace it instead. Click any item to see every stat and ability number it changes.
- **Power curve** — any of those numbers from level 1 to 18 as a chart: base stats grow, ranks follow the skill order
  and each item counts from the level you finish it (a starting guess spreads the core items over levels 6–17; adjust
  each with ‹ ›, saved per build). On **Compare**, both builds' curves share one chart (stats for two champions, ability
  numbers too for two builds of one champion).
- **Build warnings** — the build editor flags likely mistakes: two pairs of boots, items whose unique passives don't
  stack (two Lifeline or Spellblade items), duplicate legendaries, more than six core items, no boots, items from
  another map, Smite / jungle companion / support item in the wrong role, an unfinished rune page and gaps in the skill
  order. Each can be ignored for that build (and shown again).
- **Items** — every shop item with **gold efficiency** (stats valued by the cheapest basic item that gives only that
  stat, from the current patch), filters by stat, tier and map, sorting, and the **builds of yours that use each
  item** (click one to open it). Search item names with Ctrl+K too.
- **Builds** — any number per champion (e.g. *Standard*, *vs AP*), each with:
  - a **rune page** laid out like the client: path icons, keystones, grayscale unselected runes, coloured selection rings,
    the client's rune-tree artwork, stat shards and full descriptions on hover. Selection rules match the client
    (two secondary runes from different rows; a third replaces the oldest).
  - an **item set** like the client's editor: named blocks, drag items in from a shop grouped by tier (Starter, Boots,
    Basic, Epic, Legendary…), stat filters, Summoner's Rift / ARAM toggle, search with nicknames (`ie`, `bork`, `dcap`),
    gold totals and full item tooltips with the **build path** (recipe tree with component costs, combine cost) and
    what each item **builds into**. Items removed in a later patch are flagged in red. The **core** block also shows
    its combined stats (AD, AP, ability haste, health…), added up from the items' descriptions.
  - **summoner spells** (D/F slots) and an 18-level **skill order** grid with the champion's ability icons, rank limits,
    a "max order" summary and one-click fill (R at 6/11/16).
  - notes and a role tag.
  - **copy to another champion** (runes, items, spells and notes; the skill order is champion-specific).
  - **share codes** — *Copy share code* puts a short `RC1:…` code on the clipboard to paste in Discord or anywhere;
    a friend copies it (the whole message is fine) and clicks *Paste build code* on the Builds tab or in Ctrl+K.
  - **copy as League item set** — puts the build's items on the clipboard in the League client's item set format;
    import it in the client (Collection → Items → item sets) and the build shows in the in-game shop. Clipboard only:
    nothing talks to the client.
  - a **default build per role** (★): opening the champion from that role's group selects it first.
  - drag builds in the list to **reorder** them.
- **Matchups** — *Good against* / *Weak against* lists per champion with a note per matchup (drag champions in, flip,
  reorder). A matchup applies to **all roles or one role** (Pantheon vs Darius can differ top vs support); a
  role-specific matchup overrides the all-roles one for that role. Matchups recorded on the other champion are shown
  too ("noted from the other side").
- **Counter Picks** — pick up to five enemies; each roster placement (all groups or one) is ranked by the matchups that
  apply to its group's role, including inverse ones, with proficiency and priority as tie-breakers.
- **Suggested bans** — the champions that counter the most of your champions in scope (Mains weigh most; champions you
  have answers to rank lower), with the reasons and notes on hover.
- **Champions library** — every champion with roster/build/matchup badges; filter by role, class, your tags.
- **Runes** — every rune and stat shard by path and slot, with its description, its formulas from the game files
  (e.g. *70–240 by level (+5% AP) (+10% bonus AD)*) and the builds of yours that use it (Ctrl+6, or search a rune in Ctrl+K).
- **Champion stats** — every champion's base stats at a level you pick (1–18): health, mana, AD, attack speed, armor,
  magic resist, move speed, range and growth per level. Sort by any column; the top tenth of each column is green and
  your roster's champions are marked; filter by role, class, melee/ranged or your roster (Ctrl+7).
- **Random pick** — tick one or more groups (from any roster) or pick from every champion, then roll: a reel of
  portraits spins and lands on a champion, shown with its splash art, the groups it's in and its chance. Equal chances,
  or favour your Mains and higher stars. Open the champion or its quick look from the result (Ctrl+8; R rolls again).
- **Patch review** — builds whose items changed or were removed (or whose runes no longer exist) since they were saved
  are listed after a patch, with a banner on Rosters; open each one or mark it reviewed (with Undo).
- **Compare** — two champions (or two builds of one) side by side: runes, items, spells, skill order, roster
  placements, matchups and a head-to-head from your notes. From a champion's page or Ctrl+K.
- **Notes & tags** per champion. Champion, build and matchup notes support light formatting: `- bullets`,
  `[ ] checkboxes` (tick them without editing) and `**bold**`; Enter continues a list and **Ctrl+B** toggles bold.
- **Full screen** — F11 or the ⤢ caption button; leave with **Esc**, **F11** or *Exit full screen* in the title bar
  (or hover the top edge for the window's own controls).
- **Backup** — export/import all data as JSON; an automatic backup is written daily (last 10 kept).
- **Quick search** — **Ctrl+K** (or the search box in the title bar) finds champions (with the groups they're in),
  pages and actions; recently opened champions come first. From three letters it also searches the text of your
  champion, build and matchup notes and jumps to the note.
- **Undo** — deleting a build, group, item block, placement or matchup shows an *Undo* toast (also **Ctrl+Z**).
- **Step through a list** — a champion opened from a group, the pool or the library shows *‹ 2 / 5 ›* in its header;
  **Ctrl+←/→** moves to the previous / next champion.
- **Remembers where you were** — filters, searches, the last champion tab, editor tab, matchup role, shop filters and
  window zoom are kept between sessions, as are the window's size, position, maximised state and the sidebar. If the
  monitor it was on is gone, the window opens centred on the main screen. **Enter** in a search box opens or adds the
  first result.
- **Zoom** — 80–150 % (**Ctrl+=**, **Ctrl+-**, **Ctrl+0** or *Settings → Display*).
- **Pick up where you left off** — optionally reopen the last page and champion at startup (*Settings → Display*;
  off by default, so the app starts on Rosters).
- **What's new** after each update (from [CHANGELOG.md](CHANGELOG.md); also under *Settings → About*).
- **Saved ✓** in the title bar after each change; **Reduce motion** in *Settings → Display* (no animations or glow).
- **Light or dark** — *Settings → Display*: Dark (default), Light or Match Windows, and an accent colour (six presets
  or any colour). The rune page keeps the client's dark art in both.
- **Troubleshooting** — *Copy diagnostic info* (Windows user name hidden) and *Open logs folder* in Settings.

### Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+K / Ctrl+P | Quick search |
| Ctrl+1 … Ctrl+8 | Rosters · Champions · Counter Picks · Settings · Items · Runes · Champion stats · Random pick |
| Alt+← | Back |
| Ctrl+Z | Undo the last removal |
| Ctrl+= / Ctrl+- / Ctrl+0 | Zoom in / out / reset |
| F11, Esc | Full screen, leave full screen |
| Ctrl+N | New group (Rosters) or new build (champion page) |
| 1–5, 0, Delete | Rate, clear or remove the hovered roster card |
| Ctrl+Tab | Next tab on a champion page |
| Ctrl+← / Ctrl+→ | Previous / next champion in the list it was opened from |
| Ctrl+B, Enter, Esc (in notes) | Bold, continue a list, finish editing |
| Ctrl+click, Shift+click, Ctrl+A | Select roster cards (then 0–5 rates, Delete removes, Esc clears) |
| Ctrl+Shift+Space (from any app) | Show or hide quick look (changeable in Settings) |

## Data storage

Everything is local to each copy of the app, in the `data` folder **of the folder you unzipped** (next to
`Roster Control.exe`). Updates replace only the app files in `current\`, never `data\`:

| Path | Content |
|---|---|
| `data\roster.db` | SQLite database (groups, placements, builds, matchups, notes, preferences) |
| `data\backups\` | Automatic daily backups, safety backups taken before an import, and `before-upgrade-*.db` copies of the database taken before an update changes its format (last 5 kept) |
| `data\cache\` | Downloaded game data and images (safe to delete; re-downloaded). *Settings* shows its size and can clear the images. |
| `data\logs\` | Daily rolling logs (10 days) |

If the application folder isn't writable (e.g. under Program Files), `%LOCALAPPDATA%\RosterControl` is used instead.
`RosterControl.exe --data <folder>` stores data elsewhere. Copy the `data` folder (with the app closed) to move your
rosters, or use **Settings → Export**.

### Updates and your data

Updates never touch `data\`. When a new version changes the database format, it upgrades your existing data in place
on first start (nothing is re-entered or lost) and first saves the previous database as
`data\backups\before-upgrade-<date>.db`. To go back to an older version, close the app and copy that file over
`data\roster.db`. Backups exported by any version can be imported by any later version, and an older copy of the app
warns if it opens data written by a newer one.

Every release is tested against databases and backups from each earlier release (`tests/RosterControl.Tests/UpgradeTests.cs`);
a new database migration fails the tests until it's added there.

### Caching and patches

Everything that can be cached is, but champions are occasionally added and items change most patches, so:

| Data | Policy |
|---|---|
| Patch list (`versions.json`) | The app opens instantly from the newest cached patch, then checks online in the background (8 s timeout) and every 2 hours while running (every 10 minutes after an offline start). The first run downloads everything. A new patch shows an **Update** button in the title bar — it's never applied mid-edit. |
| Champions, items, runes, spells (per patch) | Immutable per patch → cached forever; a new patch is a new download. The newest 3 patches are kept, older ones pruned. |
| Portraits, item and spell icons | URLs include the patch → cached per patch, pruned with it. |
| Splash art, rune/shard/role icons (unversioned) | Re-validated after 7 days with ETag / If-Modified-Since (304 = keep); the cached copy is shown meanwhile. |
| Stat shards (Community Dragon) | Re-downloaded whenever the patch changes, otherwise daily. |
| Champion positions and AD growth (Meraki) | Weekly, or sooner when a champion is missing (a new release; retried every 12 h until Meraki has it). AD per level is used where Data Dragon reports 0 (every champion since 16.22); the calculator uses the champion's game file instead. |
| Ability numbers (Community Dragon game files) | Downloaded per champion when first shown, cached per patch; Community Dragon's patch is re-checked every 6 hours, so numbers update once it has the new patch. |
| Rune numbers (Community Dragon `perks.cdtb.bin.json`) | Downloaded once per patch when the calculator first needs them, cached like ability numbers. |

New champions get a **NEW** badge. Builds saved on an older patch say so in the build list, and the item editor marks
items whose cost, stats or recipe changed since (and items removed from the game). Editing a build saves it on the
current patch, which marks it as reviewed.

## Development

Prerequisites: .NET SDK 10.0.100+, Windows. (Python 3 only to regenerate icons.)

```powershell
dotnet build RosterControl.sln
dotnet run --project src/RosterControl.Desktop                 # data goes to src\RosterControl.Desktop\bin\...\data
dotnet test tests/RosterControl.Tests                          # rules, parsers, repository, backup (offline)
dotnet test tests/RosterControl.UITests                        # headless UI + screenshots (needs internet on first run)
dotnet tool restore; dotnet ef migrations add <Name> --project src/RosterControl.Data --startup-project src/RosterControl.Data
python tools/generate_lucide_icons.py                          # regenerate Styles/Icons.axaml
dotnet run --project tools/RosterControl.DevTools -- icon src/RosterControl.Desktop/Assets
```

```text
src/
  RosterControl.Core/          models, game-data models, rules (rune pages, skill order, item shop, counter picks)
  RosterControl.Integrations/  Data Dragon / Community Dragon / Meraki client, disk cache, image cache, parser
  RosterControl.Data/          EF Core + SQLite (migrations), repository, preferences, backups
  RosterControl.Desktop/       Avalonia app (exe "RosterControl"): views, view models, drag & drop, styles
tests/  RosterControl.Tests · RosterControl.UITests (Avalonia headless)
tools/  publish.ps1 · generate_lucide_icons.py · RosterControl.DevTools (icon)
```

## Releasing

Every release also updates this page:

1. Bump `<Version>` in [Directory.Build.props](Directory.Build.props) and add the version's section at the top of
   [CHANGELOG.md](CHANGELOG.md) (the app's *What's new* reads it).
2. Describe new features here in **Features** (and shortcuts, caching or data notes where they change).
3. Refresh the screenshots: `.\tools\update-readme-screenshots.ps1` runs the headless UI tests, which capture every
   screen, and copies the ones listed in [tools/readme-screenshots.txt](tools/readme-screenshots.txt) to `docs\images`.
   Add new screens to that list and to the gallery above.
4. Commit, push, then tag.

Releases are built by GitHub Actions ([release.yml](.github/workflows/release.yml)) when a version tag is pushed:

```powershell
git tag v1.0.1
git push origin v1.0.1
```

The workflow runs the unit tests, publishes a self-contained win-x64 build, packs it with
[Velopack](https://velopack.io) (portable zip + full and delta update packages) and creates the GitHub release with
generated notes. Running copies pick it up automatically. Every push and pull request also builds and runs the unit
tests ([ci.yml](.github/workflows/ci.yml)).

`.\tools\publish.ps1 -Version 1.0.0` still produces a plain (non-updating) portable build locally in `artifacts\publish`.

## Support

Roster Control is free and always will be. If it's useful to you, you can support development through
[GitHub Sponsors](https://github.com/sponsors/tylersbreton) (or the ❤ **Sponsor** button at the top of the repository).

## Legal

Roster Control isn't endorsed by Riot Games and doesn't reflect the views or opinions of Riot Games or anyone
officially involved in producing or managing Riot Games properties. Riot Games, and all associated properties are
trademarks or registered trademarks of Riot Games, Inc.

Roster Control is [MIT licensed](LICENSE). Third-party components: Avalonia (MIT), SkiaSharp (MIT),
CommunityToolkit.Mvvm (MIT), Velopack (MIT), EF Core / Microsoft.Extensions (MIT), Serilog (Apache-2.0),
Lucide icons (ISC), Inter (OFL 1.1).

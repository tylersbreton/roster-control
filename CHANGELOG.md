# Changelog

What's new in each version of Roster Control. The app shows this after an update (also under Settings → About).

## 1.5.0

- **Ability calculator** on the Abilities tab: pick a build and a level (1–18), tick the items to count, and see your stats (health, AD, AP, armor, attack speed, ability haste…) and every ability's actual numbers, e.g. Q: 317 damage at rank 5. Ranks follow the build's skill order, cooldowns include your ability haste, and the build's stat shards are counted.
- **Keystone numbers** — Electrocute, Arcane Comet, Aery, Grasp, Fleet Footwork and other runes with formulas in the game files show their damage, shields and heals at your level and stats. Runes without published formulas (e.g. Conqueror) say so.
- **Items page** — every shop item with gold efficiency (stats valued by the cheapest basic item, e.g. Long Sword for AD), filters by stat, tier and map, and the builds of yours that use each item. Ctrl+5, the sidebar, or search an item name with Ctrl+K.
- **Copy as League item set** — from a build (or quick look), paste it in the League client's item set import so your build shows in the in-game shop.
- **Share a roster as a file** — the roster menu saves a .rcroster file with its groups and champions, and optionally ratings and builds (notes are never included). *Import roster from file* shows what's inside, then adds it as a new roster or merges it into the current one; builds are added next to yours, never replacing them.
- **Light theme and accent colour** — Settings → Display: Dark, Light or Match Windows, and six accent presets or any colour you like.
- Internal tidy-up of the largest screens' code, for faster work on future features.

## 1.4.0

- **Quick look** — a small always-on-top window with one champion's build: runes, items, summoner spells and skill order, with a switcher for your builds. Open it from anywhere with **Ctrl+Shift+Space** (change it in Settings → Quick look), from a champion's page, a roster card's right-click menu or Ctrl+K.
- **Abilities tab** on each champion: cooldown, cost and range, plus damage, shields and heals per rank with their ratios (e.g. 35/60/85/110/135 (+50% AP)), taken from this patch's game files. Hover an ability in the skill order to see the same numbers.
- **Build totals** — the core block shows its combined stats (AD, AP, ability haste, health…) next to its gold cost; the card preview, Compare and quick look show them too.

## 1.3.0

- **Several rosters** — keep separate rosters for Solo/Duo, Flex, Clash or ARAM. Click the roster name on the Rosters page to switch, create (one group per role, empty, or a copy of the current one), rename or delete. Builds, matchups and notes are shared by every roster.
- **Select several champions** — Ctrl+click or Shift+click roster cards (Ctrl+A selects all), then move, copy, rate, prioritise or remove them together. Dragging a selected card moves the whole selection; Esc clears it.
- **Sort the champion pool** — A–Z, newest first, or by class (also on the Champions page).
- **"Not in roster" filter** on the Champions page, to find champions you haven't placed in any group of the current roster.
- **Save your roster as an image** — a PNG of the whole board to share in Discord.
- **Hover a roster card** to preview the build that opens first for that role: runes, core items and summoner spells.
- **Patch review** — when items in your builds change or are removed (or runes no longer exist), a banner on Rosters lists the affected builds with what changed. Open each one, or mark it reviewed.
- **Smoother drag scrolling** — dragging near or past the edge of the board or a column scrolls it, faster the further you go, even outside the window.
- **Compare two champions** side by side: builds, runes, items, spells, skill order, roster placements, matchups and how they fare against each other. Open it from a champion's page or Ctrl+K.
- **Pick up where you left off** — optionally reopen the last page and champion at startup (Settings → Display; the app starts on Rosters by default).
- **What's new** — this summary after each update.
- **Copy diagnostic info** and **Open logs folder** in Settings, for bug reports.
- **"Saved ✓"** briefly in the title bar after each change.
- **Reduce motion** option in Settings → Display: no animations or background glow.
- Your data is upgraded automatically, and a copy of the previous database is kept in data\backups before each upgrade.

## 1.2.0

- **Share builds** — copy a build as a short code and paste it anywhere; friends add it with Paste build code.
- **Default build per role** (★) — opens first when you open the champion from that role's group.
- **Drag builds** in the build list to reorder them.
- **Formatted notes** — bullets, checkboxes you can tick without editing, and **bold**, in champion, build and matchup notes.
- **Ctrl+K searches your notes** and jumps to them.
- The window reopens where you left it, even after unplugging a monitor.
- The mouse pointer is hidden while dragging; the dragged icon sits under it.

## 1.1.0

- **Quick search (Ctrl+K)** for champions, pages and actions, plus many keyboard shortcuts (see Settings).
- **Undo** for deleted builds, groups, item blocks, placements and matchups (also Ctrl+Z).
- Step through a group with previous / next on the champion page.
- Right-click menus to add, move and copy champions; compact cards; copy a build to another champion.
- Filters, tabs and zoom are remembered; Enter in a search box opens the first result.
- Opens instantly from the cached patch; zoom 80–150 %; friendlier network errors.
- Support development through GitHub Sponsors.

## 1.0.0

- First release: rosters by role with drag and drop, proficiency and priority per placement, rune pages and item sets like the client, summoner spells and skill order, matchups per role, counter picks and suggested bans, notes and tags, backups and automatic updates.

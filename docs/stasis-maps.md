# Stasis special places: which client maps they use

Every special place of the Stasis rules needs a real client map. It has to be walkable and have
room for a fight, and no normal gameplay may use it: no zaap, no quest NPC, no dungeon room. The
checks below were made against `bases/world.db` (MapPositions, MapTemplates, SubAreaTemplates,
NpcSpawns, MapMobs, DungeonRooms, InteractiveTeleports). The cells come from `datos/map_walkable_cells.json`
and `datos/map_fight_cells.json`, and each map id was also searched for in `content/`, the server
code, `datos/npcs_reales.json` and the worldgraph teleports. "Main floor" means
`MapManager.MainFloor`: the walkable piece you can reach the borders from, or the largest piece.
`WorldMoveHandler.TeleportAsync` lands on the walkable cell nearest to cell 280, and every landing
listed here is on the main floor.

| Place | Map id | Subarea | Fight board | Landing cell |
|---|---|---|---|---|
| Fissure arena | 202901510 «Dimensional Voyagers' Tower» [-22,-24] | 935 «Futuro no previsto» | 202903046 (twin) | 296 |
| Concours Poutch | 209455110 «Pandawushu Dojo» [20,-31] | 951 «Pueblo de Pandala» | 209585672 (twin) | 296 |
| Grande Traque | drawn each time | any of 155 subareas on worldMap 1 | the subarea's own | none (you walk there) |
| Sanctuaire | 211292160 «Forbidden Inn Great Hall» [23,-32] | 960 «Taberna Prohibida» | none (a hub) | 316 (fixed) |
| Time Attack | none of its own (any dungeon) | none | the dungeon's | none |

## Fissure: 202901510

- **Before:** 191104002, an ordinary Astrub City square [4,-18] in front of the bank. It has a door,
  three monster groups, NPCs in `life.json`/`official.json`, a quest objective and tests that use it.
  That is normal gameplay.
- **Now:** the Dimensional Voyagers' Tower as it looks in «Futuro no previsto» (a world of visions:
  worldMap 19). The present-day tower is where the travellers watch the rifts (it has a «Fissure
  Tracking Room», 130025985). This version is the same tower in a future that never happened,
  which suits a rift in time and space. Nothing references it: no NPC, monster, quest, zaap,
  teleport or dungeon. None of its four scroll neighbours exist, so no door or border leads in
  or out.
- **Cells:** 159 walkable, 261 in fight, all on the main floor. The landing cell is 296.
- **Fight board:** the generic arena rule would pick 203032070, the subarea's unnamed board (only
  132 cells match). The twin 202903046 has the same name and matches 157 of the 159 cells, so it
  is now written in `content/fights/arenas.json` under «twins».
- **Getting in and out (code):** the leader is taken in by `.fissure <clé>`, which now remembers
  where they left from. The other party members use the new `.fissure rejoindre`. `.fissure
  quitter` (aliases: `abandon`, `sortir`) still closes the expedition. When typed on the arena, it
  also takes the player back to where they came from, or to the Sanctuary if that place is not
  known (for example after a restart). `.sanctuaire` and recall potions work as well.
- **Other options considered:** the «Time Abysses» (196085764) belong to the temporal anomalies.
  «Unknown Dimension» (192677892) and «Abyssal Dimension» (154142209) are quest maps. The Fissure
  Tracking Room has NPCs (TravelersTower.cs). The dream lands' Nightmare maps are free but are
  outdoors and joined to each other.

## Concours Poutch: 209455110

- **Before:** 99090957, the Amakna kanojedo. Its master (NPC 7416) and six Puch Ingball groups
  are already there, and the village leads into it by a door.
- **Now:** the inner hall of the Pandawushu Dojo, a martial-arts dojo with tatamis. You reach it
  through the door of the quest tatami 207622146. That tatami holds Master Kuatinbe's fight and
  quest objective, but this inner room has nothing: no NPC, monster, quest or zaap. It is walkable
  normally (the village's door), so `.event aller` and walking in both work.
- **Cells:** 185 walkable, 205 in fight. The landing cell is 296, on the main floor.
- **Fight board:** the generic rule would pick 207751172, a 50-cell board of the village. The twin
  209585672 has the same name and matches 151 of its 151 cells, so it is now in `arenas.json`
  under «twins».
- **Rules (code):** the kanojedo's training rulebook (no loot, the bag is not consumed) used to
  depend on the master standing on the map. Now `Kanojedo.IsDojo` is also true for the Poutch
  room (`StasisEvents.IsPoutchRoom`), so the event's bag stays for every player.

## Grande Traque: drawn maps

- No fixed map. Each run picks `hidden` outdoor maps of one random subarea, and standing on one
  of them counts as finding it.
- **Problem found:** out of 245 subareas that qualified, 81 were not on the Twelve's world map.
  They included the dream lands (worldMap 19), Frigost III (12), Enurado, Srambad, Xelorium and
  Ecaflipus (13 to 16), Incarnam (2), the Koliseo and the test boards (-1), and a few maps with
  arena flags. Most players cannot reach those, and their [x,y] clues point at a square of
  Amakna's world.
- **Fix (code):** `MapInfo.WorldMap` is now read from MapTemplates «worldMap» (default 1).
  `StasisEvents.TraqueGround` keeps only outdoor maps with worldMap 1 that do not carry the arena
  flags. 155 subareas still qualify.

## Sanctuaire: 211292160 (unchanged)

- The Great Hall of the Forbidden Inn, an indoor level-200 Pandala hub. Only the Sanctuary's own
  files use it (`sanctuary.json`, and its four NPCs in `spawns.json`/`life.json`). No quest, zaap,
  dungeon or monster uses it. The Inn's doors (211028226, 211293186, 211295234) lead in and out,
  so `.retour` is not the only way out.
- 163 walkable cells. `cell` 316 and the NPC cells 303, 331, 357 and 289 are all walkable and on
  the main floor. It is a sensible choice and was left as it was.

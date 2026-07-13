"""VillageForge v2 — PHASED, FORWARD-PLANNING, 3D (Ken's directive 2026-07-13).

THE CENTRAL IDEA: the endgame master plan is generated FIRST (the full walled
town the reference guide's Stage 3 describes), then each PHASE builds a subset
of it. Nothing is ever torn down; phase 1's cellar is already dug under phase
3's great hall; the street grid at founding is the same one the final walls
enclose. "Needs now, needs later, more people will show up" = master plan
minus the parts you haven't earned yet.

PHASES (from the reference guide's staged recommendations):
  1 FOUNDING      pop~4  · houses(2) · research spot · COLD CELLAR (-2 levels)
                          · crop field · chokepoint wall stub at the gate
  2 ESTABLISHMENT pop~9  · +houses · library · workshop · great hall(2F)
                          · smokehouse+butcher · pens · sewing
  3 FORTRESS      pop~14 · full wall+towers+gate · church+graveyard · infirmary
                          · market · KEEP (3 floors) · more houses(2F)
  4 ENDGAME       pop~20 · grand-objective district (market cross / chapel
                          expansion) · remaining reserved lots fill in

3D: every building carries floors (1-3) and cellar depth (0-2). Cellars are
fully walled with beams every 3 tiles and a stair shaft (guide: cave-ins,
rats, flooding). Upper floors rest on the walls below (stability inherits
upward free); beams (15 wood, any span ≤ maxlen) carry floors. The render
shows EVERY LEVEL: L-2, L-1, SURFACE, F2, F3 as panels, with future-phase
buildings ghosted on the surface view.

OUTPUT per phase: village_phaseN.png + plan_phaseN.json (ordered, actuator
vocabulary, only the NEW work for that phase — the port unit).
"""

import json
import math
import os
import random

from PIL import Image, ImageDraw

WATER, GRASS, FOREST, ROCK = 0, 1, 2, 3
TERRAIN_COLORS = {WATER: (58, 96, 138), GRASS: (116, 150, 84), FOREST: (74, 104, 58), ROCK: (128, 126, 118)}
ROAD_COLOR = (168, 148, 110)
FIELD_COLOR = (150, 160, 70)
GRAVE_COLOR = (100, 110, 90)
WALL_COLOR = (72, 70, 66)
GHOST = (210, 205, 190)

KIND_FILL = {
    "house": (122, 88, 54), "great_hall": (140, 104, 62), "keep": (110, 108, 104),
    "church": (146, 118, 78), "library": (128, 100, 70), "workshop": (104, 80, 60),
    "infirmary": (140, 120, 100), "market": (150, 122, 66), "smokehouse": (96, 74, 56),
    "barn": (112, 86, 58), "sewing": (118, 92, 62), "research": (126, 102, 74),
}


class Situation:
    def __init__(self, seed, biome, pop_final=20, w=110, h=110, style=None):
        self.seed, self.biome, self.pop_final = seed, biome, pop_final
        self.w, self.h = w, h
        self.style = style or Style()


class Style:
    """THE LLM'S EXPRESSIVE SURFACE (Ken 2026-07-13: the screenshots differ
    because the PLAYERS differ — 'that person playing our game is the LLM
    saying this is what we want; execute our generator'). The in-game
    architect lane fills exactly this JSON; deterministic code executes it.
    Every knob is a taste, not a correctness rule — correctness (terrain,
    stability, phases, reservations) stays in the generator."""

    def __init__(self,
                 density="village",        # hamlet | village | town  (lot gaps: 3 / 1 / 0-shared)
                 walls="palisade",         # none | palisade | stone | stone_integrated
                 layout="crossroads",      # crossroads | grid | ring
                 verticality="low",        # low | mid | high  (floor-count bias)
                 farms="outside",          # outside | inside
                 keep=True,                # a central keep/manor?
                 name="unnamed style"):
        self.density, self.walls, self.layout = density, walls, layout
        self.verticality, self.farms, self.keep = verticality, farms, keep
        self.name = name

    def lot_gap(self):
        return {"hamlet": 3, "village": 1, "town": 0}[self.density]

    def spread(self):
        return {"hamlet": 30, "village": 24, "town": 18}[self.density]

    def floors_for(self, kind, rng):
        bias = {"low": 0.15, "mid": 0.5, "high": 0.8}[self.verticality]
        if kind == "keep":
            return 3 if self.verticality != "high" else 4
        if kind in ("great_hall", "church", "library"):
            return 2 if rng.random() < bias + 0.3 else 1
        return 2 if rng.random() < bias else 1


GRID_FILE = r"F:\DEV_ENV\projects\Mods\Going Medieval\LLM_NPCs_BepInEx\validation\worldmap_grid.txt"


def load_game_terrain(sit, path=GRID_FILE):
    """--from-game: the mod's full-resolution snapshot export becomes the
    forge's terrain — master plans generated against the REAL map. '#'
    (already built) is treated like rock: never build on it."""
    with open(path, encoding="utf-8") as f:
        w, h = (int(v) for v in f.readline().split())
        rows = [f.readline().rstrip("\n") for _ in range(h)]
    sit.w, sit.h = w, h
    mapping = {"~": WATER, ".": GRASS, "T": FOREST, "#": ROCK, "^": ROCK, " ": ROCK}
    return [[mapping.get(rows[y][x] if x < len(rows[y]) else " ", ROCK)
             for x in range(w)] for y in range(h)]


# ── terrain (as v1) ─────────────────────────────────────────────────────────
def synth_terrain(sit):
    rng = random.Random(sit.seed * 7919)
    t = [[GRASS] * sit.w for _ in range(sit.h)]
    water_blobs = {"marsh": 24, "valley": 5, "hillside": 4}[sit.biome]
    forest_blobs = {"marsh": 8, "valley": 12, "hillside": 10}[sit.biome]
    rock_blobs = {"marsh": 2, "valley": 3, "hillside": 9}[sit.biome]

    def blob(cls, count, rmin, rmax):
        for _ in range(count):
            cx, cy = rng.randrange(sit.w), rng.randrange(sit.h)
            r = rng.randint(rmin, rmax)
            for y in range(max(0, cy - r), min(sit.h, cy + r)):
                for x in range(max(0, cx - r), min(sit.w, cx + r)):
                    if math.hypot(x - cx, y - cy) < r * (0.7 + 0.3 * rng.random()):
                        t[y][x] = cls
    blob(FOREST, forest_blobs, 5, 13)
    blob(ROCK, rock_blobs, 3, 7)
    blob(WATER, water_blobs, 4, 12)
    if sit.biome == "valley":
        ry = sit.h // 4 + rng.randint(-6, 6)
        for x in range(sit.w):
            ry = min(sit.h - 3, max(2, ry + rng.choice((-1, 0, 0, 1))))
            for dy in range(-2, 3):
                t[ry + dy][x] = WATER
    return t


def find_center(sit, t):
    best, bx, by = -1, sit.w // 2, sit.h // 2
    rng = random.Random(sit.seed * 31)
    for _ in range(800):
        x, y = rng.randrange(14, sit.w - 14), rng.randrange(14, sit.h - 14)
        if t[y][x] != GRASS:
            continue
        r = 0
        while r < 20:
            r += 1
            if any(not (0 <= int(x + r * math.cos(math.radians(a))) < sit.w
                        and 0 <= int(y + r * math.sin(math.radians(a))) < sit.h)
                   or t[int(y + r * math.sin(math.radians(a)))][int(x + r * math.cos(math.radians(a)))] == WATER
                   for a in range(0, 360, 30)):
                break
        if r > best:
            best, bx, by = r, x, y
    return bx, by


# ── master plan: the ENDGAME town, then phase tags ─────────────────────────
def master_plan(sit, t):
    rng = random.Random(sit.seed)
    st = sit.style
    cx, cy = find_center(sit, t)
    occ = [[0] * sit.w for _ in range(sit.h)]   # 0 free, 1 road, 2 lot, 3 wall, 4 field, 5 grave
    plaza = 3 if st.density == "hamlet" else 4
    buildings = []

    # street skeleton BY LAYOUT CULTURE (the LLM's pick), laid once at founding
    for y in range(cy - plaza, cy + plaza + 1):
        for x in range(cx - plaza, cx + plaza + 1):
            occ[y][x] = 1

    def lay(x, y, wide_axis):
        if 0 <= x < sit.w - 1 and 0 <= y < sit.h - 1 and t[y][x] != WATER:
            occ[y][x] = 1
            if wide_axis == "h":
                occ[y + 1][x] = 1
            elif wide_axis == "v":
                occ[y][x + 1] = 1

    reach = st.spread() + 4
    if st.layout in ("crossroads", "grid"):
        for d in range(plaza + 1, reach):
            lay(cx + d, cy, "h"); lay(cx - d, cy, "h")
            lay(cx, cy + d, "v"); lay(cx, cy - d, "v")
    if st.layout == "grid":
        for off in (-10, 10):
            for d in range(0, reach - 4):
                lay(cx + d, cy + off, "h"); lay(cx - d, cy + off, "h")
                lay(cx + off, cy + d, "v"); lay(cx + off, cy - d, "v")
    if st.layout in ("crossroads", "ring"):
        ringr = st.spread() - 8
        for a in range(0, 360, 2):
            x = int(cx + ringr * math.cos(math.radians(a)))
            y = int(cy + ringr * math.sin(math.radians(a)))
            if 0 <= x < sit.w and 0 <= y < sit.h and t[y][x] != WATER and occ[y][x] == 0:
                occ[y][x] = 1
    if st.layout == "ring":
        for d in range(plaza + 1, st.spread() - 7):   # one spoke to the ring + gate
            lay(cx, cy + d, "v")

    def road_adjacent_spots(w, h):
        """Lots ALONG street frontage (v1 flaw #2 fixed): scan cells adjacent
        to roads, try the rect on each side of the road direction."""
        spots = []
        for y in range(2, sit.h - h - 2):
            for x in range(2, sit.w - w - 2):
                if occ[y][x] != 0:
                    continue
                north_road = any(occ[y - 1][x + i] == 1 for i in range(w))
                west_road = any(occ[y + i][x - 1] == 1 for i in range(h))
                if north_road or west_road:
                    spots.append((x, y))
        rng.shuffle(spots)
        return spots

    def fits(x0, y0, w, h):
        # FOOTPRINT: in bounds, dry, unoccupied (forest is clearable — allowed).
        # The margin may contain ROADS (that's the point of frontage) — only
        # the footprint itself must be free. (v2.0 bug: margin-included-road
        # rejected every road-adjacent lot → built 0/0.)
        for y in range(y0, y0 + h):
            for x in range(x0, x0 + w):
                if not (0 <= x < sit.w and 0 <= y < sit.h):
                    return False
                if t[y][x] in (WATER, ROCK) or occ[y][x] != 0:
                    return False
        # margin BY DENSITY (the style's voice): hamlet keeps 3 clear cells,
        # village 1, town 0 — shared walls are a taste, not a rule.
        gap = st.lot_gap()
        if gap > 0:
            for y in range(y0 - gap, y0 + h + gap):
                for x in range(x0 - gap, x0 + w + gap):
                    if 0 <= x < sit.w and 0 <= y < sit.h and occ[y][x] in (2, 4, 5):
                        return False
        # forward coherence: near the plaza, same landmass (no cross-water annexes)
        if math.hypot(x0 + w / 2 - cx, y0 + h / 2 - cy) > st.spread():
            return False
        return True

    def place(kind, w, h, phase, floors=1, cellar=0, dist=(0, 24)):
        for (x, y) in road_adjacent_spots(w, h):
            d = math.hypot(x + w / 2 - cx, y + h / 2 - cy)
            if not (dist[0] <= d <= dist[1]) or not fits(x, y, w, h):
                continue
            for yy in range(y, y + h):
                for xx in range(x, x + w):
                    occ[yy][xx] = 2
            b = {"kind": kind, "rect": [x, y, w, h], "phase": phase,
                 "floors": floors, "cellar": cellar}
            buildings.append(b)
            return b
        return None

    # ENDGAME composition, tagged by the phase that builds it; sizes shrink
    # one notch if the first try can't find a lot (lot-first sizing, v2.2)
    sp = st.spread()

    def place_shrinking(kind, w, h, phase, floors=None, cellar=0, dist=None):
        floors = floors if floors is not None else st.floors_for(kind, rng)
        d = dist or (plaza + 1, sp - 2)
        b = place(kind, w, h, phase, floors=floors, cellar=cellar, dist=d)
        if b is None and w > 4 and h > 4:
            b = place(kind, w - 1, h - 1, phase, floors=floors, cellar=cellar, dist=d)
        return b

    place_shrinking("great_hall", 10, 8, 2, cellar=2, dist=(plaza + 1, plaza + 10))  # cellar dug in phase 1!
    if st.keep:
        place_shrinking("keep", 7, 7, 3, cellar=1, dist=(plaza + 2, sp // 2 + 4))
    place_shrinking("research", 5, 5, 1, dist=(plaza + 1, sp // 2))
    place_shrinking("library", 6, 6, 2)
    place_shrinking("workshop", 6, 5, 2)
    place_shrinking("sewing", 4, 5, 2)
    place_shrinking("market", 7, 5, 3, dist=(plaza + 1, sp // 2))
    place_shrinking("church", 6, 9, 3, dist=(8, sp - 2))
    place_shrinking("infirmary", 6, 5, 3, dist=(6, sp - 4))
    place_shrinking("smokehouse", 4, 4, 2, dist=(8, sp - 2))
    place_shrinking("barn", 7, 5, 2, dist=(10, sp))
    # houses: phase 1 gets 2, phase 2 +3, phase 3 +3, phase 4 +2 (pop growth)
    for i, phase in enumerate([1, 1, 2, 2, 2, 3, 3, 3, 4, 4]):
        w = rng.randint(5, 7)
        h = rng.randint(5, 8)
        if rng.random() < 0.5:
            w, h = h, w
        place_shrinking("house", w, h, phase,
                        cellar=1 if rng.random() < 0.3 else 0, dist=(plaza + 1, sp - 2))

    # fields & graveyard & pens (outside the lanes, inside/near wall line)
    fields = []
    for phase, n in ((1, 1), (2, 2), (3, 2)):
        for _ in range(n):
            for _try in range(500):
                fx, fy = rng.randrange(4, sit.w - 10), rng.randrange(4, sit.h - 8)
                d = math.hypot(fx + 3 - cx, fy + 2 - cy)
                if 12 <= d <= 26 and fits(fx, fy, 7, 5):
                    for yy in range(fy, fy + 5):
                        for xx in range(fx, fx + 7):
                            occ[yy][xx] = 4
                    fields.append({"rect": [fx, fy, 7, 5], "phase": phase})
                    break
    grave = None
    for _try in range(500):
        gx, gy = rng.randrange(4, sit.w - 10), rng.randrange(4, sit.h - 10)
        if 14 <= math.hypot(gx + 4 - cx, gy + 4 - cy) <= 24 and fits(gx, gy, 8, 8):
            for yy in range(gy, gy + 8):
                for xx in range(gx, gx + 8):
                    occ[yy][xx] = 5
            grave = {"rect": [gx, gy, 8, 8], "phase": 3}
            break

    # wall hull BY DOCTRINE (the style's pick): none / palisade / stone;
    # stone_integrated hugs the outer buildings at margin 1 (their backs ARE
    # the wall line). Phase 1 builds ONLY the gate chokepoint stub.
    if st.walls == "none":
        return {"center": (cx, cy), "plaza": plaza, "occ": occ, "buildings": buildings,
                "fields": fields, "grave": grave, "walls": [], "towers": [],
                "gate": None, "gate_i": -1, "style": vars(st)}
    hull_margin = 1 if st.walls == "stone_integrated" else 3
    pts = [(b["rect"][0] + dx, b["rect"][1] + dy) for b in buildings
           for (dx, dy) in ((0, 0), (b["rect"][2], 0), (0, b["rect"][3]), (b["rect"][2], b["rect"][3]))]
    if st.farms == "inside":
        pts += [(f["rect"][0], f["rect"][1]) for f in fields]
    hx0, hy0 = max(1, min(p[0] for p in pts) - hull_margin), max(1, min(p[1] for p in pts) - hull_margin)
    hx1, hy1 = min(sit.w - 2, max(p[0] for p in pts) + hull_margin), min(sit.h - 2, max(p[1] for p in pts) + hull_margin)
    ring = ([(x, hy0) for x in range(hx0, hx1 + 1)] + [(hx1, y) for y in range(hy0, hy1 + 1)]
            + [(x, hy1) for x in range(hx1, hx0 - 1, -1)] + [(hx0, y) for y in range(hy1, hy0 - 1, -1)])
    walls, towers, gate = [], [], None
    gate_i = next((i for i, (x, y) in enumerate(ring) if y == hy1 and abs(x - cx) <= 1 and t[y][x] != WATER),
                  len(ring) // 2)
    for i, (x, y) in enumerate(ring):
        if t[y][x] == WATER or occ[y][x] in (2, 5):
            continue
        if abs(i - gate_i) <= 1:
            gate = (x, y)
            continue
        walls.append((x, y))
        occ[y][x] = 3
    for (tx, ty) in ((hx0, hy0), (hx1, hy0), (hx0, hy1), (hx1, hy1)):
        if t[ty][tx] != WATER:
            towers.append((tx - 1, ty - 1, 3, 3))

    return {"center": (cx, cy), "plaza": plaza, "occ": occ, "buildings": buildings,
            "fields": fields, "grave": grave, "walls": walls, "towers": towers,
            "gate": gate, "gate_i": gate_i, "style": vars(st)}


# ── render one phase: level panels + ghosted future ────────────────────────
def render_phase(sit, t, mp, phase, out_png):
    S = 7
    cx, cy = mp["center"]
    main_w, main_h = sit.w * S, sit.h * S
    panel = 46 * 3           # three sub-panels stacked, ~46px cells each? keep simple: quarter-scale level views
    sub_S = 3
    sub_w = sit.w * sub_S
    img = Image.new("RGB", (main_w + sub_w + 24, max(main_h, 5 * (sit.h * sub_S + 18)) + 40), (24, 22, 20))
    d = ImageDraw.Draw(img)

    # main surface view
    for y in range(sit.h):
        for x in range(sit.w):
            d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=TERRAIN_COLORS[t[y][x]])
    for y in range(sit.h):
        for x in range(sit.w):
            if mp["occ"][y][x] == 1:
                d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=ROAD_COLOR)
    for f in mp["fields"]:
        x0, y0, w, h = f["rect"]
        if f["phase"] <= phase:
            d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], fill=FIELD_COLOR, outline=(96, 110, 40))
        else:
            d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], outline=GHOST, width=1)
    if mp["grave"]:
        x0, y0, w, h = mp["grave"]["rect"]
        if mp["grave"]["phase"] <= phase:
            d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], fill=GRAVE_COLOR)
        else:
            d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], outline=GHOST, width=1)
    for b in mp["buildings"]:
        x0, y0, w, h = b["rect"]
        if b["phase"] <= phase:
            fill = KIND_FILL.get(b["kind"], (120, 90, 60))
            d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], fill=fill, outline=(50, 36, 22), width=2)
            tag = f"{b['floors']}F" + (f"·C{b['cellar']}" if b["cellar"] else "")
            d.text((x0 * S + 2, y0 * S + 2), tag, fill=(240, 235, 220))
        else:
            d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], outline=GHOST, width=1)
    # walls: phase 1 = gate stub only (chokepoint per the guide); phase >=3 full
    if phase >= 1 and mp["gate"]:
        gx, gy = mp["gate"]
        d.rectangle([(gx - 2) * S, gy * S, (gx + 3) * S - 1, gy * S + S - 1], fill=(150, 110, 60))
    for (x, y) in mp["walls"]:
        if phase >= 3:
            d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=WALL_COLOR)
        else:
            d.point((x * S + S // 2, y * S + S // 2), fill=GHOST)
    for (x0, y0, w, h) in mp["towers"]:
        if phase >= 3:
            d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], fill=(60, 58, 56), outline=(30, 28, 26), width=2)
        else:
            d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], outline=GHOST, width=1)
    d.ellipse([(cx - 0.5) * S, (cy - 0.5) * S, (cx + 0.5) * S, (cy + 0.5) * S], fill=(180, 60, 50))

    # LEVEL PANELS (the 3D story): L-2, L-1, F2, F3 — built-so-far only
    def level_panel(idx, title, selector):
        ox = main_w + 16
        oy = idx * (sit.h * sub_S + 18) + 4
        d.text((ox, oy), title, fill=(220, 215, 200))
        oy += 12
        d.rectangle([ox, oy, ox + sit.w * sub_S, oy + sit.h * sub_S], fill=(34, 32, 30))
        for b in mp["buildings"]:
            if b["phase"] > phase:
                continue
            x0, y0, w, h = b["rect"]
            show, style = selector(b)
            if not show:
                continue
            fill = KIND_FILL.get(b["kind"], (120, 90, 60)) if style == "solid" else (70, 66, 60)
            d.rectangle([ox + x0 * sub_S, oy + y0 * sub_S,
                         ox + (x0 + w) * sub_S, oy + (y0 + h) * sub_S], fill=fill, outline=(20, 18, 16))
            if style == "cellar":
                # beams every 3 tiles (the guide's cave-in rule) drawn as ticks
                for bx in range(x0 + 2, x0 + w - 1, 3):
                    d.line([ox + bx * sub_S, oy + y0 * sub_S, ox + bx * sub_S, oy + (y0 + h) * sub_S],
                           fill=(150, 120, 80))
                # stair shaft marker (corner)
                d.rectangle([ox + x0 * sub_S, oy + y0 * sub_S, ox + x0 * sub_S + 4, oy + y0 * sub_S + 4],
                            fill=(220, 200, 120))

    level_panel(0, "LEVEL -2 (deep cellar: cold store, walled, beamed)",
                lambda b: (b["cellar"] >= 2, "cellar"))
    level_panel(1, "LEVEL -1 (cellars: stairs down, full wall ring)",
                lambda b: (b["cellar"] >= 1, "cellar"))
    level_panel(2, "FLOOR 2 (upper storeys on load-bearing walls)",
                lambda b: (b["floors"] >= 2, "solid"))
    level_panel(3, "FLOOR 3 (keep towers; stability inherited upward)",
                lambda b: (b["floors"] >= 3, "solid"))

    names = {1: "PHASE 1 · FOUNDING", 2: "PHASE 2 · ESTABLISHMENT", 3: "PHASE 3 · FORTRESS", 4: "PHASE 4 · ENDGAME"}
    built = sum(1 for b in mp["buildings"] if b["phase"] <= phase)
    d.text((6, main_h + 10),
           f"VillageForge v2 · {names[phase]} · seed {sit.seed} · {sit.biome} · built {built}/{len(mp['buildings'])} "
           f"(ghost outlines = RESERVED for later phases; tags: floors F / cellar C-depth)",
           fill=(230, 225, 210))
    img.save(out_png)


def phase_plan(mp, phase):
    """The NEW work this phase adds (the port unit for the mod)."""
    items = []
    for b in mp["buildings"]:
        if b["phase"] != phase:
            continue
        item = {"kind": b["kind"], "rect": b["rect"], "floors": b["floors"],
                "cellar_depth": b["cellar"], "walls": "wood_wall_element",
                "door": "wood_door", "floor": "wood_floor"}
        if b["cellar"]:
            item["cellar"] = {"dig_levels": b["cellar"], "stair_shaft": True,
                              "full_wall_ring": True, "beam_every": 3}
        if b["floors"] > 1:
            item["upper_floors"] = {"count": b["floors"] - 1, "beams": "wood_beam",
                                    "stairs": "wood_stair_straight"}
        items.append(item)
    for f in mp["fields"]:
        if f["phase"] == phase:
            items.append({"kind": "field", "rect": f["rect"]})
    if mp["grave"] and mp["grave"]["phase"] == phase:
        items.append({"kind": "graveyard", "rect": mp["grave"]["rect"]})
    if phase == 1 and mp["gate"]:
        items.append({"kind": "gate_chokepoint_stub", "cell": list(mp["gate"])})
    if phase == 3:
        items.append({"kind": "wall_ring", "cells": len(mp["walls"]), "id": "wood_wall_element"})
        items.append({"kind": "towers", "count": len(mp["towers"])})
    return items


STYLE_PRESETS = {
    "martial_hill_clan": Style(density="town", walls="stone_integrated", layout="grid",
                               verticality="high", farms="outside", keep=True,
                               name="martial hill clan (tight stone, towers)"),
    "pastoral_hamlet": Style(density="hamlet", walls="none", layout="ring",
                             verticality="low", farms="inside", keep=False,
                             name="pastoral hamlet (scattered, open, fields within)"),
    "market_village": Style(density="village", walls="palisade", layout="crossroads",
                            verticality="mid", farms="outside", keep=True,
                            name="market village (crossroads, palisade)"),
}


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--biome", default="valley", choices=("marsh", "valley", "hillside"))
    ap.add_argument("--style", default="market_village", choices=list(STYLE_PRESETS) + ["all"])
    ap.add_argument("--phase", type=int, default=0, help="0 = all phases")
    ap.add_argument("--from-game", action="store_true",
                    help="use the mod's worldmap_grid.txt export instead of synthetic terrain")
    ap.add_argument("--out", default="samples_v2")
    args = ap.parse_args()
    styles = STYLE_PRESETS if args.style == "all" else {args.style: STYLE_PRESETS[args.style]}
    os.makedirs(args.out, exist_ok=True)
    for sname, style in styles.items():
        sit = Situation(args.seed, args.biome, style=style)
        t = load_game_terrain(sit) if args.from_game else synth_terrain(sit)
        mp = master_plan(sit, t)
        phases = (1, 2, 3, 4) if args.phase == 0 else (args.phase,)
        for phase in phases:
            png = os.path.join(args.out, f"village_seed{args.seed}_{args.biome}_{sname}_phase{phase}.png")
            render_phase(sit, t, mp, phase, png)
            with open(os.path.join(args.out, f"plan_seed{args.seed}_{args.biome}_{sname}_phase{phase}.json"),
                      "w", encoding="utf-8") as f:
                json.dump({"phase": phase, "style": vars(style), "new_work": phase_plan(mp, phase)}, f, indent=1)
            print("rendered", png)


if __name__ == "__main__":
    main()

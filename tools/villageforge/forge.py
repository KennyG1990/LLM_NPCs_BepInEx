"""VillageForge — offline coherent-village generator (Ken's directive 2026-07-13).

"Design some kind of system that works OUT of the game that can generate random
coherent villages under the same context as in the game, then port it over.
Input mimics the game situations; output we can SEE and validate."

INPUT  (Situation): seed, stage 1-4, population, biome terrain (water/forest/rock
        percentages mimic marsh/valley/hillside), map size.
OUTPUT: village.png (the thing Ken judges) + plan.json (ordered blueprint list in
        the mod's actuator vocabulary: wood_wall_element/wood_door/wood_floor/
        building ids + cells — the port format).

DESIGN LAWS (from Ken's reference screenshots):
 - The village REASONS TOWARD THE ENDGAME: stage 1 already lays the street grid
   and plaza that stage 4's walled town will need. No throwaway shacks.
 - Streets first, buildings on lots along streets, doors facing the road.
 - Variety: houses differ in size and orientation; communal/production/farm/
   graveyard districts; wall ring with towers and a gate at stage 4.
 - Terrain is law: nothing on water; forest lots are marked for clearing;
   water gaps in the wall line are the moat doing the wall's job.
"""

import argparse
import json
import math
import os
import random

from PIL import Image, ImageDraw

# terrain classes
WATER, GRASS, FOREST, ROCK = 0, 1, 2, 3
# occupancy
FREE, ROAD, BUILDING, WALL, FIELD, YARD, GRAVE = 0, 1, 2, 3, 4, 5, 6

TERRAIN_COLORS = {WATER: (58, 96, 138), GRASS: (116, 150, 84), FOREST: (74, 104, 58), ROCK: (128, 126, 118)}
ROAD_COLOR = (168, 148, 110)
FIELD_COLOR = (150, 160, 70)
YARD_COLOR = (140, 124, 92)
GRAVE_COLOR = (100, 110, 90)
WALL_COLOR = (72, 70, 66)
TOWER_COLOR = (60, 58, 56)
HOUSE_FILL = (122, 88, 54)
HOUSE_EDGE = (58, 40, 22)
COMMUNAL_FILL = (140, 104, 62)
PRODUCTION_FILL = (104, 80, 60)
ROOF_LINE = (86, 60, 34)


class Situation:
    def __init__(self, seed, stage, pop, biome, w=120, h=120):
        self.seed, self.stage, self.pop, self.biome = seed, stage, pop, biome
        self.w, self.h = w, h

    def label(self):
        return f"seed {self.seed} · stage {self.stage} · pop {self.pop} · {self.biome}"


def synth_terrain(sit):
    """Blobby terrain mimicking the game's biomes."""
    rng = random.Random(sit.seed * 7919)
    t = [[GRASS] * sit.w for _ in range(sit.h)]
    water_blobs = {"marsh": 26, "valley": 6, "hillside": 4}[sit.biome]
    forest_blobs = {"marsh": 8, "valley": 12, "hillside": 10}[sit.biome]
    rock_blobs = {"marsh": 2, "valley": 3, "hillside": 9}[sit.biome]

    def blob(cls, count, rmin, rmax):
        for _ in range(count):
            cx, cy = rng.randrange(sit.w), rng.randrange(sit.h)
            r = rng.randint(rmin, rmax)
            for y in range(max(0, cy - r), min(sit.h, cy + r)):
                for x in range(max(0, cx - r), min(sit.w, cx + r)):
                    d = math.hypot(x - cx, y - cy)
                    if d < r * (0.7 + 0.3 * rng.random()):
                        t[y][x] = cls

    blob(FOREST, forest_blobs, 5, 14)
    blob(ROCK, rock_blobs, 3, 8)
    blob(WATER, water_blobs, 4, 13)
    if sit.biome == "valley":                      # a river band
        ry = sit.h // 3 + rng.randint(-8, 8)
        for x in range(sit.w):
            ry = min(sit.h - 3, max(2, ry + rng.choice((-1, 0, 0, 1))))
            for dy in range(-2, 3):
                t[ry + dy][x] = WATER
    return t


def find_center(sit, t):
    """Most-open dry cell: maximize dry radius (the site scorer, offline)."""
    best, bx, by = -1, sit.w // 2, sit.h // 2
    rng = random.Random(sit.seed * 31)
    for _ in range(600):
        x = rng.randrange(12, sit.w - 12)
        y = rng.randrange(12, sit.h - 12)
        if t[y][x] != GRASS:
            continue
        r = 0
        while r < 18:
            r += 1
            ok = True
            for a in range(0, 360, 30):
                px = int(x + r * math.cos(math.radians(a)))
                py = int(y + r * math.sin(math.radians(a)))
                if not (0 <= px < sit.w and 0 <= py < sit.h) or t[py][px] == WATER:
                    ok = False
                    break
            if not ok:
                break
        if r > best:
            best, bx, by = r, x, y
    return bx, by


def clear_for(t, occ, x0, y0, w, h, allow_forest=True):
    """A lot is usable when every cell is non-water, unoccupied."""
    for y in range(y0, y0 + h):
        for x in range(x0, x0 + w):
            if not (0 <= x < len(t[0]) and 0 <= y < len(t)):
                return False
            if t[y][x] == WATER or (t[y][x] == FOREST and not allow_forest) or t[y][x] == ROCK:
                return False
            if occ[y][x] != FREE:
                return False
    return True


def stamp(occ, x0, y0, w, h, kind):
    for y in range(y0, y0 + h):
        for x in range(x0, x0 + w):
            occ[y][x] = kind


def generate(sit):
    rng = random.Random(sit.seed)
    t = synth_terrain(sit)
    occ = [[FREE] * sit.w for _ in range(sit.h)]
    plan = []
    cx, cy = find_center(sit, t)

    # ── plaza + streets (laid at stage 1 — the endgame skeleton) ──────────
    extent = 10 + sit.stage * 7
    plaza = 3 if sit.stage < 3 else 4
    stamp(occ, cx - plaza, cy - plaza, plaza * 2 + 1, plaza * 2 + 1, ROAD)
    plan.append({"kind": "plaza", "rect": [cx - plaza, cy - plaza, plaza * 2 + 1, plaza * 2 + 1]})
    for d in range(plaza + 1, extent):                     # N-S / E-W roads, stop at water
        for (dx, dy) in ((d, 0), (-d, 0), (0, d), (0, -d)):
            x, y = cx + dx, cy + dy
            if 0 <= x < sit.w - 1 and 0 <= y < sit.h - 1 and t[y][x] != WATER:
                stamp(occ, x, y, 2 if dy == 0 else 1, 2 if dx == 0 else 1, ROAD)

    buildings = []

    def try_place(w, h, kind, name, fill, near_road=True, ring=(2, extent), forest_ok=False):
        for _ in range(900):
            ang = rng.random() * math.tau
            r = rng.uniform(*ring)
            x0 = int(cx + r * math.cos(ang)) - w // 2
            y0 = int(cy + r * math.sin(ang)) - h // 2
            if not clear_for(t, occ, x0 - 1, y0 - 1, w + 2, h + 2, allow_forest=forest_ok):
                continue
            if near_road:
                touches = any(
                    occ[yy][xx] == ROAD
                    for yy in range(max(0, y0 - 2), min(sit.h, y0 + h + 2))
                    for xx in range(max(0, x0 - 2), min(sit.w, x0 + w + 2)))
                if not touches:
                    continue
            stamp(occ, x0, y0, w, h, kind)
            buildings.append({"name": name, "rect": [x0, y0, w, h], "fill": fill, "kind": kind})
            plan.append({"kind": name, "rect": [x0, y0, w, h],
                         "walls": "wood_wall_element", "door": "wood_door", "floor": "wood_floor"})
            return True
        return False

    # ── communal core (stage-gated) ───────────────────────────────────────
    if sit.stage >= 2:
        try_place(10, 7, BUILDING, "great_hall", COMMUNAL_FILL, ring=(plaza + 2, plaza + 9))
    if sit.stage >= 3:
        try_place(6, 9, BUILDING, "church", COMMUNAL_FILL, ring=(plaza + 4, extent - 3))
        # graveyard at the edge of town
        for _ in range(300):
            gx = int(cx + (extent - 4) * math.cos(rng.random() * math.tau)) - 4
            gy = int(cy + (extent - 4) * math.sin(rng.random() * math.tau)) - 4
            if clear_for(t, occ, gx, gy, 9, 9):
                stamp(occ, gx, gy, 9, 9, GRAVE)
                plan.append({"kind": "graveyard", "rect": [gx, gy, 9, 9]})
                break

    # ── houses: varied sizes/orientations, count grows with pop ──────────
    houses_needed = max(2, math.ceil(sit.pop / 2))
    placed_houses = 0
    for _ in range(houses_needed * 3):
        if placed_houses >= houses_needed:
            break
        w = rng.randint(5, 8)
        h = rng.randint(5, 9)
        if rng.random() < 0.5:
            w, h = h, w
        if try_place(w, h, BUILDING, "house", HOUSE_FILL, ring=(plaza + 2, extent - 2)):
            placed_houses += 1

    # ── production district + farms ───────────────────────────────────────
    try_place(5, 6, BUILDING, "workshop", PRODUCTION_FILL, ring=(plaza + 2, plaza + 10))
    try_place(4, 5, BUILDING, "butcher+smokehouse", PRODUCTION_FILL, ring=(plaza + 4, extent - 2))
    if sit.stage >= 2:
        try_place(7, 5, BUILDING, "barn", PRODUCTION_FILL, ring=(plaza + 5, extent - 2))
        for _ in range(2 + sit.stage):
            for _try in range(400):
                fx = int(cx + rng.uniform(extent - 8, extent + 6) * math.cos(rng.uniform(-0.9, 0.9) + math.pi / 2))
                fy = int(cy + rng.uniform(extent - 8, extent + 6) * math.sin(rng.uniform(-0.9, 0.9) + math.pi / 2))
                if clear_for(t, occ, fx, fy, 7, 5):
                    stamp(occ, fx, fy, 7, 5, FIELD)
                    plan.append({"kind": "field", "rect": [fx, fy, 7, 5]})
                    break
    # stockpile yard by the plaza
    for _try in range(300):
        yx = int(cx + (plaza + 3) * math.cos(rng.random() * math.tau)) - 2
        yy = int(cy + (plaza + 3) * math.sin(rng.random() * math.tau)) - 2
        if clear_for(t, occ, yx, yy, 5, 5):
            stamp(occ, yx, yy, 5, 5, YARD)
            plan.append({"kind": "stockpile_yard", "rect": [yx, yy, 5, 5]})
            break

    # ── stage 4: wall ring + towers + gate; water gaps are the moat ──────
    walls = []
    towers = []
    gate = None
    if sit.stage >= 4:
        # hull = bounding box of everything built, +3 margin
        xs = [b["rect"][0] for b in buildings] + [cx]
        ys = [b["rect"][1] for b in buildings] + [cy]
        xe = [b["rect"][0] + b["rect"][2] for b in buildings] + [cx]
        ye = [b["rect"][1] + b["rect"][3] for b in buildings] + [cy]
        x0, y0 = max(1, min(xs) - 3), max(1, min(ys) - 3)
        x1, y1 = min(sit.w - 2, max(xe) + 3), min(sit.h - 2, max(ye) + 3)
        ring = ([(x, y0) for x in range(x0, x1 + 1)] + [(x1, y) for y in range(y0, y1 + 1)]
                + [(x, y1) for x in range(x1, x0 - 1, -1)] + [(x0, y) for y in range(y1, y0 - 1, -1)])
        gate_i = next((i for i, (x, y) in enumerate(ring)
                       if y == y1 and abs(x - cx) <= 1 and t[y][x] != WATER), len(ring) // 2)
        for i, (x, y) in enumerate(ring):
            if t[y][x] == WATER:
                continue                     # the moat serves
            if occ[y][x] in (BUILDING, GRAVE):
                continue
            if abs(i - gate_i) <= 1:
                gate = (x, y)
                plan.append({"kind": "gate", "cell": [x, y], "id": "wood_door"})
                continue
            occ[y][x] = WALL
            walls.append((x, y))
        for (tx, ty) in ((x0, y0), (x1, y0), (x0, y1), (x1, y1)):
            if t[ty][tx] != WATER:
                towers.append((tx - 1, ty - 1, 3, 3))
                plan.append({"kind": "tower", "rect": [tx - 1, ty - 1, 3, 3]})
        plan.append({"kind": "wall_ring", "cells": len(walls), "id": "wood_wall_element"})

    return t, occ, buildings, walls, towers, gate, plan, (cx, cy, plaza, extent)


def render(sit, t, occ, buildings, walls, towers, gate, meta, out_png):
    cx, cy, plaza, extent = meta
    S = 8
    img = Image.new("RGB", (sit.w * S, sit.h * S + 34), (24, 22, 20))
    d = ImageDraw.Draw(img)
    for y in range(sit.h):
        for x in range(sit.w):
            d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=TERRAIN_COLORS[t[y][x]])
            if t[y][x] == FOREST and (x * 7 + y * 13) % 3 == 0:
                d.ellipse([x * S + 2, y * S + 1, x * S + 6, y * S + 5], fill=(48, 72, 40))
    for y in range(sit.h):
        for x in range(sit.w):
            c = occ[y][x]
            if c == ROAD:
                d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=ROAD_COLOR)
            elif c == FIELD:
                d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=FIELD_COLOR)
                if x % 2 == 0:
                    d.line([x * S + 1, y * S + 1, x * S + 1, y * S + S - 2], fill=(96, 110, 40))
            elif c == YARD:
                d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=YARD_COLOR)
            elif c == GRAVE:
                d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=GRAVE_COLOR)
                if (x + y) % 2 == 0:
                    d.rectangle([x * S + 3, y * S + 2, x * S + 5, y * S + 6], fill=(180, 180, 170))
    for b in buildings:
        x0, y0, w, h = b["rect"]
        d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1],
                    fill=b["fill"], outline=HOUSE_EDGE, width=2)
        if w >= h:            # roof ridge line for the gabled look
            d.line([x0 * S + 3, (y0 + h / 2) * S, (x0 + w) * S - 3, (y0 + h / 2) * S], fill=ROOF_LINE, width=2)
        else:
            d.line([(x0 + w / 2) * S, y0 * S + 3, (x0 + w / 2) * S, (y0 + h) * S - 3], fill=ROOF_LINE, width=2)
    for (x, y) in walls:
        d.rectangle([x * S, y * S, x * S + S - 1, y * S + S - 1], fill=WALL_COLOR)
        d.rectangle([x * S + 2, y * S + 2, x * S + 4, y * S + 4], fill=(140, 138, 130))
    for (x0, y0, w, h) in towers:
        d.rectangle([x0 * S, y0 * S, (x0 + w) * S - 1, (y0 + h) * S - 1], fill=TOWER_COLOR, outline=(30, 28, 26), width=2)
    if gate:
        gx, gy = gate
        d.rectangle([gx * S - S, gy * S, gx * S + 2 * S - 1, gy * S + S - 1], fill=(150, 110, 60))
    d.ellipse([(cx - 0.5) * S, (cy - 0.5) * S, (cx + 0.5) * S, (cy + 0.5) * S], fill=(180, 60, 50))  # the well/hearth
    d.text((6, sit.h * S + 8), f"VillageForge · {sit.label()} · houses={sum(1 for b in buildings if b['name']=='house')} "
           f"walls={len(walls)} towers={len(towers)}", fill=(230, 225, 210))
    img.save(out_png)
    return out_png


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--stage", type=int, default=2, choices=(1, 2, 3, 4))
    ap.add_argument("--pop", type=int, default=6)
    ap.add_argument("--biome", default="valley", choices=("marsh", "valley", "hillside"))
    ap.add_argument("--out", default=".")
    args = ap.parse_args()
    sit = Situation(args.seed, args.stage, args.pop, args.biome)
    t, occ, buildings, walls, towers, gate, plan, meta = generate(sit)
    os.makedirs(args.out, exist_ok=True)
    tag = f"s{args.seed}_st{args.stage}_p{args.pop}_{args.biome}"
    png = render(sit, t, occ, buildings, walls, towers, gate, meta, os.path.join(args.out, f"village_{tag}.png"))
    with open(os.path.join(args.out, f"plan_{tag}.json"), "w", encoding="utf-8") as f:
        json.dump({"situation": vars(sit), "plan": plan}, f, indent=1)
    print(f"rendered {png} · buildings={len(buildings)} plan_items={len(plan)}")


if __name__ == "__main__":
    main()

using System;
using System.Collections.Generic;
using System.Text;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// SITE SCORER — the deterministic half of "where do we build?" (Ken: geometry
    /// is math, the CHOICE is creativity). It takes a PREFERENCE (emitted by the
    /// elected leader's LLM voice — near the forest, up high, away from the others)
    /// and finds the real buildable sites that best satisfy it. The geometry is
    /// always sound (never a house in the river); the weighting is expressive, so
    /// the answer differs with who's leading and what they value.
    ///
    /// Consumes WorldMap (must be Scan()'d): Surface / Cls / CellarBelow + Min/Max
    /// Surface. Pure engine — no LLM, no game API. The leader-voice call (next unit)
    /// produces the Preference and picks from the returned shortlist.
    /// </summary>
    public static class SiteScorer
    {
        /// <summary>Per-factor weight, -1..+1. + = want more, - = avoid, 0 = don't care.
        /// This is exactly what the leader's LLM call emits.</summary>
        public struct Preference
        {
            public float NearHome, Forest, FertileSoil, Water, Stone, HighGround, Openness, Privacy, Cellar;
        }

        public struct Site
        {
            public int X, Y, Z, PadSize;
            public float Score;
            public string Reason;
        }

        public static string LastResult = "(idle)";

        // Bounded search radius for "distance to nearest <feature>" (cells).
        private const int FeatureR = 20;

        /// <summary>Rank buildable sites by how well they satisfy the preference.
        /// footprint = required flat, dry, buildable pad edge (e.g. 12 for a real home).
        /// homeX/homeZ = existing home for near/privacy factors (-1 = ignore).</summary>
        /// <summary>Max distance (tiles) a chosen build site may be from the
        /// current camp — a short walk, keeps the colony ONE place.</summary>
        public const int MaxLeash = 35;

        public static List<Site> FindSites(Preference pref, int footprint = 12, int topN = 3, int homeX = -1, int homeZ = -1)
        {
            var sites = new List<Site>();
            try
            {
                if (WorldMap.Surface == null || WorldMap.Cls == null)
                { LastResult = "sites: worldmap not scanned"; return sites; }

                int sx = WorldMap.SizeX, sz = WorldMap.SizeZ;
                float span = Math.Max(1, WorldMap.MaxSurface - WorldMap.MinSurface);
                float maxDist = (float)Math.Sqrt((double)sx * sx + (double)sz * sz);
                int stride = Math.Max(2, footprint / 2);

                foreach (var s in EnumerateCandidates(sx, sz, footprint, stride))
                {
                    int cx = s.Item1, cz = s.Item2, pad = s.Item3;
                    int surf = WorldMap.Surface[cx, cz];
                    if (surf < 0) continue;

                    // HARD LEASH (Ken: "they build their house a mile away from
                    // their stockpile"): near_home was only a soft weight the
                    // leader could zero out — sites 100+ tiles from camp won and
                    // the colony split (endless hauling, exhaustion, idling).
                    // A real leader relocates the village a SHORT walk, not a
                    // day's march. Taste still picks WITHIN the leash.
                    if (homeX >= 0 && homeZ >= 0)
                    {
                        int lx = cx - homeX, lz = cz - homeZ;
                        if (lx * lx + lz * lz > MaxLeash * MaxLeash) continue;
                    }

                    float highGround = (surf - WorldMap.MinSurface) / span;
                    float forest = NearFeature(cx, cz, WorldMap.CLS_TREE);
                    float water = NearFeature(cx, cz, WorldMap.CLS_WATER);
                    float stone = NearFeature(cx, cz, WorldMap.CLS_ROCK);
                    float fertile = LocalFraction(cx, cz, WorldMap.CLS_OPEN, 6);   // grass/topsoil density ~ farmland
                    float openness = LocalFraction(cx, cz, WorldMap.CLS_OPEN, 10); // room to grow
                    float cellar = Math.Min(1f, WorldMap.CellarBelow[cx, cz] / 6f);
                    float nearHome = 0f, privacy = 0f;
                    if (homeX >= 0 && homeZ >= 0)
                    {
                        float d = (float)Math.Sqrt((cx - homeX) * (cx - homeX) + (cz - homeZ) * (cz - homeZ)) / maxDist;
                        nearHome = 1f - Math.Min(1f, d);
                        privacy = Math.Min(1f, d);
                    }

                    float score =
                        pref.NearHome * nearHome + pref.Forest * forest + pref.FertileSoil * fertile +
                        pref.Water * water + pref.Stone * stone + pref.HighGround * highGround +
                        pref.Openness * openness + pref.Privacy * privacy + pref.Cellar * cellar;

                    var reason = BuildReason(pad, surf, highGround, forest, water, stone, fertile, openness, cellar, nearHome, privacy, pref);
                    sites.Add(new Site { X = cx, Y = surf, Z = cz, PadSize = pad, Score = score, Reason = reason });
                }

                sites.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (sites.Count > topN) sites = sites.GetRange(0, topN);
                LastResult = sites.Count > 0
                    ? $"sites: {sites.Count} candidate(s), best @({sites[0].X},{sites[0].Y},{sites[0].Z}) pad{sites[0].PadSize} score{sites[0].Score:F2}"
                    : $"sites: NONE fit a {footprint}x{footprint} flat dry pad";
                return sites;
            }
            catch (Exception ex) { LastResult = "sites EXC: " + (ex.InnerException?.Message ?? ex.Message); return sites; }
        }

        // Candidate anchors: coarse stride; each must anchor a flat, dry, buildable
        // footprint pad. Yields (cx, cz, padEdge).
        private static IEnumerable<Tuple<int, int, int>> EnumerateCandidates(int sx, int sz, int footprint, int stride)
        {
            for (int cx = 0; cx + footprint < sx; cx += stride)
                for (int cz = 0; cz + footprint < sz; cz += stride)
                {
                    int pad = FlatDryPad(cx, cz, footprint);
                    if (pad >= footprint) yield return Tuple.Create(cx + footprint / 2, cz + footprint / 2, pad);
                }
        }

        // Largest square edge (up to 'want') at (x,z) where every cell is open-buildable
        // and dry and level within +/-1 of the anchor surface. 0 if the anchor itself
        // isn't buildable.
        private static int FlatDryPad(int x, int z, int want)
        {
            int baseSurf = WorldMap.Surface[x, z];
            if (baseSurf < 0 || WorldMap.Cls[x, z] != WorldMap.CLS_OPEN) return 0;
            int edge = 0;
            for (int e = 1; e <= want; e++)
            {
                bool ok = true;
                for (int dx = 0; dx < e && ok; dx++)
                    for (int dz = 0; dz < e; dz++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if (nx >= WorldMap.SizeX || nz >= WorldMap.SizeZ) { ok = false; break; }
                        if (WorldMap.Cls[nx, nz] != WorldMap.CLS_OPEN) { ok = false; break; }
                        int s = WorldMap.Surface[nx, nz];
                        if (s < 0 || Math.Abs(s - baseSurf) > 1) { ok = false; break; }
                    }
                if (ok) edge = e; else break;
            }
            return edge;
        }

        // 1.0 if the feature is adjacent, decaying to 0 at FeatureR; 0 if none within R.
        private static float NearFeature(int x, int z, byte cls)
        {
            int best = int.MaxValue;
            for (int dz = -FeatureR; dz <= FeatureR; dz++)
                for (int dx = -FeatureR; dx <= FeatureR; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= WorldMap.SizeX || nz >= WorldMap.SizeZ) continue;
                    if (WorldMap.Cls[nx, nz] != cls) continue;
                    int d = Math.Abs(dx) + Math.Abs(dz);
                    if (d < best) best = d;
                }
            if (best == int.MaxValue) return 0f;
            return 1f - Math.Min(1f, best / (float)FeatureR);
        }

        // Fraction of cells of class 'cls' within radius r (density proxy).
        private static float LocalFraction(int x, int z, byte cls, int r)
        {
            int hit = 0, total = 0;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= WorldMap.SizeX || nz >= WorldMap.SizeZ) continue;
                    total++;
                    if (WorldMap.Cls[nx, nz] == cls) hit++;
                }
            return total > 0 ? hit / (float)total : 0f;
        }

        // Human-readable reason naming the factors this site satisfies that the leader
        // actually asked for (weight != 0), strongest first.
        private static string BuildReason(int pad, int surf, float high, float forest, float water, float stone,
            float fertile, float open, float cellar, float near, float privacy, Preference p)
        {
            var parts = new List<Tuple<float, string>>();
            void Add(float w, float v, string want, string avoid)
            { if (Math.Abs(w) > 0.01f) parts.Add(Tuple.Create(Math.Abs(w) * (w > 0 ? v : 1 - v), w > 0 ? want : avoid)); }
            Add(p.HighGround, high, $"high ground(+{surf})", "low-lying");
            Add(p.Forest, forest, "near forest", "clear of woods");
            Add(p.Water, water, "near water", "away from water");
            Add(p.Stone, stone, "near stone", "away from rock");
            Add(p.FertileSoil, fertile, "good farmland", "");
            Add(p.Openness, open, "room to expand", "");
            Add(p.Cellar, cellar, "deep cellar rock", "");
            Add(p.NearHome, near, "close to home", "");
            Add(p.Privacy, privacy, "private/remote", "");
            parts.Sort((a, b) => b.Item1.CompareTo(a.Item1));
            var sb = new StringBuilder($"flat {pad}x{pad} pad; ");
            int n = 0;
            foreach (var pt in parts) { if (string.IsNullOrEmpty(pt.Item2)) continue; sb.Append(pt.Item2); if (++n >= 3) break; sb.Append(", "); }
            return sb.ToString().TrimEnd(',', ' ');
        }
    }
}

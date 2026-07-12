using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// EQUIP LOGIC (Ken, high-return survival fix). Job assignment implies gear:
    /// a settler told to HUNT with no ranged weapon is a dead link (the overnight
    /// starvation run — hunters assigned, bows sitting unused in the stockpile).
    /// This is the programmatic "right-click a bow -> Equip" for capable hunters.
    ///
    /// GAME-LEGAL chain (decompiled ground truth — NO stat cheat, Ken principle
    /// "force the REAL action"):
    ///   pile.IsForbidden = false;
    ///   pile.equipTarget = humanoid;                 // private field (reflection)
    ///   humanoid.Inventory.AddEquipOrder(pile);       // public
    ///   => WorkerGoapAgent auto-fires "EquipGoal" once EquipOrders is non-empty
    ///      (NSMedieval_Goap_WorkerGoapAgent.cs:223-236). The settler then walks
    ///      to the pile and equips it. Mirrors ResourcePileInstance.OnEquipOrder:1285.
    ///
    /// Detect: settler is a hunter (ActiveJobCombination & JobType.Hunting(0x20),
    ///   or has the Marksman skill) AND has no ranged weapon AND no pending equip
    ///   order. Ranged = an equipped Weapon whose ActiveWeaponMode.AttackType != Melee
    ///   (AttackType = {Melee, RangeChargeBefore, RangeChargeAfter}).
    /// Find: a ranged-weapon pile stored on a stockpile, unreserved (equipTarget==null),
    ///   via ResourcePileManager.AllPileInstances (proven in ResourceUnforbidder);
    ///   a pile is a ranged weapon if Repository&lt;EquipmentRepository,Equipment&gt;
    ///   .GetByID(pileResourceId) is a Weapon with a ranged primary mode.
    ///
    /// Idempotent + reload-safe: equip orders are transient game state re-derived
    /// each pass from world truth. Equipped/pending settlers self-skip. Fully
    /// null-safe: any missing type/pile => no-op, never throws.
    /// </summary>
    public static class EquipManager
    {
        public static string LastResult = "(idle)";
        public static int LastHuntersMissingWeapon = 0;   // read by ColonyAlerts
        private const int MaxEquipsPerPass = 4;
        private const long JobTypeHunting = 0x20;

        private static Type _pileMgrType;
        private static Type _repoOpenGeneric, _equipRepoType, _equipmentType;

        // ── reflection helpers (mirror ResourceUnforbidder / JobRouter) ──
        private static Type FindTypeByName(string shortName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            { try { foreach (var t in asm.GetTypes()) if (t.Name == shortName) return t; } catch { } }
            return null;
        }

        private static object SingletonInstance(Type t)
        {
            for (var cur = t; cur != null; cur = cur.BaseType)
            {
                var p = cur.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (p != null) { try { var v = p.GetValue(null, null); if (v != null) return v; } catch { } }
            }
            return null;
        }

        // hierarchy-walk property/field getter (from JobRouter.HGet)
        private static object HGet(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null) { try { var v = p.GetValue(o, null); if (v != null) return v; } catch { } }
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) { try { var v = f.GetValue(o); if (v != null) return v; } catch { } }
            }
            return null;
        }

        private static bool SetPrivateField(object o, string name, object value)
        {
            if (o == null) return false;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) { try { f.SetValue(o, value); return true; } catch { return false; } }
            }
            return false;
        }

        /// <summary>Repository&lt;EquipmentRepository, Equipment&gt;.Instance.GetByID(id).
        /// Returns the Equipment blueprint for a resource id, or null if the resource
        /// is not an equippable item.</summary>
        private static object GetEquipmentBlueprint(string resourceId)
        {
            if (string.IsNullOrEmpty(resourceId)) return null;
            try
            {
                _equipRepoType = _equipRepoType ?? FindTypeByName("EquipmentRepository");
                _equipmentType = _equipmentType ?? FindTypeByName("Equipment");
                if (_equipRepoType == null || _equipmentType == null) return null;
                if (_repoOpenGeneric == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    { try { foreach (var t in asm.GetTypes()) if (t.Name == "Repository`2") { _repoOpenGeneric = t; break; } } catch { } if (_repoOpenGeneric != null) break; }
                }
                if (_repoOpenGeneric == null) return null;
                var closed = _repoOpenGeneric.MakeGenericType(_equipRepoType, _equipmentType);
                var inst = SingletonInstance(closed);
                var getById = closed.GetMethod("GetByID", BindingFlags.Public | BindingFlags.Instance)
                              ?? _equipRepoType.GetMethod("GetByID", BindingFlags.Public | BindingFlags.Instance);
                if (inst == null || getById == null) return null;
                return getById.Invoke(inst, new object[] { resourceId });
            }
            catch { return null; }
        }

        // A weapon Equipment (blueprint) whose primary mode is ranged.
        // AttackType lives one hop deeper than the mode: WeaponMode
        // .WeaponTypeSettings.AttackType (decompiled WeaponMode:212-266 — the
        // direct HGet(mode,"AttackType") read null on every real weapon,
        // which hid 4 crafted ranged weapons on 2026-07-11).
        private static object ModeAttackType(object mode) =>
            mode == null ? null : (HGet(HGet(mode, "WeaponTypeSettings"), "AttackType") ?? HGet(mode, "AttackType"));

        private static bool IsRangedWeaponEquipment(object equipment)
        {
            if (equipment == null) return false;
            try
            {
                var itemType = HGet(equipment, "ItemType");
                if (itemType == null || itemType.ToString() != "Weapon") return false;
                var at = ModeAttackType(HGet(equipment, "PrimaryWeaponMode"));
                if (at == null) return false;
                return at.ToString() != "Melee";   // RangeChargeBefore / RangeChargeAfter
            }
            catch { return false; }
        }

        // Does the settler already have a ranged weapon equipped?
        private static bool HasRangedWeapon(object model)
        {
            try
            {
                var getEquip = model?.GetType().GetMethod("GetEquipment", BindingFlags.Public | BindingFlags.Instance);
                var equips = getEquip?.Invoke(model, null) as IEnumerable;
                if (equips == null) return false;
                foreach (var eq in equips)
                {
                    if (eq == null) continue;
                    var bp = HGet(eq, "Blueprint");
                    var itemType = bp != null ? HGet(bp, "ItemType") : null;
                    if (itemType == null || itemType.ToString() != "Weapon") continue;
                    var at = ModeAttackType(HGet(eq, "ActiveWeaponMode") ?? HGet(HGet(eq, "Blueprint"), "PrimaryWeaponMode"));
                    if (at != null && at.ToString() != "Melee") return true;
                }
                return false;
            }
            catch { return false; }
        }

        private static bool IsHunter(object model)
        {
            // (a) currently hunting
            try
            {
                var wb = HGet(model, "WorkerBehaviour");
                var active = wb != null ? HGet(wb, "ActiveJobCombination") : null;
                if (active != null && (Convert.ToInt64(active) & JobTypeHunting) != 0) return true;
            }
            catch { }
            // (b) capable hunter — has the Marksman skill
            try
            {
                var skillsOwner = HGet(model, "Skills") ?? HGet(model, "WorkerSkills");
                var list = skillsOwner != null ? HGet(skillsOwner, "Skills") as IEnumerable : null;
                if (list != null)
                    foreach (var sk in list)
                    {
                        var sn = HGet(sk, "Id")?.ToString();
                        if (sn != null && sn.Replace(" ", "").Equals("Marksman", StringComparison.OrdinalIgnoreCase)) return true;
                    }
            }
            catch { }
            return false;
        }

        private static bool HasPendingEquipOrder(object inventory)
        {
            try
            {
                var orders = HGet(inventory, "EquipOrders") as ICollection;
                return orders != null && orders.Count > 0;
            }
            catch { return false; }
        }

        // Snapshot of unreserved ranged-weapon piles — STOCKPILE-STORED FIRST,
        // then GROUND piles. v1 only counted stored piles and reported "NONE in
        // stockpile" while weapons lay in plain sight on the ground (Ken watched
        // TWO colonies die around unclaimed arms). A weapon you can walk to IS
        // a weapon. Also counts melee piles so the report never again claims
        // an empty armory that isn't.
        public static int LastRangedStored, LastRangedGround, LastMeleeSeen;
        // DIAG (sling invisible 2026-07-11 19:20): a crafted sling reached
        // TargetReached yet the census read ranged 0 — log WHY a known-ranged
        // id fails classification, once per id per session.
        private static readonly string[] _knownRangedIds =
            { "sling", "sling_staff", "short_bow", "war_bow", "long_bow", "curved_bow", "light_crossbow", "crossbow", "heavy_crossbow" };
        private static readonly HashSet<string> _diagLogged = new HashSet<string>();
        private static List<object> FindRangedWeaponPiles()
        {
            var storedList = new List<object>(); var groundList = new List<object>();
            LastRangedStored = LastRangedGround = LastMeleeSeen = 0;
            try
            {
                _pileMgrType = _pileMgrType ?? FindTypeByName("ResourcePileManager");
                var mgr = _pileMgrType != null ? SingletonInstance(_pileMgrType) : null;
                var piles = (_pileMgrType?.GetProperty("AllPileInstances")?.GetValue(mgr, null)
                             ?? _pileMgrType?.GetProperty("AllPiles")?.GetValue(mgr, null)) as IEnumerable;
                if (piles == null) return storedList;
                foreach (var pile in piles)
                {
                    if (pile == null) continue;
                    try
                    {
                        if (HGet(pile, "equipTarget") != null) continue;   // already reserved
                        var bp = HGet(pile, "Blueprint");
                        var id = bp?.GetType().GetMethod("GetID", BindingFlags.Public | BindingFlags.Instance)?.Invoke(bp, null) as string;
                        if (id == null) continue;
                        bool knownRanged = false;
                        foreach (var kr in _knownRangedIds)
                            if (id == kr || id.EndsWith("_" + kr, StringComparison.OrdinalIgnoreCase)) { knownRanged = true; break; }
                        // QUALITY VARIANTS (ground truth ProductionStepSpawnProduct:218):
                        // quality items spawn as '<quality>_<id>' piles (good_sling)
                        // — EquipmentRepository.GetByID misses them, which made 2
                        // crafted slings invisible (2026-07-11 19:40, worldCount=2
                        // vs ranged 0). The Resource blueprint carries its OWN
                        // EquipmentBlueprint (the game's CheckAchievements uses it)
                        // — read that first, repo lookup only as fallback.
                        var eq = HGet(bp, "EquipmentBlueprint") ?? GetEquipmentBlueprint(id);
                        if (eq == null)
                        {
                            if (knownRanged && _diagLogged.Add(id))
                                LLMNPCsPlugin.LogToFile($"[EquipManager] DIAG '{id}' pile EXISTS but EquipmentRepository.GetByID returned null — classification impossible");
                            continue;
                        }
                        bool ranged = IsRangedWeaponEquipment(eq);
                        if (knownRanged && !ranged && _diagLogged.Add(id))
                            LLMNPCsPlugin.LogToFile($"[EquipManager] DIAG '{id}' pile classified NOT-ranged: ItemType={HGet(eq, "ItemType")} primaryMode={HGet(eq, "PrimaryWeaponMode")} attackType={ModeAttackType(HGet(eq, "PrimaryWeaponMode"))}");
                        bool weapon = ranged;
                        if (!ranged)
                        {
                            try { weapon = HGet(eq, "ItemType")?.ToString() == "Weapon"; } catch { }
                            if (weapon) LastMeleeSeen++;
                            continue;
                        }
                        var storedM = pile.GetType().GetMethod("IsStoredOnStockpile", BindingFlags.Public | BindingFlags.Instance);
                        bool isStored = storedM != null && (bool)storedM.Invoke(pile, null);
                        if (isStored) { storedList.Add(pile); LastRangedStored++; }
                        else { groundList.Add(pile); LastRangedGround++; }
                    }
                    catch { }
                }
            }
            catch { }
            storedList.AddRange(groundList);   // prefer stored, but ground COUNTS
            return storedList;
        }

        private static bool AssignPile(object pile, object model, object inventory)
        {
            try
            {
                var isForbidden = pile.GetType().GetProperty("IsForbidden", BindingFlags.Public | BindingFlags.Instance);
                try { isForbidden?.SetValue(pile, false, null); } catch { }
                SetPrivateField(pile, "equipTarget", model);
                var add = inventory.GetType().GetMethod("AddEquipOrder", BindingFlags.Public | BindingFlags.Instance);
                if (add == null) return false;
                add.Invoke(inventory, new[] { pile });
                return true;
            }
            catch { return false; }
        }

        /// <summary>Equip capable hunters that lack a ranged weapon from spare
        /// stockpile piles. Returns the number of equip orders issued this pass.
        /// Also updates LastHuntersMissingWeapon for ColonyAlerts.</summary>
        public static int TryEquipHunters(List<Settler> settlers)
        {
            int missing = 0, equipped = 0, triedPiles = 0;
            try
            {
                if (settlers == null || settlers.Count == 0) { LastResult = "equip: no settlers"; return 0; }

                // Resolve hunter models needing a weapon first (cheap), so we only
                // enumerate piles when there's actually a need.
                var needy = new List<(object model, object inv, string name)>();
                foreach (var s in settlers)
                {
                    if (s == null || s.gameObject == null) continue;
                    if (!GameBridge.TryGetValidatedSettlerIdentity(s.gameObject, out _, out var name, out var rc)) continue;
                    var model = HGet(rc, "HumanoidInstance") ?? rc;
                    if (model == null) continue;
                    if (!IsHunter(model)) continue;
                    if (HasRangedWeapon(model)) continue;
                    var inv = HGet(model, "Inventory");
                    if (inv == null) continue;
                    missing++;
                    if (HasPendingEquipOrder(inv)) continue;   // already walking to a bow
                    needy.Add((model, inv, name));
                }
                LastHuntersMissingWeapon = missing;

                if (needy.Count == 0) { LastResult = missing > 0 ? $"equip: {missing} hunter(s) missing weapon, all have pending orders" : "equip: no hunter needs a weapon"; return 0; }

                var piles = FindRangedWeaponPiles();
                triedPiles = piles.Count;
                if (piles.Count == 0)
                {
                    // HONEST census — never again "NONE" over a visible armory.
                    LastResult = $"equip: {missing} hunter(s) need a ranged weapon — piles seen: ranged 0 (stored 0, ground 0), melee {LastMeleeSeen} (craft a bow/sling)";
                    LLMNPCsPlugin.LogToFile("[EquipManager] " + LastResult);
                    return 0;
                }

                int pi = 0;
                foreach (var (model, inv, name) in needy)
                {
                    if (equipped >= MaxEquipsPerPass || pi >= piles.Count) break;
                    if (AssignPile(piles[pi], model, inv)) { equipped++; pi++; LLMNPCsPlugin.LogToFile($"[EquipManager] ordered {name} to equip a ranged weapon from stockpile"); }
                    else pi++;
                }
                LastResult = $"equip: ordered {equipped}/{needy.Count} hunter(s) to a bow (missing={missing}, piles={triedPiles})";
                if (equipped > 0) LLMNPCsPlugin.LogToFile("[EquipManager] " + LastResult);
                return equipped;
            }
            catch (Exception ex) { LastResult = "equip EXC: " + (ex.InnerException?.Message ?? ex.Message); return 0; }
        }
    }
}

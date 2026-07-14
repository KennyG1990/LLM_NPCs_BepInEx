using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace GoingMedieval.LLM_NPCs
{
    /// <summary>
    /// Centralized bridge to the game's settler/worker system.
    /// Uses reflection to discover and interact with NSMedieval types at runtime,
    /// avoiding direct references to game types that aren't available at compile time.
    /// </summary>
    public static class GameBridge
    {
        // Cached type references
        private static Type _workerControllerType;   // The singleton manager type (e.g. WorkerController)
        private static Type _individualWorkerType;    // The per-worker component type (e.g. Humanoid)
        private static Type _selectionListenerType;
        private static Type _buildingsManagerType;
        private static bool _discoveryDone;
        private static bool _workerFieldDiscovered;
        private static FieldInfo _workersField;       // The collection field on WorkerController
        private static PropertyInfo _workersProp;     // Or property
        private static MethodInfo _workersGetterMethod; // Or zero-arg method returning worker collection
        private static readonly HashSet<string> _loggedWorkerCandidateRejections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _loggedFindTypeRejections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] WorkerTypeSemanticRejectTokens =
        {
            "repository", "definition", "config", "controller", "manager", "data", "database", "image-repository", "imagerepository", "image_repository",
            "preview", "body", "image", "icon", "camera", "generator", "presenter", "helper", "ui"
        };

        private static readonly string[] WorkerPresentationRejectTokens =
        {
            "workerimage", "worker_image", "workericon", "worker_icon", "iconcamera", "icon_camera",
            "humanoidimage", "humanoid_image", "repository", ".ui.", " ui.", ".ui", "camera", "icon", "image",
            "workerbodypreview", "bodypreview", "preview", "generator", "presenter", "helper"
        };

        private static readonly string[] WorkerTypeNameHints =
        {
            "humanoid", "worker", "creature", "settler", "character", "citizen", "npc"
        };

        // Cached reflection members
        private static FieldInfo _selectedObjectField;
        private static PropertyInfo _selectedObjectProp;
        private static MethodInfo _getSelectedMethod;

        // Settler cache to avoid per-frame reflection
        private static List<GameObject> _cachedSettlers = new List<GameObject>();
        private static bool _hasSettlerCache;
        private static float _cacheTimestamp;
        private const float CACHE_DURATION = 5f; // seconds
        private static float _lastSceneScanTime = -999f;
        private const float SCENE_SCAN_RETRY_INTERVAL = 3f;
        private static string _lastAcceptedSettlerIdentityLogSignature;

        /// <summary>
        /// Initialize type discovery. Call once during plugin startup or first use.
        /// </summary>
        public static void Initialize()
        {
            if (_discoveryDone) return;
            _discoveryDone = true;

            DiscoverSettlerType();
            DiscoverSelectionSystem();
        }

        // ── LOAD GATE (root cause of the loading-screen hang) ─────────────────
        // The game exposes its load pipeline state on the static
        // NSMedieval.Controllers.LoadingController: IsLoadingComplete is set true
        // only when InvokeLoadingCompleteEvent fires at the END of the whole load
        // (ground truth: decompiled LoadingController). The mod used to start
        // acting (designating trees, placing blueprints, forcing goals) as soon
        // as settler objects EXISTED — which is mid-load, while the loader is
        // still building slopes/meshes. Mutating world state in that window
        // wedges the loader (Libury hung at 37.5%; a FRESH map hung at 'Loading
        // Slopes' the same way). NOTHING may touch the world until this is true.
        private static Type _loadingCtrlType;
        private static PropertyInfo _isLoadingComplete, _isSceneTransition, _isLeavingMainScene;

        /// <summary>True only when the game reports the load pipeline fully
        /// finished and no scene transition is in progress. Fail-CLOSED: if the
        /// controller can't be found, we report NOT ready and the mod stays idle.</summary>
        public static bool IsWorldReady()
        {
            try
            {
                if (_loadingCtrlType == null)
                {
                    _loadingCtrlType = FindType("NSMedieval.Controllers.LoadingController");
                    if (_loadingCtrlType == null)
                    {
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            try
                            {
                                foreach (var t in asm.GetTypes())
                                    if (t.Name == "LoadingController" && t.FullName.StartsWith("NSMedieval"))
                                    { _loadingCtrlType = t; break; }
                            }
                            catch { }
                            if (_loadingCtrlType != null) break;
                        }
                    }
                    if (_loadingCtrlType != null)
                    {
                        const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                        _isLoadingComplete = _loadingCtrlType.GetProperty("IsLoadingComplete", SF);
                        _isSceneTransition = _loadingCtrlType.GetProperty("IsSceneTransition", SF);
                        _isLeavingMainScene = _loadingCtrlType.GetProperty("IsLeavingMainScene", SF);
                    }
                }
                if (_isLoadingComplete == null) return false;   // fail closed
                if (!(bool)_isLoadingComplete.GetValue(null, null)) return false;
                if (_isSceneTransition != null && (bool)_isSceneTransition.GetValue(null, null)) return false;
                if (_isLeavingMainScene != null && (bool)_isLeavingMainScene.GetValue(null, null)) return false;
                return true;
            }
            catch { return false; }                             // fail closed
        }

        /// <summary>
        /// Gets all settler GameObjects currently in the scene.
        /// Uses caching to avoid expensive reflection every frame.
        /// </summary>
        public static List<GameObject> GetAllSettlers()
        {
            Initialize();
            SanitizeDiscoveredTypes();

            // Return cached results if still fresh (including empty-list cache)
            if (_hasSettlerCache && (Time.time - _cacheTimestamp) < CACHE_DURATION)
            {
                // Validate cache (remove destroyed objects)
                _cachedSettlers.RemoveAll(go => go == null);
                return new List<GameObject>(_cachedSettlers);
            }

            var results = new List<GameObject>();

            // Strategy 1: Find WorkerController singleton and enumerate its managed workers
            if (_workerControllerType != null)
            {
                results = FilterToLikelySettlers(ExtractWorkersFromController());
            }

            // Strategy 2: If we discovered an individual worker type, find them directly
            if (results.Count == 0 && _individualWorkerType != null)
            {
                var objects = SafeFindObjectsOfType(_individualWorkerType, "direct worker lookup");

                var directMatches = new List<GameObject>();
                foreach (var obj in objects)
                {
                    if (obj is Component comp && comp != null && comp.gameObject.activeInHierarchy)
                        directMatches.Add(comp.gameObject);
                }

                results = FilterToLikelySettlers(directMatches);
            }

            // Strategy 3: one-time scene scan to discover better individual worker type, then retry.
            // Important: we do NOT return broad animator scans directly (that produced trees/resources).
            if (results.Count == 0 && (Time.time - _lastSceneScanTime) >= SCENE_SCAN_RETRY_INTERVAL)
            {
                _lastSceneScanTime = Time.time;
                ScanSceneForSettlers();

                if (_workerControllerType != null)
                {
                    results = FilterToLikelySettlers(ExtractWorkersFromController());
                }

                if (results.Count == 0 && _individualWorkerType != null)
                {
                    var objects = SafeFindObjectsOfType(_individualWorkerType, "post-scan worker lookup");
                    var directMatches = new List<GameObject>();
                    foreach (var obj in objects)
                    {
                        if (obj is Component comp && comp != null && comp.gameObject.activeInHierarchy)
                            directMatches.Add(comp.gameObject);
                    }

                    results = FilterToLikelySettlers(directMatches);
                }
            }

            // Final de-duplication and safety filter.
            results = FilterToLikelySettlers(results);

            // Update cache
            _cachedSettlers = results;
            _hasSettlerCache = true;
            _cacheTimestamp = Time.time;

            return new List<GameObject>(results);
        }

        /// <summary>
        /// Finds the WorkerController singleton and extracts individual worker GameObjects
        /// from its internal collection fields.
        /// </summary>
        private static List<GameObject> ExtractWorkersFromController()
        {
            var results = new List<GameObject>();

            try
            {
                // Find the WorkerController singleton instance
                var controllers = SafeFindObjectsOfType(_workerControllerType, "worker controller lookup");
                if (controllers.Length == 0) return results;

                var dedupe = new HashSet<int>();
                foreach (var controller in controllers)
                {
                    if (controller == null) continue;

                    // Discover collection members once, against the first valid controller.
                    if (!_workerFieldDiscovered)
                    {
                        _workerFieldDiscovered = true;
                        DiscoverWorkersField(controller);
                    }

                    var collection = ResolveWorkerCollection(controller);
                    if (!(collection is IEnumerable enumerable))
                        continue;

                    int extractedFromThisController = 0;
                    int scanned = 0;
                    foreach (var item in enumerable)
                    {
                        if (++scanned > 8192) break; // hard safety guard

                        var go = ExtractGameObject(item);
                        if (go == null || !go.activeInHierarchy) continue;
                        if (!IsLikelyWorkerGameObject(go)) continue;

                        if (dedupe.Add(go.GetInstanceID()))
                        {
                            results.Add(go);
                            extractedFromThisController++;
                        }
                    }

                    LLMNPCsPlugin.LogToFile($"[GameBridge] Extracted {extractedFromThisController} filtered workers from {_workerControllerType.Name}");
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[GameBridge] Error extracting workers from controller: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Discovers the field/property on WorkerController that holds the worker collection.
        /// </summary>
        private static void DiscoverWorkersField(UnityEngine.Object controller)
        {
            var type = controller.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // Log all fields for debugging
            var allFields = GetAllInstanceFields(type, flags).ToArray();
            var allProps = GetAllInstanceProperties(type, flags).ToArray();
            LLMNPCsPlugin.LogToFile($"[GameBridge] WorkerController fields: {string.Join(", ", allFields.Select(f => $"{f.FieldType.Name} {f.Name}"))}");
            LLMNPCsPlugin.LogToFile($"[GameBridge] WorkerController props: {string.Join(", ", allProps.Select(p => $"{p.PropertyType.Name} {p.Name}"))}");

            // Search for collection fields with worker-related names
            string[] collectionNames =
            {
                "_workers", "workers", "worker",
                "_humanoids", "humanoids", "humanoid",
                "_settlers", "settlers", "settler",
                "_characters", "characters", "character",
                "_citizens", "citizens", "citizen",
                "_pawns", "pawns", "pawn",
                "_instances", "instances",
                "_entities", "entities"
            };

            foreach (var name in collectionNames)
            {
                var field = allFields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
                if (field != null && IsCollectionType(field.FieldType))
                {
                    _workersField = field;
                    LLMNPCsPlugin.LogToFile($"[GameBridge] Found workers field by name: {field.FieldType.Name} {field.Name}");
                    return;
                }

                var prop = allProps.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (prop != null && prop.CanRead && IsCollectionType(prop.PropertyType))
                {
                    _workersProp = prop;
                    LLMNPCsPlugin.LogToFile($"[GameBridge] Found workers property by name: {prop.PropertyType.Name} {prop.Name}");
                    return;
                }
            }

            // Search by type: any collection likely holding worker-like objects/components
            foreach (var field in allFields)
            {
                if (IsCollectionOfWorkerLikeObjects(field.FieldType) || IsCollectionOfComponents(field.FieldType))
                {
                    _workersField = field;
                    LLMNPCsPlugin.LogToFile($"[GameBridge] Found workers field by type: {field.FieldType.Name} {field.Name}");
                    return;
                }
            }

            foreach (var prop in allProps)
            {
                if (prop.CanRead && (IsCollectionOfWorkerLikeObjects(prop.PropertyType) || IsCollectionOfComponents(prop.PropertyType)))
                {
                    _workersProp = prop;
                    LLMNPCsPlugin.LogToFile($"[GameBridge] Found workers property by type: {prop.PropertyType.Name} {prop.Name}");
                    return;
                }
            }

            // Search by method: zero-arg methods returning worker-like collections
            var methods = type.GetMethods(flags)
                .Where(m => !m.IsSpecialName && m.GetParameters().Length == 0)
                .ToList();

            foreach (var method in methods)
            {
                var returnType = method.ReturnType;
                if (!IsCollectionType(returnType)) continue;

                var lower = method.Name.ToLowerInvariant();
                if (lower.Contains("worker") || lower.Contains("humanoid") || lower.Contains("settler") || lower.Contains("character") || lower.Contains("citizen"))
                {
                    _workersGetterMethod = method;
                    LLMNPCsPlugin.LogToFile($"[GameBridge] Found workers getter method: {method.ReturnType.Name} {method.Name}()");
                    return;
                }
            }

            LLMNPCsPlugin.LogToFile("[GameBridge] WARNING: Could not discover workers field on WorkerController");
        }

        private static object ResolveWorkerCollection(UnityEngine.Object controller)
        {
            object collection = null;

            try
            {
                if (_workersField != null)
                    collection = _workersField.GetValue(controller);
            }
            catch { }

            if (collection == null)
            {
                try
                {
                    if (_workersProp != null)
                        collection = _workersProp.GetValue(controller, null);
                }
                catch { }
            }

            if (collection == null)
            {
                try
                {
                    if (_workersGetterMethod != null)
                        collection = _workersGetterMethod.Invoke(controller, null);
                }
                catch { }
            }

            if (collection == null)
            {
                // Brute force: inspect all enumerable fields/properties and score worker-likeness.
                collection = BruteForceFindWorkerCollection(controller);
            }

            return collection;
        }

        private static object BruteForceFindWorkerCollection(UnityEngine.Object controller)
        {
            var type = controller.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var allFields = GetAllInstanceFields(type, flags).ToArray();
            var allProps = GetAllInstanceProperties(type, flags).ToArray();

            object bestCollection = null;
            int bestScore = 0;

            int EvaluateEnumerable(IEnumerable enumerable)
            {
                int score = 0;
                int sampled = 0;
                foreach (var item in enumerable)
                {
                    if (++sampled > 32) break;
                    if (item == null) continue;

                    if (IsWorkerLikeObject(item)) score += 3;

                    var go = ExtractGameObject(item);
                    if (go != null)
                    {
                        score += 1;
                        if (IsLikelyWorkerGameObject(go)) score += 5;
                    }
                }

                return score;
            }

            // Try every enumerable field and check if it contains GameObjects/Components
            foreach (var field in allFields)
            {
                if (!IsCollectionType(field.FieldType)) continue;
                try
                {
                    var val = field.GetValue(controller);
                    if (val is IEnumerable enumerable)
                    {
                        int score = EvaluateEnumerable(enumerable);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCollection = val;
                            _workersField = field;
                        }
                    }
                }
                catch { }
            }

            foreach (var prop in allProps)
            {
                if (!prop.CanRead || !IsCollectionType(prop.PropertyType)) continue;
                try
                {
                    var val = prop.GetValue(controller, null);
                    if (val is IEnumerable enumerable)
                    {
                        int score = EvaluateEnumerable(enumerable);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestCollection = val;
                            _workersField = null;
                            _workersProp = prop;
                        }
                    }
                }
                catch { }
            }

            if (bestCollection != null)
            {
                var source = _workersField != null
                    ? $"field {_workersField.FieldType.Name} {_workersField.Name}"
                    : _workersProp != null
                        ? $"prop {_workersProp.PropertyType.Name} {_workersProp.Name}"
                        : "unknown member";
                LLMNPCsPlugin.LogToFile($"[GameBridge] Brute force selected worker collection from {source} (score {bestScore})");
            }

            return bestCollection;
        }

        private static bool IsCollectionType(Type t)
        {
            if (t == null) return false;
            if (typeof(Delegate).IsAssignableFrom(t)) return false;
            if (t.IsArray) return true;
            if (typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string))
                return true;
            return false;
        }

        private static bool IsCollectionOfComponents(Type t)
        {
            if (t == null || typeof(Delegate).IsAssignableFrom(t)) return false;
            if (t.IsArray)
            {
                var elemType = t.GetElementType();
                return elemType != null && (typeof(Component).IsAssignableFrom(elemType) || typeof(GameObject).IsAssignableFrom(elemType));
            }
            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (args == null || args.Length == 0) return false;
                // Check last generic arg (for Dictionary<K,V> check V, for List<T> check T)
                var checkType = args.Last();
                if (typeof(Delegate).IsAssignableFrom(checkType)) return false;
                return typeof(Component).IsAssignableFrom(checkType) || typeof(GameObject).IsAssignableFrom(checkType);
            }
            return false;
        }

        private static bool IsCollectionOfWorkerLikeObjects(Type t)
        {
            if (!IsCollectionType(t)) return false;

            Type elemType = null;
            if (t.IsArray)
            {
                elemType = t.GetElementType();
            }
            else if (t.IsGenericType)
            {
                var args = t.GetGenericArguments();
                if (args != null && args.Length > 0)
                    elemType = args.Last();
            }

            if (elemType == null || typeof(Delegate).IsAssignableFrom(elemType)) return false;

            var lowerType = elemType.Name.ToLowerInvariant();
            if (lowerType.Contains("worker") || lowerType.Contains("humanoid") || lowerType.Contains("settler") || lowerType.Contains("character") || lowerType.Contains("citizen"))
                return true;

            return false;
        }

        /// <summary>
        /// Gets the currently selected settler using the game's selection system.
        /// Returns null if nothing is selected or the selection isn't a settler.
        /// </summary>
        public static GameObject GetSelectedSettler()
        {
            Initialize();

            try
            {
                // Strategy 1: SelectionInputListener (singleton/static/instance)
                var selectedFromListener = TryGetSelectedFromType(_selectionListenerType, _selectedObjectField, _selectedObjectProp, _getSelectedMethod);
                if (selectedFromListener != null && IsSettler(selectedFromListener))
                {
                    LLMNPCsPlugin.LogToFile($"[GameBridge] Selected settler via SelectionInputListener: {selectedFromListener.name}");
                    return selectedFromListener;
                }

                // Strategy 2: Try MultiSelectPanelManager
                var multiSelectType = FindType("NSMedieval.Manager.MultiSelectPanelManager");
                var selectedFromMulti = TryGetSelectedFromType(multiSelectType, null, null, null);
                if (selectedFromMulti != null && IsSettler(selectedFromMulti))
                {
                    LLMNPCsPlugin.LogToFile($"[GameBridge] Selected settler via MultiSelect: {selectedFromMulti.name}");
                    return selectedFromMulti;
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[GameBridge] Error getting selected settler: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Gets the name of a settler from its GameObject using reflection.
        /// </summary>
        public static string GetSettlerName(GameObject go)
        {
            if (go == null) return "Unknown";

            var workerComp = GetRuntimeWorkerComponent(go);
            var name = TryResolveNameFromComponent(workerComp);
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            // Fallback: only inspect worker-like NSMedieval components (prevents repository poisoning)
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                var type = comp.GetType();
                if (type.Namespace == null || !type.Namespace.Contains("NSMedieval")) continue;
                if (!IsValidWorkerTypeCandidate(type, out _)) continue;

                name = TryResolveNameFromComponent(comp);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }

            // Fallback: use GameObject name
            return go.name;
        }

        /// <summary>
        /// Gets a settler's ID string (game-internal ID, else a stable
        /// name-derived hash). Unity instance IDs change every session, which
        /// fragmented settler memory across restarts; the name-hash fallback
        /// keeps identity stable so memories survive save reloads.
        /// </summary>
        public static string GetSettlerId(GameObject go)
        {
            if (go == null) return null;

            var workerComp = GetRuntimeWorkerComponent(go);
            var id = TryResolveIdFromComponent(workerComp);
            if (!string.IsNullOrWhiteSpace(id))
                return id;

            // Fallback: only inspect worker-like NSMedieval components (prevents repository poisoning)
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                var type = comp.GetType();
                if (type.Namespace == null || !type.Namespace.Contains("NSMedieval")) continue;
                if (!IsValidWorkerTypeCandidate(type, out _)) continue;

                id = TryResolveIdFromComponent(comp);
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }

            var stableName = SanitizeDisplayName(
                TryResolveNameFromComponent(workerComp) ?? go.name);
            var stableId = ComputeStableSettlerId(stableName);
            if (!string.IsNullOrWhiteSpace(stableId))
                return stableId;

            return go.GetInstanceID().ToString();
        }

        /// <summary>
        /// Deterministic settler ID from the sanitized display name:
        /// "gm_" + first 12 hex chars of SHA1(name). Stable across game
        /// sessions and save reloads; scoped per save by the DB key.
        /// Returns null for unusable names so callers can fall back.
        /// </summary>
        public static string ComputeStableSettlerId(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return null;
            var normalized = displayName.Trim().ToLowerInvariant();
            if (normalized == "unknown" || normalized.Length < 3)
                return null;
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized));
                var sb = new System.Text.StringBuilder("gm_", 15);
                for (int i = 0; i < 6; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static Component GetRuntimeWorkerComponent(GameObject go)
        {
            if (go == null || !go.activeInHierarchy)
                return null;

            if (_individualWorkerType != null && typeof(Component).IsAssignableFrom(_individualWorkerType))
            {
                try
                {
                    var exact = go.GetComponent(_individualWorkerType) as Component;
                    if (exact != null && IsValidWorkerIdentityComponent(exact))
                        return exact;
                }
                catch { }
            }

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                if (!IsValidGameplayRuntimeWorkerComponent(comp) && !IsValidWorkerIdentityComponent(comp)) continue;
                return comp;
            }

            // Owner/root traversal fallback: only accept gameplay worker/humanoid components.
            var parent = go.transform.parent;
            int depth = 0;
            while (parent != null && depth++ < 4)
            {
                foreach (var comp in parent.GetComponents<Component>())
                {
                    if (IsValidGameplayRuntimeWorkerComponent(comp) || IsValidWorkerIdentityComponent(comp))
                        return comp;
                }

                parent = parent.parent;
            }

            var root = go.transform.root;
            if (root != null)
            {
                foreach (var comp in root.GetComponents<Component>())
                {
                    if (IsValidGameplayRuntimeWorkerComponent(comp) || IsValidWorkerIdentityComponent(comp))
                        return comp;
                }
            }

            return null;
        }

        public static bool TryGetValidatedSettlerIdentity(GameObject go, out string id, out string name, out Component runtimeComponent)
        {
            id = null;
            name = null;
            runtimeComponent = null;

            if (go == null || !go.activeInHierarchy)
                return false;

            if (!IsLikelyWorkerGameObject(go))
                return false;

            runtimeComponent = GetRuntimeWorkerComponent(go);
            if (runtimeComponent == null)
                return false;

            name = SanitizeDisplayName(TryResolveNameFromComponent(runtimeComponent) ?? go.name);
            id = TryResolveIdFromComponent(runtimeComponent)
                 ?? ComputeStableSettlerId(name)
                 ?? go.GetInstanceID().ToString();
            return !string.IsNullOrWhiteSpace(id);
        }

        private static string SanitizeDisplayName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "Unknown";

            var value = raw.Trim();

            var cloneIdx = value.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
            if (cloneIdx >= 0)
            {
                value = value.Substring(cloneIdx + "(Clone)".Length);
            }

            value = value.Trim('_', '-', ' ');
            value = value.Replace("_", " ").Trim();

            if (string.IsNullOrWhiteSpace(value))
                value = raw;

            return value;
        }

        public static List<Settler> GetValidatedSettlers()
        {
            var results = new List<Settler>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var go in GetAllSettlers())
            {
                if (!TryGetValidatedSettlerIdentity(go, out var id, out var name, out _))
                    continue;

                if (!seen.Add(id))
                    continue;

                var settler = EnsureSettlerComponent(go);
                if (settler == null)
                    continue;

                settler.Name = string.IsNullOrWhiteSpace(name) ? go.name : name;
                results.Add(settler);
            }

            LogAcceptedSettlerIdentities(results);

            return results;
        }

        /// <summary>
        /// Finds a settler by its ID string.
        /// </summary>
        public static GameObject FindSettlerById(string id)
        {
            foreach (var go in GetAllSettlers())
            {
                if (GetSettlerId(go) == id)
                    return go;
            }
            return null;
        }

        /// <summary>
        /// Ensures a settler placeholder component exists on the given GameObject.
        /// Returns the Settler component (adds one if missing).
        /// </summary>
        public static Settler EnsureSettlerComponent(GameObject go)
        {
            if (go == null) return null;
            var settler = go.GetComponent<Settler>();
            if (settler == null)
                settler = go.AddComponent<Settler>();
            settler.Name = GetSettlerName(go);
            return settler;
        }

        /// <summary>
        /// Checks if a GameObject is a settler (has an NSMedieval component that matches our discovered type).
        /// </summary>
        public static bool IsSettler(GameObject go)
        {
            return IsLikelyWorkerGameObject(go);
        }

        /// <summary>
        /// The discovered settler component type, or null if not found yet.
        /// </summary>
        public static Type SettlerComponentType => _individualWorkerType ?? _workerControllerType;

        /// <summary>
        /// Force a re-scan of the scene (e.g. after loading a new game).
        /// </summary>
        public static void Reset()
        {
            _lastSceneScanTime = -999f;
            _workerFieldDiscovered = false;
            _workersField = null;
            _workersProp = null;
            _workersGetterMethod = null;
            _cachedSettlers.Clear();
            _hasSettlerCache = false;
            _cacheTimestamp = 0;
        }

        public static float GetColonyWealth()
        {
            try
            {
                if (_buildingsManagerType == null)
                    _buildingsManagerType = FindType("NSMedieval.BuildingComponents.BuildingsManagerMain");

                if (_buildingsManagerType != null)
                {
                    var managers = SafeFindObjectsOfType(_buildingsManagerType, "wealth lookup");
                    if (managers != null && managers.Length > 0 && managers[0] != null)
                    {
                        var prop = _buildingsManagerType.GetProperty("TotalBuildingWealth");
                        if (prop != null)
                        {
                            return (float)prop.GetValue(managers[0], null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[GameBridge] Error getting colony wealth: {ex.Message}");
            }
            return 0f;
        }

        public static object GetGoapAgent(Component runtimeComponent)
        {
            if (runtimeComponent == null) return null;

            // Ground truth (decompiled HumanoidInstance:546): the agent is
            // CreatureBase.GoapAgent — a PROTECTED property on a BASE class,
            // which plain GetProperty on the derived type does NOT return.
            // Hierarchy-walk with DeclaredOnly (the codebase's known CRTP
            // gotcha). Candidates: the component itself, its HumanoidInstance,
            // and its WorkerBehaviour.
            object HierarchyGet(object o, string name)
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

            var candidates = new object[]
            {
                runtimeComponent,
                HierarchyGet(runtimeComponent, "HumanoidInstance"),
                HierarchyGet(runtimeComponent, "WorkerBehaviour") ?? HierarchyGet(runtimeComponent, "workerBehaviour"),
            };
            foreach (var c in candidates)
            {
                if (c == null) continue;
                var agent = HierarchyGet(c, "GoapAgent")
                         ?? HierarchyGet(c, "WorkerGoapAgent")
                         ?? HierarchyGet(c, "workerGoapAgent");
                if (agent != null) return agent;
                // one more hop: candidate's WorkerBehaviour
                var wb = HierarchyGet(c, "WorkerBehaviour");
                if (wb != null)
                {
                    agent = HierarchyGet(wb, "GoapAgent") ?? HierarchyGet(wb, "WorkerGoapAgent");
                    if (agent != null) return agent;
                }
            }
            return null;
        }

        public static bool ForceGoal(Component runtimeComponent, string goalId)
        {
            var goapAgent = GetGoapAgent(runtimeComponent);
            if (goapAgent == null) return false;

            var method = goapAgent.GetType().GetMethod("ForceNextGoalExclusive", BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return false;
            try
            {
                // ForceNextGoalExclusive returns the Goal it forced (null if the
                // goalId isn't a real goal), so a non-null result == it actually
                // took. This lets us discover valid goal ids at runtime.
                var result = method.Invoke(goapAgent, new object[] { goalId });
                return result != null;
            }
            catch { return false; }
        }

        /// <summary>Force the settler to ACTUALLY go eat (walk to accessible food
        /// and consume it) — the real action, not a hunger-stat cheat. Tries the
        /// likely eat-goal ids and returns the one that took (or null). Food must
        /// be un-forbidden and in supply for the goal to find a target.</summary>
        public static string TryTriggerEat(GameObject settler)
        {
            if (!TryGetValidatedSettlerIdentity(settler, out _, out _, out var rc)) return null;
            foreach (var g in new[] { "EatGoal", "HaveMealGoal", "EatMealGoal", "ConsumeGoal",
                                      "ConsumeFoodGoal", "FeedGoal", "GetFoodGoal", "HungerGoal", "NourishGoal" })
                if (ForceGoal(rc, g)) return g;
            return null;
        }

        public static bool TryTriggerHaul(GameObject settler)
        {
            if (!TryGetValidatedSettlerIdentity(settler, out _, out _, out var runtimeComponent)) return false;
            return ForceGoal(runtimeComponent, "StockpileHaulingGoal") || ForceGoal(runtimeComponent, "StockpileUrgentHaulingGoal");
        }

        public static bool TryTriggerBuild(GameObject settler, string buildingType = null)
        {
            if (!TryGetValidatedSettlerIdentity(settler, out _, out _, out var runtimeComponent)) return false;
            return ForceGoal(runtimeComponent, "ConstructBuildingGoal");
        }

        public static bool TryTriggerRepair(GameObject settler)
        {
            if (!TryGetValidatedSettlerIdentity(settler, out _, out _, out var runtimeComponent)) return false;
            return ForceGoal(runtimeComponent, "RepairBuildingGoal");
        }

        public static bool TryChangeClothing(GameObject settler, string weatherCondition)
        {
            if (!TryGetValidatedSettlerIdentity(settler, out _, out _, out var runtimeComponent)) return false;
            return ForceGoal(runtimeComponent, "AutoEquipGoal");
        }

        public static bool TrySeekMedic(GameObject settler)
        {
            if (!TryGetValidatedSettlerIdentity(settler, out _, out _, out var runtimeComponent)) return false;
            return ForceGoal(runtimeComponent, "PatientGoal") || ForceGoal(runtimeComponent, "SelfTendWoundsGoal");
        }

        public static bool TryAssignConstructionPriority(GameObject settler, int priority)
        {
            if (!TryGetValidatedSettlerIdentity(settler, out _, out _, out var runtimeComponent)) return false;
            var goapAgent = GetGoapAgent(runtimeComponent);
            if (goapAgent == null) return false;
            
            var method = goapAgent.GetType().GetMethod("ChangeJobPriority", BindingFlags.Public | BindingFlags.Instance);
            if (method != null)
            {
                var jobTypeType = FindType("NSMedieval.State.WorkerJobs.JobType");
                if (jobTypeType != null)
                {
                    var jobVal = Enum.ToObject(jobTypeType, 4); // Construction
                    method.Invoke(goapAgent, new object[] { jobVal, priority });
                    return true;
                }
            }
            return false;
        }

        public static bool TrySetCombatMode(GameObject settler, bool enableCombat)
        {
            if (!TryGetValidatedSettlerIdentity(settler, out _, out _, out var runtimeComponent)) return false;
            
            object workerBehaviour = runtimeComponent;
            if (runtimeComponent.GetType().FullName == "NSMedieval.State.HumanoidInstance")
            {
                workerBehaviour = NPCContextExtractor.GetPropertyValue<object>(runtimeComponent, "WorkerBehaviour") 
                                  ?? NPCContextExtractor.GetFieldValue<object>(runtimeComponent, "workerBehaviour");
            }
            if (workerBehaviour == null) return false;
            
            var method = workerBehaviour.GetType().GetMethod("SetCombatMode", BindingFlags.Public | BindingFlags.Instance);
            if (method != null)
            {
                var combatModeType = FindType("NSMedieval.State.WorkerJobs.UnitCombatModeType");
                if (combatModeType != null)
                {
                    var modeVal = Enum.ToObject(combatModeType, enableCombat ? 1 : 3); // 1 = Aggressive, 3 = Neutral
                    method.Invoke(workerBehaviour, new object[] { modeVal, true });
                    return true;
                }
            }
            return false;
        }

        #region Private Discovery Methods

        private static void DiscoverSettlerType()
        {
            // Controller types (singleton managers)
            string[] controllerPatterns = {
                "NSMedieval.WorkerController",
                "NSMedieval.HumanoidController",
                "NSMedieval.CreatureController",
            };

            // Individual worker types (per-settler components)
            string[] workerPatterns = {
                "NSMedieval.Humanoid",
                "NSMedieval.Worker",
                "NSMedieval.Settler",
                "NSMedieval.Character",
            };

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.FullName.Contains("Assembly-CSharp")) continue;

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }

                    // Find controller types
                    foreach (var pattern in controllerPatterns)
                    {
                        var match = types.FirstOrDefault(t => t != null && t.FullName == pattern);
                        if (TrySetWorkerControllerTypeCandidate(match, "assembly exact pattern", allowOverwrite: false))
                        {
                            break;
                        }
                    }

                    // Find individual worker types
                    foreach (var pattern in workerPatterns)
                    {
                        var match = types.FirstOrDefault(t => t != null && t.FullName == pattern);
                        if (TrySetIndividualWorkerTypeCandidate(match, "assembly exact pattern", allowOverwrite: false))
                        {
                            break;
                        }
                    }

                    // If no exact individual type found, search NSMedieval types
                    if (_individualWorkerType == null)
                    {
                        // Look for non-controller types that are likely per-settler
                        var candidates = types.Where(t =>
                            t != null &&
                            typeof(Component).IsAssignableFrom(t) &&
                            t.Namespace != null &&
                            t.Namespace.Contains("NSMedieval") &&
                            (t.Name == "Humanoid" || t.Name == "Worker" || t.Name == "Settler" || t.Name == "Character")
                        ).ToList();

                        if (candidates.Count > 0)
                        {
                            TrySetIndividualWorkerTypeCandidate(candidates.First(), "assembly candidate", allowOverwrite: false);
                        }
                    }

                    // Also search for controller by pattern if not found
                    if (_workerControllerType == null)
                    {
                        var controllerCandidates = types.Where(t =>
                            t != null &&
                            typeof(Component).IsAssignableFrom(t) &&
                            t.Namespace != null &&
                            t.Namespace.Contains("NSMedieval") &&
                            t.Name.Contains("Controller") &&
                            (t.Name.Contains("Worker") || t.Name.Contains("Humanoid"))
                        ).ToList();

                        if (controllerCandidates.Count > 0)
                        {
                            TrySetWorkerControllerTypeCandidate(controllerCandidates.First(), "assembly controller candidate", allowOverwrite: false);
                        }
                    }

                    // Log what NSMedieval components exist for debugging
                    var nsComponents = types.Where(t =>
                        t != null &&
                        typeof(Component).IsAssignableFrom(t) &&
                        t.Namespace != null &&
                        t.Namespace.StartsWith("NSMedieval")
                    ).ToList();

                    if (nsComponents.Count > 0)
                    {
                        LLMNPCsPlugin.LogToFile($"[GameBridge] NSMedieval Components found: {string.Join(", ", nsComponents.Take(50).Select(c => c.FullName))}");
                    }

                    if (_workerControllerType != null || _individualWorkerType != null)
                        return;
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.Log.LogError($"[GameBridge] Error discovering settler type: {ex.Message}");
            }

            if (_workerControllerType == null && _individualWorkerType == null)
                LLMNPCsPlugin.Log.LogWarning("[GameBridge] Could not discover any settler/controller type from assemblies");
        }

        private static void DiscoverSelectionSystem()
        {
            try
            {
                _selectionListenerType = FindType("NSMedieval.SelectionInputListener");
                if (_selectionListenerType != null)
                {
                    LLMNPCsPlugin.Log.LogInfo($"[GameBridge] Found selection listener: {_selectionListenerType.FullName}");
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(_selectionListenerType))
                    {
                        LLMNPCsPlugin.LogToFile($"[GameBridge] Selection listener is non-Unity type ({_selectionListenerType.FullName}); using singleton/static reflection path.");
                    }

                    // Cache reflection members for selected object
                    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                    // Look for methods/properties/fields that return selected objects
                    string[] selectedNames = { "Selected", "selected", "SelectedObject", "selectedObject",
                                               "CurrentSelection", "currentSelection", "_selected", "_selectedObject" };

                    foreach (var name in selectedNames)
                    {
                        _selectedObjectField = _selectedObjectField ?? _selectionListenerType.GetField(name, flags);
                        _selectedObjectProp = _selectedObjectProp ?? _selectionListenerType.GetProperty(name, flags);
                    }

                    // Look for getter methods
                    string[] methodNames = { "GetSelected", "GetSelectedObject", "GetSelection" };
                    foreach (var name in methodNames)
                    {
                        var m = _selectionListenerType.GetMethod(name, flags);
                        if (m != null && m.GetParameters().Length == 0)
                        {
                            _getSelectedMethod = m;
                            break;
                        }
                    }

                    // Log what we found for debugging
                    LLMNPCsPlugin.LogToFile($"[GameBridge] SelectionListener fields: {string.Join(", ", _selectionListenerType.GetFields(flags).Select(f => $"{f.FieldType.Name} {f.Name}"))}");
                    LLMNPCsPlugin.LogToFile($"[GameBridge] SelectionListener props: {string.Join(", ", _selectionListenerType.GetProperties(flags).Select(p => $"{p.PropertyType.Name} {p.Name}"))}");
                    LLMNPCsPlugin.LogToFile($"[GameBridge] SelectionListener methods: {string.Join(", ", _selectionListenerType.GetMethods(flags).Where(m => !m.IsSpecialName).Select(m => $"{m.ReturnType.Name} {m.Name}()"))}");
                }
                else
                {
                    LLMNPCsPlugin.LogToFile("[GameBridge] SelectionInputListener type not found");
                }
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[GameBridge] Error discovering selection system: {ex.Message}");
            }
        }

        private static void ScanSceneForSettlers()
        {
            LLMNPCsPlugin.LogToFile("[GameBridge:ScanScene] Scanning scene for worker-related NSMedieval components...");

            try
            {
                var allComponents = UnityEngine.Object.FindObjectsOfType<Component>();
                int workerLikeComponentCount = 0;

                foreach (var comp in allComponents)
                {
                    if (comp == null) continue;
                    var type = comp.GetType();
                    if (type.Namespace == null || !type.Namespace.StartsWith("NSMedieval")) continue;

                    if (!IsValidWorkerTypeCandidate(type, out var rejectionReason))
                    {
                        LogWorkerCandidateRejectionOnce(type, rejectionReason, "scene scan");
                        continue;
                    }

                    workerLikeComponentCount++;

                    TrySetIndividualWorkerTypeCandidate(type, "scene scan", allowOverwrite: false);
                }

                LLMNPCsPlugin.LogToFile($"[GameBridge:ScanScene] Worker-like NSMedieval components found: {workerLikeComponentCount}");
            }
            catch (Exception ex)
            {
                LLMNPCsPlugin.LogToFile($"[GameBridge:ScanScene] Error during scene scan: {ex.Message}");
            }
        }

        private static GameObject ExtractGameObject(object obj)
        {
            return ExtractGameObject(obj, 0);
        }

        private static GameObject ExtractGameObject(object obj, int depth)
        {
            if (obj == null) return null;
            if (obj is GameObject go) return go;
            if (obj is Component comp) return comp.gameObject;
            if (obj is Transform t) return t.gameObject;
            if (obj is string) return null;

            if (depth >= 2) return null;

            // Try reflection to get a gameObject property
            var objType = obj.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var goProp = objType.GetProperty("gameObject", flags);
            if (goProp != null)
            {
                try
                {
                    var propVal = goProp.GetValue(obj, null);
                    var nested = ExtractGameObject(propVal, depth + 1);
                    if (nested != null) return nested;
                }
                catch { }
            }

            // Common member names used by wrappers/view models
            string[] memberNames =
            {
                "GameObject", "gameObject", "Transform", "transform",
                "View", "view", "Worker", "worker", "Humanoid", "humanoid",
                "Character", "character", "Entity", "entity"
            };

            foreach (var memberName in memberNames)
            {
                var field = objType.GetField(memberName, flags);
                if (field != null)
                {
                    try
                    {
                        var nested = ExtractGameObject(field.GetValue(obj), depth + 1);
                        if (nested != null) return nested;
                    }
                    catch { }
                }

                var prop = objType.GetProperty(memberName, flags);
                if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        var nested = ExtractGameObject(prop.GetValue(obj, null), depth + 1);
                        if (nested != null) return nested;
                    }
                    catch { }
                }
            }

            return null;
        }

        private static List<GameObject> FilterToLikelySettlers(IEnumerable<GameObject> candidates)
        {
            var results = new List<GameObject>();
            if (candidates == null) return results;

            var seen = new HashSet<int>();
            foreach (var go in candidates)
            {
                if (go == null || !go.activeInHierarchy) continue;
                if (!seen.Add(go.GetInstanceID())) continue;
                if (!IsLikelyWorkerGameObject(go)) continue;
                results.Add(go);
            }

            return results;
        }

        private static bool IsLikelyWorkerGameObject(GameObject go)
        {
            if (go == null || !go.activeInHierarchy) return false;

            // Fast reject obvious non-settlers observed in logs.
            var lowerName = (go.name ?? string.Empty).ToLowerInvariant();
            string[] hardRejectNameTokens =
            {
                "tree", "leaf", "hay", "patch", "resource", "rock", "bush", "plant", "crop",
                "mesh", "terrain", "water", "projectile", "effect", "fire"
            };
            if (hardRejectNameTokens.Any(tok => lowerName.Contains(tok)))
                return false;

            // If we discovered a specific worker component type, that's authoritative.
            if (_individualWorkerType != null && go.GetComponent(_individualWorkerType) != null)
                return true;

            // Check for worker-like NSMedieval components in hierarchy.
            if (HasWorkerLikeNSMedievalComponent(go))
                return true;

            // Reject obvious non-worker actor categories even if animated.
            if (HasRejectedNSMedievalComponent(go))
                return false;

            // Workers should be animated (on self/parent/children).
            if (!HasAnimatorInHierarchy(go))
                return false;

            // Last chance: if resolved settler name looks human-readable (not mesh/resource id), accept.
            var resolvedName = GetSettlerName(go);
            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                var rn = resolvedName.Trim();
                if (!rn.Contains("_") && !rn.ToLowerInvariant().Contains("mesh") && rn.Any(char.IsLetter) && rn.Any(char.IsUpper))
                    return true;
            }

            return false;
        }

        private static bool HasAnimatorInHierarchy(GameObject go)
        {
            if (go == null) return false;
            if (go.GetComponent<Animator>() != null) return true;
            if (go.GetComponentInChildren<Animator>(true) != null) return true;

            var parent = go.transform.parent;
            int depth = 0;
            while (parent != null && depth++ < 3)
            {
                if (parent.GetComponent<Animator>() != null)
                    return true;
                parent = parent.parent;
            }

            return false;
        }

        private static bool HasRejectedNSMedievalComponent(GameObject go)
        {
            if (go == null) return false;

            bool IsRejected(Component c)
            {
                if (c == null) return false;
                var t = c.GetType();
                if (t.Namespace == null || !t.Namespace.StartsWith("NSMedieval")) return false;
                var lower = t.Name.ToLowerInvariant();
                return lower.Contains("animal") || lower.Contains("creature") || lower.Contains("resource") || lower.Contains("plant") || lower.Contains("crop") || lower.Contains("tree");
            }

            foreach (var c in go.GetComponents<Component>())
                if (IsRejected(c)) return true;

            return false;
        }

        private static bool HasWorkerLikeNSMedievalComponent(GameObject go)
        {
            if (go == null) return false;

            bool MatchComponent(Component c)
            {
                if (c == null) return false;
                if (!IsValidGameplayRuntimeWorkerComponent(c))
                {
                    var t = c.GetType();
                    IsValidWorkerTypeCandidate(t, out var rejectionReason);
                    LogWorkerCandidateRejectionOnce(t, rejectionReason, "hierarchy component check");
                    return false;
                }

                return true;
            }

            // Self
            foreach (var c in go.GetComponents<Component>())
                if (MatchComponent(c)) return true;

            // Parents (limited walk)
            var parent = go.transform.parent;
            int depth = 0;
            while (parent != null && depth++ < 3)
            {
                foreach (var c in parent.GetComponents<Component>())
                    if (MatchComponent(c)) return true;
                parent = parent.parent;
            }

            // Children (limited count)
            int checkedChildren = 0;
            foreach (Transform child in go.transform)
            {
                if (checkedChildren++ > 8) break;
                foreach (var c in child.GetComponents<Component>())
                    if (MatchComponent(c)) return true;
            }

            return false;
        }

        private static bool IsWorkerTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;

            var lower = typeName.ToLowerInvariant();
            if (lower.Contains("resource") || lower.Contains("tree") || lower.Contains("plant") || lower.Contains("crop") || lower.Contains("animal"))
                return false;

            return lower.Contains("worker") || lower.Contains("humanoid") || lower.Contains("settler") || lower.Contains("character") || lower.Contains("citizen") || lower.Contains("npc");
        }

        private static bool TrySetIndividualWorkerTypeCandidate(Type candidate, string source, bool allowOverwrite)
        {
            if (candidate == null)
                return false;

            if (!IsValidWorkerTypeCandidate(candidate, out var rejectionReason))
            {
                LogWorkerCandidateRejectionOnce(candidate, rejectionReason, source);
                return false;
            }

            if (_individualWorkerType != null && !allowOverwrite)
                return false;

            _individualWorkerType = candidate;
            LLMNPCsPlugin.Log.LogInfo($"[GameBridge] Updated individual worker type from {source}: {candidate.FullName}");
            return true;
        }

        private static bool TrySetWorkerControllerTypeCandidate(Type candidate, string source, bool allowOverwrite)
        {
            if (candidate == null)
                return false;

            if (!IsValidWorkerControllerTypeCandidate(candidate, out var rejectionReason))
            {
                LogWorkerCandidateRejectionOnce(candidate, rejectionReason, source);
                return false;
            }

            if (_workerControllerType != null && !allowOverwrite)
                return false;

            _workerControllerType = candidate;
            LLMNPCsPlugin.Log.LogInfo($"[GameBridge] Updated worker controller type from {source}: {candidate.FullName}");
            return true;
        }

        private static bool IsValidWorkerControllerTypeCandidate(Type type, out string rejectionReason)
        {
            rejectionReason = null;

            if (type == null)
            {
                rejectionReason = "controller type is null";
                return false;
            }

            if (!typeof(UnityEngine.Object).IsAssignableFrom(type) || !typeof(Component).IsAssignableFrom(type))
            {
                rejectionReason = "controller type is not a Unity Component";
                return false;
            }

            if (type.IsAbstract)
            {
                rejectionReason = "controller type is abstract";
                return false;
            }

            var fullName = type.FullName ?? type.Name ?? string.Empty;
            var lower = fullName.ToLowerInvariant();
            if (ContainsRejectedWorkerSemantic(lower) && !lower.Contains("workercontroller") && !lower.Contains("humanoidcontroller"))
            {
                rejectionReason = "controller type contains blocked repository/config/manager/data semantics";
                return false;
            }

            if (!lower.Contains("controller"))
            {
                rejectionReason = "controller type name missing 'controller' token";
                return false;
            }

            if (!(lower.Contains("worker") || lower.Contains("humanoid") || lower.Contains("settler") || lower.Contains("character") || lower.Contains("citizen") || lower.Contains("npc")))
            {
                rejectionReason = "controller type name missing worker-like tokens";
                return false;
            }

            return true;
        }

        private static bool IsValidWorkerTypeCandidate(Type type, out string rejectionReason)
        {
            rejectionReason = null;

            if (type == null)
            {
                rejectionReason = "type is null";
                return false;
            }

            if (!typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                rejectionReason = "not assignable to UnityEngine.Object";
                return false;
            }

            if (!typeof(Component).IsAssignableFrom(type))
            {
                rejectionReason = "not Component-derived";
                return false;
            }

            if (type.IsAbstract)
            {
                rejectionReason = "type is abstract";
                return false;
            }

            var fullName = type.FullName ?? type.Name ?? string.Empty;
            var fullLower = fullName.ToLowerInvariant();

            if (ContainsPresentationTypeSemantic(fullLower))
            {
                rejectionReason = "type is UI/image/icon/camera/repository presentation-facing";
                return false;
            }

            if (fullLower.Contains("humanoidimagerepository"))
            {
                rejectionReason = "explicitly blocked HumanoidImageRepository false-positive";
                return false;
            }

            if (ContainsRejectedWorkerSemantic(fullLower))
            {
                rejectionReason = "type contains blocked repository/definition/config/controller/manager/data/database/image-repository semantics";
                return false;
            }

            var nameLower = (type.Name ?? string.Empty).ToLowerInvariant();
            if (!WorkerTypeNameHints.Any(hint => nameLower.Contains(hint)))
            {
                rejectionReason = "type name is missing worker-like tokens";
                return false;
            }

            if (!HasInstanceBindingSemantics(type))
            {
                rejectionReason = "missing GameObject/Transform instance semantics";
                return false;
            }

            return true;
        }

        private static bool ContainsRejectedWorkerSemantic(string valueLower)
        {
            if (string.IsNullOrWhiteSpace(valueLower))
                return false;

            return WorkerTypeSemanticRejectTokens.Any(token => valueLower.Contains(token));
        }

        private static bool ContainsPresentationTypeSemantic(string valueLower)
        {
            if (string.IsNullOrWhiteSpace(valueLower))
                return false;

            if (valueLower.Contains("nsmedieval.ui") || valueLower.Contains(".ui."))
                return true;

            if (valueLower.Contains("workerimage") || valueLower.Contains("workericon") || valueLower.Contains("iconcamera") || valueLower.Contains("humanoidimage"))
                return true;

            return WorkerPresentationRejectTokens.Any(token => valueLower.Contains(token));
        }

        private static bool IsValidGameplayRuntimeWorkerComponent(Component comp)
        {
            if (comp == null) return false;
            if (!comp.gameObject || !comp.gameObject.activeInHierarchy) return false;
            if (comp is Behaviour behaviour && !behaviour.isActiveAndEnabled) return false;

            var type = comp.GetType();
            if (!IsValidWorkerTypeCandidate(type, out _)) return false;
            if (type.Namespace == null || !type.Namespace.StartsWith("NSMedieval")) return false;

            var fullLower = (type.FullName ?? type.Name ?? string.Empty).ToLowerInvariant();
            if (ContainsPresentationTypeSemantic(fullLower)) return false;
            if (ContainsRejectedWorkerSemantic(fullLower)) return false;

            // Strict runtime actor requirement: real worker/humanoid actor component only.
            var typeNameLower = (type.Name ?? string.Empty).ToLowerInvariant();
            if (!(typeNameLower.Contains("worker")
                  || typeNameLower.Contains("humanoid")
                  || typeNameLower.Contains("settler")
                  || typeNameLower.Contains("character")
                  || typeNameLower.Contains("citizen")
                  || typeNameLower.Contains("npc")))
            {
                return false;
            }

            return true;
        }

        private static bool IsValidWorkerIdentityComponent(Component comp)
        {
            if (comp == null) return false;
            if (!comp.gameObject || !comp.gameObject.activeInHierarchy) return false;

            var type = comp.GetType();
            if (!IsValidWorkerTypeCandidate(type, out _)) return false;
            if (type.Namespace == null || !type.Namespace.StartsWith("NSMedieval")) return false;

            var typeNameLower = (type.Name ?? string.Empty).ToLowerInvariant();
            return typeNameLower.Contains("worker")
                || typeNameLower.Contains("humanoid")
                || typeNameLower.Contains("settler")
                || typeNameLower.Contains("character")
                || typeNameLower.Contains("citizen")
                || typeNameLower.Contains("npc");
        }

        private static void LogAcceptedSettlerIdentities(List<Settler> settlers)
        {
            if (settlers == null) return;

            var parts = new List<string>();
            foreach (var settler in settlers)
            {
                if (settler == null || settler.gameObject == null) continue;
                if (!TryGetValidatedSettlerIdentity(settler.gameObject, out _, out var name, out var runtimeComp)) continue;

                var displayName = string.IsNullOrWhiteSpace(name) ? settler.gameObject.name : name;
                var runtimeType = runtimeComp?.GetType()?.FullName ?? "<null>";
                parts.Add($"{displayName}:{runtimeType}");
            }

            var signature = string.Join(" | ", parts);
            if (string.Equals(signature, _lastAcceptedSettlerIdentityLogSignature, StringComparison.Ordinal))
                return;

            _lastAcceptedSettlerIdentityLogSignature = signature;
            LLMNPCsPlugin.LogToFile($"[GameBridge] Accepted settler identities => {(string.IsNullOrWhiteSpace(signature) ? "<none>" : signature)}");
        }

        private static void SanitizeDiscoveredTypes()
        {
            if (_individualWorkerType != null && !IsValidWorkerTypeCandidate(_individualWorkerType, out var workerReason))
            {
                LogWorkerCandidateRejectionOnce(_individualWorkerType, workerReason, "discovered type sanitizer");
                _individualWorkerType = null;
            }

            if (_workerControllerType != null && !IsValidWorkerControllerTypeCandidate(_workerControllerType, out var controllerReason))
            {
                LogWorkerCandidateRejectionOnce(_workerControllerType, controllerReason, "discovered type sanitizer");
                _workerControllerType = null;
                _workerFieldDiscovered = false;
                _workersField = null;
                _workersProp = null;
                _workersGetterMethod = null;
            }
        }

        private static bool HasInstanceBindingSemantics(Type type)
        {
            if (type == null) return false;
            if (typeof(Component).IsAssignableFrom(type) || typeof(GameObject).IsAssignableFrom(type) || typeof(Transform).IsAssignableFrom(type))
                return true;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            if (type.GetProperty("gameObject", flags) != null || type.GetProperty("GameObject", flags) != null)
                return true;
            if (type.GetProperty("transform", flags) != null || type.GetProperty("Transform", flags) != null)
                return true;
            if (type.GetField("gameObject", flags) != null || type.GetField("GameObject", flags) != null)
                return true;
            if (type.GetField("transform", flags) != null || type.GetField("Transform", flags) != null)
                return true;

            return false;
        }

        private static void LogWorkerCandidateRejectionOnce(Type type, string reason, string source)
        {
            if (type == null || string.IsNullOrEmpty(reason)) return;

            var key = $"{type.FullName}|{reason}|{source}";
            if (!_loggedWorkerCandidateRejections.Add(key))
                return;

            var caller = GetCallingMethodName();
            LLMNPCsPlugin.LogDebug($"[GameBridge] Rejected worker type candidate {type.FullName} from {source} (caller: {caller}): {reason}");
        }

        private static bool IsWorkerLikeObject(object item)
        {
            if (item == null) return false;
            var type = item.GetType();
            if (typeof(Delegate).IsAssignableFrom(type)) return false;

            var name = type.Name.ToLowerInvariant();
            if (name.Contains("worker") || name.Contains("humanoid") || name.Contains("settler") || name.Contains("character") || name.Contains("citizen"))
                return true;

            if (type.Namespace != null && type.Namespace.StartsWith("NSMedieval") && IsWorkerTypeName(type.Name))
                return true;

            return false;
        }

        private static IEnumerable<FieldInfo> GetAllInstanceFields(Type type, BindingFlags flags)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                foreach (var field in current.GetFields(flags | BindingFlags.DeclaredOnly))
                    yield return field;
            }
        }

        private static IEnumerable<PropertyInfo> GetAllInstanceProperties(Type type, BindingFlags flags)
        {
            var seen = new HashSet<string>();
            for (var current = type; current != null; current = current.BaseType)
            {
                foreach (var prop in current.GetProperties(flags | BindingFlags.DeclaredOnly))
                {
                    var key = $"{prop.Name}:{prop.PropertyType.FullName}";
                    if (seen.Add(key))
                        yield return prop;
                }
            }
        }

        private static GameObject TryGetSelectedFromType(Type sourceType, FieldInfo preferredField, PropertyInfo preferredProp, MethodInfo preferredMethod)
        {
            if (sourceType == null) return null;

            // 1) Static selection members on the type itself.
            var selected = TryGetSelectedFromStaticMembers(sourceType);
            if (selected != null) return selected;

            // 2) Singleton/static instance resolution.
            var instance = TryResolveSingletonInstance(sourceType);

            // 3) Unity component discovery only if type is safe for FindObjectsOfType.
            if (instance == null)
            {
                instance = SafeFindFirstComponent(sourceType, $"selection source lookup ({sourceType.FullName})");
            }

            if (instance == null)
                return null;

            // Preferred members discovered from SelectionInputListener.
            selected = TryGetSelectedFromPreferredMembers(instance, preferredField, preferredProp, preferredMethod);
            if (selected != null) return selected;

            // Brute force instance scan.
            return BruteForceGetSelected(instance);
        }

        private static GameObject TryGetSelectedFromPreferredMembers(object source, FieldInfo field, PropertyInfo prop, MethodInfo method)
        {
            if (source == null) return null;
            var sourceType = source.GetType();

            try
            {
                if (method != null && method.GetParameters().Length == 0 && method.DeclaringType != null && method.DeclaringType.IsAssignableFrom(sourceType))
                {
                    var result = method.Invoke(method.IsStatic ? null : source, null);
                    var go = ExtractGameObject(result);
                    if (go != null) return go;
                }
            }
            catch { }

            try
            {
                if (prop != null && prop.CanRead)
                {
                    var getter = prop.GetGetMethod(true);
                    if (getter != null && prop.DeclaringType != null && prop.DeclaringType.IsAssignableFrom(sourceType))
                    {
                        var result = prop.GetValue(getter.IsStatic ? null : source, null);
                        var go = ExtractGameObject(result);
                        if (go != null) return go;
                    }
                }
            }
            catch { }

            try
            {
                if (field != null && field.DeclaringType != null && field.DeclaringType.IsAssignableFrom(sourceType))
                {
                    var result = field.GetValue(field.IsStatic ? null : source);
                    var go = ExtractGameObject(result);
                    if (go != null) return go;
                }
            }
            catch { }

            return null;
        }

        private static GameObject TryGetSelectedFromStaticMembers(Type sourceType)
        {
            if (sourceType == null) return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            string[] selectedNames =
            {
                "Selected", "selected", "SelectedObject", "selectedObject",
                "CurrentSelection", "currentSelection", "Selection", "selection",
                "CurrentSelected", "currentSelected", "_selected", "_selectedObject"
            };

            foreach (var name in selectedNames)
            {
                try
                {
                    var field = sourceType.GetField(name, flags);
                    if (field != null)
                    {
                        var go = ExtractGameObject(field.GetValue(null));
                        if (go != null) return go;
                    }
                }
                catch { }

                try
                {
                    var prop = sourceType.GetProperty(name, flags);
                    if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                    {
                        var go = ExtractGameObject(prop.GetValue(null, null));
                        if (go != null) return go;
                    }
                }
                catch { }
            }

            foreach (var methodName in new[] { "GetSelected", "GetSelectedObject", "GetSelection", "GetCurrentSelection" })
            {
                try
                {
                    var method = sourceType.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
                    if (method != null)
                    {
                        var go = ExtractGameObject(method.Invoke(null, null));
                        if (go != null) return go;
                    }
                }
                catch { }
            }

            return null;
        }

        private static object TryResolveSingletonInstance(Type sourceType)
        {
            if (sourceType == null) return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            string[] singletonNames = { "Instance", "instance", "Singleton", "singleton", "Current", "current", "Manager", "manager", "_instance", "_singleton" };

            foreach (var name in singletonNames)
            {
                try
                {
                    var field = sourceType.GetField(name, flags);
                    if (field != null)
                    {
                        var val = field.GetValue(null);
                        if (val != null && sourceType.IsAssignableFrom(val.GetType()))
                            return val;
                    }
                }
                catch { }

                try
                {
                    var prop = sourceType.GetProperty(name, flags);
                    if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                    {
                        var val = prop.GetValue(null, null);
                        if (val != null && sourceType.IsAssignableFrom(val.GetType()))
                            return val;
                    }
                }
                catch { }
            }

            foreach (var methodName in new[] { "GetInstance", "Get", "Current", "GetCurrent" })
            {
                try
                {
                    var method = sourceType.GetMethod(methodName, flags, null, Type.EmptyTypes, null);
                    if (method != null && sourceType.IsAssignableFrom(method.ReturnType))
                    {
                        var val = method.Invoke(null, null);
                        if (val != null) return val;
                    }
                }
                catch { }
            }

            return null;
        }

        private static UnityEngine.Object[] SafeFindObjectsOfType(Type targetType, string context)
        {
            if (targetType == null)
            {
                LogInvalidFindTypeOnce(targetType, context, "target type is null");
                return Array.Empty<UnityEngine.Object>();
            }

            if (!typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                LogInvalidFindTypeOnce(targetType, context, "target type is not assignable to UnityEngine.Object");
                return Array.Empty<UnityEngine.Object>();
            }

            // Guard against invalid runtime calls: this path is for scene instance discovery.
            if (!typeof(Component).IsAssignableFrom(targetType) && !typeof(GameObject).IsAssignableFrom(targetType))
            {
                LogInvalidFindTypeOnce(targetType, context, "target type is not Component/GameObject");
                return Array.Empty<UnityEngine.Object>();
            }

            if (targetType.IsAbstract)
            {
                LogInvalidFindTypeOnce(targetType, context, "target type is abstract");
                return Array.Empty<UnityEngine.Object>();
            }

            if (targetType.ContainsGenericParameters)
            {
                LogInvalidFindTypeOnce(targetType, context, "target type has unbound generic parameters");
                return Array.Empty<UnityEngine.Object>();
            }

            try
            {
                return UnityEngine.Object.FindObjectsOfType(targetType);
            }
            catch (Exception ex)
            {
                LogInvalidFindTypeOnce(targetType, context, $"FindObjectsOfType threw: {ex.Message}");
                return Array.Empty<UnityEngine.Object>();
            }
        }

        private static void LogInvalidFindTypeOnce(Type targetType, string context, string reason)
        {
            var typeName = targetType?.FullName ?? "<null>";
            var caller = GetCallingMethodName();
            var key = $"{typeName}|{context}|{reason}";
            if (!_loggedFindTypeRejections.Add(key))
                return;

            LLMNPCsPlugin.LogToFile($"[GameBridge] Rejected dynamic find target type '{typeName}' in {context} (caller: {caller}): {reason}");
        }

        private static string GetCallingMethodName()
        {
            try
            {
                var frames = new StackTrace(2, false).GetFrames();
                if (frames == null) return "unknown";

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    if (method == null) continue;
                    var declaringType = method.DeclaringType;
                    if (declaringType == typeof(GameBridge)) continue;
                    return $"{declaringType?.FullName ?? "unknown"}.{method.Name}";
                }
            }
            catch { }

            return "unknown";
        }

        private static string TryResolveNameFromComponent(Component comp)
        {
            if (comp == null) return null;

            var type = comp.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            string[] nameFields = { "Name", "name", "workerName", "WorkerName", "characterName", "CharacterName", "_name" };
            foreach (var fieldName in nameFields)
            {
                var field = type.GetField(fieldName, flags);
                if (field != null && field.FieldType == typeof(string))
                {
                    var val = field.GetValue(comp) as string;
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }

                var prop = type.GetProperty(fieldName, flags);
                if (prop != null && prop.PropertyType == typeof(string) && prop.CanRead)
                {
                    try
                    {
                        var val = prop.GetValue(comp, null) as string;
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                    catch { }
                }
            }

            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0 || prop.PropertyType != typeof(string))
                    continue;

                if (!prop.Name.ToLowerInvariant().Contains("name"))
                    continue;

                try
                {
                    var val = prop.GetValue(comp, null) as string;
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
                catch { }
            }

            return null;
        }

        private static string TryResolveIdFromComponent(Component comp)
        {
            if (comp == null) return null;

            var id = NPCContextExtractor.GetFieldValue<string>(comp, "id");
            if (!string.IsNullOrWhiteSpace(id))
                return id;

            id = NPCContextExtractor.GetPropertyValue<string>(comp, "Id");
            if (!string.IsNullOrWhiteSpace(id))
                return id;

            id = NPCContextExtractor.GetPropertyValue<string>(comp, "ID");
            if (!string.IsNullOrWhiteSpace(id))
                return id;

            return null;
        }

        private static object SafeFindFirstComponent(Type targetType, string context)
        {
            if (targetType == null) return null;
            if (!typeof(Component).IsAssignableFrom(targetType)) return null;

            var found = SafeFindObjectsOfType(targetType, context);
            foreach (var obj in found)
            {
                if (obj is Component comp && comp != null)
                    return comp;
            }

            return null;
        }

        private static GameObject BruteForceGetSelected(object listener)
        {
            if (listener == null) return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = listener.GetType();

            // Check all fields
            foreach (var field in type.GetFields(flags))
            {
                if (typeof(Component).IsAssignableFrom(field.FieldType) ||
                    typeof(GameObject).IsAssignableFrom(field.FieldType) ||
                    field.Name.ToLower().Contains("select"))
                {
                    try
                    {
                        var val = field.GetValue(listener);
                        var go = ExtractGameObject(val);
                        if (go != null && IsSettler(go)) return go;
                    }
                    catch { }
                }
            }

            // Check all properties
            foreach (var prop in type.GetProperties(flags))
            {
                if (!prop.CanRead) continue;
                if (typeof(Component).IsAssignableFrom(prop.PropertyType) ||
                    typeof(GameObject).IsAssignableFrom(prop.PropertyType) ||
                    prop.Name.ToLower().Contains("select"))
                {
                    try
                    {
                        var val = prop.GetValue(listener, null);
                        var go = ExtractGameObject(val);
                        if (go != null && IsSettler(go)) return go;
                    }
                    catch { }
                }
            }

            // Check lists/arrays that might contain selected objects
            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType.IsGenericType || field.FieldType.IsArray)
                {
                    try
                    {
                        var val = field.GetValue(listener);
                        if (val is System.Collections.IEnumerable enumerable)
                        {
                            foreach (var item in enumerable)
                            {
                                var go = ExtractGameObject(item);
                                if (go != null && IsSettler(go)) return go;
                            }
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(fullName);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        #endregion
    }
}

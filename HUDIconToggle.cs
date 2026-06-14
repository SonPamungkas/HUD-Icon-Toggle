using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HUDIconToggle
{
    [BepInPlugin(GUID, "HUD Icon Toggle", VERSION)]
    public class HUDIconTogglePlugin : BaseUnityPlugin
    {
        public const string GUID    = "com.hudmodding.nuclearoption.hudicontoggle";
        public const string VERSION = "1.0.1";
        // CONFIG VERSION — bump this (and delete your .cfg) when keybind layout changes.
        private const int CONFIG_VERSION = 4;

        internal static ManualLogSource Log;

        // ── Config ────────────────────────────────────────────────────────────

//        private ConfigEntry<KeyboardShortcut> _keyDump;
//        private ConfigEntry<KeyboardShortcut> _keyDumpAllUnits;

        private ConfigEntry<KeyboardShortcut> _keyMasterToggle;
        private ConfigEntry<KeyboardShortcut> _keyToggleFriendly;
        private ConfigEntry<KeyboardShortcut> _keyToggleEnemy;
        private ConfigEntry<KeyboardShortcut> _keyToggleNeutral;

        // Per-faction category keys: [factionKeyIndex, categoryIndex]
        //   factionKeyIndex 0 = Friendly, 1 = Enemy, 2 = Neutral
        private ConfigEntry<KeyboardShortcut>[,] _catKeys;

        // ── Visibility toggles (config-menu mirrors of the keybind state) ──────
        // These are the actual source of truth: keybinds flip these entries,
        // and the config menu can flip them directly too. A SettingChanged
        // handler re-applies visibility whenever either happens.
        private ConfigEntry<bool>[]   _factionVisCfg; // [faction]
        private ConfigEntry<bool>[,]  _catVisCfg;      // [faction, category]

        // Guards against re-entrant Apply calls while we're updating config
        // entries in bulk (e.g. HandleMasterToggle writing 21 entries at once).
        private bool _suppressConfigCallback;

        // ── Visibility state ──────────────────────────────────────────────────
        //
        // There is NO separate master-visible flag. All state lives in two
        // config-backed grids:
        //
        //   _factionVisCfg[fi]    — faction master switch (Friendly/Enemy/Neutral)
        //   _catVisCfg[fi, ci]    — per-faction per-category cell
        //
        // ResolveVisible = _factionVisCfg[fi].Value && _catVisCfg[fi, ci].Value
        //
        // The "master toggle" key is just a convenience that writes false/true
        // into every cell and every faction switch at once. Because there is no
        // hidden flag, any subsequent targeted key press operates directly on
        // the grid and is immediately effective. Config entries are bound to
        // SettingChanged so toggling a checkbox in the config menu re-applies
        // visibility immediately too.

        // Built once at startup from ScriptableObject type names — maps unit
        // display name (lowercase) → category, derived from the game's own
        // definition class (AircraftDefinition, ShipDefinition, etc.).
        // This is the primary classification source; keyword Rules are fallback.
        private readonly Dictionary<string, IconCategory> _typeMap
            = new Dictionary<string, IconCategory>(256, StringComparer.OrdinalIgnoreCase);

        // ── Icon cache ────────────────────────────────────────────────────────

        private readonly List<IconEntry>     _icons = new List<IconEntry>(256);
        private readonly HashSet<GameObject> _known = new HashSet<GameObject>();
        private readonly List<IconEntry>[,]  _grid  = new List<IconEntry>[3, 6];

        private Transform _iconLayer;
        private int       _layerChildCount = -1;
        private float     _cleanupTimer;
        private const float CLEANUP_INTERVAL = 5f;

        private const string HUDCANVAS_PATH = "SceneEssentials/Canvas/HUDCanvas";
        private const string ICONLAYER_NAME = "IconLayer";
        private const float  FACTION_DELTA  = 0.15f;

        // ── Classification rules (first match wins) ───────────────────────────

        private static readonly (IconCategory cat, string[] kw)[] Rules =
        {
            ( IconCategory.Buildings, new[] {
                "aircraft revetment", "airfield",
                "ammo dump", "ammo storage", "barracks", "bunker",
                "command post", "control tower", "emplacement",
                "enrichment plant", "factory", "fortif", "fuel depot",
                "fuel storage", "generator", "guard tower", "hangar",
                "hardened aircraft shelter", "helipad", "large factory",
                "munitions pallet", "outpost", "pillbox", "radar station",
                "radar tower", "refinery", "storage tank",
                "vehicle depot", "vertical factory", "structure" } ),

            ( IconCategory.Missiles, new[] {
                "aam-", "agm-", "agr-", "air-2", "alm-", "alnd-", "arad-", "atb-",
                "arm-", "ashm-", "asm", "at-145", "atgm", "atp-", "bomb",
                "cbo-", "cruise", "demolition bomb", "eyeball", "gbm-", "dt-1600",
                "glide", "gpo-", "gs25", "guided shell", "hasm-", "irm-", "missile",
                "mmr-", "nl-98", "pab-", "piledriver", "ram-45", "rocket", "shell", "glr-04",
                "sam ir", "torpedo", "tusko", "tbm-", "tbm", "vlm-", "warhead", "r6 longsword", "r9 stratolance" } ),

            ( IconCategory.Aircraft, new[] {
                "a-19", "a-19c", "alkyon", "anvil", "attackhelo", "bomber", "cas1",
                "chicane", "ci-22", "coin", "compass", "cricket",
                "darkreach", "drone", "ew-25", "ea-25b", "ew1", "f-16m", "f-99",
                "fastbomber", "fighter", "fighter1", "fs-12", "fs-12v", "fs-20", "fs-20b",
                "fs-3ex", "fq-106", "heli", "helicopter", "jet", "kestrel", "kr-67", "kr-67a",
                "longsword", "mc-260", "medusa", "mig-15", "multirole",
                "quadvtol", "rah-72", "saber", "sah-46", "sfb",
                "sfb-81", "shrike", "smallfighter", "t/a-30", "t/a-30yh", "ternion",
                "trainer", "uav", "uf-0", "uh-90", "uh-90k", "ufo", "utilityhelo",
                "vl-49", "vl-49d","vtol" } ),

            ( IconCategory.Naval, new[] {
                "annex", "argus", "assault carrier", "battleship",
                "carrier", "corvette", "cruiser", "cursor", "destroyer",
                "dynamo", "frigate", "hyperion", "landing craft", "otb-",
                "patrol boat", "shard class", "ship", "submarine",
                "vessel" } ),

            ( IconCategory.Ground, new[] {
                "aerosentry", "afv", "afv-", "afv6", "apc","apc-", "apm-71", "artillery", "agm-98 launcher container", "aa gun container",
                "boltstrike", "bs200", "fga-30",
                "fga-57", "field deployable airpad", "fire control",
                "flatbed", "frcv-105", "fuel tanker", "hexhound", "horse",
                "hlt", "hlt-","howitzer", "ifv", "jackknife", "lcv25", "lcv45", "lcv-",
                "lcv-25", "lcv-45", "launcher container", "linebreaker", "mbt", "mlrs",
                "mobile air defense", "mortar", "msv", "msv-", "munitions truck",
                "radar container", "radar truck", "ram45 launcher", "ram45 sam launcher", "ram-45 launcher container", "r9 stratolance sam launcher",
                "recon truck", "sam", "spaa", "spaag", "spearhead",
                "stratolance", "slmmr-", "t9k41", "tractor", "type-12", "type-14",
                "wreck mbt" } ),
        };

        private static readonly HashSet<string> Ignored =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "targetArrow", "targetText", "TargetCode",
              "objectivePointer", "ObjectiveInfo" };

        // Static — compiled once, reused on every Classify call.
        private static readonly System.Text.RegularExpressions.Regex s_netIdSuffix
            = new System.Text.RegularExpressions.Regex(
                @"\[\d+\]$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // ── Helper arrays for iteration ───────────────────────────────────────

        private static readonly IconFaction[]  AllFactions   = (IconFaction[]) Enum.GetValues(typeof(IconFaction));
        private static readonly IconCategory[] AllCategories = (IconCategory[])Enum.GetValues(typeof(IconCategory));

        // Maps the three keyed factions (index 0/1/2) to IconFaction enum values
        private static readonly IconFaction[] KeyFactions =
            { IconFaction.Friendly, IconFaction.Enemy, IconFaction.Neutral };

        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            Log = Logger;

            for (int f = 0; f < 3; f++)
                for (int c = 0; c < 6; c++)
                    _grid[f, c] = new List<IconEntry>(32);

            BindConfig();
            BuildTypeMap();
            new Harmony(GUID).PatchAll();
            SceneManager.sceneLoaded += OnSceneLoaded;

            Log.LogInfo($"HUD Icon Toggle v{VERSION} loaded. (config v{CONFIG_VERSION})");
            LogKeybinds(); // outputs at LogDebug level — silent in normal BepInEx config
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Rebuild the type map on each scene load so late-loading mod
            // definitions (loaded after our Awake) are always picked up.
            // Also reset the icon layer ref — it's recreated per mission.
            _iconLayer       = null;
            _layerChildCount = -1;
            BuildTypeMap();
        }

        // ── Config binding ────────────────────────────────────────────────────

        private void BindConfig()
        {
            _factionVisCfg = new ConfigEntry<bool>[3];
            _catVisCfg     = new ConfigEntry<bool>[3, 6];

            // ── Debug ─────────────────────────────────────────────────────────
//         _keyDump = Config.Bind(
//               "Debug",
//                "Dump Icon Layer",
//                new KeyboardShortcut(KeyCode.F1, KeyCode.RightShift),
//                "Dumps all IconLayer children and current visibility state to the BepInEx log.");

//            _keyDumpAllUnits = Config.Bind(
//                "Debug",
//                "Dump All Unit Definitions",
//               new KeyboardShortcut(KeyCode.F2, KeyCode.RightShift),
//                "Dumps the display name of every unit/building type the game has loaded, " +
//                "regardless of whether it's currently spawned. Can be used from the main menu.");

            // ── Master Toggles ────────────────────────────────────────────────
            _keyMasterToggle = Config.Bind(
                "Master Toggles",
                "Toggle All Icons",
                new KeyboardShortcut(KeyCode.Alpha0, KeyCode.Backspace),
                new ConfigDescription(
                    "Hide every HUD icon at once. Press again to restore all. " +
                    "After hiding all, any per-faction or per-category key will work immediately.",
                    null, new ConfigurationManagerAttributes { Order = 100 }));

            _keyToggleFriendly = Config.Bind(
                "Master Toggles",
                "Toggle All Friendlies",
                new KeyboardShortcut(KeyCode.Alpha0, KeyCode.RightShift),
                new ConfigDescription(
                    "Show / hide all friendly icons. Resets per-category overrides for friendlies.",
                    null, new ConfigurationManagerAttributes { Order = 90 }));

            _factionVisCfg[(int)IconFaction.Friendly] = Config.Bind(
                "Master Toggles",
                "Friendlies Visible",
                true,
                new ConfigDescription(
                    "Current visibility state for all friendly icons. Mirrors 'Toggle All Friendlies'.",
                    null, new ConfigurationManagerAttributes { Order = 89 }));

            _keyToggleEnemy = Config.Bind(
                "Master Toggles",
                "Toggle All Enemies",
                new KeyboardShortcut(KeyCode.Alpha0, KeyCode.LeftShift),
                new ConfigDescription(
                    "Show / hide all enemy icons. Resets per-category overrides for enemies.",
                    null, new ConfigurationManagerAttributes { Order = 80 }));

            _factionVisCfg[(int)IconFaction.Enemy] = Config.Bind(
                "Master Toggles",
                "Enemies Visible",
                true,
                new ConfigDescription(
                    "Current visibility state for all enemy icons. Mirrors 'Toggle All Enemies'.",
                    null, new ConfigurationManagerAttributes { Order = 79 }));

            _keyToggleNeutral = Config.Bind(
                "Master Toggles",
                "Toggle All Neutrals",
                new KeyboardShortcut(KeyCode.Alpha0, KeyCode.RightControl),
                new ConfigDescription(
                    "Show / hide all neutral / uncaptured structure icons. " +
                    "Resets per-category overrides for neutrals.",
                    null, new ConfigurationManagerAttributes { Order = 70 }));

            _factionVisCfg[(int)IconFaction.Neutral] = Config.Bind(
                "Master Toggles",
                "Neutrals Visible",
                true,
                new ConfigDescription(
                    "Current visibility state for all neutral icons. Mirrors 'Toggle All Neutrals'.",
                    null, new ConfigurationManagerAttributes { Order = 69 }));

            // ── Per-faction category keys ─────────────────────────────────────
            // Friendly  → RightShift  + Alpha1–6
            // Enemy     → LeftShift   + Alpha1–6
            // Neutral   → RightControl+ Alpha1–6
            //
            // Categories map to Alpha keys in IconCategory enum order:
            //   Alpha1=Missiles  Alpha2=Buildings  Alpha3=Aircraft
            //   Alpha4=Naval     Alpha5=Ground     Alpha6=Other

            string[] sections = { "Friendly Units", "Enemy Units", "Neutral Units" };

            KeyCode[] catAlphas =
            {
                KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3,
                KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6,
            };

            KeyCode[] modifiers = { KeyCode.RightShift, KeyCode.LeftShift, KeyCode.RightControl };

            _catKeys = new ConfigEntry<KeyboardShortcut>[3, 6];

            for (int fi = 0; fi < 3; fi++)
            {
                string factionLabel = sections[fi].Replace(" Units", "");
                for (int ci = 0; ci < 6; ci++)
                {
                    IconCategory cat = (IconCategory)ci;
                    int order = (6 - ci) * 10; // higher order = shown first

                    _catKeys[fi, ci] = Config.Bind(
                        sections[fi],
                        $"Toggle {cat}",
                        new KeyboardShortcut(catAlphas[ci], modifiers[fi]),
                        new ConfigDescription(
                            $"Show / hide {factionLabel} {cat} icons. " +
                            $"If the {factionLabel} faction master is hidden, this restores it first.",
                            null, new ConfigurationManagerAttributes { Order = order }));

                    _catVisCfg[fi, ci] = Config.Bind(
                        sections[fi],
                        $"{cat} Visible",
                        true,
                        new ConfigDescription(
                            $"Current visibility state for {factionLabel} {cat} icons. " +
                            $"Mirrors 'Toggle {cat}'.",
                            null, new ConfigurationManagerAttributes { Order = order - 1 }));
                }
            }

            // React to in-menu checkbox changes (and to our own writes below).
            foreach (var e in _factionVisCfg) e.SettingChanged += OnVisibilityConfigChanged;
            foreach (var e in _catVisCfg)     e.SettingChanged += OnVisibilityConfigChanged;
        }

        // Fired whenever a visibility checkbox changes, whether from a keybind
        // (we write the ConfigEntry ourselves) or from the user editing the
        // config menu directly. Re-applies visibility to match the new state.
        private void OnVisibilityConfigChanged(object sender, EventArgs e)
        {
            if (_suppressConfigCallback) return;
            ApplyAll();
        }

        // ── Update ────────────────────────────────────────────────────────────

        private void Update()
        {
            ScanForNewIcons();

            _cleanupTimer -= Time.unscaledDeltaTime;
            if (_cleanupTimer <= 0f)
            {
                PruneDeadEntries();
                _cleanupTimer = CLEANUP_INTERVAL;
            }

            // Keybind checks — early return on first match to avoid double-firing.
//            if (_keyDump.Value.IsDown())          { DumpIconLayer();                          return; }
//            if (_keyDumpAllUnits.Value.IsDown())  { DumpAllUnitDefinitions();                 return; }
            if (_keyMasterToggle.Value.IsDown())  { HandleMasterToggle();                     return; }
            if (_keyToggleFriendly.Value.IsDown()){ HandleFactionToggle(IconFaction.Friendly); return; }
            if (_keyToggleEnemy.Value.IsDown())   { HandleFactionToggle(IconFaction.Enemy);    return; }
            if (_keyToggleNeutral.Value.IsDown()) { HandleFactionToggle(IconFaction.Neutral);  return; }

            for (int fi = 0; fi < 3; fi++)
                for (int ci = 0; ci < 6; ci++)
                    if (_catKeys[fi, ci].Value.IsDown())
                    {
                        HandleCategoryToggle(KeyFactions[fi], (IconCategory)ci);
                        return;
                    }
        }

        // ── Toggle handlers ───────────────────────────────────────────────────

        // Master toggle: hide-all / restore-all.
        // Intent is derived from actual state: if anything is currently hidden
        // (any faction switch off, or any grid cell false), restore everything.
        // Only hides when everything is already fully visible.
        private void HandleMasterToggle()
        {
            bool anythingHidden = false;
            for (int f = 0; f < 3; f++)
            {
                if (!_factionVisCfg[f].Value) { anythingHidden = true; break; }
                for (int c = 0; c < 6; c++)
                    if (!_catVisCfg[f, c].Value) { anythingHidden = true; break; }
                if (anythingHidden) break;
            }

            _suppressConfigCallback = true;
            try
            {
                if (anythingHidden)
                {
                    // Restore everything.
                    for (int f = 0; f < 3; f++)
                    {
                        _factionVisCfg[f].Value = true;
                        for (int c = 0; c < 6; c++)
                            _catVisCfg[f, c].Value = true;
                    }
                    Log.LogDebug("All icons SHOWN.");
                }
                else
                {
                    // Everything visible — hide it all.
                    for (int f = 0; f < 3; f++)
                    {
                        _factionVisCfg[f].Value = false;
                        for (int c = 0; c < 6; c++)
                            _catVisCfg[f, c].Value = true; // cells stay true; faction switch does the hiding
                    }
                    Log.LogDebug("All icons HIDDEN. (any targeted key will override this)");
                }
            }
            finally
            {
                _suppressConfigCallback = false;
            }

            ApplyAll();
        }

        // Faction master: flip show/hide for all icons of that faction;
        // resets per-category cells so they can't silently contradict the switch.
        private void HandleFactionToggle(IconFaction fac)
        {
            int fi = (int)fac;
            bool newVal = !_factionVisCfg[fi].Value;

            _suppressConfigCallback = true;
            try
            {
                _factionVisCfg[fi].Value = newVal;

                // Reset category cells — the faction switch is now authoritative.
                for (int c = 0; c < 6; c++)
                    _catVisCfg[fi, c].Value = true;
            }
            finally
            {
                _suppressConfigCallback = false;
            }

            ApplyFactionRows(fac);
            Log.LogDebug($"{fac} (all): {(newVal ? "SHOWN" : "HIDDEN")} " +
                        $"({CountFaction(fac)} icons) — category overrides reset.");
        }

        // Category key: flip one faction/category cell.
        // If the faction master is currently OFF, this is treated as an explicit
        // "show this type" intent — restore the faction master, reset all cells
        // for that faction to true, then ensure the target cell is visible.
        // We do NOT toggle in this case: the player's first press should always show.
        private void HandleCategoryToggle(IconFaction fac, IconCategory cat)
        {
            int fi = (int)fac;
            int ci = (int)cat;

            if (!_factionVisCfg[fi].Value)
            {
                // Faction was globally hidden — restore it and show only this category.
                _suppressConfigCallback = true;
                try
                {
                    _factionVisCfg[fi].Value = true;
                    for (int c = 0; c < 6; c++)
                        _catVisCfg[fi, c].Value = false;  // hide everything for this faction...
                    _catVisCfg[fi, ci].Value = true;       // ...then show only the requested cell
                }
                finally
                {
                    _suppressConfigCallback = false;
                }

                ApplyFactionRows(fac);
                Log.LogDebug($"[{fac}] {cat}: SHOWN (from hidden) ({CountFactionCategory(fac, cat)} icons)");
                return;
            }

            // Normal case: faction is visible, just toggle the cell.
            bool newVal = !_catVisCfg[fi, ci].Value;
            _catVisCfg[fi, ci].Value = newVal; // fires OnVisibilityConfigChanged → ApplyAll

            Log.LogDebug($"[{fac}] {cat}: {(newVal ? "SHOWN" : "HIDDEN")} " +
                        $"({CountFactionCategory(fac, cat)} icons)");
        }

        // ── Cache / scan ──────────────────────────────────────────────────────

        // Static — allocated once, never changes.
        private static readonly Dictionary<string, IconCategory> s_typeToCategory
            = new Dictionary<string, IconCategory>(StringComparer.Ordinal)
        {
            { "AircraftDefinition",  IconCategory.Aircraft  },
            { "AircraftParameters",  IconCategory.Aircraft  },
            { "BuildingDefinition",  IconCategory.Buildings },
            { "MissileDefinition",   IconCategory.Missiles  },
            { "ShipDefinition",      IconCategory.Naval     },
            { "VehicleDefinition",   IconCategory.Ground    },
            { "UnitDefinition",      IconCategory.Ground    },
            // SceneryDefinition intentionally absent — props never show as icons.
        };

        private void BuildTypeMap()
        {
            _typeMap.Clear();

            int mapped = 0;
            foreach (var obj in Resources.FindObjectsOfTypeAll<ScriptableObject>())
            {
                if (!s_typeToCategory.TryGetValue(obj.GetType().Name, out var cat)) continue;

                string displayName = GetDisplayName(obj) ?? obj.name;
                if (string.IsNullOrEmpty(displayName)) continue;

                // Store both display name and asset name (lowercased) so either
                // form that might appear as a spawned icon name will match.
                _typeMap[displayName.ToLowerInvariant()] = cat;
                if (!string.Equals(displayName, obj.name, StringComparison.OrdinalIgnoreCase))
                    _typeMap[obj.name.ToLowerInvariant()] = cat;

                mapped++;
            }

            Log.LogInfo($"TypeMap built: {mapped} definitions → {_typeMap.Count} name entries.");
        }

        private Transform GetIconLayer()
        {
            if (_iconLayer != null && _iconLayer.gameObject != null) return _iconLayer;
            _iconLayer = null;
            var hud = GameObject.Find(HUDCANVAS_PATH);
            if (hud == null) return null;
            _iconLayer = hud.transform.Find(ICONLAYER_NAME);
            _layerChildCount = _iconLayer != null ? _iconLayer.childCount : -1;
            return _iconLayer;
        }

        private void ScanForNewIcons()
        {
            var layer = GetIconLayer();
            if (layer == null) return;

            int current = layer.childCount;
            if (current == _layerChildCount) return;
            _layerChildCount = current;

            foreach (Transform child in layer)
            {
                var go = child.gameObject;
                if (_known.Contains(go)) continue;

                _known.Add(go);
                if (ShouldIgnore(go.name)) continue;

                var fac   = GetFactionFromColor(go);
                var cat   = Classify(go.name.ToLowerInvariant());
                var entry = new IconEntry(go, fac, cat);

                EnsureCanvasGroup(go);
                _icons.Add(entry);
                _grid[(int)fac, (int)cat].Add(entry);

                SetVisible(go, ResolveVisible(entry));
            }
        }

        private void PruneDeadEntries()
        {
            bool dirty = false;
            for (int i = _icons.Count - 1; i >= 0; i--)
            {
                if (_icons[i].Go != null) continue;
                _icons.RemoveAt(i);
                dirty = true;
            }

            if (!dirty) return;

            for (int f = 0; f < 3; f++)
                for (int c = 0; c < 6; c++)
                    _grid[f, c].Clear();

            foreach (var e in _icons)
                _grid[(int)e.Faction, (int)e.Category].Add(e);

            // Rebuild _known from surviving entries
            _known.Clear();
            foreach (var e in _icons) _known.Add(e.Go);
        }

        // ── Visibility application ────────────────────────────────────────────

        private void ApplyAll()
        {
            for (int i = _icons.Count - 1; i >= 0; i--)
            {
                var e = _icons[i];
                if (e.Go == null) { _icons.RemoveAt(i); continue; }
                SetVisible(e.Go, ResolveVisible(e));
            }
        }

        private void ApplyFactionRows(IconFaction fac)
        {
            int fi = (int)fac;
            for (int ci = 0; ci < 6; ci++)
            {
                var list = _grid[fi, ci];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Go == null) { list.RemoveAt(i); continue; }
                    SetVisible(list[i].Go, ResolveVisible(list[i]));
                }
            }
        }

        // Single source of truth: faction switch AND grid cell must both be true.
        private bool ResolveVisible(IconEntry e)
        {
            int fi = (int)e.Faction, ci = (int)e.Category;
            return _factionVisCfg[fi].Value && _catVisCfg[fi, ci].Value;
        }

        // ── SetVisible ────────────────────────────────────────────────────────

        private static void EnsureCanvasGroup(GameObject go)
        {
            if (go.GetComponent<CanvasGroup>() == null)
                go.AddComponent<CanvasGroup>();
        }

        private static void SetVisible(GameObject go, bool visible)
        {
            var cg = go.GetComponent<CanvasGroup>() ?? go.AddComponent<CanvasGroup>();
            cg.alpha          = visible ? 1f : 0f;
            cg.blocksRaycasts = visible;
            cg.interactable   = visible;
        }

        // ── Counters ──────────────────────────────────────────────────────────

        private int CountFaction(IconFaction fac)
        {
            int fi = (int)fac, n = 0;
            for (int ci = 0; ci < 6; ci++) n += _grid[fi, ci].Count;
            return n;
        }

        private int CountFactionCategory(IconFaction fac, IconCategory cat)
            => _grid[(int)fac, (int)cat].Count;

        // ── Faction colour detection ──────────────────────────────────────────

        private static IconFaction GetFactionFromColor(GameObject go)
        {
            var img = go.GetComponent<Image>();
            if (img == null) return IconFaction.Neutral;

            float r = img.color.r, g = img.color.g, b = img.color.b;
            if (b - r > FACTION_DELTA && b > g * 0.5f) return IconFaction.Friendly; // blue ally
            if (g - r > FACTION_DELTA)                 return IconFaction.Friendly; // green player
            if (r - g > FACTION_DELTA)                 return IconFaction.Enemy;
            return IconFaction.Neutral;
        }

        // ── Classification ────────────────────────────────────────────────────

        private static bool ShouldIgnore(string name)
        {
            if (Ignored.Contains(name)) return true;
            if (name.StartsWith("hitmarker",    StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("radarWarning", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private IconCategory Classify(string nl)
        {
            // Primary: type-map lookup. Strip the [NetID] suffix spawned icons append.
            string stripped = s_netIdSuffix.Replace(nl, "").Trim();
            if (_typeMap.TryGetValue(stripped, out var mapped)) return mapped;

            // Fallback: keyword rules for mods using non-standard definition types.
            foreach (var (cat, kws) in Rules)
                foreach (var kw in kws)
                    if (nl.Contains(kw)) return cat;

            return IconCategory.Other;
        }

        // ── Diagnostic dump ───────────────────────────────────────────────────

        private void DumpIconLayer()
        {
            Log.LogInfo("=== HUD ICON TOGGLE: DUMP START ===");
            var layer = GetIconLayer();
            if (layer == null)
            {
                Log.LogWarning("  IconLayer not found.");
                Log.LogInfo("=== HUD ICON TOGGLE: DUMP END ===");
                return;
            }

            Log.LogInfo($"  Path: {GetPath(layer)}  |  Children: {layer.childCount}  |  Cached: {_icons.Count}");
            Log.LogInfo($"  factionVis: Friendly={_factionVisCfg[0].Value}  Enemy={_factionVisCfg[1].Value}  Neutral={_factionVisCfg[2].Value}");
            Log.LogInfo("");

            Log.LogInfo("  visGrid (faction × category):");
            foreach (var fac in AllFactions)
            {
                var sb = new System.Text.StringBuilder($"    {fac,-10}: ");
                foreach (var cat in AllCategories)
                    sb.Append($"{cat}={_catVisCfg[(int)fac,(int)cat].Value}  ");
                Log.LogInfo(sb.ToString());
            }
            Log.LogInfo("");

            var counts = new int[3, 6];
            foreach (Transform child in layer)
            {
                string name = child.gameObject.name;
                if (ShouldIgnore(name)) continue;
                var cat = Classify(name.ToLowerInvariant());
                var fac = GetFactionFromColor(child.gameObject);
                counts[(int)fac, (int)cat]++;

                var img = child.gameObject.GetComponent<Image>();
                string col = img != null
                    ? $"r={img.color.r:F2} g={img.color.g:F2} b={img.color.b:F2}"
                    : "no Image";
                Log.LogInfo($"  [{fac}/{cat}] \"{name}\"  color=({col})");
            }

            Log.LogInfo("");
            Log.LogInfo("  --- Totals ---");
            foreach (var fac in AllFactions)
                foreach (var cat in AllCategories)
                    if (counts[(int)fac, (int)cat] > 0)
                        Log.LogInfo($"    {fac} / {cat}: {counts[(int)fac, (int)cat]}");

            Log.LogInfo("");
            Log.LogInfo("  --- Unclassified (Other) icons currently spawned: ---");
            foreach (Transform child in layer)
            {
                string name = child.gameObject.name;
                if (ShouldIgnore(name)) continue;
                if (Classify(name.ToLowerInvariant()) == IconCategory.Other)
                    Log.LogInfo($"    UNCLASSIFIED: \"{name}\"");
            }

            Log.LogInfo("=== HUD ICON TOGGLE: DUMP END ===");
        }

        // Dumps the display name of every unit/building "Parameters" or
        // "Definition" ScriptableObject currently loaded in memory, regardless
        // of whether it's spawned in a mission. Can be run from the main menu.
        private void DumpAllUnitDefinitions()
        {
            Log.LogInfo("=== HUD ICON TOGGLE: ALL UNIT DEFINITIONS ===");

            var allObjects = Resources.FindObjectsOfTypeAll<ScriptableObject>();
            Log.LogInfo($"  Total ScriptableObjects in memory: {allObjects.Length}");

            // Collect all unique type names first so we can see what's available
            var typeNames = new SortedSet<string>();
            foreach (var obj in allObjects)
                typeNames.Add(obj.GetType().Name);

            Log.LogInfo("  --- All ScriptableObject type names found: ---");
            foreach (var tn in typeNames)
                Log.LogInfo($"    {tn}");

            Log.LogInfo("  --- Entries matching 'Parameters' or 'Definition': ---");
            int total = 0;
            var unclassified = new List<string>();

            foreach (var obj in allObjects)
            {
                string typeName = obj.GetType().Name;
                if (!typeName.Contains("Parameters") && !typeName.Contains("Definition"))
                    continue;

                string displayName = GetDisplayName(obj) ?? obj.name;
                var cat = Classify(displayName.ToLowerInvariant());
                Log.LogInfo($"  [{typeName}] [{cat}] \"{displayName}\"  (asset: \"{obj.name}\")");
                total++;

                if (cat == IconCategory.Other)
                    unclassified.Add(displayName);
            }

            Log.LogInfo($"  --- Total matching definitions: {total} ---");
            Log.LogInfo("  --- Unclassified (Other): ---");
            foreach (var n in unclassified)
                Log.LogInfo($"    UNCLASSIFIED: \"{n}\"");

            Log.LogInfo("=== END ALL UNIT DEFINITIONS ===");
        }

        // Reflection helper: looks for a human-readable display-name field/property.
        // Results are cached per Type so identical definition types only pay the
        // reflection cost once across the BuildTypeMap scan.
        private static readonly Dictionary<Type, System.Reflection.MemberInfo> s_displayNameCache
            = new Dictionary<Type, System.Reflection.MemberInfo>();

        private static readonly string[] s_displayNameCandidates
            = { "displayName", "DisplayName", "unitName", "UnitName" };

        private static readonly System.Reflection.BindingFlags s_bfInstance
            = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic;

        private static string GetDisplayName(object obj)
        {
            var type = obj.GetType();

            if (!s_displayNameCache.TryGetValue(type, out var member))
            {
                member = null;
                foreach (var name in s_displayNameCandidates)
                {
                    var f = type.GetField(name, s_bfInstance);
                    if (f != null && f.FieldType == typeof(string)) { member = f; break; }
                    var p = type.GetProperty(name, s_bfInstance);
                    if (p != null && p.PropertyType == typeof(string)) { member = p; break; }
                }
                s_displayNameCache[type] = member; // cache even if null
            }

            if (member is System.Reflection.FieldInfo fi)
            {
                var val = fi.GetValue(obj) as string;
                if (!string.IsNullOrEmpty(val)) return val;
            }
            else if (member is System.Reflection.PropertyInfo pi)
            {
                var val = pi.GetValue(obj) as string;
                if (!string.IsNullOrEmpty(val)) return val;
            }

            return null;
        }

        private void LogKeybinds()
        {
            Log.LogDebug($"HUD Icon Toggle v{VERSION} — keybind layout:");
//            Log.LogDebug($"  [Debug]   Dump:               {_keyDump.Value}");
//            Log.LogDebug($"  [Debug]   Dump all units:     {_keyDumpAllUnits.Value}");
            Log.LogDebug($"  [Master]  All icons:           {_keyMasterToggle.Value}");
            Log.LogDebug($"  [Master]  All friendlies:      {_keyToggleFriendly.Value} (visible={_factionVisCfg[0].Value})");
            Log.LogDebug($"  [Master]  All enemies:         {_keyToggleEnemy.Value} (visible={_factionVisCfg[1].Value})");
            Log.LogDebug($"  [Master]  All neutrals:        {_keyToggleNeutral.Value} (visible={_factionVisCfg[2].Value})");
            string[] factionLabels = { "Friendly", "Enemy  ", "Neutral" };
            for (int fi = 0; fi < 3; fi++)
            {
                Log.LogDebug($"  [{factionLabels[fi]} category keys]");
                for (int ci = 0; ci < 6; ci++)
                    Log.LogDebug($"    {(IconCategory)ci,-12}: {_catKeys[fi, ci].Value}  (visible={_catVisCfg[fi, ci].Value})");
            }
        }

        private static string GetPath(Transform t)
        {
            var parts = new Stack<string>();
            while (t != null) { parts.Push(t.name); t = t.parent; }
            return string.Join("/", parts);
        }

        // ── Data types ────────────────────────────────────────────────────────

        private class IconEntry
        {
            public readonly GameObject   Go;
            public readonly IconFaction  Faction;
            public readonly IconCategory Category;
            public IconEntry(GameObject go, IconFaction fac, IconCategory cat)
            { Go = go; Faction = fac; Category = cat; }
        }
    }

    public enum IconCategory { Missiles, Buildings, Aircraft, Naval, Ground, Other }
    public enum IconFaction   { Friendly, Enemy, Neutral }

    // ── ConfigurationManagerAttributes stub ───────────────────────────────────
    // Copied inline so we don't need a hard reference to the Configuration Manager
    // DLL. When the mod is absent the attributes are silently ignored by BepInEx;
    // when it is present it picks up these properties via reflection and uses them
    // to control ordering, descriptions, and read-only display in the F1 menu.
#pragma warning disable CS0649 // Fields set via reflection by Configuration Manager
    internal sealed class ConfigurationManagerAttributes
    {
        public bool?   Browsable;
        public bool?   ReadOnly;
        public bool?   IsAdvanced;
        public int?    Order;
        public string  Category;
        public string  DispName;
        public string  Description;
        public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
    }
#pragma warning restore CS0649
}

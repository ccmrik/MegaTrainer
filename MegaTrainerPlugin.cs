using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MegaTrainer
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class MegaTrainerPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.rik.megatrainer";
        public const string PluginName = "MegaTrainer";
        public const string PluginVersion = "1.6.0";

        internal static ManualLogSource Log;
        private static Harmony _harmony;
        private static FileSystemWatcher _stateWatcher;

        // Workspace-standard debug toggle. Silent by default; flip on when
        // diagnosing an issue. Existing Log.LogInfo calls are milestone/status
        // messages kept always-on; DebugLog() is the channel for new chatter.
        public static ConfigEntry<bool> DebugMode;

        /// <summary>Gated diagnostic log. Silent unless DebugMode = true.</summary>
        public static void DebugLog(string msg)
        {
            if (DebugMode?.Value == true) Log?.LogInfo(msg);
        }

        // Current cheat states — read by Harmony patches
        internal static Dictionary<string, bool> CheatStates = new Dictionary<string, bool>();
        private static string _trainerStatePath;

        // Speed & Jump multipliers — adjusted via Num+/Num- keys
        internal static float SpeedMultiplier = 1.0f;
        internal static float JumpMultiplier = 1.0f;

        // Cached FieldInfo for Player.m_debugFly (private field — the "fly" command toggles this)
        private static FieldInfo _debugFlyField;
        private static bool _debugFlyFieldSearched;

        // Cached FieldInfo for Player.m_noPlacementCost (private field — free build toggle)
        private static FieldInfo _noPlacementCostField;
        private static bool _noPlacementCostFieldSearched;

        // HUD overlay state
        private static float _hudShowTime = -10f;
        // Auto-tame timer (runs every 2 seconds while tame_all is enabled)
        private static float _nextTameCheck = 0f;
        private const float HudVisibleDuration = 5f;
        private const float HudFadeDuration = 0.5f;

        // Per-frame state tracking — only call setters when state changes
        private static bool _lastGhostState;
        private static bool _lastFlyState;
        private static bool _lastNoCostState;
        private static bool _lastRestedState;
        // Cached rested status effect ref to avoid lookup every frame
        private static StatusEffect _cachedRestedSE;
        private static int _cachedRestedPlayer;  // instance ID to invalidate on player change
        private static float _nextRestedReset;  // throttle reflection write to once per second
        private static GUIStyle _hudLabelStyle;
        private static GUIStyle _hudHeaderStyle;
        private static GUIStyle _hudKeyStyle;
        private static GUIStyle _hudOnStyle;
        private static GUIStyle _hudOffStyle;
        private static Texture2D _bgTex;
        private static Texture2D _borderTex;

        // Thread-safe flags — set from FileSystemWatcher bg thread, consumed on main thread
        private static volatile bool _pendingHudShow;
        private static volatile bool _pendingStateReload;

        // Numpad → cheat ID mapping
        private struct NumpadBinding
        {
            public readonly KeyCode Key;
            public readonly bool RequiresAlt;
            public readonly string CheatId;
            public readonly string Label;
            public NumpadBinding(KeyCode key, bool requiresAlt, string cheatId, string label)
            { Key = key; RequiresAlt = requiresAlt; CheatId = cheatId; Label = label; }
        }

        private static readonly NumpadBinding[] NumpadBindings = new[]
        {
            // Base numpad bindings (no modifier)
            new NumpadBinding(KeyCode.Keypad0, false, "god_mode", "God Mode"),
            new NumpadBinding(KeyCode.Keypad1, false, "unlimited_stamina", "Unlimited Stamina"),
            new NumpadBinding(KeyCode.Keypad2, false, "unlimited_weight", "Unlimited Carry Weight"),
            new NumpadBinding(KeyCode.Keypad3, false, "ghost_mode", "Ghost Mode"),
            new NumpadBinding(KeyCode.Keypad4, false, "fly_mode", "Fly Mode"),
            new NumpadBinding(KeyCode.Keypad5, false, "no_placement_cost", "Free Build"),
            new NumpadBinding(KeyCode.Keypad6, false, "no_weather_damage", "No Weather Damage"),
            new NumpadBinding(KeyCode.Keypad7, false, "instant_kill", "One-Hit Kill"),
            new NumpadBinding(KeyCode.Keypad8, false, "no_durability_loss", "No Durability Loss"),
            // ALT + numpad bindings
            new NumpadBinding(KeyCode.Keypad0, true, "explore_map", "Reveal Map"),
            new NumpadBinding(KeyCode.Keypad1, true, "always_rested", "Always Rested"),
            new NumpadBinding(KeyCode.Keypad2, true, "infinite_eitr", "Infinite Eitr"),
            new NumpadBinding(KeyCode.Keypad3, true, "tame_all", "Instant Tame"),
            new NumpadBinding(KeyCode.Keypad4, true, "no_structure_damage", "Invincible Structures"),
            new NumpadBinding(KeyCode.Keypad5, true, "fast_skill_up", "Fast Skill Up"),
        };

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            DebugMode = Config.Bind(
                "99. Debug",
                "DebugMode",
                false,
                "Enable verbose debug logging for MegaTrainer (patch traces, state reload chatter)"
            );

            // Find trainer_state.json — MegaLoad writes it next to the BepInEx folder
            // BepInEx plugins path: .../BepInEx/plugins/MegaTrainer/
            // trainer_state.json:   .../trainer_state.json (sibling of BepInEx/)
            string pluginDir = Path.GetDirectoryName(Info.Location);
            string bepinexDir = Path.GetFullPath(Path.Combine(pluginDir, "..", ".."));
            string profileDir = Path.GetDirectoryName(bepinexDir);
            _trainerStatePath = Path.Combine(profileDir, "trainer_state.json");

            Log.LogInfo($"Trainer state path: {_trainerStatePath}");

            LoadState();
            SetupWatcher();

            // Apply Harmony patches individually so one failure doesn't kill the rest
            _harmony = new Harmony(PluginGUID);
            PatchAllIndividually();

            Log.LogInfo($"{PluginName} loaded! Watching for MegaLoad trainer toggles.");
        }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(_trainerStatePath))
                {
                    Log.LogInfo("No trainer_state.json found — all cheats OFF.");
                    CheatStates.Clear();
                    return;
                }

                string json = File.ReadAllText(_trainerStatePath);
                var data = JsonParser.ParseTrainerData(json);

                var newStates = new Dictionary<string, bool>();
                foreach (var entry in data)
                    newStates[entry.Key] = entry.Value;

                // Log changes
                foreach (var kv in newStates)
                {
                    bool wasEnabled = CheatStates.ContainsKey(kv.Key) && CheatStates[kv.Key];
                    if (kv.Value != wasEnabled)
                        Log.LogInfo($"Cheat '{kv.Key}' → {(kv.Value ? "ON" : "OFF")}");
                }

                CheatStates = newStates;

                // Read multipliers from state file
                float newSpeed = JsonParser.ExtractFloatValue(json, "speed_multiplier", 1.0f);
                float newJump = JsonParser.ExtractFloatValue(json, "jump_multiplier", 1.0f);
                if (!Mathf.Approximately(newSpeed, SpeedMultiplier))
                    Log.LogInfo($"Speed multiplier → {newSpeed:F1}x");
                if (!Mathf.Approximately(newJump, JumpMultiplier))
                    Log.LogInfo($"Jump multiplier → {newJump:F1}x");
                SpeedMultiplier = newSpeed;
                JumpMultiplier = newJump;

                // Handle one-shot cheats
                if (IsCheatEnabled("explore_map"))
                    RevealMap();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to load trainer state: {ex.Message}");
            }
        }

        private void SetupWatcher()
        {
            try
            {
                string dir = Path.GetDirectoryName(_trainerStatePath);
                string file = Path.GetFileName(_trainerStatePath);

                if (!Directory.Exists(dir))
                {
                    Log.LogWarning($"Trainer state directory doesn't exist yet: {dir}");
                    return;
                }

                _stateWatcher = new FileSystemWatcher(dir, file);
                _stateWatcher.Changed += OnStateChanged;
                _stateWatcher.Created += OnStateChanged;
                _stateWatcher.IncludeSubdirectories = false;
                _stateWatcher.SynchronizingObject = null;
                _stateWatcher.EnableRaisingEvents = true;

                Log.LogInfo($"Watching: {_trainerStatePath}");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to setup file watcher: {ex.Message}");
            }
        }

        private void OnStateChanged(object sender, FileSystemEventArgs e)
        {
            // FileSystemWatcher fires on a background thread — defer Unity work to main thread
            _pendingStateReload = true;
        }

        internal static bool IsCheatEnabled(string id)
        {
            return CheatStates.ContainsKey(id) && CheatStates[id];
        }

        private void Update()
        {
            // Process deferred state reload from FileSystemWatcher (bg thread → main thread)
            if (_pendingStateReload)
            {
                _pendingStateReload = false;
                try
                {
                    LoadState();
                    ShowHud();
                    if (Player.m_localPlayer != null)
                        Player.m_localPlayer.Message(MessageHud.MessageType.Center, "MegaTrainer updated!");
                }
                catch (Exception ex)
                {
                    Log.LogError($"Error reloading trainer state: {ex.Message}");
                }
            }

            // Process deferred HUD show
            if (_pendingHudShow)
            {
                _pendingHudShow = false;
                ShowHud();
            }

            Player player = Player.m_localPlayer;
            if (player == null) return;

            // Ghost mode — toggle enemy awareness (only when state changes)
            bool wantGhost = IsCheatEnabled("ghost_mode");
            if (wantGhost != _lastGhostState)
            {
                player.SetGhostMode(wantGhost);
                _lastGhostState = wantGhost;
            }

            // Fly mode — set m_debugFly directly (do NOT touch m_debugMode — it
            // enables debug hotkeys, unlocks all recipes, and breaks server commands)
            bool wantFly = IsCheatEnabled("fly_mode");
            if (wantFly != _lastFlyState)
            {
                SetDebugFly(player, wantFly);
                _lastFlyState = wantFly;
            }

            // Always Rested — force-apply SE_Rested and keep its timer alive
            bool wantRested = IsCheatEnabled("always_rested");
            if (wantRested)
                ForceRested(player);
            else if (_lastRestedState && !wantRested)
                _cachedRestedSE = null;  // clear cache when toggled off
            _lastRestedState = wantRested;

            // Free Build — set m_noPlacementCost directly (uses Valheim's built-in free build).
            // A Harmony patch on UpdateKnownRecipesList temporarily hides m_noPlacementCost
            // so the game doesn't permanently mass-discover all recipes/pieces.
            bool wantNoCost = IsCheatEnabled("no_placement_cost");
            if (wantNoCost != _lastNoCostState)
            {
                SetNoPlacementCost(player, wantNoCost);
                _lastNoCostState = wantNoCost;
            }

            // Auto-tame — continuously tame nearby creatures every 2 seconds while enabled
            if (IsCheatEnabled("tame_all") && Time.time >= _nextTameCheck)
            {
                _nextTameCheck = Time.time + 2f;
                TameNearby();
            }

            // Don't process hotkeys when UI is open
            if (IsUIBlockingInput()) return;

            // Numpad cheat toggles
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            foreach (var binding in NumpadBindings)
            {
                if (Input.GetKeyDown(binding.Key) && binding.RequiresAlt == alt)
                {
                    bool newState = !IsCheatEnabled(binding.CheatId);
                    CheatStates[binding.CheatId] = newState;
                    string status = newState ? "ON" : "OFF";
                    player.Message(MessageHud.MessageType.Center, $"{binding.Label}: {status}");
                    Log.LogInfo($"Hotkey: {binding.Label} → {status}");
                    PersistState();
                    ShowHud();

                    // Trigger one-shot cheats immediately
                    if (newState && binding.CheatId == "explore_map") RevealMap();
                }
            }

            // Speed & Jump adjustment with Num+/Num-/Num*
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (!ctrl)
            {
                if (Input.GetKeyDown(KeyCode.KeypadPlus))
                {
                    SpeedMultiplier = Mathf.Round(Mathf.Clamp(SpeedMultiplier + 0.1f, 0.1f, 16.0f) * 10f) / 10f;
                    player.Message(MessageHud.MessageType.TopLeft, " ");
                    player.Message(MessageHud.MessageType.TopLeft, $"Speed: {SpeedMultiplier:F1}x");
                }
                if (Input.GetKeyDown(KeyCode.KeypadMinus))
                {
                    SpeedMultiplier = Mathf.Round(Mathf.Clamp(SpeedMultiplier - 0.1f, 0.1f, 16.0f) * 10f) / 10f;
                    player.Message(MessageHud.MessageType.TopLeft, " ");
                    player.Message(MessageHud.MessageType.TopLeft, $"Speed: {SpeedMultiplier:F1}x");
                }
            }
            else
            {
                if (Input.GetKeyDown(KeyCode.KeypadPlus))
                {
                    JumpMultiplier = Mathf.Round(Mathf.Clamp(JumpMultiplier + 0.1f, 0.1f, 16.0f) * 10f) / 10f;
                    player.Message(MessageHud.MessageType.TopLeft, " ");
                    player.Message(MessageHud.MessageType.TopLeft, $"Jump Height: {JumpMultiplier:F1}x");
                }
                if (Input.GetKeyDown(KeyCode.KeypadMinus))
                {
                    JumpMultiplier = Mathf.Round(Mathf.Clamp(JumpMultiplier - 0.1f, 0.1f, 16.0f) * 10f) / 10f;
                    player.Message(MessageHud.MessageType.TopLeft, " ");
                    player.Message(MessageHud.MessageType.TopLeft, $"Jump Height: {JumpMultiplier:F1}x");
                }
            }

            if (Input.GetKeyDown(KeyCode.KeypadMultiply))
            {
                SpeedMultiplier = 1.0f;
                JumpMultiplier = 1.0f;
                player.Message(MessageHud.MessageType.TopLeft, " ");
                player.Message(MessageHud.MessageType.TopLeft, "Speed & Jump: 1.0x (reset)");
            }
        }

        private static bool IsUIBlockingInput()
        {
            return Chat.instance != null && Chat.instance.HasFocus()
                || Console.IsVisible()
                || TextInput.IsVisible()
                || Minimap.InTextInput()
                || Menu.IsVisible()
                || InventoryGui.IsVisible();
        }

        private void PersistState()
        {
            try
            {
                // Temporarily disable watcher to avoid re-triggering LoadState
                if (_stateWatcher != null)
                    _stateWatcher.EnableRaisingEvents = false;

                // Read existing data to preserve saved_profiles
                string existingJson = File.Exists(_trainerStatePath) ? File.ReadAllText(_trainerStatePath) : "";
                var savedProfilesSection = ExtractSavedProfiles(existingJson);

                // Build cheats array
                var parts = new List<string>();
                foreach (var kv in CheatStates)
                    parts.Add($"    {{\n      \"id\": \"{kv.Key}\",\n      \"enabled\": {kv.Value.ToString().ToLower()}\n    }}");

                string cheatsArray = string.Join(",\n", parts);
                string speedStr = SpeedMultiplier.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                string jumpStr = JumpMultiplier.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                string json = $"{{\n  \"active\": {{\n    \"cheats\": [\n{cheatsArray}\n    ],\n    \"speed_multiplier\": {speedStr},\n    \"jump_multiplier\": {jumpStr}\n  }},\n  \"saved_profiles\": {savedProfilesSection}\n}}";

                File.WriteAllText(_trainerStatePath, json);
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to persist trainer state: {ex.Message}");
            }
            finally
            {
                if (_stateWatcher != null)
                    _stateWatcher.EnableRaisingEvents = true;
            }
        }

        private static string ExtractSavedProfiles(string json)
        {
            if (string.IsNullOrEmpty(json)) return "[]";
            int idx = json.IndexOf("\"saved_profiles\"");
            if (idx < 0) return "[]";
            int colonIdx = json.IndexOf(':', idx);
            if (colonIdx < 0) return "[]";
            // Find the opening bracket
            int bracketStart = json.IndexOf('[', colonIdx);
            if (bracketStart < 0) return "[]";
            // Find matching close
            int depth = 0;
            for (int i = bracketStart; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']')
                {
                    depth--;
                    if (depth == 0)
                        return json.Substring(bracketStart, i - bracketStart + 1);
                }
            }
            return "[]";
        }

        private static void SetDebugFly(Player player, bool enabled)
        {
            if (!_debugFlyFieldSearched)
            {
                _debugFlyFieldSearched = true;
                _debugFlyField = AccessTools.Field(typeof(Player), "m_debugFly");
                if (_debugFlyField == null)
                    Log.LogWarning("Could not find Player.m_debugFly field — fly toggle won't work");
            }
            if (_debugFlyField != null)
                _debugFlyField.SetValue(player, enabled);
        }

        internal static void SetNoPlacementCost(Player player, bool enabled)
        {
            if (!_noPlacementCostFieldSearched)
            {
                _noPlacementCostFieldSearched = true;
                _noPlacementCostField = AccessTools.Field(typeof(Player), "m_noPlacementCost");
                if (_noPlacementCostField == null)
                    Log.LogWarning("Could not find Player.m_noPlacementCost field — free build toggle won't work");
            }
            if (_noPlacementCostField != null)
                _noPlacementCostField.SetValue(player, enabled);
        }

        internal static bool GetNoPlacementCost(Player player)
        {
            if (_noPlacementCostField == null) return false;
            return (bool)_noPlacementCostField.GetValue(player);
        }

        private static FieldInfo _seTimeField;
        private static bool _seTimeFieldSearched;

        private static void ForceRested(Player player)
        {
            var seman = player.GetSEMan();
            if (seman == null) return;

            // Invalidate cache if player instance changed
            int playerId = player.GetInstanceID();
            if (playerId != _cachedRestedPlayer)
            {
                _cachedRestedSE = null;
                _cachedRestedPlayer = playerId;
            }

            // Use cached reference; re-lookup only if lost
            if (_cachedRestedSE == null)
            {
                _cachedRestedSE = seman.GetStatusEffect("Rested".GetHashCode());
                if (_cachedRestedSE == null)
                {
                    seman.AddStatusEffect("Rested".GetHashCode());
                    _cachedRestedSE = seman.GetStatusEffect("Rested".GetHashCode());
                }
            }

            if (_cachedRestedSE != null)
            {
                // Keep the timer alive by resetting elapsed time via reflection (m_time is private)
                // Throttle to once per second — no need to reset every frame
                if (Time.time < _nextRestedReset) return;
                _nextRestedReset = Time.time + 1f;

                if (!_seTimeFieldSearched)
                {
                    _seTimeFieldSearched = true;
                    _seTimeField = AccessTools.Field(typeof(StatusEffect), "m_time");
                    if (_seTimeField == null)
                        Log.LogWarning("Could not find StatusEffect.m_time — Always Rested timer reset won't work");
                }
                _seTimeField?.SetValue(_cachedRestedSE, 0f);
            }
        }

        private static void RevealMap()
        {
            if (Minimap.instance == null) return;
            Minimap.instance.ExploreAll();
            Log.LogInfo("Map fully revealed!");
        }

        private static void TameNearby()
        {
            if (Player.m_localPlayer == null) return;
            var pos = Player.m_localPlayer.transform.position;
            var tameMethod = typeof(Tameable).GetMethod("Tame",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (tameMethod == null)
            {
                Log.LogWarning("Tameable.Tame() method not found — game API may have changed");
                return;
            }
            var characters = Character.GetAllCharacters();
            int tamed = 0;
            foreach (var c in characters)
            {
                if (c == null || c.IsPlayer()) continue;
                if (c.IsTamed()) continue;
                if (Vector3.Distance(pos, c.transform.position) > 100f) continue;
                var tameable = c.GetComponent<Tameable>();
                if (tameable != null)
                {
                    tameMethod.Invoke(tameable, null);
                    tamed++;
                    Log.LogInfo($"  Tamed: {c.m_name} ({Utils.GetPrefabName(c.gameObject)})");
                }
            }
            if (tamed > 0)
                Log.LogInfo($"Tamed {tamed} creatures!");
        }

        private void PatchAllIndividually()
        {
            int ok = 0, fail = 0;
            foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
            {
                // Skip types without HarmonyPatch attribute
                if (type.GetCustomAttribute<HarmonyPatch>() == null) continue;
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    Log.LogWarning($"Patch {type.Name} failed: {ex.Message}");
                }
            }
            Log.LogInfo($"Harmony patches: {ok} applied, {fail} failed");
        }

        private static void ShowHud()
        {
            _hudShowTime = Time.time;
        }

        private void OnGUI()
        {
            float elapsed = Time.time - _hudShowTime;
            if (elapsed > HudVisibleDuration + HudFadeDuration) return;
            if (Player.m_localPlayer == null) return;

            // Calculate alpha (full opacity then fade out)
            float alpha;
            if (elapsed < HudVisibleDuration)
                alpha = 0.85f;
            else
                alpha = 0.85f * (1f - (elapsed - HudVisibleDuration) / HudFadeDuration);

            // Init styles once
            if (_hudLabelStyle == null)
            {
                _hudLabelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false
                };
                _hudHeaderStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false
                };
                _hudKeyStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false
                };
                _hudOnStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleRight,
                    wordWrap = false
                };
                _hudOffStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleRight,
                    wordWrap = false
                };
            }

            float panelW = 340f;
            float rowH = 22f;
            float headerH = 30f;
            float padding = 10f;
            int rowCount = NumpadBindings.Length;

            // Extra rows for speed/jump if non-default
            bool showSpeed = !Mathf.Approximately(SpeedMultiplier, 1.0f);
            bool showJump = !Mathf.Approximately(JumpMultiplier, 1.0f);
            int extraRows = (showSpeed ? 1 : 0) + (showJump ? 1 : 0);
            if (extraRows > 0) extraRows++; // separator spacing

            float panelH = headerH + padding + (rowCount + extraRows) * rowH + padding;
            float x = 10f;
            float y = Screen.height / 2f - panelH / 2f;

            // Background
            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0f, 0f, 0f, alpha);
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0.08f, 0.08f, 0.12f, 1f));
                _bgTex.Apply();
            }
            var bgColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(new Rect(x, y, panelW, panelH), _bgTex);

            // Border
            if (_borderTex == null)
            {
                _borderTex = new Texture2D(1, 1);
                _borderTex.SetPixel(0, 0, new Color(0.82f, 0.67f, 0.15f, 1f));
                _borderTex.Apply();
            }
            GUI.color = new Color(1f, 1f, 1f, alpha * 0.6f);
            GUI.DrawTexture(new Rect(x, y, panelW, 2f), _borderTex); // top
            GUI.DrawTexture(new Rect(x, y + panelH - 2f, panelW, 2f), _borderTex); // bottom
            GUI.DrawTexture(new Rect(x, y, 2f, panelH), _borderTex); // left
            GUI.DrawTexture(new Rect(x + panelW - 2f, y, 2f, panelH), _borderTex); // right
            GUI.color = bgColor;

            float cy = y + padding;

            // Header
            _hudHeaderStyle.normal.textColor = new Color(0.92f, 0.78f, 0.2f, alpha);
            GUI.Label(new Rect(x, cy, panelW, headerH), "MegaTrainer", _hudHeaderStyle);
            cy += headerH;

            // Column positions
            float colKey = x + 10f;
            float colLabel = x + 65f;
            float colStatus = x + panelW - 70f;

            foreach (var binding in NumpadBindings)
            {
                bool active = IsCheatEnabled(binding.CheatId);

                // Key badge
                string keyNum = binding.Key.ToString().Replace("Keypad", "");
                string keyLabel = binding.RequiresAlt ? $"A+{keyNum}" : keyNum;
                _hudKeyStyle.normal.textColor = new Color(0.6f, 0.6f, 0.65f, alpha);
                GUI.Label(new Rect(colKey, cy, 48f, rowH), $"[{keyLabel}]", _hudKeyStyle);

                // Cheat name
                _hudLabelStyle.normal.textColor = active
                    ? new Color(0.9f, 0.9f, 0.9f, alpha)
                    : new Color(0.5f, 0.5f, 0.5f, alpha);
                GUI.Label(new Rect(colLabel, cy, 180f, rowH), binding.Label, _hudLabelStyle);

                // Status
                if (active)
                {
                    _hudOnStyle.normal.textColor = new Color(0.3f, 0.9f, 0.3f, alpha);
                    GUI.Label(new Rect(colStatus, cy, 55f, rowH), "ON", _hudOnStyle);
                }
                else
                {
                    _hudOffStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f, alpha * 0.6f);
                    GUI.Label(new Rect(colStatus, cy, 55f, rowH), "OFF", _hudOffStyle);
                }

                cy += rowH;
            }

            // Speed/Jump multipliers
            if (showSpeed || showJump)
            {
                cy += 4f;
                if (showSpeed)
                {
                    _hudKeyStyle.normal.textColor = new Color(0.6f, 0.6f, 0.65f, alpha);
                    GUI.Label(new Rect(colKey, cy, 40f, rowH), "[+/-]", _hudKeyStyle);
                    _hudLabelStyle.normal.textColor = new Color(0.7f, 0.8f, 1.0f, alpha);
                    GUI.Label(new Rect(colLabel, cy, 180f, rowH), $"Speed: {SpeedMultiplier:F1}x", _hudLabelStyle);
                    cy += rowH;
                }
                if (showJump)
                {
                    _hudKeyStyle.normal.textColor = new Color(0.6f, 0.6f, 0.65f, alpha);
                    GUI.Label(new Rect(colKey, cy, 40f, rowH), "[C+/-]", _hudKeyStyle);
                    _hudLabelStyle.normal.textColor = new Color(0.7f, 0.8f, 1.0f, alpha);
                    GUI.Label(new Rect(colLabel, cy, 180f, rowH), $"Jump: {JumpMultiplier:F1}x", _hudLabelStyle);
                    cy += rowH;
                }
            }

            GUI.backgroundColor = prevBg;
        }

        private void OnDestroy()
        {
            if (_stateWatcher != null)
            {
                _stateWatcher.EnableRaisingEvents = false;
                _stateWatcher.Dispose();
                _stateWatcher = null;
            }
            _harmony?.UnpatchSelf();
        }
    }
}

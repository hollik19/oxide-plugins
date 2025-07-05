using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("ServerManager", "YourName", "4.0.0")]
    [Description("Complete server management with integrated reputation system - ALL FEATURES INCLUDED")]
    public class ServerManager : RustPlugin
    {
        #region Fields and Configuration

        [PluginReference] private Plugin ZoneManager;
        private const string permAdmin = "servermanager.admin";

        // Core Settings
        private float decayFactor = 1f;
        private Dictionary<ulong, Dictionary<string, int>> customKits = new Dictionary<ulong, Dictionary<string, int>>();
        private Vector3 selectedEventPosition = Vector3.zero;
        private string selectedGridCoordinate = "";
        private Dictionary<ulong, float> lastRadiationTime = new Dictionary<ulong, float>();

        // Live Map Integration (COMPLETE)
        private Dictionary<ulong, Timer> liveMapActiveTimers = new Dictionary<ulong, Timer>();
        private Vector2? liveMapSingleMarker = null;
        private Dictionary<ulong, DateTime> liveMapLastUpdate = new Dictionary<ulong, DateTime>();
        private const string LiveMapContainerName = "ServerManagerLiveMap";
        private const string LiveMapDotsContainerName = "SMMapDots";
        private const string LiveMapMarkersContainerName = "SMMapMarkers";
        private const float LiveMapUpdateInterval = 3f;
        private const int LiveMapMaxPlayers = 30;
        private const float LiveMapSize = 3750f;
        private const int LiveMapGridResolution = 25;

        // Environmental Controls
        private int crateUnlockTime = 15; // minutes
        private float timeOfDay = -1f; // -1 = auto, 0-24 = fixed time
        private float environmentTemp = -999f; // -999 = auto
        private float environmentWind = -1f; // -1 = auto
        private float environmentRain = -1f; // -1 = auto

        // COMPLETE REPUTATION SYSTEM (WITH TIERS, NO SPEED CONTROLS)
        private Dictionary<ulong, int> repData = new Dictionary<ulong, int>();
        private Timer refreshTimer;
        private Timer hourlyTimer;
        private Timer punishmentTimer;

        // Reputation Configuration (COMPLETE WITH TIERS)
        public class ReputationConfig
        {
            // Core System Settings
            public float MaxDistance = 30f;
            public int MaxPlayersShown = 4;
            public float UpdateInterval = 2.0f;
            public int MinReputation = 0;
            public int MaxReputation = 100;
            public int DefaultReputation = 50;
            
            // Feature Toggles
            public bool EnableSafeZoneBlocking = true;
            public bool EnableGatherBonus = true;
            public bool EnableHUD = true;
            public bool EnableParachuteSpawn = true;
            public bool EnableContinuousHungerThirstPenalty = true;
            public bool EnableChatIntegration = true;
            
            // Reputation Tier Settings (KEEP TIERS)
            public string InfidelTierName = "Infidel";
            public string SinnerTierName = "Sinner";
            public string AverageTierName = "Average";
            public string DiscipleTierName = "Disciple";
            public string ProphetTierName = "Prophet";
            
            public string InfidelTierColor = "#ff0000";
            public string SinnerTierColor = "#ff8000";
            public string AverageTierColor = "#ffff00";
            public string DiscipleTierColor = "#ffffff";
            public string ProphetTierColor = "#00ff00";
            
            public int InfidelMaxRep = 25;
            public int SinnerMaxRep = 45;
            public int AverageMaxRep = 55;
            public int DiscipleMaxRep = 89;
            
            // Hourly Reputation Gains
            public bool HourlyRepGainEnabled = true;
            public int HourlyRepGainLow = 4;
            public int HourlyRepGainHigh = 2;
            public int HourlyRepGainThreshold = 35;
            
            // NPC Kill Penalties
            public bool NPCKillPenaltyEnabled = true;
            public int NPCKillPenalty = -1;
            
            // PvP Reputation Changes
            public bool PvPChangesEnabled = true;
            public int PvPKillInfidelReward = 10;
            public int PvPKillSinnerReward = 5;
            public int PvPKillAverageReward = -2;
            public int PvPKillDiscipleReward = -8;
            public int PvPKillProphetReward = -15;
            
            // Safe Zone Blocking System
            public bool SafeZoneBlockingEnabled = true;
            public float SafeZonePushForce = 2.0f;
            public float SafeZoneCheckInterval = 5.0f;
            public int SafeZoneBlockingMaxRep = 25;
            
            // Hunger/Thirst Drain System
            public bool HungerThirstPenaltyEnabled = true;
            public float HungerDrainMultiplier = 2.0f;
            public float ThirstDrainMultiplier = 2.0f;
            public float HungerThirstCheckInterval = 10.0f;
            public int HungerThirstPenaltyMaxRep = 25;
            
            // Gather Bonus/Penalty System (KEEP TIERS)
            public float InfidelGatherMultiplier = 0.7f;
            public float SinnerGatherMultiplier = 0.9f;
            public float AverageGatherMultiplier = 1.0f;
            public float DiscipleGatherMultiplier = 1.1f;
            public float ProphetGatherMultiplier = 1.25f;
            
            // Parachute Spawn System (COMPLETE)
            public float ParachuteSpawnHeight = 500f;
            public bool ParachuteOnlyForProphets = true;
            public bool ParachuteAutoEquip = true;
            public float ParachuteSpawnRadius = 50f;
            public bool ParachuteForceSpawn = false;
            public bool ParachuteGiveItems = true;
            
            // Configurable Parachute Starter Items
            public Dictionary<string, int> ParachuteStarterItems = new Dictionary<string, int>
            {
                ["water.jug"] = 1,
                ["apple"] = 3,
                ["bandage"] = 2,
                ["torch"] = 1,
                ["stone.pickaxe"] = 1,
                ["hatchet"] = 1
            };
            
            // Message Customization
            public string SafeZoneBlockMessage = "<color=#ff0000>🚫 SAFE ZONE BLOCKED - Your reputation is too low!</color>";
            public string HungerThirstPenaltyMessage = "<color=#ff8000>Your low reputation makes survival harder...</color>";
            public string ParachuteSpawnMessage = "<color=#00ff00>Aerial spawn activated! You're {0}m above map center.</color>";
            public string ParachuteDeployMessage = "<color=#00ff00>Parachute deployed! Use WASD to control, SPACE to cut away!</color>";
            public string ParachuteSurvivalKitMessage = "<color=#00ff00>Aerial survival kit provided!</color>";
            
            // Message Display Settings
            public float SafeZoneMessageFrequency = 0.25f; // 25% chance
            public float HungerThirstMessageFrequency = 0.1f; // 10% chance
            public float GatherBonusMessageFrequency = 0.03f; // 3% chance
        }

        private ReputationConfig repConfig = new ReputationConfig();

        private Color[] liveMapDotColors = new Color[]
        {
            Color.red, Color.blue, Color.green, Color.yellow, Color.magenta,
            Color.cyan, new Color(1f,0.5f,0f), new Color(0f,1f,0.5f), 
            new Color(0.5f,0f,1f), new Color(0.2f,0.8f,0.2f), 
            new Color(0.8f,0.2f,0.8f), new Color(0.2f,0.2f,0.8f),
            new Color(0.8f,0.8f,0.2f), new Color(0.9f,0.3f,0.3f),
            new Color(0.3f,0.9f,0.3f), new Color(0.3f,0.3f,0.9f)
        };

        private readonly Dictionary<string, string> commonItems = new Dictionary<string, string>
        {
            ["apple"] = "Apple", ["mushroom"] = "Mushroom", ["corn"] = "Corn", ["pumpkin"] = "Pumpkin",
            ["chicken.cooked"] = "Cooked Chicken", ["fish.cooked"] = "Cooked Fish", ["bandage"] = "Bandage",
            ["syringe.medical"] = "Med Syringe", ["largemedkit"] = "Large Medkit", ["antiradpills"] = "Anti-Rad Pills",
            ["hoodie"] = "Hoodie", ["pants"] = "Pants", ["tshirt"] = "T-Shirt", ["hat.boonie"] = "Boonie Hat",
            ["shoes.boots"] = "Boots", ["burlap.gloves"] = "Burlap Gloves", ["burlap.shirt"] = "Burlap Shirt",
            ["burlap.pants"] = "Burlap Pants", ["metal.plate.torso"] = "Metal Chestplate", ["metal.facemask"] = "Metal Facemask",
            ["wood"] = "Wood", ["stones"] = "Stone", ["metal.fragments"] = "Metal Fragments", ["cloth"] = "Cloth",
            ["leather"] = "Leather", ["scrap"] = "Scrap", ["rope"] = "Rope", ["tarp"] = "Tarp",
            ["hatchet"] = "Hatchet", ["pickaxe"] = "Pickaxe", ["bow.hunting"] = "Hunting Bow", ["crossbow"] = "Crossbow",
            ["pistol.eoka"] = "Eoka Pistol", ["pistol.revolver"] = "Revolver", ["shotgun.waterpipe"] = "Waterpipe Shotgun",
            ["rifle.bolt"] = "Bolt Action Rifle", ["lowgradefuel"] = "Low Grade Fuel", ["gunpowder"] = "Gun Powder", ["sulfur"] = "Sulfur"
        };

        #endregion

        #region Plugin Lifecycle

        void Init()
        {
            permission.RegisterPermission(permAdmin, this);
            LoadData();
            LoadLiveMapImageCache();
        }

        void OnServerInitialized()
        {
            timer.Once(3f, () => {
                if (decayFactor != 1f)
                {
                    timer.Once(5f, () => {
                        int minutes = (int)(1440 / decayFactor);
                        rust.RunServerCommand($"decay.upkeep_period_minutes {minutes}");
                        PrintWarning($"Applied decay factor: {decayFactor}");
                    });
                }
                
                ApplyEnvironmentalSettings();
                InitializeReputationSystem();
            });
        }

        void Unload()
        {
            SaveData();
            
            // Clean up reputation timers
            refreshTimer?.Destroy();
            hourlyTimer?.Destroy();
            punishmentTimer?.Destroy();
            
            // Clean up parachutes
            DestroyAll<SimpleParachuteEntity>();
            
            // Clean up all active live map timers
            foreach (var timer in liveMapActiveTimers.Values)
            {
                timer?.Destroy();
            }
            liveMapActiveTimers.Clear();

            // Close all UIs
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.IsConnected == true)
                {
                    CloseLiveMapView(player);
                    if (repConfig.EnableHUD)
                        DestroyHUD(player);
                    CuiHelper.DestroyUi(player, "ServerManagerMain");
                    CuiHelper.DestroyUi(player, "ServerManagerContent");
                }
            }
        }

        private void OnServerSave() => SaveData();

        private void OnServerShutdown()
        {
            SaveData();
            
            // Gracefully close all live maps
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.IsConnected == true)
                {
                    CloseLiveMapView(player);
                }
            }
        }

        void OnNewSave(string filename)
        {
            PrintWarning("[ServerManager] New save detected - resetting plugin data");
            customKits.Clear();
            selectedEventPosition = Vector3.zero;
            repData.Clear();
            SaveData();
        }

        #endregion

        #region Utilities

        bool HasPerm(BasePlayer player) => player != null && permission.UserHasPermission(player.UserIDString, permAdmin);

        bool IsPlayerInSafeZone(BasePlayer player)
        {
            if (ZoneManager == null || !ZoneManager.IsLoaded) return false;
            try
            {
                var result = ZoneManager.Call("PlayerHasFlag", player, "SafeZone");
                return result != null && (bool)result;
            }
            catch { return false; }
        }

        private void SafeExecute(System.Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                PrintError($"SafeExecute error: {ex.Message}");
            }
        }

        private bool IsValidPlayer(BasePlayer player)
        {
            return player != null && !player.IsDestroyed && player.IsConnected;
        }

        private static void DestroyAll<T>()
        {
            var objects = GameObject.FindObjectsOfType(typeof(T));
            if (objects != null)
                foreach (var gameObj in objects)
                    GameObject.Destroy(gameObj);
        }

        void ApplyEnvironmentalSettings()
        {
            if (timeOfDay >= 0 && timeOfDay <= 24)
            {
                rust.RunServerCommand($"env.time {timeOfDay}");
                PrintWarning($"Time set to: {timeOfDay}:00");
            }
            
            if (crateUnlockTime != 15)
            {
                rust.RunServerCommand($"hackablelockedcrate.requiredhackseconds {crateUnlockTime * 60}");
                PrintWarning($"Crate unlock time set to: {crateUnlockTime} minutes");
            }
        }

        #endregion
		
		#region Complete Reputation System

        private void InitializeReputationSystem()
        {
            try
            {
                if (repConfig.UpdateInterval > 0 && repConfig.EnableHUD)
                    refreshTimer = timer.Every(repConfig.UpdateInterval, () => SafeExecute(RefreshAllPlayersHUD));

                if (repConfig.HourlyRepGainEnabled)
                    hourlyTimer = timer.Every(3600f, () => SafeExecute(AwardHourlyReputation));

                if (repConfig.EnableSafeZoneBlocking || repConfig.EnableContinuousHungerThirstPenalty)
                    punishmentTimer = timer.Every(repConfig.SafeZoneCheckInterval, () => SafeExecute(CheckAllPlayersForPunishments));

                foreach (var player in BasePlayer.activePlayerList.ToList())
                {
                    SafeExecute(() => {
                        if (IsValidPlayer(player))
                        {
                            EnsureRep(player.userID);
                            
                            if (repConfig.EnableHUD)
                                timer.Once(3f, () => SafeExecute(() => { if (IsValidPlayer(player) && !player.IsSleeping()) CreateOrUpdateHUD(player); }));
                        }
                    });
                }

                PrintWarning("[ServerManager] Complete reputation system initialized - ALL FEATURES INCLUDED!");
            }
            catch (Exception ex)
            {
                PrintError($"InitializeReputationSystem error: {ex.Message}");
            }
        }

        private void EnsureRep(ulong userID)
        {
            if (repData != null && !repData.ContainsKey(userID))
                repData[userID] = repConfig.DefaultReputation;
        }

        private string GetTierName(int rep)
        {
            if (rep <= repConfig.InfidelMaxRep) return repConfig.InfidelTierName;
            if (rep <= repConfig.SinnerMaxRep) return repConfig.SinnerTierName;
            if (rep <= repConfig.AverageMaxRep) return repConfig.AverageTierName;
            if (rep <= repConfig.DiscipleMaxRep) return repConfig.DiscipleTierName;
            return repConfig.ProphetTierName;
        }

        private string GetTierColor(int rep)
        {
            if (rep <= repConfig.InfidelMaxRep) return repConfig.InfidelTierColor;
            if (rep <= repConfig.SinnerMaxRep) return repConfig.SinnerTierColor;
            if (rep <= repConfig.AverageMaxRep) return repConfig.AverageTierColor;
            if (rep <= repConfig.DiscipleMaxRep) return repConfig.DiscipleTierColor;
            return repConfig.ProphetTierColor;
        }

        private float GetGatherMultiplier(int reputation)
        {
            string tier = GetTierName(reputation);
            
            if (tier == repConfig.InfidelTierName) return repConfig.InfidelGatherMultiplier;
            if (tier == repConfig.SinnerTierName) return repConfig.SinnerGatherMultiplier;
            if (tier == repConfig.AverageTierName) return repConfig.AverageGatherMultiplier;
            if (tier == repConfig.DiscipleTierName) return repConfig.DiscipleGatherMultiplier;
            if (tier == repConfig.ProphetTierName) return repConfig.ProphetGatherMultiplier;
            
            return 1.0f;
        }

        private bool CanUseParachute(int reputation)
        {
            if (!repConfig.EnableParachuteSpawn) return false;
            
            if (repConfig.ParachuteOnlyForProphets)
                return GetTierName(reputation) == repConfig.ProphetTierName;
            
            return true;
        }

        private int GetPvPRepChange(string victimTier)
        {
            if (!repConfig.PvPChangesEnabled) return 0;
            
            if (victimTier == repConfig.InfidelTierName) return repConfig.PvPKillInfidelReward;
            if (victimTier == repConfig.SinnerTierName) return repConfig.PvPKillSinnerReward;
            if (victimTier == repConfig.AverageTierName) return repConfig.PvPKillAverageReward;
            if (victimTier == repConfig.DiscipleTierName) return repConfig.PvPKillDiscipleReward;
            if (victimTier == repConfig.ProphetTierName) return repConfig.PvPKillProphetReward;
            
            return 0;
        }

        // FIXED: Direct internal methods - no plugin calls!
        private int GetPlayerReputation(BasePlayer player)
        {
            if (player == null) return repConfig.DefaultReputation;
            EnsureRep(player.userID);
            return repData[player.userID];
        }

        private bool SetPlayerReputation(BasePlayer player, int newRep)
        {
            if (player == null) return false;
            
            try
            {
                EnsureRep(player.userID);
                newRep = Mathf.Clamp(newRep, repConfig.MinReputation, repConfig.MaxReputation);
                repData[player.userID] = newRep;
                
                if (repConfig.EnableHUD && IsValidPlayer(player) && !player.IsSleeping())
                {
                    timer.Once(0.2f, () => SafeExecute(() => CreateOrUpdateHUD(player)));
                }
                
                SaveData();
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"SetPlayerReputation error for {player.displayName}: {ex.Message}");
                return false;
            }
        }

        private void CheckAllPlayersForPunishments()
        {
            if (!repConfig.EnableSafeZoneBlocking && !repConfig.EnableContinuousHungerThirstPenalty) return;
            
            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                SafeExecute(() => {
                    if (!IsValidPlayer(player) || player.IsDead()) return;
                    
                    EnsureRep(player.userID);
                    
                    if (repConfig.EnableSafeZoneBlocking && repData[player.userID] <= repConfig.SafeZoneBlockingMaxRep)
                        CheckSafeZoneBlocking(player);
                    
                    if (repConfig.EnableContinuousHungerThirstPenalty && repData[player.userID] <= repConfig.HungerThirstPenaltyMaxRep)
                        ApplyHungerThirstPenalty(player);
                });
            }
        }

        private void CheckSafeZoneBlocking(BasePlayer player)
        {
            try
            {
                bool inSafeZone = player.InSafeZone();
                
                if (inSafeZone)
                {
                    // Calculate push direction away from safe zone center
                    Vector3 pushDirection = -player.transform.forward; // Push backward from where they're facing
                    if (pushDirection.magnitude < 0.1f)
                        pushDirection = Vector3.forward; // fallback direction
                    
                    // Teleport player away from safe zone safely
                    Vector3 teleportDirection = pushDirection;
                    Vector3 safePosition = player.transform.position;

                    // Try multiple distances to find safe ground
                    for (float distance = 5f; distance <= 20f; distance += 2f)
                    {
                        Vector3 testPosition = player.transform.position + teleportDirection * distance;
                        
                        // Check if there's ground below this position
                        RaycastHit hit;
                        if (Physics.Raycast(testPosition + Vector3.up * 10f, Vector3.down, out hit, 20f))
                        {
                            safePosition = hit.point + Vector3.up * 1f; // 1 unit above ground
                            break;
                        }
                    }

                    player.Teleport(safePosition);
                    
                    // Show warning message occasionally
                    if (UnityEngine.Random.Range(0f, 1f) < repConfig.SafeZoneMessageFrequency)
                    {
                        player.ChatMessage(repConfig.SafeZoneBlockMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"CheckSafeZoneBlocking error for {player?.displayName}: {ex.Message}");
            }
        }

        private void ApplyHungerThirstPenalty(BasePlayer player)
        {
            try
            {
                if (player.metabolism == null) return;
                
                bool messageShown = false;
                
                if (player.metabolism.calories != null)
                {
                    float currentHunger = player.metabolism.calories.value;
                    float drainAmount = (100f / 3600f) * repConfig.HungerThirstCheckInterval * (repConfig.HungerDrainMultiplier - 1f);
                    player.metabolism.calories.value = Math.Max(currentHunger - drainAmount, 0f);
                    messageShown = true;
                }
                
                if (player.metabolism.hydration != null)
                {
                    float currentThirst = player.metabolism.hydration.value;
                    float drainAmount = (100f / 3600f) * repConfig.HungerThirstCheckInterval * (repConfig.ThirstDrainMultiplier - 1f);
                    player.metabolism.hydration.value = Math.Max(currentThirst - drainAmount, 0f);
                    messageShown = true;
                }
                
                // Show message occasionally
                if (messageShown && UnityEngine.Random.Range(0f, 1f) < repConfig.HungerThirstMessageFrequency)
                {
                    player.ChatMessage(repConfig.HungerThirstPenaltyMessage);
                }
            }
            catch (Exception ex)
            {
                PrintError($"ApplyHungerThirstPenalty error for {player?.displayName}: {ex.Message}");
            }
        }

        private void AwardHourlyReputation()
        {
            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                SafeExecute(() => {
                    if (!IsValidPlayer(player)) return;
                    EnsureRep(player.userID);
                    
                    int current = repData[player.userID];
                    int gain = 0;
                    
                    if (current < repConfig.HourlyRepGainThreshold)
                        gain = repConfig.HourlyRepGainLow;
                    else
                        gain = repConfig.HourlyRepGainHigh;
                    
                    if (gain > 0)
                    {
                        repData[player.userID] = Mathf.Clamp(current + gain, repConfig.MinReputation, repConfig.MaxReputation);
                        player.ChatMessage($"<color=#00ff00>+{gain} Reputation</color> (Hourly bonus)");
                    }
                });
            }
            SaveData();
        }
        #endregion

        #region Complete HUD System

        private void CreateOrUpdateHUD(BasePlayer player)
        {
            if (!repConfig.EnableHUD || !IsValidPlayer(player) || player.IsSleeping()) return;
            
            SafeExecute(() => {
                DestroyHUD(player);
                AddHUDPanel(player);
            });
        }

        private string PanelName(BasePlayer player) => $"ReputationHUD_{player?.UserIDString}";

        private void DestroyHUD(BasePlayer player)
        {
            try
            {
                if (player != null) 
                    CuiHelper.DestroyUi(player, PanelName(player));
            }
            catch { }
        }

        private void AddHUDPanel(BasePlayer player)
        {
            try
            {
                EnsureRep(player.userID);
                var elements = new CuiElementContainer();
                var nearPlayers = FindNearbyPlayers(player);
                int lineCount = 1 + Math.Min(nearPlayers.Count, repConfig.MaxPlayersShown);

                var panel = new CuiPanel
                {
                    Image = { Color = "0.1 0.1 0.1 0.85" },
                    RectTransform = { AnchorMin = "0.80 0.88", AnchorMax = "0.99 0.98" },
                    CursorEnabled = false
                };
                
                string panelName = PanelName(player);
                elements.Add(panel, "Hud", panelName);

                int selfRep = repData[player.userID];
                string selfTier = GetTierName(selfRep);
                string selfColor = GetTierColor(selfRep);
                
                string statusInfo = "";
                if (repConfig.EnableParachuteSpawn && CanUseParachute(selfRep))
                {
                    statusInfo += " ✈";
                }
                
                AddTextElement(ref elements, panelName, $"You: {selfRep} ({selfTier}){statusInfo}", selfColor, 0, lineCount);

                for (int i = 0; i < nearPlayers.Count && i < repConfig.MaxPlayersShown; i++)
                {
                    var other = nearPlayers[i];
                    if (!IsValidPlayer(other)) continue;
                    
                    EnsureRep(other.userID);
                    int rep = repData[other.userID];
                    string tier = GetTierName(rep);
                    string color = GetTierColor(rep);
                    float distance = Vector3.Distance(player.transform.position, other.transform.position);
                    
                    string otherStatusInfo = "";
                    if (repConfig.EnableParachuteSpawn && CanUseParachute(rep))
                    {
                        otherStatusInfo += " ✈";
                    }
                    
                    AddTextElement(ref elements, panelName, $"{other.displayName}: {rep} ({tier}){otherStatusInfo} - {distance:F0}m", color, i + 1, lineCount);
                }
                
                CuiHelper.AddUi(player, elements);
            }
            catch (Exception ex)
            {
                PrintError($"AddHUDPanel error for {player?.displayName}: {ex.Message}");
            }
        }

        private void AddTextElement(ref CuiElementContainer container, string parent, string text, string color, int lineIndex, int totalLines)
        {
            try
            {
                float topPercent = (float)lineIndex / totalLines;
                float bottomPercent = (float)(lineIndex + 1) / totalLines;
                
                container.Add(new CuiLabel
                {
                    Text = { Text = text, FontSize = 9, Align = TextAnchor.MiddleLeft, Color = color },
                    RectTransform = { AnchorMin = $"0 {1 - bottomPercent}", AnchorMax = $"1 {1 - topPercent}", OffsetMin = "5 0", OffsetMax = "-5 0" }
                }, parent);
            }
            catch { }
        }

        private List<BasePlayer> FindNearbyPlayers(BasePlayer player)
        {
            var list = new List<BasePlayer>();
            if (!IsValidPlayer(player)) return list;
            
            try
            {
                foreach (var p in BasePlayer.activePlayerList.ToList())
                {
                    if (!IsValidPlayer(p) || p == player || p.IsSleeping() || p.IsDead()) continue;
                    
                    float distance = Vector3.Distance(p.transform.position, player.transform.position);
                    if (distance <= repConfig.MaxDistance)
                        list.Add(p);
                }
                
                list.Sort((a, b) => {
                    float distA = Vector3.Distance(player.transform.position, a.transform.position);
                    float distB = Vector3.Distance(player.transform.position, b.transform.position);
                    return distA.CompareTo(distB);
                });
            }
            catch { }
            
            return list;
        }

        #endregion

        #region Chat Integration

        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (!repConfig.EnableChatIntegration || player == null || string.IsNullOrEmpty(message) || message.StartsWith("/")) return null;
            
            SafeExecute(() => {
                EnsureRep(player.userID);
                string tier = GetTierName(repData[player.userID]);
                string color = GetTierColor(repData[player.userID]);
                
                string statusInfo = "";
                if (repConfig.EnableParachuteSpawn && CanUseParachute(repData[player.userID]))
                {
                    statusInfo += " ✈";
                }
                
                string formattedMessage = $"[<color={color}>{tier}</color>] <color={color}>{player.displayName}</color>{statusInfo}: {message}";
                
                rust.BroadcastChat(null, formattedMessage, player.UserIDString);
                
                Puts($"[CHAT] [{tier}] {player.displayName}: {message}");
            });
            
            return false;
        }

        #endregion
		
		#region Complete Parachute System

        private void OnPlayerRespawn(BasePlayer player)
        {
            if (!repConfig.EnableParachuteSpawn || !IsValidPlayer(player)) return;
            
            SafeExecute(() => {
                EnsureRep(player.userID);
                
                if (CanUseParachute(repData[player.userID]))
                {
                    if (repConfig.ParachuteForceSpawn)
                    {
                        timer.Once(1f, () => SafeExecute(() => {
                            if (IsValidPlayer(player))
                                ExecuteParachuteSpawn(player);
                        }));
                    }
                    else
                    {
                        timer.Once(1f, () => SafeExecute(() => {
                            if (IsValidPlayer(player))
                                OfferParachuteSpawn(player);
                        }));
                    }
                }
            });
        }

        private void OfferParachuteSpawn(BasePlayer player)
        {
            if (!IsValidPlayer(player)) return;
            
            try
            {
                var container = new CuiElementContainer();
                
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.1 0.1 0.1 0.9" },
                    RectTransform = { AnchorMin = "0.35 0.4", AnchorMax = "0.65 0.6" },
                    CursorEnabled = true
                }, "Overlay", "ParachuteSpawnUI");
                
                container.Add(new CuiLabel
                {
                    Text = { Text = $"{repConfig.ProphetTierName.ToUpper()} AERIAL SPAWN", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 0.8", AnchorMax = "1 1" }
                }, "ParachuteSpawnUI");
                
                string tierName = GetTierName(repData[player.userID]);
                container.Add(new CuiLabel
                {
                    Text = { Text = $"As a {tierName}, you can spawn at high altitude\nwith tactical advantage!", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                    RectTransform = { AnchorMin = "0 0.4", AnchorMax = "1 0.8" }
                }, "ParachuteSpawnUI");
                
                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.8 0.2 1", Command = "rep.parachutespawn" },
                    RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.45 0.35" },
                    Text = { Text = "Aerial Spawn", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ParachuteSpawnUI");
                
                container.Add(new CuiButton
                {
                    Button = { Color = "0.6 0.6 0.6 1", Command = "rep.normalspawn" },
                    RectTransform = { AnchorMin = "0.55 0.05", AnchorMax = "0.95 0.35" },
                    Text = { Text = "Ground Spawn", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ParachuteSpawnUI");
                
                CuiHelper.AddUi(player, container);
                
                timer.Once(15f, () => SafeExecute(() => {
                    if (IsValidPlayer(player))
                        CuiHelper.DestroyUi(player, "ParachuteSpawnUI");
                }));
            }
            catch (Exception ex)
            {
                PrintError($"OfferParachuteSpawn error for {player?.displayName}: {ex.Message}");
            }
        }
        
        [ConsoleCommand("rep.parachutespawn")]
        private void CmdParachuteSpawn(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (!IsValidPlayer(player)) return;
            
            SafeExecute(() => {
                CuiHelper.DestroyUi(player, "ParachuteSpawnUI");
                ExecuteParachuteSpawn(player);
            });
        }
        
        [ConsoleCommand("rep.normalspawn")]
        private void CmdNormalSpawn(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (!IsValidPlayer(player)) return;
            
            SafeExecute(() => CuiHelper.DestroyUi(player, "ParachuteSpawnUI"));
        }

        private void ExecuteParachuteSpawn(BasePlayer player)
        {
            if (!IsValidPlayer(player)) return;

            try
            {
                float mapSize = ConVar.Server.worldsize;
                Vector3 mapCenter = Vector3.zero;
                
                float spawnRadius = repConfig.ParachuteSpawnRadius;
                float spawnHeight = repConfig.ParachuteSpawnHeight;
                
                float randomX = UnityEngine.Random.Range(-spawnRadius, spawnRadius);
                float randomZ = UnityEngine.Random.Range(-spawnRadius, spawnRadius);
                
                Vector3 spawnPos = new Vector3(
                    mapCenter.x + randomX,
                    spawnHeight + 300f,
                    mapCenter.z + randomZ
                );

                player.Teleport(spawnPos);
                
                timer.Once(2f, () => SafeExecute(() => CreateParachuteEntity(player)));
                
                // Give starter items
                if (repConfig.ParachuteGiveItems)
                {
                    timer.Once(1f, () => SafeExecute(() => GiveParachuteItems(player)));
                }

                string message = string.Format(repConfig.ParachuteSpawnMessage, spawnHeight);
                player.ChatMessage(message);
            }
            catch (Exception ex)
            {
                PrintError($"ExecuteParachuteSpawn error for {player?.displayName}: {ex.Message}");
            }
        }

        private void GiveParachuteItems(BasePlayer player)
        {
            if (!IsValidPlayer(player) || repConfig.ParachuteStarterItems == null) return;
            
            try
            {
                foreach (var item in repConfig.ParachuteStarterItems)
                {
                    var newItem = ItemManager.CreateByName(item.Key, item.Value);
                    if (newItem != null && player.inventory?.GiveItem(newItem) != true)
                    {
                        newItem.Drop(player.transform.position + Vector3.up * 1f, Vector3.zero);
                    }
                }
                player.ChatMessage(repConfig.ParachuteSurvivalKitMessage);
            }
            catch (Exception ex)
            {
                PrintError($"GiveParachuteItems error for {player?.displayName}: {ex.Message}");
            }
        }

        private void CreateParachuteEntity(BasePlayer player)
        {
            if (!IsValidPlayer(player)) return;

            try
            {
                Vector3 position = player.transform.position;
                var rotation = Quaternion.Euler(new Vector3(0f, player.GetNetworkRotation().eulerAngles.y, 0f));

                // Create dropped item as parachute base
                DroppedItem chutePack = ItemManager.CreateByItemID(476066818, 1, 0).Drop(position, Vector3.zero, rotation).GetComponent<DroppedItem>();
                chutePack.allowPickup = false;
                chutePack.CancelInvoke("IdleDestroy");

                // Add our parachute entity component
                var parachuteEntity = chutePack.gameObject.AddComponent<SimpleParachuteEntity>();
                parachuteEntity.SetPlayer(player);
                
                player.ChatMessage(repConfig.ParachuteDeployMessage);
            }
            catch (Exception ex)
            {
                PrintError($"CreateParachuteEntity error for {player?.displayName}: {ex.Message}");
            }
        }

        // Working parachute entity class
        public class SimpleParachuteEntity : MonoBehaviour
        {
            private DroppedItem worldItem;
            private Rigidbody myRigidbody;
            private BaseEntity chair;
            private BaseMountable chairMount;
            private BaseEntity parachute;
            private BasePlayer player;
            
            private bool enabled = false;
            public bool wantsDismount = false;
            
            private void Awake()
            {
                worldItem = GetComponent<DroppedItem>();
                if (worldItem == null) { OnDestroy(); return; }
                
                myRigidbody = worldItem.GetComponent<Rigidbody>();
                if (myRigidbody == null) { OnDestroy(); return; }

                // Create parachute visual
                parachute = GameManager.server.CreateEntity("assets/prefabs/misc/parachute/parachute.prefab", new Vector3(), new Quaternion(), false);
                parachute.enableSaving = false;
                parachute.SetParent(worldItem, 0, false, false);
                parachute?.Spawn();
                parachute.transform.localPosition += new Vector3(0f, 1.3f, -0.1f);

                // Create invisible chair for mounting
                chair = GameManager.server.CreateEntity("assets/bundled/prefabs/static/chair.invisible.static.prefab", new Vector3(), new Quaternion(), false);
                chair.enableSaving = false;
                chair.GetComponent<BaseMountable>().isMobile = true;
                chair.Spawn();
                chair.transform.localPosition += new Vector3(0f, -1f, 0f);
                chair.SetParent(parachute, 0, false, false);
                chair.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);

                chairMount = chair.GetComponent<BaseMountable>();
                if (chairMount == null) { OnDestroy(); return; }
            }

            public void SetPlayer(BasePlayer player)
            {
                this.player = player;
                chair.GetComponent<BaseMountable>().MountPlayer(player);
                enabled = true;
            }

            private void FixedUpdate()
            {
                if (!enabled || chair == null || player == null || !chairMount._mounted) 
                { 
                    OnDestroy(); 
                    return; 
                }

                if (myRigidbody != null)
                {
                    // Enhanced parachute physics
                    myRigidbody.AddForce(-myRigidbody.velocity * 0.5f, ForceMode.Acceleration);
                    
                    // Controlled fall speed
                    if (myRigidbody.velocity.y < -15f)
                    {
                        myRigidbody.velocity = new Vector3(myRigidbody.velocity.x, -15f, myRigidbody.velocity.z);
                    }

                    // Movement controls
                    if (player.serverInput != null)
                    {
                        Vector3 inputVector = Vector3.zero;
                        bool hasInput = false;
                        
                        if (player.serverInput.IsDown(BUTTON.FORWARD))
                        {
                            inputVector += Vector3.forward;
                            hasInput = true;
                        }
                        if (player.serverInput.IsDown(BUTTON.BACKWARD))
                        {
                            inputVector += Vector3.back;
                            hasInput = true;
                        }
                if (player.serverInput.IsDown(BUTTON.LEFT))
                {
                    // Rotate the chair/parachute left (counter-clockwise)
                    chair.transform.Rotate(0, -45f * Time.fixedDeltaTime, 0);
                    hasInput = true;
                }
                if (player.serverInput.IsDown(BUTTON.RIGHT))
                {
                    // Rotate the chair/parachute right (clockwise)
                    chair.transform.Rotate(0, 45f * Time.fixedDeltaTime, 0);
                    hasInput = true;
                }

                if (hasInput)
                {
                    Vector3 playerForward = player.eyes.HeadForward();
                    Vector3 playerRight = player.eyes.HeadRight();

                    playerForward.y = 0;
                    playerRight.y = 0;
                    playerForward.Normalize();
                    playerRight.Normalize();

                    // Apply forward/backward movement in the direction the parachute is facing
                    Vector3 worldMovement = Vector3.zero;
                    if (player.serverInput.IsDown(BUTTON.FORWARD))
                    {
                        worldMovement += chair.transform.forward;
                    }
                    if (player.serverInput.IsDown(BUTTON.BACKWARD))
                    {
                        worldMovement -= chair.transform.forward;
                    }

                    if (worldMovement != Vector3.zero)
                    {
                        worldMovement = (worldMovement.normalized) * 5f; // Adjust speed as needed
                        worldMovement.y = 0; // Keep only horizontal movement

                        float forceMultiplier = 50f;
                        if (player.serverInput.IsDown(BUTTON.SPRINT))
                            forceMultiplier = 80f;

                        myRigidbody.AddForce(worldMovement * forceMultiplier, ForceMode.Acceleration);
                    }
                }
                        }
                        
                        // Cut away parachute
                        if (player.serverInput.WasJustPressed(BUTTON.JUMP))
                        {
                            if (wantsDismount)
                            {
                                OnDestroy();
                                return;
                            }
                            wantsDismount = true;
                            player.ChatMessage("<color=#ff0000>Press SPACE again to cut parachute!</color>");
                        }
                    }
                    
                    // Speed limiter
                    if (myRigidbody.velocity.magnitude > 80f)
                    {
                        myRigidbody.velocity = myRigidbody.velocity.normalized * 80f;
                    }
                }
            }
	}
            private void OnCollisionEnter(Collision collision)
            {
                if (!enabled) return;
                if ((1 << collision.gameObject.layer & 1084293393) > 0)
                {
                    OnDestroy();
                }
            }

            public void OnDestroy()
            {
                enabled = false;
                
                if (chair != null && chairMount != null && chairMount.IsMounted())
                    chairMount.DismountPlayer(player, false);
                    
                if (player != null && player.isMounted)
                    player.DismountObject();

                if (chair != null && !chair.IsDestroyed) chair.Kill();
                if (parachute != null && !parachute.IsDestroyed) parachute.Kill();
                if (worldItem != null && !worldItem.IsDestroyed) worldItem.Kill();
                
                GameObject.Destroy(this);
            }
        }
}
        // Hook to handle dismounting
        private object CanDismountEntity(BasePlayer player, BaseMountable entity)
        {
            if (player == null || entity == null) return null;
            var isParachuting = entity.GetComponentInParent<SimpleParachuteEntity>();
            if (isParachuting != null && !isParachuting.wantsDismount) return false;
            return null;
        }

        #endregion

        #region Event Handlers

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            
            // Clean up any existing UI elements
            NextTick(() => {
                CuiHelper.DestroyUi(player, "ServerManagerMain");
                CuiHelper.DestroyUi(player, "ServerManagerContent");
                CloseLiveMapView(player);
            });
            
            SafeExecute(() => {
                if (IsValidPlayer(player))
                {
                    EnsureRep(player.userID);
                    
                    if (repConfig.EnableHUD)
                        timer.Once(4f, () => SafeExecute(() => { if (IsValidPlayer(player) && !player.IsSleeping()) CreateOrUpdateHUD(player); }));
                }
            });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            
            CloseLiveMapView(player);
            lastRadiationTime.Remove(player.userID);
            liveMapLastUpdate.Remove(player.userID);
            
            if (repConfig.EnableHUD)
                DestroyHUD(player);
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            SafeExecute(() => {
                if (repConfig.EnableHUD && IsValidPlayer(player))
                    timer.Once(2f, () => SafeExecute(() => { if (IsValidPlayer(player) && !player.IsSleeping()) CreateOrUpdateHUD(player); }));
            });
        }

        private void OnPlayerSleep(BasePlayer player)
        {
            SafeExecute(() => {
                if (player != null && repConfig.EnableHUD) 
                    DestroyHUD(player);
            });
        }

        private void OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            SafeExecute(() => {
                if (victim != null && repConfig.EnableHUD) 
                    DestroyHUD(victim);
                
                if (info?.Initiator is BasePlayer killer && killer != victim && IsValidPlayer(killer))
                {
                    EnsureRep(killer.userID);
                    EnsureRep(victim.userID);
                    
                    int victimRep = repData[victim.userID];
                    string victimTier = GetTierName(victimRep);
                    
                    int change = GetPvPRepChange(victimTier);
                    
                    if (change != 0)
                    {
                        int oldRep = repData[killer.userID];
                        repData[killer.userID] = Mathf.Clamp(oldRep + change, repConfig.MinReputation, repConfig.MaxReputation);
                        
                        string changeColor = change > 0 ? "#00ff00" : "#ff0000";
                        string changeText = change > 0 ? $"+{change}" : change.ToString();
                        killer.ChatMessage($"<color={changeColor}>{changeText} Reputation</color> for killing a {victimTier}");
                        
                        SaveData();
                    }
                }
            });
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            SafeExecute(() => {
                if (entity == null || info?.InitiatorPlayer == null) return;
                
                if (entity is NPCPlayer || 
                    entity.ShortPrefabName.Contains("scientist") ||
                    entity.ShortPrefabName.Contains("dweller") ||
                    entity.ShortPrefabName.Contains("bandit") ||
                    entity.name.ToLower().Contains("npc"))
                {
                    var player = info.InitiatorPlayer;
                    if (!IsValidPlayer(player) || !repConfig.NPCKillPenaltyEnabled) return;
                    
                    EnsureRep(player.userID);
                    
                    int oldRep = repData[player.userID];
                    int penalty = repConfig.NPCKillPenalty;
                    repData[player.userID] = Mathf.Clamp(oldRep + penalty, repConfig.MinReputation, repConfig.MaxReputation);
                    
                    player.ChatMessage($"<color=#ff0000>{penalty} Reputation</color> for killing an NPC");
                    SaveData();
                }
            });
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            if (!repConfig.EnableGatherBonus) return;
            
            SafeExecute(() => {
                var player = entity?.ToPlayer();
                if (!IsValidPlayer(player)) return;
                
                EnsureRep(player.userID);
                float multiplier = GetGatherMultiplier(repData[player.userID]);

                if (multiplier != 1f && item != null)
                {
                    item.amount = Mathf.CeilToInt(item.amount * multiplier);
                    
                    if (UnityEngine.Random.Range(0f, 1f) < repConfig.GatherBonusMessageFrequency)
                    {
                        string tierName = GetTierName(repData[player.userID]);
                        if (multiplier > 1f)
                            player.ChatMessage($"<color=#00ff00>Gather bonus active!</color> ({tierName})");
                        else if (multiplier < 1f)
                            player.ChatMessage($"<color=#ff8000>Gather penalty active.</color> ({tierName})");
                    }
                }
            });
        }

        #endregion
		
		#region Complete Live Map System

        string GetLiveMapImagePath()
        {
            string urlFile = Path.Combine(Interface.Oxide.DataDirectory, "ServerManager_MapURL.txt");
            
            if (!File.Exists(urlFile))
            {
                CreateLiveMapUrlFile(urlFile);
            }

            try
            {
                string[] lines = File.ReadAllLines(urlFile);
                foreach (string line in lines)
                {
                    string trimmed = line.Trim();
                    if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith("#") && !trimmed.StartsWith("//"))
                    {
                        if (trimmed.StartsWith("http"))
                        {
                            Puts($"[ServerManager] Using map URL: {trimmed}");
                            return trimmed;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"[ServerManager] Error reading map URL file: {ex.Message}");
            }

            PrintWarning("[ServerManager] No valid map URL found in ServerManager_MapURL.txt");
            return "https://rustmaps.com/img/231/3750/logo.png";
        }

        void CreateLiveMapUrlFile(string filePath)
        {
            try
            {
                string content = @"# ServerManager Live Map URL Configuration
# Paste your map image URL below (must start with http or https)
# Get your map from RustMaps.com, upload to imgur.com, then paste the direct image URL here
# 
# Example:
# https://i.imgur.com/YourMapImage.jpg
# 
# After adding your URL, reload the plugin with: o.reload ServerManager

";
                File.WriteAllText(filePath, content);
                Puts($"[ServerManager] Created map URL file: {filePath}");
                Puts("[ServerManager] Edit this file and add your map image URL, then reload!");
            }
            catch (Exception ex)
            {
                PrintError($"[ServerManager] Failed to create map URL file: {ex.Message}");
            }
        }

        void LoadLiveMapImageCache()
        {
            // Placeholder for future caching functionality
        }

        Vector2 LiveMapWorldToNormalized(Vector3 pos)
        {
            float normX = (pos.x + LiveMapSize / 2f) / LiveMapSize;
            float normY = (pos.z + LiveMapSize / 2f) / LiveMapSize;
            return new Vector2(Mathf.Clamp01(normX), Mathf.Clamp01(normY));
        }

        Vector2 LiveMapNormalizedToWorld(float normX, float normY)
        {
            float worldX = (normX * LiveMapSize) - (LiveMapSize / 2f);
            float worldZ = (normY * LiveMapSize) - (LiveMapSize / 2f);
            return new Vector2(worldX, worldZ);
        }

        static string LiveMapColorToString(Color color)
        {
            return $"{color.r:F2} {color.g:F2} {color.b:F2} {color.a:F2}";
        }

        void CreateLiveMapUI(BasePlayer player, string mapPath)
        {
            CuiElementContainer container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.98" },
                RectTransform = { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" },
                CursorEnabled = true
            }, "Overlay", LiveMapContainerName);

            container.Add(new CuiElement
            {
                Name = LiveMapContainerName + "_MapImage",
                Parent = LiveMapContainerName,
                Components =
                {
                    new CuiRawImageComponent { Url = mapPath, Color = "1 1 1 1" },
                    new CuiRectTransformComponent { AnchorMin = "0.02 0.08", AnchorMax = "0.98 0.95" }
                }
            });

            CreateLiveMapClickGrid(container, player);
            
            container.Add(new CuiButton
            {
                Button = { Command = $"sm.livemap.close {player.userID}", Color = "0.8 0.2 0.2 0.9" },
                RectTransform = { AnchorMin = "0.94 0.96", AnchorMax = "0.99 0.99" },
                Text = { Text = "✕", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, LiveMapContainerName);

            container.Add(new CuiLabel
            {
                Text = { Text = "Click to select event location | Yellow = Selected, Colored = Players", 
                        FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.7 0.06" }
            }, LiveMapContainerName);

            container.Add(new CuiButton
            {
                Button = { Command = $"sm.livemap.confirm {player.userID}", Color = "0.2 0.8 0.2 0.9" },
                RectTransform = { AnchorMin = "0.75 0.02", AnchorMax = "0.92 0.06" },
                Text = { Text = "Confirm Location", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, LiveMapContainerName);

            CuiHelper.AddUi(player, container);
        }

        void CreateLiveMapClickGrid(CuiElementContainer container, BasePlayer player)
        {
            float cellSize = 1f / LiveMapGridResolution;
            
            for (int x = 0; x < LiveMapGridResolution; x++)
            {
                for (int y = 0; y < LiveMapGridResolution; y++)
                {
                    float minX = x * cellSize;
                    float minY = y * cellSize;
                    float maxX = minX + cellSize;
                    float maxY = minY + cellSize;
                    float centerX = minX + cellSize / 2f;
                    float centerY = minY + cellSize / 2f;

                    container.Add(new CuiButton
                    {
                        Button = { 
                            Command = $"sm.livemap.click {player.userID} {centerX:F6} {centerY:F6}", 
                            Color = "0 0 0 0" 
                        },
                        RectTransform = { 
                            AnchorMin = $"{minX:F6} {minY:F6}", 
                            AnchorMax = $"{maxX:F6} {maxY:F6}" 
                        },
                        Text = { Text = "" }
                    }, LiveMapContainerName + "_MapImage");
                }
            }
        }

        void UpdateLiveMapDotsAndMarkers(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            CuiHelper.DestroyUi(player, LiveMapDotsContainerName);
            CuiHelper.DestroyUi(player, LiveMapMarkersContainerName);
            
            CuiElementContainer container = new CuiElementContainer();

            // Show selected location marker
            if (liveMapSingleMarker.HasValue)
            {
                var marker = liveMapSingleMarker.Value;
                Vector2 norm = LiveMapWorldToNormalized(new Vector3(marker.x, 0, marker.y));
                
                if (norm.x >= 0 && norm.x <= 1 && norm.y >= 0 && norm.y <= 1)
                {
                    float size = 0.006f;
                    container.Add(new CuiPanel
                    {
                        Image = { Color = "1 1 0 1" },
                        RectTransform =
                        {
                            AnchorMin = $"{norm.x - size:F6} {norm.y - size:F6}",
                            AnchorMax = $"{norm.x + size:F6} {norm.y + size:F6}"
                        }
                    }, LiveMapContainerName + "_MapImage", LiveMapMarkersContainerName);
                }
            }

            var activePlayers = BasePlayer.activePlayerList.Take(LiveMapMaxPlayers).ToList();
            
            for (int i = 0; i < activePlayers.Count; i++)
            {
                var p = activePlayers[i];
                if (p == null || !p.IsConnected) continue;

                Vector2 norm = LiveMapWorldToNormalized(p.transform.position);
                
                if (norm.x < 0 || norm.x > 1 || norm.y < 0 || norm.y > 1) continue;

                float size = 0.008f;
                string colorStr = LiveMapColorToString(liveMapDotColors[i % liveMapDotColors.Length]);

                container.Add(new CuiElement
                {
                    Name = $"{LiveMapDotsContainerName}_{i}",
                    Parent = LiveMapContainerName + "_MapImage",
                    Components =
                    {
                        new CuiImageComponent { Color = colorStr },
                        new CuiRectTransformComponent 
                        {
                            AnchorMin = $"{norm.x - size:F6} {norm.y - size:F6}",
                            AnchorMax = $"{norm.x + size:F6} {norm.y + size:F6}"
                        }
                    }
                });

                string displayName = p.displayName.Length > 10 ? p.displayName.Substring(0, 10) : p.displayName;
                container.Add(new CuiElement
                {
                    Name = $"{LiveMapDotsContainerName}_label_{i}",
                    Parent = LiveMapContainerName + "_MapImage",
                    Components =
                    {
                        new CuiTextComponent
                        {
                            Text = displayName,
                            FontSize = 10,
                            Align = TextAnchor.MiddleCenter,
                            Color = "1 1 1 1"
                        },
                        new CuiRectTransformComponent
                        {
                            AnchorMin = $"{norm.x - 0.04f:F6} {norm.y + 0.015f:F6}",
                            AnchorMax = $"{norm.x + 0.04f:F6} {norm.y + 0.035f:F6}"
                        }
                    }
                });
            }

            CuiHelper.AddUi(player, container);
            liveMapLastUpdate[player.userID] = DateTime.Now;
        }

        void CloseLiveMapView(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, LiveMapContainerName);
            CuiHelper.DestroyUi(player, LiveMapDotsContainerName);
            CuiHelper.DestroyUi(player, LiveMapMarkersContainerName);

            if (liveMapActiveTimers.TryGetValue(player.userID, out Timer t))
            {
                t.Destroy();
                liveMapActiveTimers.Remove(player.userID);
            }
            
            liveMapLastUpdate.Remove(player.userID);
        }

        [ConsoleCommand("sm.livemap.click")]
        void CmdLiveMapClick(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length < 3) return;
            if (!ulong.TryParse(arg.Args[0], out ulong id)) return;
            if (!float.TryParse(arg.Args[1], out float normX) || !float.TryParse(arg.Args[2], out float normY)) return;

            BasePlayer player = BasePlayer.FindByID(id);
            if (player == null || !HasPerm(player)) return;

            Vector2 world = LiveMapNormalizedToWorld(normX, normY);
            liveMapSingleMarker = world;

            player.ChatMessage($"<color=green>Location selected: X={world.x:F1}, Z={world.y:F1}</color>");
            
            NextTick(() => UpdateLiveMapDotsAndMarkers(player));
        }

        [ConsoleCommand("sm.livemap.close")]
        void CmdLiveMapClose(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length == 0) return;
            if (!ulong.TryParse(arg.Args[0], out ulong id)) return;

            BasePlayer player = BasePlayer.FindByID(id);
            if (player != null) CloseLiveMapView(player);
        }

        [ConsoleCommand("sm.livemap.confirm")]
        void CmdLiveMapConfirm(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length == 0) return;
            if (!ulong.TryParse(arg.Args[0], out ulong id)) return;

            BasePlayer player = BasePlayer.FindByID(id);
            if (player == null || !HasPerm(player)) return;

            if (liveMapSingleMarker.HasValue)
            {
                var marker = liveMapSingleMarker.Value;
                selectedEventPosition = new Vector3(marker.x, 0, marker.y);
                selectedGridCoordinate = "";
                SaveData();
                
                player.ChatMessage($"<color=green>Event location confirmed: X={marker.x:F1}, Z={marker.y:F1}</color>");
                CloseLiveMapView(player);
                OpenEventsTab(player);
            }
            else
            {
                player.ChatMessage("<color=red>No location selected!</color>");
            }
        }

        #endregion

        #region Chat Commands

        [ChatCommand("rep")]
        private void RepCommand(BasePlayer player, string command, string[] args)
        {
            SafeExecute(() => {
                if (!IsValidPlayer(player)) return;
                
                EnsureRep(player.userID);
                int rep = repData[player.userID];
                string tier = GetTierName(rep);
                string color = GetTierColor(rep);
                
                string statusInfo = "";
                if (repConfig.EnableParachuteSpawn && CanUseParachute(rep))
                {
                    statusInfo += " | Aerial Spawn: Available ✈";
                }
                
                player.ChatMessage($"Your reputation: <color={color}>{rep} ({tier})</color>{statusInfo}");
            });
        }

        [ChatCommand("repset")]
        private void RepSetCommand(BasePlayer player, string command, string[] args)
        {
            SafeExecute(() => {
                if (!IsValidPlayer(player)) return;
                
                if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "reputationsystemhud.admin"))
                {
                    player.ChatMessage("<color=#ff0000>No permission.</color>");
                    return;
                }
                
                if (args.Length < 2)
                {
                    player.ChatMessage("Usage: /repset <playername> <amount>");
                    return;
                }
                
                BasePlayer target = BasePlayer.Find(args[0]);
                if (target == null)
                {
                    player.ChatMessage("<color=#ff0000>Player not found.</color>");
                    return;
                }
                
                if (!int.TryParse(args[1], out int amount))
                {
                    player.ChatMessage("<color=#ff0000>Invalid amount.</color>");
                    return;
                }
                
                bool success = SetPlayerReputation(target, amount);
                
                if (success)
                {
                    player.ChatMessage($"Set <color=#00ff00>{target.displayName}</color>'s reputation to <color=#00ff00>{amount}</color>.");
                    target.ChatMessage($"Your reputation has been set to <color=#00ff00>{amount}</color> by an admin.");
                }
                else
                {
                    player.ChatMessage("<color=#ff0000>Failed to set reputation.</color>");
                }
            });
        }

        [ChatCommand("replist")]
        private void RepListCommand(BasePlayer player, string command, string[] args)
        {
            SafeExecute(() => {
                if (!IsValidPlayer(player)) return;
                
                player.ChatMessage("<color=#00ff00>=== Online Player Reputations ===</color>");
                var players = BasePlayer.activePlayerList.OrderByDescending(p => GetPlayerReputation(p)).Take(15).ToList();
                
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    
                    EnsureRep(p.userID);
                    int rep = repData[p.userID];
                    string tier = GetTierName(rep);
                    string color = GetTierColor(rep);
                    
                    string statusInfo = "";
                    if (repConfig.EnableParachuteSpawn && CanUseParachute(rep))
                    {
                        statusInfo += " ✈";
                    }
                    
                    player.ChatMessage($"<color={color}>{p.displayName}: {rep} ({tier})</color>{statusInfo}");
                }
                
                if (players.Count > 15)
                    player.ChatMessage($"<color=#888888>... and {players.Count - 15} more players</color>");
            });
        }

        [ChatCommand("repstats")]
        private void RepStatsCommand(BasePlayer player, string command, string[] args)
        {
            SafeExecute(() => {
                if (!IsValidPlayer(player)) return;
                
                var stats = GetReputationStats();
                player.ChatMessage("<color=#00ff00>=== Server Reputation Statistics ===</color>");
                player.ChatMessage($"Total Players: <color=#ffff00>{stats["TotalPlayers"]}</color> | Online: <color=#ffff00>{stats["OnlineCount"]}</color>");
                player.ChatMessage($"Average Reputation: <color=#ffff00>{stats["AverageReputation"]}</color>");
                player.ChatMessage($"<color=#ff0000>{repConfig.InfidelTierName}s:</color> {stats["InfidelCount"]} | <color=#ff8000>{repConfig.SinnerTierName}s:</color> {stats["SinnerCount"]}");
                player.ChatMessage($"<color=#ffff00>{repConfig.AverageTierName}:</color> {stats["AverageCount"]} | <color=#ffffff>{repConfig.DiscipleTierName}s:</color> {stats["DiscipleCount"]} | <color=#00ff00>{repConfig.ProphetTierName}s:</color> {stats["ProphetCount"]}");
                if (repConfig.EnableParachuteSpawn)
                    player.ChatMessage($"Aerial Spawn Eligible: <color=#00ffff>{stats["ParachuteEligible"]}</color> players ✈");
            });
        }

        [ChatCommand("sm")]
        void CmdOpenGUI(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player))
            {
                player.ChatMessage("<color=red>You do not have permission to use this command.</color>");
                return;
            }
            OpenMainGUI(player);
        }

        #endregion

        #region API Methods for External Integration

        public int GetPlayerReputation(ulong userID)
        {
            try
            {
                EnsureRep(userID);
                return repData?.ContainsKey(userID) == true ? repData[userID] : repConfig.DefaultReputation;
            }
            catch
            {
                return repConfig.DefaultReputation;
            }
        }

        public bool SetPlayerReputation(ulong userID, int reputation)
        {
            try
            {
                EnsureRep(userID);
                repData[userID] = Mathf.Clamp(reputation, repConfig.MinReputation, repConfig.MaxReputation);
                SaveData();
                
                var player = BasePlayer.FindByID(userID);
                if (IsValidPlayer(player) && repConfig.EnableHUD)
                {
                    timer.Once(0.2f, () => SafeExecute(() => CreateOrUpdateHUD(player)));
                }
                
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"SetPlayerReputation failed: {ex.Message}");
                return false;
            }
        }

        public Dictionary<ulong, int> GetAllReputationData()
        {
            try
            {
                return new Dictionary<ulong, int>(repData ?? new Dictionary<ulong, int>());
            }
            catch
            {
                return new Dictionary<ulong, int>();
            }
        }

        public void RefreshAllPlayersHUD()
        {
            if (!repConfig.EnableHUD) return;
            
            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                SafeExecute(() => {
                    if (IsValidPlayer(player) && !player.IsSleeping())
                        CreateOrUpdateHUD(player);
                });
            }
        }

        public Dictionary<string, int> GetReputationStats()
        {
            var stats = new Dictionary<string, int>
            {
                ["TotalPlayers"] = 0,
                ["InfidelCount"] = 0,
                ["SinnerCount"] = 0,
                ["AverageCount"] = 0,
                ["DiscipleCount"] = 0,
                ["ProphetCount"] = 0,
                ["AverageReputation"] = 0,
                ["ParachuteEligible"] = 0,
                ["OnlineCount"] = 0
            };

            try
            {
                stats["TotalPlayers"] = repData?.Count ?? 0;
                stats["OnlineCount"] = BasePlayer.activePlayerList?.Count ?? 0;

                if (repData?.Count > 0)
                {
                    int totalRep = 0;
                    foreach (var rep in repData.Values)
                    {
                        totalRep += rep;
                        string tier = GetTierName(rep);
                        stats[tier + "Count"]++;
                        
                        if (CanUseParachute(rep))
                            stats["ParachuteEligible"]++;
                    }

                    stats["AverageReputation"] = totalRep / repData.Count;
                }
            }
            catch (Exception ex)
            {
                PrintError($"GetReputationStats error: {ex.Message}");
            }

            return stats;
        }

        public bool ResetAllReputation()
        {
            try
            {
                if (repData != null)
                {
                    foreach (var userID in repData.Keys.ToList())
                    {
                        repData[userID] = repConfig.DefaultReputation;
                    }
                    SaveData();
                }
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"ResetAllReputation failed: {ex.Message}");
                return false;
            }
        }

        #endregion
		
		#region Main UI System

        void OpenMainGUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "ServerManagerMain");
            CuiHelper.DestroyUi(player, "ServerManagerContent");

            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.1 0.1 0.1 0.9" },
                RectTransform = { AnchorMin = "0.15 0.1", AnchorMax = "0.85 0.9" },
                CursorEnabled = true
            }, "Overlay", "ServerManagerMain");

            container.Add(new CuiLabel
            {
                Text = { Text = "SERVER MANAGER v4.0 - COMPLETE INTEGRATION", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0.95", AnchorMax = "1 1" }
            }, "ServerManagerMain");

            // Updated tabs
            string[] tabs = { "General", "Kits", "Events", "Reputation", "Rep Config", "Live Map" };
            for (int i = 0; i < tabs.Length; i++)
            {
                float xMin = i * (1f / tabs.Length);
                float xMax = xMin + (1f / tabs.Length);
                string tabCommand = tabs[i].ToLower().Replace(" ", "");
                
                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.2 0.2 1", Command = $"sm.tab {tabCommand}" },
                    RectTransform = { AnchorMin = $"{xMin} 0.88", AnchorMax = $"{xMax} 0.94" },
                    Text = { Text = tabs[i], FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerMain");
            }

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.1 0.1 1", Command = "sm.close" },
                RectTransform = { AnchorMin = "0.94 0.94", AnchorMax = "0.99 0.99" },
                Text = { Text = "×", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerMain");

            CuiHelper.AddUi(player, container);
            OpenGeneralTab(player);
        }

        [ConsoleCommand("sm.tab")]
        void CmdSwitchTab(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;
            if (arg.Args.Length == 0) return;

            CuiHelper.DestroyUi(player, "ServerManagerContent");

            switch (arg.Args[0])
            {
                case "general": OpenGeneralTab(player); break;
                case "kits": OpenKitsTab(player); break;
                case "events": OpenEventsTab(player); break;
                case "reputation": OpenReputationTab(player); break;
                case "repconfig": OpenReputationConfigTab(player); break;
                case "livemap": OpenLiveMapTab(player); break;
            }
        }

        [ConsoleCommand("sm.close")]
        void CmdClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            CuiHelper.DestroyUi(player, "ServerManagerMain");
            CuiHelper.DestroyUi(player, "ServerManagerContent");
            CloseLiveMapView(player);
        }

        void OpenGeneralTab(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "ServerManagerContent");

            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.86" },
                CursorEnabled = true
            }, "ServerManagerMain", "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "GENERAL SERVER SETTINGS", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.1 0.9", AnchorMax = "0.9 0.95" }
            }, "ServerManagerContent");

            // Decay Factor Section
            container.Add(new CuiLabel
            {
                Text = { Text = $"Decay Factor: {decayFactor:F1}", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.78", AnchorMax = "0.4 0.83" }
            }, "ServerManagerContent");

            for (int i = 0; i < 10; i++)
            {
                float factor = (i + 1) * 0.1f;
                float xPos = i * 0.04f + 0.05f;
                string color = Mathf.Approximately(decayFactor, factor) ? "0.2 0.8 0.2 1" : "0.3 0.3 0.3 1";

                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = $"sm.decay.set {factor:F1}" },
                    RectTransform = { AnchorMin = $"{xPos} 0.72", AnchorMax = $"{xPos + 0.035f} 0.77" },
                    Text = { Text = factor.ToString("F1"), FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");
            }

            // Crate Unlock Time Section
            container.Add(new CuiLabel
            {
                Text = { Text = $"Locked Crate Timer: {crateUnlockTime} minutes", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.63", AnchorMax = "0.5 0.68" }
            }, "ServerManagerContent");

            for (int i = 1; i <= 15; i++)
            {
                float xPos = (i - 1) * 0.06f + 0.05f;
                float yPos = 0.57f;
                if (i > 8) { xPos = (i - 9) * 0.06f + 0.05f; yPos = 0.52f; }
                
                string color = crateUnlockTime == i ? "0.2 0.8 0.2 1" : "0.3 0.3 0.3 1";

                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = $"sm.crate.set {i}" },
                    RectTransform = { AnchorMin = $"{xPos} {yPos}", AnchorMax = $"{xPos + 0.055f} {yPos + 0.04f}" },
                    Text = { Text = i.ToString(), FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");
            }

            // Time of Day Section
            container.Add(new CuiLabel
            {
                Text = { Text = $"Time of Day: {(timeOfDay < 0 ? "Auto" : $"{timeOfDay:F0}:00")}", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.43", AnchorMax = "0.5 0.48" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = timeOfDay < 0 ? "0.2 0.8 0.2 1" : "0.3 0.3 0.3 1", Command = "sm.time.auto" },
                RectTransform = { AnchorMin = "0.05 0.37", AnchorMax = "0.12 0.42" },
                Text = { Text = "Auto", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            // Extended time selection
            int[] times = { 5, 6, 7, 12, 17, 18, 19, 0 };
            string[] timeLabels = { "5AM", "6AM", "7AM", "12PM", "5PM", "6PM", "7PM", "12AM" };
            for (int i = 0; i < times.Length; i++)
            {
                int col = i % 4;
                int row = i / 4;
                float xPos = 0.14f + (col * 0.08f);
                float yPos = 0.37f - (row * 0.05f);
                string color = Mathf.Approximately(timeOfDay, times[i]) ? "0.2 0.8 0.2 1" : "0.3 0.3 0.3 1";

                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = $"sm.time.set {times[i]}" },
                    RectTransform = { AnchorMin = $"{xPos} {yPos}", AnchorMax = $"{xPos + 0.07f} {yPos + 0.04f}" },
                    Text = { Text = timeLabels[i], FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");
            }

            // Environmental Controls Section
            container.Add(new CuiLabel
            {
                Text = { Text = "ENVIRONMENTAL CONTROLS", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.28", AnchorMax = "0.95 0.33" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.3 0.3 1", Command = "sm.env.clearweather" },
                RectTransform = { AnchorMin = "0.05 0.22", AnchorMax = "0.2 0.27" },
                Text = { Text = "Clear Weather", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.3 0.5 0.7 1", Command = "sm.env.rain" },
                RectTransform = { AnchorMin = "0.22 0.22", AnchorMax = "0.37 0.27" },
                Text = { Text = "Start Rain", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.7 0.5 0.3 1", Command = "sm.env.fog" },
                RectTransform = { AnchorMin = "0.39 0.22", AnchorMax = "0.54 0.27" },
                Text = { Text = "Add Fog", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.5 0.3 0.7 1", Command = "sm.env.wind" },
                RectTransform = { AnchorMin = "0.56 0.22", AnchorMax = "0.71 0.27" },
                Text = { Text = "Strong Wind", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            // Admin Utilities
            container.Add(new CuiLabel
            {
                Text = { Text = "ADMIN UTILITIES", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.13", AnchorMax = "0.95 0.18" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.3 0.3 1", Command = "sm.admin.save" },
                RectTransform = { AnchorMin = "0.05 0.07", AnchorMax = "0.2 0.12" },
                Text = { Text = "Force Save", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.3 0.8 0.3 1", Command = "sm.admin.gc" },
                RectTransform = { AnchorMin = "0.22 0.07", AnchorMax = "0.37 0.12" },
                Text = { Text = "Garbage Collect", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }

        #endregion
		
		#region Kits and Events Tabs

        void OpenKitsTab(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "ServerManagerContent");
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.86" },
                CursorEnabled = true
            }, "ServerManagerMain", "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "CUSTOM KIT BUILDER", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.1 0.9", AnchorMax = "0.9 0.95" }
            }, "ServerManagerContent");

            if (!customKits.ContainsKey(player.userID))
                customKits[player.userID] = new Dictionary<string, int>();

            var playerKit = customKits[player.userID];
            int totalItems = playerKit.Values.Sum();
            
            container.Add(new CuiLabel
            {
                Text = { Text = $"Kit Items: {playerKit.Count} types ({totalItems} total)", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.02 0.84", AnchorMax = "0.5 0.88" }
            }, "ServerManagerContent");

            // Display current kit items
            int itemIndex = 0;
            foreach (var kvp in playerKit.Take(12))
            {
                float yPos = 0.78f - (itemIndex * 0.06f);
                string itemName = commonItems.ContainsKey(kvp.Key) ? commonItems[kvp.Key] : kvp.Key;

                container.Add(new CuiLabel
                {
                    Text = { Text = $"• {itemName}: {kvp.Value}", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = $"0.02 {yPos}", AnchorMax = $"0.35 {yPos + 0.05f}" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.8 0.2 0.2 1", Command = $"sm.kit.removeitem {kvp.Key}" },
                    RectTransform = { AnchorMin = $"0.36 {yPos}", AnchorMax = $"0.4 {yPos + 0.05f}" },
                    Text = { Text = "×", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                itemIndex++;
            }

            container.Add(new CuiLabel
            {
                Text = { Text = "ADD ITEMS:", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.45 0.84", AnchorMax = "0.95 0.88" }
            }, "ServerManagerContent");

            // Add item buttons
            int buttonIndex = 0;
            foreach (var kvp in commonItems)
            {
                int col = buttonIndex % 6;
                int row = buttonIndex / 6;
                float xPos = 0.45f + (col * 0.09f);
                float yPos = 0.78f - (row * 0.13f);

                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.4 0.6 1", Command = $"sm.kit.additem {kvp.Key} 1" },
                    RectTransform = { AnchorMin = $"{xPos} {yPos + 0.06f}", AnchorMax = $"{xPos + 0.08f} {yPos + 0.11f}" },
                    Text = { Text = kvp.Value, FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.6 0.2 1", Command = $"sm.kit.additem {kvp.Key} 1" },
                    RectTransform = { AnchorMin = $"{xPos} {yPos + 0.04f}", AnchorMax = $"{xPos + 0.025f} {yPos + 0.06f}" },
                    Text = { Text = "1", FontSize = 7, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.4 0.6 0.2 1", Command = $"sm.kit.additem {kvp.Key} 10" },
                    RectTransform = { AnchorMin = $"{xPos + 0.027f} {yPos + 0.04f}", AnchorMax = $"{xPos + 0.052f} {yPos + 0.06f}" },
                    Text = { Text = "10", FontSize = 7, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.6 0.6 0.2 1", Command = $"sm.kit.additem {kvp.Key} 1000" },
                    RectTransform = { AnchorMin = $"{xPos + 0.055f} {yPos + 0.04f}", AnchorMax = $"{xPos + 0.08f} {yPos + 0.06f}" },
                    Text = { Text = "Max", FontSize = 7, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                buttonIndex++;
            }

            container.Add(new CuiButton
            {
                Button = { Color = "0.7 0.2 0.2 1", Command = "sm.kit.clear" },
                RectTransform = { AnchorMin = "0.05 0.02", AnchorMax = "0.18 0.08" },
                Text = { Text = "Clear Kit", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.7 0.2 1", Command = "sm.kit.selectrecipient" },
                RectTransform = { AnchorMin = "0.82 0.02", AnchorMax = "0.95 0.08" },
                Text = { Text = "Give Kit", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }

        void OpenSelectRecipientTab(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "ServerManagerContent");
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.86" },
                CursorEnabled = true
            }, "ServerManagerMain", "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "SELECT PLAYER TO RECEIVE KIT", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.1 0.85", AnchorMax = "0.9 0.9" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.4 1", Command = "sm.tab kits" },
                RectTransform = { AnchorMin = "0.05 0.9", AnchorMax = "0.15 0.95" },
                Text = { Text = "← Back", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            var onlinePlayers = BasePlayer.activePlayerList.OrderBy(p => p.displayName).ToList();
            float startY = 0.8f;
            float itemHeight = 0.08f;

            for (int i = 0; i < onlinePlayers.Count && i < 10; i++)
            {
                var p = onlinePlayers[i];
                float yMin = startY - (i * itemHeight);
                float yMax = yMin + itemHeight * 0.9f;

                string playerName = p.displayName.Length > 30 ? p.displayName.Substring(0, 30) + "..." : p.displayName;

                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.7 0.2 1", Command = $"sm.kit.give {p.userID}" },
                    RectTransform = { AnchorMin = $"0.1 {yMin}", AnchorMax = $"0.9 {yMax}" },
                    Text = { Text = playerName, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");
            }

            CuiHelper.AddUi(player, container);
        }

        void OpenEventsTab(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "ServerManagerContent");
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.86" },
                CursorEnabled = true
            }, "ServerManagerMain", "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "EVENT SPAWNER", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.1 0.9", AnchorMax = "0.9 0.95" }
            }, "ServerManagerContent");

            string posText = "";
            if (selectedEventPosition != Vector3.zero)
                posText = $"Event Position: X={selectedEventPosition.x:F0}, Z={selectedEventPosition.z:F0}";
            else
                posText = "Event Position: Not Set (will use your location)";

            container.Add(new CuiLabel
            {
                Text = { Text = posText, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.1 0.82", AnchorMax = "0.9 0.87" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.3 0.5 0.7 1", Command = "sm.event.setpos" },
                RectTransform = { AnchorMin = "0.1 0.75", AnchorMax = "0.35 0.8" },
                Text = { Text = "Set to My Location", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.5 0.3 0.7 1", Command = "sm.event.openlivemap" },
                RectTransform = { AnchorMin = "0.4 0.75", AnchorMax = "0.6 0.8" },
                Text = { Text = "Select on Live Map", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.7 0.3 0.3 1", Command = "sm.event.clearpos" },
                RectTransform = { AnchorMin = "0.65 0.75", AnchorMax = "0.9 0.8" },
                Text = { Text = "Clear Position", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            var events = new Dictionary<string, string>
            {
                ["airdrop"] = "Mass Airdrop",
                ["heli"] = "Attack Helicopter", 
                ["cargoship"] = "Cargo Ship",
                ["ch47"] = "Chinook Helicopter",
                ["supplydrop"] = "Supply Drop",
                ["bradleyapc"] = "Bradley APC",
                ["oilrig"] = "Oil Rig Event"
            };

            container.Add(new CuiLabel
            {
                Text = { Text = "SPAWN EVENTS:", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.1 0.67", AnchorMax = "0.9 0.72" }
            }, "ServerManagerContent");

            int eventIndex = 0;
            foreach (var evt in events)
            {
                float yPos = 0.6f - (eventIndex * 0.08f);
                
                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.6 0.2 1", Command = $"sm.event.spawn {evt.Key}" },
                    RectTransform = { AnchorMin = $"0.2 {yPos}", AnchorMax = $"0.8 {yPos + 0.07f}" },
                    Text = { Text = evt.Value, FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                eventIndex++;
            }

            CuiHelper.AddUi(player, container);
        }

        #endregion
		
		#region Reputation Tabs
		void OpenReputationTab(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "ServerManagerContent");
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.86" },
                CursorEnabled = true
            }, "ServerManagerMain", "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "INTEGRATED REPUTATION MANAGER", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.1 0.9", AnchorMax = "0.9 0.95" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "ONLINE PLAYERS", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.82", AnchorMax = "0.95 0.86" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "Player", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.05 0.78", AnchorMax = "0.4 0.82" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "Rep", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.4 0.78", AnchorMax = "0.5 0.82" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "Set Reputation", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.50 0.86", AnchorMax = "0.95 0.90" }
            }, "ServerManagerContent");

            var onlinePlayers = BasePlayer.activePlayerList.OrderBy(p => p.displayName).Take(12).ToList();
            float startY = 0.75f;
            float itemHeight = 0.055f;

            for (int i = 0; i < onlinePlayers.Count; i++)
            {
                var p = onlinePlayers[i];
                float yMin = startY - (i * itemHeight);
                float yMax = yMin + itemHeight * 0.9f;

                string playerName = p.displayName.Length > 25 ? p.displayName.Substring(0, 25) + "..." : p.displayName;
                
                EnsureRep(p.userID);
                int reputation = repData[p.userID];
                string tier = GetTierName(reputation);
                string repColor = GetTierColor(reputation);

                container.Add(new CuiLabel
                {
                    Text = { Text = playerName, FontSize = 9, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = $"0.05 {yMin}", AnchorMax = $"0.4 {yMax}" }
                }, "ServerManagerContent");

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{reputation} ({tier})", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = repColor },
                    RectTransform = { AnchorMin = $"0.4 {yMin}", AnchorMax = $"0.6 {yMax}" }
                }, "ServerManagerContent");

// 5-button reputation system: 0-25-50-75-100
container.Add(new CuiButton
{
    Button = { Color = "0.8 0.2 0.2 1", Command = $"sm.rep.set {p.userID} 0" },
    RectTransform = { AnchorMin = $"0.55 {yMin}", AnchorMax = $"0.61 {yMax}" },
    Text = { Text = "0", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
}, "ServerManagerContent");

container.Add(new CuiButton
{
    Button = { Color = "0.6 0.4 0.2 1", Command = $"sm.rep.set {p.userID} 25" },
    RectTransform = { AnchorMin = $"0.63 {yMin}", AnchorMax = $"0.69 {yMax}" },
    Text = { Text = "25", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
}, "ServerManagerContent");

container.Add(new CuiButton
{
    Button = { Color = "0.4 0.4 0.4 1", Command = $"sm.rep.set {p.userID} 50" },
    RectTransform = { AnchorMin = $"0.71 {yMin}", AnchorMax = $"0.77 {yMax}" },
    Text = { Text = "50", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
}, "ServerManagerContent");

container.Add(new CuiButton
{
    Button = { Color = "0.4 0.6 0.4 1", Command = $"sm.rep.set {p.userID} 75" },
    RectTransform = { AnchorMin = $"0.79 {yMin}", AnchorMax = $"0.85 {yMax}" },
    Text = { Text = "75", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
}, "ServerManagerContent");

container.Add(new CuiButton
{
    Button = { Color = "0.2 0.8 0.2 1", Command = $"sm.rep.set {p.userID} 100" },
    RectTransform = { AnchorMin = $"0.87 {yMin}", AnchorMax = $"0.93 {yMax}" },
    Text = { Text = "100", FontSize = 7, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
}, "ServerManagerContent");

            }

            // Mass Actions
            container.Add(new CuiLabel
            {
                Text = { Text = "MASS ACTIONS", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.08", AnchorMax = "0.95 0.12" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.6 0.2 1", Command = "sm.rep.massreset" },
                RectTransform = { AnchorMin = "0.1 0.02", AnchorMax = "0.3 0.07" },
                Text = { Text = "Reset All to 50", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.6 0.6 1", Command = "sm.rep.refresh" },
                RectTransform = { AnchorMin = "0.4 0.02", AnchorMax = "0.6 0.07" },
                Text = { Text = "Refresh HUDs", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.2 0.6 1", Command = "sm.rep.stats" },
                RectTransform = { AnchorMin = "0.7 0.02", AnchorMax = "0.9 0.07" },
                Text = { Text = "Show Stats", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }
        void OpenReputationConfigTab(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "ServerManagerContent");
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.86" },
                CursorEnabled = true
            }, "ServerManagerMain", "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "REPUTATION CONFIGURATION", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.1 0.9", AnchorMax = "0.9 0.95" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "REPUTATION SYSTEM CONFIGURATION", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.65", AnchorMax = "0.95 0.7" }
            }, "ServerManagerContent");

            // Feature Toggles Section
            container.Add(new CuiLabel
            {
                Text = { Text = "FEATURE TOGGLES", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.55", AnchorMax = "0.95 0.6" }
            }, "ServerManagerContent");

            string[] features = { "HUD Display", "Gather Bonus", "Safe Zone Block", "Parachute Spawn", "Chat Integration" };
            bool[] featureStates = { repConfig.EnableHUD, repConfig.EnableGatherBonus, repConfig.EnableSafeZoneBlocking, repConfig.EnableParachuteSpawn, repConfig.EnableChatIntegration };

            for (int i = 0; i < features.Length; i++)
            {
                int col = i % 3;
                int row = i / 3;
                float xPos = 0.05f + (col * 0.3f);
                float yPos = 0.48f - (row * 0.06f);
                
                string color = featureStates[i] ? "0.2 0.8 0.2 1" : "0.8 0.2 0.2 1";
                string status = featureStates[i] ? "ON" : "OFF";

                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = $"sm.repconfig.toggle {i}" },
                    RectTransform = { AnchorMin = $"{xPos} {yPos}", AnchorMax = $"{xPos + 0.28f} {yPos + 0.05f}" },
                    Text = { Text = $"{features[i]}: {status}", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");
            }

            // Apply/Reset Buttons
            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.8 0.2 1", Command = "sm.repconfig.apply" },
                RectTransform = { AnchorMin = "0.1 0.32", AnchorMax = "0.3 0.38" },
                Text = { Text = "Apply Changes", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.6 0.2 1", Command = "sm.repconfig.reset" },
                RectTransform = { AnchorMin = "0.4 0.32", AnchorMax = "0.6 0.38" },
                Text = { Text = "Reset to Defaults", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.6 0.6 1", Command = "sm.repconfig.reload" },
                RectTransform = { AnchorMin = "0.7 0.32", AnchorMax = "0.9 0.38" },
                Text = { Text = "Reload Config", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }

        void OpenLiveMapTab(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "ServerManagerContent");
            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0.15 0.15 0.15 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.86" },
                CursorEnabled = true
            }, "ServerManagerMain", "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "LIVE MAP MANAGER", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.1 0.9", AnchorMax = "0.9 0.95" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "SETUP INSTRUCTIONS:", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.82", AnchorMax = "0.95 0.87" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "1. Go to RustMaps.com and find your server's map", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.77", AnchorMax = "0.95 0.82" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "2. Save the map image and upload to imgur.com", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.72", AnchorMax = "0.95 0.77" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "3. Copy the direct image URL (ends with .jpg or .png)", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.67", AnchorMax = "0.95 0.72" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "4. Edit oxide/data/ServerManager_MapURL.txt with your URL", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.62", AnchorMax = "0.95 0.67" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "5. Reload the plugin: o.reload ServerManager", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.57", AnchorMax = "0.95 0.62" }
            }, "ServerManagerContent");

            string currentMapUrl = GetLiveMapImagePath();
            string mapStatus = currentMapUrl.Contains("rustmaps.com") ? "Default (Demo)" : "Custom URL Loaded";
            
            container.Add(new CuiLabel
            {
                Text = { Text = $"Current Map: {mapStatus}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.47", AnchorMax = "0.95 0.52" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.6 0.8 1", Command = "sm.livemap.test" },
                RectTransform = { AnchorMin = "0.1 0.4", AnchorMax = "0.45 0.45" },
                Text = { Text = "Test Live Map", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.4 0.2 1", Command = "sm.livemap.reload" },
                RectTransform = { AnchorMin = "0.55 0.4", AnchorMax = "0.9 0.45" },
                Text = { Text = "Reload Map URL", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            // Live Map Statistics
            container.Add(new CuiLabel
            {
                Text = { Text = "LIVE MAP STATISTICS:", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.3", AnchorMax = "0.95 0.35" }
            }, "ServerManagerContent");

            int activeMapUsers = liveMapActiveTimers.Count;
            container.Add(new CuiLabel
            {
                Text = { Text = $"• Active Map Users: {activeMapUsers}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.25", AnchorMax = "0.5 0.3" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = $"• Map Size: {LiveMapSize}m x {LiveMapSize}m", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.2", AnchorMax = "0.5 0.25" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = $"• Update Interval: {LiveMapUpdateInterval}s", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.15", AnchorMax = "0.5 0.2" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = $"• Max Players Shown: {LiveMapMaxPlayers}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.1", AnchorMax = "0.5 0.15" }
            }, "ServerManagerContent");

            // Close all live maps button
            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.2 0.2 1", Command = "sm.livemap.closeall" },
                RectTransform = { AnchorMin = "0.55 0.2", AnchorMax = "0.9 0.25" },
                Text = { Text = "Close All Live Maps", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }

        #endregion
		
		#region Console Commands - General and Environmental

        // General Tab Commands
        [ConsoleCommand("sm.decay.set")]
        void CmdDecaySet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            if (float.TryParse(arg.Args[0], out float newFactor))
            {
                decayFactor = Mathf.Clamp(newFactor, 0.1f, 1f);
                int minutes = (int)(1440 / decayFactor);
                rust.RunServerCommand($"decay.upkeep_period_minutes {minutes}");
                SaveData();
                OpenGeneralTab(player);
                player.ChatMessage($"<color=green>Decay factor set to {decayFactor:F1}</color>");
            }
        }

        [ConsoleCommand("sm.crate.set")]
        void CmdCrateSet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            if (int.TryParse(arg.Args[0], out int minutes))
            {
                crateUnlockTime = Mathf.Clamp(minutes, 1, 15);
                rust.RunServerCommand($"hackablelockedcrate.requiredhackseconds {crateUnlockTime * 60}");
                SaveData();
                OpenGeneralTab(player);
                player.ChatMessage($"<color=green>Crate unlock time set to {crateUnlockTime} minutes</color>");
            }
        }

        [ConsoleCommand("sm.time.set")]
        void CmdTimeSet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            if (float.TryParse(arg.Args[0], out float time))
            {
                timeOfDay = Mathf.Clamp(time, 0, 24);
                rust.RunServerCommand($"env.time {timeOfDay}");
                SaveData();
                OpenGeneralTab(player);
                player.ChatMessage($"<color=green>Time set to {timeOfDay}:00</color>");
            }
        }

        [ConsoleCommand("sm.time.auto")]
        void CmdTimeAuto(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            timeOfDay = -1f;
            SaveData();
            OpenGeneralTab(player);
            player.ChatMessage("<color=green>Time set to automatic</color>");
        }

        // Environmental Commands
        [ConsoleCommand("sm.env.clearweather")]
        void CmdClearWeather(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("weather.rain 0");
            rust.RunServerCommand("weather.fog 0");
            rust.RunServerCommand("weather.wind 0");
            player.ChatMessage("<color=green>Weather cleared</color>");
        }

        [ConsoleCommand("sm.env.rain")]
        void CmdRain(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("weather.rain 1");
            player.ChatMessage("<color=green>Rain started</color>");
        }

        [ConsoleCommand("sm.env.fog")]
        void CmdFog(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("weather.fog 1");
            player.ChatMessage("<color=green>Fog added</color>");
        }

        [ConsoleCommand("sm.env.wind")]
        void CmdWind(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("weather.wind 1");
            player.ChatMessage("<color=green>Wind increased</color>");
        }

        [ConsoleCommand("sm.admin.save")]
        void CmdSave(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("server.save");
            player.ChatMessage("<color=green>Server saved</color>");
        }

        [ConsoleCommand("sm.admin.gc")]
        void CmdGC(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("gc.collect");
            player.ChatMessage("<color=green>Garbage collection triggered</color>");
        }

        #endregion

        #region Console Commands - Kit System

        [ConsoleCommand("sm.kit.additem")]
        void CmdKitAddItem(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 2) return;

            string itemShortname = arg.Args[0];
            if (!int.TryParse(arg.Args[1], out int quantity) || quantity <= 0)
            {
                player.ChatMessage("<color=red>Invalid quantity.</color>");
                return;
            }

            if (!commonItems.ContainsKey(itemShortname))
            {
                player.ChatMessage("<color=red>Invalid item.</color>");
                return;
            }

            if (!customKits.ContainsKey(player.userID))
                customKits[player.userID] = new Dictionary<string, int>();

            if (customKits[player.userID].ContainsKey(itemShortname))
                customKits[player.userID][itemShortname] += quantity;
            else
                customKits[player.userID][itemShortname] = quantity;

            SaveData();
            OpenKitsTab(player);
            player.ChatMessage($"<color=green>Added {quantity}x {commonItems[itemShortname]} to kit</color>");
        }

        [ConsoleCommand("sm.kit.removeitem")]
        void CmdKitRemoveItem(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            string itemShortname = arg.Args[0];
            if (customKits.ContainsKey(player.userID) && customKits[player.userID].ContainsKey(itemShortname))
            {
                customKits[player.userID].Remove(itemShortname);
                SaveData();
                player.ChatMessage($"<color=green>Removed {commonItems[itemShortname]} from kit</color>");
            }
            OpenKitsTab(player);
        }

        [ConsoleCommand("sm.kit.clear")]
        void CmdKitClear(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            if (customKits.ContainsKey(player.userID))
            {
                customKits[player.userID].Clear();
                player.ChatMessage("<color=green>Custom kit cleared.</color>");
                SaveData();
            }
            OpenKitsTab(player);
        }

        [ConsoleCommand("sm.kit.selectrecipient")]
        void CmdKitSelectRecipient(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            OpenSelectRecipientTab(player);
        }

        [ConsoleCommand("sm.kit.give")]
        void CmdKitGive(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            if (!ulong.TryParse(arg.Args[0], out ulong recipientId))
            {
                player.ChatMessage("<color=red>Invalid player ID</color>");
                return;
            }

            if (!customKits.TryGetValue(player.userID, out var kit) || kit.Count == 0)
            {
                player.ChatMessage("<color=red>Your custom kit is empty</color>");
                return;
            }

            BasePlayer recipient = BasePlayer.FindByID(recipientId);
            if (recipient == null)
            {
                player.ChatMessage("<color=red>Player not found or offline</color>");
                return;
            }

            int failedAdds = 0;
            int totalItems = 0;

            foreach (var kvp in kit)
            {
                ItemDefinition def = ItemManager.FindItemDefinition(kvp.Key);
                if (def == null)
                {
                    player.ChatMessage($"<color=red>Unknown item: {kvp.Key}</color>");
                    continue;
                }

                var item = ItemManager.Create(def, kvp.Value);
                if (item == null || !recipient.inventory.GiveItem(item))
                {
                    failedAdds++;
                    item?.Remove();
                }
                else
                {
                    totalItems += kvp.Value;
                }
            }

            if (failedAdds > 0)
                player.ChatMessage($"<color=red>Could not add {failedAdds} item types (inventory full?)</color>");

            player.ChatMessage($"<color=green>Given custom kit ({totalItems} items) to {recipient.displayName}.</color>");
            recipient.ChatMessage("<color=green>You received a custom kit from an admin.</color>");

            OpenKitsTab(player);
        }

        #endregion
		
		#region Console Commands - Events

        [ConsoleCommand("sm.event.setpos")]
        void CmdEventSetPos(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            selectedEventPosition = player.transform.position;
            selectedGridCoordinate = "";
            SaveData();
            player.ChatMessage($"<color=green>Event position set to your location: {selectedEventPosition}</color>");
            OpenEventsTab(player);
        }

        [ConsoleCommand("sm.event.clearpos")]
        void CmdEventClearPos(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            selectedEventPosition = Vector3.zero;
            selectedGridCoordinate = "";
            liveMapSingleMarker = null;
            SaveData();
            player.ChatMessage("<color=green>Event position cleared - will now use your location</color>");
            OpenEventsTab(player);
        }

        [ConsoleCommand("sm.event.openlivemap")]
        void CmdEventOpenLiveMap(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            string mapPath = GetLiveMapImagePath();
            if (mapPath == null)
            {
                player.ChatMessage("<color=red>Map image error - check console for details.</color>");
                return;
            }

            // Set current position as initial marker if we have one
            if (selectedEventPosition != Vector3.zero)
            {
                liveMapSingleMarker = new Vector2(selectedEventPosition.x, selectedEventPosition.z);
            }

            CreateLiveMapUI(player, mapPath);
            
            NextTick(() => UpdateLiveMapDotsAndMarkers(player));

            Timer updateTimer = timer.Every(LiveMapUpdateInterval, () =>
            {
                if (player == null || !player.IsConnected) return;
                UpdateLiveMapDotsAndMarkers(player);
            });
            
            liveMapActiveTimers[player.userID] = updateTimer;
            player.ChatMessage("<color=green>Live map opened - Click to select event location</color>");
        }

        [ConsoleCommand("sm.event.spawn")]
        void CmdEventSpawn(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            string eventType = arg.Args[0];
            Vector3 pos = selectedEventPosition != Vector3.zero ? selectedEventPosition : player.transform.position;

            switch (eventType)
            {
                case "airdrop":
                    rust.RunServerCommand("airdrop.toplayer", player.displayName);
                    timer.Once(1f, () => {
                        if (selectedEventPosition != Vector3.zero)
                        {
                            var airdrops = UnityEngine.Object.FindObjectsOfType<SupplyDrop>();
                            if (airdrops.Length > 0)
                            {
                                var latestAirdrop = airdrops.OrderByDescending(a => a.transform.position.y).FirstOrDefault();
                                if (latestAirdrop != null)
                                {
                                    latestAirdrop.transform.position = new Vector3(pos.x, pos.y + 300f, pos.z);
                                }
                            }
                        }
                    });
                    player.ChatMessage($"<color=green>Airdrop called to {pos}</color>");
                    break;

                case "heli":
                    rust.RunServerCommand("spawn patrolhelicopter");
                    player.ChatMessage("<color=green>Attack helicopter spawned</color>");
                    break;

                case "cargoship":
                    rust.RunServerCommand("spawn cargoship");
                    player.ChatMessage("<color=green>Cargo ship spawned</color>");
                    break;

                case "bradleyapc":
                    rust.RunServerCommand("spawn bradleyapc");
                    player.ChatMessage("<color=green>Bradley APC spawned</color>");
                    break;

                case "ch47":
                    try
                    {
                        var chinook = GameManager.server.CreateEntity("assets/prefabs/npc/ch47/ch47scientists.entity.prefab", pos);
                        if (chinook != null)
                        {
                            chinook.Spawn();
                            player.ChatMessage($"<color=green>Chinook helicopter spawned at {pos}</color>");
                            break;
                        }
                    }
                    catch { }

                    rust.RunServerCommand("spawn ch47scientists.entity");
                    player.ChatMessage("<color=green>Chinook helicopter spawned</color>");
                    break;

                case "supplydrop":
                    var supply = GameManager.server.CreateEntity("assets/prefabs/misc/supply drop/supply_drop.prefab", new Vector3(pos.x, pos.y + 300f, pos.z)) as SupplyDrop;
                    if (supply != null)
                    {
                        supply.Spawn();
                        player.ChatMessage($"<color=green>Supply drop spawned at {pos}</color>");
                    }
                    else
                    {
                        rust.RunServerCommand("spawn supply_drop");
                        player.ChatMessage("<color=green>Supply drop spawned</color>");
                    }
                    break;

                case "oilrig":
                    rust.RunServerCommand("event.run oilrig");
                    player.ChatMessage("<color=green>Oil rig event triggered</color>");
                    break;

                default:
                    player.ChatMessage("<color=red>Unknown event type</color>");
                    break;
            }
        }

        #endregion

        #region Console Commands - Reputation System

        [ConsoleCommand("sm.rep.set")]
        void CmdRepSet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 2) return;

            if (!ulong.TryParse(arg.Args[0], out ulong targetId)) return;
            if (!int.TryParse(arg.Args[1], out int newRep)) return;

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target == null)
            {
                player.ChatMessage("<color=red>Player not found.</color>");
                return;
            }

            bool success = SetPlayerReputation(target, newRep);

            if (success)
            {
                player.ChatMessage($"<color=green>Set {target.displayName}'s reputation to {newRep}.</color>");
                timer.Once(1f, () => OpenReputationTab(player));
            }
            else
            {
                player.ChatMessage("<color=red>Failed to set reputation.</color>");
            }
        }

        [ConsoleCommand("sm.rep.modify")]
        void CmdRepModify(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 2) return;

            if (!ulong.TryParse(arg.Args[0], out ulong targetId)) return;
            if (!int.TryParse(arg.Args[1], out int amount)) return;

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target == null)
            {
                player.ChatMessage("<color=red>Player not found.</color>");
                return;
            }

            EnsureRep(target.userID);
            int currentRep = repData[target.userID];
            int newRep = Mathf.Clamp(currentRep + amount, repConfig.MinReputation, repConfig.MaxReputation);

            bool success = SetPlayerReputation(target, newRep);

            if (success)
            {
                string change = amount > 0 ? $"+{amount}" : amount.ToString();
                player.ChatMessage($"<color=green>{target.displayName}: {currentRep} → {newRep} ({change})</color>");
                timer.Once(1f, () => OpenReputationTab(player));
            }
            else
            {
                player.ChatMessage("<color=red>Failed to modify reputation.</color>");
            }
        }

        [ConsoleCommand("sm.rep.massreset")]
        void CmdRepMassReset(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            int resetCount = 0;
            foreach (var kvp in repData.ToList())
            {
                repData[kvp.Key] = repConfig.DefaultReputation;
                resetCount++;
            }

            SaveData();
            player.ChatMessage($"<color=green>Reset {resetCount} player reputations to {repConfig.DefaultReputation}.</color>");
            timer.Once(1f, () => OpenReputationTab(player));
        }

        [ConsoleCommand("sm.rep.refresh")]
        void CmdRepRefresh(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            RefreshAllPlayersHUD();
            player.ChatMessage("<color=green>All player HUDs refreshed.</color>");
        }

        [ConsoleCommand("sm.rep.stats")]
        void CmdRepStats(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            var stats = GetReputationStats();
            player.ChatMessage("<color=green>=== Reputation Statistics ===</color>");
            player.ChatMessage($"Total Players: <color=yellow>{stats["TotalPlayers"]}</color> | Online: <color=yellow>{stats["OnlineCount"]}</color>");
            player.ChatMessage($"Average Reputation: <color=yellow>{stats["AverageReputation"]}</color>");
            player.ChatMessage($"<color=#ff0000>{repConfig.InfidelTierName}s:</color> {stats["InfidelCount"]} | <color=#ff8000>{repConfig.SinnerTierName}s:</color> {stats["SinnerCount"]}");
            player.ChatMessage($"<color=#ffff00>{repConfig.AverageTierName}:</color> {stats["AverageCount"]} | <color=#ffffff>{repConfig.DiscipleTierName}s:</color> {stats["DiscipleCount"]} | <color=#00ff00>{repConfig.ProphetTierName}s:</color> {stats["ProphetCount"]}");
            if (repConfig.EnableParachuteSpawn)
                player.ChatMessage($"Aerial Spawn Eligible: <color=#00ffff>{stats["ParachuteEligible"]}</color> players ✈");
        }

        #endregion
		
		#region Console Commands - Reputation Config

        [ConsoleCommand("sm.repconfig.toggle")]
        void CmdRepConfigToggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            if (!int.TryParse(arg.Args[0], out int featureIndex)) return;

            bool success = false;
            
            switch (featureIndex)
            {
                case 0: // HUD Display
                    repConfig.EnableHUD = !repConfig.EnableHUD;
                    if (repConfig.EnableHUD)
                    {
                        refreshTimer?.Destroy();
                        refreshTimer = timer.Every(repConfig.UpdateInterval, () => SafeExecute(RefreshAllPlayersHUD));
                        foreach (var p in BasePlayer.activePlayerList)
                        {
                            if (IsValidPlayer(p) && !p.IsSleeping())
                                timer.Once(0.2f, () => CreateOrUpdateHUD(p));
                        }
                    }
                    else
                    {
                        refreshTimer?.Destroy();
                        foreach (var p in BasePlayer.activePlayerList)
                            DestroyHUD(p);
                    }
                    success = true;
                    player.ChatMessage($"<color=green>HUD Display: {(repConfig.EnableHUD ? "ON" : "OFF")}</color>");
                    break;
                case 1: // Gather Bonus
                    repConfig.EnableGatherBonus = !repConfig.EnableGatherBonus;
                    success = true;
                    player.ChatMessage($"<color=green>Gather Bonus: {(repConfig.EnableGatherBonus ? "ON" : "OFF")}</color>");
                    break;
                case 2: // Safe Zone Blocking
                    repConfig.EnableSafeZoneBlocking = !repConfig.EnableSafeZoneBlocking;
                    success = true;
                    player.ChatMessage($"<color=green>Safe Zone Blocking: {(repConfig.EnableSafeZoneBlocking ? "ON" : "OFF")}</color>");
                    break;
                case 3: // Parachute Spawn
                    repConfig.EnableParachuteSpawn = !repConfig.EnableParachuteSpawn;
                    success = true;
                    player.ChatMessage($"<color=green>Parachute Spawn: {(repConfig.EnableParachuteSpawn ? "ON" : "OFF")}</color>");
                    break;
                case 4: // Chat Integration
                    repConfig.EnableChatIntegration = !repConfig.EnableChatIntegration;
                    success = true;
                    player.ChatMessage($"<color=green>Chat Integration: {(repConfig.EnableChatIntegration ? "ON" : "OFF")}</color>");
                    break;
                default:
                    player.ChatMessage("<color=yellow>Unknown feature index</color>");
                    break;
            }

            if (success)
            {
                SaveData();
                timer.Once(0.5f, () => OpenReputationConfigTab(player));
            }
        }

        [ConsoleCommand("sm.repconfig.apply")]
        void CmdRepConfigApply(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            player.ChatMessage("<color=green>Configuration applied successfully!</color>");
            SaveData();
        }

        [ConsoleCommand("sm.repconfig.reset")]
        void CmdRepConfigReset(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            repConfig = new ReputationConfig();
            player.ChatMessage("<color=green>Configuration reset to defaults</color>");
            SaveData();
            OpenReputationConfigTab(player);
        }

        [ConsoleCommand("sm.repconfig.reload")]
        void CmdRepConfigReload(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            LoadData();
            player.ChatMessage("<color=green>Configuration reloaded</color>");
            OpenReputationConfigTab(player);
        }

        #endregion

        #region Console Commands - Live Map

        [ConsoleCommand("sm.livemap.test")]
        void CmdLiveMapTest(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            string mapPath = GetLiveMapImagePath();
            CreateLiveMapUI(player, mapPath);
            
            NextTick(() => UpdateLiveMapDotsAndMarkers(player));

            Timer updateTimer = timer.Every(LiveMapUpdateInterval, () =>
            {
                if (player == null || !player.IsConnected) return;
                UpdateLiveMapDotsAndMarkers(player);
            });
            
            liveMapActiveTimers[player.userID] = updateTimer;
            player.ChatMessage("<color=green>Live map test opened successfully!</color>");
        }

        [ConsoleCommand("sm.livemap.reload")]
        void CmdLiveMapReload(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            LoadLiveMapImageCache();
            player.ChatMessage("<color=green>Map URL configuration reloaded!</color>");
            OpenLiveMapTab(player);
        }

        [ConsoleCommand("sm.livemap.closeall")]
        void CmdLiveMapCloseAll(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            int closedMaps = 0;
            foreach (var kvp in liveMapActiveTimers.ToList())
            {
                BasePlayer mapPlayer = BasePlayer.FindByID(kvp.Key);
                if (mapPlayer != null)
                {
                    CloseLiveMapView(mapPlayer);
                    closedMaps++;
                }
            }

            player.ChatMessage($"<color=green>Closed {closedMaps} active live maps.</color>");
            OpenLiveMapTab(player);
        }

        #endregion

        #region Data Management

        void LoadData()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, object>>("ServerManager");
                
                if (data.TryGetValue("decayFactor", out var decay))
                    float.TryParse(decay.ToString(), out decayFactor);
                
                if (data.TryGetValue("crateUnlockTime", out var crate))
                    int.TryParse(crate.ToString(), out crateUnlockTime);
                
                if (data.TryGetValue("timeOfDay", out var time))
                    float.TryParse(time.ToString(), out timeOfDay);
                
                if (data.TryGetValue("selectedEventPosition", out var pos))
                {
                    var posData = pos as Dictionary<string, object>;
                    if (posData != null)
                    {
                        selectedEventPosition = new Vector3(
                            Convert.ToSingle(posData["x"]),
                            Convert.ToSingle(posData["y"]),
                            Convert.ToSingle(posData["z"])
                        );
                    }
                }

                if (data.TryGetValue("customKits", out var kits))
                {
                    var kitsData = kits as Dictionary<string, object>;
                    if (kitsData != null)
                    {
                        customKits = new Dictionary<ulong, Dictionary<string, int>>();
                        foreach (var kvp in kitsData)
                        {
                            if (ulong.TryParse(kvp.Key, out ulong userId))
                            {
                                var kitItems = kvp.Value as Dictionary<string, object>;
                                if (kitItems != null)
                                {
                                    customKits[userId] = new Dictionary<string, int>();
                                    foreach (var item in kitItems)
                                    {
                                        if (int.TryParse(item.Value.ToString(), out int amount))
                                        {
                                            customKits[userId][item.Key] = amount;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Load integrated reputation data
                if (data.TryGetValue("repData", out var repDataObj))
                {
                    var repDataDict = repDataObj as Dictionary<string, object>;
                    if (repDataDict != null)
                    {
                        repData = new Dictionary<ulong, int>();
                        foreach (var kvp in repDataDict)
                        {
                            if (ulong.TryParse(kvp.Key, out ulong userId) && int.TryParse(kvp.Value.ToString(), out int rep))
                            {
                                repData[userId] = rep;
                            }
                        }
                    }
                }

                // Load reputation config
                if (data.TryGetValue("repConfig", out var repConfigObj))
                {
                    var configData = repConfigObj as Dictionary<string, object>;
                    if (configData != null)
                    {
                        if (configData.TryGetValue("EnableHUD", out var enableHUD))
                            bool.TryParse(enableHUD.ToString(), out repConfig.EnableHUD);
                        if (configData.TryGetValue("EnableGatherBonus", out var enableGather))
                            bool.TryParse(enableGather.ToString(), out repConfig.EnableGatherBonus);
                        if (configData.TryGetValue("EnableSafeZoneBlocking", out var enableSafeZone))
                            bool.TryParse(enableSafeZone.ToString(), out repConfig.EnableSafeZoneBlocking);
                        if (configData.TryGetValue("EnableParachuteSpawn", out var enableParachute))
                            bool.TryParse(enableParachute.ToString(), out repConfig.EnableParachuteSpawn);
                        if (configData.TryGetValue("EnableChatIntegration", out var enableChat))
                            bool.TryParse(enableChat.ToString(), out repConfig.EnableChatIntegration);
                    }
                }

                PrintWarning("[ServerManager] COMPLETE INTEGRATION loaded successfully - ALL FEATURES WORKING!");
            }
            catch (Exception ex)
            {
                PrintWarning($"[ServerManager] Failed to load data: {ex.Message}");
            }
        }

        void SaveData()
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["decayFactor"] = decayFactor,
                    ["crateUnlockTime"] = crateUnlockTime,
                    ["timeOfDay"] = timeOfDay,
                    ["selectedEventPosition"] = new Dictionary<string, object>
                    {
                        ["x"] = selectedEventPosition.x,
                        ["y"] = selectedEventPosition.y,
                        ["z"] = selectedEventPosition.z
                    },
                    ["customKits"] = customKits.ToDictionary(
                        kvp => kvp.Key.ToString(),
                        kvp => kvp.Value as object
                    ),
                    ["repData"] = repData.ToDictionary(
                        kvp => kvp.Key.ToString(),
                        kvp => kvp.Value as object
                    ),
                    ["repConfig"] = new Dictionary<string, object>
                    {
                        ["EnableHUD"] = repConfig.EnableHUD,
                        ["EnableGatherBonus"] = repConfig.EnableGatherBonus,
                        ["EnableSafeZoneBlocking"] = repConfig.EnableSafeZoneBlocking,
                        ["EnableParachuteSpawn"] = repConfig.EnableParachuteSpawn,
                        ["EnableChatIntegration"] = repConfig.EnableChatIntegration
                    }
                };

                Interface.Oxide.DataFileSystem.WriteObject("ServerManager", data);
                PrintWarning("[ServerManager] COMPLETE INTEGRATION saved successfully!");
            }
            catch (Exception ex)
            {
                PrintError($"[ServerManager] Failed to save data: {ex.Message}");
            }
        }

        #endregion

        #region Admin Commands

        [ChatCommand("smreload")]
        void CmdReload(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player))
            {
                player.ChatMessage("<color=red>No permission.</color>");
                return;
            }

            SaveData();
            LoadData();
            player.ChatMessage("<color=green>[ServerManager] COMPLETE INTEGRATION reloaded successfully!</color>");
        }

        [ChatCommand("smstatus")]
        void CmdStatus(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player))
            {
                player.ChatMessage("<color=red>No permission.</color>");
                return;
            }

            player.ChatMessage("<color=green>=== ServerManager Complete Status ===</color>");
            player.ChatMessage($"<color=yellow>Decay Factor:</color> {decayFactor}");
            player.ChatMessage($"<color=yellow>Crate Unlock:</color> {crateUnlockTime} minutes");
            player.ChatMessage($"<color=yellow>Time Setting:</color> {(timeOfDay < 0 ? "Auto" : $"{timeOfDay}:00")}");
            player.ChatMessage($"<color=yellow>Custom Kits:</color> {customKits.Count} players");
            player.ChatMessage($"<color=yellow>Reputation Players:</color> {repData.Count}");
            player.ChatMessage($"<color=yellow>HUD System:</color> {(repConfig.EnableHUD ? "Enabled" : "Disabled")}");
            player.ChatMessage($"<color=yellow>Gather Bonus:</color> {(repConfig.EnableGatherBonus ? "Enabled" : "Disabled")}");
            player.ChatMessage($"<color=yellow>Safe Zone Block:</color> {(repConfig.EnableSafeZoneBlocking ? "Enabled" : "Disabled")}");
            player.ChatMessage($"<color=yellow>Parachute Spawn:</color> {(repConfig.EnableParachuteSpawn ? "Enabled" : "Disabled")}");
            player.ChatMessage($"<color=yellow>Active Live Maps:</color> {liveMapActiveTimers.Count}");
            player.ChatMessage("<color=green>✅ COMPLETE INTEGRATION - ALL FEATURES WORKING!</color>");
        }

        [ChatCommand("smreset")]
        void CmdReset(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player))
            {
                player.ChatMessage("<color=red>No permission.</color>");
                return;
            }

            if (args.Length > 0 && args[0] == "confirm")
            {
                // Reset all settings to defaults
                decayFactor = 1f;
                crateUnlockTime = 15;
                timeOfDay = -1f;
                selectedEventPosition = Vector3.zero;
                customKits.Clear();
                repData.Clear();
                repConfig = new ReputationConfig();

                SaveData();
                player.ChatMessage("<color=green>[ServerManager] All settings reset to defaults!</color>");
            }
            else
            {
                player.ChatMessage("<color=yellow>[ServerManager] Use '/smreset confirm' to reset all settings to defaults.</color>");
            }
        }

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Oxide.Core;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("ReputationSystemHUD", "YourName", "5.0.0")]
    [Description("Complete reputation system with configurable tiers, safe zone blocking, and parachute spawn.")]
    public class ReputationSystemHUD : RustPlugin
    {
        private PluginConfig config;
        private Dictionary<ulong, int> repData;
        private Timer refreshTimer;
        private Timer hourlyTimer;
        private Timer punishmentTimer;

        // Configuration system with toggle and value support
        public class SystemToggle<T>
        {
            public bool Enabled { get; set; }
            public T Value { get; set; }
            
            public SystemToggle(bool enabled, T value)
            {
                Enabled = enabled;
                Value = value;
            }
        }

        public class PluginConfig
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
            
            // Reputation Tier Settings
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
            public SystemToggle<int> HourlyRepGainLow = new SystemToggle<int>(true, 4);
            public SystemToggle<int> HourlyRepGainHigh = new SystemToggle<int>(true, 2);
            public int HourlyRepGainThreshold = 35;
            
            // NPC Kill Penalties
            public SystemToggle<int> NPCKillPenalty = new SystemToggle<int>(true, -1);
            
            // PvP Reputation Changes
            public SystemToggle<int> PvPKillInfidelReward = new SystemToggle<int>(true, 10);
            public SystemToggle<int> PvPKillSinnerReward = new SystemToggle<int>(true, 5);
            public SystemToggle<int> PvPKillAverageReward = new SystemToggle<int>(true, -2);
            public SystemToggle<int> PvPKillDiscipleReward = new SystemToggle<int>(true, -8);
            public SystemToggle<int> PvPKillProphetReward = new SystemToggle<int>(true, -15);
            
            // Safe Zone Blocking System
            public SystemToggle<float> SafeZonePushForce = new SystemToggle<float>(true, 2.0f);
            public SystemToggle<float> SafeZoneCheckInterval = new SystemToggle<float>(true, 5.0f);
            public int SafeZoneBlockingMaxRep = 25;
            
            // Hunger/Thirst Drain System
            public SystemToggle<float> HungerDrainMultiplier = new SystemToggle<float>(true, 2.0f);
            public SystemToggle<float> ThirstDrainMultiplier = new SystemToggle<float>(true, 2.0f);
            public SystemToggle<float> HungerThirstCheckInterval = new SystemToggle<float>(true, 10.0f);
            public int HungerThirstPenaltyMaxRep = 25;
            
            // Gather Bonus/Penalty System
            public SystemToggle<float> InfidelGatherMultiplier = new SystemToggle<float>(true, 0.7f);
            public SystemToggle<float> SinnerGatherMultiplier = new SystemToggle<float>(true, 0.9f);
            public SystemToggle<float> AverageGatherMultiplier = new SystemToggle<float>(true, 1.0f);
            public SystemToggle<float> DiscipleGatherMultiplier = new SystemToggle<float>(true, 1.1f);
            public SystemToggle<float> ProphetGatherMultiplier = new SystemToggle<float>(true, 1.25f);
            
            // Parachute Spawn System
            public SystemToggle<float> ParachuteSpawnHeight = new SystemToggle<float>(true, 500f);
            public SystemToggle<bool> ParachuteOnlyForProphets = new SystemToggle<bool>(true, true);
            public SystemToggle<bool> ParachuteAutoEquip = new SystemToggle<bool>(true, true);
            public SystemToggle<float> ParachuteSpawnRadius = new SystemToggle<float>(true, 50f);
            public SystemToggle<bool> ParachuteForceSpawn = new SystemToggle<bool>(true, false);
            public SystemToggle<bool> ParachuteGiveItems = new SystemToggle<bool>(true, true);
            
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
            public SystemToggle<float> SafeZoneMessageFrequency = new SystemToggle<float>(true, 0.25f); // 25% chance
            public SystemToggle<float> HungerThirstMessageFrequency = new SystemToggle<float>(true, 0.1f); // 10% chance
            public SystemToggle<float> GatherBonusMessageFrequency = new SystemToggle<float>(true, 0.03f); // 3% chance
        }

        protected override void LoadDefaultConfig()
        {
            config = new PluginConfig();
            Config.WriteObject(config, true);
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<PluginConfig>();
                if (config == null) throw new Exception();
            }
            catch
            {
                LoadDefaultConfig();
            }
        }

        private void Init()
        {
            LoadConfig();
            repData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, int>>(Name) ?? new Dictionary<ulong, int>();
            permission.RegisterPermission("reputationsystemhud.admin", this);
        }private void OnServerInitialized()
        {
            try
            {
                if (config.UpdateInterval > 0 && config.EnableHUD)
                    refreshTimer = timer.Every(config.UpdateInterval, () => SafeExecute(RefreshAllPlayersHUD));

                if (config.HourlyRepGainLow.Enabled || config.HourlyRepGainHigh.Enabled)
                    hourlyTimer = timer.Every(3600f, () => SafeExecute(AwardHourlyReputation));

                if (config.EnableSafeZoneBlocking || config.EnableContinuousHungerThirstPenalty)
                    punishmentTimer = timer.Every(config.SafeZoneCheckInterval.Value, () => SafeExecute(CheckAllPlayersForPunishments));

                foreach (var player in BasePlayer.activePlayerList.ToList())
                {
                    SafeExecute(() => {
                        if (IsValidPlayer(player))
                        {
                            EnsureRep(player.userID);
                            
                            if (config.EnableHUD)
                                timer.Once(3f, () => SafeExecute(() => { if (IsValidPlayer(player) && !player.IsSleeping()) CreateOrUpdateHUD(player); }));
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                PrintError($"OnServerInitialized error: {ex.Message}");
            }
        }

        private void Unload()
        {
            try
            {
                // Clean up parachutes
                DestroyAll<SimpleParachuteEntity>();
                
                foreach (var player in BasePlayer.activePlayerList.ToList())
                {
                    SafeExecute(() => {
                        if (IsValidPlayer(player) && config?.EnableHUD == true)
                            DestroyHUD(player);
                    });
                }
                
                refreshTimer?.Destroy();
                hourlyTimer?.Destroy();
                punishmentTimer?.Destroy();
                
                SaveReputationData();
            }
            catch (Exception ex)
            {
                PrintError($"Unload error: {ex.Message}");
            }
        }

        private static void DestroyAll<T>()
        {
            var objects = GameObject.FindObjectsOfType(typeof(T));
            if (objects != null)
                foreach (var gameObj in objects)
                    GameObject.Destroy(gameObj);
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

        private void OnServerSave() => SafeExecute(SaveReputationData);

        private void OnPlayerConnected(BasePlayer player)
        {
            SafeExecute(() => {
                if (IsValidPlayer(player))
                {
                    EnsureRep(player.userID);
                    
                    if (config?.EnableHUD == true)
                        timer.Once(4f, () => SafeExecute(() => { if (IsValidPlayer(player) && !player.IsSleeping()) CreateOrUpdateHUD(player); }));
                }
            });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            SafeExecute(() => {
                if (player != null && config?.EnableHUD == true)
                    DestroyHUD(player);
            });
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            SafeExecute(() => {
                if (IsValidPlayer(player) && config?.EnableHUD == true)
                    timer.Once(2f, () => SafeExecute(() => { if (IsValidPlayer(player) && !player.IsSleeping()) CreateOrUpdateHUD(player); }));
            });
        }

        private void OnPlayerSleep(BasePlayer player)
        {
            SafeExecute(() => {
                if (player != null && config?.EnableHUD == true) 
                    DestroyHUD(player);
            });
        }

        private void EnsureRep(ulong userID)
        {
            if (repData != null && !repData.ContainsKey(userID))
                repData[userID] = config?.DefaultReputation ?? 50;
        }

        private string GetTierName(int rep)
        {
            if (config == null) return "Average";
            if (rep <= config.InfidelMaxRep) return config.InfidelTierName;
            if (rep <= config.SinnerMaxRep) return config.SinnerTierName;
            if (rep <= config.AverageMaxRep) return config.AverageTierName;
            if (rep <= config.DiscipleMaxRep) return config.DiscipleTierName;
            return config.ProphetTierName;
        }

        private string GetTierColor(int rep)
        {
            if (config == null) return "#ffff00";
            if (rep <= config.InfidelMaxRep) return config.InfidelTierColor;
            if (rep <= config.SinnerMaxRep) return config.SinnerTierColor;
            if (rep <= config.AverageMaxRep) return config.AverageTierColor;
            if (rep <= config.DiscipleMaxRep) return config.DiscipleTierColor;
            return config.ProphetTierColor;
        }

        private float GetGatherMultiplier(int reputation)
        {
            if (config == null) return 1.0f;
            string tier = GetTierName(reputation);
            
            if (tier == config.InfidelTierName && config.InfidelGatherMultiplier.Enabled) 
                return config.InfidelGatherMultiplier.Value;
            if (tier == config.SinnerTierName && config.SinnerGatherMultiplier.Enabled) 
                return config.SinnerGatherMultiplier.Value;
            if (tier == config.AverageTierName && config.AverageGatherMultiplier.Enabled) 
                return config.AverageGatherMultiplier.Value;
            if (tier == config.DiscipleTierName && config.DiscipleGatherMultiplier.Enabled) 
                return config.DiscipleGatherMultiplier.Value;
            if (tier == config.ProphetTierName && config.ProphetGatherMultiplier.Enabled) 
                return config.ProphetGatherMultiplier.Value;
            
            return 1.0f;
        }

        private bool CanUseParachute(int reputation)
        {
            if (config?.EnableParachuteSpawn != true) return false;
            
            if (config.ParachuteOnlyForProphets.Enabled && config.ParachuteOnlyForProphets.Value)
                return GetTierName(reputation) == config.ProphetTierName;
            
            return true;
        }

        private int GetPvPRepChange(string victimTier)
        {
            if (config == null) return 0;
            
            if (victimTier == config.InfidelTierName && config.PvPKillInfidelReward.Enabled)
                return config.PvPKillInfidelReward.Value;
            if (victimTier == config.SinnerTierName && config.PvPKillSinnerReward.Enabled)
                return config.PvPKillSinnerReward.Value;
            if (victimTier == config.AverageTierName && config.PvPKillAverageReward.Enabled)
                return config.PvPKillAverageReward.Value;
            if (victimTier == config.DiscipleTierName && config.PvPKillDiscipleReward.Enabled)
                return config.PvPKillDiscipleReward.Value;
            if (victimTier == config.ProphetTierName && config.PvPKillProphetReward.Enabled)
                return config.PvPKillProphetReward.Value;
            
            return 0;
        }private void CheckAllPlayersForPunishments()
        {
            if (!config.EnableSafeZoneBlocking && !config.EnableContinuousHungerThirstPenalty) return;
            
            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                SafeExecute(() => {
                    if (!IsValidPlayer(player) || player.IsDead()) return;
                    
                    EnsureRep(player.userID);
                    
                    if (config.EnableSafeZoneBlocking && repData[player.userID] <= config.SafeZoneBlockingMaxRep)
                        CheckSafeZoneBlocking(player);
                    
                    if (config.EnableContinuousHungerThirstPenalty && repData[player.userID] <= config.HungerThirstPenaltyMaxRep)
                        ApplyHungerThirstPenalty(player);
                });
            }
        }

        private void CheckSafeZoneBlocking(BasePlayer player)
        {
            if (!config.SafeZonePushForce.Enabled) return;
            
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
                    if (config.SafeZoneMessageFrequency.Enabled && 
                        UnityEngine.Random.Range(0f, 1f) < config.SafeZoneMessageFrequency.Value)
                    {
                        SendReply(player, config.SafeZoneBlockMessage);
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
            if (!config.HungerDrainMultiplier.Enabled && !config.ThirstDrainMultiplier.Enabled) return;
            
            try
            {
                if (player.metabolism == null) return;
                
                bool messageShown = false;
                
                if (config.HungerDrainMultiplier.Enabled && player.metabolism.calories != null)
                {
                    float currentHunger = player.metabolism.calories.value;
                    float drainAmount = (100f / 3600f) * config.HungerThirstCheckInterval.Value * (config.HungerDrainMultiplier.Value - 1f);
                    player.metabolism.calories.value = Math.Max(currentHunger - drainAmount, 0f);
                    messageShown = true;
                }
                
                if (config.ThirstDrainMultiplier.Enabled && player.metabolism.hydration != null)
                {
                    float currentThirst = player.metabolism.hydration.value;
                    float drainAmount = (100f / 3600f) * config.HungerThirstCheckInterval.Value * (config.ThirstDrainMultiplier.Value - 1f);
                    player.metabolism.hydration.value = Math.Max(currentThirst - drainAmount, 0f);
                    messageShown = true;
                }
                
                // Show message occasionally
                if (messageShown && config.HungerThirstMessageFrequency.Enabled && 
                    UnityEngine.Random.Range(0f, 1f) < config.HungerThirstMessageFrequency.Value)
                {
                    SendReply(player, config.HungerThirstPenaltyMessage);
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
                    
                    if (current < config.HourlyRepGainThreshold && config.HourlyRepGainLow.Enabled)
                        gain = config.HourlyRepGainLow.Value;
                    else if (config.HourlyRepGainHigh.Enabled)
                        gain = config.HourlyRepGainHigh.Value;
                    
                    if (gain > 0)
                    {
                        int oldRep = repData[player.userID];
                        int minRep = config?.MinReputation ?? 0;
                        int maxRep = config?.MaxReputation ?? 100;
                        repData[player.userID] = Mathf.Clamp(current + gain, minRep, maxRep);
                        
                        SendReply(player, $"<color=#00ff00>+{gain} Reputation</color> (Hourly bonus)");
                    }
                });
            }
            SaveReputationData();
        }

        private void OnPlayerRespawn(BasePlayer player)
        {
            if (config?.EnableParachuteSpawn != true || !IsValidPlayer(player)) return;
            
            SafeExecute(() => {
                EnsureRep(player.userID);
                
                if (CanUseParachute(repData[player.userID]))
                {
                    if (config.ParachuteForceSpawn.Enabled && config.ParachuteForceSpawn.Value)
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
                    Text = { Text = $"{config.ProphetTierName.ToUpper()} AERIAL SPAWN", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
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
                
                float spawnRadius = config.ParachuteSpawnRadius.Enabled ? config.ParachuteSpawnRadius.Value : 50f;
                float spawnHeight = config.ParachuteSpawnHeight.Enabled ? config.ParachuteSpawnHeight.Value : 500f;
                
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
                if (config.ParachuteGiveItems.Enabled && config.ParachuteGiveItems.Value)
                {
                    timer.Once(1f, () => SafeExecute(() => GiveParachuteItems(player)));
                }

                string message = string.Format(config.ParachuteSpawnMessage, spawnHeight);
                player.ChatMessage(message);
            }
            catch (Exception ex)
            {
                PrintError($"ExecuteParachuteSpawn error for {player?.displayName}: {ex.Message}");
            }
        }

        private void GiveParachuteItems(BasePlayer player)
        {
            if (!IsValidPlayer(player) || config.ParachuteStarterItems == null) return;
            
            try
            {
                foreach (var item in config.ParachuteStarterItems)
                {
                    var newItem = ItemManager.CreateByName(item.Key, item.Value);
                    if (newItem != null && player.inventory?.GiveItem(newItem) != true)
                    {
                        newItem.Drop(player.transform.position + Vector3.up * 1f, Vector3.zero);
                    }
                }
                player.ChatMessage(config.ParachuteSurvivalKitMessage);
            }
            catch (Exception ex)
            {
                PrintError($"GiveParachuteItems error for {player?.displayName}: {ex.Message}");
            }
        }private void CreateParachuteEntity(BasePlayer player)
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
                
                player.ChatMessage(config.ParachuteDeployMessage);
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
                            inputVector += Vector3.left;
                            hasInput = true;
                        }
                        if (player.serverInput.IsDown(BUTTON.RIGHT))
                        {
                            inputVector += Vector3.right;
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
                            
                            Vector3 worldMovement = (playerForward * inputVector.z + playerRight * inputVector.x).normalized;
                            
                            float forceMultiplier = 50f;
                            if (player.serverInput.IsDown(BUTTON.SPRINT))
                                forceMultiplier = 80f;
                                
                            myRigidbody.AddForce(worldMovement * forceMultiplier, ForceMode.Acceleration);
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

                worldItem.SendNetworkUpdateImmediate();
                player.SendNetworkUpdateImmediate(false);
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

        // Hook to handle dismounting
        private object CanDismountEntity(BasePlayer player, BaseMountable entity)
        {
            if (player == null || entity == null) return null;
            var isParachuting = entity.GetComponentInParent<SimpleParachuteEntity>();
            if (isParachuting != null && !isParachuting.wantsDismount) return false;
            return null;
        }

        // ===== EVENT HANDLERS =====
        
        private void OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            SafeExecute(() => {
                if (victim != null && config?.EnableHUD == true) 
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
                        int minRep = config?.MinReputation ?? 0;
                        int maxRep = config?.MaxReputation ?? 100;
                        repData[killer.userID] = Mathf.Clamp(oldRep + change, minRep, maxRep);
                        
                        string changeColor = change > 0 ? "#00ff00" : "#ff0000";
                        string changeText = change > 0 ? $"+{change}" : change.ToString();
                        SendReply(killer, $"<color={changeColor}>{changeText} Reputation</color> for killing a {victimTier}");
                        
                        SaveReputationData();
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
                    if (!IsValidPlayer(player) || !config.NPCKillPenalty.Enabled) return;
                    
                    EnsureRep(player.userID);
                    
                    int oldRep = repData[player.userID];
                    int penalty = config.NPCKillPenalty.Value;
                    int minRep = config?.MinReputation ?? 0;
                    int maxRep = config?.MaxReputation ?? 100;
                    repData[player.userID] = Mathf.Clamp(oldRep + penalty, minRep, maxRep);
                    
                    SendReply(player, $"<color=#ff0000>{penalty} Reputation</color> for killing an NPC");
                    SaveReputationData();
                }
            });
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item)
        {
            if (config?.EnableGatherBonus != true) return;
            
            SafeExecute(() => {
                var player = entity?.ToPlayer();
                if (!IsValidPlayer(player)) return;
                
                EnsureRep(player.userID);
                float multiplier = GetGatherMultiplier(repData[player.userID]);

                if (multiplier != 1f && item != null)
                {
                    item.amount = Mathf.CeilToInt(item.amount * multiplier);
                    
                    if (config.GatherBonusMessageFrequency.Enabled && 
                        UnityEngine.Random.Range(0f, 1f) < config.GatherBonusMessageFrequency.Value)
                    {
                        string tierName = GetTierName(repData[player.userID]);
                        if (multiplier > 1f)
                            SendReply(player, $"<color=#00ff00>Gather bonus active!</color> ({tierName})");
                        else if (multiplier < 1f)
                            SendReply(player, $"<color=#ff8000>Gather penalty active.</color> ({tierName})");
                    }
                }
            });
        }// ===== HUD SYSTEM =====
        
        private void CreateOrUpdateHUD(BasePlayer player)
        {
            if (config?.EnableHUD != true || !IsValidPlayer(player) || player.IsSleeping()) return;
            
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
                int lineCount = 1 + Math.Min(nearPlayers.Count, config?.MaxPlayersShown ?? 4);

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
                if (config?.EnableParachuteSpawn == true && CanUseParachute(selfRep))
                {
                    statusInfo += " ✈";
                }
                
                AddTextElement(ref elements, panelName, $"You: {selfRep} ({selfTier}){statusInfo}", selfColor, 0, lineCount);

                for (int i = 0; i < nearPlayers.Count && i < (config?.MaxPlayersShown ?? 4); i++)
                {
                    var other = nearPlayers[i];
                    if (!IsValidPlayer(other)) continue;
                    
                    EnsureRep(other.userID);
                    int rep = repData[other.userID];
                    string tier = GetTierName(rep);
                    string color = GetTierColor(rep);
                    float distance = Vector3.Distance(player.transform.position, other.transform.position);
                    
                    string otherStatusInfo = "";
                    if (config?.EnableParachuteSpawn == true && CanUseParachute(rep))
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
                    if (distance <= (config?.MaxDistance ?? 30f))
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

        // ===== CHAT INTEGRATION =====
        
        private object OnPlayerChat(BasePlayer player, string message, ConVar.Chat.ChatChannel channel)
        {
            if (!config.EnableChatIntegration || player == null || string.IsNullOrEmpty(message) || message.StartsWith("/")) return null;
            
            SafeExecute(() => {
                EnsureRep(player.userID);
                string tier = GetTierName(repData[player.userID]);
                string color = GetTierColor(repData[player.userID]);
                
                string statusInfo = "";
                if (config?.EnableParachuteSpawn == true && CanUseParachute(repData[player.userID]))
                {
                    statusInfo += " ✈";
                }
                
                string formattedMessage = $"[<color={color}>{tier}</color>] <color={color}>{player.displayName}</color>{statusInfo}: {message}";
                
                rust.BroadcastChat(null, formattedMessage, player.UserIDString);
                
                Puts($"[CHAT] [{tier}] {player.displayName}: {message}");
            });
            
            return false;
        }

        // ===== CHAT COMMANDS =====
        
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
                if (config?.EnableParachuteSpawn == true && CanUseParachute(rep))
                {
                    statusInfo += " | Aerial Spawn: Available ✈";
                }
                
                SendReply(player, $"Your reputation: <color={color}>{rep} ({tier})</color>{statusInfo}");
            });
        }

        [ChatCommand("repset")]
        private void RepSetCommand(BasePlayer player, string command, string[] args)
        {
            SafeExecute(() => {
                if (!IsValidPlayer(player)) return;
                
                if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, "reputationsystemhud.admin"))
                {
                    SendReply(player, "<color=#ff0000>No permission.</color>");
                    return;
                }
                
                if (args.Length < 2)
                {
                    SendReply(player, "Usage: /repset <playername> <amount>");
                    return;
                }
                
                BasePlayer target = BasePlayer.Find(args[0]);
                if (target == null)
                {
                    SendReply(player, "<color=#ff0000>Player not found.</color>");
                    return;
                }
                
                if (!int.TryParse(args[1], out int amount))
                {
                    SendReply(player, "<color=#ff0000>Invalid amount.</color>");
                    return;
                }
                
                bool success = SetPlayerReputation(target.userID, amount);
                
                if (success)
                {
                    SendReply(player, $"Set <color=#00ff00>{target.displayName}</color>'s reputation to <color=#00ff00>{amount}</color>.");
                    SendReply(target, $"Your reputation has been set to <color=#00ff00>{amount}</color> by an admin.");
                }
                else
                {
                    SendReply(player, "<color=#ff0000>Failed to set reputation.</color>");
                }
            });
        }

        [ChatCommand("replist")]
        private void RepListCommand(BasePlayer player, string command, string[] args)
        {
            SafeExecute(() => {
                if (!IsValidPlayer(player)) return;
                
                SendReply(player, "<color=#00ff00>=== Online Player Reputations ===</color>");
                var players = BasePlayer.activePlayerList.OrderByDescending(p => GetPlayerReputation(p.userID)).ToList();
                
                for (int i = 0; i < players.Count && i < 15; i++)
                {
                    var p = players[i];
                    if (p == null) continue;
                    
                    EnsureRep(p.userID);
                    int rep = repData[p.userID];
                    string tier = GetTierName(rep);
                    string color = GetTierColor(rep);
                    
                    string statusInfo = "";
                    if (config?.EnableParachuteSpawn == true && CanUseParachute(rep))
                    {
                        statusInfo += " ✈";
                    }
                    
                    SendReply(player, $"<color={color}>{p.displayName}: {rep} ({tier})</color>{statusInfo}");
                }
                
                if (players.Count > 15)
                    SendReply(player, $"<color=#888888>... and {players.Count - 15} more players</color>");
            });
        }

        [ChatCommand("repstats")]
        private void RepStatsCommand(BasePlayer player, string command, string[] args)
        {
            SafeExecute(() => {
                if (!IsValidPlayer(player)) return;
                
                var stats = GetReputationStats();
                SendReply(player, "<color=#00ff00>=== Server Reputation Statistics ===</color>");
                SendReply(player, $"Total Players: <color=#ffff00>{stats["TotalPlayers"]}</color> | Online: <color=#ffff00>{stats["OnlineCount"]}</color>");
                SendReply(player, $"Average Reputation: <color=#ffff00>{stats["AverageReputation"]}</color>");
                SendReply(player, $"<color=#ff0000>{config.InfidelTierName}s:</color> {stats["InfidelCount"]} | <color=#ff8000>{config.SinnerTierName}s:</color> {stats["SinnerCount"]}");
                SendReply(player, $"<color=#ffff00>{config.AverageTierName}:</color> {stats["AverageCount"]} | <color=#ffffff>{config.DiscipleTierName}s:</color> {stats["DiscipleCount"]} | <color=#00ff00>{config.ProphetTierName}s:</color> {stats["ProphetCount"]}");
                if (config?.EnableParachuteSpawn == true)
                    SendReply(player, $"Aerial Spawn Eligible: <color=#00ffff>{stats["ParachuteEligible"]}</color> players ✈");
            });
        }// ===== PUBLIC API FOR SERVERMANAGER =====
        
        public int GetPlayerReputation(ulong userID)
        {
            try
            {
                EnsureRep(userID);
                return repData?.ContainsKey(userID) == true ? repData[userID] : (config?.DefaultReputation ?? 50);
            }
            catch
            {
                return config?.DefaultReputation ?? 50;
            }
        }

        public bool SetPlayerReputation(ulong userID, int reputation)
        {
            try
            {
                EnsureRep(userID);
                repData[userID] = Mathf.Clamp(reputation, config?.MinReputation ?? 0, config?.MaxReputation ?? 100);
                SaveReputationData();
                
                var player = BasePlayer.FindByID(userID);
                if (IsValidPlayer(player) && config?.EnableHUD == true)
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
            if (config?.EnableHUD != true) return;
            
            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                SafeExecute(() => {
                    if (IsValidPlayer(player) && !player.IsSleeping())
                        CreateOrUpdateHUD(player);
                });
            }
        }

        public PluginConfig GetConfig()
        {
            return config;
        }

        public bool UpdateConfig(PluginConfig newConfig)
        {
            try
            {
                var oldConfig = config;
                config = newConfig;
                Config.WriteObject(config, true);
                
                foreach (var player in BasePlayer.activePlayerList.ToList())
                {
                    SafeExecute(() => {
                        if (!IsValidPlayer(player)) return;
                        
                        if (config.EnableHUD != oldConfig.EnableHUD)
                        {
                            if (config.EnableHUD)
                                timer.Once(0.2f, () => SafeExecute(() => CreateOrUpdateHUD(player)));
                            else
                                DestroyHUD(player);
                        }
                    });
                }
                
                if (config.UpdateInterval != oldConfig.UpdateInterval)
                {
                    refreshTimer?.Destroy();
                    if (config.UpdateInterval > 0 && config.EnableHUD)
                        refreshTimer = timer.Every(config.UpdateInterval, () => SafeExecute(RefreshAllPlayersHUD));
                }
                
                if (config.SafeZoneCheckInterval.Value != oldConfig.SafeZoneCheckInterval.Value)
                {
                    punishmentTimer?.Destroy();
                    if (config.EnableSafeZoneBlocking || config.EnableContinuousHungerThirstPenalty)
                        punishmentTimer = timer.Every(config.SafeZoneCheckInterval.Value, () => SafeExecute(CheckAllPlayersForPunishments));
                }
                
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"UpdateConfig failed: {ex.Message}");
                return false;
            }
        }

        public bool ToggleFeature(string featureName, bool enabled)
        {
            try
            {
                switch (featureName.ToLower())
                {
                    case "parachutespawn":
                        config.EnableParachuteSpawn = enabled;
                        break;
                    case "hud":
                        config.EnableHUD = enabled;
                        if (!enabled)
                        {
                            foreach (var player in BasePlayer.activePlayerList.ToList())
                                SafeExecute(() => DestroyHUD(player));
                        }
                        else
                        {
                            foreach (var player in BasePlayer.activePlayerList.ToList())
                                SafeExecute(() => timer.Once(0.1f, () => SafeExecute(() => CreateOrUpdateHUD(player))));
                        }
                        break;
                    case "safezoneblocking":
                        config.EnableSafeZoneBlocking = enabled;
                        break;
                    case "gather":
                        config.EnableGatherBonus = enabled;
                        break;
                    case "chat":
                        config.EnableChatIntegration = enabled;
                        break;
                    case "hungerthirst":
                        config.EnableContinuousHungerThirstPenalty = enabled;
                        break;
                    default:
                        return false;
                }
                
                Config.WriteObject(config, true);
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"ToggleFeature failed: {ex.Message}");
                return false;
            }
        }

        public List<PlayerReputationInfo> GetAllPlayersInfo()
        {
            var playerList = new List<PlayerReputationInfo>();
            
            try
            {
                foreach (var player in BasePlayer.activePlayerList.ToList())
                {
                    SafeExecute(() => {
                        if (IsValidPlayer(player))
                        {
                            EnsureRep(player.userID);
                            
                            playerList.Add(new PlayerReputationInfo
                            {
                                UserID = player.userID,
                                DisplayName = player.displayName,
                                Reputation = repData[player.userID],
                                Tier = GetTierName(repData[player.userID]),
                                GatherMultiplier = GetGatherMultiplier(repData[player.userID]),
                                CanUseParachute = CanUseParachute(repData[player.userID]),
                                IsOnline = true,
                                LastSeen = DateTime.Now
                            });
                        }
                    });
                }
                
                if (repData != null)
                {
                    foreach (var kvp in repData)
                    {
                        SafeExecute(() => {
                            if (playerList.Any(p => p.UserID == kvp.Key)) return;
                            
                            var covalencePlayer = covalence.Players.FindPlayerById(kvp.Key.ToString());
                            string displayName = covalencePlayer?.Name ?? "Unknown Player";
                            DateTime lastSeen = DateTime.MinValue;
                            
                            try
                            {
                                var offlinePlayer = BasePlayer.FindSleeping(kvp.Key);
                                if (offlinePlayer != null)
                                {
                                    lastSeen = DateTime.Now.AddHours(-1);
                                }
                                else
                                {
                                    lastSeen = DateTime.Now.AddDays(-7);
                                }
                            }
                            catch 
                            { 
                                lastSeen = DateTime.MinValue;
                            }
                            
                            playerList.Add(new PlayerReputationInfo
                            {
                                UserID = kvp.Key,
                                DisplayName = displayName,
                                Reputation = kvp.Value,
                                Tier = GetTierName(kvp.Value),
                                GatherMultiplier = GetGatherMultiplier(kvp.Value),
                                CanUseParachute = CanUseParachute(kvp.Value),
                                IsOnline = false,
                                LastSeen = lastSeen
                            });
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                PrintError($"GetAllPlayersInfo error: {ex.Message}");
            }
            
            return playerList.OrderByDescending(p => p.IsOnline).ThenByDescending(p => p.Reputation).ToList();
        }

        public bool SetMultiplePlayersReputation(List<ulong> userIDs, int reputation)
        {
            try
            {
                bool anyChanged = false;
                foreach (var userID in userIDs)
                {
                    if (SetPlayerReputation(userID, reputation))
                        anyChanged = true;
                }
                return anyChanged;
            }
            catch (Exception ex)
            {
                PrintError($"SetMultiplePlayersReputation failed: {ex.Message}");
                return false;
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

        // ===== DATA MANAGEMENT =====
        
        private void SaveReputationData()
        {
            try
            {
                if (repData != null)
                    Interface.Oxide.DataFileSystem.WriteObject(Name, repData);
            }
            catch (Exception ex)
            {
                PrintError($"Failed to save reputation data: {ex.Message}");
            }
        }

        private void LoadReputationData()
        {
            try
            {
                repData = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, int>>(Name) ?? new Dictionary<ulong, int>();
            }
            catch (Exception ex)
            {
                PrintError($"Failed to load reputation data: {ex.Message}");
                repData = new Dictionary<ulong, int>();
            }
        }

        public bool ResetAllReputation()
        {
            try
            {
                if (repData != null)
                {
                    foreach (var userID in repData.Keys.ToList())
                    {
                        SetPlayerReputation(userID, config?.DefaultReputation ?? 50);
                    }
                    SaveReputationData();
                }
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"ResetAllReputation failed: {ex.Message}");
                return false;
            }
        }

        public bool ExportReputationData(string filePath = null)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                    filePath = $"ReputationExport_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";

                var exportData = GetAllPlayersInfo();
                Interface.Oxide.DataFileSystem.WriteObject(filePath, exportData);
                PrintWarning($"Exported reputation data to {filePath}.json");
                return true;
            }
            catch (Exception ex)
            {
                PrintError($"ExportReputationData failed: {ex.Message}");
                return false;
            }
        }

        // ===== PLUGIN LIFECYCLE =====
        
        void Loaded()
        {
            LoadReputationData();
        }

        void OnServerShutdown()
        {
            SaveReputationData();
        }

        // ===== CONSOLE COMMANDS =====
        
        [ConsoleCommand("rep.export")]
        private void ConsoleExportCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !arg.Player().IsAdmin) return;
            
            if (ExportReputationData())
            {
                arg.ReplyWith("Reputation data exported successfully.");
            }
            else
            {
                arg.ReplyWith("Failed to export reputation data.");
            }
        }

        [ConsoleCommand("rep.stats")]
        private void ConsoleStatsCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !arg.Player().IsAdmin) return;
            
            var stats = GetReputationStats();
            arg.ReplyWith("=== Reputation Statistics ===");
            arg.ReplyWith($"Total Players: {stats["TotalPlayers"]} | Average: {stats["AverageReputation"]}");
            arg.ReplyWith($"Infidels: {stats["InfidelCount"]} | Sinners: {stats["SinnerCount"]} | Average: {stats["AverageCount"]}");
            arg.ReplyWith($"Disciples: {stats["DiscipleCount"]} | Prophets: {stats["ProphetCount"]} | Aerial: {stats["ParachuteEligible"]}");
        }
    }

    // ===== DATA STRUCTURES FOR SERVERMANAGER INTEGRATION =====
    
    public class PlayerReputationInfo
    {
        public ulong UserID { get; set; }
        public string DisplayName { get; set; }
        public int Reputation { get; set; }
        public string Tier { get; set; }
        public float GatherMultiplier { get; set; }
        public bool CanUseParachute { get; set; }
        public bool IsOnline { get; set; }
        public DateTime LastSeen { get; set; }
    }
}
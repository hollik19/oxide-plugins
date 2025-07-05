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
    [Info("ServerManager", "Modded", "4.0.0")]
    [Description("Complete server management with chair-based parachute system, live map, reputation, events, and environmental controls")]
    public class ServerManager : RustPlugin
    {
        [PluginReference]
        private Plugin ReputationSystemHUD;

        [PluginReference]
        private Plugin ZoneManager;

        private const string permAdmin = "servermanager.admin";
        private const string permParachute = "servermanager.parachute";

        // Core Settings
        private float decayFactor = 1f;
        private Dictionary<ulong, Dictionary<string, int>> customKits = new Dictionary<ulong, Dictionary<string, int>>();
        private Vector3 selectedEventPosition = Vector3.zero;
        private string selectedGridCoordinate = "";

        // Chair-Based Parachute System
        private Dictionary<ulong, ChairController> activeParachutes = new Dictionary<ulong, ChairController>();
        private Dictionary<ulong, Timer> parachuteFixedUpdateTimers = new Dictionary<ulong, Timer>();
        private const float MAX_PARACHUTE_SPEED = 70f; // km/h max speed
        private const float PARACHUTE_FALL_SPEED = -5f; // Controlled descent
        private const float PARACHUTE_LIFT_FORCE = 15f; // Upward force
        private const float HEADING_ROTATION_SPEED = 45f; // degrees per second
        private const float SPEED_CHANGE_RATE = 10f; // speed change per second

        // Live Map Integration
        private Dictionary<ulong, Timer> liveMapActiveTimers = new Dictionary<ulong, Timer>();
        private Vector2? liveMapSingleMarker = null;
        private Dictionary<ulong, DateTime> liveMapLastUpdate = new Dictionary<ulong, DateTime>();
        private Dictionary<ulong, bool> liveMapTeleportMode = new Dictionary<ulong, bool>();
        private const string LiveMapContainerName = "ServerManagerLiveMap";
        private const string LiveMapDotsContainerName = "SMMapDots";
        private const string LiveMapMarkersContainerName = "SMMapMarkers";
        private const float LiveMapUpdateInterval = 2f;
        private const int LiveMapMaxPlayers = 30;
        private const float LiveMapSize = 3750f;
        private const int LiveMapGridResolution = 25;

        // Environmental Controls
        private int crateUnlockTime = 15;
        private float timeOfDay = -1f;
        private float environmentTemp = -999f;
        private float environmentWind = -1f;
        private float environmentRain = -1f;

        // Reputation Config
        private bool repNpcPenaltyEnabled = true;
        private bool repParachuteSpawnEnabled = true;
        private bool repHudDisplayEnabled = true;
        private bool repSafeZoneHostilityEnabled = true;
        private bool repGatherBonusEnabled = true;

        private Color[] liveMapDotColors = new Color[]
        {
            Color.red, Color.blue, Color.green, Color.yellow, Color.magenta,
            Color.cyan, new Color(1f,0.5f,0f), new Color(0f,1f,0.5f), 
            new Color(0.5f,0f,1f), new Color(0.2f,0.8f,0.2f), 
            new Color(0.8f,0.2f,0.8f), new Color(0.2f,0.2f,0.8f),
            new Color(0.8f,0.8f,0.2f), new Color(0.9f,0.3f,0.3f),
            new Color(0.3f,0.9f,0.3f), new Color(0.3f,0.3f,0.9f)
        };

        // Chair Controller Class for Parachute System
        public class ChairController
        {
            public BasePlayer player;
            public BaseChair chair;
            public float currentSpeed = 0f;
            public float targetSpeed = 0f;
            public Vector3 direction = Vector3.forward;
            public bool isActive = false;
            public DateTime startTime;
            public Vector3 initialPosition;

            public ChairController(BasePlayer player, BaseChair chair)
            {
                this.player = player;
                this.chair = chair;
                this.startTime = DateTime.Now;
                this.initialPosition = chair.transform.position;
                this.isActive = true;
            }

            public void UpdateMovement()
            {
                if (!isActive || chair == null || chair.IsDestroyed || player == null || !player.IsConnected)
                {
                    isActive = false;
                    return;
                }

                // Smooth speed interpolation
                currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, SPEED_CHANGE_RATE * Time.deltaTime);

                // Convert km/h to m/s
                float speedMS = (currentSpeed * 1000f) / 3600f;

                // Calculate movement vector
                Vector3 moveVector = direction * speedMS * Time.deltaTime;
                
                // Add controlled fall (parachute effect)
                moveVector.y = PARACHUTE_FALL_SPEED * Time.deltaTime;

                // Apply movement to chair
                Vector3 newPosition = chair.transform.position + moveVector;
                
                // Ensure we don't go underground
                RaycastHit hit;
                if (Physics.Raycast(newPosition + Vector3.up * 10f, Vector3.down, out hit, 20f, LayerMask.GetMask("Terrain", "World")))
                {
                    if (newPosition.y < hit.point.y + 2f)
                    {
                        newPosition.y = hit.point.y + 2f;
                    }
                }

                chair.transform.position = newPosition;
                chair.transform.rotation = Quaternion.LookRotation(direction);

                // Update player if they're still mounted
                if (chair.GetMounted() == player)
                {
                    player.ClientRPCPlayer(null, player, "ForcePositionTo", newPosition);
                }
            }

            public void ProcessInput(InputState input)
            {
                if (!isActive || input == null) return;

                // W/S for speed control (0-70 km/h)
                if (input.IsDown(BUTTON.FORWARD))
                {
                    targetSpeed = Mathf.Min(targetSpeed + SPEED_CHANGE_RATE * Time.deltaTime, MAX_PARACHUTE_SPEED);
                }
                else if (input.IsDown(BUTTON.BACKWARD))
                {
                    targetSpeed = Mathf.Max(targetSpeed - SPEED_CHANGE_RATE * Time.deltaTime, 0f);
                }

                // A/D for heading control (rotate chair)
                if (input.IsDown(BUTTON.LEFT))
                {
                    float rotationAmount = -HEADING_ROTATION_SPEED * Time.deltaTime;
                    direction = Quaternion.Euler(0, rotationAmount, 0) * direction;
                }
                else if (input.IsDown(BUTTON.RIGHT))
                {
                    float rotationAmount = HEADING_ROTATION_SPEED * Time.deltaTime;
                    direction = Quaternion.Euler(0, rotationAmount, 0) * direction;
                }
            }
        }

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

        void Init()
        {
            permission.RegisterPermission(permAdmin, this);
            permission.RegisterPermission(permParachute, this);
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
                LoadReputationConfig();
            });
        }

        bool HasPerm(BasePlayer player) => player != null && permission.UserHasPermission(player.UserIDString, permAdmin);
        bool HasParachutePerm(BasePlayer player) => player != null && (permission.UserHasPermission(player.UserIDString, permAdmin) || permission.UserHasPermission(player.UserIDString, permParachute));

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

        int GetPlayerReputation(BasePlayer player)
        {
            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded) 
            {
                return 50;
            }
            
            try
            {
                var rep = ReputationSystemHUD.Call("GetPlayerReputation", player.userID);
                if (rep != null && rep is int reputation)
                {
                    return reputation;
                }
                return 50;
            }
            catch (Exception ex)
            {
                PrintWarning($"GetPlayerReputation error for {player.displayName}: {ex.Message}");
                return 50;
            }
        }

        bool SetPlayerReputation(BasePlayer player, int newRep)
        {
            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded) 
            {
                return false;
            }
            
            try
            {
                newRep = Mathf.Clamp(newRep, 0, 100);
                var result = ReputationSystemHUD.Call("SetPlayerReputation", player.userID, newRep);
                
                if (result != null && result is bool success && success)
                {
                    timer.Once(0.5f, () => {
                        try { ReputationSystemHUD.Call("RefreshAllPlayersHUD"); } catch { }
                    });
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                PrintWarning($"SetPlayerReputation error for {player.displayName}: {ex.Message}");
                return false;
            }
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

        void LoadReputationConfig()
        {
            if (ReputationSystemHUD?.IsLoaded != true) return;
            try
            {
                var config = ReputationSystemHUD.Call("GetConfig");
                if (config != null)
                {
                    PrintWarning("[ServerManager] Reputation config loaded successfully");
                }
            }
            catch (Exception ex)
            {
                PrintWarning($"[ServerManager] Failed to load reputation config: {ex.Message}");
            }
        }

        // Chair-Based Parachute System Commands
        [ChatCommand("para")]
        void CmdSpawnParachute(BasePlayer player, string command, string[] args)
        {
            if (!HasParachutePerm(player))
            {
                player.ChatMessage("<color=red>You do not have permission to use parachutes.</color>");
                return;
            }

            if (activeParachutes.ContainsKey(player.userID))
            {
                player.ChatMessage("<color=red>You already have an active parachute!</color>");
                return;
            }

            SpawnParachuteChair(player);
        }

        [ChatCommand("pararemove")]
        void CmdRemoveParachute(BasePlayer player, string command, string[] args)
        {
            if (!HasParachutePerm(player))
            {
                player.ChatMessage("<color=red>You do not have permission to use parachutes.</color>");
                return;
            }

            RemoveParachute(player);
        }

        void SpawnParachuteChair(BasePlayer player)
        {
			void SpawnParachuteChair(BasePlayer player)
{
    // Add this check at the beginning:
    if (ReputationSystemHUD?.IsLoaded == true)
    {
        int reputation = GetPlayerReputation(player);
        if (reputation < 90) // Prophet tier requirement
        {
            player.ChatMessage("<color=red>Only Prophet tier players (90+ reputation) can use parachutes!</color>");
            return;
        }
    }
    
    // Rest of existing method stays the same...
    Vector3 spawnPos = player.transform.position + Vector3.up * 200f;
    // ... continue with existing code
}
            Vector3 spawnPos = player.transform.position + Vector3.up * 200f;
            
            BaseChair chair = GameManager.server.CreateEntity("assets/prefabs/deployable/chair/chair.deployed.prefab", spawnPos) as BaseChair;
            if (chair == null)
            {
                player.ChatMessage("<color=red>Failed to spawn parachute chair.</color>");
                return;
            }

            chair.Spawn();
            chair.SetFlag(BaseEntity.Flags.Locked, true);
            
            ChairController controller = new ChairController(player, chair);
            activeParachutes[player.userID] = controller;

            // Start FixedUpdate timer for movement processing
            parachuteFixedUpdateTimers[player.userID] = timer.Every(0.02f, () => {
                if (activeParachutes.ContainsKey(player.userID))
                {
                    activeParachutes[player.userID].UpdateMovement();
                }
                else
                {
                    parachuteFixedUpdateTimers[player.userID]?.Destroy();
                    parachuteFixedUpdateTimers.Remove(player.userID);
                }
            });

            // Force mount player to chair
            timer.Once(0.1f, () => {
                if (chair != null && !chair.IsDestroyed && player.IsConnected)
                {
                    chair.AttemptMount(player, false);
                    player.ChatMessage("<color=green>Parachute deployed! Use W/S for speed (0-70 km/h), A/D to steer.</color>");
                }
            });

            // Auto-remove after 10 minutes
            timer.Once(600f, () => {
                if (activeParachutes.ContainsKey(player.userID))
                {
                    RemoveParachute(player);
                }
            });
        }

        void RemoveParachute(BasePlayer player)
        {
            if (!activeParachutes.ContainsKey(player.userID)) return;

            ChairController controller = activeParachutes[player.userID];
            activeParachutes.Remove(player.userID);

            if (parachuteFixedUpdateTimers.ContainsKey(player.userID))
            {
                parachuteFixedUpdateTimers[player.userID]?.Destroy();
                parachuteFixedUpdateTimers.Remove(player.userID);
            }

            if (controller.chair != null && !controller.chair.IsDestroyed)
            {
                if (controller.chair.GetMounted() == player)
                {
                    controller.chair.DismountPlayer(player, true);
                }
                controller.chair.Kill();
            }

            player.ChatMessage("<color=yellow>Parachute removed.</color>");
        }

        // Input processing for chair controls
        void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!activeParachutes.ContainsKey(player.userID)) return;
            
            ChairController controller = activeParachutes[player.userID];
            if (controller.chair != null && controller.chair.GetMounted() == player)
            {
                controller.ProcessInput(input);
            }
        }

        // Clean up parachute when player dismounts
        void OnEntityDismounted(BaseMountable entity, BasePlayer player)
        {
            if (entity is BaseChair && activeParachutes.ContainsKey(player.userID))
            {
                if (activeParachutes[player.userID].chair == entity)
                {
                    timer.Once(1f, () => RemoveParachute(player));
                }
            }
        }

        // Handle parachute landing
        void OnEntityGroundMissing(BaseEntity entity)
        {
            if (entity is BaseChair chair)
            {
                foreach (var kvp in activeParachutes.ToList())
                {
                    if (kvp.Value.chair == chair)
                    {
                        BasePlayer player = BasePlayer.FindByID(kvp.Key);
                        if (player != null)
                        {
                            player.ChatMessage("<color=green>Parachute landed safely!</color>");
                            RemoveParachute(player);
                        }
                        break;
                    }
                }
            }
        }// ===== LIVE MAP FUNCTIONALITY =====
        
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
                    new CuiRectTransformComponent { AnchorMin = "0.02 0.12", AnchorMax = "0.98 0.95" }
                }
            });

            CreateLiveMapClickGrid(container, player);
            
            // Close button
            container.Add(new CuiButton
            {
                Button = { Command = $"sm.livemap.close {player.userID}", Color = "0.8 0.2 0.2 0.9" },
                RectTransform = { AnchorMin = "0.94 0.96", AnchorMax = "0.99 0.99" },
                Text = { Text = "✕", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, LiveMapContainerName);

            // Teleport mode toggle
            bool teleportMode = liveMapTeleportMode.ContainsKey(player.userID) && liveMapTeleportMode[player.userID];
            string teleportColor = teleportMode ? "0.2 0.8 0.2 0.9" : "0.6 0.6 0.6 0.9";
            string teleportText = teleportMode ? "Teleport: ON" : "Teleport: OFF";
            
            container.Add(new CuiButton
            {
                Button = { Command = $"sm.livemap.toggleteleport {player.userID}", Color = teleportColor },
                RectTransform = { AnchorMin = "0.02 0.96", AnchorMax = "0.15 0.99" },
                Text = { Text = teleportText, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, LiveMapContainerName);

            // Instructions
            string instructionText = teleportMode ? 
                "TELEPORT MODE: Click to teleport instantly | Yellow = Selected, Colored = Players" :
                "EVENT MODE: Click to select event location | Yellow = Selected, Colored = Players";
                
            container.Add(new CuiLabel
            {
                Text = { Text = instructionText, FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.02 0.02", AnchorMax = "0.7 0.06" }
            }, LiveMapContainerName);

            // Confirm button (only show in event mode)
            if (!teleportMode)
            {
                container.Add(new CuiButton
                {
                    Button = { Command = $"sm.livemap.confirm {player.userID}", Color = "0.2 0.8 0.2 0.9" },
                    RectTransform = { AnchorMin = "0.75 0.02", AnchorMax = "0.92 0.06" },
                    Text = { Text = "Confirm Location", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, LiveMapContainerName);
            }
            else
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Click anywhere to teleport instantly", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.2 0.8 0.2 1" },
                    RectTransform = { AnchorMin = "0.75 0.02", AnchorMax = "0.92 0.06" }
                }, LiveMapContainerName);
            }

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

        // Live Map Console Commands
        [ConsoleCommand("sm.livemap.click")]
        void CmdLiveMapClick(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length < 3) return;
            if (!ulong.TryParse(arg.Args[0], out ulong id)) return;
            if (!float.TryParse(arg.Args[1], out float normX) || !float.TryParse(arg.Args[2], out float normY)) return;

            BasePlayer player = BasePlayer.FindByID(id);
            if (player == null || !HasPerm(player)) return;

            Vector2 world = LiveMapNormalizedToWorld(normX, normY);
            
            // Check if teleport mode is enabled
            bool teleportMode = liveMapTeleportMode.ContainsKey(player.userID) && liveMapTeleportMode[player.userID];
            
            if (teleportMode)
            {
                // Teleport player instantly
                Vector3 teleportPos = new Vector3(world.x, 0, world.y);
                
                // Find ground level
                RaycastHit hit;
                if (Physics.Raycast(teleportPos + Vector3.up * 200f, Vector3.down, out hit, 300f, LayerMask.GetMask("Terrain", "World")))
                {
                    teleportPos.y = hit.point.y + 1f;
                }
                else
                {
                    teleportPos.y = TerrainMeta.HeightMap.GetHeight(teleportPos);
                }
                
                player.Teleport(teleportPos);
                player.ChatMessage($"<color=green>Teleported to X={world.x:F1}, Z={world.y:F1}</color>");
                CloseLiveMapView(player);
            }
            else
            {
                // Event location selection mode
                liveMapSingleMarker = world;
                player.ChatMessage($"<color=green>Location selected: X={world.x:F1}, Z={world.y:F1}</color>");
                NextTick(() => UpdateLiveMapDotsAndMarkers(player));
            }
        }

        [ConsoleCommand("sm.livemap.toggleteleport")]
        void CmdLiveMapToggleTeleport(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length == 0) return;
            if (!ulong.TryParse(arg.Args[0], out ulong id)) return;

            BasePlayer player = BasePlayer.FindByID(id);
            if (player == null || !HasPerm(player)) return;

            if (!liveMapTeleportMode.ContainsKey(player.userID))
                liveMapTeleportMode[player.userID] = false;
                
            liveMapTeleportMode[player.userID] = !liveMapTeleportMode[player.userID];
            
            string mode = liveMapTeleportMode[player.userID] ? "TELEPORT" : "EVENT";
            player.ChatMessage($"<color=yellow>Live Map mode switched to: {mode}</color>");
            
            // Refresh the UI to show new mode
            string mapPath = GetLiveMapImagePath();
            CuiHelper.DestroyUi(player, LiveMapContainerName);
            CreateLiveMapUI(player, mapPath);
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
                Save();
                
                player.ChatMessage($"<color=green>Event location confirmed: X={marker.x:F1}, Z={marker.y:F1}</color>");
                CloseLiveMapView(player);
                OpenEventsTab(player);
            }
            else
            {
                player.ChatMessage("<color=red>No location selected!</color>");
            }
        }

        // ===== MAIN COMMAND & TAB SYSTEM =====

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
                Text = { Text = "SERVER MANAGER v4.0.0 - Chair Parachute System", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
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
        }// ===== GENERAL TAB & ENVIRONMENTAL CONTROLS =====

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
                RectTransform = { AnchorMin = "0.05 0.82", AnchorMax = "0.4 0.87" }
            }, "ServerManagerContent");

            for (int i = 0; i < 10; i++)
            {
                float factor = (i + 1) * 0.1f;
                float xPos = i * 0.04f + 0.05f;
                string color = Mathf.Approximately(decayFactor, factor) ? "0.2 0.8 0.2 1" : "0.3 0.3 0.3 1";

                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = $"sm.decay.set {factor:F1}" },
                    RectTransform = { AnchorMin = $"{xPos} 0.76", AnchorMax = $"{xPos + 0.035f} 0.81" },
                    Text = { Text = factor.ToString("F1"), FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");
            }

            // Crate Unlock Time Section
            container.Add(new CuiLabel
            {
                Text = { Text = $"Locked Crate Timer: {crateUnlockTime} minutes", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.67", AnchorMax = "0.5 0.72" }
            }, "ServerManagerContent");

            for (int i = 1; i <= 15; i++)
            {
                float xPos = (i - 1) * 0.06f + 0.05f;
                float yPos = 0.61f;
                if (i > 8) { xPos = (i - 9) * 0.06f + 0.05f; yPos = 0.56f; }
                
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
                RectTransform = { AnchorMin = "0.05 0.47", AnchorMax = "0.5 0.52" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = timeOfDay < 0 ? "0.2 0.8 0.2 1" : "0.3 0.3 0.3 1", Command = "sm.time.auto" },
                RectTransform = { AnchorMin = "0.05 0.41", AnchorMax = "0.12 0.46" },
                Text = { Text = "Auto", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            // Extended time selection with sunrise/sunset
            int[] times = { 5, 6, 7, 12, 17, 18, 19, 0 };
            string[] timeLabels = { "5AM", "6AM", "7AM", "12PM", "5PM", "6PM", "7PM", "12AM" };
            for (int i = 0; i < times.Length; i++)
            {
                int col = i % 4;
                int row = i / 4;
                float xPos = 0.14f + (col * 0.08f);
                float yPos = 0.41f - (row * 0.05f);
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
                RectTransform = { AnchorMin = "0.05 0.32", AnchorMax = "0.95 0.37" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.3 0.3 1", Command = "sm.env.clearweather" },
                RectTransform = { AnchorMin = "0.05 0.26", AnchorMax = "0.2 0.31" },
                Text = { Text = "Clear Weather", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.3 0.5 0.7 1", Command = "sm.env.rain" },
                RectTransform = { AnchorMin = "0.22 0.26", AnchorMax = "0.37 0.31" },
                Text = { Text = "Start Rain", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.7 0.5 0.3 1", Command = "sm.env.fog" },
                RectTransform = { AnchorMin = "0.39 0.26", AnchorMax = "0.54 0.31" },
                Text = { Text = "Add Fog", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.5 0.3 0.7 1", Command = "sm.env.wind" },
                RectTransform = { AnchorMin = "0.56 0.26", AnchorMax = "0.71 0.31" },
                Text = { Text = "Strong Wind", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.3 0.7 0.5 1", Command = "sm.env.storm" },
                RectTransform = { AnchorMin = "0.73 0.26", AnchorMax = "0.88 0.31" },
                Text = { Text = "Thunder Storm", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            // Parachute System Controls
            container.Add(new CuiLabel
            {
                Text = { Text = "PARACHUTE SYSTEM", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.17", AnchorMax = "0.95 0.22" }
            }, "ServerManagerContent");

            int activeParachuteCount = activeParachutes.Count;
            container.Add(new CuiLabel
            {
                Text = { Text = $"Active Parachutes: {activeParachuteCount}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.05 0.13", AnchorMax = "0.3 0.17" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.6 0.8 1", Command = "sm.para.spawn.self" },
                RectTransform = { AnchorMin = "0.35 0.11", AnchorMax = "0.5 0.16" },
                Text = { Text = "Spawn Parachute", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.4 0.2 1", Command = "sm.para.remove.self" },
                RectTransform = { AnchorMin = "0.52 0.11", AnchorMax = "0.67 0.16" },
                Text = { Text = "Remove Mine", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.2 0.2 1", Command = "sm.para.removeall" },
                RectTransform = { AnchorMin = "0.69 0.11", AnchorMax = "0.84 0.16" },
                Text = { Text = "Remove All", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            // Admin Utilities
            container.Add(new CuiLabel
            {
                Text = { Text = "ADMIN UTILITIES", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.04", AnchorMax = "0.95 0.09" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.3 0.3 1", Command = "sm.admin.save" },
                RectTransform = { AnchorMin = "0.05 0.01", AnchorMax = "0.18 0.04" },
                Text = { Text = "Force Save", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.3 0.8 0.3 1", Command = "sm.admin.gc" },
                RectTransform = { AnchorMin = "0.2 0.01", AnchorMax = "0.33 0.04" },
                Text = { Text = "Garbage Collect", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.4 0.8 1", Command = "sm.admin.restart" },
                RectTransform = { AnchorMin = "0.35 0.01", AnchorMax = "0.48 0.04" },
                Text = { Text = "Restart Warning", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.6 0.2 1", Command = "sm.admin.broadcast" },
                RectTransform = { AnchorMin = "0.5 0.01", AnchorMax = "0.63 0.04" },
                Text = { Text = "Broadcast", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }

        // ===== GENERAL TAB CONSOLE COMMANDS =====

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
                Save();
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
                Save();
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
                Save();
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
            Save();
            OpenGeneralTab(player);
            player.ChatMessage("<color=green>Time set to automatic</color>");
        }

        [ConsoleCommand("sm.env.clearweather")]
        void CmdClearWeather(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("weather.rain 0");
            rust.RunServerCommand("weather.fog 0");
            rust.RunServerCommand("weather.wind 0");
            rust.RunServerCommand("weather.cloud 0");
            player.ChatMessage("<color=green>Weather cleared</color>");
        }

        [ConsoleCommand("sm.env.rain")]
        void CmdRain(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("weather.rain 1");
            rust.RunServerCommand("weather.cloud 1");
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

        [ConsoleCommand("sm.env.storm")]
        void CmdStorm(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            rust.RunServerCommand("weather.rain 1");
            rust.RunServerCommand("weather.wind 1");
            rust.RunServerCommand("weather.cloud 1");
            rust.RunServerCommand("weather.fog 0.3");
            player.ChatMessage("<color=green>Thunder storm created</color>");
        }

        [ConsoleCommand("sm.para.spawn.self")]
        void CmdParaSpawnSelf(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            if (activeParachutes.ContainsKey(player.userID))
            {
                player.ChatMessage("<color=red>You already have an active parachute!</color>");
                return;
            }

            SpawnParachuteChair(player);
        }

        [ConsoleCommand("sm.para.remove.self")]
        void CmdParaRemoveSelf(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            if (!activeParachutes.ContainsKey(player.userID))
            {
                player.ChatMessage("<color=red>You don't have an active parachute!</color>");
                return;
            }

            RemoveParachute(player);
            OpenGeneralTab(player);
        }

        [ConsoleCommand("sm.para.removeall")]
        void CmdParaRemoveAll(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            int removed = 0;
            foreach (var kvp in activeParachutes.ToList())
            {
                BasePlayer parachutePlayer = BasePlayer.FindByID(kvp.Key);
                if (parachutePlayer != null)
                {
                    RemoveParachute(parachutePlayer);
                    removed++;
                }
            }

            player.ChatMessage($"<color=green>Removed {removed} active parachutes</color>");
            OpenGeneralTab(player);
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

        [ConsoleCommand("sm.admin.restart")]
        void CmdRestart(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            Server.Broadcast("<color=red>SERVER RESTART IN 5 MINUTES - SAVE YOUR PROGRESS!</color>");
            player.ChatMessage("<color=green>Restart warning broadcasted</color>");
        }

        [ConsoleCommand("sm.admin.broadcast")]
        void CmdBroadcast(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            string message = string.Join(" ", arg.Args ?? new string[0]);
            if (string.IsNullOrEmpty(message))
            {
                player.ChatMessage("<color=red>Usage: Enter a message to broadcast</color>");
                return;
            }

            Server.Broadcast($"<color=yellow>[ADMIN BROADCAST]</color> {message}");
            player.ChatMessage($"<color=green>Broadcasted:</color> {message}");
        }// ===== KITS TAB =====

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

            // Add "Give to All" option
            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.6 0.2 1", Command = "sm.kit.giveall" },
                RectTransform = { AnchorMin = "0.1 0.02", AnchorMax = "0.9 0.08" },
                Text = { Text = "GIVE KIT TO ALL ONLINE PLAYERS", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }

        // Kit Commands
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

            Save();
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
                Save();
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
                Save();
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

        [ConsoleCommand("sm.kit.giveall")]
        void CmdKitGiveAll(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            if (!customKits.TryGetValue(player.userID, out var kit) || kit.Count == 0)
            {
                player.ChatMessage("<color=red>Your custom kit is empty</color>");
                return;
            }

            int playersGiven = 0;
            int totalFailures = 0;

            foreach (var recipient in BasePlayer.activePlayerList)
            {
                if (recipient == null || !recipient.IsConnected) continue;

                int failedAdds = 0;
                int totalItems = 0;

                foreach (var kvp in kit)
                {
                    ItemDefinition def = ItemManager.FindItemDefinition(kvp.Key);
                    if (def == null) continue;

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

                if (totalItems > 0)
                {
                    playersGiven++;
                    recipient.ChatMessage("<color=green>You received a custom kit from an admin.</color>");
                }

                totalFailures += failedAdds;
            }

            player.ChatMessage($"<color=green>Given custom kit to {playersGiven} players.</color>");
            if (totalFailures > 0)
                player.ChatMessage($"<color=yellow>Total item failures: {totalFailures} (inventory space issues)</color>");

            OpenKitsTab(player);
        }

        // ===== EVENTS TAB =====

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
                ["oilrig"] = "Oil Rig Event",
                ["zombiehorde"] = "Zombie Horde"
            };

            container.Add(new CuiLabel
            {
                Text = { Text = "SPAWN EVENTS:", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.1 0.67", AnchorMax = "0.9 0.72" }
            }, "ServerManagerContent");

            int eventIndex = 0;
            foreach (var evt in events)
            {
                float yPos = 0.6f - (eventIndex * 0.07f);
                
                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.6 0.2 1", Command = $"sm.event.spawn {evt.Key}" },
                    RectTransform = { AnchorMin = $"0.2 {yPos}", AnchorMax = $"0.8 {yPos + 0.06f}" },
                    Text = { Text = evt.Value, FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                eventIndex++;
            }

            // Event quantity controls
            container.Add(new CuiLabel
            {
                Text = { Text = "QUANTITY CONTROLS:", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.1 0.05", AnchorMax = "0.9 0.09" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.4 0.2 1", Command = "sm.event.multiple 3" },
                RectTransform = { AnchorMin = "0.1 0.01", AnchorMax = "0.3 0.05" },
                Text = { Text = "Spawn x3", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.4 0.2 1", Command = "sm.event.multiple 5" },
                RectTransform = { AnchorMin = "0.35 0.01", AnchorMax = "0.55 0.05" },
                Text = { Text = "Spawn x5", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.2 0.2 1", Command = "sm.event.cleanup" },
                RectTransform = { AnchorMin = "0.7 0.01", AnchorMax = "0.9 0.05" },
                Text = { Text = "Cleanup Events", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }// Event Commands
        [ConsoleCommand("sm.event.setpos")]
        void CmdEventSetPos(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            selectedEventPosition = player.transform.position;
            selectedGridCoordinate = "";
            Save();
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
            Save();
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

            // Ensure teleport mode is disabled for event selection
            liveMapTeleportMode[player.userID] = false;

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

                case "zombiehorde":
                    for (int i = 0; i < 20; i++)
                    {
                        Vector3 spawnPos = pos + new Vector3(UnityEngine.Random.Range(-50f, 50f), 0, UnityEngine.Random.Range(-50f, 50f));
                        rust.RunServerCommand($"spawn murderer {spawnPos.x} {spawnPos.y} {spawnPos.z}");
                    }
                    player.ChatMessage($"<color=green>Zombie horde spawned at {pos}</color>");
                    break;

                default:
                    player.ChatMessage("<color=red>Unknown event type</color>");
                    break;
            }
        }

        [ConsoleCommand("sm.event.multiple")]
        void CmdEventMultiple(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            if (!int.TryParse(arg.Args[0], out int count) || count < 1 || count > 10)
            {
                player.ChatMessage("<color=red>Invalid count (1-10)</color>");
                return;
            }

            player.ChatMessage($"<color=yellow>Multiple event mode enabled. Next {count} events will spawn multiple instances.</color>");
            // Store the count for next event spawn
            // This would require additional state management for full implementation
        }

        [ConsoleCommand("sm.event.cleanup")]
        void CmdEventCleanup(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            int cleaned = 0;
            
            // Clean up helicopters
            foreach (var heli in UnityEngine.Object.FindObjectsOfType<PatrolHelicopter>())
            {
                if (heli != null && !heli.IsDestroyed)
                {
                    heli.Kill();
                    cleaned++;
                }
            }

            // Clean up bradley
            foreach (var bradley in UnityEngine.Object.FindObjectsOfType<BradleyAPC>())
            {
                if (bradley != null && !bradley.IsDestroyed)
                {
                    bradley.Kill();
                    cleaned++;
                }
            }

            // Clean up cargo ships
            foreach (var cargo in UnityEngine.Object.FindObjectsOfType<CargoShip>())
            {
                if (cargo != null && !cargo.IsDestroyed)
                {
                    cargo.Kill();
                    cleaned++;
                }
            }

            player.ChatMessage($"<color=green>Cleaned up {cleaned} event entities</color>");
        }

        // ===== REPUTATION TAB =====

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
                Text = { Text = "REPUTATION MANAGER", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.1 0.9", AnchorMax = "0.9 0.95" }
            }, "ServerManagerContent");

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Reputation System HUD plugin is not loaded!", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 0.2 0.2 1" },
                    RectTransform = { AnchorMin = "0.1 0.4", AnchorMax = "0.9 0.5" }
                }, "ServerManagerContent");
                
                CuiHelper.AddUi(player, container);
                return;
            }

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
                Text = { Text = "Actions", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.7 0.7 0.7 1" },
                RectTransform = { AnchorMin = "0.5 0.78", AnchorMax = "0.95 0.82" }
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
                
                int reputation = GetPlayerReputation(p);
                string repColor = reputation >= 75 ? "0.2 1 0.2 1" : reputation >= 50 ? "1 1 0.2 1" : reputation >= 25 ? "1 0.6 0.2 1" : "1 0.2 0.2 1";

                container.Add(new CuiLabel
                {
                    Text = { Text = playerName, FontSize = 9, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = $"0.05 {yMin}", AnchorMax = $"0.4 {yMax}" }
                }, "ServerManagerContent");

                container.Add(new CuiLabel
                {
                    Text = { Text = reputation.ToString(), FontSize = 9, Align = TextAnchor.MiddleCenter, Color = repColor },
                    RectTransform = { AnchorMin = $"0.4 {yMin}", AnchorMax = $"0.5 {yMax}" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.6 0.2 0.2 1", Command = $"sm.rep.modify {p.userID} -10" },
                    RectTransform = { AnchorMin = $"0.52 {yMin}", AnchorMax = $"0.59 {yMax}" },
                    Text = { Text = "-10", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.4 0.4 0.4 1", Command = $"sm.rep.set {p.userID} 0" },
                    RectTransform = { AnchorMin = $"0.61 {yMin}", AnchorMax = $"0.68 {yMax}" },
                    Text = { Text = "0", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.4 0.4 0.4 1", Command = $"sm.rep.set {p.userID} 50" },
                    RectTransform = { AnchorMin = $"0.7 {yMin}", AnchorMax = $"0.77 {yMax}" },
                    Text = { Text = "50", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.4 0.4 0.4 1", Command = $"sm.rep.set {p.userID} 100" },
                    RectTransform = { AnchorMin = $"0.79 {yMin}", AnchorMax = $"0.87 {yMax}" },
                    Text = { Text = "100", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");

                container.Add(new CuiButton
                {
                    Button = { Color = "0.2 0.6 0.2 1", Command = $"sm.rep.modify {p.userID} 10" },
                    RectTransform = { AnchorMin = $"0.89 {yMin}", AnchorMax = $"0.96 {yMax}" },
                    Text = { Text = "+10", FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");
            }

            // Mass Actions
            container.Add(new CuiLabel
            {
                Text = { Text = "MASS ACTIONS", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.12", AnchorMax = "0.95 0.16" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.6 0.2 1", Command = "sm.rep.massreset" },
                RectTransform = { AnchorMin = "0.1 0.06", AnchorMax = "0.25 0.11" },
                Text = { Text = "Reset All to 50", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.6 0.6 1", Command = "sm.rep.refresh" },
                RectTransform = { AnchorMin = "0.3 0.06", AnchorMax = "0.45 0.11" },
                Text = { Text = "Refresh HUDs", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.2 0.6 1", Command = "sm.rep.stats" },
                RectTransform = { AnchorMin = "0.5 0.06", AnchorMax = "0.65 0.11" },
                Text = { Text = "Show Stats", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.2 0.6 1", Command = "sm.rep.massmodify -25" },
                RectTransform = { AnchorMin = "0.1 0.01", AnchorMax = "0.25 0.05" },
                Text = { Text = "All -25 Rep", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.6 0.2 1", Command = "sm.rep.massmodify 25" },
                RectTransform = { AnchorMin = "0.3 0.01", AnchorMax = "0.45 0.05" },
                Text = { Text = "All +25 Rep", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.4 0.2 1", Command = "sm.rep.randomize" },
                RectTransform = { AnchorMin = "0.5 0.01", AnchorMax = "0.65 0.05" },
                Text = { Text = "Randomize All", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.6 0.6 1", Command = "sm.rep.backup" },
                RectTransform = { AnchorMin = "0.7 0.01", AnchorMax = "0.85 0.05" },
                Text = { Text = "Backup Data", FontSize = 9, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }// Reputation Commands
        [ConsoleCommand("sm.rep.set")]
        void CmdRepSet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 2) return;

            if (!ulong.TryParse(arg.Args[0], out ulong targetId)) return;
            if (!int.TryParse(arg.Args[1], out int newRep)) return;

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                player.ChatMessage("<color=red>Reputation plugin not loaded.</color>");
                return;
            }

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

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                player.ChatMessage("<color=red>Reputation plugin not loaded.</color>");
                return;
            }

            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target == null)
            {
                player.ChatMessage("<color=red>Player not found.</color>");
                return;
            }

            int currentRep = GetPlayerReputation(target);
            int newRep = Mathf.Clamp(currentRep + amount, 0, 100);

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

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                player.ChatMessage("<color=red>Reputation plugin not loaded.</color>");
                return;
            }

            try
            {
                var result = ReputationSystemHUD.Call("ResetAllReputation");
                if (result != null && result is bool success && success)
                {
                    player.ChatMessage("<color=green>All player reputations reset to 50.</color>");
                    timer.Once(1f, () => OpenReputationTab(player));
                }
                else
                {
                    player.ChatMessage("<color=red>Mass reset failed.</color>");
                }
            }
            catch
            {
                player.ChatMessage("<color=red>Mass reset failed.</color>");
            }
        }

        [ConsoleCommand("sm.rep.refresh")]
        void CmdRepRefresh(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                player.ChatMessage("<color=red>Reputation plugin not loaded.</color>");
                return;
            }

            try
            {
                ReputationSystemHUD.Call("RefreshAllPlayersHUD");
                player.ChatMessage("<color=green>All player HUDs refreshed.</color>");
            }
            catch
            {
                player.ChatMessage("<color=red>HUD refresh failed.</color>");
            }
        }

        [ConsoleCommand("sm.rep.stats")]
        void CmdRepStats(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                player.ChatMessage("<color=red>Reputation plugin not loaded.</color>");
                return;
            }

            try
            {
                var stats = ReputationSystemHUD.Call("GetReputationStats");
                if (stats != null)
                {
                    player.ChatMessage("<color=green>=== Reputation Statistics ===</color>");
                    player.ChatMessage($"<color=yellow>Check server console for detailed stats.</color>");
                    PrintWarning("=== Reputation Statistics ===");
                    PrintWarning(stats.ToString());
                }
            }
            catch
            {
                player.ChatMessage("<color=red>Stats retrieval failed.</color>");
            }
        }

        [ConsoleCommand("sm.rep.massmodify")]
        void CmdRepMassModify(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            if (!int.TryParse(arg.Args[0], out int amount))
            {
                player.ChatMessage("<color=red>Invalid amount.</color>");
                return;
            }

            int modified = 0;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected) continue;
                
                int currentRep = GetPlayerReputation(p);
                int newRep = Mathf.Clamp(currentRep + amount, 0, 100);
                
                if (SetPlayerReputation(p, newRep))
                {
                    modified++;
                }
            }

            string change = amount > 0 ? $"+{amount}" : amount.ToString();
            player.ChatMessage($"<color=green>Modified {modified} players' reputation by {change}</color>");
            timer.Once(1f, () => OpenReputationTab(player));
        }

        [ConsoleCommand("sm.rep.randomize")]
        void CmdRepRandomize(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            int randomized = 0;
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected) continue;
                
                int randomRep = UnityEngine.Random.Range(0, 101);
                
                if (SetPlayerReputation(p, randomRep))
                {
                    randomized++;
                }
            }

            player.ChatMessage($"<color=green>Randomized {randomized} players' reputation (0-100)</color>");
            timer.Once(1f, () => OpenReputationTab(player));
        }

        [ConsoleCommand("sm.rep.backup")]
        void CmdRepBackup(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            try
            {
                var backupData = new Dictionary<string, object>();
                foreach (var p in BasePlayer.activePlayerList)
                {
                    if (p == null || !p.IsConnected) continue;
                    backupData[p.userID.ToString()] = GetPlayerReputation(p);
                }

                string backupFile = $"ReputationBackup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                Interface.Oxide.DataFileSystem.WriteObject(backupFile, backupData);
                
                player.ChatMessage($"<color=green>Reputation data backed up to {backupFile}</color>");
                PrintWarning($"[ServerManager] Reputation backup created: {backupFile}");
            }
            catch (Exception ex)
            {
                player.ChatMessage("<color=red>Backup failed.</color>");
                PrintError($"[ServerManager] Backup error: {ex.Message}");
            }
        }

        // ===== REPUTATION CONFIG TAB =====

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

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                container.Add(new CuiLabel
                {
                    Text = { Text = "Reputation System HUD plugin is not loaded!", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 0.2 0.2 1" },
                    RectTransform = { AnchorMin = "0.1 0.4", AnchorMax = "0.9 0.5" }
                }, "ServerManagerContent");
                
                CuiHelper.AddUi(player, container);
                return;
            }

            // Feature Toggles Section
            container.Add(new CuiLabel
            {
                Text = { Text = "FEATURE TOGGLES", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.75", AnchorMax = "0.95 0.8" }
            }, "ServerManagerContent");

            string[] features = { "NPC Kill Penalty", "Parachute Spawn", "HUD Display", "Safe Zone Hostility", "Gather Bonus" };
            bool[] featureStates = { repNpcPenaltyEnabled, repParachuteSpawnEnabled, repHudDisplayEnabled, repSafeZoneHostilityEnabled, repGatherBonusEnabled };

            for (int i = 0; i < features.Length; i++)
            {
                int col = i % 3;
                int row = i / 3;
                float xPos = 0.05f + (col * 0.3f);
                float yPos = 0.68f - (row * 0.08f);
                
                string color = featureStates[i] ? "0.2 0.8 0.2 1" : "0.8 0.2 0.2 1";
                string status = featureStates[i] ? "ON" : "OFF";

                container.Add(new CuiButton
                {
                    Button = { Color = color, Command = $"sm.repconfig.toggle {i}" },
                    RectTransform = { AnchorMin = $"{xPos} {yPos}", AnchorMax = $"{xPos + 0.28f} {yPos + 0.06f}" },
                    Text = { Text = $"{features[i]}: {status}", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
                }, "ServerManagerContent");
            }

            // Reputation System Status
            container.Add(new CuiLabel
            {
                Text = { Text = "SYSTEM STATUS", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.45", AnchorMax = "0.95 0.5" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "✓ Reputation System HUD: Connected", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "0.2 0.8 0.2 1" },
                RectTransform = { AnchorMin = "0.05 0.4", AnchorMax = "0.5 0.44" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = $"✓ Zone Manager: {(ZoneManager?.IsLoaded == true ? "Connected" : "Not Connected")}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = ZoneManager?.IsLoaded == true ? "0.2 0.8 0.2 1" : "0.8 0.6 0.2 1" },
                RectTransform = { AnchorMin = "0.05 0.36", AnchorMax = "0.5 0.4" }
            }, "ServerManagerContent");

            // Quick Actions
            container.Add(new CuiLabel
            {
                Text = { Text = "QUICK ACTIONS", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.27", AnchorMax = "0.95 0.32" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.8 0.2 1", Command = "sm.repconfig.apply" },
                RectTransform = { AnchorMin = "0.1 0.2", AnchorMax = "0.25 0.26" },
                Text = { Text = "Apply Changes", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.6 0.2 1", Command = "sm.repconfig.reset" },
                RectTransform = { AnchorMin = "0.3 0.2", AnchorMax = "0.45 0.26" },
                Text = { Text = "Reset to Defaults", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.6 0.6 1", Command = "sm.repconfig.reload" },
                RectTransform = { AnchorMin = "0.5 0.2", AnchorMax = "0.65 0.26" },
                Text = { Text = "Reload Config", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.6 0.8 1", Command = "sm.repconfig.test" },
                RectTransform = { AnchorMin = "0.7 0.2", AnchorMax = "0.85 0.26" },
                Text = { Text = "Test Features", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            // Configuration Info
            container.Add(new CuiLabel
            {
                Text = { Text = "CONFIGURATION INFO", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.11", AnchorMax = "0.95 0.16" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "• NPC Kill Penalty: Reduces reputation when killing scientists/NPCs", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.05 0.07", AnchorMax = "0.95 0.11" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "• Parachute Spawn: Allows reputation-based parachute spawning", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.05 0.04", AnchorMax = "0.95 0.07" }
            }, "ServerManagerContent");

            container.Add(new CuiLabel
            {
                Text = { Text = "• HUD Display: Shows reputation HUD to players", FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "0.9 0.9 0.9 1" },
                RectTransform = { AnchorMin = "0.05 0.01", AnchorMax = "0.95 0.04" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }

        // Reputation Config Commands
        [ConsoleCommand("sm.repconfig.toggle")]
        void CmdRepConfigToggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 1) return;

            if (!int.TryParse(arg.Args[0], out int featureIndex)) return;

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                player.ChatMessage("<color=red>Reputation plugin not loaded.</color>");
                return;
            }

            try
            {
                bool success = false;
                string featureName = "";
                
                switch (featureIndex)
                {
                    case 0: // NPC Kill Penalty
                        repNpcPenaltyEnabled = !repNpcPenaltyEnabled;
                        success = true;
                        player.ChatMessage($"<color=green>NPC Kill Penalty: {(repNpcPenaltyEnabled ? "ON" : "OFF")}</color>");
                        break;
                    case 1: // Parachute Spawn
                        featureName = "parachutespawn";
                        repParachuteSpawnEnabled = !repParachuteSpawnEnabled;
                        var result2 = ReputationSystemHUD.Call("ToggleFeature", featureName, repParachuteSpawnEnabled);
                        success = result2 != null && result2 is bool s2 && s2;
                        break;
                    case 2: // HUD Display
                        featureName = "hud";
                        repHudDisplayEnabled = !repHudDisplayEnabled;
                        var result3 = ReputationSystemHUD.Call("ToggleFeature", featureName, repHudDisplayEnabled);
                        success = result3 != null && result3 is bool s3 && s3;
                        break;
                    case 3: // Safe Zone Hostility
                        featureName = "safezones";
                        repSafeZoneHostilityEnabled = !repSafeZoneHostilityEnabled;
                        var result4 = ReputationSystemHUD.Call("ToggleFeature", featureName, repSafeZoneHostilityEnabled);
                        success = result4 != null && result4 is bool s4 && s4;
                        break;
                    case 4: // Gather Bonus
                        featureName = "gather";
                        repGatherBonusEnabled = !repGatherBonusEnabled;
                        var result5 = ReputationSystemHUD.Call("ToggleFeature", featureName, repGatherBonusEnabled);
                        success = result5 != null && result5 is bool s5 && s5;
                        break;
                    default:
                        player.ChatMessage("<color=yellow>Unknown feature index</color>");
                        break;
                }

                if (success)
                {
                    if (featureIndex != 0) // Not NPC penalty
                        player.ChatMessage($"<color=green>Feature toggled successfully!</color>");
                    Save(); // Save our local config
                }
                else if (featureIndex != 0)
                {
                    player.ChatMessage($"<color=yellow>Feature toggle may not be fully supported</color>");
                }
                
                OpenReputationConfigTab(player);
            }
            catch (Exception ex)
            {
                player.ChatMessage("<color=red>Error toggling feature.</color>");
                PrintError($"Feature toggle error: {ex.Message}");
            }
        }

        [ConsoleCommand("sm.repconfig.apply")]
        void CmdRepConfigApply(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded)
            {
                player.ChatMessage("<color=red>Reputation plugin not loaded.</color>");
                return;
            }

            try
            {
                var result = ReputationSystemHUD.Call("ApplyConfiguration");
                
                if (result != null && result is bool success && success)
                {
                    player.ChatMessage("<color=green>Configuration applied successfully!</color>");
                    Save();
                }
                else
                {
                    player.ChatMessage("<color=yellow>Configuration saved locally (API may not be available)</color>");
                    Save();
                }
            }
            catch (Exception ex)
            {
                player.ChatMessage("<color=yellow>Configuration saved locally (API error)</color>");
                PrintError($"RepConfig Apply Error: {ex.Message}");
                Save();
            }
        }

        [ConsoleCommand("sm.repconfig.reset")]
        void CmdRepConfigReset(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            repNpcPenaltyEnabled = true;
            repParachuteSpawnEnabled = true;
            repHudDisplayEnabled = true;
            repSafeZoneHostilityEnabled = true;
            repGatherBonusEnabled = true;

            player.ChatMessage("<color=green>Configuration reset to defaults</color>");
            Save();
            OpenReputationConfigTab(player);
        }

        [ConsoleCommand("sm.repconfig.reload")]
        void CmdRepConfigReload(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            LoadReputationConfig();
            player.ChatMessage("<color=green>Configuration reloaded from plugin</color>");
            OpenReputationConfigTab(player);
        }

        [ConsoleCommand("sm.repconfig.test")]
        void CmdRepConfigTest(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            try
            {
                var testResult = ReputationSystemHUD.Call("TestFeatures");
                if (testResult != null)
                {
                    player.ChatMessage("<color=green>Feature test completed - check console for results</color>");
                    PrintWarning($"[ServerManager] Reputation feature test result: {testResult}");
                }
                else
                {
                    player.ChatMessage("<color=yellow>Feature test not supported by reputation plugin</color>");
                }
            }
            catch (Exception ex)
            {
                player.ChatMessage("<color=red>Feature test failed</color>");
                PrintError($"[ServerManager] Feature test error: {ex.Message}");
            }
        }// ===== LIVE MAP TAB =====

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
                RectTransform = { AnchorMin = "0.1 0.4", AnchorMax = "0.3 0.45" },
                Text = { Text = "Test Live Map", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.4 0.2 1", Command = "sm.livemap.reload" },
                RectTransform = { AnchorMin = "0.35 0.4", AnchorMax = "0.55 0.45" },
                Text = { Text = "Reload Map URL", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.8 0.6 1", Command = "sm.livemap.teleportmode" },
                RectTransform = { AnchorMin = "0.6 0.4", AnchorMax = "0.8 0.45" },
                Text = { Text = "Open Teleport Map", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
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

            // Teleport mode users
            int teleportUsers = 0;
foreach (var kvp in liveMapTeleportMode)
{
    if (kvp.Value) teleportUsers++;
}
            container.Add(new CuiLabel
            {
                Text = { Text = $"• Teleport Mode Users: {teleportUsers}", FontSize = 12, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.55 0.25", AnchorMax = "0.95 0.3" }
            }, "ServerManagerContent");

            // Map management buttons
            container.Add(new CuiButton
            {
                Button = { Color = "0.8 0.2 0.2 1", Command = "sm.livemap.closeall" },
                RectTransform = { AnchorMin = "0.55 0.2", AnchorMax = "0.75 0.25" },
                Text = { Text = "Close All Maps", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.2 0.8 1", Command = "sm.livemap.resetall" },
                RectTransform = { AnchorMin = "0.77 0.2", AnchorMax = "0.95 0.25" },
                Text = { Text = "Reset All Modes", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            // Advanced Settings
            container.Add(new CuiLabel
            {
                Text = { Text = "ADVANCED SETTINGS:", FontSize = 14, Align = TextAnchor.MiddleLeft, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.09" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.4 0.6 0.4 1", Command = "sm.livemap.settings" },
                RectTransform = { AnchorMin = "0.05 0.01", AnchorMax = "0.2 0.05" },
                Text = { Text = "Map Settings", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.6 0.4 0.6 1", Command = "sm.livemap.export" },
                RectTransform = { AnchorMin = "0.25 0.01", AnchorMax = "0.4 0.05" },
                Text = { Text = "Export Data", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            container.Add(new CuiButton
            {
                Button = { Color = "0.4 0.4 0.6 1", Command = "sm.livemap.import" },
                RectTransform = { AnchorMin = "0.45 0.01", AnchorMax = "0.6 0.05" },
                Text = { Text = "Import Data", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, "ServerManagerContent");

            CuiHelper.AddUi(player, container);
        }

        // Live Map Tab Commands
        [ConsoleCommand("sm.livemap.test")]
        void CmdLiveMapTest(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            string mapPath = GetLiveMapImagePath();
            if (mapPath == null)
            {
                player.ChatMessage("<color=red>Map image error - check console for details.</color>");
                return;
            }

            // Ensure event mode for testing
            liveMapTeleportMode[player.userID] = false;

            CreateLiveMapUI(player, mapPath);
            
            NextTick(() => UpdateLiveMapDotsAndMarkers(player));

            Timer updateTimer = timer.Every(LiveMapUpdateInterval, () =>
            {
                if (player == null || !player.IsConnected) return;
                UpdateLiveMapDotsAndMarkers(player);
            });
            
            liveMapActiveTimers[player.userID] = updateTimer;
            player.ChatMessage("<color=green>Live map test opened successfully in EVENT mode!</color>");
        }

        [ConsoleCommand("sm.livemap.teleportmode")]
        void CmdLiveMapTeleportMode(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            string mapPath = GetLiveMapImagePath();
            if (mapPath == null)
            {
                player.ChatMessage("<color=red>Map image error - check console for details.</color>");
                return;
            }

            // Enable teleport mode
            liveMapTeleportMode[player.userID] = true;

            CreateLiveMapUI(player, mapPath);
            
            NextTick(() => UpdateLiveMapDotsAndMarkers(player));

            Timer updateTimer = timer.Every(LiveMapUpdateInterval, () =>
            {
                if (player == null || !player.IsConnected) return;
                UpdateLiveMapDotsAndMarkers(player);
            });
            
            liveMapActiveTimers[player.userID] = updateTimer;
            player.ChatMessage("<color=green>Live map opened in TELEPORT mode - click anywhere to teleport!</color>");
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

        [ConsoleCommand("sm.livemap.resetall")]
        void CmdLiveMapResetAll(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            liveMapTeleportMode.Clear();
            liveMapSingleMarker = null;
            
            player.ChatMessage("<color=green>All live map modes reset to event mode.</color>");
            OpenLiveMapTab(player);
        }

        [ConsoleCommand("sm.livemap.settings")]
        void CmdLiveMapSettings(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            player.ChatMessage("<color=yellow>=== Live Map Settings ===</color>");
            player.ChatMessage($"<color=green>Map Size:</color> {LiveMapSize}m x {LiveMapSize}m");
            player.ChatMessage($"<color=green>Update Interval:</color> {LiveMapUpdateInterval} seconds");
            player.ChatMessage($"<color=green>Max Players:</color> {LiveMapMaxPlayers}");
            player.ChatMessage($"<color=green>Grid Resolution:</color> {LiveMapGridResolution} x {LiveMapGridResolution}");
            player.ChatMessage($"<color=green>Active Users:</color> {liveMapActiveTimers.Count}");
            player.ChatMessage($"<color=green>Teleport Users:</color> {liveMapTeleportMode.Count(kvp => kvp.Value)}");
        }

        [ConsoleCommand("sm.livemap.export")]
        void CmdLiveMapExport(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            try
            {
                var exportData = new Dictionary<string, object>
                {
                    ["mapSize"] = LiveMapSize,
                    ["updateInterval"] = LiveMapUpdateInterval,
                    ["maxPlayers"] = LiveMapMaxPlayers,
                    ["gridResolution"] = LiveMapGridResolution,
                    ["teleportModes"] = liveMapTeleportMode.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
                    ["selectedMarker"] = liveMapSingleMarker.HasValue ? new { x = liveMapSingleMarker.Value.x, y = liveMapSingleMarker.Value.y } : null,
                    ["exportTime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                };

                string exportFile = $"LiveMapData_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                Interface.Oxide.DataFileSystem.WriteObject(exportFile, exportData);
                
                player.ChatMessage($"<color=green>Live map data exported to {exportFile}</color>");
                PrintWarning($"[ServerManager] Live map data exported: {exportFile}");
            }
            catch (Exception ex)
            {
                player.ChatMessage("<color=red>Export failed.</color>");
                PrintError($"[ServerManager] Export error: {ex.Message}");
            }
        }

        [ConsoleCommand("sm.livemap.import")]
        void CmdLiveMapImport(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player)) return;

            player.ChatMessage("<color=yellow>Live map import functionality would require file selection.</color>");
            player.ChatMessage("<color=yellow>Place import file in oxide/data/ and use console command 'sm.livemap.importfile filename'</color>");
        }

        // ===== DATA MANAGEMENT & CONFIGURATION =====

        void Loaded()
        {
            LoadData();
        }

        void Unload()
        {
            SaveData();
            
            // Clean up all active live map timers
            foreach (var timer in liveMapActiveTimers.Values)
            {
                timer?.Destroy();
            }
            liveMapActiveTimers.Clear();

            // Clean up all parachute timers
            foreach (var timer in parachuteFixedUpdateTimers.Values)
            {
                timer?.Destroy();
            }
            parachuteFixedUpdateTimers.Clear();

            // Clean up parachute entities
            foreach (var controller in activeParachutes.Values)
            {
                if (controller.chair != null && !controller.chair.IsDestroyed)
                {
                    controller.chair.Kill();
                }
            }
            activeParachutes.Clear();

            // Close all live map UIs
            foreach (var player in BasePlayer.activePlayerList)
            {
                CloseLiveMapView(player);
            }
        }

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

                // Load reputation config
                if (data.TryGetValue("repNpcPenaltyEnabled", out var npcPenalty))
                    bool.TryParse(npcPenalty.ToString(), out repNpcPenaltyEnabled);
                if (data.TryGetValue("repParachuteSpawnEnabled", out var paraSpawn))
                    bool.TryParse(paraSpawn.ToString(), out repParachuteSpawnEnabled);
                if (data.TryGetValue("repHudDisplayEnabled", out var hudDisplay))
                    bool.TryParse(hudDisplay.ToString(), out repHudDisplayEnabled);
                if (data.TryGetValue("repSafeZoneHostilityEnabled", out var safeZone))
                    bool.TryParse(safeZone.ToString(), out repSafeZoneHostilityEnabled);
                if (data.TryGetValue("repGatherBonusEnabled", out var gather))
                    bool.TryParse(gather.ToString(), out repGatherBonusEnabled);

                PrintWarning("[ServerManager] Configuration loaded successfully");
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
                    ["repNpcPenaltyEnabled"] = repNpcPenaltyEnabled,
                    ["repParachuteSpawnEnabled"] = repParachuteSpawnEnabled,
                    ["repHudDisplayEnabled"] = repHudDisplayEnabled,
                    ["repSafeZoneHostilityEnabled"] = repSafeZoneHostilityEnabled,
                    ["repGatherBonusEnabled"] = repGatherBonusEnabled
                };

                Interface.Oxide.DataFileSystem.WriteObject("ServerManager", data);
                PrintWarning("[ServerManager] Configuration saved successfully");
            }
            catch (Exception ex)
            {
                PrintError($"[ServerManager] Failed to save data: {ex.Message}");
            }
        }

        void Save() => SaveData();
		// ===== PLAYER HOOKS & UTILITIES =====

        void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            
            // Clean up any existing UI elements
            NextTick(() => {
                CuiHelper.DestroyUi(player, "ServerManagerMain");
                CuiHelper.DestroyUi(player, "ServerManagerContent");
                CloseLiveMapView(player);
            });

            // Initialize teleport mode setting
            if (!liveMapTeleportMode.ContainsKey(player.userID))
            {
                liveMapTeleportMode[player.userID] = false;
            }
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            
            // Clean up live map
            CloseLiveMapView(player);
            
            // Clean up parachute
            if (activeParachutes.ContainsKey(player.userID))
            {
                RemoveParachute(player);
            }
            
            // Clean up data
            liveMapLastUpdate.Remove(player.userID);
            liveMapTeleportMode.Remove(player.userID);
        }

        void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;

            // Check if reputation-based parachute spawn is enabled
            if (repParachuteSpawnEnabled && ReputationSystemHUD?.IsLoaded == true)
            {
                try
                {
                    int reputation = GetPlayerReputation(player);
                    
                    // High reputation players (75+) get automatic parachute option
                    if (reputation >= 75 && HasParachutePerm(player))
                    {
                        timer.Once(2f, () => {
                            if (player != null && player.IsConnected && !activeParachutes.ContainsKey(player.userID))
                            {
                                player.ChatMessage("<color=green>[High Reputation Bonus] Type /para to spawn a parachute!</color>");
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    PrintError($"[ServerManager] OnPlayerRespawned error: {ex.Message}");
                }
            }
        }

        void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;

            // Protect parachute chairs from damage
            if (entity is BaseChair chair)
            {
                foreach (var controller in activeParachutes.Values)
                {
                    if (controller.chair == chair)
                    {
                        info.damageTypes.ScaleAll(0f); // No damage to parachute chairs
                        return;
                    }
                }
            }

            // Handle reputation penalties for NPC kills
            if (repNpcPenaltyEnabled && info?.InitiatorPlayer != null && entity is BasePlayer target)
            {
                BasePlayer attacker = info.InitiatorPlayer;
                
                // Check if target is NPC/scientist
                if (target.IsNpc && ReputationSystemHUD?.IsLoaded == true)
                {
                    try
                    {
                        int currentRep = GetPlayerReputation(attacker);
                        int penalty = UnityEngine.Random.Range(1, 6); // 1-5 reputation penalty
                        int newRep = Mathf.Max(0, currentRep - penalty);
                        
                        if (SetPlayerReputation(attacker, newRep))
                        {
                            attacker.ChatMessage($"<color=red>-{penalty} Reputation for killing scientist ({newRep}/100)</color>");
                        }
                    }
                    catch (Exception ex)
                    {
                        PrintError($"[ServerManager] NPC kill penalty error: {ex.Message}");
                    }
                }
            }
        }

        void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || info == null) return;

            // Prevent attacking while in parachute
            if (activeParachutes.ContainsKey(attacker.userID))
            {
                var controller = activeParachutes[attacker.userID];
                if (controller.chair != null && controller.chair.GetMounted() == attacker)
                {
                    info.damageTypes.ScaleAll(0f);
                    attacker.ChatMessage("<color=yellow>Cannot attack while parachuting!</color>");
                }
            }
        }

        void OnPlayerSleep(BasePlayer player)
        {
            if (player == null) return;

            // Remove parachute when player goes to sleep
            if (activeParachutes.ContainsKey(player.userID))
            {
                RemoveParachute(player);
            }
        }

        void OnServerSave()
        {
            SaveData();
        }

        void OnServerShutdown()
        {
            SaveData();
            
            // Gracefully close all live maps and clean up
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.IsConnected == true)
                {
                    CloseLiveMapView(player);
                    if (activeParachutes.ContainsKey(player.userID))
                    {
                        RemoveParachute(player);
                    }
                }
            }
        }

        void OnNewSave(string filename)
        {
            PrintWarning("[ServerManager] New save detected - resetting plugin data");
            customKits.Clear();
            selectedEventPosition = Vector3.zero;
            liveMapSingleMarker = null;
            liveMapTeleportMode.Clear();
            SaveData();
        }

        // ===== ADMIN COMMANDS =====

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
            LoadReputationConfig();
            player.ChatMessage("<color=green>[ServerManager] Plugin reloaded successfully!</color>");
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
                liveMapSingleMarker = null;
                liveMapTeleportMode.Clear();
                
                // Reset reputation config
                repNpcPenaltyEnabled = true;
                repParachuteSpawnEnabled = true;
                repHudDisplayEnabled = true;
                repSafeZoneHostilityEnabled = true;
                repGatherBonusEnabled = true;

                SaveData();
                player.ChatMessage("<color=green>[ServerManager] All settings reset to defaults!</color>");
            }
            else
            {
                player.ChatMessage("<color=yellow>[ServerManager] Use '/smreset confirm' to reset all settings to defaults.</color>");
            }
        }

        [ChatCommand("smstatus")]
        void CmdStatus(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player))
            {
                player.ChatMessage("<color=red>No permission.</color>");
                return;
            }

            player.ChatMessage("<color=green>=== ServerManager Status ===</color>");
            int teleportCount = 0;
foreach (var kvp in liveMapTeleportMode) { if (kvp.Value) teleportCount++; }
player.ChatMessage($"<color=yellow>Teleport Mode Users:</color> {teleportCount}");
            player.ChatMessage($"<color=yellow>Decay Factor:</color> {decayFactor}");
            player.ChatMessage($"<color=yellow>Crate Unlock:</color> {crateUnlockTime} minutes");
            player.ChatMessage($"<color=yellow>Time Setting:</color> {(timeOfDay < 0 ? "Auto" : $"{timeOfDay}:00")}");
            player.ChatMessage($"<color=yellow>Custom Kits:</color> {customKits.Count} players");
            player.ChatMessage($"<color=yellow>Active Maps:</color> {liveMapActiveTimers.Count}");
            player.ChatMessage($"<color=yellow>Active Parachutes:</color> {activeParachutes.Count}");
            player.ChatMessage($"<color=yellow>Teleport Mode Users:</color> {liveMapTeleportMode.Where(kvp => kvp.Value).Count()}");
            player.ChatMessage($"<color=yellow>Reputation Plugin:</color> {(ReputationSystemHUD?.IsLoaded == true ? "Loaded" : "Not Loaded")}");
            player.ChatMessage($"<color=yellow>Zone Manager:</color> {(ZoneManager?.IsLoaded == true ? "Loaded" : "Not Loaded")}");
        }

        [ChatCommand("smhelp")]
        void CmdHelp(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player))
            {
                player.ChatMessage("<color=red>No permission.</color>");
                return;
            }

            player.ChatMessage("<color=green>=== ServerManager Help ===</color>");
            player.ChatMessage("<color=yellow>/sm</color> - Open main GUI");
            player.ChatMessage("<color=yellow>/para</color> - Spawn parachute (with permission)");
            player.ChatMessage("<color=yellow>/pararemove</color> - Remove your parachute");
            player.ChatMessage("<color=yellow>/smstatus</color> - Show plugin status");
            player.ChatMessage("<color=yellow>/smreload</color> - Reload plugin configuration");
            player.ChatMessage("<color=yellow>/smreset confirm</color> - Reset all settings");
            player.ChatMessage("<color=yellow>/smhelp</color> - Show this help");
            
            if (HasParachutePerm(player))
            {
                player.ChatMessage("<color=green>Parachute Controls:</color>");
                player.ChatMessage("• <color=white>W/S</color> - Speed control (0-70 km/h)");
                player.ChatMessage("• <color=white>A/D</color> - Heading control (rotate)");
                player.ChatMessage("• <color=white>Dismount</color> - Auto-remove parachute");
            }
        }

        [ChatCommand("smdebug")]
        void CmdDebug(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player))
            {
                player.ChatMessage("<color=red>No permission.</color>");
                return;
            }

            player.ChatMessage("<color=green>=== ServerManager Debug Info ===</color>");
            player.ChatMessage($"<color=yellow>Active Parachutes:</color> {activeParachutes.Count}");
            foreach (var kvp in activeParachutes)
            {
                var p = BasePlayer.FindByID(kvp.Key);
                var name = p?.displayName ?? "Unknown";
                player.ChatMessage($"  • {name}: Speed={kvp.Value.currentSpeed:F1} km/h, Active={kvp.Value.isActive}");
            }
            
            player.ChatMessage($"<color=yellow>Live Map Timers:</color> {liveMapActiveTimers.Count}");
            player.ChatMessage($"<color=yellow>Parachute Timers:</color> {parachuteFixedUpdateTimers.Count}");
            int teleportModeCount = 0;
foreach (var kvp in liveMapTeleportMode) { if (kvp.Value) teleportModeCount++; }
player.ChatMessage($"<color=yellow>Map Teleport Modes:</color> {teleportModeCount} enabled");
            
            if (liveMapSingleMarker.HasValue)
            {
                var marker = liveMapSingleMarker.Value;
                player.ChatMessage($"<color=yellow>Selected Event Marker:</color> X={marker.x:F1}, Z={marker.y:F1}");
            }
        }

        [ChatCommand("smcleanup")]
        void CmdCleanup(BasePlayer player, string command, string[] args)
        {
            if (!HasPerm(player))
            {
                player.ChatMessage("<color=red>No permission.</color>");
                return;
            }

            int cleanedParachutes = 0;
            int cleanedMaps = 0;
            int cleanedTimers = 0;

            // Clean up orphaned parachutes
            foreach (var kvp in activeParachutes.ToList())
            {
                var p = BasePlayer.FindByID(kvp.Key);
                if (p == null || !p.IsConnected || kvp.Value.chair == null || kvp.Value.chair.IsDestroyed)
                {
                    activeParachutes.Remove(kvp.Key);
                    cleanedParachutes++;
                }
            }

            // Clean up orphaned timers
            foreach (var kvp in parachuteFixedUpdateTimers.ToList())
            {
                if (!activeParachutes.ContainsKey(kvp.Key))
                {
                    kvp.Value?.Destroy();
                    parachuteFixedUpdateTimers.Remove(kvp.Key);
                    cleanedTimers++;
                }
            }

            // Clean up orphaned map timers
            foreach (var kvp in liveMapActiveTimers.ToList())
            {
                var p = BasePlayer.FindByID(kvp.Key);
                if (p == null || !p.IsConnected)
                {
                    kvp.Value?.Destroy();
                    liveMapActiveTimers.Remove(kvp.Key);
                    cleanedMaps++;
                }
            }

            player.ChatMessage($"<color=green>Cleanup completed:</color>");
            player.ChatMessage($"• <color=yellow>Parachutes:</color> {cleanedParachutes} removed");
            player.ChatMessage($"• <color=yellow>Map timers:</color> {cleanedMaps} removed");
            player.ChatMessage($"• <color=yellow>Parachute timers:</color> {cleanedTimers} removed");
        }

        // ===== CONSOLE COMMANDS FOR SERVER ADMINS =====

        [ConsoleCommand("sm.admin.massparachute")]
        void ConsoleMassParachute(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null) return; // Console only

            int spawned = 0;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (activeParachutes.ContainsKey(player.userID)) continue;

                SpawnParachuteChair(player);
                spawned++;
            }

            PrintWarning($"[ServerManager] Mass parachute spawn: {spawned} parachutes created");
        }

        [ConsoleCommand("sm.admin.clearmaps")]
        void ConsoleClearMaps(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null) return; // Console only

            int cleared = 0;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player?.IsConnected == true)
                {
                    CloseLiveMapView(player);
                    cleared++;
                }
            }

            PrintWarning($"[ServerManager] Cleared {cleared} active live maps");
        }

        [ConsoleCommand("sm.admin.info")]
        void ConsoleInfo(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null) return; // Console only

            PrintWarning("=== ServerManager Plugin Info ===");
            PrintWarning($"Version: 4.0.0 - Chair Parachute System");
            PrintWarning($"Active Parachutes: {activeParachutes.Count}");
            PrintWarning($"Active Live Maps: {liveMapActiveTimers.Count}");
            PrintWarning($"Custom Kits: {customKits.Count}");
            int consoleCount = 0;
foreach (var kvp in liveMapTeleportMode) { if (kvp.Value) consoleCount++; }
PrintWarning($"Teleport Mode Users: {consoleCount}");
            PrintWarning($"Reputation Plugin: {(ReputationSystemHUD?.IsLoaded == true ? "Loaded" : "Not Loaded")}");
            PrintWarning($"Zone Manager: {(ZoneManager?.IsLoaded == true ? "Loaded" : "Not Loaded")}");
        }

        // ===== ERROR HANDLING & FINAL CLEANUP =====

        void OnPluginLoaded(Plugin plugin)
        {
            if (plugin.Name == "ReputationSystemHUD")
            {
                PrintWarning("[ServerManager] ReputationSystemHUD plugin loaded - features available");
                LoadReputationConfig();
            }
            else if (plugin.Name == "ZoneManager")
            {
                PrintWarning("[ServerManager] ZoneManager plugin loaded - safe zone detection available");
            }
        }

        void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin.Name == "ReputationSystemHUD")
            {
                PrintWarning("[ServerManager] ReputationSystemHUD plugin unloaded - reputation features disabled");
            }
            else if (plugin.Name == "ZoneManager")
            {
                PrintWarning("[ServerManager] ZoneManager plugin unloaded - safe zone detection disabled");
            }
        }

        // Final error handling wrapper
        private void HandleError(string context, System.Action action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                PrintError($"[ServerManager] Error in {context}: {ex.Message}");
                PrintError($"[ServerManager] Stack trace: {ex.StackTrace}");
            }
        }

        // Plugin lifecycle completion
        void OnPlayerInitialized(BasePlayer player)
        {
            HandleError("OnPlayerInitialized", () => {
                if (player == null) return;
                
                // Initialize player data if needed
                if (!liveMapTeleportMode.ContainsKey(player.userID))
                {
                    liveMapTeleportMode[player.userID] = false;
                }
            });
        }

        // Memory cleanup and optimization
        void OnTick()
        {
            // Periodic cleanup every 5 minutes
            if (UnityEngine.Time.realtimeSinceStartup % 300f < 1f)
            {
                HandleError("PeriodicCleanup", () => {
                    // Clean up disconnected players from data structures
                    var disconnectedPlayers = liveMapTeleportMode.Keys
                        .Where(id => BasePlayer.FindByID(id) == null)
                        .ToList();
                        
                    foreach (var playerId in disconnectedPlayers)
                    {
                        liveMapTeleportMode.Remove(playerId);
                        liveMapLastUpdate.Remove(playerId);
                    }
                });
            }
        }
    }
}

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
    [Info("ServerManager", "YourName", "3.0.1")]
    [Description("Complete server management with live map, reputation, events, and environmental controls - FIXED")]
    public class ServerManager : RustPlugin
    {
        [PluginReference]
        private Plugin ReputationSystemHUD;

        [PluginReference]
        private Plugin ZoneManager;

        private const string permAdmin = "servermanager.admin";

        // Core Settings
        private float decayFactor = 1f;
        private Dictionary<ulong, Dictionary<string, int>> customKits = new Dictionary<ulong, Dictionary<string, int>>();
        private Vector3 selectedEventPosition = Vector3.zero;
        private string selectedGridCoordinate = "";
        private Dictionary<ulong, float> lastRadiationTime = new Dictionary<ulong, float>();

        // Live Map Integration
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

        // Reputation Config Cache (FIXED)
        private bool repNpcPenaltyEnabled = true;
        private float repInfidelSpeed = 0.6f;
        private float repSinnerSpeed = 0.8f;
        private float repAverageSpeed = 1.0f;
        private float repDiscipleSpeed = 1.3f;
        private float repProphetSpeed = 1.6f;
        private bool repMovementSpeedEnabled = true;
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
        };private readonly Dictionary<string, string> commonItems = new Dictionary<string, string>
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

        // FIXED: Reputation methods using correct API
        int GetPlayerReputation(BasePlayer player)
        {
            if (ReputationSystemHUD == null || !ReputationSystemHUD.IsLoaded) 
            {
                return 50;
            }
            
            try
            {
                // Use the correct API method name from ReputationSystemHUD
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
                // Use the correct API method name from ReputationSystemHUD
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
                // Load current config from ReputationSystemHUD plugin
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
        }void CreateLiveMapClickGrid(CuiElementContainer container, BasePlayer player)
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
                Save();
                
                player.ChatMessage($"<color=green>Event location confirmed: X={marker.x:F1}, Z={marker.y:F1}</color>");
                CloseLiveMapView(player);
                OpenEventsTab(player);
            }
            else
            {
                player.ChatMessage("<color=red>No location selected!</color>");
            }
        }// ===== MAIN COMMAND & TAB SYSTEM =====

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
                Text = { Text = "SERVER MANAGER v3.0.1 - FIXED", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
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

            // Add time controls inline
            AddTimeControlsToGeneralTab(container);

            CuiHelper.AddUi(player, container);
        }

        // Time of Day Section
        void AddTimeControlsToGeneralTab(CuiElementContainer container)
        {
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

// Extended time selection with sunrise/sunset
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
        }// ===== GENERAL TAB CONSOLE COMMANDS =====

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
        }// ===== EVENTS TAB =====

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

        // Event Commands
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
        }// ===== REPUTATION TAB (FIXED) =====

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
                
                // FIXED: Use correct API method
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

        // FIXED: Reputation Commands using correct API
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
        }// ===== REPUTATION CONFIG TAB (FIXED) =====

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

            // Just a simple header since we removed the speed controls
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

string[] features = { "NPC Kill Penalty", "Parachute Spawn", "HUD Display", "Safe Zone Hostility", "Gather Bonus" };
bool[] featureStates = { repNpcPenaltyEnabled, repParachuteSpawnEnabled, repHudDisplayEnabled, repSafeZoneHostilityEnabled, repGatherBonusEnabled };

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

        // FIXED: Reputation Config Commands using correct API
        [ConsoleCommand("sm.repconfig.speed")]
        void CmdRepConfigSpeed(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 2) return;

            if (!int.TryParse(arg.Args[0], out int tierIndex) || tierIndex < 0 || tierIndex > 4) return;
            if (!float.TryParse(arg.Args[1], out float change)) return;

            float[] speeds = { repInfidelSpeed, repSinnerSpeed, repAverageSpeed, repDiscipleSpeed, repProphetSpeed };
            speeds[tierIndex] = Mathf.Clamp(speeds[tierIndex] + change, 0.1f, 3.0f);

            repInfidelSpeed = speeds[0];
            repSinnerSpeed = speeds[1];
            repAverageSpeed = speeds[2];
            repDiscipleSpeed = speeds[3];
            repProphetSpeed = speeds[4];

            OpenReputationConfigTab(player);
        }

        [ConsoleCommand("sm.repconfig.speedset")]
        void CmdRepConfigSpeedSet(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !HasPerm(player) || arg.Args.Length < 2) return;

            if (!int.TryParse(arg.Args[0], out int tierIndex) || tierIndex < 0 || tierIndex > 4) return;
            if (!float.TryParse(arg.Args[1], out float speed)) return;

            speed = Mathf.Clamp(speed, 0.1f, 3.0f);

            switch (tierIndex)
            {
                case 0: repInfidelSpeed = speed; break;
                case 1: repSinnerSpeed = speed; break;
                case 2: repAverageSpeed = speed; break;
                case 3: repDiscipleSpeed = speed; break;
                case 4: repProphetSpeed = speed; break;
            }

            OpenReputationConfigTab(player);
        }

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
                    case 2: // Parachute Spawn
                        featureName = "parachutespawn";
                        repParachuteSpawnEnabled = !repParachuteSpawnEnabled;
                        var result2 = ReputationSystemHUD.Call("ToggleFeature", featureName, repParachuteSpawnEnabled);
                        success = result2 != null && result2 is bool s2 && s2;
                        break;
                    case 3: // HUD Display
                        featureName = "hud";
                        repHudDisplayEnabled = !repHudDisplayEnabled;
                        var result3 = ReputationSystemHUD.Call("ToggleFeature", featureName, repHudDisplayEnabled);
                        success = result3 != null && result3 is bool s3 && s3;
                        break;
                    case 4: // Safe Zone Hostility
                        featureName = "safezones";
                        repSafeZoneHostilityEnabled = !repSafeZoneHostilityEnabled;
                        var result4 = ReputationSystemHUD.Call("ToggleFeature", featureName, repSafeZoneHostilityEnabled);
                        success = result4 != null && result4 is bool s4 && s4;
                        break;
                    case 5: // Gather Bonus
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
                // FIXED: Use the correct API method from ReputationSystemHUD
                var result = ReputationSystemHUD.Call("UpdateSpeedSettings", 
                    repInfidelSpeed, repSinnerSpeed, repAverageSpeed, repDiscipleSpeed, repProphetSpeed);
                
                if (result != null && result is bool success && success)
                {
                    player.ChatMessage("<color=green>Speed settings applied successfully!</color>");
                    Save();
                }
                else
                {
                    player.ChatMessage("<color=yellow>Speed settings saved locally (API may not be available)</color>");
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

            repInfidelSpeed = 0.6f;
            repSinnerSpeed = 0.8f;
            repAverageSpeed = 1.0f;
            repDiscipleSpeed = 1.3f;
            repProphetSpeed = 1.6f;
            repNpcPenaltyEnabled = true;
            repMovementSpeedEnabled = true;
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
                if (data.TryGetValue("repInfidelSpeed", out var infSpeed))
                    float.TryParse(infSpeed.ToString(), out repInfidelSpeed);
                if (data.TryGetValue("repSinnerSpeed", out var sinSpeed))
                    float.TryParse(sinSpeed.ToString(), out repSinnerSpeed);
                if (data.TryGetValue("repAverageSpeed", out var avgSpeed))
                    float.TryParse(avgSpeed.ToString(), out repAverageSpeed);
                if (data.TryGetValue("repDiscipleSpeed", out var discSpeed))
                    float.TryParse(discSpeed.ToString(), out repDiscipleSpeed);
                if (data.TryGetValue("repProphetSpeed", out var propSpeed))
                    float.TryParse(propSpeed.ToString(), out repProphetSpeed);

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
                    ["repInfidelSpeed"] = repInfidelSpeed,
                    ["repSinnerSpeed"] = repSinnerSpeed,
                    ["repAverageSpeed"] = repAverageSpeed,
                    ["repDiscipleSpeed"] = repDiscipleSpeed,
                    ["repProphetSpeed"] = repProphetSpeed,
                    ["repNpcPenaltyEnabled"] = repNpcPenaltyEnabled,
                    ["repMovementSpeedEnabled"] = repMovementSpeedEnabled,
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
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            
            CloseLiveMapView(player);
            lastRadiationTime.Remove(player.userID);
            liveMapLastUpdate.Remove(player.userID);
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
                
                // Reset reputation config
                repInfidelSpeed = 0.6f;
                repSinnerSpeed = 0.8f;
                repAverageSpeed = 1.0f;
                repDiscipleSpeed = 1.3f;
                repProphetSpeed = 1.6f;
                repNpcPenaltyEnabled = true;
                repMovementSpeedEnabled = true;
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
            player.ChatMessage($"<color=yellow>Decay Factor:</color> {decayFactor}");
            player.ChatMessage($"<color=yellow>Crate Unlock:</color> {crateUnlockTime} minutes");
            player.ChatMessage($"<color=yellow>Time Setting:</color> {(timeOfDay < 0 ? "Auto" : $"{timeOfDay}:00")}");
            player.ChatMessage($"<color=yellow>Custom Kits:</color> {customKits.Count} players");
            player.ChatMessage($"<color=yellow>Active Maps:</color> {liveMapActiveTimers.Count}");
            player.ChatMessage($"<color=yellow>Reputation Plugin:</color> {(ReputationSystemHUD?.IsLoaded == true ? "Loaded" : "Not Loaded")}");
            player.ChatMessage($"<color=yellow>Zone Manager:</color> {(ZoneManager?.IsLoaded == true ? "Loaded" : "Not Loaded")}");
        }

        // ===== ERROR HANDLING & CLEANUP =====

        private void OnServerSave()
        {
            SaveData();
        }

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
            SaveData();
        }
    }
}
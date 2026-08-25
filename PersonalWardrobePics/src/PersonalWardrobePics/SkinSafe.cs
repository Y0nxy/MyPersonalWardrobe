using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace tinyWardrobe
{
    public static class SkinSafe
    {
        [System.Serializable]
        public class CachedPlayerEntry
        {
            public string steamId = "";
            public string nickName = "";
            public string lastUpdated = "";
            public Plugin.OutfitPreset outfit = new Plugin.OutfitPreset();
        }

        [System.Serializable]
        private class CacheWrapper
        {
            public List<CachedPlayerEntry> entries = new List<CachedPlayerEntry>();
        }

        private static Dictionary<string, CachedPlayerEntry> cacheMap = new Dictionary<string, CachedPlayerEntry>();
        private static bool isInitialized = false;

        // Path structure identical to the decompiled mod's method
        private static string BaseDirPath
        {
            get
            {
                string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dirPath = Path.Combine(appDataPath, "LandCrab", "PEAK", "tinyWardrobe");

                if (!Directory.Exists(dirPath))
                {
                    Directory.CreateDirectory(dirPath);
                }

                return dirPath;
            }
        }

        private static string SaveFilePath => Path.Combine(BaseDirPath, "playerCache.json");

        public static void EnsureInitialized()
        {
            if (isInitialized) return;
            isInitialized = true;
            LoadCache();
        }

        public static string GetPlayerIdentifier(Photon.Realtime.Player player)
        {
            if (player == null) return string.Empty;

            if (player.CustomProperties != null)
            {
                if (player.CustomProperties.TryGetValue("SteamID", out object steamIdObj) && steamIdObj != null)
                    return steamIdObj.ToString();

                if (player.CustomProperties.TryGetValue("SteamId", out object steamIdObj2) && steamIdObj2 != null)
                    return steamIdObj2.ToString();

                if (player.CustomProperties.TryGetValue("UserID", out object userIdObj) && userIdObj != null)
                    return userIdObj.ToString();
            }

            if (!string.IsNullOrEmpty(player.UserId)) return player.UserId;

            return $"Actor_{player.ActorNumber}_{player.NickName}";
        }

        public static void CachePlayerOutfit(Photon.Realtime.Player player, Plugin.OutfitPreset preset)
        {
            EnsureInitialized();
            if (player == null || preset == null) return;

            string id = GetPlayerIdentifier(player);
            if (string.IsNullOrEmpty(id)) return;

            if (preset.badgeData == null)
            {
                preset.badgeData = new bool[0];
            }

            CachedPlayerEntry entry = new CachedPlayerEntry
            {
                steamId = id,
                nickName = string.IsNullOrEmpty(player.NickName) ? "Unknown Player" : player.NickName,
                lastUpdated = DateTime.UtcNow.ToString("o"),
                outfit = preset
            };

            cacheMap[id] = entry;
            SaveCache();
        }

        public static List<CachedPlayerEntry> GetAllCachedPlayers()
        {
            EnsureInitialized();
            return new List<CachedPlayerEntry>(cacheMap.Values);
        }

        private static void LoadCache()
        {
            try
            {
                string path = SaveFilePath;
                if (!File.Exists(path)) return;

                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return;

                CacheWrapper wrapper = JsonUtility.FromJson<CacheWrapper>(json);

                cacheMap.Clear();
                if (wrapper != null && wrapper.entries != null)
                {
                    foreach (var entry in wrapper.entries)
                    {
                        if (entry != null && !string.IsNullOrEmpty(entry.steamId))
                        {
                            cacheMap[entry.steamId] = entry;
                        }
                    }
                }
                Plugin.Log?.LogInfo($"[SkinSafeCache] Loaded {cacheMap.Count} players from cache at: {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SkinSafeCache] Failed to load player cache JSON: {ex.Message}");
            }
        }

        private static void SaveCache()
        {
            try
            {
                CacheWrapper wrapper = new CacheWrapper
                {
                    entries = new List<CachedPlayerEntry>(cacheMap.Values)
                };

                string json = JsonUtility.ToJson(wrapper, true);
                string path = SaveFilePath;

                File.WriteAllText(path, json);
                Plugin.Log?.LogInfo($"[SkinSafeCache] Saved {cacheMap.Count} player entries to: {path}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SkinSafeCache] Failed to save player cache JSON: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(CharacterCustomization), "OnPlayerDataChange")]
    public static class OnPlayerDataChangePatch
    {
        [HarmonyPostfix]
        public static void Postfix(CharacterCustomization __instance, PersistentPlayerData playerData)
        {
            if (__instance == null || playerData == null || playerData.customizationData == null)
                return;

            PhotonView view = __instance.GetComponent<PhotonView>();
            Photon.Realtime.Player targetPlayer = (view != null) ? view.Owner : __instance.overridePhotonPlayer;

            if (targetPlayer == null)
                return;

            Plugin.OutfitPreset preset = new Plugin.OutfitPreset
            {
                customName = targetPlayer.NickName,
                skin = playerData.customizationData.currentSkin,
                eyes = playerData.customizationData.currentEyes,
                mouth = playerData.customizationData.currentMouth,
                accessory = playerData.customizationData.currentAccessory,
                outfit = playerData.customizationData.currentOutfit,
                hat = playerData.customizationData.currentHat,
                sash = playerData.customizationData.currentSash,
                badgeData = new bool[0],
                hasData = true
            };

            Character character = __instance.GetComponent<Character>();
            if (character != null && character.data != null && character.data.badgeStatus != null)
            {
                preset.badgeData = character.data.badgeStatus;
            }

            SkinSafe.CachePlayerOutfit(targetPlayer, preset);
        }
    }
}
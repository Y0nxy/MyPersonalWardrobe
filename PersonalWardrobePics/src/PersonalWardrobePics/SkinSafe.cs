using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace tinyWardrobe
{
    public static class SkinSafe
    {
        [System.Serializable]
        public class SavedOutfitData
        {
            public string nickName = "";
            public string lastUpdated = "";
            public int skin;
            public int eyes;
            public int mouth;
            public int accessory;
            public int outfit;
            public int hat;
            public int sash;
            public bool[] badgeData = new bool[0];
            public bool hasData = true;
        }

        private static Dictionary<string, SavedOutfitData> cacheMap = new Dictionary<string, SavedOutfitData>();
        private static bool isInitialized = false;

        public static GameObject safeUIRoot;
        private static TMP_InputField searchBar;
        private static Transform listContent;
        public static bool uiOpen = false;
        public static bool wasOpenBeforeMenuClosed = true;

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

        private static string SaveFilePath => Path.Combine(BaseDirPath, "SkinSafe.json");

        public static void EnsureInitialized()
        {
            if (isInitialized) return;
            isInitialized = true;
            LoadCache();
        }

        public static string GetSteamId(Photon.Realtime.Player player)
        {
            if (player == null) return string.Empty;

            if (TextChatCommands.Steam.TryGetSteamId(player, out ulong steamId) && steamId != 0UL)
            {
                return steamId.ToString();
            }

            return !string.IsNullOrEmpty(player.UserId) ? player.UserId : $"Actor_{player.ActorNumber}";
        }

        public static void CachePlayerOutfit(Photon.Realtime.Player player, SavedOutfitData outfitData)
        {
            EnsureInitialized();
            if (player == null || outfitData == null) return;

            if (outfitData.skin == 0 && outfitData.eyes == 0 && outfitData.mouth == 0 && outfitData.outfit == 0 && outfitData.hat == 0)
            {
                return;
            }

            string steamIdStr = GetSteamId(player);
            if (string.IsNullOrEmpty(steamIdStr)) return;

            if (outfitData.badgeData == null) outfitData.badgeData = new bool[0];
            outfitData.nickName = string.IsNullOrEmpty(player.NickName) ? "Unknown Player" : player.NickName;
            outfitData.lastUpdated = DateTime.UtcNow.ToString("o");

            cacheMap[steamIdStr] = outfitData;
            SaveCache();

            if (uiOpen) RefreshUIList();
        }

        public static void SaveAllLobbyPlayers()
        {
            EnsureInitialized();
            if (!PhotonNetwork.InRoom || PhotonNetwork.PlayerList == null) return;

            PersistentPlayerDataService service = GameHandler.GetService<PersistentPlayerDataService>();
            if (service == null) return;

            foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList)
            {
                if (player == null) continue;
                if (player == PhotonNetwork.LocalPlayer) continue; // FIXED LOGIC: Was skipping everyone BUT you

                PersistentPlayerData playerData = service.GetPlayerData(player);
                var cData = playerData?.customizationData;
                if (cData == null) continue;

                if (cData.currentSkin == 0 &&
                    cData.currentOutfit == 0 &&
                    cData.currentHat == 0)
                {
                    continue;
                }

                Character targetChar = Plugin.GetCharacterFromPlayer(player);
                bool[] currentBadges = null;
                if (targetChar?.data != null)
                {
                    currentBadges = targetChar.data.badgeStatus;
                }
                SavedOutfitData outfit = new SavedOutfitData
                {
                    nickName = player.NickName,
                    skin = cData.currentSkin,
                    eyes = cData.currentEyes,
                    mouth = cData.currentMouth,
                    accessory = cData.currentAccessory,
                    outfit = cData.currentOutfit,
                    hat = cData.currentHat,
                    sash = cData.currentSash,
                    badgeData = currentBadges,

                    hasData = true
                };

                CachePlayerOutfit(player, outfit);
            }
        }

        private static void SaveCache()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                int index = 0;
                foreach (KeyValuePair<string, SavedOutfitData> kvp in cacheMap)
                {
                    index++;
                    string innerJson = JsonUtility.ToJson(kvp.Value, true);
                    string indentedJson = "    " + innerJson.Replace("\n", "\n    ");
                    sb.Append($"  \"{kvp.Key}\": {indentedJson}");
                    if (index < cacheMap.Count) sb.AppendLine(",");
                    else sb.AppendLine();
                }
                sb.AppendLine("}");

                File.WriteAllText(SaveFilePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SkinSafe] Failed to save JSON: {ex.Message}");
            }
        }

        private static void LoadCache()
        {
            try
            {
                cacheMap.Clear();
                if (!File.Exists(SaveFilePath)) return;

                string rawJson = File.ReadAllText(SaveFilePath);
                if (string.IsNullOrWhiteSpace(rawJson)) return;

                Regex entryRegex = new Regex(@"\""(\d+|[^\r\n\""]+)\""\s*:\s*(\{[\s\S]*?\})(?=\s*,\s*\""|\s*\}\s*$)", RegexOptions.Compiled);
                MatchCollection matches = entryRegex.Matches(rawJson);

                foreach (Match m in matches)
                {
                    string steamIdKey = m.Groups[1].Value;
                    string objectBody = m.Groups[2].Value;

                    SavedOutfitData outfit = JsonUtility.FromJson<SavedOutfitData>(objectBody);
                    if (outfit != null)
                    {
                        cacheMap[steamIdKey] = outfit;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[SkinSafe] Failed to load JSON: {ex.Message}");
            }
        }

        public static void SetUIVisibility(bool visible)
        {
            if (safeUIRoot == null)
            {
                if (!visible) return;
                CreateSkinSafeUI();
            }

            uiOpen = visible;
            safeUIRoot.SetActive(uiOpen);

            if (uiOpen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                RefreshUIList();
            }
            else
            {
                if (!Plugin.Instance.enabled)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
            }
        }

        public static void ToggleUI()
        {
            SetUIVisibility(!uiOpen);
            if (uiOpen) wasOpenBeforeMenuClosed = false;
        }

        public static void EquipOutfit(SavedOutfitData preset)
        {
            if (preset == null) return;

            CharacterCustomization.SetCharacterSkinColor(preset.skin);
            CharacterCustomization.SetCharacterEyes(preset.eyes);
            CharacterCustomization.SetCharacterMouth(preset.mouth);
            CharacterCustomization.SetCharacterAccessory(preset.accessory);
            CharacterCustomization.SetCharacterOutfit(preset.outfit);
            CharacterCustomization.SetCharacterHat(preset.hat);
            CharacterCustomization.SetCharacterSash(preset.sash);

            if (preset.badgeData != null && preset.badgeData.Length > 0 && Character.localCharacter != null && Character.localCharacter.photonView != null)
            {
                Character.localCharacter.photonView.RPC("SyncBadgeStatus", RpcTarget.All, new object[] { preset.badgeData });
            }

            if (PassportManager.instance != null && PassportManager.instance.dummy != null)
            {
                PassportManager.instance.dummy.UpdateDummy(null);
            }

            Plugin.Log?.LogInfo($"[SkinSafe] Equipped vault outfit from player: {preset.nickName}");
        }

        private static void CreateSkinSafeUI()
        {
            GameObject canvasObj = new GameObject("SkinSafeCanvas");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10000;
            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();
            UnityEngine.Object.DontDestroyOnLoad(canvasObj);

            if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                GameObject eventSystem = new GameObject("EventSystem");
                eventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
                eventSystem.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                UnityEngine.Object.DontDestroyOnLoad(eventSystem);
            }

            safeUIRoot = new GameObject("SkinSafePanel");
            RectTransform panelRect = safeUIRoot.AddComponent<RectTransform>();
            panelRect.SetParent(canvasObj.transform, false);
            panelRect.sizeDelta = new Vector2(450, 550);

            panelRect.anchorMin = new Vector2(0, 0.5f);
            panelRect.anchorMax = new Vector2(0, 0.5f);
            panelRect.pivot = new Vector2(0, 0.5f);
            panelRect.anchoredPosition = new Vector2(20, 0);

            Image bg = safeUIRoot.AddComponent<Image>();
            bg.color = new Color(0.1f, 0.1f, 0.12f, 0.95f);

            GameObject titleObj = new GameObject("Title");
            RectTransform titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.SetParent(safeUIRoot.transform, false);
            titleRect.anchoredPosition = new Vector2(0, 240);
            titleRect.sizeDelta = new Vector2(400, 35);

            TextMeshProUGUI titleTxt = titleObj.AddComponent<TextMeshProUGUI>();
            titleTxt.text = "SKIN SAFE - OUTFIT VAULT";
            titleTxt.fontSize = 20;
            titleTxt.font = Plugin.GetFont();
            titleTxt.alignment = TextAlignmentOptions.Center;
            titleTxt.color = Color.cyan;

            GameObject searchObj = new GameObject("SearchBar");
            RectTransform searchRect = searchObj.AddComponent<RectTransform>();
            searchRect.SetParent(safeUIRoot.transform, false);
            searchRect.anchoredPosition = new Vector2(0, 195);
            searchRect.sizeDelta = new Vector2(410, 35);

            Image searchBg = searchObj.AddComponent<Image>();
            searchBg.color = new Color(0.2f, 0.2f, 0.25f, 1f);

            searchBar = searchObj.AddComponent<TMP_InputField>();
            GameObject searchTxtObj = new GameObject("Text");
            RectTransform searchTxtRect = searchTxtObj.AddComponent<RectTransform>();
            searchTxtRect.SetParent(searchObj.transform, false);
            searchTxtRect.anchorMin = Vector2.zero;
            searchTxtRect.anchorMax = Vector2.one;
            searchTxtRect.sizeDelta = Vector2.zero;

            TextMeshProUGUI searchText = searchTxtObj.AddComponent<TextMeshProUGUI>();
            searchText.fontSize = 16;
            searchText.font = Plugin.GetFont();
            searchText.color = Color.white;
            searchText.alignment = TextAlignmentOptions.Center;

            searchBar.textComponent = searchText;

            GameObject phObj = new GameObject("Placeholder");
            RectTransform phRect = phObj.AddComponent<RectTransform>();
            phRect.SetParent(searchObj.transform, false);
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.sizeDelta = Vector2.zero;

            TextMeshProUGUI phText = phObj.AddComponent<TextMeshProUGUI>();
            phText.text = "Search by Name or Steam ID...";
            phText.fontSize = 16;
            phText.font = Plugin.GetFont();
            phText.fontStyle = FontStyles.Italic;
            phText.color = new Color(0.6f, 0.6f, 0.6f, 0.7f);
            phText.alignment = TextAlignmentOptions.Center;
            searchBar.placeholder = phText;
            searchBar.onValueChanged.AddListener((_) => RefreshUIList());

            // --- NEW RANDOM BUTTON ---
            GameObject rndBtnObj = new GameObject("RandomButton");
            RectTransform rndRect = rndBtnObj.AddComponent<RectTransform>();
            rndRect.SetParent(safeUIRoot.transform, false);
            rndRect.anchoredPosition = new Vector2(185, 240);
            rndRect.sizeDelta = new Vector2(30, 30);

            Image rndImgOuter = rndBtnObj.AddComponent<Image>();
            rndImgOuter.color = Color.cyan; // Cyan border to match title

            GameObject rndInner = new GameObject("Inner");
            RectTransform rndInnerRect = rndInner.AddComponent<RectTransform>();
            rndInnerRect.SetParent(rndBtnObj.transform, false);
            rndInnerRect.anchorMin = Vector2.zero;
            rndInnerRect.anchorMax = Vector2.one;
            rndInnerRect.sizeDelta = new Vector2(-4, -4);
            Image rndInnerImg = rndInner.AddComponent<Image>();
            rndInnerImg.color = new Color(0.15f, 0.15f, 0.18f);

            Button rndBtn = rndBtnObj.AddComponent<Button>();
            rndBtn.onClick.AddListener(() => {
                if (cacheMap.Count > 0)
                {
                    List<string> keys = new List<string>(cacheMap.Keys);
                    string rndKey = keys[UnityEngine.Random.Range(0, keys.Count)];
                    if (searchBar != null) searchBar.text = rndKey; // Setting this automatically triggers the UI filter
                    EquipOutfit(cacheMap[rndKey]);
                }
            });

            GameObject rndTxtObj = new GameObject("RndText");
            RectTransform rndTxtRect = rndTxtObj.AddComponent<RectTransform>();
            rndTxtRect.SetParent(rndBtnObj.transform, false);
            rndTxtRect.anchorMin = Vector2.zero;
            rndTxtRect.anchorMax = Vector2.one;
            rndTxtRect.sizeDelta = Vector2.zero;
            TextMeshProUGUI rndTxt = rndTxtObj.AddComponent<TextMeshProUGUI>();
            rndTxt.text = "?";
            rndTxt.fontSize = 20;
            rndTxt.font = Plugin.GetFont();
            rndTxt.color = Color.cyan;
            rndTxt.alignment = TextAlignmentOptions.Center;

            // --- END NEW RANDOM BUTTON ---

            GameObject scrollObj = new GameObject("ScrollView");
            RectTransform scrollRect = scrollObj.AddComponent<RectTransform>();
            scrollRect.SetParent(safeUIRoot.transform, false);
            scrollRect.anchoredPosition = new Vector2(0, -30);
            scrollRect.sizeDelta = new Vector2(410, 400);

            ScrollRect scrollRectComp = scrollObj.AddComponent<ScrollRect>();
            scrollRectComp.scrollSensitivity = 20f; // SCROLL WHEEL FIX
            scrollObj.AddComponent<RectMask2D>();

            GameObject contentObj = new GameObject("Content");
            RectTransform contentRect = contentObj.AddComponent<RectTransform>();
            contentRect.SetParent(scrollObj.transform, false);
            contentRect.anchorMin = new Vector2(0.5f, 1f);
            contentRect.anchorMax = new Vector2(0.5f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(400, 0);

            VerticalLayoutGroup layout = contentObj.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlHeight = false;
            layout.childControlWidth = true;

            ContentSizeFitter fitter = contentObj.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRectComp.content = contentRect;
            scrollRectComp.horizontal = false;
            scrollRectComp.vertical = true;
            listContent = contentRect.transform;

            safeUIRoot.SetActive(false);
        }

        private static void RefreshUIList()
        {
            if (listContent == null) return;

            foreach (Transform child in listContent)
            {
                UnityEngine.Object.Destroy(child.gameObject);
            }

            string filter = searchBar != null ? searchBar.text.ToLower() : "";

            foreach (KeyValuePair<string, SavedOutfitData> kvp in cacheMap)
            {
                string steamId = kvp.Key;
                SavedOutfitData data = kvp.Value;
                string cleanName = Regex.Replace(data.nickName.ToLower(), @"</?color(=\w+|=[#\w]+)?>", string.Empty, RegexOptions.IgnoreCase);
                if (!string.IsNullOrEmpty(filter) &&
                    !cleanName.Contains(filter) &&
                    !steamId.ToLower().Contains(filter))
                {
                    continue;
                }

                GameObject entryObj = new GameObject($"Entry_{steamId}");
                RectTransform entryRect = entryObj.AddComponent<RectTransform>();
                entryRect.SetParent(listContent, false);
                entryRect.sizeDelta = new Vector2(390, 45);

                Image entryBg = entryObj.AddComponent<Image>();
                entryBg.color = new Color(0.18f, 0.18f, 0.22f, 1f);
                entryBg.raycastTarget = true;

                Button entryBtn = entryObj.AddComponent<Button>();
                ColorBlock cb = entryBtn.colors;
                cb.normalColor = new Color(0.18f, 0.18f, 0.22f, 1f);
                cb.highlightedColor = new Color(0.28f, 0.35f, 0.45f, 1f);
                cb.pressedColor = new Color(0.12f, 0.5f, 0.4f, 1f);
                entryBtn.colors = cb;

                SavedOutfitData outfitToEquip = data;
                entryBtn.onClick.AddListener(() => {
                    EquipOutfit(outfitToEquip);
                });

                GameObject labelObj = new GameObject("Label");
                RectTransform labelRect = labelObj.AddComponent<RectTransform>();
                labelRect.SetParent(entryObj.transform, false);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.sizeDelta = Vector2.zero;

                TextMeshProUGUI labelText = labelObj.AddComponent<TextMeshProUGUI>();
                labelText.text = $"{data.nickName}\n<size=12><color=#AAAAAA>ID: {steamId}</color></size>";
                labelText.fontSize = 15;
                labelText.font = Plugin.GetFont();
                labelText.alignment = TextAlignmentOptions.Center;
                labelText.color = Color.white;

                // --- NEW DELETE BUTTON ---
                GameObject delBtnObj = new GameObject("DeleteBtn");
                RectTransform delRect = delBtnObj.AddComponent<RectTransform>();
                delRect.SetParent(entryObj.transform, false);
                delRect.anchoredPosition = new Vector2(186, 7); // Positioned inside the left edge
                delRect.sizeDelta = new Vector2(24, 24);

                Image delImgOuter = delBtnObj.AddComponent<Image>();
                delImgOuter.color = new Color(0.8f, 0.2f, 0.2f); // The red border

                GameObject delInner = new GameObject("Inner");
                RectTransform innerRect = delInner.AddComponent<RectTransform>();
                innerRect.SetParent(delBtnObj.transform, false);
                innerRect.anchorMin = Vector2.zero;
                innerRect.anchorMax = Vector2.one;
                innerRect.sizeDelta = new Vector2(-4, -4); // Insets by 2 pixels to reveal border
                Image innerImg = delInner.AddComponent<Image>();
                innerImg.color = new Color(0.12f, 0.12f, 0.12f);

                Button delBtn = delBtnObj.AddComponent<Button>();
                delBtn.onClick.AddListener(() => {
                    cacheMap.Remove(steamId);
                    SaveCache();
                    RefreshUIList();
                });

                GameObject delTxtObj = new GameObject("Text");
                RectTransform delTxtRect = delTxtObj.AddComponent<RectTransform>();
                delTxtRect.SetParent(delBtnObj.transform, false);
                delTxtRect.anchorMin = Vector2.zero;
                delTxtRect.anchorMax = Vector2.one;
                delTxtRect.sizeDelta = Vector2.zero;
                TextMeshProUGUI delTxt = delTxtObj.AddComponent<TextMeshProUGUI>();
                delTxt.text = "X";
                delTxt.fontSize = 16;
                delTxt.font = Plugin.GetFont();
                delTxt.color = new Color(0.8f, 0.2f, 0.2f);
                delTxt.alignment = TextAlignmentOptions.Midline;
                delTxt.raycastTarget = false;
            }
        }
    }

    [HarmonyPatch(typeof(CharacterCustomization), "OnPlayerDataChange")]
    public static class SkinSafeOutfitChangePatch
    {
        [HarmonyPostfix]
        public static void Postfix(CharacterCustomization __instance, PersistentPlayerData playerData)
        {
            if (__instance == null || playerData == null || playerData.customizationData == null) return;

            PhotonView view = __instance.GetComponent<PhotonView>();
            Photon.Realtime.Player targetPlayer = (view != null) ? view.Owner : __instance.overridePhotonPlayer;
            if (targetPlayer == null) return;

            Character targetChar = __instance._character;
            bool[] currentBadges = null;
            if (targetChar?.data != null)
            {
                currentBadges = targetChar.data.badgeStatus;
            }

            SkinSafe.SavedOutfitData outfit = new SkinSafe.SavedOutfitData
            {
                nickName = targetPlayer.NickName,
                skin = playerData.customizationData.currentSkin,
                eyes = playerData.customizationData.currentEyes,
                mouth = playerData.customizationData.currentMouth,
                accessory = playerData.customizationData.currentAccessory,
                outfit = playerData.customizationData.currentOutfit,
                hat = playerData.customizationData.currentHat,
                sash = playerData.customizationData.currentSash,
                badgeData = currentBadges,
                hasData = true
            };

            SkinSafe.CachePlayerOutfit(targetPlayer, outfit);
        }
    }

    [HarmonyPatch(typeof(MonoBehaviourPunCallbacks), "OnJoinedRoom")]
    public static class SkinSafeLobbyJoinPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            SkinSafe.SaveAllLobbyPlayers();
        }
    }
}
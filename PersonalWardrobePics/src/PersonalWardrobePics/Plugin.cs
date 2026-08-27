using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TextChatCommands;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Zorro.Core;

namespace tinyWardrobe
{
    [BepInAutoPlugin]
    public partial class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; } = null!;

        private const int SLOTS_PER_PAGE = 8;

        private static GameObject menuParent;
        private Transform cardGridContainer;
        private TextMeshProUGUI pageIndicatorText;

        private bool isTyping = false;

        private List<Image> buttonBorders = new List<Image>();
        private List<Image> maskImages = new List<Image>();
        private List<RawImage> portraitImages = new List<RawImage>();
        private List<TMP_InputField> slotNameInputs = new List<TMP_InputField>();
        private List<GameObject> activeCardObjects = new List<GameObject>();

        // --- CACHE & ASYNC VARIABLES ---
        private Dictionary<int, Texture2D> thumbnailCache = new Dictionary<int, Texture2D>();
        private RenderTexture tempRenderTex;
        private Queue<int> thumbnailRenderQueue = new Queue<int>();
        private bool isRenderingThumbnails = false;
        private bool hasPreloadedThumbnails = false;

        private int currentPage = 0;
        private int selectedLoadout = 0;

        private Sprite cardSprite;
        private Sprite maskSprite;
        private const int CornerRadius = 26;

        private GameObject playerListPanel;
        private Transform playerListContent;
        private int slotTargetForSteal = -1;

        [System.Serializable]
        public class OutfitPreset
        {
            public string customName = "";
            public int skin;
            public int eyes;
            public int mouth;
            public int accessory;
            public int outfit;
            public int hat;
            public int sash;
            public bool[] badgeData = new bool[0];
            public int medal;
            public bool hasData;
        }

        private List<OutfitPreset> savedPresets = new List<OutfitPreset>();
        private ConfigEntry<string> savedPresetsConfig;

        private ConfigEntry<KeyCode> toggleKeyConfig;
        private ConfigEntry<bool> copyBadgesConfig;

        private Color activeColor = new Color(0.6f, 1f, 0.6f);
        private Color inactiveColor = new Color(0.9f, 0.88f, 0.82f);

        private GameObject renderRig;
        private Camera rigCamera;
        private static TMP_FontAsset peakFont = null;
        static Harmony harmony;
        private static bool blockInput = false;

        public static TMP_FontAsset GetFont()
        {
            if (peakFont == null)
            {
                TMP_FontAsset[] fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                peakFont = Array.Find(fonts, f => f.name == "DarumaDropOne-Regular SDF");
            }
            return peakFont;
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            toggleKeyConfig = Config.Bind("General", "MenuToggleKey", KeyCode.F9, "Keybind to open the wardrobe menu.");
            copyBadgesConfig = Config.Bind("General", "CopyBadges", true, "Whether to steal badge status when cloning an outfit.");

            savedPresetsConfig = Config.Bind("General", "SavedPresetsDataList_Pages", "", "Flat list representation containing saved outfit presets data.");
            LoadPresetsFromConfig();

            EnsureMinimumSlots(SLOTS_PER_PAGE);

            cardSprite = GenerateProceduralRoundedSprite(256, 256, CornerRadius);
            maskSprite = GenerateProceduralRoundedSprite(256, 256, CornerRadius);
            SceneManager.sceneLoaded += (Scene, _) => StartCoroutine(SceneLoaded(Scene));
            harmony = new Harmony(Name);
            harmony.PatchAll();
            SkinSafe.EnsureInitialized();
        }

        IEnumerator SceneLoaded(Scene scene)
        {
            if (scene.name != "Airpor") yield break;
            if (menuParent != null) yield break;
            yield return new WaitForSeconds(5f);
            Plugin.Instance.CreateWardrobeUI();
            Log.LogInfo("Trying to load everything, Scene no longer catching");
            SceneManager.sceneLoaded -= (Scene, _) => StartCoroutine(SceneLoaded(Scene));
        }

        void OnDestroy()
        {
            // Memory cleanup
            foreach (var kvp in thumbnailCache)
            {
                if (kvp.Value != null) Destroy(kvp.Value);
            }
            thumbnailCache.Clear();

            if (tempRenderTex != null)
            {
                tempRenderTex.Release();
                Destroy(tempRenderTex);
            }

            Destroy(menuParent);
            harmony.UnpatchSelf();
            if (SkinSafe.safeUIRoot != null)
                Destroy(SkinSafe.safeUIRoot);
        }
        [HarmonyPatch]
        public static class Patches
        {
            [HarmonyPatch(typeof(GUIManager), "UpdateWindowStatus")]
            [HarmonyPostfix]
            public static void UpdateWindowStatusPatch(GUIManager __instance)
            {
                if ((menuParent != null && blockInput) || SkinSafe.uiOpen)
                {
                    __instance.windowShowingCursor = true;
                    __instance.windowBlockingInput = true;
                }
            }
        }

        private void EnsureMinimumSlots(int totalRequired)
        {
            while (savedPresets.Count < totalRequired)
            {
                savedPresets.Add(new OutfitPreset { customName = $"Slot {savedPresets.Count + 1}" });
            }
        }

        private void CheckAndExpandSlots(int modifiedIndex)
        {
            if (modifiedIndex >= savedPresets.Count - 1)
            {
                EnsureMinimumSlots(savedPresets.Count + SLOTS_PER_PAGE);
            }
        }

        private void Update()
        {
            bool pressedEsc = Input.GetKeyDown(KeyCode.Escape);
            if (!Input.GetKeyDown(toggleKeyConfig.Value) && !pressedEsc)
                return;
            if (pressedEsc)
            {
                if (menuParent == null) return;
                bool isOpen = menuParent.activeSelf;
                if (isOpen)
                {
                    menuParent.SetActive(false);
                    ToggleMenuState(false);
                }
                return;
            }
            if (menuParent == null)
            {
                CreateWardrobeUI();
                ToggleMenuState(true);
            }
            else
            {
                bool isOpening = !menuParent.activeSelf;
                menuParent.SetActive(isOpening);
                ToggleMenuState(isOpening);
            }
        }

        private void LateUpdate()
        {
            if ((menuParent != null && menuParent.activeSelf && !isTyping) || SkinSafe.uiOpen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void ToggleMenuState(bool open)
        {
            if (open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                blockInput = true;
                
                RenderCurrentPage();

                // Start background loading of all textures on first open
                if (!hasPreloadedThumbnails)
                {
                    hasPreloadedThumbnails = true;
                    for (int i = 0; i < savedPresets.Count; i++)
                    {
                        if (savedPresets[i].hasData && !thumbnailCache.ContainsKey(i))
                        {
                            EnqueueThumbnailRender(i);
                        }
                    }
                }

                if (SkinSafe.wasOpenBeforeMenuClosed)
                {
                    SkinSafe.SetUIVisibility(true);
                }
            }
            else
            {
                if (SkinSafe.uiOpen)
                {
                    SkinSafe.wasOpenBeforeMenuClosed = true;
                    SkinSafe.SetUIVisibility(false);
                }
                else
                {
                    SkinSafe.wasOpenBeforeMenuClosed = false;
                }

                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                isTyping = false;
                blockInput = false;

                // Don't destroy the render rig if it's currently processing thumbnails in the background
                if (!isRenderingThumbnails)
                {
                    DestroyRenderRig();
                }

                if (playerListPanel != null) playerListPanel.SetActive(false);
            }
        }

        private Sprite GenerateProceduralRoundedSprite(int width, int height, int radius)
        {
            if (radius <= 0) radius = 1;

            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;

            Color[] colors = new Color[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float alpha = 1f;
                    Vector2 pixelPos = new Vector2(x + 0.5f, y + 0.5f);
                    Vector2 center = Vector2.zero;
                    bool isCorner = false;

                    if (x < radius && y < radius) { isCorner = true; center = new Vector2(radius, radius); }
                    else if (x >= width - radius && y < radius) { isCorner = true; center = new Vector2(width - radius, radius); }
                    else if (x < radius && y >= height - radius) { isCorner = true; center = new Vector2(radius, height - radius); }
                    else if (x >= width - radius && y >= height - radius) { isCorner = true; center = new Vector2(width - radius, height - radius); }

                    if (isCorner)
                    {
                        float dist = Vector2.Distance(pixelPos, center);
                        alpha = Mathf.Clamp01(radius - dist + 0.5f);
                    }

                    colors[y * width + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels(colors);
            texture.Apply();

            Vector4 borders = new Vector4(radius, radius, radius, radius);
            return Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, borders);
        }

        private void SetUILayerRecursive(GameObject obj, int layer)
        {
            obj.layer = layer;
            foreach (Transform child in obj.transform)
            {
                SetUILayerRecursive(child.gameObject, layer);
            }
        }

        private void CreateWardrobeUI()
        {
            GameObject canvasObj = new GameObject("WardrobeCanvas");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;

            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();

            if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                GameObject eventSystem = new GameObject("EventSystem");
                eventSystem.AddComponent<UnityEngine.EventSystems.EventSystem>();
                eventSystem.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                DontDestroyOnLoad(eventSystem);
            }

            DontDestroyOnLoad(canvasObj);
            menuParent = canvasObj;

            GameObject bgObj = new GameObject("Background");
            RectTransform bgRect = bgObj.AddComponent<RectTransform>();
            bgRect.SetParent(canvasObj.transform, false);

            Image bgImage = bgObj.AddComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0.75f);
            bgImage.raycastTarget = true;
            StretchToFill(bgRect);

            GameObject titleObj = new GameObject("TitleText");
            RectTransform titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.SetParent(canvasObj.transform, false);

            TextMeshProUGUI titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = "tinyPresets?";
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.fontSize = 50;
            titleText.font = GetFont();
            titleText.color = Color.white;

            titleRect.anchoredPosition = new Vector2(0, 430);
            titleRect.sizeDelta = new Vector2(1000, 40);

            GameObject subtitleObj = new GameObject("SubtitleText");
            RectTransform subtitleRect = subtitleObj.AddComponent<RectTransform>();
            subtitleRect.SetParent(canvasObj.transform, false);

            TextMeshProUGUI subtitleText = subtitleObj.AddComponent<TextMeshProUGUI>();
            subtitleText.text = "SHIFT CLICK - SAVE | RIGHT CLICK - COPY";
            subtitleText.alignment = TextAlignmentOptions.Center;
            subtitleText.fontSize = 40;
            subtitleText.font = GetFont();
            subtitleText.color = new Color(0.8f, 0.8f, 0.8f, 0.9f);

            subtitleRect.anchoredPosition = new Vector2(0, 395);
            subtitleRect.sizeDelta = new Vector2(1000, 30);

            CreateButton(canvasObj.transform, "SKIN SAFE", new Vector2(420, 430), new Vector2(140, 45), () => {
                SkinSafe.ToggleUI();
            });

            GameObject gridObj = new GameObject("CardGridContainer");
            RectTransform gridRect = gridObj.AddComponent<RectTransform>();
            gridRect.SetParent(canvasObj.transform, false);
            gridRect.anchoredPosition = Vector2.zero;
            cardGridContainer = gridRect.transform;

            CreatePaginationControls(canvasObj.transform);
            CreatePlayerSelectionMenu(canvasObj.transform);
            SetUILayerRecursive(canvasObj, 5);
        }

        private void CreatePaginationControls(Transform parent)
        {
            GameObject navPanel = new GameObject("NavigationPanel");
            RectTransform navRect = navPanel.AddComponent<RectTransform>();
            navRect.SetParent(parent, false);
            navRect.anchoredPosition = new Vector2(0, -380);
            navRect.sizeDelta = new Vector2(600, 50);

            CreateButton(navPanel.transform, "< PREV", new Vector2(-150, 0), new Vector2(120, 40), () => {
                if (currentPage > 0)
                {
                    currentPage--;
                    RenderCurrentPage();
                }
            });

            GameObject pageTxtObj = new GameObject("PageText");
            RectTransform pageTxtRect = pageTxtObj.AddComponent<RectTransform>();
            pageTxtRect.SetParent(navPanel.transform, false);
            pageTxtRect.anchoredPosition = Vector2.zero;
            pageTxtRect.sizeDelta = new Vector2(180, 40);

            pageIndicatorText = pageTxtObj.AddComponent<TextMeshProUGUI>();
            pageIndicatorText.alignment = TextAlignmentOptions.Center;
            pageIndicatorText.fontSize = 20;
            pageIndicatorText.font = GetFont();
            pageIndicatorText.color = Color.white;

            CreateButton(navPanel.transform, "NEXT >", new Vector2(150, 0), new Vector2(120, 40), () => {
                int maxPages = Mathf.CeilToInt((float)savedPresets.Count / SLOTS_PER_PAGE);
                if (currentPage < maxPages - 1)
                {
                    currentPage++;
                    RenderCurrentPage();
                }
            });
        }

        private GameObject CreateButton(Transform parent, string text, Vector2 pos, Vector2 size, System.Action onClick)
        {
            GameObject btnObj = new GameObject($"Btn_{text}");
            RectTransform rect = btnObj.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;

            Image img = btnObj.AddComponent<Image>();
            img.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            if (cardSprite != null) { img.sprite = cardSprite; img.type = Image.Type.Sliced; }

            Button btn = btnObj.AddComponent<Button>();
            btn.onClick.AddListener(() => onClick?.Invoke());

            GameObject txtObj = new GameObject("Text");
            RectTransform txtRect = txtObj.AddComponent<RectTransform>();
            txtRect.SetParent(btnObj.transform, false);
            StretchToFill(txtRect);

            TextMeshProUGUI txt = txtObj.AddComponent<TextMeshProUGUI>();
            txt.text = text;
            txt.fontSize = 20;
            txt.font = GetFont();
            txt.alignment = TextAlignmentOptions.Center;
            txt.color = Color.white;

            return btnObj;
        }

        private void RenderCurrentPage()
        {
            foreach (var card in activeCardObjects)
            {
                if (card != null) Destroy(card);
            }

            activeCardObjects.Clear();
            buttonBorders.Clear();
            maskImages.Clear();
            portraitImages.Clear();
            slotNameInputs.Clear();

            int totalPages = Mathf.Max(1, Mathf.CeilToInt((float)savedPresets.Count / SLOTS_PER_PAGE));
            currentPage = Mathf.Clamp(currentPage, 0, totalPages - 1);

            if (pageIndicatorText != null)
            {
                pageIndicatorText.text = $"Page {currentPage + 1} / {totalPages}";
            }

            float[] xPositions = { -375f, -125f, 125f, 375f };
            float[] yPositions = { 180f, -100f };

            int startIndex = currentPage * SLOTS_PER_PAGE;

            for (int i = 0; i < SLOTS_PER_PAGE; i++)
            {
                int globalIndex = startIndex + i;
                if (globalIndex >= savedPresets.Count) break;

                int row = i / 4;
                int col = i % 4;

                CreateLoadoutCard(cardGridContainer, xPositions[col], yPositions[row], globalIndex, i);
            }

            UpdateBorders();
            RefreshVisibleThumbnails();
            SetUILayerRecursive(menuParent, 5);
        }

        private void CreateLoadoutCard(Transform parent, float xPos, float yPos, int globalIndex, int localIndex)
        {
            GameObject cardObj = new GameObject($"LoadoutCard_{globalIndex + 1}");
            RectTransform cardRect = cardObj.AddComponent<RectTransform>();
            cardRect.SetParent(parent, false);
            cardRect.sizeDelta = new Vector2(210, 230);
            cardRect.anchoredPosition = new Vector2(xPos, yPos);
            activeCardObjects.Add(cardObj);

            Image borderImg = cardObj.AddComponent<Image>();
            borderImg.color = inactiveColor;
            borderImg.raycastTarget = true;

            if (cardSprite != null)
            {
                borderImg.sprite = cardSprite;
                borderImg.type = Image.Type.Sliced;
            }
            buttonBorders.Add(borderImg);

            CustomClickHandler clickHandler = cardObj.AddComponent<CustomClickHandler>();
            clickHandler.OnLeftClick += () => {
                if (localIndex < slotNameInputs.Count && slotNameInputs[localIndex] != null && slotNameInputs[localIndex].isFocused) return;

                selectedLoadout = globalIndex;
                UpdateBorders();

                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                {
                    SaveCurrentOutfitToPreset(globalIndex);
                    RenderCurrentPage(); // Refresh UI to trigger queue generation for the new preset
                }
                else
                {
                    EquipPreset(globalIndex);
                }
            };

            clickHandler.OnRightClick += () => {
                selectedLoadout = globalIndex;
                UpdateBorders();
                OpenPlayerSelectionMenu(globalIndex);
            };

            GameObject maskObj = new GameObject("PortraitMask");
            RectTransform maskRect = maskObj.AddComponent<RectTransform>();
            maskRect.SetParent(cardObj.transform, false);
            maskRect.anchorMin = new Vector2(0.06f, 0.06f);
            maskRect.anchorMax = new Vector2(0.94f, 0.94f);
            maskRect.sizeDelta = Vector2.zero;

            Image maskImg = maskObj.AddComponent<Image>();
            maskImg.raycastTarget = false;
            if (maskSprite != null) { maskImg.sprite = maskSprite; maskImg.type = Image.Type.Sliced; }
            maskImages.Add(maskImg);

            Mask uiMask = maskObj.AddComponent<Mask>();
            uiMask.showMaskGraphic = false;

            GameObject innerImgObj = new GameObject("PortraitView");
            RectTransform innerRect = innerImgObj.AddComponent<RectTransform>();
            innerRect.SetParent(maskObj.transform, false);
            StretchToFill(innerRect);

            RawImage rawImg = innerImgObj.AddComponent<RawImage>();
            rawImg.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            rawImg.raycastTarget = false;
            portraitImages.Add(rawImg);

            GameObject inputObj = new GameObject("SlotNameInput");
            RectTransform inputRect = inputObj.AddComponent<RectTransform>();
            inputRect.SetParent(cardObj.transform, false);
            inputRect.anchoredPosition = new Vector2(0, -140);
            inputRect.sizeDelta = new Vector2(210, 35);

            TMP_InputField inputField = inputObj.AddComponent<TMP_InputField>();

            GameObject textObj = new GameObject("Text");
            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.SetParent(inputObj.transform, false);
            StretchToFill(textRect);

            TextMeshProUGUI inputText = textObj.AddComponent<TextMeshProUGUI>();
            inputText.alignment = TextAlignmentOptions.Center;
            inputText.fontSize = 18;
            inputText.font = GetFont();
            inputText.color = Color.white;

            inputField.textComponent = inputText;
            inputField.text = string.IsNullOrEmpty(savedPresets[globalIndex].customName) ? $"Slot {globalIndex + 1}" : savedPresets[globalIndex].customName;

            inputField.onSelect.AddListener((_) => {
                inputField.text = "";
                isTyping = true;
            });

            inputField.onDeselect.AddListener((_) => {
                isTyping = false;
            });

            int slotIdx = globalIndex;
            inputField.onEndEdit.AddListener((val) => {
                isTyping = false;
                if (string.IsNullOrWhiteSpace(val))
                {
                    val = $"Slot {slotIdx + 1}";
                    inputField.text = val;
                }
                savedPresets[slotIdx].customName = val;
                SavePresetsToConfig();
            });

            slotNameInputs.Add(inputField);
        }

        private void CreatePlayerSelectionMenu(Transform parent)
        {
            playerListPanel = new GameObject("PlayerSelectionPanel");
            RectTransform mainRect = playerListPanel.AddComponent<RectTransform>();
            mainRect.SetParent(parent, false);
            mainRect.sizeDelta = new Vector2(350, 500);

            mainRect.anchorMin = new Vector2(0.5f, 0.5f);
            mainRect.anchorMax = new Vector2(0.5f, 0.5f);
            mainRect.pivot = new Vector2(0.5f, 0.5f);
            mainRect.anchoredPosition = Vector2.zero;

            Image bg = playerListPanel.AddComponent<Image>();
            bg.color = new Color(0.12f, 0.12f, 0.12f, 0.98f);
            bg.raycastTarget = true;
            if (cardSprite != null)
            {
                bg.sprite = cardSprite;
                bg.type = Image.Type.Sliced;
            }

            GameObject headerObj = new GameObject("Header");
            RectTransform headerRect = headerObj.AddComponent<RectTransform>();
            headerRect.SetParent(playerListPanel.transform, false);
            headerRect.anchoredPosition = new Vector2(0, 220);
            headerRect.sizeDelta = new Vector2(330, 40);

            TextMeshProUGUI headerTxt = headerObj.AddComponent<TextMeshProUGUI>();
            headerTxt.text = "SELECT PLAYER TO COPY";
            headerTxt.fontSize = 16;
            headerTxt.font = GetFont();
            headerTxt.alignment = TextAlignmentOptions.Center;
            headerTxt.color = Color.yellow;

            GameObject scrollObj = new GameObject("ScrollView");
            RectTransform scrollRect = scrollObj.AddComponent<RectTransform>();
            scrollRect.SetParent(playerListPanel.transform, false);
            scrollRect.anchoredPosition = new Vector2(0, -20);
            scrollRect.sizeDelta = new Vector2(320, 400);

            ScrollRect scrollRectComp = scrollObj.AddComponent<ScrollRect>();
            scrollRectComp.scrollSensitivity = 20f;
            scrollObj.AddComponent<RectMask2D>();

            GameObject contentObj = new GameObject("Content");
            RectTransform contentRect = contentObj.AddComponent<RectTransform>();
            contentRect.SetParent(scrollObj.transform, false);
            contentRect.anchorMin = new Vector2(0.5f, 1f);
            contentRect.anchorMax = new Vector2(0.5f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = new Vector2(300, 0);

            VerticalLayoutGroup layout = contentObj.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlHeight = false;
            layout.childControlWidth = false;

            ContentSizeFitter fitter = contentObj.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRectComp.content = contentRect;
            scrollRectComp.horizontal = false;
            scrollRectComp.vertical = true;
            playerListContent = contentRect.transform;

            GameObject closeBtnObj = new GameObject("CloseButton");
            RectTransform closeRect = closeBtnObj.AddComponent<RectTransform>();
            closeBtnObj.layer = 5;
            closeRect.SetParent(playerListPanel.transform, false);
            closeRect.anchoredPosition = new Vector2(145, 225);
            closeRect.sizeDelta = new Vector2(30, 30);

            Image closeImg = closeBtnObj.AddComponent<Image>();
            closeImg.color = new Color(0.8f, 0.3f, 0.3f);
            closeImg.raycastTarget = true;
            if (cardSprite != null) { closeImg.sprite = cardSprite; closeImg.type = Image.Type.Sliced; }

            Button closeBtn = closeBtnObj.AddComponent<Button>();
            closeBtn.onClick.AddListener(() => playerListPanel.SetActive(false));

            GameObject closeTxtObj = new GameObject("X");
            RectTransform closeTxtRect = closeTxtObj.AddComponent<RectTransform>();
            closeTxtRect.SetParent(closeBtnObj.transform, false);
            StretchToFill(closeTxtRect);
            TextMeshProUGUI closeTxt = closeTxtObj.AddComponent<TextMeshProUGUI>();
            closeTxt.text = "X";
            closeTxt.fontSize = 14;
            closeTxt.font = GetFont();
            closeTxt.alignment = TextAlignmentOptions.Center;
            closeTxt.color = Color.white;

            playerListPanel.SetActive(false);
        }

        private void OpenPlayerSelectionMenu(int targetSlot)
        {
            slotTargetForSteal = targetSlot;

            foreach (Transform child in playerListContent)
            {
                Destroy(child.gameObject);
            }

            if (!PhotonNetwork.InRoom)
            {
                Log.LogWarning("You are not currently in a room network lobby.");
                return;
            }

            foreach (Photon.Realtime.Player player in PhotonNetwork.PlayerList)
            {
                Photon.Realtime.Player targetPlayer = player;
                GameObject btnObj = new GameObject($"PlayerBtn_{targetPlayer.NickName}");
                RectTransform btnRect = btnObj.AddComponent<RectTransform>();
                btnRect.SetParent(playerListContent, false);
                btnRect.sizeDelta = new Vector2(290, 45);

                Image btnImg = btnObj.AddComponent<Image>();
                btnImg.color = new Color(0.25f, 0.25f, 0.25f, 1f);
                btnImg.raycastTarget = true;
                if (cardSprite != null) { btnImg.sprite = cardSprite; btnImg.type = Image.Type.Sliced; }

                Button btn = btnObj.AddComponent<Button>();
                btn.onClick.AddListener(() => {
                    StealOutfitFromPlayer(targetPlayer, slotTargetForSteal);
                    playerListPanel.SetActive(false);
                });

                GameObject textObj = new GameObject("Text");
                RectTransform textRect = textObj.AddComponent<RectTransform>();
                textObj.layer = 5;
                textRect.SetParent(btnObj.transform, false);
                StretchToFill(textRect);

                TextMeshProUGUI btnText = textObj.AddComponent<TextMeshProUGUI>();
                btnText.text = targetPlayer.IsLocal ? $"{targetPlayer.NickName} (You)" : targetPlayer.NickName;
                btnText.fontSize = 16;
                btnText.font = GetFont();
                btnText.alignment = TextAlignmentOptions.Center;
                btnText.color = Color.white;
            }

            SetUILayerRecursive(playerListPanel, 5);
            playerListPanel.SetActive(true);
        }

        public static Character GetCharacterFromPlayer(Photon.Realtime.Player player)
        {
            if (player == null) return null;
            foreach (Character character in UnityEngine.Object.FindObjectsByType<Character>(FindObjectsSortMode.None))
            {
                if (character.photonView != null && character.photonView.Owner == player)
                {
                    return character;
                }
            }
            return null;
        }

        private void InvalidateThumbnailCache(int index)
        {
            if (thumbnailCache.TryGetValue(index, out Texture2D tex))
            {
                if (tex != null) Destroy(tex);
                thumbnailCache.Remove(index);
            }
        }

        private void StealOutfitFromPlayer(Photon.Realtime.Player targetPlayer, int slotIndex)
        {
            PersistentPlayerData playerData = GameHandler.GetService<PersistentPlayerDataService>().GetPlayerData(targetPlayer);
            if (playerData == null || playerData.customizationData == null)
            {
                Log.LogError($"Could not extract network customization data from player: {targetPlayer.NickName}");
                return;
            }
            var customizationData = playerData.customizationData;
            savedPresets[slotIndex].customName = targetPlayer.NickName;
            savedPresets[slotIndex].skin = customizationData.currentSkin;
            savedPresets[slotIndex].eyes = customizationData.currentEyes;
            savedPresets[slotIndex].mouth = customizationData.currentMouth;
            savedPresets[slotIndex].accessory = customizationData.currentAccessory;
            savedPresets[slotIndex].outfit = customizationData.currentOutfit;
            savedPresets[slotIndex].hat = customizationData.currentHat;
            savedPresets[slotIndex].sash = customizationData.currentSash;
            savedPresets[slotIndex].badgeData = new bool[0];
            savedPresets[slotIndex].medal = customizationData.currentMedal;

            Character targetChar = GetCharacterFromPlayer(targetPlayer);
            if (targetChar != null && targetChar.data != null)
            {
                savedPresets[slotIndex].badgeData = targetChar.data.badgeStatus;
            }

            savedPresets[slotIndex].hasData = true;

            InvalidateThumbnailCache(slotIndex);
            CheckAndExpandSlots(slotIndex);
            SavePresetsToConfig();
            RenderCurrentPage();
            Log.LogInfo($"Successfully cloned and saved {targetPlayer.NickName}'s appearance layout into Slot {slotIndex + 1}!");
        }

        private void SaveCurrentOutfitToPreset(int index)
        {
            PersistentPlayerData playerData = GameHandler.GetService<PersistentPlayerDataService>().GetPlayerData(PhotonNetwork.LocalPlayer);
            if (playerData == null || playerData.customizationData == null)
            {
                Log.LogError("Could not retrieve custom player data configuration.");
                return;
            }

            savedPresets[index].skin = playerData.customizationData.currentSkin;
            savedPresets[index].eyes = playerData.customizationData.currentEyes;
            savedPresets[index].mouth = playerData.customizationData.currentMouth;
            savedPresets[index].accessory = playerData.customizationData.currentAccessory;
            savedPresets[index].outfit = playerData.customizationData.currentOutfit;
            savedPresets[index].hat = playerData.customizationData.currentHat;
            savedPresets[index].sash = playerData.customizationData.currentSash;
            savedPresets[index].medal = playerData.customizationData.currentMedal;

            savedPresets[index].badgeData = new bool[0];
            if (Character.localCharacter != null && Character.localCharacter.data != null)
            {
                savedPresets[index].badgeData = Character.localCharacter.data.badgeStatus;
            }

            savedPresets[index].hasData = true;

            InvalidateThumbnailCache(index);
            CheckAndExpandSlots(index);
            SavePresetsToConfig();
            Log.LogInfo($"Saved current outfit configuration to Slot {index + 1}");
        }

        public void EquipPreset(int index)
        {
            if (index < 0 || index >= savedPresets.Count || !savedPresets[index].hasData)
            {
                Log.LogWarning($"Slot {index + 1} is empty or out of bounds!");
                return;
            }

            OutfitPreset preset = savedPresets[index];
            CharacterCustomization.SetCharacterSkinColor(preset.skin);
            CharacterCustomization.SetCharacterEyes(preset.eyes);
            CharacterCustomization.SetCharacterMouth(preset.mouth);
            CharacterCustomization.SetCharacterAccessory(preset.accessory);
            CharacterCustomization.SetCharacterOutfit(preset.outfit);
            CharacterCustomization.SetCharacterHat(preset.hat);
            CharacterCustomization.SetCharacterSash(preset.sash);
            CharacterCustomization.SetCharacterMedal(preset.medal);

            if (copyBadgesConfig.Value && Character.localCharacter != null && Character.localCharacter.photonView != null && preset.badgeData != null && preset.badgeData.Length > 0)
            {
                Character.localCharacter.photonView.RPC("SyncBadgeStatus", RpcTarget.All, new object[] { preset.badgeData });
            }

            if (PassportManager.instance != null && PassportManager.instance.dummy != null)
            {
                PassportManager.instance.dummy.UpdateDummy(null);
            }
            Log.LogInfo($"Equipped Preset Slot {index + 1}!");
        }

        // --- BACKGROUND RENDERING METHODS ---

        private OutfitPreset GetCurrentPlayerLook()
        {
            OutfitPreset originalLook = new OutfitPreset();
            PersistentPlayerData playerData = GameHandler.GetService<PersistentPlayerDataService>()?.GetPlayerData(PhotonNetwork.LocalPlayer);
            if (playerData != null && playerData.customizationData != null)
            {
                originalLook.skin = playerData.customizationData.currentSkin;
                originalLook.eyes = playerData.customizationData.currentEyes;
                originalLook.mouth = playerData.customizationData.currentMouth;
                originalLook.accessory = playerData.customizationData.currentAccessory;
                originalLook.outfit = playerData.customizationData.currentOutfit;
                originalLook.hat = playerData.customizationData.currentHat;
                originalLook.sash = playerData.customizationData.currentSash;
                originalLook.medal = playerData.customizationData.currentMedal;
                originalLook.hasData = true;
            }
            return originalLook;
        }

        private void RestorePlayerLook(OutfitPreset look)
        {
            if (!look.hasData) return;
            if (rigCamera != null) rigCamera.targetTexture = null;

            CharacterCustomization.SetCharacterSkinColor(look.skin);
            CharacterCustomization.SetCharacterEyes(look.eyes);
            CharacterCustomization.SetCharacterMouth(look.mouth);
            CharacterCustomization.SetCharacterAccessory(look.accessory);
            CharacterCustomization.SetCharacterOutfit(look.outfit);
            CharacterCustomization.SetCharacterHat(look.hat);
            CharacterCustomization.SetCharacterSash(look.sash);
            CharacterCustomization.SetCharacterMedal(look.medal);

            if (PassportManager.instance != null && PassportManager.instance.dummy != null)
            {
                PassportManager.instance.dummy.UpdateDummy(null);
            }
        }

        private void RefreshVisibleThumbnails()
        {
            int startIndex = currentPage * SLOTS_PER_PAGE;
            for (int i = 0; i < portraitImages.Count; i++)
            {
                int globalIndex = startIndex + i;
                if (globalIndex >= savedPresets.Count) break;

                if (!savedPresets[globalIndex].hasData)
                {
                    portraitImages[i].texture = null;
                    portraitImages[i].color = new Color(0.2f, 0.2f, 0.2f, 1f);
                }
                else if (thumbnailCache.TryGetValue(globalIndex, out Texture2D cachedTex) && cachedTex != null)
                {
                    // Image is ready, slot it in immediately
                    portraitImages[i].texture = cachedTex;
                    portraitImages[i].color = Color.white;
                }
                else
                {
                    // Image is missing but data exists. Queue it and set a loading state.
                    portraitImages[i].texture = null;
                    portraitImages[i].color = new Color(0.35f, 0.35f, 0.35f, 1f); // Gray indicator for pending logic
                    EnqueueThumbnailRender(globalIndex);
                }
            }
        }

        private void EnqueueThumbnailRender(int index)
        {
            if (!thumbnailRenderQueue.Contains(index))
            {
                thumbnailRenderQueue.Enqueue(index);
            }

            if (!isRenderingThumbnails)
            {
                StartCoroutine(ProcessThumbnailQueue());
            }
        }

        private IEnumerator ProcessThumbnailQueue()
        {
            isRenderingThumbnails = true;

            OutfitPreset originalLook = GetCurrentPlayerLook();
            SetupRenderRig();
            if (tempRenderTex == null) tempRenderTex = new RenderTexture(210, 230, 16);

            while (thumbnailRenderQueue.Count > 0)
            {
                int globalIndex = thumbnailRenderQueue.Dequeue();
                
                if (globalIndex < 0 || globalIndex >= savedPresets.Count || !savedPresets[globalIndex].hasData)
                    continue;

                // Stop generating if the local player goes missing (e.g. disconnected)
                if (PassportManager.instance == null || PassportManager.instance.dummy == null || rigCamera == null)
                {
                    yield return null; 
                    continue;
                }

                GameObject tempDummy = Instantiate(PassportManager.instance.dummy.gameObject, renderRig.transform, false);
                tempDummy.transform.localPosition = Vector3.zero;
                tempDummy.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                tempDummy.SetActive(true);

                PlayerCustomizationDummy dummyComp = tempDummy.GetComponent<PlayerCustomizationDummy>();
                if (dummyComp != null)
                {
                    ApplyCustomizationValues(dummyComp, savedPresets[globalIndex]);
                }

                rigCamera.targetTexture = tempRenderTex;
                rigCamera.Render();

                RenderTexture prevActive = RenderTexture.active;
                RenderTexture.active = tempRenderTex;
                Texture2D newTex = new Texture2D(210, 230, TextureFormat.RGB24, false);
                newTex.ReadPixels(new Rect(0, 0, 210, 230), 0, 0);
                newTex.Apply();
                RenderTexture.active = prevActive;

                thumbnailCache[globalIndex] = newTex;
                DestroyImmediate(tempDummy);

                UpdateVisiblePortraitIfNeeded(globalIndex, newTex);

                yield return null; // Pauses execution until the next frame. The core fix to the freezing.
            }

            RestorePlayerLook(originalLook);
            isRenderingThumbnails = false;
            
            // Only destroy if the player has fully closed the menu while it was chewing through the queue
            if (menuParent == null || !menuParent.activeSelf)
            {
                DestroyRenderRig();
            }
        }

        private void UpdateVisiblePortraitIfNeeded(int globalIndex, Texture2D tex)
        {
            int startIndex = currentPage * SLOTS_PER_PAGE;
            int endIndex = startIndex + SLOTS_PER_PAGE;

            if (globalIndex >= startIndex && globalIndex < endIndex)
            {
                int localIndex = globalIndex - startIndex;
                if (localIndex >= 0 && localIndex < portraitImages.Count && portraitImages[localIndex] != null)
                {
                    portraitImages[localIndex].texture = tex;
                    portraitImages[localIndex].color = Color.white;
                }
            }
        }

        private void SetupRenderRig()
        {
            if (renderRig != null) return;

            renderRig = new GameObject("WardrobeRenderRig");
            renderRig.transform.position = new Vector3(0f, -999f, 0f);
            DontDestroyOnLoad(renderRig);

            GameObject camObj = new GameObject("RigCamera");
            camObj.transform.SetParent(renderRig.transform, false);
            camObj.transform.localPosition = new Vector3(0f, 1f, -3f);
            camObj.transform.localRotation = Quaternion.identity;

            rigCamera = camObj.AddComponent<Camera>();
            rigCamera.clearFlags = CameraClearFlags.Color;
            rigCamera.backgroundColor = new Color(0.35f, 0.6f, 0.9f);
            rigCamera.orthographic = true;
            rigCamera.orthographicSize = 1.15f;
            rigCamera.nearClipPlane = 0.1f;
            rigCamera.farClipPlane = 10f;
            rigCamera.enabled = false;

            GameObject lightObj = new GameObject("RigLight");
            lightObj.transform.SetParent(renderRig.transform, false);
            lightObj.transform.localPosition = new Vector3(1f, 2f, -2f);
            lightObj.transform.LookAt(new Vector3(0f, 1f, 0f));
            Light lightComponent = lightObj.AddComponent<Light>();
            lightComponent.type = LightType.Directional;
            lightComponent.intensity = 1.2f;
        }

        private void DestroyRenderRig()
        {
            if (renderRig != null)
            {
                Destroy(renderRig);
                renderRig = null;
                rigCamera = null;
            }
        }

        public void ApplyCustomizationValues(PlayerCustomizationDummy dummyComp, OutfitPreset preset)
        {
            dummyComp.SetPlayerColor(preset.skin);
            int fitIndex = preset.outfit;
            dummyComp.SetPlayerCostume(fitIndex);
            int num = preset.hat;
            if (Singleton<Customization>.Instance.fits[fitIndex].overrideHat)
            {
                num = Singleton<Customization>.Instance.fits[fitIndex].overrideHatIndex;
            }
            dummyComp.SetPlayerHat(num);
            int eyesIndex = preset.eyes;
            for (int i = 0; i < dummyComp.refs.EyeRenderers.Length; i++)
            {
                dummyComp.refs.EyeRenderers[i].material.SetTexture(PlayerCustomizationDummy.MainTex, Singleton<Customization>.Instance.eyes[eyesIndex].texture);
            }
            int accessoryIndex = preset.accessory;
            dummyComp.refs.accessoryRenderer.material.SetTexture(PlayerCustomizationDummy.MainTex, Singleton<Customization>.Instance.accessories[accessoryIndex].texture);
            dummyComp.refs.accessoryRenderer.material.renderQueue = (Singleton<Customization>.Instance.accessories[accessoryIndex].drawUnderEye ? 3007 : 3009);
            dummyComp.refs.accessoryEnabled = !Singleton<Customization>.Instance.accessories[accessoryIndex].isThirdEye;
            dummyComp.refs.thirdEye.gameObject.SetActive(Singleton<Customization>.Instance.accessories[accessoryIndex].isThirdEye);
            dummyComp.refs.mouthRenderer.material.SetTexture(PlayerCustomizationDummy.MainTex, Singleton<Customization>.Instance.mouths[preset.mouth].texture);
            List<Material> list = new List<Material>();
            list.Add(dummyComp.refs.sashRenderer.materials[0]);
            int num2 = preset.sash;
            if (num2 >= dummyComp.refs.sashAscentMaterials.Length)
            {
                num2 = dummyComp.refs.sashAscentMaterials.Length - 1;
            }
            list.Add(dummyComp.refs.sashAscentMaterials[num2]);
            int medalIndex = preset.medal;
            dummyComp.refs.medalRenderer.gameObject.SetActive(medalIndex == 1);
            dummyComp.refs.sashRenderer.SetMaterials(list);
        }

        private void UpdateBorders()
        {
            int startIndex = currentPage * SLOTS_PER_PAGE;
            for (int i = 0; i < buttonBorders.Count; i++)
            {
                int globalIndex = startIndex + i;
                if (buttonBorders[i] != null)
                {
                    buttonBorders[i].color = (globalIndex == selectedLoadout) ? activeColor : inactiveColor;
                }
            }
        }

        private void StretchToFill(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
        }

        private void SavePresetsToConfig()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < savedPresets.Count; i++)
            {
                OutfitPreset p = savedPresets[i];
                string flatBadges = p.badgeData != null ? string.Join("-", System.Array.ConvertAll(p.badgeData, b => b ? "1" : "0")) : "0";
                string cleanName = string.IsNullOrEmpty(p.customName) ? $"Slot {i + 1}" : p.customName.Replace(",", "").Replace(";", "");

                builder.Append($"{p.skin},{p.eyes},{p.mouth},{p.accessory},{p.outfit},{p.hat},{p.sash},{flatBadges},{p.medal},{(p.hasData ? 1 : 0)},{cleanName}");
                if (i < savedPresets.Count - 1) builder.Append(";");
            }

            savedPresetsConfig.Value = builder.ToString();
            Config.Save();
        }

        private void LoadPresetsFromConfig()
        {
            string rawData = savedPresetsConfig.Value;
            if (string.IsNullOrEmpty(rawData)) return;

            try
            {
                savedPresets.Clear();
                string[] cards = rawData.Split(';');
                for (int i = 0; i < cards.Length; i++)
                {
                    string[] properties = cards[i].Split(',');
                    OutfitPreset preset = new OutfitPreset();

                    if (properties.Length >= 11)
                    {
                        preset.skin = int.Parse(properties[0]);
                        preset.eyes = int.Parse(properties[1]);
                        preset.mouth = int.Parse(properties[2]);
                        preset.accessory = int.Parse(properties[3]);
                        preset.outfit = int.Parse(properties[4]);
                        preset.hat = int.Parse(properties[5]);
                        preset.sash = int.Parse(properties[6]);

                        string[] badgeBits = properties[7].Split('-');
                        preset.badgeData = System.Array.ConvertAll(badgeBits, bit => bit == "1");
                        preset.medal = int.Parse(properties[8]);

                        preset.hasData = int.Parse(properties[9]) == 1;
                        preset.customName = properties[9];
                    }
                    else if (properties.Length >= 10)
                    {
                        preset.skin = int.Parse(properties[0]);
                        preset.eyes = int.Parse(properties[1]);
                        preset.mouth = int.Parse(properties[2]);
                        preset.accessory = int.Parse(properties[3]);
                        preset.outfit = int.Parse(properties[4]);
                        preset.hat = int.Parse(properties[5]);
                        preset.sash = int.Parse(properties[6]);

                        string[] badgeBits = properties[7].Split('-');
                        preset.badgeData = System.Array.ConvertAll(badgeBits, bit => bit == "1");

                        preset.hasData = int.Parse(properties[8]) == 1;
                        preset.customName = $"Slot {i + 1}";
                    }

                    savedPresets.Add(preset);
                }
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Error parsing list config fields: {ex.Message}");
            }
        }
    }

    public class CustomClickHandler : MonoBehaviour, IPointerClickHandler
    {
        public System.Action OnLeftClick;
        public System.Action OnRightClick;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                OnLeftClick?.Invoke();
            }
            else if (eventData.button == PointerEventData.InputButton.Right)
            {
                OnRightClick?.Invoke();
            }
        }
    }
}
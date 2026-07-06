using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Photon.Pun;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Zorro.Core;

namespace MyPersonalWardrobe
{
    [BepInAutoPlugin]
    public partial class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log { get; private set; } = null!;

        private GameObject menuParent;
        private Image[] buttonBorders = new Image[6];
        private RawImage[] portraitImages = new RawImage[6];
        private RenderTexture[] savedTextures = new RenderTexture[6];
        private int selectedLoadout = 0;
        private Sprite roundedUISprite;

        [System.Serializable]
        public class OutfitPreset
        {
            public int skin;
            public int eyes;
            public int mouth;
            public int accessory;
            public int outfit;
            public int hat;
            public int sash;
            public bool hasData;
        }

        private OutfitPreset[] savedPresets = new OutfitPreset[6];

        // Config entry rewritten as a regular string configuration list format
        private ConfigEntry<string> savedPresetsConfig;

        private Color activeColor = new Color(0.6f, 1f, 0.6f);
        private Color inactiveColor = new Color(0.9f, 0.88f, 0.82f);

        private GameObject renderRig;
        private Camera rigCamera;

        private void Awake()
        {
            Log = Logger;

            for (int i = 0; i < savedPresets.Length; i++)
            {
                savedPresets[i] = new OutfitPreset();
            }

            savedPresetsConfig = Config.Bind("General", "SavedPresetsDataList", "", "Flat list representation containing saved outfit presets data.");
            LoadPresetsFromConfig();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                if (menuParent == null)
                {
                    FetchGameUIAssets();
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
        }

        private void LateUpdate()
        {
            if (menuParent != null && menuParent.activeSelf)
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
                GenerateAllThumbnailsImmediate();
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                DestroyRenderRig();
            }
        }

        private void FetchGameUIAssets()
        {
            if (PassportManager.instance != null)
            {
                Image passportImg = PassportManager.instance.GetComponentInChildren<Image>(true);
                if (passportImg != null && passportImg.sprite != null)
                {
                    roundedUISprite = passportImg.sprite;
                }
            }
        }

        private void CreateWardrobeUI()
        {
            GameObject canvasObj = new GameObject("WardrobeCanvas");
            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 999;

            canvasObj.AddComponent<CanvasScaler>();
            canvasObj.AddComponent<GraphicRaycaster>();

            if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
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
            titleText.text = "WARDROBE PRESETS - CLICK TO EQUIP / SAVE";
            titleText.alignment = TextAlignmentOptions.Center;
            titleText.fontSize = 28;
            titleText.fontStyle = FontStyles.Bold;
            titleText.color = Color.white;

            titleRect.anchoredPosition = new Vector2(0, 420);
            titleRect.sizeDelta = new Vector2(800, 50);

            float[] xPositions = { -320f, 0f, 320f };
            float[] yPositions = { 200f, -160f };

            int index = 0;
            for (int row = 0; row < 2; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    CreateLoadoutCard(canvasObj.transform, xPositions[col], yPositions[row], index);
                    index++;
                }
            }

            UpdateBorders();
        }

        private void CreateLoadoutCard(Transform parent, float xPos, float yPos, int index)
        {
            GameObject cardObj = new GameObject($"LoadoutCard_{index + 1}");
            RectTransform cardRect = cardObj.AddComponent<RectTransform>();
            cardRect.SetParent(parent, false);

            cardRect.sizeDelta = new Vector2(220, 240);
            cardRect.anchoredPosition = new Vector2(xPos, yPos);

            Image borderImg = cardObj.AddComponent<Image>();
            borderImg.color = inactiveColor;
            borderImg.raycastTarget = true;

            if (roundedUISprite != null)
            {
                borderImg.sprite = roundedUISprite;
                borderImg.type = Image.Type.Sliced;
            }
            buttonBorders[index] = borderImg;

            Button btn = cardObj.AddComponent<Button>();
            btn.targetGraphic = borderImg;
            btn.onClick.AddListener(() => {
                selectedLoadout = index;
                UpdateBorders();

                if (Input.GetKey(KeyCode.LeftShift))
                {
                    SaveCurrentOutfitToPreset(index);
                    GenerateAllThumbnailsImmediate();
                }
                else
                {
                    EquipPreset(index);
                }
            });

            GameObject innerImgObj = new GameObject("PortraitView");
            RectTransform innerRect = innerImgObj.AddComponent<RectTransform>();
            innerRect.SetParent(cardObj.transform, false);

            RawImage rawImg = innerImgObj.AddComponent<RawImage>();
            rawImg.color = new Color(0.2f, 0.2f, 0.2f, 1f);
            rawImg.raycastTarget = false;
            portraitImages[index] = rawImg;

            innerRect.anchorMin = new Vector2(0.05f, 0.05f);
            innerRect.anchorMax = new Vector2(0.95f, 0.95f);
            innerRect.sizeDelta = Vector2.zero;

            savedTextures[index] = new RenderTexture(220, 240, 16);

            GameObject labelObj = new GameObject("Label");
            RectTransform labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.SetParent(cardObj.transform, false);

            TextMeshProUGUI labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.text = $"Slot {index + 1}\n<size=14>(Shift+Click to Save)</size>";
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.fontSize = 18;
            labelText.color = Color.white;

            labelRect.anchoredPosition = new Vector2(0, -145);
            labelRect.sizeDelta = new Vector2(220, 50);
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
            savedPresets[index].hasData = true;

            SavePresetsToConfig();
            Log.LogInfo($"Saved current outfit configuration to Slot {index + 1}");
        }

        private void EquipPreset(int index)
        {
            if (!savedPresets[index].hasData)
            {
                Log.LogWarning($"Slot {index + 1} is empty!");
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

            if (PassportManager.instance != null && PassportManager.instance.dummy != null && PassportManager.instance.dummy.gameObject.activeInHierarchy)
            {
                PassportManager.instance.dummy.UpdateDummy();
            }
            Log.LogInfo($"Equipped Preset Slot {index + 1}!");
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

        private void GenerateAllThumbnailsImmediate()
        {
            SetupRenderRig();

            if (rigCamera == null)
            {
                Log.LogWarning("Render studio camera failed initialization.");
                return;
            }

            PersistentPlayerData playerData = GameHandler.GetService<PersistentPlayerDataService>().GetPlayerData(PhotonNetwork.LocalPlayer);
            OutfitPreset originalLook = new OutfitPreset();
            bool gotOriginalLook = false;

            if (playerData != null && playerData.customizationData != null)
            {
                originalLook.skin = playerData.customizationData.currentSkin;
                originalLook.eyes = playerData.customizationData.currentEyes;
                originalLook.mouth = playerData.customizationData.currentMouth;
                originalLook.accessory = playerData.customizationData.currentAccessory;
                originalLook.outfit = playerData.customizationData.currentOutfit;
                originalLook.hat = playerData.customizationData.currentHat;
                originalLook.sash = playerData.customizationData.currentSash;
                gotOriginalLook = true;
            }

            for (int i = 0; i < 6; i++)
            {
                if (!savedPresets[i].hasData) continue;
                if (PassportManager.instance == null || PassportManager.instance.dummy == null) continue;

                GameObject tempDummy = Instantiate(PassportManager.instance.dummy.gameObject, renderRig.transform, false);
                tempDummy.transform.localPosition = Vector3.zero;
                tempDummy.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
                tempDummy.SetActive(true);

                foreach (var comp in tempDummy.GetComponents<MonoBehaviour>())
                {
                    if (comp != null && comp.GetType().Name == "PassportDummy")
                    {
                        comp.enabled = false;
                    }
                }

                ApplyCustomizationValues(tempDummy, savedPresets[i]);

                rigCamera.targetTexture = savedTextures[i];
                rigCamera.Render();

                portraitImages[i].texture = savedTextures[i];
                portraitImages[i].color = Color.white;

                DestroyImmediate(tempDummy);
            }

            rigCamera.targetTexture = null;

            if (gotOriginalLook)
            {
                CharacterCustomization.SetCharacterSkinColor(originalLook.skin);
                CharacterCustomization.SetCharacterEyes(originalLook.eyes);
                CharacterCustomization.SetCharacterMouth(originalLook.mouth);
                CharacterCustomization.SetCharacterAccessory(originalLook.accessory);
                CharacterCustomization.SetCharacterOutfit(originalLook.outfit);
                CharacterCustomization.SetCharacterHat(originalLook.hat);
                CharacterCustomization.SetCharacterSash(originalLook.sash);
            }
        }

        private void ApplyCustomizationValues(GameObject target, OutfitPreset preset)
        {
            CharacterCustomization.SetCharacterSkinColor(preset.skin);
            CharacterCustomization.SetCharacterEyes(preset.eyes);
            CharacterCustomization.SetCharacterMouth(preset.mouth);
            CharacterCustomization.SetCharacterAccessory(preset.accessory);
            CharacterCustomization.SetCharacterOutfit(preset.outfit);
            CharacterCustomization.SetCharacterHat(preset.hat);
            CharacterCustomization.SetCharacterSash(preset.sash);

            foreach (var comp in target.GetComponents<MonoBehaviour>())
            {
                if (comp != null && comp.GetType().Name == "PassportDummy")
                {
                    comp.enabled = true;
                    comp.SendMessage("UpdateDummy", SendMessageOptions.DontRequireReceiver);
                    comp.enabled = false;
                }
            }

            target.SendMessage("UpdateDummy", SendMessageOptions.DontRequireReceiver);

            foreach (var comp in target.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp != null && (comp.GetType().Name == "CharacterVisualsCustomizationComponent" || comp.GetType().Name == "CharacterCustomization"))
                {
                    comp.SendMessage("Refresh", SendMessageOptions.DontRequireReceiver);
                    comp.SendMessage("UpdateDummy", SendMessageOptions.DontRequireReceiver);
                }
            }
        }

        private void UpdateBorders()
        {
            for (int i = 0; i < buttonBorders.Length; i++)
            {
                if (buttonBorders[i] != null)
                {
                    buttonBorders[i].color = (i == selectedLoadout) ? activeColor : inactiveColor;
                }
            }
        }

        private void StretchToFill(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.sizeDelta = Vector2.zero;
        }

        // Serialization rewritten to convert presets into a flat delimited list string
        private void SavePresetsToConfig()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < 6; i++)
            {
                OutfitPreset p = savedPresets[i];
                builder.Append($"{p.skin},{p.eyes},{p.mouth},{p.accessory},{p.outfit},{p.hat},{p.sash},{(p.hasData ? 1 : 0)}");
                if (i < 5) builder.Append(";");
            }

            // Assigning a brand new string value guarantees BepInEx flushes data to disk
            savedPresetsConfig.Value = builder.ToString();
            Config.Save();
        }

        // Deserialization rewritten to cleanly split out data from the string values
        private void LoadPresetsFromConfig()
        {
            string rawData = savedPresetsConfig.Value;
            if (string.IsNullOrEmpty(rawData)) return;

            try
            {
                string[] cards = rawData.Split(';');
                for (int i = 0; i < Mathf.Min(6, cards.Length); i++)
                {
                    string[] properties = cards[i].Split(',');
                    if (properties.Length >= 8)
                    {
                        savedPresets[i].skin = int.Parse(properties[0]);
                        savedPresets[i].eyes = int.Parse(properties[1]);
                        savedPresets[i].mouth = int.Parse(properties[2]);
                        savedPresets[i].accessory = int.Parse(properties[3]);
                        savedPresets[i].outfit = int.Parse(properties[4]);
                        savedPresets[i].hat = int.Parse(properties[5]);
                        savedPresets[i].sash = int.Parse(properties[6]);
                        savedPresets[i].hasData = int.Parse(properties[7]) == 1;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.LogError($"Error parsing list config fields: {ex.Message}");
            }
        }
    }
}
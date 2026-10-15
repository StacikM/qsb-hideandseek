using System;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace QSBHideAndSeek
{
    public class QSBHideAndSeek : ModBehaviour
    {
        public static QSBHideAndSeek Instance;

        private const string QSBUniqueName = "Raicuparta.QuantumSpaceBuddies";

        private HideAndSeekGame _game;

        public void Awake()
        {
            Instance = this;
        }

        public void Start()
        {
            var api = ModHelper.Interaction.TryGetModApi<IQSBAPI>(QSBUniqueName);
            if (api == null)
            {
                ModHelper.Console.WriteLine("Couldn't get the QSB API - is Quantum Space Buddies installed and enabled?", MessageType.Error);
                return;
            }

            _game = gameObject.AddComponent<HideAndSeekGame>();
            _game.Init(api, this, ModHelper.Console);

            // Settings may have been applied before the game component existed.
            ApplySettings(ModHelper.Config);

            new Harmony("Stacik.QSBHideAndSeek").PatchAll(Assembly.GetExecutingAssembly());

            LoadManager.OnCompleteSceneLoad += OnCompleteSceneLoad;
            OnCompleteSceneLoad(OWScene.TitleScreen, LoadManager.GetCurrentScene());

            ModHelper.Console.WriteLine($"{nameof(QSBHideAndSeek)} is loaded!", MessageType.Success);
        }

        public void OnDestroy()
        {
            LoadManager.OnCompleteSceneLoad -= OnCompleteSceneLoad;
        }

        private void OnCompleteSceneLoad(OWScene previousScene, OWScene newScene)
        {
            if (_game != null)
            {
                _game.OnSceneLoaded(newScene);
            }
        }

        public override void Configure(IModConfig config)
        {
            ApplySettings(config);
        }

        private void ApplySettings(IModConfig config)
        {
            if (_game == null || config == null)
            {
                return;
            }

            // The settings are plain number inputs so people can read/type exact values,
            // which means we have to keep them sane ourselves.
            float Number(string key, float fallback, float min, float max)
            {
                var value = config.GetSettingsValue<float>(key);
                if (float.IsNaN(value) || float.IsInfinity(value) || (value == 0f && fallback != 0f && min > 0f))
                {
                    value = fallback; // missing key (older config.json) or garbage
                }

                return Mathf.Clamp(value, min, max);
            }

            var s = _game.LocalSettings;
            s.SeekerCount = Mathf.RoundToInt(Number("seekerCount", 1f, 1f, 2f));
            s.CountdownSeconds = Number("countdownSeconds", 30f, 1f, 600f);
            s.RespawnSeconds = Number("respawnSeconds", 5f, 1f, 60f);
            s.DeathGraceSeconds = Number("deathGraceSeconds", 60f, 0f, 600f);
            s.VoteSeconds = Number("voteSeconds", 20f, 5f, 120f);
            s.TouchDistance = Number("touchDistance", 2f, 0.5f, 10f);
            s.NametagRevealDistance = Number("nametagRevealDistance", 8f, 1f, 100f);
            s.BarrierMeters = Number("barrierMeters", 150f, 20f, 2000f);
            s.HidersSeeSeekerNametags = config.GetSettingsValue<bool>("hidersSeeSeekerNametags");

            _game.DisableTimeLoop = config.GetSettingsValue<bool>("disableTimeLoop");

            var keyName = config.GetSettingsValue<string>("menuKey");
            if (!string.IsNullOrEmpty(keyName) && Enum.TryParse<Key>(keyName, true, out var key))
            {
                _game.MenuKey = key;
            }
        }
    }
}

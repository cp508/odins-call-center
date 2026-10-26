using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Удобства для тестов. Работает ТОЛЬКО в тестовом мире (Dev.TestWorld),
    // чтобы в настоящем мире ничего не удалить и не наспавнить.
    public static class DevTools
    {
        private static readonly List<Character> Mobs = new List<Character>();
        private static float _sinceLoad;
        private static float _purgeTimer;
        private static float _mobTimer;
        private static readonly float[] PurgeAt = { 3f, 10f, 20f, 30f };

        // Пул тест-мобов: по кругу, индекс сохраняется между смертями (сброс — выход из мира/тест).
        private static List<string> _poolCache;
        private static string _poolCacheRaw;
        private static int _poolIndex;
        // Последний спавн — для автотеста (какой префаб, сколько звёзд и сам объект выдал SpawnTestMob).
        public static string LastSpawnedPrefab { get; private set; }
        public static int LastSpawnedStars { get; private set; }
        public static Character LastSpawnedMob { get; private set; }

        // Клон: камера на максимальное отдаление и хозяин на открытое место — спавн бывает в деревьях,
        // и Илья в окне клона не видит боя. Пока перенос не сделан — автотесты ждут (CloneViewPending).
        public static bool CloneViewPending;
        // Готово ли место для автотестов: не клон / не тестовый мир / перенос уже сделан
        public static bool CloneViewReady
        {
            get { return !Plugin.CloneView() || !InTestWorld() || _cloneViewDone; }
        }
        private static bool _cloneViewDone;
        private static readonly FieldInfo CamDistanceField = AccessTools.Field(typeof(GameCamera), "m_distance");
        private static int _obstacleMask = -1;

        public static bool InTestWorld()
        {
            return ZNet.instance != null && ZNet.instance.GetWorldName() == Plugin.DevTestWorld.Value;
        }

        public static void OnWorldLeft()
        {
            Mobs.Clear();
            _sinceLoad = 0f;
            _purgeTimer = 0f;
            _mobTimer = 0f;
            _cloneViewDone = false;
            CloneViewPending = false;
            LastSpawnedPrefab = null;
            LastSpawnedMob = null;
            ResetMobPool();
        }

        // Сброс ротации пула на начало списка — детерминированный старт для автотеста.
        public static void ResetMobPool()
        {
            _poolIndex = 0;
        }

        public static void Tick(float dt, Player player)
        {
            if (!InTestWorld()) return;
            _sinceLoad += dt;

            // Через 3.5с после входа (объекты вокруг уже созданы) — до старта автотестов (6с).
            if (Plugin.CloneView() && !_cloneViewDone)
            {
                CloneViewPending = true;
                if (_sinceLoad >= 3.5f)
                {
                    _cloneViewDone = true;
                    ApplyCloneView(player);
                    ForceDaylight();
                    CloneViewPending = false;
                }
            }

            // Пока подгружаются зоны вокруг — 4 прохода чистки мусора (на 3, 10, 20, 30 сек).
            // Не чаще: поиск по всем сетевым объектам тяжёлый. Самый первый проход (до 6-секундной
            // задержки старта автотестов) дополнительно сметает мобов автотестов (Greyling/Skeleton/
            // Troll), оставшихся от прошлых прогонов selftest.sh — иначе они копятся в сейве
            // тест_мода навсегда и лезут в бой к спутнику в сценариях, которые их не ждут.
            if (_purgeTimer < PurgeAt.Length && _sinceLoad >= PurgeAt[(int)_purgeTimer])
            {
                bool firstPass = _purgeTimer == 0f;
                _purgeTimer += 1f;
                CompanionManager.PurgeOldTestJunk(firstPass ? 1000f : 150f, firstPass);
            }

            // Тест-моб: один всегда жив; убили — через 6 сек новый.
            Mobs.RemoveAll(c => c == null || c.IsDead());
            if (!Plugin.DevTestMobs.Value || _sinceLoad < 10f || Mobs.Count > 0) { _mobTimer = 0f; return; }
            _mobTimer += dt;
            if (_mobTimer > 6f)
            {
                _mobTimer = 0f;
                SpawnTestMob(player);
            }
        }

        // Клон (Dev.LowGraphics): минимальная графика в рантайме — память/GPU, без PlayerPrefs
        // (тот общий с игрой Ильи в реестре, трогать нельзя). Каждая настройка — в try: это чисто
        // локальный рендер, если что-то не нашлось (другая версия Unity) — не валим загрузку мира.
        public static void ApplyLowGraphics()
        {
            try { QualitySettings.SetQualityLevel(0, false); } catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics SetQualityLevel: " + ex.Message); }
            try { QualitySettings.globalTextureMipmapLimit = 2; } catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics globalTextureMipmapLimit: " + ex.Message); }
            try { QualitySettings.shadows = ShadowQuality.Disable; QualitySettings.shadowDistance = 0f; } catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics shadows: " + ex.Message); }
            try { QualitySettings.lodBias = Plugin.DevLowGraphicsLodBias.Value; } catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics lodBias: " + ex.Message); }
            try { QualitySettings.particleRaycastBudget = 16; } catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics particleRaycastBudget: " + ex.Message); }
            try { QualitySettings.realtimeReflectionProbes = false; } catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics realtimeReflectionProbes: " + ex.Message); }
            try { if (ClutterSystem.instance != null) { ClutterSystem.instance.m_distance = 0f; ClutterSystem.instance.m_amountScale = 0f; } } catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics ClutterSystem: " + ex.Message); }
            try { if (Camera.main != null) Camera.main.farClipPlane = Mathf.Min(Camera.main.farClipPlane, 300f); } catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics farClipPlane: " + ex.Message); }
            // Внутреннее разрешение рендера — минимальное (картинка пиксельная, окно остаётся как есть).
            // Ставим ЗДЕСЬ (вход в мир), а не в Awake: GraphicsSettingsManager.ApplyStartupSettings сам
            // перезатирает эти статики загруженными (общими с Ильёй) настройками при старте — наша правка
            // должна лечь ПОСЛЕ него. UpdateCurrentRenderScale — no-op, когда m_autoTargetResolution=false
            // (проверено по IL), так что 480 не перетрётся сама игра.
            try
            {
                UpscaledFrameBuffer.m_autoTargetResolution = false;
                UpscaledFrameBuffer.m_targetResolutionVertical = 480;
                UpscaledFrameBuffer.m_upscalingAlgorithm = UpscalingAlgorithm.NearestNeighbor;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("LowGraphics UpscaledFrameBuffer: " + ex.Message); }
            Plugin.Log.LogInfo("Dev.LowGraphics: минимальная графика применена (без PlayerPrefs)");
        }

        // Клон: всегда полдень и ясно — ночью/в дождь Илья в окне клона ничего не видит (снимок 26.10 02:00).
        // Один раз при входе: тест, которому нужна ночь/дождь (regen), ставит своё и возвращает.
        private static void ForceDaylight()
        {
            if (EnvMan.instance == null) return;
            EnvMan.instance.m_debugTimeOfDay = true;
            EnvMan.instance.m_debugTime = 0.5f;
            EnvMan.instance.m_debugEnv = "Clear";
            Plugin.Log.LogInfo("Клон: время зафиксировано на полдень, погода ясно");
        }

        private static void ApplyCloneView(Player player)
        {
            try
            {
                var cam = GameCamera.instance;
                if (cam != null) CamDistanceField.SetValue(cam, cam.m_maxDistance);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("CloneView камера: " + ex.Message); }
            try
            {
                Vector3 from = player.transform.position;
                if (IsOpenSpot(from, 10f)) { Plugin.Log.LogInfo("CloneView: камера отдалена, место открытое — остаюсь"); return; }
                Vector3 spot;
                if (!FindOpenSpot(from, out spot)) { Plugin.Log.LogWarning("CloneView: открытого места в 80м не нашёл — остаюсь"); return; }
                player.transform.position = spot;
                var body = player.GetComponent<Rigidbody>();
                if (body != null) { body.position = spot; body.velocity = Vector3.zero; }
                Plugin.Log.LogInfo("CloneView: камера отдалена, хозяин перенесён на открытое место в " + Vector3.Distance(from, spot).ToString("F0") + "м от спавна");
            }
            catch (Exception ex) { Plugin.Log.LogWarning("CloneView перенос: " + ex.Message); }
        }

        // Открыто: суша выше воды, ровно (перепад по краю круга ≤3м), в радиусе нет деревьев/камней/построек.
        private static bool IsOpenSpot(Vector3 p, float radius)
        {
            var zs = ZoneSystem.instance;
            if (zs == null || !zs.IsZoneLoaded(p)) return false;
            float h;
            if (!zs.GetGroundHeight(p, out h) || h < zs.m_waterLevel + 1f) return false;
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI / 2f;
                float eh;
                var q = p + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
                if (!zs.GetGroundHeight(q, out eh) || Mathf.Abs(eh - h) > 3f || eh < zs.m_waterLevel + 0.5f) return false;
            }
            if (_obstacleMask == -1) _obstacleMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "vehicle");
            foreach (var c in Physics.OverlapSphere(new Vector3(p.x, h + 2f, p.z), radius, _obstacleMask))
                if (c != null && c.GetComponentInParent<Character>() == null) return false;
            return true;
        }

        // Кольца 12..80м по 16 направлений: сначала чистый круг 10м, не нашлось — 6м.
        private static bool FindOpenSpot(Vector3 from, out Vector3 spot)
        {
            float[] radii = { 10f, 6f };
            foreach (float rad in radii)
                for (float d = 12f; d <= 80f; d += 8f)
                    for (int i = 0; i < 16; i++)
                    {
                        float a = i * Mathf.PI / 8f;
                        var p = from + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * d;
                        float h;
                        if (IsOpenSpot(p, rad) && ZoneSystem.instance.GetGroundHeight(p, out h)) { spot = new Vector3(p.x, h + 0.3f, p.z); return true; }
                    }
            spot = from;
            return false;
        }

        // Текущий пул (TestMobPool через запятую; пусто — один TestMobPrefab). Кэш по сырой строке —
        // чтобы не парсить конфиг каждый вызов, но подхватывать правку в меню/файле конфига сразу.
        private static List<string> GetPool()
        {
            string raw = Plugin.DevTestMobPool.Value;
            if (_poolCache != null && _poolCacheRaw == raw) return _poolCache;
            _poolCacheRaw = raw;
            _poolCache = new List<string>();
            if (!string.IsNullOrEmpty(raw))
            {
                foreach (var part in raw.Split(','))
                {
                    string name = part.Trim();
                    if (name != "") _poolCache.Add(name);
                }
            }
            if (_poolCache.Count == 0) _poolCache.Add(Plugin.DevTestMobPrefab.Value);
            return _poolCache;
        }

        // Dev.TestMobStars: число 0-2 — фиксированно, "random" — случайно 0..2.
        private static int PickStars()
        {
            string s = Plugin.DevTestMobStars.Value;
            if (s == null) return 0;
            s = s.Trim();
            if (string.Equals(s, "random", StringComparison.OrdinalIgnoreCase)) return UnityEngine.Random.Range(0, 3);
            int v;
            if (int.TryParse(s, out v)) return Mathf.Clamp(v, 0, 2);
            return 0;
        }

        // Спавнит следующего по кругу моба из пула со звёздами по конфигу. Возвращает созданного —
        // пригодится автотесту и помощнику SelfTest.SpawnPrefab(..., stars).
        public static Character SpawnTestMob(Player player)
        {
            if (player == null || ZNetScene.instance == null) return null;
            var pool = GetPool();
            string prefabName = pool[_poolIndex % pool.Count];
            _poolIndex = (_poolIndex + 1) % pool.Count;
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null) { Plugin.Log.LogWarning("Нет префаба тест-моба " + prefabName); return null; }
            var pos = player.transform.position + player.transform.forward * 14f + Vector3.up * 1.5f;
            var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.LookRotation(-player.transform.forward));
            var c = go.GetComponent<Character>();
            int stars = PickStars();
            if (c != null)
            {
                c.SetLevel(stars + 1); // SetLevel 1 = 0★ (ванильная шкала)
                Mobs.Add(c);
            }
            LastSpawnedPrefab = prefabName;
            LastSpawnedStars = stars;
            LastSpawnedMob = c;
            string starsTxt = stars > 0 ? " " + new string('★', stars) : "";
            player.Message(MessageHud.MessageType.TopLeft, "Тест-моб: " + (c != null ? Localization.instance.Localize(c.m_name) : prefab.name) + starsTxt);
            Plugin.Log.LogInfo("Тест-моб " + prefabName + starsTxt + " в 14м");
            return c;
        }
    }
}

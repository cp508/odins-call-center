using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Стиль боя (этап «Бой v2а»): меняет запас времени на безопасное окно атаки и допуски риска.
    // Survival (дефолт) — максимально осторожно: не бить, если есть риск не успеть защититься,
    // не рисковать лишним уроном. Balanced/Aggressive — уже сами, допуски шире.
    public enum CombatStyle { Survival, Balanced, Aggressive }

    [BepInPlugin("com.aicompanions.valheim", "AICompanions", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "0.6.0";

        public static ManualLogSource Log;
        public static Plugin Instance; // хост корутин для SelfTest.RunNow (горячая перезагрузка)
        public static ConfigEntry<KeyboardShortcut> SummonKey;
        public static ConfigEntry<KeyboardShortcut> MenuKey;
        public static ConfigEntry<int> MaxCompanions;
        public static ConfigEntry<KeyboardShortcut> RecallKey;
        public static ConfigEntry<float> RecallTimeout;
        public static ConfigEntry<float> RecallArriveRadius;
        public static ConfigEntry<KeyboardShortcut> StanceKey;
        public static ConfigEntry<KeyboardShortcut> InventoryKey;
        public static ConfigEntry<float> EnemyHudRadius;
        public static ConfigEntry<float> CarryWeight;
        public static ConfigEntry<float> CollectRadius;
        public static ConfigEntry<float> MenuScale;
        public static ConfigEntry<float> HudScale;
        public static ConfigEntry<bool> DefendByDefault;
        public static ConfigEntry<float> DefendRadius;
        // Модель стамины (запрос Ильи 25.10, StaminaPolicy.cs): реген быстрее тем, чем ниже
        // стамина (формула игрока, порога «на половине» нет) — поэтому запас считаем явно
        // (кувырки+блок), а не долей от максимума, как было раньше (StaminaReserve убран).
        // CombatKit (временный общий набор "до этапа классов") тоже убран — классы уже в main.
        public static ConfigEntry<float> StaminaDodgeReserveCount;
        public static ConfigEntry<float> StaminaReserveMargin;
        public static ConfigEntry<float> StaminaAttackCostFallback;
        public static ConfigEntry<float> StaminaSeriesHits;
        public static ConfigEntry<bool> StaminaLogGate;
        public static ConfigEntry<float> DodgeDuration;
        public static ConfigEntry<float> BlockReach;
        public static ConfigEntry<float> DodgeReach;
        public static ConfigEntry<float> CompanionSkillFactor;
        public static ConfigEntry<float> CompanionSkillDamageBonus;
        public static ConfigEntry<float> BlockLinger;
        public static ConfigEntry<float> BlockLeadTime;
        public static ConfigEntry<float> DefaultAttackWindup;
        public static ConfigEntry<CombatStyle> Style;
        public static ConfigEntry<int> CombatKitQuality;
        public static ConfigEntry<float> MaxHitOfMax;
        public static ConfigEntry<float> MaxHitOfCurrent;
        public static ConfigEntry<float> InjuredHpFraction;
        public static ConfigEntry<float> RecoveredHpFraction;
        public static ConfigEntry<float> InjuredAllowedFraction;
        public static ConfigEntry<float> ThreatRadius;
        public static ConfigEntry<float> ThreatTickRate;
        public static ConfigEntry<float> SurroundFreeArc;
        public static ConfigEntry<float> EncircleLogSeconds;
        public static ConfigEntry<float> SafeWindowMargin;
        public static ConfigEntry<float> DodgeSafetyMargin;
        public static ConfigEntry<float> AttackWindowDuration;
        public static ConfigEntry<float> StaggerForecastFraction;
        public static ConfigEntry<bool> SaveAttackProfiles;
        public static ConfigEntry<string> FoodKit1;
        public static ConfigEntry<string> FoodKit2;
        public static ConfigEntry<string> FoodKit3;
        public static ConfigEntry<string> FoodKit4;
        public static ConfigEntry<float> EatCheckInterval;
        // Этап «Классы»: у каждой роли свой набор вещей. Танк = ровно старый общий CombatKit
        // (Club+ShieldWood) — первый класс по счёту, одиночные бой-тесты прошлых этапов не меняют поведение.
        public static ConfigEntry<string> ClassTankWeapons;
        public static ConfigEntry<string> ClassTankShield;
        public static ConfigEntry<string> ClassTankArrows;
        public static ConfigEntry<string> ClassUniversalWeapons;
        public static ConfigEntry<string> ClassUniversalShield;
        public static ConfigEntry<string> ClassUniversalArrows;
        public static ConfigEntry<string> ClassArcherWeapons;
        public static ConfigEntry<string> ClassArcherShield;
        public static ConfigEntry<string> ClassArcherArrows;
        public static ConfigEntry<string> ClassFighterWeapons;
        public static ConfigEntry<string> ClassFighterShield;
        public static ConfigEntry<string> ClassFighterArrows;
        public static ConfigEntry<float> ClassWeaponSwitchInterval;
        public static ConfigEntry<float> ClassWeaponSwitchMargin;
        public static ConfigEntry<bool> DebugSensor;
        public static ConfigEntry<bool> DevAutoStart;
        public static ConfigEntry<bool> DevAutoSpawn;
        public static ConfigEntry<string> DevTestWorld;
        public static ConfigEntry<bool> DevTestMobs;
        public static ConfigEntry<string> DevSelfTest;
        public static ConfigEntry<bool> DevSelfTestQuit;
        public static ConfigEntry<int> DevIdleQuitMinutes;
        public static ConfigEntry<bool> DevForceLocalSaves;
        public static ConfigEntry<string> DevSaveDataOverride;
        public static ConfigEntry<bool> DevLowGraphics;
        public static ConfigEntry<float> DevLowGraphicsLodBias;
        public static ConfigEntry<bool> DevCloneView;
        public static ConfigEntry<float> DevDemoPace;
        public static ConfigEntry<int> DevFpsLimit;
        public static ConfigEntry<string> DevHudLabel;
        public static ConfigEntry<bool> DevGodMode;
        // Отладочная визуализация (запрос Ильи 25.10): круги-радиусы вокруг хозяина и зоны атак.
        // Дефолт зависит от CloneView() (вычисляется при Bind — DevCloneView/DevHudLabel уже забинжены выше).
        public static ConfigEntry<bool> DebugRings;
        public static ConfigEntry<bool> AttackZones;
        public static ConfigEntry<KeyboardShortcut> DebugRingsKey;
        public static ConfigEntry<KeyboardShortcut> AttackZonesKey;
        private float _lastActivity;
        public static ConfigEntry<string> DevTestMobPrefab;
        public static ConfigEntry<string> DevTestMobPool;
        public static ConfigEntry<string> DevTestMobStars;
        // Клон ли это (для удобств просмотра): явно Dev.CloneView или задан ярлык клона Dev.HudLabel
        public static bool CloneView() { return DevCloneView.Value || !string.IsNullOrEmpty(DevHudLabel.Value); }
        public static bool MenuOpen;

        private Rect _menuRect = new Rect(20, 160, 460, 180);
        private static bool _autoStartDone;
        private bool _autoSpawnDone;
        private float _autoSpawnTimer;
        private bool _godModeDone;
        private bool _lowGraphicsDone;
        private bool _fpsLimitDone;
        private float _ovFpsAccumTime, _ovMemTimer = 2f;
        private int _ovFpsFrames;
        private float _ovFps;
        private long _ovMemMB;
        private string _ovText = "";
        private GUIStyle _ovStyle;
        // Оверлей клона, вторая часть: что тестируется (пишет testrun.sh в BepInEx/aic_overlay.txt; «!» — красным)
        private readonly System.Collections.Generic.List<string> _ovInfo = new System.Collections.Generic.List<string>();
        private System.DateTime _ovInfoStamp;
        private GUIStyle _ovInfoStyle, _ovInfoRed, _ovInfoBox;
        // Компактная строка на каждого бойца (просьба Ильи «плашка слева, компактная») — только
        // имя/HP/стамина/статус; рюкзак/еда/стойка/цель ушли в подробности одного бойца по I.
        private struct HudRow
        {
            public string Name; public float Hp; public float Max; public string Status;
            // object, не FollowerAI — мёртвый боец в очереди возрождения (CompanionDeath.Pending)
            // тоже должен быть выбираем по I (БАГ Ильи: единственный умер — I было недоступно).
            public float Stam; public float StamMax; public Color Tint; public object Ref;
        }
        private readonly System.Collections.Generic.List<HudRow> _hudRows = new System.Collections.Generic.List<HudRow>();

        // Подробности ОДНОГО бойца — лист по кругу клавишей I (выкл → 1-й → 2-й → … → выкл).
        // Выбор хранится по ССЫЛКЕ (DetailTarget), не по позиции: CompanionManager.Alive() —
        // список с RemoveAll(мёртвые), при смерти/уходе бойца с меньшим индексом все следующие
        // сдвигаются на -1, и индекс стал бы молча показывать статы другого бойца (ревью, фикс).
        // object — FollowerAI (живой) ИЛИ CompanionDeath.Pending (мёртв, ждёт возрождения):
        // по кругу I проходит и живых, и мёртвых, иначе по мёртвому бойцу подробности недоступны.
        public static object DetailTarget;
        public static int DetailIndex = -1; // только для лога/теста — отражает текущую позицию DetailTarget в общем списке
        private struct DetailRow
        {
            public bool Has;
            public string Name; public Color Tint;
            public float Hp, Max, Stam, StamMax;
            public string Stance; public string Target; public string Skill;
            public System.Collections.Generic.List<CompanionInventory.ItemSum> Equip;
            public System.Collections.Generic.List<CompanionFood.SlotView> Stomach;
            public CompanionInventory.Summary Bag;
            public int FoodReserve; public string NextFood; public float NextEta;
        }
        private DetailRow _detail;
        private const string HudHint = "[J] призвать   [K] ко мне   [U] защита/пассив   [I] подробности   [Ё] меню";
        private GUIStyle _hudLabel, _barText, _barShadow, _smallText, _detailText;
        private float _hudTimer;
        private float _pinTimer;
        private GUIStyle _hudStyle, _winStyle, _lblStyle, _btnStyle, _fldStyle, _tglStyle;

        // AudioListener.volume — через reflection: модуль UnityEngine.AudioModule не подключён к сборке
        private static System.Reflection.PropertyInfo _audioVolume;
        private static bool _audioLookedUp;
        private static void MuteOnce()
        {
            if (!_audioLookedUp)
            {
                _audioLookedUp = true;
                var t = System.Type.GetType("UnityEngine.AudioListener, UnityEngine.AudioModule");
                if (t != null) _audioVolume = t.GetProperty("volume", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            }
            if (_audioVolume == null) return;
            if ((float)_audioVolume.GetValue(null, null) != 0f) _audioVolume.SetValue(null, 0f, null);
        }

        private void Awake()
        {
            // Горячая перезагрузка создаёт новый экземпляр: без этого _lastActivity=0, и сторож простоя закрывал игру
            // сразу после reload, если она запущена >10 мин (ложные GAME EXITED в очереди тестов).
            _lastActivity = Time.realtimeSinceStartup;
            Log = Logger;
            Instance = this;
            SummonKey = Config.Bind("General", "SummonKey", new KeyboardShortcut(KeyCode.J), "Призвать ещё одного бойца");
            MenuKey = Config.Bind("General", "MenuKey", new KeyboardShortcut(KeyCode.BackQuote), "Меню бойцов (Ё — слева от 1, под Esc)");
            MaxCompanions = Config.Bind("General", "MaxCompanions", 9, "Лимит бойцов");
            RecallKey = Config.Bind("General", "RecallKey", new KeyboardShortcut(KeyCode.K), "Все ко мне — бросить цели и бежать к тебе");
            RecallTimeout = Config.Bind("General", "RecallTimeout", 10f, "«Ко мне»: аварийный таймаут режима отзыва, если не добежал (обычно снимается раньше — по RecallArriveRadius), сек");
            RecallArriveRadius = Config.Bind("General", "RecallArriveRadius", 3f, "«Ко мне»: ближе этого расстояния до тебя — считается, что добежал, снова обычная стойка, м");
            StanceKey = Config.Bind("General", "StanceKey", new KeyboardShortcut(KeyCode.U), "Всем переключить Защита/Пассив");
            InventoryKey = Config.Bind("General", "InventoryKey", new KeyboardShortcut(KeyCode.I), "Подробности одного бойца на плашке, по кругу: выкл -> 1-й -> 2-й -> ... -> выкл");
            EnemyHudRadius = Config.Bind("Hud", "EnemyHudRadius", 50f, "Радиус (м), в котором полоски HP всех существ (враги и бойцы) видны без наведения прицелом, и в котором враги показаны на миникарте (один общий радиус — дальности совпадают). 150м и 60м — отклонены как читерные, финальный выбор Ильи 25.10 — 50. Игра может не загружать мобов дальше ~80-100м — это её ограничение, не настройки");
            CarryWeight = Config.Bind("Loot", "CarryWeight", 300f, "Сколько кг боец может нести (как игрок)");
            CollectRadius = Config.Bind("Loot", "CollectRadius", 12f, "За лутом дальше этого от себя не ходит, м");
            MenuScale = Config.Bind("General", "MenuScale", 0.8f, "Размер меню Ё");
            HudScale = Config.Bind("General", "HudScale", 1.0f, "Размер плашки бойцов (под миникартой)");
            DefendByDefault = Config.Bind("Combat", "DefendByDefault", true, "Новый боец сразу защищает (иначе — пассив)");
            DefendRadius = Config.Bind("Combat", "DefendRadius", 20f, "Бьёт только врагов не дальше этого от хозяина, м");
            StaminaDodgeReserveCount = Config.Bind("Combat", "StaminaDodgeReserveCount", 1f, "Запас стамины в бою — на сколько кувырков подряд ПОСЛЕ серии ударов (правка Ильи 25.10: запас на старт серии = 1 кувырок + вся серия, а не 2 кувырка + 1 удар), плюс стоимость блока/парирования, если есть щит (StaminaPolicy.Reserve)");
            StaminaReserveMargin = Config.Bind("Combat", "StaminaReserveMargin", 1.15f, "Множитель сверху запаса стамины (StaminaPolicy.Reserve) — допуск на погрешность прогноза стоимости, а не скидка на реген (задержка регена после траты гарантирует 0 регена между кувырками)");
            StaminaAttackCostFallback = Config.Bind("Combat", "StaminaAttackCostFallback", 10f, "Стоимость своего удара (стамина), если у оружия нет данных m_attack.m_attackStamina — запасной вариант для StaminaPolicy.AttackCost");
            StaminaSeriesHits = Config.Bind("Combat", "StaminaSeriesHits", 3f, "Сколько ударов серии учитывать в запасе на старт атаки (правка Ильи 25.10: запас = 1 кувырок + ВСЯ серия, не 1 удар) — сама цепочка ударов без паузы между ними делается в ветке combo, здесь только дефолт запаса под неё (StaminaPolicy.CanStartAttack)");
            StaminaLogGate = Config.Bind("Dev", "StaminaLogGate", false, "Логировать причину, когда боец не стартует атаку из-за нехватки стамины (StaminaPolicy.CanStartAttack) — выкл. по умолчанию, это горячий путь (на каждый Attack.Start())");
            DodgeDuration = Config.Bind("Combat", "DodgeDuration", 0.6f, "Стартовое приближение длительности неуязвимости кувырка, сек — дальше самообучается по факту (реальный анимационный тег)");
            BlockReach = Config.Bind("Combat", "BlockReach", 4f, "Дальность до атакующего, на которой боец решает блок/кувырок, м");
            DodgeReach = Config.Bind("Combat", "DodgeReach", 7f, "Дальность решения «кувырок» против крупных врагов (тролль/босс) — у них радиус удара больше BlockReach, м");
            CompanionSkillFactor = Config.Bind("Combat", "CompanionSkillFactor", 0.65f, "Стартовый уровень НОВОГО (ещё не поднимавшего навык) бойца, 0-1 = уровень/100 (как Skills.GetSkillFactor). Дальше навык растёт от использования и хранится по имени бойца (CompanionSkills) — влияет на силу блока, натяжение лука, урон оружием и расход стамины");
            CompanionSkillDamageBonus = Config.Bind("Combat", "CompanionSkillDamageBonus", 0.3f, "Макс. бонус урона оружием от навыка (0-1 = +0..+100% при навыке=100)");
            BlockLinger = Config.Bind("Combat", "BlockLinger", 0.35f, "Сколько сек держать блок после последнего замаха врага — у частых атак между замахами InAttack() мигает в false на пару кадров, без линьки блок падает между ударами");
            BlockLeadTime = Config.Bind("Combat", "BlockLeadTime", 0.12f, "За сколько сек до расчётного момента удара поднимать блок (попасть в окно парирования 0.25с) — калибруется по факту, см. AttackProfiles");
            DefaultAttackWindup = Config.Bind("Combat", "DefaultAttackWindup", 0.4f, "Дефолтное время замаха врага (сек) до калибровки по конкретному врагу/анимации — до накопления данных блок поднимается сразу, как раньше");
            Style = Config.Bind("Combat", "Style", CombatStyle.Survival, "Стиль боя: Survival (осторожно, по умолчанию) / Balanced / Aggressive — меняет запас окна безопасной атаки и допуски риска при решении блок/кувырок");
            CombatKitQuality = Config.Bind("Combat", "KitQuality", 3, "Уровень прокачки набора класса (как улучшение на верстаке; ограничен максимумом предмета)");
            MaxHitOfMax = Config.Bind("Combat", "MaxHitOfMax", 0.10f, "Допустимый урон одного удара — доля МАКСИМАЛЬНОГО HP (допустимо = меньшее из этого и MaxHitOfCurrent)");
            MaxHitOfCurrent = Config.Bind("Combat", "MaxHitOfCurrent", 0.25f, "Допустимый урон одного удара — доля ТЕКУЩЕГО HP");
            InjuredHpFraction = Config.Bind("Combat", "InjuredHpFraction", 0.35f, "Режим «ранен» включается, когда HP падает до этой доли максимума — допустимый урон резко снижается (InjuredAllowedFraction)");
            RecoveredHpFraction = Config.Bind("Combat", "RecoveredHpFraction", 0.45f, "Режим «ранен» снимается, когда HP восстановится до этой доли максимума (гистерезис, выше InjuredHpFraction)");
            InjuredAllowedFraction = Config.Bind("Combat", "InjuredAllowedFraction", 0.05f, "В режиме «ранен» допустимый урон одного удара — эта доля МАКСИМАЛЬНОГО HP (вместо обычной формулы MaxHitOfMax/MaxHitOfCurrent)");
            ThreatRadius = Config.Bind("Combat", "ThreatRadius", 15f, "ThreatTracker: радиус обзора угроз вокруг бойца, м");
            ThreatTickRate = Config.Bind("Combat", "ThreatTickRate", 10f, "ThreatTracker: частота сканирования угроз, раз/сек");
            SurroundFreeArc = Config.Bind("Combat", "SurroundFreeArc", 70f, "Боец считается окружённым, если самый свободный сектор вокруг него уже этого угла, °");
            EncircleLogSeconds = Config.Bind("Combat", "EncircleLogSeconds", 3f, "Через сколько сек непрерывного окружения писать предупреждение в лог (метрика боя толпой)");
            SafeWindowMargin = Config.Bind("Combat", "SafeWindowMargin", 0.15f, "Запас сверх длительности своего замаха — атака не стартует, если какой-то враг ударит раньше, чем закончится замах + этот запас, сек");
            DodgeSafetyMargin = Config.Bind("Combat", "DodgeSafetyMargin", 0.08f, "Доп. допуск сверх прицела в СЕРЕДИНУ окна неуязвимости кувырка (не смещение к краю) — запас на погрешность прогноза удара врага, сек");
            AttackWindowDuration = Config.Bind("Combat", "AttackWindowDuration", 0.6f, "Сколько сек после успешного кувырка/парирования или оглушения врага считать БЕЗОПАСНЫМ окном для контратаки (приоритет выше обычной проверки IsSafeToAttack) — правило Ильи «бить в каждое безопасное окно»");
            StaggerForecastFraction = Config.Bind("Combat", "StaggerForecastFraction", 0.70f, "Порог прогноза накопленного оглушения (доля от GetStaggerTreshold) — выше него перед блоком выбирается кувырок, если он применим (правило Ильи: не оглушаться)");
            SaveAttackProfiles = Config.Bind("Combat", "SaveAttackProfiles", true, "Сохранять таблицу самообучения тайминга атак в BepInEx/config/AICompanions.attackprofiles.txt (переживает перезапуск)");
            DebugSensor = Config.Bind("Debug", "DebugSensor", true, "Раз в 5 сек писать в лог HP, статус и дистанцию");
            DevAutoStart = Config.Bind("Dev", "AutoStartWorld", false, "Без ролика и меню сразу грузить последний мир последним персонажем");
            DevAutoSpawn = Config.Bind("Dev", "AutoSpawnOnLoad", false, "После входа в мир сам призвать бойца в 10м перед тобой");
            DevTestWorld = Config.Bind("Dev", "TestWorld", "тест_мода", "Мир для тестов: только в нём чистка мусора и тест-мобы");
            DevTestMobs = Config.Bind("Dev", "TestMobs", true, "В тестовом мире держать живым одного слабого агрессивного моба");
            DevTestMobPrefab = Config.Bind("Dev", "TestMobPrefab", "Greyling", "Моб для теста, если TestMobPool пуст");
            DevTestMobPool = Config.Bind("Dev", "TestMobPool", "Skeleton,Greydwarf_Elite,Greydwarf_Shaman,Draugr",
                "Пул тест-мобов по кругу (префабы через запятую) — после смерти текущего спавнится следующий из списка. Пусто — только TestMobPrefab");
            DevTestMobStars = Config.Bind("Dev", "TestMobStars", "0",
                "Звёзды тест-моба: число 0-2 — фиксированно, \"random\" — случайно 0-2 при каждом спавне");
            DevSelfTest = Config.Bind("Dev", "SelfTest", "", "Автотесты через запятую (smoke,combat_basic,...) — запускает selftest.sh");
            DevSelfTestQuit = Config.Bind("Dev", "SelfTestQuit", false, "Закрыть игру после автотестов");
            DevIdleQuitMinutes = Config.Bind("Dev", "IdleQuitMinutes", 10, "В тестовом мире закрыть игру, если N минут нет ни ввода, ни автотестов (0 — выкл.). Чтобы игра не грела ноутбук без дела");
            DevForceLocalSaves = Config.Bind("Dev", "ForceLocalSaves", false, "Для клонов: мир/персонаж только из -savedir, без Steam Cloud (тот игнорирует -savedir и тащит реальные сейвы Ильи). У Ильи — выключено, не трогать");
            DevGodMode = Config.Bind("Dev", "GodMode", false, "Для клонов: игрок бессмертен и невидим мобам (SetGodMode+SetGhostMode) — дерутся только бойцы. У Ильи — выключено");
            DevSaveDataOverride = Config.Bind("Dev", "SaveDataOverride", "", "Для клонов: папка сейвов (= -savedir), ставим явно через Utils.SetSaveDataPath — аргумент -savedir сам по себе не везде подхватывается. У Ильи — пусто, не трогать");
            DevLowGraphics = Config.Bind("Dev", "LowGraphics", false, "Для клонов: минимальная графика (качество, текстуры, тени, трава, LOD, дальность камеры) — меньше памяти/GPU. Без PlayerPrefs. У Ильи — выключено");
            DevLowGraphicsLodBias = Config.Bind("Dev", "LowGraphicsLodBias", 1.25f, "Для клонов с LowGraphics: дальность прорисовки моделей (QualitySettings.lodBias). В игре: низкая 1, средняя 1.5, высокая 2. 1.25 — чуть ниже средней, чтобы Илья видел бой в окне клона (было 0.3)");
            DevDemoPace = Config.Bind("Dev", "DemoPace", 3f, "Медленные показы для Ильи (demo_*): множитель всех пауз и таймера возрождения. Илья 26.10: ~40 с на шаг → 3 (базовые паузы 12–15 с)");
            if (Mathf.Approximately(DevDemoPace.Value, 2f)) DevDemoPace.Value = 3f; // старое 2 из cfg → новое 3
            DevCloneView = Config.Bind("Dev", "CloneView", false, "Для клонов: после входа в мир камера на максимальное отдаление, хозяин — на открытое место без деревьев (чтобы видеть бой в окне клона). Включается и само, если задан Dev.HudLabel (это клон). У Ильи — выключено");
            DevFpsLimit = Config.Bind("Dev", "FpsLimit", 0, "Для клонов: предел кадров/сек (Application.targetFrameRate + vSyncCount=0), 0 — без предела. По умолчанию (у Ильи) — 0, клон сам ставит 30");
            DevHudLabel = Config.Bind("Dev", "HudLabel", "", "Для клонов: имя клона (i1, i2, …) — показывает мини-оверлей FPS/память в углу (как счётчик Steam, у клонов его нет). Пусто — оверлей выключен (у Ильи)");

            // CloneView() уже доступен — DevCloneView/DevHudLabel забинжены строками выше. В клонах
            // обе визуализации включены по умолчанию (нужны для разработки/скриншотов), у Ильи — выключены,
            // переключаются клавишами ниже (N — рядом с J/K, H — на домашнем ряду слева от J, обе свободны
            // по списку занятых клавиш в брифе: F-ряд, J/K/U/I/Ё заняты модом, G/Q/V/P/L/O/T+Ctrl/H+Shift/
            // B+Shift/C+Shift/Backslash/End/Delete/стрелки/F6/F8/F10 — другими модами; N нигде не упомянута,
            // H упомянута только как «H+Shift» — обычная H свободна).
            bool devVizCloneDefault = CloneView();
            DebugRings = Config.Bind("Dev", "DebugRings", devVizCloneDefault, "Круги-радиусы вокруг хозяина каждые 10м (10..60, с подписью расстояния) — отладка, какой радиус обзора нужен. В клонах включено по умолчанию, у Ильи — клавишей DebugRingsKey (Y; N занята оверлеем сенсора)");
            AttackZones = Config.Bind("Dev", "AttackZones", devVizCloneDefault, "Зоны атак (фиолетовые) союзников/хозяина/врагов на земле — куда придётся удар, от начала замаха до удара +0.2с. В клонах включено по умолчанию, у Ильи — клавишей AttackZonesKey (H)");
            DebugRingsKey = Config.Bind("Dev", "DebugRingsKey", new KeyboardShortcut(KeyCode.Y), "Клавиша переключения кругов-радиусов (Dev.DebugRings)");
            AttackZonesKey = Config.Bind("Dev", "AttackZonesKey", new KeyboardShortcut(KeyCode.H), "Клавиша переключения зон атак (Dev.AttackZones)");

            FoodKit1 = Config.Bind("Food", "Kit1", "Raspberry:8,CookedMeat:6,Blueberries:6", "Набор еды класса Танк: Префаб:количество через запятую (5-10 шт на позицию, 3 разных вида)");
            FoodKit2 = Config.Bind("Food", "Kit2", "Blueberries:8,NeckTailGrilled:6,Honey:6", "Набор еды класса Универсал");
            FoodKit3 = Config.Bind("Food", "Kit3", "Honey:6,CookedMeat:6,Raspberry:5", "Набор еды класса Лучник");
            FoodKit4 = Config.Bind("Food", "Kit4", "NeckTailGrilled:8,Raspberry:6,Honey:5", "Набор еды класса Боец");
            EatCheckInterval = Config.Bind("Food", "EatCheckInterval", 1f, "Как часто (сек) боец проверяет рюкзак и доедает, если желудок не полон или лучшая порция почти кончилась");

            // Этап «Классы»: свой набор вещей на роль (CompanionRole), не копия одного общего набора.
            // Танк = старый общий CombatKit (Club+ShieldWood) — первый класс по счёту (см. ClassKits) —
            // одиночные бой-тесты прошлых этапов получают тот же набор, что и раньше.
            ClassTankWeapons = Config.Bind("Classes", "TankWeapons", "Club", "Танк: оружие (через запятую — несколько вариантов, переключение по резисту цели)");
            ClassTankShield = Config.Bind("Classes", "TankShield", "ShieldWood", "Танк: щит (пусто — без щита)");
            ClassTankArrows = Config.Bind("Classes", "TankArrows", "", "Танк: стрелы (через запятую — несколько видов, переключение по резисту цели)");
            ClassUniversalWeapons = Config.Bind("Classes", "UniversalWeapons", "SwordBronze,MaceBronze", "Универсал: оружие (рубящее+дробящее — демонстрирует переключение по резисту)");
            ClassUniversalShield = Config.Bind("Classes", "UniversalShield", "ShieldWoodTower", "Универсал: щит");
            ClassUniversalArrows = Config.Bind("Classes", "UniversalArrows", "", "Универсал: стрелы");
            ClassArcherWeapons = Config.Bind("Classes", "ArcherWeapons", "BowFineWood", "Лучник: лук");
            ClassArcherShield = Config.Bind("Classes", "ArcherShield", "", "Лучник: щит (пусто — лучник без щита)");
            ClassArcherArrows = Config.Bind("Classes", "ArcherArrows", "ArrowWood,ArrowFire", "Лучник: виды стрел (колющие+огненные — переключение по резисту цели)");
            ClassFighterWeapons = Config.Bind("Classes", "FighterWeapons", "SpearFlint,AxeFlint", "Боец: оружие (колющее+рубящее — переключение по резисту цели)");
            ClassFighterShield = Config.Bind("Classes", "FighterShield", "", "Боец: щит (пусто — без щита, ставка на урон)");
            ClassFighterArrows = Config.Bind("Classes", "FighterArrows", "", "Боец: стрелы");
            ClassWeaponSwitchInterval = Config.Bind("Classes", "WeaponSwitchInterval", 1.5f, "Как часто (сек) пересчитывать лучшее оружие/стрелы по резисту целей рядом");
            ClassWeaponSwitchMargin = Config.Bind("Classes", "WeaponSwitchMargin", 0.10f, "На сколько лучший вариант должен превосходить текущий (доля), чтобы боец переключился — не дёргаться туда-сюда на шумовой разнице");

            CompanionDeath.Init(Config);

            // Клон: ставим папку сейвов явно и как можно раньше (ДО того, как FejdStartup сам
            // сканирует миры/персонажей) — аргумент -savedir сам по себе не всегда подхватывается
            // клиентским стартом (у дедсервера работает, у обычного запуска — под вопросом).
            if (DevForceLocalSaves.Value && DevSaveDataOverride.Value.Length > 0)
            {
                Utils.SetSaveDataPath(DevSaveDataOverride.Value);
                Log.LogInfo("Dev.SaveDataOverride: " + DevSaveDataOverride.Value);
            }

            CompanionSensor.TickRate = Config.Bind("Sensor", "TickRate", 5f,
                "Сенсор: сколько раз в секунду пересканировать мир вокруг точки запроса, раз/сек");
            CompanionSensor.Radius = Config.Bind("Sensor", "Radius", 60f,
                "Сенсор: радиус сканирования по умолчанию (дебаг-оверлей и т.п.), м");
            // Старое значение 25 сохранено в cfg у всех, кто запускал раньше, — новое по умолчанию (60, Илья 26.10) иначе не применится
            if (Mathf.Approximately(CompanionSensor.Radius.Value, 25f)) CompanionSensor.Radius.Value = 60f;
            // Свой флаг, не Debug.DebugSensor — тот занят логом HP/статуса бойцов в FollowerAI.
            CompanionSensor.DebugLog = Config.Bind("Sensor", "DebugLog", false,
                "Писать в лог сканы сенсора мира и нераспознанные объекты (отдельно от Debug.DebugSensor — это лог HP/статуса бойцов)");
            // N — не F-ряд, свободна (см. занятые клавиши в PROJECT_BRIEF.md).
            SensorOverlay.ToggleKey = Config.Bind("Sensor", "OverlayKey", new KeyboardShortcut(KeyCode.N),
                "Показать/скрыть дебаг-оверлей сенсора (что вижу вокруг себя)");
            SensorOverlay.OverlayScale = Config.Bind("Sensor", "OverlayScale", 1.0f,
                "Размер оверлея сенсора (как HudScale у плашки статусов)");
            SensorOverlay.MaxPerCategory = Config.Bind("Sensor", "OverlayMaxPerCategory", 6,
                "Сколько ближайших объектов показывать в каждой категории оверлея");

            new Harmony("com.aicompanions.valheim").PatchAll();
            AttackProfiles.Load();
            CompanionSkills.Load();
            Log.LogInfo("AICompanions " + Version + ": J — призвать, Ё — меню");
        }

        private void Update()
        {
            // Клон (Dev.HudLabel): мини-оверлей FPS/память (у клонов своего счётчика Steam нет — они
            // идут мимо Steam-оверлея). Строку пересобираем ТОЛЬКО по таймеру, не каждый кадр.
            if (DevHudLabel.Value.Length > 0)
            {
                _ovFpsFrames++;
                _ovFpsAccumTime += Time.unscaledDeltaTime;
                if (_ovFpsAccumTime >= 0.5f)
                {
                    _ovFps = _ovFpsFrames / _ovFpsAccumTime;
                    _ovFpsFrames = 0; _ovFpsAccumTime = 0f;
                    _ovText = BuildOverlayText();
                }
                _ovMemTimer -= Time.unscaledDeltaTime;
                if (_ovMemTimer <= 0f)
                {
                    _ovMemTimer = 2f;
                    _ovMemMB = WorkingSetMB(); // Process.WorkingSet64 в Mono/Unity даёт 0 — читаем через WinAPI
                    _ovText = BuildOverlayText();
                    ReadOverlayInfo();
                    // Игра сама переставляет предел кадров из своих настроек — держим наш предел клона.
                    if (DevFpsLimit.Value > 0 && (Application.targetFrameRate != DevFpsLimit.Value || QualitySettings.vSyncCount != 0))
                    {
                        Application.targetFrameRate = DevFpsLimit.Value;
                        QualitySettings.vSyncCount = 0;
                    }
                }
            }

            // Лёгкие клоны (Dev.LowGraphics) — без звука (Илья 26.10); полный клон и игра Ильи — со звуком.
            if (DevLowGraphics.Value) MuteOnce();

            // Сторож простоя: тестовый мир, никто не трогает клавиатуру/мышь и не идут автотесты → закрыть игру.
            if (Input.anyKey || Input.GetAxis("Mouse X") != 0f || Input.GetAxis("Mouse Y") != 0f || SelfTest.IsRunning)
                _lastActivity = Time.realtimeSinceStartup;
            if (DevIdleQuitMinutes.Value > 0 && Player.m_localPlayer != null && DevTools.InTestWorld()
                && Time.realtimeSinceStartup - _lastActivity > DevIdleQuitMinutes.Value * 60f)
            {
                Log.LogWarning("Игра простаивает " + DevIdleQuitMinutes.Value + " мин в тестовом мире — закрываю, чтобы не грела ноутбук");
                _lastActivity = Time.realtimeSinceStartup;
                Application.Quit();
            }
            if (DevAutoStart.Value && !_autoStartDone && FejdStartup.instance != null)
            {
                _autoStartDone = true;
                StartCoroutine(AutoStartWorld());
            }

            var player = Player.m_localPlayer;
            if (player == null || ZNetScene.instance == null)
            {
                _autoSpawnDone = false;
                _autoSpawnTimer = 0f;
                _godModeDone = false;
                _lowGraphicsDone = false;
                _fpsLimitDone = false;
                MenuOpen = false;
                DetailIndex = -1;
                DetailTarget = null;
                DevTools.OnWorldLeft();
                SelfTest.OnWorldLeft();
                DevViz.OnWorldLeft();
                CompanionDeath.OnWorldLeft();
                return;
            }

            // Клон (Dev.GodMode): хозяин бессмертен и невидим мобам — дерутся только бойцы.
            if (DevGodMode.Value && !_godModeDone)
            {
                _godModeDone = true;
                player.SetGodMode(true);
                player.SetGhostMode(true);
                Log.LogInfo("Dev.GodMode: игрок бессмертен и невидим мобам");
            }
            if (DevLowGraphics.Value && !_lowGraphicsDone)
            {
                _lowGraphicsDone = true;
                DevTools.ApplyLowGraphics();
                StartCoroutine(LowGraphicsGcRoutine());
            }
            if (DevFpsLimit.Value > 0 && !_fpsLimitDone)
            {
                _fpsLimitDone = true;
                Application.targetFrameRate = DevFpsLimit.Value;
                QualitySettings.vSyncCount = 0;
                Log.LogInfo("Dev.FpsLimit: " + DevFpsLimit.Value + " кадр/с");
            }

            if (DevAutoSpawn.Value && !_autoSpawnDone)
            {
                _autoSpawnTimer += Time.deltaTime;
                if (_autoSpawnTimer > 4f)
                {
                    _autoSpawnDone = true;
                    CompanionManager.SpawnOne(10f);
                }
            }
            DevTools.Tick(Time.deltaTime, player);
            SelfTest.Tick(this, Time.deltaTime);
            AttackProfiles.Tick(Time.deltaTime);
            DevViz.Tick(Time.deltaTime);
            CompanionDeath.Tick(Time.deltaTime);
            CompanionSkills.Tick(Time.deltaTime);

            // Метки на карте — 2 раза/с (просьба Ильи), не каждый кадр.
            _pinTimer -= Time.deltaTime;
            if (_pinTimer <= 0f) { _pinTimer = 0.5f; CompanionMapPins.Tick(); EnemyMapPins.Tick(); }

            _hudTimer -= Time.deltaTime;
            if (_hudTimer <= 0f)
            {
                _hudTimer = 0.25f;
                _hudRows.Clear();
                _detail = new DetailRow();
                var alive = CompanionManager.Alive();
                // DetailTarget пропал — живой боец умер/ушёл (не в alive) ИЛИ мёртвый возродился
                // (Pending ушёл из очереди) — сброс в «выкл», а не молчаливая подмена на того, кто
                // сейчас оказался на той же позиции списка (ревью, major-фикс).
                if (DetailTarget != null)
                {
                    bool stillThere = DetailTarget is CompanionDeath.Pending
                        ? CompanionDeath.Queue.Contains((CompanionDeath.Pending)DetailTarget)
                        : alive.Contains((FollowerAI)DetailTarget);
                    if (!stillThere) { DetailTarget = null; DetailIndex = -1; }
                }
                for (int i = 0; i < alive.Count; i++)
                {
                    var c = alive[i];
                    var b = c.Body;
                    _hudRows.Add(new HudRow
                    {
                        Name = c.Name,
                        Hp = b != null ? b.GetHealth() : 0f,
                        Max = b != null ? b.GetMaxHealth() : 1f,
                        Status = c.Status,
                        Stam = c.Stamina.Current,
                        StamMax = c.Stamina.Max,
                        Tint = c.GetColor(),
                        Ref = c
                    });
                    if (object.ReferenceEquals(c, DetailTarget))
                    {
                        DetailIndex = i; // только для лога/теста — отражает текущую позицию

                        string nextFood = null; float nextEta = 0f;
                        if (b != null) c.Food.PeekNext(out nextFood, out nextEta);
                        // Голый/недоодетый после возрождения (c.Revival != null) — показываем
                        // ЗАМОРОЖЕННЫЙ снимок "что было до смерти" (CompanionDeath), а не реальное
                        // тело (оно либо голое, либо одето лишь частично) — БАГ Ильи: по I у такого
                        // бойца не видно ничего. Полностью одет (Revival снят) — обычное живое тело.
                        System.Collections.Generic.List<CompanionInventory.ItemSum> equip;
                        CompanionInventory.Summary bag;
                        if (c.Revival != null) { equip = c.Revival.EquipSnapshot; bag = c.Revival.BagSnapshot; }
                        else { equip = b != null ? CompanionInventory.SummarizeEquipped(b) : null; bag = b != null ? CompanionInventory.Summarize(b) : null; }
                        _detail = new DetailRow
                        {
                            Has = true,
                            Name = c.Name,
                            Tint = c.GetColor(),
                            Hp = b != null ? b.GetHealth() : 0f,
                            Max = b != null ? b.GetMaxHealth() : 1f,
                            Stam = c.Stamina.Current,
                            StamMax = c.Stamina.Max,
                            Stance = c.StanceLabel(),
                            Target = c.CurrentTargetName(),
                            Skill = CompanionSkills.Summary(c.Name),
                            Equip = equip,
                            Stomach = b != null ? c.Food.ViewSlots() : null,
                            Bag = bag,
                            FoodReserve = b != null ? c.Food.CountReserve() : 0,
                            NextFood = nextFood,
                            NextEta = nextEta
                        };
                    }
                }
                // Погибшие, ждущие возрождения (CompanionDeath) — строка в плашке не пропадает,
                // просто переходит из "мёртв · возрождение через Nс" в обычный статус живого бойца.
                // Ref=p (не null) — мёртвый боец ТОЖЕ выбираем по кругу I (БАГ Ильи: единственный
                // умер — I было недоступно совсем); подробности — снимок "как было до смерти"
                // (CompanionDeath.InventorySnapshot), не пусто.
                int pendingIdx = alive.Count;
                foreach (var p in CompanionDeath.Queue)
                {
                    _hudRows.Add(new HudRow
                    {
                        Name = p.Name,
                        Hp = 0f,
                        Max = Mathf.Max(1f, p.LastMaxHp),
                        Status = CompanionDeath.StatusText(p),
                        Stam = 0f,
                        StamMax = 1f,
                        Tint = CompanionIdentity.Colors[p.ColorIndex].Value,
                        Ref = p
                    });
                    if (object.ReferenceEquals(p, DetailTarget))
                    {
                        DetailIndex = pendingIdx;
                        System.Collections.Generic.List<CompanionInventory.ItemSum> equip;
                        CompanionInventory.Summary bag;
                        CompanionDeath.InventorySnapshot(p, out equip, out bag);
                        _detail = new DetailRow
                        {
                            Has = true,
                            Name = p.Name,
                            Tint = CompanionIdentity.Colors[p.ColorIndex].Value,
                            Hp = 0f,
                            Max = Mathf.Max(1f, p.LastMaxHp),
                            Stam = 0f,
                            StamMax = 1f,
                            Stance = p.Mode == FollowerAI.Stance.Defend ? "Защищает" : "Пассив",
                            Target = null,
                            Skill = CompanionSkills.Summary(p.Name),
                            Equip = equip,
                            Stomach = null,
                            Bag = bag,
                            FoodReserve = 0,
                            NextFood = null,
                            NextEta = 0f
                        };
                    }
                    pendingIdx++;
                }
            }

            // Пока печатаешь имя в меню — горячие клавиши не срабатывают.
            bool typing = MenuOpen && GUIUtility.keyboardControl != 0;
            if (!typing && Pressed(MenuKey)) MenuOpen = !MenuOpen;
            if (MenuOpen && Input.GetKeyDown(KeyCode.Escape)) MenuOpen = false;
            if (!typing && !MenuOpen && Pressed(SummonKey)) CompanionManager.SpawnOne();
            if (!typing && Pressed(RecallKey))
            {
                CompanionManager.RecallAll();
                player.Message(MessageHud.MessageType.TopLeft, "Все ко мне!");
            }
            if (!typing && Pressed(DebugRingsKey))
            {
                DebugRings.Value = !DebugRings.Value;
                player.Message(MessageHud.MessageType.TopLeft, "Круги-радиусы: " + (DebugRings.Value ? "ВКЛ" : "выкл"));
            }
            if (!typing && Pressed(AttackZonesKey))
            {
                AttackZones.Value = !AttackZones.Value;
                player.Message(MessageHud.MessageType.TopLeft, "Зоны атак: " + (AttackZones.Value ? "ВКЛ" : "выкл"));
            }
            if (!typing && Pressed(InventoryKey)) CycleDetail();
            if (!typing && Pressed(SensorOverlay.ToggleKey)) SensorOverlay.Visible = !SensorOverlay.Visible;
            SensorOverlay.Tick(Time.deltaTime);
            if (!typing && Pressed(StanceKey))
            {
                bool anyDefend = CompanionManager.Alive().Exists(c => c.Mode == FollowerAI.Stance.Defend);
                CompanionManager.SetStanceAll(anyDefend ? FollowerAI.Stance.Passive : FollowerAI.Stance.Defend);
                player.Message(MessageHud.MessageType.TopLeft, anyDefend ? "Все: пассив — не атакуют" : "Все: защищают тебя");
            }

            if (MenuOpen)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private string BuildOverlayText()
        {
            return DevHudLabel.Value + " · " + Mathf.RoundToInt(_ovFps) + " FPS · " + (_ovMemMB / 1024f).ToString("0.0") + " ГБ";
        }

        // Файл перечитываем только когда он изменился (раз в секунду проверяем дату записи).
        private void ReadOverlayInfo()
        {
            try
            {
                string path = System.IO.Path.Combine(Paths.BepInExRootPath, "aic_overlay.txt");
                if (!System.IO.File.Exists(path)) { _ovInfo.Clear(); return; }
                var stamp = System.IO.File.GetLastWriteTimeUtc(path);
                if (stamp == _ovInfoStamp) return;
                _ovInfoStamp = stamp;
                _ovInfo.Clear();
                foreach (var line in System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8))
                    if (line.Trim().Length > 0) _ovInfo.Add(line.TrimEnd());
            }
            catch (System.Exception) { } // файл как раз переписывается — прочитаем в следующий раз
        }

        // Мини-оверлей клона (правый верх, под миникартой — та рисуется игрой отдельно от нашего IMGUI).
        private void DrawOverlay(float width)
        {
            if (_ovStyle == null)
            {
                _ovStyle = new GUIStyle(GUI.skin.box) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _ovStyle.normal.textColor = Color.white;
            }
            // Под миникартой (она ~220 px сверху справа), крупно и на подложке — читается в окне-четверти.
            // Окно клона — в правом нижнем углу (Илья 25.10: под миникартой место плашки бойцов, не наслаивать).
            float ovBottom = Screen.height / (Screen.width / Mathf.Max(1f, width)) - 10f; // с учётом масштаба IMGUI

            // Что тестируется: ветка/фича, тест, при перетесте — красным причина/исправление/критерий
            string now = SelfTest.NowRunning();
            if (_ovInfo.Count == 0 && now.Length == 0) { GUI.Box(new Rect(width - 330f, ovBottom - 34f, 320f, 34f), _ovText, _ovStyle); return; }
            if (_ovInfoStyle == null)
            {
                _ovInfoStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
                _ovInfoStyle.normal.textColor = Color.white;
                _ovInfoRed = new GUIStyle(_ovInfoStyle);
                _ovInfoRed.normal.textColor = new Color(1f, 0.35f, 0.3f);
                _ovInfoBox = new GUIStyle(GUI.skin.box);
            }
            float boxW = Mathf.Min(620f, width - 20f), x = width - boxW - 10f, pad = 8f, textW = boxW - 2f * pad;
            // «@сценарий|текст» — только пока идёт этот сценарий; «Сейчас» — сразу под заголовком ветки
            string cur = SelfTest.CurrentName;
            var lines = new System.Collections.Generic.List<string>();
            foreach (var l in _ovInfo)
            {
                if (!l.StartsWith("@")) { lines.Add(l); continue; }
                int bar = l.IndexOf('|');
                if (bar > 1 && l.Substring(1, bar - 1) == cur) lines.Add(l.Substring(bar + 1));
            }
            if (now.Length > 0) lines.Insert(Mathf.Min(1, lines.Count), "СЕЙЧАС: " + now);
            float h = pad;
            foreach (var l in lines) h += _ovInfoStyle.CalcHeight(new GUIContent(l.StartsWith("!") ? l.Substring(1) : l), textW);
            float y = ovBottom - (h + pad);
            GUI.Box(new Rect(width - 330f, y - 40f, 320f, 34f), _ovText, _ovStyle); // FPS/память — над блоком теста
            GUI.Box(new Rect(x, y, boxW, h + pad), GUIContent.none, _ovInfoBox);
            float cy = y + pad;
            foreach (var l in lines)
            {
                bool red = l.StartsWith("!");
                string t = red ? l.Substring(1) : l;
                float lh = _ovInfoStyle.CalcHeight(new GUIContent(t), textW);
                GUI.Label(new Rect(x + pad, cy, textW, lh), t, red ? _ovInfoRed : _ovInfoStyle);
                cy += lh;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ProcMemCounters
        {
            public uint cb; public uint PageFaultCount;
            public System.UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern System.IntPtr GetCurrentProcess();
        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
        private static extern bool GetProcessMemoryInfo(System.IntPtr process, out ProcMemCounters counters, uint size);

        private static long WorkingSetMB()
        {
            try
            {
                ProcMemCounters c;
                if (GetProcessMemoryInfo(GetCurrentProcess(), out c, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(ProcMemCounters))))
                    return (long)(c.WorkingSetSize.ToUInt64() / (1024UL * 1024UL));
            }
            catch (System.Exception) { }
            return 0;
        }

        // Обработчик клавиши I — подробности одного бойца по кругу: выкл -> 1-й -> 2-й -> ... -> выкл.
        // Отдельный публичный метод (не инлайн в Update), чтобы автотест звал его прямо —
        // без эмуляции Input.GetKeyDown (её не воспроизвести в сценарии SelfTest).
        public static void CycleDetail()
        {
            // Живые + мёртвые (CompanionDeath.Queue) — одним кругом, иначе по мёртвому бойцу
            // подробности недоступны вовсе (БАГ Ильи: единственный боец умер — I пусто).
            var alive = CompanionManager.Alive();
            var list = new System.Collections.Generic.List<object>(alive.Count + CompanionDeath.Queue.Count);
            foreach (var f in alive) list.Add(f);
            foreach (var p in CompanionDeath.Queue) list.Add(p);
            int count = list.Count;
            // Следующий ПОСЛЕ текущей ссылки (не индекса) — устойчиво к смерти/уходу бойцов
            // с меньшим индексом между нажатиями I (ревью, major-фикс).
            int idx = DetailTarget != null ? list.IndexOf(DetailTarget) : -1;
            idx++;
            if (count == 0 || idx >= count) idx = -1;
            DetailTarget = idx >= 0 ? list[idx] : null;
            DetailIndex = idx;
            Log.LogInfo("[HUD] Подробности (I): " + (idx < 0 ? "выкл" : (idx + 1) + "/" + count));
        }

        // Свой «нажата ли клавиша»: штатный KeyboardShortcut.IsDown() не срабатывает,
        // если зажат любой лишний модификатор (Shift при беге) — команды не проходили на бегу.
        private static bool Pressed(ConfigEntry<KeyboardShortcut> entry)
        {
            var sc = entry.Value;
            if (sc.MainKey == KeyCode.None || !Input.GetKeyDown(sc.MainKey)) return false;
            foreach (var m in sc.Modifiers) if (!Input.GetKey(m)) return false;
            return true;
        }

        // Загрузчик уничтожает старый экземпляр ДО загрузки новой сборки: снять патчи, убрать спутников и шаблон.
        private void OnDestroy()
        {
            StopAllCoroutines();
            new Harmony("com.aicompanions.valheim").UnpatchSelf();
            CompanionManager.DismissAll();
            CompanionFactory.Shutdown();
            AttackProfiles.Shutdown();
            CompanionSkills.Shutdown();
            CompanionMapPins.Shutdown();
            DevViz.Shutdown();
            EnemyMapPins.Shutdown();
            EnemyHudRange.Shutdown();
            CompanionDeath.Shutdown();
            if (Instance == this) Instance = null;
            Log.LogInfo("AICompanions " + Version + ": выгружен (патчи сняты, бойцы убраны)");
        }

        // Клон (Dev.LowGraphics), один раз через 5с после входа в мир: выгрузить мусор загрузки
        // (неиспользуемые ассеты + managed-куча) — освобождает память, накопленную за старт/спавн.
        private IEnumerator LowGraphicsGcRoutine()
        {
            yield return new WaitForSeconds(5f);
            long before = System.GC.GetTotalMemory(false);
            yield return Resources.UnloadUnusedAssets();
            System.GC.Collect();
            long after = System.GC.GetTotalMemory(true);
            Log.LogInfo("Dev.LowGraphics: UnloadUnusedAssets+GC — managed-куча " + (before / 1024 / 1024) + "МБ -> " + (after / 1024 / 1024) + "МБ");
        }

        private IEnumerator AutoStartWorld()
        {
            float waited = 0f;
            while (waited < 60f && !(FejdStartup.instance != null && FejdStartup.instance.m_mainMenu != null
                                     && FejdStartup.instance.m_mainMenu.activeInHierarchy))
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(1f);
            var fs = FejdStartup.instance;
            if (fs == null) { Log.LogWarning("AutoStart: нет FejdStartup"); yield break; }
            Log.LogInfo("AutoStart: главное меню → персонаж");
            fs.OnStartGame();
            yield return new WaitForSecondsRealtime(1.5f);
            fs.OnCharacterStart();
            yield return new WaitForSecondsRealtime(1.5f);
            var world = CompanionFactory.GetField(fs, "m_world") as World;
            Log.LogInfo("AutoStart: мир = " + (world != null ? world.m_name : "НЕ ВЫБРАН"));
            if (world != null) fs.OnWorldStart();
        }

        private void OnGUI()
        {
            if (Player.m_localPlayer == null) return;
            // Масштаб под экран: на 3K-дисплее стандартный IMGUI мелкий.
            float baseScale = Mathf.Max(1f, Screen.height / 1080f);
            float hs = baseScale * Mathf.Clamp(HudScale.Value, 0.5f, 3f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(hs, hs, 1f));
            float width = Screen.width / hs;
            DrawHud(width);
            if (DevHudLabel.Value.Length > 0) DrawOverlay(width);
            // Подписи расстояний на кругах — в НЕМАСШТАБИРОВАННЫХ координатах экрана (Camera.WorldToScreenPoint
            // уже даёт реальные пиксели), поэтому сбрасываем GUI.matrix на время вызова.
            if (DebugRings.Value)
            {
                GUI.matrix = Matrix4x4.identity;
                DevViz.DrawRingLabels();
                GUI.matrix = Matrix4x4.Scale(new Vector3(hs, hs, 1f));
            }
            SensorOverlay.Draw();
            SelfTest.DrawCaption(width, Screen.height / hs);
            if (!MenuOpen) return;
            float ms = baseScale * Mathf.Clamp(MenuScale.Value, 0.5f, 3f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(ms, ms, 1f));
            if (_winStyle == null) BuildMenuStyles();
            _menuRect = GUILayout.Window(424242, _menuRect, DrawMenu, "Бойцы  v" + Version, _winStyle);
        }

        // Компактная плашка ПОД МИНИКАРТОЙ (правка Ильи 25.10 22:40 по скрину клона — раньше была
        // слева сверху и наслаивалась на подсказки клавиш/панель вещей): подсказка клавиш + на
        // каждого бойца одна строка (цвет/имя/HP/стамина/статус). При >5 бойцах — сжимаем высоту
        // строки, а не обрезаем список (все до MaxCompanions=9 остаются видны). Подробности ОДНОГО
        // бойца (рюкзак/еда/стойка/цель/навык) — отдельной панелью под списком, по кругу клавишей I.
        // Низ малой миникарты в координатах нашего IMGUI (он масштабирован: GUI.matrix = hs); нет карты — запасное место.
        // (из main/plate-right — позиция проверена снимком окна клона, не на глаз.)
        private static float MinimapBottomGui(float width)
        {
            float hs = Screen.width / Mathf.Max(1f, width);
            try
            {
                var mm = Minimap.instance;
                if (mm != null && mm.m_smallRoot != null && mm.m_smallRoot.activeInHierarchy)
                {
                    var rt = mm.m_smallRoot.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        var c = new Vector3[4];
                        rt.GetWorldCorners(c); // Screen Space Overlay: углы — в пикселях экрана, y снизу
                        return (Screen.height - c[0].y) / hs + 8f;
                    }
                }
            }
            catch (System.Exception) { }
            return 235f;
        }

        private void DrawHud(float width)
        {
            if (_hudStyle == null)
            {
                _hudStyle = new GUIStyle(GUI.skin.box);
                _hudLabel = new GUIStyle(GUI.skin.label) { fontSize = 15, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
                _hudLabel.normal.textColor = Color.white;
                _barText = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _barText.normal.textColor = Color.white;
                _barShadow = new GUIStyle(_barText);
                _barShadow.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
                _smallText = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Overflow };
                _smallText.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
                _detailText = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip };
                _detailText.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            }
            const float pad = 8f, nameW = 116f, barW = 118f, barHBase = 14f, gap = 8f, statusW = 180f;
            int n = _hudRows.Count;
            // «лимит строк/сжатие» (бриф) — выбрана компрессия: все бойцы остаются видны, строка ниже.
            float scale = n > 5 ? Mathf.Max(0.6f, 5f / n) : 1f;
            float rowH = 24f * scale, stamH = 6f * scale, barH = barHBase * scale, rowGap = 3f * scale;

            Vector2 hint = _hudLabel.CalcSize(new GUIContent(HudHint));
            float contentW = Mathf.Max(hint.x, nameW + barW + gap + statusW);
            if (_detail.Has)
            {
                if (_detail.Equip != null && _detail.Equip.Count > 0) contentW = Mathf.Max(contentW, TilesWidth(_detail.Equip.Count));
                if (_detail.Stomach != null) contentW = Mathf.Max(contentW, TilesWidth(3));
                if (_detail.Bag != null) contentW = Mathf.Max(contentW, BagWidth(_detail.Bag));
            }
            // Плашка — справа ПОД МИНИКАРТОЙ, позиция по реальному низу карты (не на глаз, main/plate-right).
            float x = width - contentW - pad - 10f, y = MinimapBottomGui(width);
            float listH = n * (rowH + stamH + rowGap);
            GUI.Box(new Rect(x - pad, y, contentW + pad * 2f, pad * 2f + hint.y + listH), GUIContent.none, _hudStyle);
            y += pad;
            GUI.Label(new Rect(x, y, contentW, hint.y), HudHint, _hudLabel);
            y += hint.y;
            for (int i = 0; i < _hudRows.Count; i++)
            {
                var r = _hudRows[i];
                // Цвет бойца виден и тут — полоска слева от строки (просьба Ильи «цвет виден везде»).
                if (CompanionIdentity.IsDark(r.Tint)) FillRect(new Rect(x - 6f, y - 1f, 5f, rowH + stamH + 2f), new Color(0.92f, 0.92f, 0.92f)); // чёрная полоска — со светлой обводкой
                FillRect(new Rect(x - 5f, y, 3f, rowH + stamH), r.Tint);
                if (DetailTarget != null && object.ReferenceEquals(r.Ref, DetailTarget)) DrawFrame(new Rect(x - 6f, y - 1f, contentW + 6f, rowH + stamH + 2f), new Color(1f, 1f, 1f, 0.55f));
                GUI.Label(new Rect(x, y, nameW, rowH), r.Name, _hudLabel);
                var bar = new Rect(x + nameW, y + (rowH - barH) / 2f - stamH / 2f, barW, barH);
                float pct = r.Max > 0f ? Mathf.Clamp01(r.Hp / r.Max) : 0f;
                // >50% зелёная, 25–50% жёлтая, <25% красная
                Color fill = pct > 0.5f ? new Color(0.2f, 0.75f, 0.2f) : pct > 0.25f ? new Color(0.9f, 0.8f, 0.1f) : new Color(0.85f, 0.15f, 0.15f);
                FillRect(bar, new Color(0f, 0f, 0f, 0.6f));
                FillRect(new Rect(bar.x, bar.y, bar.width * pct, bar.height), fill);
                string hp = (int)r.Hp + "/" + (int)r.Max;
                GUI.Label(new Rect(bar.x + 1f, bar.y + 1f, bar.width, bar.height), hp, _barShadow);
                GUI.Label(bar, hp, _barText);
                GUI.Label(new Rect(bar.xMax + gap, y, statusW, rowH), r.Status, _hudLabel);
                // Полоска стамины — тонкая, сразу под HP (жёлтая/оранжевая).
                var sbar = new Rect(bar.x, bar.yMax + 1f, barW, stamH);
                float spct = r.StamMax > 0f ? Mathf.Clamp01(r.Stam / r.StamMax) : 0f;
                FillRect(sbar, new Color(0f, 0f, 0f, 0.6f));
                FillRect(new Rect(sbar.x, sbar.y, sbar.width * spct, sbar.height), new Color(0.95f, 0.65f, 0.1f));
                y += rowH + stamH + rowGap;
            }
            if (_detail.Has) DrawDetailPanel(x, y + 4f, contentW);
        }

        // Подробности ОДНОГО бойца (клавиша I, по кругу) — HP/стамина точными числами, стойка,
        // текущая цель боя, навык, надето/желудок/рюкзак (те же плитки, что раньше были в каждой
        // строке плашки — ушли сюда ради компактности основного списка).
        private void DrawDetailPanel(float x, float y0, float contentW)
        {
            var d = _detail;
            const float headerH = 22f, lineH = 18f;
            float extraH = 0f;
            if (d.Equip != null && d.Equip.Count > 0) extraH += BagH;
            if (d.Stomach != null) extraH += BagH;
            if (d.Bag != null) extraH += BagH + 16f;
            float boxH = 10f + headerH + 3f * lineH + extraH;
            float y = y0;
            GUI.Box(new Rect(x - 8f, y, contentW + 16f, boxH), GUIContent.none, _hudStyle);
            y += 6f;
            FillRect(new Rect(x - 5f, y, 3f, headerH), d.Tint);
            string nums = (int)d.Hp + "/" + (int)d.Max + " HP   " + (int)d.Stam + "/" + (int)d.StamMax + " стамина";
            GUI.Label(new Rect(x, y, contentW, headerH), d.Name + " — подробно:   " + nums, _hudLabel);
            y += headerH;
            GUI.Label(new Rect(x, y, contentW, lineH), "Стойка: " + d.Stance, _detailText);
            y += lineH;
            GUI.Label(new Rect(x, y, contentW, lineH), "Цель: " + (d.Target == null ? "нет" : d.Target), _detailText);
            y += lineH;
            GUI.Label(new Rect(x, y, contentW, lineH), d.Skill, _detailText);
            y += lineH;
            if (d.Equip != null && d.Equip.Count > 0) { DrawTileStrip(x, y, "Надето:", d.Equip, true); y += BagH; }
            if (d.Stomach != null) { DrawStomach(x, y, d.Stomach); y += BagH; }
            if (d.Bag != null)
            {
                DrawBag(x, y, d.Bag);
                y += BagH;
                string next = d.NextFood == null ? "нечего есть" : d.NextFood + (d.NextEta > 1f ? " через " + (int)d.NextEta + "с" : " скоро");
                GUI.Label(new Rect(x, y, contentW, 16f), "Запас еды: " + d.FoodReserve + " · след.: " + next, _smallText);
            }
        }

        // Плитки: иконка, сверху количество, снизу подпись. highlight — рамка «надето».
        private const float BagH = 50f, TileW = 64f, IconS = 30f;

        private void DrawTileStrip(float x, float y, string label, System.Collections.Generic.List<CompanionInventory.ItemSum> items, bool highlight)
        {
            GUI.Label(new Rect(x, y, 96f, 22f), label, _barText);
            float tx = x + 100f;
            foreach (var it in items)
            {
                FillRect(new Rect(tx + 2f, y, TileW - 4f, BagH - 4f), highlight ? new Color(0.55f, 0.42f, 0.08f, 0.55f) : new Color(0f, 0f, 0f, 0.35f));
                if (highlight) DrawFrame(new Rect(tx + 2f, y, TileW - 4f, BagH - 4f), new Color(1f, 0.82f, 0.2f));
                var icon = new Rect(tx + (TileW - IconS) / 2f, y + 2f, IconS, IconS);
                DrawSprite(icon, it.Icon);
                DrawStars(icon, it.Quality);
                GUI.Label(new Rect(tx, y + IconS + 2f, TileW, 14f), it.Name, _smallText);
                tx += TileW;
            }
        }

        // Желудок: 3 слота, иконка + оставшееся время (сек/мин), как в Hud.UpdateFood.
        private void DrawStomach(float x, float y, System.Collections.Generic.List<CompanionFood.SlotView> slots)
        {
            GUI.Label(new Rect(x, y, 96f, 22f), "Желудок " + slots.Count + "/3", _barText);
            float tx = x + 100f;
            foreach (var s in slots)
            {
                FillRect(new Rect(tx + 2f, y, TileW - 4f, BagH - 4f), new Color(0.08f, 0.32f, 0.14f, 0.5f));
                var icon = new Rect(tx + (TileW - IconS) / 2f, y + 2f, IconS, IconS);
                DrawSprite(icon, s.Icon);
                string t = s.TimeLeft >= 60f ? Mathf.CeilToInt(s.TimeLeft / 60f) + "м" : Mathf.FloorToInt(s.TimeLeft) + "с";
                GUI.Label(new Rect(tx, y + IconS + 2f, TileW, 14f), t, _smallText);
                tx += TileW;
            }
            for (int i = slots.Count; i < 3; i++)
            {
                FillRect(new Rect(tx + 2f, y, TileW - 4f, BagH - 4f), new Color(0f, 0f, 0f, 0.25f));
                tx += TileW;
            }
        }

        private static void DrawFrame(Rect r, Color c)
        {
            FillRect(new Rect(r.x, r.y, r.width, 2f), c);
            FillRect(new Rect(r.x, r.yMax - 2f, r.width, 2f), c);
            FillRect(new Rect(r.x, r.y, 2f, r.height), c);
            FillRect(new Rect(r.xMax - 2f, r.y, 2f, r.height), c);
        }

        private static float TilesWidth(int count)
        {
            return 100f + Mathf.Max(1, count) * TileW;
        }

        // Рюкзак (без надетого): слева «ячейки и вес», дальше по плитке на каждый вид предмета.
        private void DrawBag(float x, float y, CompanionInventory.Summary bag)
        {
            GUI.Label(new Rect(x, y, 100f, 22f), bag.SlotsUsed + "/" + bag.SlotsTotal + " яч", _barText);
            Color wc = bag.Weight > bag.MaxWeight * 0.9f ? new Color(1f, 0.5f, 0.3f) : Color.white;
            var old = _barText.normal.textColor;
            _barText.normal.textColor = wc;
            GUI.Label(new Rect(x, y + 20f, 100f, 22f), (int)bag.Weight + "/" + (int)bag.MaxWeight + " кг", _barText);
            _barText.normal.textColor = old;
            if (bag.Items.Count == 0) { GUI.Label(new Rect(x + 100f, y, 200f, 40f), "рюкзак пуст", _hudLabel); return; }
            float tx = x + 100f;
            foreach (var it in bag.Items)
            {
                var icon = new Rect(tx + (TileW - IconS) / 2f, y + 2f, IconS, IconS);
                FillRect(new Rect(tx + 2f, y, TileW - 4f, BagH - 4f), new Color(0f, 0f, 0f, 0.35f));
                DrawSprite(icon, it.Icon);
                DrawStars(icon, it.Quality);
                GUI.Label(new Rect(icon.x + 1f, icon.y + 1f, IconS + 14f, 16f), it.Count.ToString(), _barShadow);
                GUI.Label(new Rect(icon.x, icon.y, IconS + 14f, 16f), it.Count.ToString(), _barText);
                GUI.Label(new Rect(tx, y + IconS + 2f, TileW, 14f), it.Stacks + "ст · " + it.Weight.ToString("0.#") + "кг", _smallText);
                tx += TileW;
            }
        }

        // Уровень прокачки предмета звёздочками в углу иконки (как «★2» у игрока в инвентаре); 1 уровень — без звёзд.
        private void DrawStars(Rect icon, int quality)
        {
            if (quality <= 1) return;
            string st = "★" + quality;
            var old = _smallText.normal.textColor;
            var r = new Rect(icon.xMax - 22f, icon.yMax - 13f, 26f, 14f);
            _smallText.normal.textColor = Color.black;
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), st, _smallText);
            _smallText.normal.textColor = new Color(1f, 0.85f, 0.2f);
            GUI.Label(r, st, _smallText);
            _smallText.normal.textColor = old;
        }

        private static void DrawSprite(Rect r, Sprite sp)
        {
            if (sp == null || sp.texture == null) return;
            var t = sp.texture;
            var tr = sp.textureRect;
            GUI.DrawTextureWithTexCoords(r, t, new Rect(tr.x / t.width, tr.y / t.height, tr.width / t.width, tr.height / t.height));
        }

        private static float BagWidth(CompanionInventory.Summary bag)
        {
            return bag == null ? 0f : 100f + Mathf.Max(1, bag.Items.Count) * TileW;
        }

        private static void FillRect(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        // Крупный шрифт и непрозрачный фон — стандартный IMGUI мелкий и полупрозрачный.
        private void BuildMenuStyles()
        {
            var bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0.07f, 0.07f, 0.09f, 0.96f));
            bg.Apply();
            _winStyle = new GUIStyle(GUI.skin.window) { fontSize = 20, padding = new RectOffset(14, 14, 44, 12) };
            _winStyle.normal.background = bg;
            _winStyle.onNormal.background = bg;
            _winStyle.normal.textColor = Color.white;
            _winStyle.onNormal.textColor = Color.white;
            _lblStyle = new GUIStyle(GUI.skin.label) { fontSize = 18 };
            _lblStyle.normal.textColor = Color.white;
            _btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 18, fixedHeight = 34 };
            _fldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 18, fixedHeight = 32 };
            _tglStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 18 };
            _tglStyle.normal.textColor = Color.white;
            _tglStyle.onNormal.textColor = new Color(0.5f, 1f, 0.5f);
        }

        private void DrawMenu(int id)
        {
            var player = Player.m_localPlayer;
            var alive = CompanionManager.Alive();
            if (alive.Count == 0) GUILayout.Label("Никого нет. J или кнопка ниже — призвать.", _lblStyle);
            foreach (var c in alive)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(7f); // место для цветной полоски строки (ниже, FillRect по GetLastRect)
                string nn = GUILayout.TextField(c.Name, 20, _fldStyle, GUILayout.Width(170));
                if (nn != c.Name) c.SetName(nn);
                if (GUILayout.Button(c.RoleLabel(), _btnStyle, GUILayout.Width(95))) c.CycleRole();
                string dist = player != null ? (int)Vector3.Distance(c.transform.position, player.transform.position) + "м" : "?";
                GUILayout.Label(dist, _lblStyle, GUILayout.Width(55));
                bool defend = c.Mode == FollowerAI.Stance.Defend;
                if (GUILayout.Button(defend ? "Защищает" : "Пассив", _btnStyle, GUILayout.Width(120)))
                    c.SetStance(defend ? FollowerAI.Stance.Passive : FollowerAI.Stance.Defend);
                if (GUILayout.Button("Ко мне", _btnStyle, GUILayout.Width(95))) c.Recall();
                if (GUILayout.Button("Отдать мне", _btnStyle, GUILayout.Width(130)))
                {
                    int n = c.GiveAllToOwner();
                    if (player != null) player.Message(MessageHud.MessageType.TopLeft, c.Name + " отдал предметов: " + n);
                }
                if (GUILayout.Button("Убрать", _btnStyle, GUILayout.Width(95))) CompanionManager.Dismiss(c);
                GUILayout.EndHorizontal();
                // Цвет бойца виден и в меню — полоска слева от строки (тот же приём, что в плашке HUD).
                var rowRect = GUILayoutUtility.GetLastRect();
                if (CompanionIdentity.IsDark(c.GetColor())) FillRect(new Rect(rowRect.x - 1f, rowRect.y + 1f, 6f, rowRect.height - 2f), new Color(0.92f, 0.92f, 0.92f));
                FillRect(new Rect(rowRect.x, rowRect.y + 2f, 4f, rowRect.height - 4f), c.GetColor());
                // Навыки спутника (этап «Навыки как у игрока») — свои за именем, растут от боя.
                GUILayout.Label(CompanionSkills.Summary(c.Name), _lblStyle);
            }
            GUILayout.Space(8);
            GUILayout.Label("Всем:", _lblStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Ко мне", _btnStyle)) CompanionManager.RecallAll();
            if (GUILayout.Button("Защищать", _btnStyle)) CompanionManager.SetStanceAll(FollowerAI.Stance.Defend);
            if (GUILayout.Button("Пассив", _btnStyle)) CompanionManager.SetStanceAll(FollowerAI.Stance.Passive);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Всё мне", _btnStyle))
            {
                int n = 0;
                foreach (var c in alive) n += c.GiveAllToOwner();
                if (player != null) player.Message(MessageHud.MessageType.TopLeft, "Отдали предметов: " + n);
            }
            if (GUILayout.Button("Призвать", _btnStyle)) CompanionManager.SpawnOne();
            if (GUILayout.Button("Убрать всех", _btnStyle)) CompanionManager.DismissAll();
            GUILayout.EndHorizontal();
            if (DevTools.InTestWorld())
            {
                GUILayout.BeginHorizontal();
                bool on = GUILayout.Toggle(DevTestMobs.Value, DevTestMobs.Value ? " Тест-мобы: ВКЛ" : " Тест-мобы: выкл", _tglStyle);
                if (on != DevTestMobs.Value) DevTestMobs.Value = on;
                if (GUILayout.Button("Тест-моб сейчас", _btnStyle)) DevTools.SpawnTestMob(player);
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Ё / Esc — закрыть   ·   K — все ко мне   ·   U — защита/пассив", _lblStyle);
            GUI.DragWindow();
        }
    }
}

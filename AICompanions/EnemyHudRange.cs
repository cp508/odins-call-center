using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // «Все в 150м видны» (просьба Ильи): полоски HP над всеми существами в радиусе — без наведения
    // прицелом. Игра (EnemyHud.UpdateHuds) показывает полоску не-игроку/не-боссу/не-верховому только
    // пока на нём прицел (или недавно был — HudData.m_hoverTimer копится, когда НЕ под прицелом,
    // порог m_hoverShowDuration). Решение: каждый кадр, ДО оригинального UpdateHuds, сбрасываем
    // hoverTimer в 0 всем, кто уже попал в m_huds и сейчас в нашем радиусе — для игры это выглядит
    // так, будто их только что навели, весь остальный путь показа (бар/имя/иконки/экран) — штатный
    // код игры, не трогаем. Плюс сам радиус (m_maxShowDistance) поднимаем до конфига, иначе игра
    // даже не создаёт HudData на мобов дальше дефолтной дистанции (EnemyHud.LateUpdate → TestShow).
    // m_huds/HudData — приватные (сам класс HudData — nested private), поля HudData публичные —
    // читаем/пишем их через reflection по РЕАЛЬНОМУ типу, как CompanionNameColorPatch в UiPatches.cs.
    [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
    static class EnemyHudAlwaysShowPatch
    {
        static void Prefix(EnemyHud __instance, Player player)
        {
            if (__instance == null || player == null) return;
            EnemyHudRange.RememberOriginal(__instance); // один раз — до первой правки полей
            float radius = Plugin.EnemyHudRadius.Value;
            if (radius <= 0f) return;
            if (__instance.m_maxShowDistance < radius) __instance.m_maxShowDistance = radius;
            if (__instance.m_maxShowDistanceBoss < radius) __instance.m_maxShowDistanceBoss = radius;
            EnemyHudRange.ForceShowAll(__instance, player, radius);
        }
    }

    public static class EnemyHudRange
    {
        private static readonly FieldInfo HudsField =
            typeof(EnemyHud).GetField("m_huds", BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo _hoverField; // HudData.m_hoverTimer — тип узнаём по первому реальному объекту
        private static FieldInfo _guiField;    // HudData.m_gui — для автотестов (IsBarVisible)

        // Исходные (до нашей правки) дистанции — чтобы вернуть их при выгрузке мода (горячая
        // перезагрузка): Prefix их только ПОДНИМАЕТ и никогда не опускает, поля живут на синглтоне
        // EnemyHud.instance дольше, чем наш Harmony-патч (ревью, major-фикс).
        private static float _origMaxShow = -1f;
        private static float _origMaxShowBoss = -1f;
        private static EnemyHud _origOwner;

        public static void RememberOriginal(EnemyHud hud)
        {
            if (hud == null) return;
            if (_origMaxShow >= 0f && _origOwner == hud) return; // уже запомнили для этого синглтона
            _origMaxShow = hud.m_maxShowDistance;
            _origMaxShowBoss = hud.m_maxShowDistanceBoss;
            _origOwner = hud;
        }

        // Plugin.OnDestroy (горячая перезагрузка) — вернуть EnemyHud.instance дистанции, которые
        // были до патча, иначе ваниль продолжает показывать полоски дальше штатной дистанции даже
        // после выгрузки мода, до конца игровой сессии.
        public static void Shutdown()
        {
            if (_origMaxShow >= 0f && EnemyHud.instance != null && EnemyHud.instance == _origOwner)
            {
                EnemyHud.instance.m_maxShowDistance = _origMaxShow;
                EnemyHud.instance.m_maxShowDistanceBoss = _origMaxShowBoss;
            }
            _origMaxShow = -1f;
            _origMaxShowBoss = -1f;
            _origOwner = null;
        }

        public static void ForceShowAll(EnemyHud hud, Player player, float radius)
        {
            if (hud == null || HudsField == null || player == null) return;
            var huds = HudsField.GetValue(hud) as IDictionary;
            if (huds == null) return;
            Vector3 ownerPos = player.transform.position;
            float r2 = radius * radius;
            foreach (DictionaryEntry e in huds)
            {
                var c = e.Key as Character;
                if (c == null || c.IsDead() || c == (Character)player) continue; // Unity-null — уничтоженный/выгруженный тоже == null
                if ((c.transform.position - ownerPos).sqrMagnitude > r2) continue;
                // Ревью (major): m_huds содержит ЛЮБОЕ существо в радиусе видимости игры (кабан, олень,
                // курица, торговец), фильтра по вражде там нет (IL EnemyHud.TestShow — только дистанция/
                // boss/alerted). Задача — полоски над ВРАГАМИ и СОЮЗНИКАМИ, не над всей нейтральной живностью.
                // Тот же паттерн фильтра, что в EnemyMapPins.Tick().
                bool isAlly = FollowerAI.Registry.ContainsKey(c);
                bool isEnemy = BaseAI.IsEnemy(player, c);
                if (!isAlly && !isEnemy) continue;
                object hd = e.Value;
                if (hd == null) continue;
                if (_hoverField == null)
                    _hoverField = hd.GetType().GetField("m_hoverTimer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_hoverField != null) _hoverField.SetValue(hd, 0f);
            }
        }

        // Для автотестов: реально ли сейчас видна полоска над этим существом (после всех проверок
        // игры — дистанция, экран, камера), без повторной реализации логики UpdateHuds в тесте.
        public static bool IsBarVisible(Character c)
        {
            if (c == null || EnemyHud.instance == null || HudsField == null) return false;
            var huds = HudsField.GetValue(EnemyHud.instance) as IDictionary;
            if (huds == null || !huds.Contains(c)) return false;
            object hd = huds[c];
            if (hd == null) return false;
            if (_guiField == null)
                _guiField = hd.GetType().GetField("m_gui", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var go = _guiField != null ? _guiField.GetValue(hd) as GameObject : null;
            return go != null && go.activeSelf;
        }
    }
}

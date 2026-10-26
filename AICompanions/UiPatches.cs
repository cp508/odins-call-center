using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Цифра урона, ПРОШЕДШЕГО по спутнику, — красная (как у игрока); «Заблокировано» остаётся белым.
    // Игра красит красным только урон по себе (player=true в DamageText.ShowText) — подменяем флаг,
    // пока идёт ApplyDamage спутника.
    [HarmonyPatch(typeof(Character), "ApplyDamage")]
    static class CompanionDamageTextFlagPatch
    {
        public static bool InCompanionDamage;
        static void Prefix(Character __instance) { InCompanionDamage = FollowerAI.Registry.ContainsKey(__instance); }
        static void Postfix() { InCompanionDamage = false; }
    }

    [HarmonyPatch(typeof(DamageText), "ShowText", new[] { typeof(HitData.DamageModifier), typeof(Vector3), typeof(float), typeof(bool) })]
    static class CompanionDamageTextRedPatch
    {
        static void Prefix(ref bool player) { if (CompanionDamageTextFlagPatch.InCompanionDamage) player = true; }
    }

    // Имя над головой бойца — его цветом (просьба Ильи «цвет виден везде»). EnemyHud.ShowHud зовётся
    // КАЖДЫЙ кадр для каждого видимого Character (EnemyHud.LateUpdate) и заводит/обновляет HudData —
    // красим только m_name бойцов (FollowerAI.Registry), чужих не трогаем. HudData — приватный nested
    // класс, TMP-текст — из сборки, которую не подключали в build.sh: оба поля читаем через reflection
    // по РЕАЛЬНОМУ типу объекта (как SetInfo/GetInfo в загрузчике читают приватный PluginInfo).
    [HarmonyPatch(typeof(EnemyHud), "ShowHud")]
    static class CompanionNameColorPatch
    {
        private static readonly FieldInfo HudsField =
            typeof(EnemyHud).GetField("m_huds", BindingFlags.NonPublic | BindingFlags.Instance);
        private static FieldInfo _nameField;   // HudData.m_name — тип узнаём по первому реальному объекту
        private static PropertyInfo _colorProp; // TMP_Text.color — тоже чужая сборка, тоже лениво
        private static PropertyInfo _outlineColorProp, _outlineWidthProp; // TMP_Text.outlineColor/outlineWidth
        private static readonly Color32 LightOutline = new Color32(240, 240, 240, 255);

        static void Postfix(EnemyHud __instance, Character c)
        {
            FollowerAI f;
            if (c == null || HudsField == null || __instance == null) return;
            if (!FollowerAI.Registry.TryGetValue(c, out f)) return;
            var huds = HudsField.GetValue(__instance) as IDictionary;
            if (huds == null || !huds.Contains(c)) return;
            object hd = huds[c];
            if (hd == null) return;
            if (_nameField == null)
                _nameField = hd.GetType().GetField("m_name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_nameField == null) return;
            object nameObj = _nameField.GetValue(hd);
            if (nameObj == null) return;
            if (_colorProp == null) _colorProp = nameObj.GetType().GetProperty("color");
            if (_colorProp != null) _colorProp.SetValue(nameObj, f.GetColor(), null);
            // Чёрное имя (Илья 25.10: «почти не видно») — со светлой обводкой. Ставим только если ещё не стоит:
            // смена обводки пересобирает материал текста, каждый кадр нельзя.
            Color col = f.GetColor();
            if (!CompanionIdentity.IsDark(col)) return;
            if (_outlineColorProp == null)
            {
                _outlineColorProp = nameObj.GetType().GetProperty("outlineColor");
                _outlineWidthProp = nameObj.GetType().GetProperty("outlineWidth");
            }
            if (_outlineColorProp == null || _outlineWidthProp == null) return;
            var cur = (Color32)_outlineColorProp.GetValue(nameObj, null);
            if (cur.r == LightOutline.r && cur.g == LightOutline.g && cur.b == LightOutline.b) return;
            _outlineWidthProp.SetValue(nameObj, 0.3f, null);
            _outlineColorProp.SetValue(nameObj, LightOutline, null);
        }
    }
}

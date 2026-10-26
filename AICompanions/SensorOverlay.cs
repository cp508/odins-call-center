using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AICompanions
{
    // Дебаг-оверлей сенсора: по клавише показывает вокруг игрока найденное по категориям
    // с расстояниями (этап «Сенсор», п.3). Config.Bind — в Plugin.Awake (правило проекта),
    // здесь только поля и Tick/Draw (минимум вставок в Plugin.cs, см. _research/sensor_status.md).
    public static class SensorOverlay
    {
        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<float> OverlayScale;
        public static ConfigEntry<int> MaxPerCategory;
        public static bool Visible;

        private static readonly CompanionSensor _sensor = new CompanionSensor();

        // Сканируем только пока оверлей открыт — закрытый не тратит время на Physics/скан.
        // Троттлинг по TickRate — один раз, в самом CompanionSensor.Tick (не дублируем здесь таймер).
        public static void Tick(float dt)
        {
            if (!Visible) return;
            var me = Player.m_localPlayer;
            if (me == null) return;
            _sensor.Tick(dt, me.transform.position, CompanionSensor.Radius.Value, me);
        }

        private static GUIStyle _box, _header, _line, _hostileLine;

        private static void EnsureStyles()
        {
            if (_box != null) return;
            var bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.07f, 0.88f));
            bg.Apply();
            _box = new GUIStyle(GUI.skin.box);
            _box.normal.background = bg;
            _header = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            _header.normal.textColor = new Color(1f, 0.85f, 0.3f);
            _line = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false, clipping = TextClipping.Overflow };
            _line.normal.textColor = Color.white;
            _hostileLine = new GUIStyle(_line);
            _hostileLine.normal.textColor = new Color(1f, 0.45f, 0.4f);
        }

        public static void Draw()
        {
            if (!Visible) return;
            var me = Player.m_localPlayer;
            if (me == null) return;
            EnsureStyles();

            float baseScale = Mathf.Max(1f, Screen.height / 1080f);
            float s = baseScale * Mathf.Clamp(OverlayScale.Value, 0.5f, 3f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float screenW = Screen.width / s;

            const float panelW = 280f, pad = 8f, rowH = 16f;
            int max = Mathf.Max(1, MaxPerCategory.Value);

            int rows = 1; // заголовок
            rows += CountRows(_sensor.Trees, max) + CountRows(_sensor.Ores, max) + CountRows(_sensor.Pickables, max)
                  + CountRows(_sensor.Loot, max) + CountRows(_sensor.Mobs, max) + CountRows(_sensor.Stations, max);

            float x = screenW - panelW - 16f;
            float y = 16f;
            float h = pad * 2f + rows * rowH;
            GUI.Box(new Rect(x, y, panelW, h), GUIContent.none, _box);
            float cy = y + pad;
            float cx = x + pad;
            float w = panelW - pad * 2f;

            GUI.Label(new Rect(cx, cy, w, rowH), "Сенсор r=" + (int)CompanionSensor.Radius.Value + "м  "
                + _sensor.LastScanMs.ToString("0.00") + "мс  [N]", _header);
            cy += rowH;

            cy = DrawCategory(cx, cy, w, rowH, "Деревья", _sensor.Trees, max, TreeLine);
            cy = DrawCategory(cx, cy, w, rowH, "Руда", _sensor.Ores, max, OreLine);
            cy = DrawCategory(cx, cy, w, rowH, "Сбор", _sensor.Pickables, max, PickableLine);
            cy = DrawCategory(cx, cy, w, rowH, "Лут", _sensor.Loot, max, LootLine);
            cy = DrawCategory(cx, cy, w, rowH, "Мобы", _sensor.Mobs, max, MobLine);
            DrawCategory(cx, cy, w, rowH, "Станции", _sensor.Stations, max, StationLine);
        }

        // +1 — строка заголовка категории ("Деревья (N):"), которую рисует DrawCategory.
        private static int CountRows(List<SensorHit> list, int max)
        {
            return list.Count == 0 ? 0 : 1 + Mathf.Min(list.Count, max);
        }

        private delegate string LineFmt(SensorHit h);

        private static float DrawCategory(float x, float y, float w, float rowH, string title, List<SensorHit> list, int max, LineFmt fmt)
        {
            if (list.Count == 0) return y;
            GUI.Label(new Rect(x, y, w, rowH), title + " (" + list.Count + "):", _header);
            y += rowH;
            int n = Mathf.Min(list.Count, max);
            for (int i = 0; i < n; i++)
            {
                var h = list[i];
                var style = h.Category == SensorCategory.Mob && h.Hostile ? _hostileLine : _line;
                GUI.Label(new Rect(x, y, w, rowH), fmt(h), style);
                y += rowH;
            }
            return y;
        }

        private static string TreeLine(SensorHit h)
        {
            return "  " + h.Name + " " + (int)h.Distance + "м тир" + h.MinToolTier;
        }

        private static string OreLine(SensorHit h)
        {
            return "  " + h.Name + " " + (int)h.Distance + "м тир" + h.MinToolTier;
        }

        private static string PickableLine(SensorHit h)
        {
            return "  " + h.Name + " " + (int)h.Distance + "м" + (h.PickableReady ? "" : " (пусто)");
        }

        private static string LootLine(SensorHit h)
        {
            return "  " + h.Name + " x" + h.Stack + " " + (int)h.Distance + "м";
        }

        private static string MobLine(SensorHit h)
        {
            string star = h.Level > 1 ? "★" + h.Level : "";
            string flag = h.Hostile ? " [ВРАГ]" : "";
            return "  " + h.Name + star + " " + (int)h.Hp + "/" + (int)h.MaxHp + "HP " + (int)h.Distance + "м"
                + flag + " опасн." + h.Danger.ToString("0.0");
        }

        private static string StationLine(SensorHit h)
        {
            return "  " + h.StationType + " " + h.Name + " " + (int)h.Distance + "м";
        }
    }
}

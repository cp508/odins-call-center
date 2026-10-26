using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Автотесты сенсора (этап «Сенсор»). Рядом с игроком создаём по одному объекту каждой категории
    // и проверяем: сенсор нашёл именно его, с верной категорией и расстоянием ±0.5м; замер времени скана.
    public static class SensorTests
    {
        public static void Register()
        {
            SelfTest.Register("sensor_basic", SensorBasic, 60f);
            SelfTest.Register("demo_sensor", DemoSensor, 400f);
            SelfTest.Register("sensor_scan60", SensorScan60, 60f);
        }

        // Имена префабов — не из dump8/il8 (это данные ассетов, не C#-код, см. _research/ahead_sensor_scout.md §6).
        // Берём первый, который реально есть в ZNetScene.instance (SelfTest.SpawnPrefab вернёт null, если нет)
        private static readonly string[] TreeNames = { "Beech1", "Beech_small1", "Birch1", "Birch2", "FirTree", "Pinetree_01" };
        private static readonly string[] OreNames = { "MineRock_Tin", "MineRock_Obsidian", "MineRock_Meteorite", "rock4_copper", "rock3_silver" };
        private static readonly string[] BushNames = { "RaspberryBush", "BlueberryBush", "Pickable_Mushroom", "Pickable_Stone", "Pickable_Flint" };
        private static readonly string[] ItemNames = { "Wood", "Stone", "Flint", "Resin" };
        private static readonly string[] StationNames = { "piece_workbench", "piece_stonecutter", "forge", "blackforge" };

        private static GameObject SpawnFirstAvailable(string[] candidates, float forward, float right, out string used)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                var go = SelfTest.SpawnPrefab(candidates[i], forward, right);
                if (go != null) { used = candidates[i]; return go; }
            }
            used = null;
            return null;
        }

        // ПОКАЗ ДЛЯ ИЛЬИ (demo_sensor): оверлей сенсора включён, рядом по одному объекту каждой категории,
        // крупные подписи по центру, паузы × Plugin.DevDemoPace. Проверка — глазами, вердикт PASS, если всё заспавнилось.
        // Цена скана на радиусе из конфига (Илья: 60 м): 50 сканов подряд, среднее/макс. мс; PASS, если среднее ≤10 мс.
        private static IEnumerator SensorScan60()
        {
            var me = Player.m_localPlayer;
            var sensor = new CompanionSensor();
            float r = CompanionSensor.Radius.Value, sum = 0f, max = 0f;
            var sw = new System.Diagnostics.Stopwatch();
            for (int i = 0; i < 50; i++)
            {
                sw.Reset(); sw.Start();
                sensor.Scan(me.transform.position, r, me);
                sw.Stop();
                float ms = (float)sw.Elapsed.TotalMilliseconds;
                sum += ms; if (ms > max) max = ms;
                yield return null;
            }
            float avg = sum / 50f;
            string info = "радиус=" + r + "м скан ср=" + avg.ToString("0.00") + "мс макс=" + max.ToString("0.00") + "мс (n=50), тик " + CompanionSensor.TickRate.Value + "/с";
            if (avg <= 10f) SelfTest.Pass(info); else SelfTest.Fail("скан дорогой | " + info);
        }

        private static IEnumerator DemoSensor()
        {
            float pace = Plugin.DevDemoPace.Value;
            bool wasVisible = SensorOverlay.Visible;
            string treeName, oreName, bushName, itemName, stationName;
            SpawnFirstAvailable(TreeNames, 8f, -6f, out treeName);
            SpawnFirstAvailable(OreNames, 8f, -2f, out oreName);
            SpawnFirstAvailable(BushNames, 8f, 2f, out bushName);
            SpawnFirstAvailable(ItemNames, 8f, 6f, out itemName);
            SpawnFirstAvailable(StationNames, 8f, 10f, out stationName);
            var mobGo = SelfTest.SpawnPrefab("Greyling", 14f, 0f);
            SensorOverlay.Visible = true;
            yield return SelfTest.Caption("ПОКАЗ: сенсор мира (1/4)\nОверлей включён (у себя — клавиша N). Перед тобой по одному объекту каждого вида.", 12f * pace);
            yield return SelfTest.Caption("(2/4) В оверлее — категории: деревья, руда, кусты/сбор, лут, станции, мобы.\nУ каждого — имя и расстояние в метрах.", 12f * pace);
            yield return SelfTest.Caption("(3/4) Сравни расстояния: дерево/руда/куст/лут ~8–12 м, станция ~13 м, моб ~14 м.\nМоб двигается — его расстояние меняется на глазах.", 15f * pace);
            yield return SelfTest.Caption("(4/4) Сканирование — раз в 0.2 с по слоям, не каждый кадр. ПОКАЗ ЗАКОНЧЕН.", 8f * pace);
            SelfTest.ClearCaption();
            SensorOverlay.Visible = wasVisible;
            if (mobGo != null) ZNetScene.instance.Destroy(mobGo);
            SelfTest.Pass("показ: дерево=" + treeName + " руда=" + oreName + " куст=" + bushName + " лут=" + itemName + " станция=" + stationName);
        }

        private static IEnumerator SensorBasic()
        {
            string treeName, oreName, bushName, itemName, stationName;
            var treeGo = SpawnFirstAvailable(TreeNames, 8f, -6f, out treeName);
            var oreGo = SpawnFirstAvailable(OreNames, 8f, -2f, out oreName);
            var bushGo = SpawnFirstAvailable(BushNames, 8f, 2f, out bushName);
            var itemGo = SpawnFirstAvailable(ItemNames, 8f, 6f, out itemName);
            var stationGo = SpawnFirstAvailable(StationNames, 8f, 10f, out stationName);
            var mobGo = SelfTest.SpawnPrefab("Greyling", 14f, 0f);

            var missing = new List<string>();
            if (treeGo == null) missing.Add("дерево (" + string.Join(",", TreeNames) + ")");
            if (oreGo == null) missing.Add("руда (" + string.Join(",", OreNames) + ")");
            if (bushGo == null) missing.Add("куст (" + string.Join(",", BushNames) + ")");
            if (itemGo == null) missing.Add("предмет (" + string.Join(",", ItemNames) + ")");
            if (stationGo == null) missing.Add("станция (" + string.Join(",", StationNames) + ")");
            if (mobGo == null) missing.Add("Greyling");
            if (missing.Count > 0) { SelfTest.Fail("нет префабов: " + string.Join(" | ", missing.ToArray())); yield break; }

            SelfTest.Log("спавн: дерево=" + treeName + " руда=" + oreName + " куст=" + bushName + " предмет=" + itemName + " станция=" + stationName);

            // Коллайдерам/физике дать кадр-два устояться, прежде чем сканировать.
            yield return new WaitForSeconds(1f);

            var me = SelfTest.Me;
            var sensor = new CompanionSensor();
            sensor.Scan(me.transform.position, 30f, me);

            var results = new List<string>();
            bool ok = true;

            ok &= CheckHit(results, "дерево", sensor.Trees, treeGo, SensorCategory.Tree, me.transform.position);
            ok &= CheckHit(results, "руда", sensor.Ores, oreGo, SensorCategory.Ore, me.transform.position);
            ok &= CheckHit(results, "куст", sensor.Pickables, bushGo, SensorCategory.Pickable, me.transform.position);
            ok &= CheckHit(results, "предмет", sensor.Loot, itemGo, SensorCategory.Loot, me.transform.position);
            ok &= CheckHit(results, "станция", sensor.Stations, stationGo, SensorCategory.Station, me.transform.position);
            ok &= CheckHit(results, "моб", sensor.Mobs, mobGo, SensorCategory.Mob, me.transform.position);

            var mobHit = FindByGameObject(sensor.Mobs, mobGo);
            if (mobHit != null && !mobHit.Hostile) { ok = false; results.Add("моб: ожидался враждебным (Greyling), Hostile=false"); }

            string timing = "скан=" + sensor.LastScanMs.ToString("0.00") + "мс";
            string details = timing + " | " + string.Join(" ; ", results.ToArray());
            if (ok) SelfTest.Pass(details);
            else SelfTest.Fail(details);
        }

        private static SensorHit FindByGameObject(List<SensorHit> list, GameObject go)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Comp != null && list[i].Comp.gameObject == go) return list[i];
            return null;
        }

        // Проверка одной категории: найден, категория верна, расстояние совпадает с реальным ±0.5м,
        // направление на найденный объект совпадает с направлением на реально заспавненный ±0.1° (dot).
        private static bool CheckHit(List<string> results, string label, List<SensorHit> list, GameObject go, SensorCategory expectCat, Vector3 from)
        {
            var h = FindByGameObject(list, go);
            if (h == null) { results.Add(label + ": НЕ НАЙДЕН"); return false; }
            if (h.Category != expectCat) { results.Add(label + ": категория=" + h.Category + " (ждали " + expectCat + ")"); return false; }
            float expected = Vector3.Distance(from, go.transform.position);
            float diff = Mathf.Abs(h.Distance - expected);
            if (diff > 0.5f) { results.Add(label + ": дистанция=" + h.Distance.ToString("0.00") + " ожидали=" + expected.ToString("0.00") + " (разница " + diff.ToString("0.00") + "м)"); return false; }
            Vector3 expectedDir = (go.transform.position - from).normalized;
            Vector3 actualDir = (h.Comp.transform.position - from).normalized;
            float dot = Vector3.Dot(expectedDir, actualDir);
            if (dot < 0.999f) { results.Add(label + ": направление неверно (dot=" + dot.ToString("0.000") + ")"); return false; }
            results.Add(label + ": OK " + h.Distance.ToString("0.0") + "м");
            return true;
        }
    }
}

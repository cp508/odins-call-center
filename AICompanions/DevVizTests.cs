using System.Collections;
using System.Reflection;
using UnityEngine;

namespace AICompanions
{
    // Автотесты отладочной визуализации (ветка devviz): круги-радиусы и зоны атак.
    public static class DevVizTests
    {
        // Та же техника, что в CombatV2aTests.ParryDrill — форсируем цель скелета на бойца, чтобы
        // бой начался СРАЗУ и гарантированно (не ждать естественную агро-детекцию ИИ).
        private static readonly FieldInfo TargetField =
            typeof(MonsterAI).GetField("m_targetCreature", BindingFlags.NonPublic | BindingFlags.Instance);

        public static void Register()
        {
            SelfTest.Register("devviz_rings", Rings, 30f);
            SelfTest.Register("devviz_attack_zones", AttackZonesTest, 60f);
        }

        // Включаем Dev.DebugRings на время теста, проверяем 6 колец и точный радиус по вершине
        // каждого (горизонтальная дистанция от игрока — высота земли тут не участвует).
        private static IEnumerator Rings()
        {
            bool was = Plugin.DebugRings.Value;
            Plugin.DebugRings.Value = true;
            yield return new WaitForSeconds(0.6f); // хотя бы один цикл обновления (период 0.2с)

            int count = DevViz.RingCount;
            SelfTest.Log("колец создано: " + count);
            if (count != 6)
            {
                Plugin.DebugRings.Value = was;
                SelfTest.Fail("колец " + count + ", ожидалось 6");
                yield break;
            }

            float[] expected = { 10f, 20f, 30f, 40f, 50f, 60f };
            Vector3 from = SelfTest.Me.transform.position;
            float maxErr = 0f;
            string details = "";
            for (int i = 0; i < expected.Length; i++)
            {
                float r;
                DevViz.TryGetRingRadius(i, out r);
                float dist;
                DevViz.TryGetRingHorizontalDistance(i, 0, from, out dist);
                float err = Mathf.Abs(dist - expected[i]);
                if (err > maxErr) maxErr = err;
                details += (int)expected[i] + "м:r=" + r.ToString("0") + ",факт=" + dist.ToString("0.00") + " ";
            }
            SelfTest.Log(details);
            Plugin.DebugRings.Value = was;

            if (maxErr > 0.3f) SelfTest.Fail("радиус неточен, макс. ошибка " + maxErr.ToString("0.00") + "м | " + details);
            else SelfTest.Pass("6 колец, макс. ошибка радиуса " + maxErr.ToString("0.00") + "м | подписи — экранные, см. скриншот");
        }

        // Боец (Defend) против скелета: на замах любой из сторон должна появиться зона, после удара
        // (+0.2с) исчезнуть. Проверяем: зон показано > 0, утечек нет (активных после боя = 0).
        private static IEnumerator AttackZonesTest()
        {
            bool was = Plugin.AttackZones.Value;
            Plugin.AttackZones.Value = true;
            int before = DevViz.EverCreated;

            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { Plugin.AttackZones.Value = was; SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            var mob = SelfTest.SpawnPrefab("Skeleton_NoArcher", 8f, 0f);
            if (mob == null) mob = SelfTest.SpawnPrefab("Skeleton", 8f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;
            if (mc == null) { Plugin.AttackZones.Value = was; SelfTest.Fail("нет префаба Skeleton"); yield break; }
            mc.SetLevel(1);
            var mai = mc.GetComponent<MonsterAI>();

            float t0 = Time.time;
            while (Time.time - t0 < 35f)
            {
                if (c == null || c.Body == null || c.Body.IsDead()) { SelfTest.Log("боец мёртв/исчез на " + (Time.time - t0).ToString("0.0") + "с"); break; }
                if (mc == null || mc.IsDead()) { SelfTest.Log("скелет убит на " + (Time.time - t0).ToString("0.0") + "с"); break; }
                // Скелет должен бить именно бойца (иначе бой может не начаться вовсе — естественная
                // агро-детекция не гарантирована в короткое окно теста).
                if (mai != null && TargetField != null) TargetField.SetValue(mai, c.Body);
                yield return new WaitForSeconds(0.3f);
            }
            SelfTest.Log("зон к этому моменту: " + (DevViz.EverCreated - before));
            // Убрать участников СЕЙЧАС (а не ждать замах/предохранитель) — иначе возможна гонка:
            // кто-то из двоих мог начать новый замах именно в момент измерения (не утечка, а совпадение).
            // После Destroy атакующий == null (Unity-null), DevViz.ExpireZones подчищает на следующем
            // тике (период 0.1с) — ждём с запасом.
            CompanionManager.DismissAll();
            SelfTest.CleanupArena();
            yield return new WaitForSeconds(0.5f);

            int created = DevViz.EverCreated - before;
            int activeAfter = DevViz.ActiveZoneCount;
            Plugin.AttackZones.Value = was;

            string info = "зон_показано=" + created + " активных_после=" + activeAfter;
            SelfTest.Log(info);
            if (created <= 0) { SelfTest.Fail("зон не было ни одной | " + info); yield break; }
            if (activeAfter != 0) { SelfTest.Fail("утечка: зоны не очистились | " + info); yield break; }
            SelfTest.Pass(info);
        }
    }
}

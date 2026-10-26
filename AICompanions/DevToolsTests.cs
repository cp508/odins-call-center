using System.Collections;
using UnityEngine;

namespace AICompanions
{
    // Автотест пула тест-мобов (запрос Ильи): DevTools держит "вечного" тест-моба в мире тест_мода
    // (DevTools.Tick, каждый кадр, независимо от сценария) — после смерти текущего спавнится
    // следующий из Dev.TestMobPool по кругу, со звёздами по Dev.TestMobStars. Сценарий настраивает
    // конфиг на известный короткий список, ждёт первый ambient-спавн, убивает моба и ждёт второй.
    public static class DevToolsTests
    {
        public static void Register()
        {
            SelfTest.Register("devtools_mobpool", MobPool, 45f);
        }

        private static IEnumerator MobPool()
        {
            string prevPool = Plugin.DevTestMobPool.Value;
            string prevStars = Plugin.DevTestMobStars.Value;
            bool prevOn = Plugin.DevTestMobs.Value;

            string firstPrefab = null, secondPrefab = null;
            int firstStars = -1, secondStars = -1;
            bool ok1, ok2 = false;

            Plugin.DevTestMobs.Value = true;
            Plugin.DevTestMobPool.Value = "Skeleton,Draugr";
            Plugin.DevTestMobStars.Value = "1";
            DevTools.ResetMobPool();

            // try/finally: конфиг общий на клон (ConfigEntry пишет в cfg-файл на диске) — если тело
            // сценария вылетит исключением, восстановление ДОЛЖНО выполниться всё равно, иначе
            // Dev.TestMobPool/TestMobStars останутся "Skeleton,Draugr"/"1" навсегда и сломают
            // эмбиент-моба для всех следующих веток/прогонов на этом клоне очереди testrun.sh.
            try
            {
                // CleanupArena() перед сценарием уже убрал прошлого ambient-моба — DevTools.Tick (идёт
                // каждый кадр сам по себе) через ~6с заметит пустоту и создаст первого из пула.
                var r1 = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(() => DevTools.LastSpawnedPrefab == "Skeleton" && DevTools.LastSpawnedMob != null, 15f, r1);
                ok1 = r1.Ok;
                if (ok1) { firstPrefab = DevTools.LastSpawnedPrefab; firstStars = DevTools.LastSpawnedStars; }

                if (ok1)
                {
                    var firstMob = DevTools.LastSpawnedMob;
                    firstMob.SetHealth(0f);
                    var rDead = new SelfTest.WaitResult();
                    yield return SelfTest.WaitUntil(() => firstMob == null || firstMob.IsDead(), 3f, rDead);
                    SelfTest.Log("первый моб " + firstPrefab + " " + firstStars + "★ убит=" + rDead.Ok);

                    var r2 = new SelfTest.WaitResult();
                    yield return SelfTest.WaitUntil(() => DevTools.LastSpawnedPrefab == "Draugr", 15f, r2);
                    ok2 = r2.Ok;
                    if (ok2) { secondPrefab = DevTools.LastSpawnedPrefab; secondStars = DevTools.LastSpawnedStars; }
                }
            }
            finally
            {
                Plugin.DevTestMobPool.Value = prevPool;
                Plugin.DevTestMobStars.Value = prevStars;
                Plugin.DevTestMobs.Value = prevOn;
                DevTools.ResetMobPool();
            }

            string info = "1-й=" + firstPrefab + " " + firstStars + "★, 2-й=" + secondPrefab + " " + secondStars + "★";
            if (!ok1) { SelfTest.Fail("первый моб из пула не появился за 15с | " + info); yield break; }
            if (firstStars != 1) { SelfTest.Fail("звёзды первого не по конфигу (ждали 1) | " + info); yield break; }
            if (!ok2) { SelfTest.Fail("после смерти следующий из пула не появился за 15с | " + info); yield break; }
            if (secondStars != 1) { SelfTest.Fail("звёзды второго не по конфигу (ждали 1) | " + info); yield break; }
            SelfTest.Pass(info);
        }
    }
}

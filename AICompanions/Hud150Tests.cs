using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Автотесты этапа hud150: компактная плашка слева, подробности по I, полоски HP в 150м,
    // метки врагов на карте.
    public static class Hud150Tests
    {
        public static void Register()
        {
            SelfTest.Register("hud150_bars", Hud150Bars, 60f);
            SelfTest.Register("minimap_enemies", MinimapEnemies, 60f);
            SelfTest.Register("hud_details_cycle", HudDetailsCycle, 30f);
        }

        // Мобы на 3 дистанциях ВНУТРИ EnemyHudRadius (доли радиуса — не фикс. метры: конфиг мог
        // измениться, напр. 150→60 по просьбе Ильи 25.10, ревью major-фикс) — полоска должна быть
        // видна без наведения; моб за радиусом (radius+40) — полоски быть не должно. Плюс 2 бойца
        // (союзники) на тех же дистанциях — задача ветки «над всеми врагами И союзниками» (минор).
        private static IEnumerator Hud150Bars()
        {
            float radius = Plugin.EnemyHudRadius.Value;
            float[] inside = new float[] { radius * 0.2f, radius * 0.6f, radius * 0.93f };
            var insideMobs = new List<Character>();
            for (int i = 0; i < inside.Length; i++)
            {
                var go = SelfTest.SpawnPrefab("Greyling", inside[i], (i - 1) * 2f);
                var mc = go != null ? go.GetComponent<Character>() : null;
                if (mc == null) { SelfTest.Fail("не создался моб на " + (int)inside[i] + "м"); yield break; }
                insideMobs.Add(mc);
            }
            float outsideDist = radius + 40f;
            var outGo = SelfTest.SpawnPrefab("Greyling", outsideDist, 0f);
            var outMob = outGo != null ? outGo.GetComponent<Character>() : null;
            if (outMob == null) { SelfTest.Fail("не создался моб за радиусом"); yield break; }

            // Союзники (бойцы) на 2 из контрольных дистанций — «полоски над всеми врагами и
            // союзниками» (ZADACHI:62), не только над врагами.
            var ally1 = SelfTest.SpawnCompanion(inside[1], 6f);
            var ally2 = SelfTest.SpawnCompanion(inside[2], -6f);
            if (ally1 == null || ally2 == null) { SelfTest.Fail("не призвались союзники"); yield break; }

            yield return new WaitForSeconds(1.5f);

            int okInside = 0;
            string parts = "";
            for (int i = 0; i < insideMobs.Count; i++)
            {
                var mc = insideMobs[i];
                bool shown = mc != null && EnemyHudRange.IsBarVisible(mc);
                float d = mc != null ? Vector3.Distance(mc.transform.position, SelfTest.Me.transform.position) : -1f;
                parts += (i > 0 ? ", " : "") + (int)inside[i] + "м(факт " + (int)d + "м)=" + (shown ? "видна" : "НЕТ");
                if (shown) okInside++;
            }
            bool outsideHidden = outMob == null || !EnemyHudRange.IsBarVisible(outMob);
            float dOut = outMob != null ? Vector3.Distance(outMob.transform.position, SelfTest.Me.transform.position) : -1f;

            bool ally1Shown = ally1.Body != null && EnemyHudRange.IsBarVisible(ally1.Body);
            bool ally2Shown = ally2.Body != null && EnemyHudRange.IsBarVisible(ally2.Body);
            string alliesInfo = "союзники: 1=" + (ally1Shown ? "видна" : "НЕТ") + ", 2=" + (ally2Shown ? "видна" : "НЕТ");

            string info = "радиус=" + (int)radius + "м | внутри: " + parts
                + " | снаружи " + (int)outsideDist + "м(факт " + (int)dOut + "м)=" + (outsideHidden ? "нет(верно)" : "ЕСТЬ(ошибка)")
                + " | " + alliesInfo;
            SelfTest.Log(info);
            if (okInside < inside.Length) { SelfTest.Fail(info); yield break; }
            if (!outsideHidden) { SelfTest.Fail(info); yield break; }
            if (!ally1Shown || !ally2Shown) { SelfTest.Fail(info); yield break; }
            SelfTest.Pass(info);
        }

        // Обычный враг (Greyling) и опасный (Skeleton, принудительно 2★) — оба получают пин,
        // опасный — особым значком, обычный — нет; после смерти/ухода пин должен пропасть.
        private static IEnumerator MinimapEnemies()
        {
            var normalGo = SelfTest.SpawnPrefab("Greyling", 10f, -4f);
            var normal = normalGo != null ? normalGo.GetComponent<Character>() : null;
            var dangerGo = SelfTest.SpawnPrefab("Skeleton", 10f, 4f);
            var danger = dangerGo != null ? dangerGo.GetComponent<Character>() : null;
            if (normal == null || danger == null) { SelfTest.Fail("не создались мобы"); yield break; }
            danger.SetLevel(3); // 1=база, 2=1★, 3=2★ — "опасный" по нашему правилу

            yield return new WaitForSeconds(1f);
            EnemyMapPins.Tick(); // не ждать таймер Plugin.Update (0.5с) — зовём напрямую

            bool normalPinned = EnemyMapPins.HasPin(normal);
            bool dangerPinned = EnemyMapPins.HasPin(danger);
            bool dangerSpecial = EnemyMapPins.IsDangerPinned(danger);
            bool normalSpecialWrong = EnemyMapPins.IsDangerPinned(normal);
            string info1 = "обычный: пин=" + normalPinned + " особый=" + normalSpecialWrong
                + " | опасный(2★): пин=" + dangerPinned + " особый=" + dangerSpecial;
            SelfTest.Log(info1);
            if (!normalPinned) { SelfTest.Fail("нет пина у обычного врага | " + info1); yield break; }
            if (!dangerPinned || !dangerSpecial) { SelfTest.Fail("опасный не получил особый значок | " + info1); yield break; }
            if (normalSpecialWrong) { SelfTest.Fail("обычный получил особый значок | " + info1); yield break; }

            // Смерть обычного врага: настоящий урон игры (как от оружия). РАЗОБРАНО (ревью,
            // major-фикс): урон проходил и реально убивал Greyling и раньше — но Character.OnDeath
            // (IL подтверждено) сам вызывает ZNetScene.Destroy() как ПОСЛЕДНИЙ шаг смерти, синхронно,
            // в тот же кадр/тик, что и RPC_Damage. К моменту любой проверки normal.IsDead()/GetHealth()
            // после урона объект уже unity-null (Destroy помечает его таким немедленно) — метод
            // физически не вызвать, false-null маскирует настоящую смерть под «не умер». Поэтому
            // раньше тест ВСЕГДА проходил через страховку Destroy, независимо от реального исхода.
            // Фикс: ловим момент смерти ДО уничтожения — через normal.m_onDeath (публичный Action,
            // Character.OnDeath вызывает его ДО своего ZNetScene.Destroy).
            bool reallyDied = false;
            normal.m_onDeath += delegate { reallyDied = true; };

            var hit = new HitData();
            hit.m_damage.m_damage = 99999f;
            hit.m_point = normal.transform.position;
            hit.m_dir = Vector3.down;
            normal.Damage(hit);
            yield return new WaitForSeconds(1f);
            bool diedForReal = reallyDied;
            // Теперь настоящая страховка: сработает только если урон и правда не убил.
            if (!diedForReal && normal != null && ZNetScene.instance != null) ZNetScene.instance.Destroy(normal.gameObject);
            // ZADACHI:62 — «удаление при уходе/смерти»: смерть (этот враг) и уход (danger ниже)
            // проверяем РАЗНЫМИ путями, не только через Destroy — теперь можем требовать честно.
            if (!diedForReal) { SelfTest.Fail("обычный враг не умер по-настоящему от летального урона (m_onDeath не сработал)"); yield break; }

            // Уход опасного: имитируем выходом из сцены (Destroy) — то же самое, что при уходе из радиуса.
            if (danger != null && ZNetScene.instance != null) ZNetScene.instance.Destroy(danger.gameObject);

            yield return new WaitForSeconds(1f);
            EnemyMapPins.Tick();
            bool normalGone = !EnemyMapPins.HasPin(normal);
            bool dangerGone = !EnemyMapPins.HasPin(danger);
            string info2 = "после смерти(" + (diedForReal ? "удар" : "страховка Destroy") + ")/ухода: обычный пин-пропал=" + normalGone + ", опасный пин-пропал=" + dangerGone;
            SelfTest.Log(info2);
            if (!normalGone || !dangerGone) { SelfTest.Fail(info2); yield break; }
            SelfTest.Pass(info1 + " | " + info2);
        }

        // Обработчик клавиши I (Plugin.CycleDetail) — звать прямо, без эмуляции Input.GetKeyDown.
        // 3 бойца: ожидаемая последовательность индексов 0,1,2,-1,0 (цикл по кругу).
        private static IEnumerator HudDetailsCycle()
        {
            var c1 = SelfTest.SpawnCompanion(2f, -2f);
            var c2 = SelfTest.SpawnCompanion(2f, 0f);
            var c3 = SelfTest.SpawnCompanion(2f, 2f);
            if (c1 == null || c2 == null || c3 == null) { SelfTest.Fail("не призвались все 3"); yield break; }
            yield return new WaitForSeconds(0.5f);
            if (CompanionManager.Alive().Count != 3) { SelfTest.Fail("в строю не 3 бойца: " + CompanionManager.Alive().Count); yield break; }

            Plugin.DetailIndex = -1; Plugin.DetailTarget = null; // детерминированный старт
            var seq = new List<int>();
            for (int i = 0; i < 5; i++)
            {
                Plugin.CycleDetail();
                seq.Add(Plugin.DetailIndex);
                yield return null;
            }
            string seqStr = "";
            for (int i = 0; i < seq.Count; i++) seqStr += (i > 0 ? "," : "") + seq[i];
            string info = "последовательность I: " + seqStr + " (ожидалось 0,1,2,-1,0)";
            SelfTest.Log(info);

            int[] expect = new int[] { 0, 1, 2, -1, 0 };
            bool ok = seq.Count == expect.Length;
            if (ok) for (int i = 0; i < expect.Length; i++) if (seq[i] != expect[i]) { ok = false; break; }
            if (!ok) { SelfTest.Fail(info); yield break; }
            SelfTest.Pass(info);
        }
    }
}

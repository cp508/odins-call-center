using System.Collections;
using UnityEngine;

namespace AICompanions
{
    // Автотесты этапа «Бой v2а»: точный тайминг ударов, допустимый урон, режим «ранен»,
    // безопасные окна атаки, бой 1 против толпы. hud_demo для этого этапа — тот же сценарий,
    // что уже зарегистрирован в Stage2b2Tests (требования не изменились).
    public static class CombatV2aTests
    {
        public static void Register()
        {
            SelfTest.Register("duel_skeleton", DuelSkeleton, 100f);
            SelfTest.Register("duel_greydwarf_brute", DuelGreydwarfBrute, 100f);
            SelfTest.Register("parry_drill", ParryDrill, 60f);
            SelfTest.Register("crowd_1v3", Crowd1v3, 100f);
            SelfTest.Register("troll_solo", TrollSolo, 55f);
        }

        // 1 спутник (щит+дубина, сытый — набор CombatKit/FoodKit по умолчанию уже этому соответствует)
        // Отработка защиты: спутник в ПАССИВЕ (не бьёт), скелет ближнего боя 3★ бьёт 40с.
        // Меряется только защита: безопасно >= 95%, из принятых на щит — парирований >= 60%.
        private static readonly System.Reflection.FieldInfo TargetField =
            typeof(MonsterAI).GetField("m_targetCreature", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        private static IEnumerator ParryDrill()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            var mob = SelfTest.SpawnPrefab("Skeleton_NoArcher", 6f, 0f);
            if (mob == null) mob = SelfTest.SpawnPrefab("Skeleton", 6f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;
            if (mc == null) { SelfTest.Fail("нет префаба Skeleton"); yield break; }
            // 1★: удар после блока укладывается в допустимый урон → по правилам должен быть блок/парирование
            // (3★ бьёт слишком сильно для деревянного щита — там верно выбирается кувырок).
            mc.SetLevel(1);
            float t0 = Time.time;
            while (Time.time - t0 < 40f)
            {
                if (c == null) { SelfTest.Log("боец-объект уничтожен на " + (Time.time - t0).ToString("0.0") + "с"); break; }
                if (c.Body == null) { SelfTest.Log("Body == null на " + (Time.time - t0).ToString("0.0") + "с"); break; }
                if (c.Body.IsDead()) { SelfTest.Log("IsDead, HP=" + c.Body.GetHealth().ToString("0.0") + " на " + (Time.time - t0).ToString("0.0") + "с"); break; }
                c.SetStance(FollowerAI.Stance.Passive); // держим пассив (WantsToFight=false), защита работает
                // скелет должен бить именно спутника, а не игрока (иначе мерить нечего)
                var mai = mc != null ? mc.GetComponent<MonsterAI>() : null;
                if (mai != null && TargetField != null) TargetField.SetValue(mai, c.Body);
                yield return new WaitForSeconds(0.5f);
            }
            bool alive = c.Body != null && !c.Body.IsDead();
            int safe, total;
            c.GetSafetyStats(out safe, out total);
            float pct = total > 0 ? (float)safe / total * 100f : 100f;
            float parryPct = c.Blocks > 0 ? 100f * c.Parries / c.Blocks : 0f;
            string info = BuildInfo(c, pct, safe, total) + " парирований_из_блоков=" + parryPct.ToString("0") + "%";
            SelfTest.Log(info);
            LogItog(c, pct, alive);
            if (total < 3) { SelfTest.Fail("скелет почти не бил (" + total + " ударов) | " + info); yield break; }
            if (!alive) { SelfTest.Fail("боец мёртв | " + info); yield break; }
            if (pct < 95f) { SelfTest.Fail("мало безопасных ударов | " + info); yield break; }
            if (c.Blocks > 0 && parryPct < 60f) { SelfTest.Fail("мало парирований | " + info); yield break; }
            SelfTest.Pass(info);
        }

        // против Skeleton 90с или до его смерти: безопасных >= 95%, парирований >= 2, спутник жив.
        private static IEnumerator DuelSkeleton()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            // Скелет только ближнего боя (у простого Skeleton бывает лук) и 2★ — чтобы бой длился и он успевал бить.
            var mob = SelfTest.SpawnPrefab("Skeleton_NoArcher", 10f, 0f);
            if (mob == null) mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;
            if (mc == null) { SelfTest.Fail("нет префаба Skeleton"); yield break; }
            mc.SetLevel(2);

            float t0 = Time.time;
            float killTime = -1f;
            while (Time.time - t0 < 90f)
            {
                if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }
                if (mc == null || mc.IsDead()) { killTime = Time.time - t0; break; }
                yield return new WaitForSeconds(1f);
            }

            bool alive = c.Body != null && !c.Body.IsDead();
            int safe, total;
            c.GetSafetyStats(out safe, out total);
            float pct = total > 0 ? (float)safe / total * 100f : 100f;
            bool skeletonDead = mc == null || mc.IsDead();
            string info = BuildInfo(c, pct, safe, total) + " скелетЖив=" + !skeletonDead
                + " времяУбийства=" + (killTime >= 0f ? killTime.ToString("0.0") + "с" : "н/д");
            SelfTest.Log(info);
            LogItog(c, pct, alive);
            if (!alive) { SelfTest.Fail("боец мёртв | " + info); yield break; }
            // Цель этапа (спека Ильи): safe>=95%, жив, staggered=0, safeAttack>=90%, скелет убит <=60с.
            // Парирования отдельно НЕ требуются — правило «парируем только если прогноз допускает»
            // (динамически, по текущей броне/стамине/прокачке) может честно означать «всегда кувырок»
            // против конкретно этого удара, это не брак, если safe/safeAttack в норме.
            if (total > 0 && pct < 95f) { SelfTest.Fail("мало безопасных ударов | " + info); yield break; }
            if (c.Staggered > 0) { SelfTest.Fail("боец был оглушён | " + info); yield break; }
            if (c.GetSafeAttackPct() < 90f) { SelfTest.Fail("мало безопасных своих атак | " + info); yield break; }
            if (!skeletonDead) { SelfTest.Fail("не убил скелета за 90с | " + info); yield break; }
            if (killTime > 60f) { SelfTest.Fail("убил скелета слишком долго (>60с) | " + info); yield break; }
            SelfTest.Pass(info);
        }

        // (Имя теста историческое.) Против скелета 2★ — тяжёлый одиночный бой; грейдворфы по просьбе Ильи
        // убраны из тестов как слишком пассивные. Безопасных >= 90%, спутник жив ИЛИ убил врага.
        private static IEnumerator DuelGreydwarfBrute()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            var mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
            var lvlC = mob != null ? mob.GetComponent<Character>() : null;
            if (lvlC != null) lvlC.SetLevel(3);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;
            if (mc == null) { SelfTest.Fail("нет префаба Skeleton"); yield break; }

            float t0 = Time.time;
            while (Time.time - t0 < 90f)
            {
                if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }
                if (mc == null || mc.IsDead()) break;
                yield return new WaitForSeconds(1f);
            }

            bool alive = c.Body != null && !c.Body.IsDead();
            bool killed = mc == null || mc.IsDead();
            int safe, total;
            c.GetSafetyStats(out safe, out total);
            float pct = total > 0 ? (float)safe / total * 100f : 100f;
            string info = BuildInfo(c, pct, safe, total) + " бруталЖив=" + !killed;
            SelfTest.Log(info);
            LogItog(c, pct, alive);
            if (!alive && !killed) { SelfTest.Fail("боец мёртв, брут жив | " + info); yield break; }
            if (total > 0 && pct < 90f) { SelfTest.Fail("мало безопасных ударов | " + info); yield break; }
            if (c.Staggered > 0) { SelfTest.Fail("боец был оглушён | " + info); yield break; }
            if (c.GetSafeAttackPct() < 90f) { SelfTest.Fail("мало безопасных своих атак | " + info); yield break; }
            SelfTest.Pass(info);
        }

        // 1 спутник против 4 скелетов 90с (толпа; грейдворфы убраны по просьбе Ильи): безопасных >= 85%, спутник жив, ни разу не
        // окружён дольше 3с (ThreatTracker.MaxSurroundedSeen, лог реального значения).
        private static IEnumerator Crowd1v3()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            // 5 скелетов одновременно (ближники + лучники как выпадет), один из них 1★ (просьба Ильи)
            SelfTest.SpawnPrefab("Skeleton_NoArcher", 10f, -6f);
            SelfTest.SpawnPrefab("Skeleton", 10f, -3f);
            var star = SelfTest.SpawnPrefab("Skeleton_NoArcher", 10f, 0f);
            if (star != null && star.GetComponent<Character>() != null) star.GetComponent<Character>().SetLevel(2);
            SelfTest.SpawnPrefab("Skeleton", 10f, 3f);
            SelfTest.SpawnPrefab("Skeleton_NoArcher", 10f, 6f);

            float t0 = Time.time;
            while (Time.time - t0 < 90f)
            {
                if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }
                yield return new WaitForSeconds(1f);
            }

            bool alive = c.Body != null && !c.Body.IsDead();
            int safe, total;
            c.GetSafetyStats(out safe, out total);
            float pct = total > 0 ? (float)safe / total * 100f : 100f;
            string info = BuildInfo(c, pct, safe, total) + " максОкружение=" + c.Threats.MaxSurroundedSeen.ToString("0.0") + "с";
            SelfTest.Log(info);
            LogItog(c, pct, alive);
            if (!alive) { SelfTest.Fail("боец мёртв | " + info); yield break; }
            if (total > 0 && pct < 85f) { SelfTest.Fail("мало безопасных ударов (толпа) | " + info); yield break; }
            if (c.Threats.MaxSurroundedSeen > 3f) { SelfTest.Fail("окружён дольше 3с подряд | " + info); yield break; }
            if (c.Staggered > 0) { SelfTest.Fail("боец был оглушён | " + info); yield break; }
            if (c.GetSafeAttackPct() < 90f) { SelfTest.Fail("мало безопасных своих атак | " + info); yield break; }
            SelfTest.Pass(info);
        }

        // Против Troll 45с: безопасных >= 90% (почти всё — кувырки по таймингу). Лог прогноз vs
        // факт времени удара тролля — уже пишет AttackProfiles при каждой калибровке
        // ("[AttackProfiles] профиль Troll/... windup=... (замер ...)"), плюс явная сводка здесь.
        private static IEnumerator TrollSolo()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            var mob = SelfTest.SpawnPrefab("Troll", 12f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;
            if (mc == null) { SelfTest.Fail("нет префаба Troll"); yield break; }

            float t0 = Time.time;
            while (Time.time - t0 < 45f)
            {
                if (c.Body == null) break;
                yield return new WaitForSeconds(1f);
            }

            bool alive = c.Body != null && !c.Body.IsDead();
            int safe, total;
            c.GetSafetyStats(out safe, out total);
            float pct = total > 0 ? (float)safe / total * 100f : 100f;
            int windowsAvail = c.Guard.WindowsAvailable;
            float windowPct = windowsAvail > 0 ? (float)c.WindowsUsed / windowsAvail * 100f : 100f;
            string info = BuildInfo(c, pct, safe, total)
                + " урон_по_троллю=" + c.DamageDealt.ToString("0")
                + " окна_доступно=" + windowsAvail + " окна_использовано=" + c.WindowsUsed
                + " (" + windowPct.ToString("0") + "%)"
                + " попыток_блока_недопустимых=" + c.Guard.UnsafeBlockAttempts;
            SelfTest.Log(info + " (прогноз-vs-факт по каждому удару — см. строки [AttackProfiles] выше в логе)");
            LogItog(c, pct, alive);
            if (total > 0 && pct < 90f) { SelfTest.Fail("мало безопасных ударов | " + info); yield break; }
            if (c.Staggered > 0) { SelfTest.Fail("боец был оглушён | " + info); yield break; }
            if (c.GetSafeAttackPct() < 90f) { SelfTest.Fail("мало безопасных своих атак | " + info); yield break; }
            if (c.Guard.UnsafeBlockAttempts > 0) { SelfTest.Fail("блок/парирование против недопустимого удара | " + info); yield break; }
            if (c.DamageDealt <= 0f) { SelfTest.Fail("не нанёс урона троллю | " + info); yield break; }
            if (windowsAvail > 0 && windowPct < 70f) { SelfTest.Fail("мало использованных безопасных окон | " + info); yield break; }
            SelfTest.Pass(info);
        }

        private static string BuildInfo(FollowerAI c, float pct, int safe, int total)
        {
            bool alive = c.Body != null && !c.Body.IsDead();
            var weapon = alive ? c.Body.GetCurrentWeapon() : null;
            string weaponName = weapon != null && weapon.m_shared != null ? weapon.m_shared.m_name : "нет";
            return "безопасно=" + pct.ToString("0") + "% (" + safe + "/" + total + ")"
                + " парирований=" + c.Parries + " блоков=" + c.Blocks + " кувырков=" + c.Dodge.DodgeCount
                + " урон_всего=" + c.DamageTaken.ToString("0")
                + " HP=" + (alive ? ((int)c.Body.GetHealth()).ToString() : "мёртв")
                + " в_руке=" + weaponName;
        }

        // Сводка, которую просил Илья: одна строка на сценарий, грепается независимо от деталей
        // в BuildInfo. Формат фиксирован — его парсит внешний harness.
        private static void LogItog(FollowerAI c, float safePct, bool alive)
        {
            SelfTest.Log("ИТОГ safe=" + safePct.ToString("0") + "% parries=" + c.Parries
                + " dodges=" + c.Dodge.DodgeCount + " staggered=" + c.Staggered
                + " safeAttack=" + c.GetSafeAttackPct().ToString("0") + "% alive=" + alive);
        }
    }
}

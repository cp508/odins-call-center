using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Автотесты этапа «Классы»: свои наборы вещей/еды на класс + переключение оружия по резисту.
    public static class ClassesTests
    {
        public static void Register()
        {
            SelfTest.Register("classes_kits", ClassesKits, 40f);
            SelfTest.Register("weapon_switch_resist", WeaponSwitchResist, 90f);
        }

        // 4 бойца подряд после чистого DismissAll — классы по кругу Tank/Universal/Archer/Fighter
        // (FollowerAI._classCounter сброшен фреймворком перед сценарием). Проверяем: роли у всех
        // разные, наборы (экипировка) у всех разные, вещи хозяина не тронуты.
        private static IEnumerator ClassesKits()
        {
            var owner = SelfTest.Me;
            int ownerItemsBefore = owner.GetInventory().GetAllItems().Count;
            var ownerWeaponBefore = owner.GetCurrentWeapon();

            var mates = new List<FollowerAI>();
            for (int i = 0; i < 4; i++)
            {
                var c = SelfTest.SpawnCompanion(2f, i * 1.5f - 2f);
                if (c == null) { SelfTest.Fail("боец #" + i + " не призвался"); yield break; }
                mates.Add(c);
                yield return new WaitForSeconds(0.3f);
            }
            // Время на доэкипировку (TickBody-ретрай до 3с, если первая попытка молча отказала).
            yield return new WaitForSeconds(3.5f);

            var roles = new List<string>();
            var fingerprints = new List<string>();
            foreach (var c in mates)
            {
                if (c.Body == null) { SelfTest.Fail("боец пропал во время настройки"); yield break; }
                roles.Add(c.RoleLabel());
                fingerprints.Add(Fingerprint(c));
            }

            string rolesStr = string.Join(", ", roles.ToArray());
            string fpStr = string.Join(" | ", fingerprints.ToArray());
            SelfTest.Log("роли: " + rolesStr);
            SelfTest.Log("наборы: " + fpStr);

            int uniqueRoles = CountUnique(roles);
            int uniqueKits = CountUnique(fingerprints);

            int ownerItemsAfter = owner.GetInventory().GetAllItems().Count;
            var ownerWeaponAfter = owner.GetCurrentWeapon();
            bool ownerUntouched = ownerItemsAfter == ownerItemsBefore && ownerWeaponAfter == ownerWeaponBefore;

            string info = "ролей разных=" + uniqueRoles + "/4 наборов разных=" + uniqueKits + "/4"
                + " хозяин_вещей=" + ownerItemsBefore + "->" + ownerItemsAfter
                + " хозяин_оружие=" + (ownerWeaponBefore == ownerWeaponAfter ? "не менялось" : "ИЗМЕНИЛОСЬ");

            if (uniqueRoles < 4) { SelfTest.Fail("роли повторяются | " + info); yield break; }
            if (uniqueKits < 4) { SelfTest.Fail("наборы совпадают | " + info); yield break; }
            if (!ownerUntouched) { SelfTest.Fail("вещи хозяина задеты | " + info); yield break; }
            SelfTest.Pass(info);
        }

        private static string Fingerprint(FollowerAI c)
        {
            var sums = CompanionInventory.SummarizeEquipped(c.Body);
            var names = new List<string>();
            foreach (var s in sums) names.Add(s.Name);
            names.Sort();
            return string.Join("+", names.ToArray());
        }

        private static int CountUnique(List<string> values)
        {
            var set = new HashSet<string>(values);
            return set.Count;
        }

        // Боец переведён в класс Боец (2 оружия разного типа урона: колющее SpearFlint + рубящее
        // AxeFlint, см. Plugin.ClassFighterWeapons) явным CycleRole — не зависит от порядка спавна.
        // Фаза 1-2: резист цели переставляется руками (Character.m_damageModifiers — публичное
        // поле), без боя (Пассив), и проверяется, что боец берёт правильное по формуле оружие.
        // Фаза 3: боец реально дерётся (Защита) с целью, резист которой постоянно меняется —
        // проверяем по факту (ThreatTracker.Threats[].Attacking), что НИ ОДНО переключение не
        // совпало с летящим ударом.
        private static IEnumerator WeaponSwitchResist()
        {
            var c = SelfTest.SpawnCompanion(2f, 0f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(0.3f);
            int guard = 0;
            while (c.Role != CompanionRole.Fighter && guard++ < 4) { c.CycleRole(); yield return new WaitForSeconds(0.3f); }
            yield return new WaitForSeconds(3.5f); // доэкипировка набора Бойца

            if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }
            var weapon = c.Body.GetCurrentWeapon();
            if (c.Role != CompanionRole.Fighter || weapon == null)
            {
                SelfTest.Fail("не стал Бойцом/нет оружия (роль=" + c.RoleLabel() + ")"); yield break;
            }

            var mob = SelfTest.SpawnPrefab("Greyling", 8f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;
            if (mc == null) { SelfTest.Fail("нет префаба Greyling"); yield break; }

            c.SetStance(FollowerAI.Stance.Passive); // фазы 1-2 — без боя, чисто проверка выбора оружия
            yield return new WaitForSeconds(0.3f);

            // Фаза 1: цель слаба к колющему, устойчива к рубящему/дробящему -> ждём SpearFlint.
            SetResist(mc, HitData.DamageModifier.Weak, HitData.DamageModifier.Resistant, HitData.DamageModifier.Resistant);
            yield return new WaitForSeconds(Plugin.ClassWeaponSwitchInterval.Value + 1f);
            bool gotSpear = IsEquippedPrefab(c, "SpearFlint");
            SelfTest.Log("фаза1 (слаб к колющему) оружие=" + WeaponName(c) + " взял_колющее=" + gotSpear + " | " + c.Weapons.LastSwitchInfo);

            // Фаза 2: переставили наоборот — слаба к рубящему, устойчива к колющему -> ждём AxeFlint.
            SetResist(mc, HitData.DamageModifier.Resistant, HitData.DamageModifier.Weak, HitData.DamageModifier.Resistant);
            yield return new WaitForSeconds(Plugin.ClassWeaponSwitchInterval.Value + 1f);
            bool gotAxe = IsEquippedPrefab(c, "AxeFlint");
            SelfTest.Log("фаза2 (слаб к рубящему) оружие=" + WeaponName(c) + " взял_рубящее=" + gotAxe + " | " + c.Weapons.LastSwitchInfo);

            // Фаза 3: реальный бой, резист дёргается каждые 2с — считаем смены оружия и ловим совпадение
            // с летящим ударом по факту (независимая проверка, не через внутренние счётчики WeaponSwitch).
            c.SetStance(FollowerAI.Stance.Defend);
            string lastSeen = WeaponName(c);
            int switches = 0;
            int violations = 0;
            float flipTimer = 0f;
            bool favorPierce = true;
            float t0 = Time.time;
            while (Time.time - t0 < 20f)
            {
                if (c.Body == null || c.Body.IsDead()) break;
                if (mc == null || mc.IsDead())
                {
                    mob = SelfTest.SpawnPrefab("Greyling", 8f, 0f);
                    mc = mob != null ? mob.GetComponent<Character>() : null;
                }
                flipTimer -= 0.2f;
                if (mc != null && flipTimer <= 0f)
                {
                    flipTimer = 2f;
                    favorPierce = !favorPierce;
                    if (favorPierce) SetResist(mc, HitData.DamageModifier.Weak, HitData.DamageModifier.Resistant, HitData.DamageModifier.Resistant);
                    else SetResist(mc, HitData.DamageModifier.Resistant, HitData.DamageModifier.Weak, HitData.DamageModifier.Resistant);
                }
                string now = WeaponName(c);
                if (now != lastSeen)
                {
                    switches++;
                    if (c.Weapons.AnyIncomingAttack()) violations++;
                    lastSeen = now;
                }
                yield return new WaitForSeconds(0.2f);
            }

            bool alive = c.Body != null && !c.Body.IsDead();
            string info = "фаза1_колющее=" + gotSpear + " фаза2_рубящее=" + gotAxe + " бой_смен=" + switches + " нарушений=" + violations
                + " отложено_из-за-удара=" + c.Weapons.DeferredByIncoming + " живой=" + alive;
            SelfTest.Log(info);

            if (!alive) { SelfTest.Fail("боец мёртв | " + info); yield break; }
            if (!gotSpear) { SelfTest.Fail("против слабости к колющему не взял колющее | " + info); yield break; }
            if (!gotAxe) { SelfTest.Fail("против слабости к рубящему не взял рубящее | " + info); yield break; }
            if (violations > 0) { SelfTest.Fail("переключение во время летящего удара | " + info); yield break; }
            SelfTest.Pass(info);
        }

        private static string WeaponName(FollowerAI c)
        {
            if (c.Body == null) return "?";
            var w = c.Body.GetCurrentWeapon();
            return w != null && w.m_shared != null ? w.m_shared.m_name : "нет";
        }

        // Сравнение по ССЫЛКЕ на ItemData конкретного префаба из рюкзака, а не по m_shared.m_name
        // (это локализационный ключ вида "$item_spearflint", сравнивать с "SpearFlint" бессмысленно).
        private static bool IsEquippedPrefab(FollowerAI c, string prefabName)
        {
            if (c.Body == null) return false;
            var w = c.Body.GetCurrentWeapon();
            if (w == null) return false;
            var target = c.Body.GetInventory().GetItem(prefabName, -1, true);
            return target != null && w == target;
        }

        private static void SetResist(Character mc, HitData.DamageModifier pierce, HitData.DamageModifier slash, HitData.DamageModifier blunt)
        {
            var mods = mc.m_damageModifiers;
            mods.m_pierce = pierce;
            mods.m_slash = slash;
            mods.m_blunt = blunt;
            mc.m_damageModifiers = mods;
        }
    }
}

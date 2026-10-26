using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Автотесты этапа 2б-2: еда (желудок), стамина, блок/парирование, кувырок.
    public static class Stage2b2Tests
    {
        public static void Register()
        {
            SelfTest.Register("food_eat", FoodEat, 60f);
            SelfTest.Register("stamina_reserve", StaminaReserve, 90f);
            SelfTest.Register("block_parry", BlockParry, 90f);
            SelfTest.Register("dodge_strong", DodgeStrong, 70f);
            SelfTest.Register("hud_demo", HudDemo, 75f);
        }

        // 15с на заполнение 3 слотов желудка; проверка формулы maxHP/maxStamina = база + сумма еды (±1);
        // ещё 20с — таймеры еды должны убывать.
        private static IEnumerator FoodEat()
        {
            var c = SelfTest.SpawnCompanion(2f, 0f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(15f);
            if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }

            var owner = SelfTest.Me;
            int slots = c.Food.Slots.Count;
            float sumHp = 0f, sumStam = 0f;
            foreach (var s in c.Food.Slots) { sumHp += s.Health; sumStam += s.Stamina; }
            float expectHp = owner.m_baseHP + sumHp;
            float expectStam = owner.m_baseStamina + sumStam;
            float actualHp = c.Body.GetMaxHealth();
            float actualStam = c.Stamina.Max;

            string num = "слотов=" + slots + " maxHP=" + actualHp.ToString("0.0") + "(ожид " + expectHp.ToString("0.0") + ")"
                + " maxStam=" + actualStam.ToString("0.0") + "(ожид " + expectStam.ToString("0.0") + ")"
                + " базаHP=" + owner.m_baseHP + " базаStam=" + owner.m_baseStamina;
            SelfTest.Log(num);

            if (slots != 3) { SelfTest.Fail("слотов желудка " + slots + " != 3 | " + num); yield break; }
            if (Mathf.Abs(actualHp - expectHp) > 1f) { SelfTest.Fail("maxHP не по формуле | " + num); yield break; }
            if (Mathf.Abs(actualStam - expectStam) > 1f) { SelfTest.Fail("maxStam не по формуле | " + num); yield break; }
            if (actualHp <= owner.m_baseHP) { SelfTest.Fail("maxHP не больше базы | " + num); yield break; }
            if (actualStam <= owner.m_baseStamina) { SelfTest.Fail("maxStam не больше базы | " + num); yield break; }

            var before = new float[3];
            for (int i = 0; i < 3; i++) before[i] = c.Food.Slots[i].Time;

            yield return new WaitForSeconds(20f);
            if (c.Body == null) { SelfTest.Fail("боец пропал на втором ожидании"); yield break; }

            int decreased = 0;
            var after = new List<string>();
            for (int i = 0; i < Mathf.Min(3, c.Food.Slots.Count); i++)
            {
                after.Add(before[i].ToString("0.0") + "->" + c.Food.Slots[i].Time.ToString("0.0"));
                if (c.Food.Slots[i].Time < before[i] - 5f) decreased++;
            }
            string decStr = string.Join(", ", after.ToArray());
            SelfTest.Log("через 20с: " + decStr);
            if (decreased < 2) { SelfTest.Fail("таймеры еды не убывают: " + decStr); yield break; }

            SelfTest.Pass(num + " | убыло таймеров: " + decreased + "/3");
        }

        // Против 3 грейлингов 60с: атака не должна стартовать при стамине < резерва (нарушений = 0),
        // спутник жив. Блок/кувырок могут уходить ниже резерва — это не нарушение.
        private static IEnumerator StaminaReserve()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            SelfTest.SpawnPrefab("Greyling", 10f, -5f);
            SelfTest.SpawnPrefab("Greyling", 10f, 0f);
            SelfTest.SpawnPrefab("Greyling", 10f, 5f);

            float t0 = Time.time;
            while (Time.time - t0 < 60f)
            {
                if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }
                yield return new WaitForSeconds(1f);
            }

            bool alive = c.Body != null && !c.Body.IsDead();
            string info = "нарушений=" + c.ReserveViolations + " блоков=" + c.Blocks
                + " стамина=" + (int)c.Stamina.Current + "/" + (int)c.Stamina.Max
                + " HP=" + (alive ? ((int)c.Body.GetHealth()).ToString() : "мёртв");
            if (c.ReserveViolations > 0) { SelfTest.Fail(info); yield break; }
            if (!alive) { SelfTest.Fail("боец мёртв | " + info); yield break; }
            SelfTest.Pass(info);
        }

        // Против Skeleton 60с (респавн по гибели, чтобы давление на блок было весь тест):
        // блоков ≥ 3, парирований ≥ 1, спутник жив.
        private static IEnumerator BlockParry()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            var mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;

            float t0 = Time.time;
            while (Time.time - t0 < 60f)
            {
                if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }
                if (mc == null || mc.IsDead())
                {
                    mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
                    mc = mob != null ? mob.GetComponent<Character>() : null;
                }
                yield return new WaitForSeconds(1f);
            }

            bool alive = c.Body != null && !c.Body.IsDead();
            string info = "блоков=" + c.Blocks + " парирований=" + c.Parries
                + " HP=" + (alive ? ((int)c.Body.GetHealth()).ToString() : "мёртв");
            if (c.Blocks < 3) { SelfTest.Fail("мало блоков | " + info); yield break; }
            if (c.Parries < 1) { SelfTest.Fail("нет парирований | " + info); yield break; }
            if (!alive) { SelfTest.Fail("боец мёртв | " + info); yield break; }
            SelfTest.Pass(info);
        }

        // Против Troll (12м) 40с: кувырков ≥ 2. Смерть спутника — не FAIL, если кувырки были.
        private static IEnumerator DodgeStrong()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            var mob = SelfTest.SpawnPrefab("Troll", 12f, 0f);
            if (mob == null) { SelfTest.Fail("нет префаба Troll"); yield break; }

            float hpBefore = c.Body.GetHealth();
            float t0 = Time.time;
            while (Time.time - t0 < 40f)
            {
                yield return new WaitForSeconds(1f);
                if (c.Body == null) break;
            }

            bool alive = c.Body != null && !c.Body.IsDead();
            string hpInfo = alive ? ((int)hpBefore) + "->" + (int)c.Body.GetHealth() : ((int)hpBefore) + "->погиб";
            string info = "кувырков=" + c.Dodge.DodgeCount + " блоков=" + c.Blocks + " HP " + hpInfo;
            if (c.Dodge.DodgeCount < 2) { SelfTest.Fail(info); yield break; }
            SelfTest.Pass(info);
        }

        // Демо-сценарий для визуальной проверки HUD (не боевая проверка): 2 спутника рядом с
        // игроком, каждому — немного лута разных видов, они едят; держит 60с (успеть
        // скриншотами проверить плашку: HP, стамину, «надето», желудок, рюкзак, запас еды).
        private static IEnumerator HudDemo()
        {
            var c1 = SelfTest.SpawnCompanion(2f, -1.5f);
            var c2 = SelfTest.SpawnCompanion(2f, 1.5f);
            if (c1 == null || c2 == null) { SelfTest.Fail("не призвались"); yield break; }
            yield return new WaitForSeconds(1f);
            if (c1.Body == null || c2.Body == null) { SelfTest.Fail("боец пропал сразу после призыва"); yield break; }

            int n1 = GiveDemoLoot(c1);
            int n2 = GiveDemoLoot(c2);
            SelfTest.Log("бойцы призваны, выдано предметов: " + n1 + " и " + n2 + " — держим 60с для скриншотов плашки");

            float t0 = Time.time;
            while (Time.time - t0 < 60f)
            {
                if (c1.Body == null || c2.Body == null) { SelfTest.Fail("боец пропал во время демо"); yield break; }
                yield return new WaitForSeconds(2f);
            }

            bool alive = !c1.Body.IsDead() && !c2.Body.IsDead();
            string info = "боец1: желудок=" + c1.Food.Slots.Count + "/3 рюкзак=" + CompanionInventory.Summarize(c1.Body).Items.Count + " видов"
                + " | боец2: желудок=" + c2.Food.Slots.Count + "/3 рюкзак=" + CompanionInventory.Summarize(c2.Body).Items.Count + " видов";
            if (!alive) { SelfTest.Fail("боец мёртв | " + info); yield break; }
            SelfTest.Pass(info);
        }

        private static readonly string[] DemoLootPrefabs = { "Wood", "Stone", "DeerHide" };
        private static readonly int[] DemoLootCounts = { 10, 6, 2 };

        private static int GiveDemoLoot(FollowerAI c)
        {
            var inv = c.Body.GetInventory();
            int given = 0;
            for (int i = 0; i < DemoLootPrefabs.Length; i++)
            {
                var prefab = ZNetScene.instance.GetPrefab(DemoLootPrefabs[i]);
                if (prefab == null) { SelfTest.Log("нет лут-префаба " + DemoLootPrefabs[i]); continue; }
                if (inv.AddItem(prefab, DemoLootCounts[i])) given++;
            }
            return given;
        }
    }
}

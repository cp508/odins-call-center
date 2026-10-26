using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace AICompanions
{
    // Разведка для предложений по прокачке (ветка proposals, Илья 26.10 ночь: "карта прокачки по
    // классам и тирам" + "самообеспечение" — ОБСУЖДАЕМ, не реализуем). dump8/il8 снимают только
    // сигнатуры типов сборки (см. _research/ahead_classes_craft_cook.md), а не runtime-списки живой
    // игры — поэтому нужен свой автотест, который реально проходит ObjectDB.instance.m_items и
    // m_recipes и пишет РЕАЛЬНЫЕ цифры в TSV. Ничего не чинит и не боевой: без спутников/мобов.
    public static class ProposalsDumpTests
    {
        // Путь СВОЕЙ ветки (не профиль Ильи) — как и остальные dev-дампы мода, в C:\temp\aic.
        private const string OutPath = "C:\\temp\\aic\\proposals_items.tsv";

        private const string StationsOutPath = "C:\\temp\\aic\\proposals_stations.tsv";

        public static void Register()
        {
            SelfTest.Register("dump_items", DumpItems, 60f);
            SelfTest.Register("dump_stations", DumpStations, 60f);
        }

        // Самообеспечение: что компаньон варит/жарит сам (CookingStation.m_conversion — простая готовка
        // "положил сырое -> жду -> готово", НЕ через Recipe/ObjectDB.m_recipes, см. _research/ahead_classes_craft_cook.md
        // §5) и какой уровень станции (кузня/верстак) нужен хозяину (CraftingStation на префабах из ZNetScene).
        private static IEnumerator DumpStations()
        {
            if (ZNetScene.instance == null) { SelfTest.Fail("нет ZNetScene.instance"); yield break; }

            var sb = new StringBuilder();
            sb.Append("kind\tstation\tcol1\tcol2\tcol3\tcol4\n");
            int nCraft = 0, nCook = 0, nConv = 0, i = 0;

            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                i++;
                if (go == null) continue;
                var craft = go.GetComponent<CraftingStation>();
                if (craft != null)
                {
                    nCraft++;
                    sb.Append("CRAFT\t").Append(Safe(LocalizedName(craft.m_name))).Append('\t')
                      .Append(craft.m_craftingSkill).Append('\t')
                      .Append(craft.m_craftRequireFire).Append('\t')
                      .Append(craft.m_craftRequireRoof).Append('\t')
                      .Append(craft.m_upgrader).Append('\n');
                }
                var cook = go.GetComponent<CookingStation>();
                if (cook != null)
                {
                    nCook++;
                    sb.Append("COOKSTATION\t").Append(Safe(LocalizedName(cook.m_name))).Append('\t')
                      .Append(cook.m_requireFire).Append('\t')
                      .Append(cook.m_useFuel).Append('\t')
                      .Append(cook.m_slots != null ? cook.m_slots.Length : 0).Append("\t\n");
                    if (cook.m_conversion != null)
                    {
                        foreach (var conv in cook.m_conversion)
                        {
                            if (conv == null || conv.m_from == null || conv.m_to == null) continue;
                            nConv++;
                            sb.Append("COOKCONV\t").Append(Safe(LocalizedName(cook.m_name))).Append('\t')
                              .Append(conv.m_from.gameObject.name).Append('\t')
                              .Append(conv.m_to.gameObject.name).Append('\t')
                              .Append(conv.m_cookTime.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("\t\n");
                        }
                    }
                }
                if (i % 200 == 0) yield return null;
            }

            string info;
            try
            {
                File.WriteAllText(StationsOutPath, sb.ToString());
                info = "станций_крафт=" + nCraft + " станций_готовки=" + nCook + " конверсий=" + nConv + " файл=" + StationsOutPath;
            }
            catch (Exception ex) { SelfTest.Fail("не записал файл " + StationsOutPath + ": " + ex.Message); yield break; }

            if (File.Exists(StationsOutPath) && nCraft > 0) SelfTest.Pass(info);
            else SelfTest.Fail(info + " (нужно файл записан и станций крафта >0)");
        }

        private static IEnumerator DumpItems()
        {
            if (ObjectDB.instance == null) { SelfTest.Fail("нет ObjectDB.instance"); yield break; }

            // Рецепт по предмету: Recipe.m_item — ссылка на ItemDrop префаба, его gameObject и есть
            // ключ (m_items содержит те же префабы). Один предмет — максимум один рецепт на пред.
            var recipeByItem = new Dictionary<GameObject, Recipe>();
            foreach (var r in ObjectDB.instance.m_recipes)
            {
                if (r == null || r.m_item == null || r.m_item.gameObject == null) continue;
                if (!recipeByItem.ContainsKey(r.m_item.gameObject)) recipeByItem[r.m_item.gameObject] = r;
            }

            var sb = new StringBuilder();
            sb.Append("prefab\tname\tname_key\titemType\tskillType\tmaxQuality\tweight\tvalue\t")
              .Append("dmg_q1\tdmg_qmax\tarmor_q1\tarmor_qmax\tblock_q1\tblock_qmax\tparry_q1\tparry_qmax\t")
              .Append("atkStamina\tatkRange\tatkAngle\tdurability_q1\tdurability_qmax\t")
              .Append("food_hp\tfood_stam\tfood_eitr\tfood_regen\tfood_burn\t")
              .Append("se_name\tse_hp_front\tse_hp_over\tse_hp_dur\tse_stam_front\tse_stam_over\tse_stam_dur\t")
              .Append("se_eitr_front\tse_eitr_over\tse_eitr_dur\tse_armor_add\tse_armor_mult\tse_speed\tse_ttl\t")
              .Append("station\tstationLevel\tmaterials\n");

            int n = 0, errors = 0, i = 0;
            foreach (var go in ObjectDB.instance.m_items)
            {
                i++;
                if (go == null) continue;
                var drop = go.GetComponent<ItemDrop>();
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) continue;
                try
                {
                    AppendRow(sb, go, drop, recipeByItem);
                    n++;
                }
                catch (Exception ex)
                {
                    errors++;
                    SelfTest.Log("дамп предмета " + go.name + " упал: " + ex.Message);
                }
                if (i % 80 == 0) yield return null; // не держать кадр на большом списке
            }

            // Координатор (проверка вопроса 1 squad_classes_spec): урон голыми руками без оружия —
            // Humanoid.m_unarmedWeapon (приватного аналога в ObjectDB.m_items нет, он не предмет для
            // подбора, а дефолтная "заглушка" атаки персонажа). Берём с живого игрока в тестовом мире.
            if (SelfTest.Me != null && SelfTest.Me.m_unarmedWeapon != null)
            {
                var uGo = SelfTest.Me.m_unarmedWeapon.gameObject;
                var uDrop = uGo != null ? uGo.GetComponent<ItemDrop>() : null;
                if (uDrop != null && uDrop.m_itemData != null && uDrop.m_itemData.m_shared != null)
                {
                    try { AppendRow(sb, uGo, uDrop, recipeByItem); n++; }
                    catch (Exception ex) { SelfTest.Log("дамп голых рук упал: " + ex.Message); }
                }
            }

            string info;
            try
            {
                string dir = Path.GetDirectoryName(OutPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(OutPath, sb.ToString());
                info = "предметов=" + n + " ошибок=" + errors + " файл=" + OutPath;
            }
            catch (Exception ex) { SelfTest.Fail("не записал файл " + OutPath + ": " + ex.Message); yield break; }

            if (File.Exists(OutPath) && n > 300) SelfTest.Pass(info);
            else SelfTest.Fail(info + " (нужно файл записан и предметов >300)");
        }

        private static void AppendRow(StringBuilder sb, GameObject go, ItemDrop drop, Dictionary<GameObject, Recipe> recipeByItem)
        {
            var item = drop.m_itemData;
            var shared = item.m_shared;
            int maxQ = Mathf.Max(1, shared.m_maxQuality);

            sb.Append(go.name).Append('\t');
            sb.Append(Safe(LocalizedName(shared.m_name))).Append('\t');
            sb.Append(Safe(shared.m_name)).Append('\t');
            sb.Append(shared.m_itemType).Append('\t');
            sb.Append(shared.m_skillType).Append('\t');
            sb.Append(maxQ).Append('\t');
            sb.Append(shared.m_weight.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(shared.m_value).Append('\t');

            sb.Append(DmgStr(item, 1)).Append('\t');
            sb.Append(DmgStr(item, maxQ)).Append('\t');

            sb.Append(SafeNum(item, 1, maxQ, 0)).Append('\t');  // armor_q1
            sb.Append(SafeNum(item, 1, maxQ, 1)).Append('\t');  // armor_qmax
            sb.Append(SafeNum(item, 1, maxQ, 2)).Append('\t');  // block_q1
            sb.Append(SafeNum(item, 1, maxQ, 3)).Append('\t');  // block_qmax
            sb.Append(SafeNum(item, 1, maxQ, 4)).Append('\t');  // parry_q1
            sb.Append(SafeNum(item, 1, maxQ, 5)).Append('\t');  // parry_qmax

            float atkStamina = shared.m_attack != null ? shared.m_attack.m_attackStamina : 0f;
            float atkRange = shared.m_attack != null ? shared.m_attack.m_attackRange : 0f;
            float atkAngle = shared.m_attack != null ? shared.m_attack.m_attackAngle : 0f;
            sb.Append(atkStamina.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(atkRange.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(atkAngle.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');

            sb.Append(SafeNum(item, 1, maxQ, 6)).Append('\t'); // durability_q1
            sb.Append(SafeNum(item, 1, maxQ, 7)).Append('\t'); // durability_qmax

            sb.Append(shared.m_food.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(shared.m_foodStamina.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(shared.m_foodEitr.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(shared.m_foodRegen.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(shared.m_foodBurnTime.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');

            var se = shared.m_consumeStatusEffect as SE_Stats;
            if (se != null)
            {
                sb.Append(Safe(LocalizedName(se.m_name))).Append('\t');
                sb.Append(se.m_healthUpFront.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_healthOverTime.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_healthOverTimeDuration.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_staminaUpFront.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_staminaOverTime.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_staminaOverTimeDuration.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_eitrUpFront.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_eitrOverTime.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_eitrOverTimeDuration.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_addArmor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_armorMultiplier.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_speedModifier.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(se.m_ttl.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
            }
            else
            {
                // 14 пустых полей se_* (как в ветке выше, по одному на каждый Append().Append('\t')) — ровно 14 '\t'.
                sb.Append("\t\t\t\t\t\t\t\t\t\t\t\t\t\t");
            }

            Recipe recipe;
            if (recipeByItem.TryGetValue(go, out recipe) && recipe != null)
            {
                string station = recipe.m_craftingStation != null ? LocalizedName(recipe.m_craftingStation.m_name) : "(рука)";
                sb.Append(Safe(station)).Append('\t');
                sb.Append(recipe.m_minStationLevel).Append('\t');
                sb.Append(Safe(Materials(recipe)));
            }
            else
            {
                // 3 пустых поля (station/stationLevel/materials), последнее без своего '\t' перед '\n' — ровно 2 '\t'.
                sb.Append("\t\t");
            }
            sb.Append('\n');
        }

        private static string Materials(Recipe recipe)
        {
            if (recipe.m_resources == null || recipe.m_resources.Length == 0) return "";
            var parts = new List<string>();
            foreach (var req in recipe.m_resources)
            {
                if (req == null || req.m_resItem == null || req.m_resItem.gameObject == null) continue;
                string name = req.m_resItem.gameObject.name;
                parts.Add(name + "=" + req.m_amount + (req.m_amountPerLevel > 0 ? "(+" + req.m_amountPerLevel + "/ур)" : ""));
            }
            return string.Join(";", parts.ToArray());
        }

        private static string DmgStr(ItemDrop.ItemData item, int quality)
        {
            HitData.DamageTypes d;
            try { d = item.GetDamage(quality, 0f); }
            catch { return ""; }
            var parts = new List<string>();
            AddIfNonZero(parts, "blunt", d.m_blunt);
            AddIfNonZero(parts, "slash", d.m_slash);
            AddIfNonZero(parts, "pierce", d.m_pierce);
            AddIfNonZero(parts, "chop", d.m_chop);
            AddIfNonZero(parts, "pickaxe", d.m_pickaxe);
            AddIfNonZero(parts, "fire", d.m_fire);
            AddIfNonZero(parts, "frost", d.m_frost);
            AddIfNonZero(parts, "lightning", d.m_lightning);
            AddIfNonZero(parts, "poison", d.m_poison);
            AddIfNonZero(parts, "spirit", d.m_spirit);
            float total;
            try { total = d.GetTotalDamage(); } catch { total = 0f; }
            parts.Add("TOTAL:" + total.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray());
        }

        private static void AddIfNonZero(List<string> parts, string label, float v)
        {
            if (Mathf.Abs(v) > 0.01f) parts.Add(label + ":" + v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }

        // kind: 0 armor_q1,1 armor_qmax,2 block_q1,3 block_qmax,4 parry_q1,5 parry_qmax,6 dur_q1,7 dur_qmax
        // (один метод вместо 8 почти одинаковых try/catch — расчёт по формулам самого предмета, не ручным).
        private static string SafeNum(ItemDrop.ItemData item, int q1, int qmax, int kind)
        {
            try
            {
                switch (kind)
                {
                    case 0: return item.GetArmor(q1, 0f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case 1: return item.GetArmor(qmax, 0f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case 2: return item.GetBaseBlockPower(q1).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case 3: return item.GetBaseBlockPower(qmax).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case 4: return item.GetDeflectionForce(q1).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case 5: return item.GetDeflectionForce(qmax).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case 6: return item.GetMaxDurability(q1).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    case 7: return item.GetMaxDurability(qmax).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    default: return "";
                }
            }
            catch { return ""; }
        }

        private static string LocalizedName(string key)
        {
            try { return Localization.instance != null ? Localization.instance.Localize(key) : key; }
            catch { return key; }
        }

        private static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\t", " ").Replace("\n", " ").Replace("\r", " ");
        }
    }
}

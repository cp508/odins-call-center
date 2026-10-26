using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Этап «Классы»: у каждой роли (CompanionRole) — свой уникальный набор вещей и еды, не копия
    // временного общего CombatKit (тот был "до этапа классов" — теперь мы на нём). Роль решает
    // набор, а не наоборот (раньше было наоборот — угадывали роль ПОСЛЕ выдачи одного общего
    // набора всем по снаряжению; этот способ убран вместе с мёртвым кодом после рефакторинга).
    // Танк = ровно старый CombatKit по умолчанию (Club+ShieldWood) — первый класс по счёту
    // (сброс счётчика в CompanionManager.DismissAll, см. FollowerAI._classCounter), поэтому
    // одиночные бой-тесты прошлых этапов (ParryDrill/DuelSkeleton/BlockParry/...), где боец
    // призывается первым после чистого DismissAll, получают тот же набор, что и раньше.
    public static class ClassKits
    {
        public class Kit
        {
            public string[] Weapons; // [0] экипируется сразу, остальные лежат в рюкзаке для переключения по резисту
            public string Shield;    // "" — без щита
            public string[] Arrows;  // для Archer: несколько видов на переключение по резисту ("" — нет)
            public int FoodKitIndex; // 0..3 -> Plugin.FoodKit1..4
        }

        public static Kit For(CompanionRole role)
        {
            switch (role)
            {
                case CompanionRole.Tank:
                    return new Kit { Weapons = Split(Plugin.ClassTankWeapons.Value), Shield = Plugin.ClassTankShield.Value, Arrows = Split(Plugin.ClassTankArrows.Value), FoodKitIndex = 0 };
                case CompanionRole.Archer:
                    return new Kit { Weapons = Split(Plugin.ClassArcherWeapons.Value), Shield = Plugin.ClassArcherShield.Value, Arrows = Split(Plugin.ClassArcherArrows.Value), FoodKitIndex = 2 };
                case CompanionRole.Fighter:
                    return new Kit { Weapons = Split(Plugin.ClassFighterWeapons.Value), Shield = Plugin.ClassFighterShield.Value, Arrows = Split(Plugin.ClassFighterArrows.Value), FoodKitIndex = 3 };
                default: // Universal
                    return new Kit { Weapons = Split(Plugin.ClassUniversalWeapons.Value), Shield = Plugin.ClassUniversalShield.Value, Arrows = Split(Plugin.ClassUniversalArrows.Value), FoodKitIndex = 1 };
            }
        }

        private static string[] Split(string csv)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(csv))
            {
                foreach (var raw in csv.Split(','))
                {
                    var s = raw.Trim();
                    if (s != "") list.Add(s);
                }
            }
            return list.ToArray();
        }

        // Выдать набор новой роли (+ прокачка, как раньше Combat.KitQuality); previous != null — сначала
        // снять ИМЕННО его предметы (смена класса в меню Ё). Возвращает список выданных для лога.
        public static List<string> Apply(Humanoid body, CompanionRole role, Kit previous)
        {
            var given = new List<string>();
            if (body == null || ZNetScene.instance == null) return given;
            var inv = body.GetInventory();

            if (previous != null)
            {
                RemoveNamed(inv, body, previous.Weapons);
                RemoveNamed(inv, body, new[] { previous.Shield });
                RemoveNamed(inv, body, previous.Arrows);
            }

            var kit = For(role);
            foreach (var w in kit.Weapons) GiveOne(inv, w, given);
            if (!string.IsNullOrEmpty(kit.Shield)) GiveOne(inv, kit.Shield, given);
            foreach (var a in kit.Arrows) GiveOne(inv, a, given);

            int q = Mathf.Max(1, Plugin.CombatKitQuality.Value);
            foreach (var it in inv.GetAllItems())
            {
                if (it.m_shared.m_maxQuality <= 1 || it.m_quality >= q) continue;
                it.m_quality = Mathf.Min(q, it.m_shared.m_maxQuality);
                it.m_durability = it.GetMaxDurability();
            }
            return given;
        }

        // Экипировать «активный» комплект класса: первое оружие, щит, первую стрелу (если есть).
        // true = всё, что найдено в рюкзаке, экипировано (или уже было) — иначе есть что повторить
        // (Character.InAttack()/InDodge() в этот кадр — вызывающий код сам повторяет, см. FollowerAI).
        public static bool EquipInitial(Humanoid body, Kit kit)
        {
            if (body == null) return true;
            var inv = body.GetInventory();
            bool ok = true;
            if (kit.Weapons.Length > 0) ok &= EquipNamed(body, inv, kit.Weapons[0]);
            ok &= EquipNamed(body, inv, kit.Shield);
            if (kit.Arrows.Length > 0) ok &= EquipNamed(body, inv, kit.Arrows[0]);
            return ok;
        }

        private static bool EquipNamed(Humanoid body, Inventory inv, string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return true;
            var item = inv.GetItem(prefabName, -1, true);
            if (item == null || item.m_equipped) return true; // нет такого или уже экипирован — не повод для ретрая
            return CompanionEquip.TryEquip(body, item);
        }

        private static bool GiveOne(Inventory inv, string prefabName, List<string> given)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null) { Plugin.Log.LogWarning("Нет префаба класса: " + prefabName); return false; }
            if (!inv.AddItem(prefab, 1)) { Plugin.Log.LogWarning("Не влезло в рюкзак (набор класса): " + prefabName); return false; }
            given.Add(prefabName);
            return true;
        }

        private static void RemoveNamed(Inventory inv, Humanoid body, string[] names)
        {
            if (names == null) return;
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var item = inv.GetItem(name, -1, true);
                if (item == null) continue;
                if (item.m_equipped) CompanionEquip.Unequip(body, item);
                inv.RemoveItem(item);
            }
        }
    }
}

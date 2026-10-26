using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AICompanions
{
    // Лут и рюкзак спутника. Подбор — штатным ItemDrop.Pickup, как у игрока.
    public static class CompanionInventory
    {
        private static readonly FieldInfo DropsField =
            typeof(ItemDrop).GetField("s_instances", BindingFlags.NonPublic | BindingFlags.Static);

        // Один предмет — один сборщик, чтобы двое не бежали за одной смолой.
        public static readonly HashSet<ItemDrop> Claimed = new HashSet<ItemDrop>();

        // Сводка для плашки: одна строка на вид предмета, а не на каждый стак.
        public class ItemSum
        {
            public string Name;
            public Sprite Icon;
            public int Count;
            public int Stacks;
            public int Quality; // уровень прокачки (у сводки рюкзака — максимальный среди стаков)
            public float Weight;
        }

        public class Summary
        {
            public int SlotsUsed;
            public int SlotsTotal;
            public float Weight;
            public float MaxWeight;
            public readonly List<ItemSum> Items = new List<ItemSum>();
        }

        public static bool CanTake(Humanoid body, ItemDrop drop)
        {
            var inv = body.GetInventory();
            var item = drop.m_itemData;
            if (item == null || item.m_shared == null) return false;
            return inv.CanAddItem(item, -1)
                && inv.GetTotalWeight() + item.GetWeight(-1) <= Plugin.CarryWeight.Value;
        }

        // Ближайший лут: выпал сам (не выброшен тобой), рядом со спутником и в зоне вокруг хозяина.
        public static ItemDrop FindLoot(Humanoid body, Vector3 from, Vector3 ownerPos)
        {
            var drops = DropsField != null ? DropsField.GetValue(null) as List<ItemDrop> : null;
            if (drops == null) return null;
            ItemDrop best = null;
            float bestDist = Plugin.CollectRadius.Value;
            foreach (var d in drops)
            {
                if (d == null || !d.m_autoPickup || d.IsPiece() || Claimed.Contains(d)) continue;
                Vector3 p = d.transform.position;
                float dist = Vector3.Distance(from, p);
                if (dist > bestDist || Vector3.Distance(ownerPos, p) > Plugin.DefendRadius.Value) continue;
                if (!CanTake(body, d)) continue;
                best = d;
                bestDist = dist;
            }
            return best;
        }

        // Всё, кроме надетого, — хозяину в инвентарь; не влезло — к ногам (такое спутники сами не поднимут).
        public static int GiveAllTo(Humanoid body, Player player)
        {
            var inv = body.GetInventory();
            var pinv = player.GetInventory();
            int n = 0;
            foreach (var item in new List<ItemDrop.ItemData>(inv.GetAllItems()))
            {
                if (body.IsItemEquiped(item)) continue;
                inv.RemoveItem(item);
                if (!(pinv.CanAddItem(item, -1) && pinv.AddItem(item)))
                {
                    var drop = ItemDrop.DropItem(item, item.m_stack, player.transform.position + player.transform.forward + Vector3.up, Quaternion.identity);
                    if (drop != null) drop.OnPlayerDrop();
                }
                n++;
            }
            return n;
        }

        // Экипированное — отдельно от рюкзака, для плашки HUD (пометка «надето»).
        public static List<ItemSum> SummarizeEquipped(Humanoid body)
        {
            var list = new List<ItemSum>();
            foreach (var item in body.GetInventory().GetEquippedItems())
            {
                list.Add(new ItemSum
                {
                    Name = Localization.instance.Localize(item.m_shared.m_name),
                    Icon = item.GetIcon(),
                    Count = item.m_stack,
                    Stacks = 1,
                    Quality = item.m_quality,
                    Weight = item.GetWeight(-1)
                });
            }
            return list;
        }

        public static Summary Summarize(Humanoid body)
        {
            var s = new Summary();
            var inv = body.GetInventory();
            s.SlotsTotal = inv.GetWidth() * inv.GetHeight();
            s.SlotsUsed = s.SlotsTotal - inv.GetEmptySlots();
            s.Weight = inv.GetTotalWeight();
            s.MaxWeight = Plugin.CarryWeight.Value;
            var byName = new Dictionary<string, ItemSum>();
            foreach (var item in inv.GetAllItems())
            {
                if (body.IsItemEquiped(item)) continue;
                ItemSum sum;
                if (!byName.TryGetValue(item.m_shared.m_name, out sum))
                {
                    sum = new ItemSum { Name = Localization.instance.Localize(item.m_shared.m_name), Icon = item.GetIcon() };
                    byName[item.m_shared.m_name] = sum;
                    s.Items.Add(sum);
                }
                sum.Count += item.m_stack;
                sum.Stacks++;
                if (item.m_quality > sum.Quality) sum.Quality = item.m_quality;
                sum.Weight += item.GetWeight(-1);
            }
            return s;
        }
    }
}

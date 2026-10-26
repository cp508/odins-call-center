using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Желудок спутника — порт формул Player.m_foods/CanEat/EatFood/UpdateFood (этап 2б-2).
    // 3 слота, тают по времени, дают +HP/+стамину в GetTotalFoodValue. Спутник ест сам из рюкзака.
    public class CompanionFood
    {
        public class Slot
        {
            public ItemDrop.ItemData Item;
            public float Time;
            public float Health;
            public float Stamina;
            public bool CanEatAgain() { return Time < Item.m_shared.m_foodBurnTime / 2f; }
        }

        public readonly List<Slot> Slots = new List<Slot>();
        public string LastAte = "";

        private float _updateTimer;
        private float _regenTimer;
        private Humanoid _body;
        private Player _owner;
        private CompanionStamina _stamina;

        public void Init(Humanoid body, Player owner, CompanionStamina stamina)
        {
            _body = body;
            _owner = owner;
            _stamina = stamina;
        }

        // Наборы еды — [Food] Kit1..Kit4 в конфиге, строка вида "Raspberry:8,CookedMeat:6,Honey:5".
        // Один набор на класс (ClassKits.Kit.FoodKitIndex: Tank=0,Universal=1,Archer=2,Fighter=3) —
        // «свой уникальный набор еды» требование Ильи, не копия хозяина. В каждом наборе 3 РАЗНЫХ
        // вида — желудок 3-слотовый, а CanEat не даёт съесть один и тот же вид дважды подряд: с 2
        // видами 3-й слот никогда не заполнится (fix бага).
        private static string GetKitConfig(int index)
        {
            switch (((index % 4) + 4) % 4)
            {
                case 0: return Plugin.FoodKit1.Value;
                case 1: return Plugin.FoodKit2.Value;
                case 2: return Plugin.FoodKit3.Value;
                default: return Plugin.FoodKit4.Value;
            }
        }

        // Смена класса в меню Ё (FollowerAI.CycleRole): старый набор еды снять ИЗ РЮКЗАКА (не
        // доеденное — в желудке уже, Slots само истает по Tick, не трогаем) и выдать набор нового
        // класса — аналогично ClassKits.Apply/RemoveNamed для оружия/щита/стрел.
        public void SwitchKit(int newIndex, int oldIndex)
        {
            RemoveKit(oldIndex);
            GiveStartKit(newIndex);
        }

        private void RemoveKit(int index)
        {
            var kit = GetKitConfig(index).Split(',');
            var inv = _body.GetInventory();
            foreach (var entry in kit)
            {
                string e = entry.Trim();
                if (e == "") continue;
                string name = e.Split(':')[0].Trim();
                var item = inv.GetItem(name, -1, true);
                if (item != null) inv.RemoveItem(item);
            }
        }

        public void GiveStartKit(int index)
        {
            var kit = GetKitConfig(index).Split(',');
            var inv = _body.GetInventory();
            var given = new List<string>();
            foreach (var entry in kit)
            {
                string e = entry.Trim();
                if (e == "") continue;
                var parts = e.Split(':');
                string name = parts[0].Trim();
                int count = parts.Length > 1 ? int.Parse(parts[1].Trim()) : 1;
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (prefab == null) { Plugin.Log.LogWarning("Нет префаба еды " + name); continue; }
                inv.AddItem(prefab, count);
                given.Add(name + "x" + count);
            }
            Plugin.Log.LogInfo(_body.m_name + ": набор еды #" + index + " — " + string.Join("+", given.ToArray()));
        }

        public bool CanEat(ItemDrop.ItemData item)
        {
            foreach (var s in Slots)
                if (s.Item.m_shared.m_name == item.m_shared.m_name)
                    return s.CanEatAgain();
            return Slots.Count < 3;
        }

        public bool EatFood(ItemDrop.ItemData item)
        {
            if (!CanEat(item)) return false;
            Slot slot = null;
            foreach (var s in Slots)
                if (s.Item.m_shared.m_name == item.m_shared.m_name) { slot = s; break; }

            if (slot == null && Slots.Count < 3)
            {
                slot = new Slot();
                Slots.Add(slot);
            }
            if (slot == null) slot = GetMostDepleted();
            if (slot == null) return false;

            slot.Item = item;
            slot.Time = item.m_shared.m_foodBurnTime;
            slot.Health = item.m_shared.m_food;
            slot.Stamina = item.m_shared.m_foodStamina;
            LastAte = Localization.instance.Localize(item.m_shared.m_name);
            ApplyTotals();
            return true;
        }

        private Slot GetMostDepleted()
        {
            Slot best = null;
            foreach (var s in Slots)
            {
                if (!s.CanEatAgain()) continue;
                if (best == null || s.Time < best.Time) best = s;
            }
            return best;
        }

        public void GetTotalFoodValue(out float hp, out float stamina)
        {
            hp = _owner != null ? _owner.m_baseHP : 25f;
            stamina = _owner != null ? _owner.m_baseStamina : 50f;
            foreach (var s in Slots) { hp += s.Health; stamina += s.Stamina; }
        }

        private void ApplyTotals()
        {
            float hp, stamina;
            GetTotalFoodValue(out hp, out stamina);
            if (_body != null) _body.SetMaxHealth(hp);
            if (_stamina != null) _stamina.SetMax(stamina);
        }

        public void Tick(float dt)
        {
            _updateTimer += dt; // раз в 1 сек, как у игрока (сложность/Game.m_foodRate не трогаем)
            while (_updateTimer >= 1f)
            {
                _updateTimer -= 1f;
                for (int i = Slots.Count - 1; i >= 0; i--)
                {
                    var s = Slots[i];
                    s.Time -= 1f;
                    float pct = Mathf.Pow(Mathf.Clamp01(s.Time / s.Item.m_shared.m_foodBurnTime), 0.3f);
                    s.Health = s.Item.m_shared.m_food * pct;
                    s.Stamina = s.Item.m_shared.m_foodStamina * pct;
                    if (s.Time <= 0f) Slots.RemoveAt(i);
                }
                _regenTimer += 1f;
                if (_regenTimer >= 10f)
                {
                    _regenTimer -= 10f;
                    float sum = 0f;
                    foreach (var s in Slots) sum += s.Item.m_shared.m_foodRegen;
                    if (sum > 0f && _body != null) _body.Heal(sum, true);
                }
                // Как у игрока (Player.UpdateFood) — только внутри секундного шага, не каждый
                // кадр: SetMaxHealth пишет ZDO безусловно при каждом вызове (в отличие от
                // SetHealth, там есть дедуп), вызов на каждый Update() грузил сеть зря.
                ApplyTotals();
            }
        }

        private ItemDrop.ItemData FindBestCandidate()
        {
            ItemDrop.ItemData best = null;
            float bestVal = -1f;
            // Защита от гонки: HUD (Plugin.Update) может дёрнуть PeekNext в тот же кадр, что
            // спутник призван, но ДО FollowerAI.SetupOnce()/Food.Init() — _body ещё null.
            if (_body == null) return null;
            foreach (var it in _body.GetInventory().GetAllItems())
            {
                if (it.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                if (it.m_shared.m_food <= 0f && it.m_shared.m_foodStamina <= 0f && it.m_shared.m_foodEitr <= 0f) continue;
                if (!CanEat(it)) continue;
                float val = it.m_shared.m_food + it.m_shared.m_foodStamina + it.m_shared.m_foodEitr;
                if (val > bestVal) { bestVal = val; best = it; }
            }
            return best;
        }

        // Раз в ~1 сек из FollowerAI: пусто или лучшая почти кончилась — съесть лучшую доступную.
        public bool AutoEat()
        {
            var best = FindBestCandidate();
            if (best == null) return false;
            if (!EatFood(best)) return false;
            _body.GetInventory().RemoveItem(best, 1);
            return true;
        }

        // Для HUD «запас»: сколько ещё съедобного в рюкзаке, что съест следующим и когда (прикидка).
        public int CountReserve()
        {
            int n = 0;
            if (_body == null) return n;
            foreach (var it in _body.GetInventory().GetAllItems())
            {
                if (it.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                if (it.m_shared.m_food <= 0f && it.m_shared.m_foodStamina <= 0f && it.m_shared.m_foodEitr <= 0f) continue;
                n += it.m_stack;
            }
            return n;
        }

        // Для HUD: иконка + оставшееся время по каждому слоту желудка.
        public struct SlotView { public Sprite Icon; public float TimeLeft; }

        public List<SlotView> ViewSlots()
        {
            var list = new List<SlotView>();
            foreach (var s in Slots)
                list.Add(new SlotView { Icon = s.Item.GetIcon(), TimeLeft = s.Time });
            return list;
        }

        public bool PeekNext(out string name, out float eta)
        {
            name = null; eta = 0f;
            var best = FindBestCandidate();
            if (best != null)
            {
                // CanEat() уже пропускает только то, что реально съедобно ПРЯМО сейчас —
                // значит AutoEat доест это на следующем тике, eta=0 ("скоро") верно по смыслу.
                name = Localization.instance.Localize(best.m_shared.m_name);
                return true;
            }

            // Ничего не съедобно прямо сейчас (все 3 слота заняты и ни один ещё не "дозрел"
            // до половины, либо слот того же вида ещё не остыл) — посмотреть в рюкзаке, какая
            // еда ближе всех к готовности, и посчитать РЕАЛЬНОЕ время по её же слоту (а не min
            // по всем слотам — тот всегда отрицательный и давал eta=0 всегда, баг).
            Slot bestSlot = null;
            if (_body == null) return false;
            foreach (var it in _body.GetInventory().GetAllItems())
            {
                if (it.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                if (it.m_shared.m_food <= 0f && it.m_shared.m_foodStamina <= 0f && it.m_shared.m_foodEitr <= 0f) continue;
                foreach (var s in Slots)
                {
                    if (s.Item.m_shared.m_name != it.m_shared.m_name) continue;
                    if (bestSlot == null || s.Time < bestSlot.Time) bestSlot = s;
                }
            }
            if (bestSlot == null) return false;
            name = Localization.instance.Localize(bestSlot.Item.m_shared.m_name);
            eta = Mathf.Max(0f, bestSlot.Time - bestSlot.Item.m_shared.m_foodBurnTime * 0.5f);
            return true;
        }
    }
}

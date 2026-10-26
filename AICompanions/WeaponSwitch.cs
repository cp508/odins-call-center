using UnityEngine;

namespace AICompanions
{
    // Этап «Классы», часть 2: несколько наборов оружия/стрел. В бою считаем ожидаемый урон каждого
    // СВОЕГО варианта (из набора класса) против всех целей, которых видит ThreatTracker (резисты —
    // Character.m_damageModifiers: Weak/Resistant/Immune по типам урона), держим лучший экипированным.
    // Переключение НИКОГДА не в момент летящего удара (ThreatTracker.Threats[].Attacking==true) и не
    // когда сам боец занят атакой/кувырком — иначе он на долю секунды остаётся без нужного оружия
    // ровно тогда, когда это важнее всего (запрос Ильи).
    public class WeaponSwitch
    {
        private Humanoid _body;
        private ThreatTracker _threats;
        private float _timer;

        public int Switches;          // всего успешных переключений (метрика в лог автотеста)
        public int DeferredByIncoming; // сколько раз лучший вариант был не текущим, но решили подождать (летел удар/сам занят)
        public string LastSwitchInfo = "";

        public void Init(Humanoid body, ThreatTracker threats)
        {
            _body = body;
            _threats = threats;
        }

        // Публично — автотест weapon_switch_resist проверяет по этому же признаку независимо.
        public bool AnyIncomingAttack()
        {
            if (_threats == null) return false;
            var list = _threats.Threats;
            for (int i = 0; i < list.Count; i++) if (list[i].Attacking) return true;
            return false;
        }

        public void Tick(float dt, CompanionRole role)
        {
            if (_body == null) return;
            _timer -= dt;
            if (_timer > 0f) return;
            _timer = Mathf.Max(0.5f, Plugin.ClassWeaponSwitchInterval.Value);
            if (_threats == null || _threats.Threats.Count == 0) return; // вне боя набор не трогаем
            var kit = ClassKits.For(role);
            EvaluateGroup(kit.Weapons, false);
            EvaluateGroup(kit.Arrows, true);
        }

        private void EvaluateGroup(string[] prefabNames, bool isAmmo)
        {
            if (prefabNames == null || prefabNames.Length < 2) return; // меньше 2 вариантов — нечего выбирать
            var inv = _body.GetInventory();
            ItemDrop.ItemData current = isAmmo ? _body.GetAmmoItem() : _body.GetCurrentWeapon();
            ItemDrop.ItemData best = null;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < prefabNames.Length; i++)
            {
                var item = inv.GetItem(prefabNames[i], -1, true);
                if (item == null) continue;
                float score = ExpectedDamage(item);
                if (best == null || score > bestScore) { bestScore = score; best = item; }
            }
            if (best == null || best == current) return;
            float curScore = current != null ? ExpectedDamage(current) : float.NegativeInfinity;
            // Лучше минимум на запас (Plugin.ClassWeaponSwitchMargin) — не дёргаться туда-сюда на шумовой разнице.
            if (!(bestScore > curScore * (1f + Plugin.ClassWeaponSwitchMargin.Value)) && curScore > 0f) return;
            if (curScore <= 0f && bestScore <= 0f) return; // оба бесполезны (иммунитет/игнор) — смысла менять нет

            if (_body.InAttack() || _body.InDodge() || AnyIncomingAttack())
            {
                DeferredByIncoming++;
                return;
            }

            if (!CompanionEquip.TryEquip(_body, best)) return;
            Switches++;
            string curName = current != null && current.m_shared != null ? current.m_shared.m_name : "ничего";
            string bestName = best.m_shared != null ? best.m_shared.m_name : "?";
            LastSwitchInfo = (isAmmo ? "стрелы " : "оружие ") + curName + " -> " + bestName
                + " (ожид.урон " + curScore.ToString("0.0") + "->" + bestScore.ToString("0.0") + ")";
            Plugin.Log.LogInfo("[Классы] " + _body.m_name + ": " + LastSwitchInfo);
        }

        // Средний ожидаемый урон этого предмета по ВСЕМ целям, которых видит ThreatTracker
        // ("против большинства активных врагов" — Илья) — резисты берутся из живых данных цели,
        // а не из предположений о конкретном мобе.
        private float ExpectedDamage(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null || _threats == null) return 0f;
            var dmg = item.GetDamage(); // с учётом item.m_quality (ClassKits.Apply прокачивает набор), не 1-й уровень m_shared.m_damages
            var list = _threats.Threats;
            int n = 0;
            float sum = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                var enemy = list[i].Enemy;
                if (enemy == null) continue;
                sum += ExpectedAgainst(dmg, enemy);
                n++;
            }
            return n > 0 ? sum / n : 0f;
        }

        private static float ExpectedAgainst(HitData.DamageTypes dmg, Character enemy)
        {
            var mods = enemy.m_damageModifiers;
            float total = 0f;
            total += dmg.m_blunt * Mult(mods.GetModifier(HitData.DamageType.Blunt));
            total += dmg.m_slash * Mult(mods.GetModifier(HitData.DamageType.Slash));
            total += dmg.m_pierce * Mult(mods.GetModifier(HitData.DamageType.Pierce));
            total += dmg.m_fire * Mult(mods.GetModifier(HitData.DamageType.Fire));
            total += dmg.m_frost * Mult(mods.GetModifier(HitData.DamageType.Frost));
            total += dmg.m_lightning * Mult(mods.GetModifier(HitData.DamageType.Lightning));
            total += dmg.m_poison * Mult(mods.GetModifier(HitData.DamageType.Poison));
            total += dmg.m_spirit * Mult(mods.GetModifier(HitData.DamageType.Spirit));
            return total;
        }

        // Множители модификатора — по IL HitData.ApplyModifier (classes_il1.txt): 0.25/0.5/0.75 — три
        // степени "резист", 1.25/1.5/2.0 — три степени "слабости", Immune/Ignore = 0, иначе Normal = 1.
        public static float Mult(HitData.DamageModifier m)
        {
            switch (m)
            {
                case HitData.DamageModifier.Immune: return 0f;
                case HitData.DamageModifier.Ignore: return 0f;
                case HitData.DamageModifier.VeryResistant: return 0.25f;
                case HitData.DamageModifier.Resistant: return 0.5f;
                case HitData.DamageModifier.SlightlyResistant: return 0.75f;
                case HitData.DamageModifier.SlightlyWeak: return 1.25f;
                case HitData.DamageModifier.Weak: return 1.5f;
                case HitData.DamageModifier.VeryWeak: return 2f;
                default: return 1f; // Normal
            }
        }
    }
}

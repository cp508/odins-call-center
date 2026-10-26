using UnityEngine;

namespace AICompanions
{
    // Модель стамины спутника (запрос Ильи 25.10). Факт из IL Player.UpdateStats (stamina_il_1.txt):
    // реген тем БЫСТРЕЕ, чем НИЖЕ стамина — линейно, порога «на половине» нет (CompanionStamina.Tick).
    // Значит ждать половины/полной перед атакой не только не нужно, но и вредно — чем дольше стоим
    // и ждём, тем МЕДЛЕННЕЕ реально идёт реген (он уже быстрее ровно сейчас, у низкой стамины).
    //
    // Правило (ПРАВКА Ильи 25.10, ZADACHI/ai-companions.md): атаковать, как только стамина >= запас
    // + стоимость ВСЕЙ СЕРИИ ударов (не одного удара) — запас для старта серии = 1 кувырок на отход
    // ПОСЛЕ серии (+блок/парирование, если есть щит), а не 2 кувырка + 1 удар, как было раньше.
    // Сама цепочка ударов без паузы между ними (реген=0 во время атаки, задержка 1с — одна на всю
    // серию, а не на каждый удар) — логика ветки combo; здесь (StaminaSeriesHits) только дефолт
    // запаса под эту модель, чтобы его не пришлось переоткрывать. БЕЗ скидки на реген между кувырками
    // — любая трата сбрасывает m_staminaRegenDelay, реген между ними практически нулевой (задержка
    // ~0.6-1с у игрока), поэтому запас — ПРЯМАЯ сумма стоимостей. Margin сверху — на погрешность
    // прогноза, а не на эту скидку (её не существует).
    //
    // Вне боя эта политика ничего не трогает: CombatGuard уже не поднимает блок и не кувыркает без
    // реальной угрозы (ThreatTracker.Threats пуст — WantsBlock=false, TryDodge никто не зовёт), а
    // движение (следование MonsterAI за хозяином) стамину вообще не тратит (в IL UpdateStats бег НЕ
    // списывает стамину — только явный Encumbered-дренаж через UseStamina, не связанный с бегом).
    // Поэтому "вне боя держать высокой" получается само — тут только метрики/лог для проверки.
    public static class StaminaPolicy
    {
        // Стоимость ОДНОГО кувырка — спрашиваем у CompanionDodge, не дублируем формулу (там же
        // учтены навык и модификаторы снаряжения).
        public static float DodgeCost(FollowerAI f)
        {
            return f != null && f.Dodge != null ? f.Dodge.Cost : 8f;
        }

        public static bool HasShield(FollowerAI f)
        {
            return f != null && f.Guard != null && f.Guard.HasBlockerNow();
        }

        // Худший случай блока (не идеальный парир, доля заблокированного урона=1 — максимум по
        // формуле) — переоценить запас безопаснее, чем недооценить (правило Ильи: лучше лишний
        // запас, чем нехватка стамины на кувырок). ДИНАМИЧЕСКИ: базовое значение m_blockStaminaDrain
        // — не константа, а реальное поле текущего набора (Plugin.CombatKit) + SEMan.
        // Экип-модификатор щита (Character.GetEquipmentBlockStaminaModifier) здесь НЕ зовём —
        // проверено по IL (stamina_fix2: Character.GetEquipmentBlockStaminaModifier = `ldc.r4 0;
        // ret`, override есть ТОЛЬКО в Player через Player-only систему сетовых бонусов
        // GetEquipmentModifier(5); у Humanoid — тело нашего спутника — override нет, виртуальный
        // вызов резолвится в Character-базу и всегда даёт 0). Это факт движка (экип-бонусы блока
        // предусмотрены только для игрока), не упущение — звать мёртвый код не стали.
        public static float BlockCost(FollowerAI f)
        {
            if (f == null || f.Stamina == null) return 0f;
            float cost = f.Stamina.BlockStaminaDrain;
            var seman = f.Stamina.Seman;
            if (seman != null) { try { seman.ModifyBlockStaminaUsage(cost, ref cost, false); } catch { } }
            return Mathf.Max(0f, cost);
        }

        // Нижний запас в бою: кувырки на отход ПОСЛЕ серии + блок (если есть щит), с запасом на
        // погрешность прогноза. Стоимость самой серии ударов — отдельно, в CanStartAttack (ниже).
        public static float Reserve(FollowerAI f)
        {
            if (f == null || f.Stamina == null) return 0f;
            float reserve = DodgeCost(f) * Mathf.Max(1f, Plugin.StaminaDodgeReserveCount.Value);
            if (HasShield(f)) reserve += BlockCost(f);
            return reserve * Mathf.Max(1f, Plugin.StaminaReserveMargin.Value);
        }

        // Стоимость своего следующего удара текущим оружием (ItemData.SharedData.m_attack.m_attackStamina,
        // IL Attack.GetAttackStamina — ветка "не Player": модификаторы снаряжения по стамине на NPC НЕ
        // применяются вовсе, companion у нас Humanoid, не Player, так и считаем). Порядок как в игре:
        // сначала SEMan.ModifyAttackStaminaUsage (БЕЗ условия isPlayer — stamina_il_3.txt:43-56), потом
        // скидка за навык (-33% на максимальном). SEMan своей системы эффектов у спутника пока нет
        // (как в CompanionStamina.Tick) — почти всегда множитель 1, считаем честно для единообразия.
        public static float AttackCost(FollowerAI f)
        {
            if (f == null || f.Body == null) return Plugin.StaminaAttackCostFallback.Value;
            var weapon = f.Body.GetCurrentWeapon();
            var atk = weapon != null && weapon.m_shared != null ? weapon.m_shared.m_attack : null;
            if (atk == null) return Plugin.StaminaAttackCostFallback.Value;
            float cost = atk.m_attackStamina;
            // Сигнатура по дампу (stamina_seman_1.txt): ModifyAttackStaminaUsage(baseStaminaUse, ref
            // staminaUse, minZero) — IL Attack.GetAttackStamina зовёт её как (cost, ref cost, true).
            var seman = f.Stamina != null ? f.Stamina.Seman : null;
            if (seman != null) { try { seman.ModifyAttackStaminaUsage(cost, ref cost, true); } catch { } }
            float skill = Mathf.Clamp01(Plugin.CompanionSkillFactor.Value);
            cost -= cost * 0.33f * skill;
            return Mathf.Max(0f, cost);
        }

        // Главное правило: атаковать, как только стамина >= запас + стоимость ВСЕЙ СЕРИИ ударов
        // (StaminaSeriesHits, правка Ильи 25.10 — не «запас + 1 удар») — НЕ ждать половины/полной
        // (ниже стамина — реген и так быстрее, см. комментарий класса). Серия как цепочка без пауз
        // между ударами — ветка combo; здесь только запас под неё, умножаем AttackCost одного удара.
        // out reason строится ТОЛЬКО когда включён Combat.LogStaminaGate (по умолчанию выкл.) —
        // иначе это лишняя конкатенация+2×ToString на каждый отказ стартовать атаку, на горячем
        // пути (CompanionAttackReservePatch.Prefix, который раньше никуда reason не девал).
        public static bool CanStartAttack(FollowerAI f, out string reason)
        {
            reason = "";
            bool log = Plugin.StaminaLogGate.Value;
            if (f == null || f.Stamina == null) { if (log) reason = "нет данных"; return false; }
            float seriesCost = AttackCost(f) * Mathf.Max(1f, Plugin.StaminaSeriesHits.Value);
            float need = Reserve(f) + seriesCost;
            if (f.Stamina.Current < need)
            {
                if (log)
                {
                    reason = "стамина " + f.Stamina.Current.ToString("0.0") + "/" + f.Stamina.Max.ToString("0.0")
                        + " < запас+серия(" + need.ToString("0.0") + ")";
                }
                return false;
            }
            return true;
        }

        // Для метрик/лога: сейчас есть реальная угроза рядом (не только "целится", а вообще видна) —
        // вне боя тратить стамину незачем, и, как написано выше, структурно это уже так.
        public static bool InCombat(FollowerAI f)
        {
            return f != null && f.Threats != null && f.Threats.EnemiesNear > 0;
        }
    }
}

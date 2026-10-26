using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Этап 2б-2: еда/стамина/блок/кувырок как у игрока — Harmony-перехват виртуальных стаб-методов
    // Character ТОЛЬКО для спутников (FollowerAI.Registry). Игрок и мобы идут штатным путём (return true).

    // Имя параметра должно совпадать с оригиналом (Character.UseStamina(Single stamina)) —
    // Harmony сверяет патч-параметры с сигнатурой метода по имени.
    [HarmonyPatch(typeof(Character), "UseStamina", new[] { typeof(float) })]
    static class CompanionUseStaminaPatch
    {
        static bool Prefix(Character __instance, float stamina)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return true;
            f.Stamina.Use(stamina);
            return false;
        }
    }

    [HarmonyPatch(typeof(Character), "HaveStamina", new[] { typeof(float) })]
    static class CompanionHaveStaminaPatch
    {
        static bool Prefix(Character __instance, float amount, ref bool __result)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return true;
            __result = f.Stamina.Have(amount);
            return false;
        }
    }

    [HarmonyPatch(typeof(Character), "AddStamina", new[] { typeof(float) })]
    static class CompanionAddStaminaPatch
    {
        static bool Prefix(Character __instance, float v)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return true;
            f.Stamina.Add(v);
            return false;
        }
    }

    // Новая атака спутника не начинается, если: 1) Guard держит/хочет держать блок (иначе своя
    // атака (InAttack()=true) молча аннулирует Humanoid.IsBlocking() и удар проходит без блока —
    // см. CombatGuard.WantsBlock), 2) стамины не хватает по StaminaPolicy.CanStartAttack (Reserve —
    // 1 кувырок [+блок] — плюс стоимость ВСЕЙ серии ударов, StaminaSeriesHits, не одного удара —
    // StaminaPolicy.cs, запрос Ильи 25.10: атаковать сразу, как только хватает, не ждать половины/
    // полной — ниже стамина реген и так быстрее), 3) БЕЗОПАСНОЕ
    // ОКНО (этап «Бой v2а»): какой-то враг рядом ударит раньше, чем закончится собственный замах +
    // запас — атаку откладываем, кроме приоритетного окна после парирования/стаггера (контратака по
    // оглушённому важнее собственной безопасности). Блок/кувырок сами идут не через этот метод —
    // им ничего из этого не мешает (и запасная проверка стамины у них своя, CompanionDodge/BlockAttack).
    [HarmonyPatch(typeof(Attack), "Start")]
    static class CompanionAttackReservePatch
    {
        static bool Prefix(Humanoid character, ref bool __result)
        {
            FollowerAI f;
            if (character == null || !FollowerAI.Registry.TryGetValue(character, out f)) return true;
            if (f.Recalling) // «Ко мне» в бою — новых атак не начинаем, в т.ч. комбо (правило Ильи)
            {
                __result = false;
                return false;
            }
            if (f.Guard.WantsBlock)
            {
                __result = false;
                return false;
            }
            string reason;
            if (!StaminaPolicy.CanStartAttack(f, out reason))
            {
                if (Plugin.StaminaLogGate.Value && reason.Length > 0)
                    Plugin.Log.LogInfo("[AIC][STAMINA] " + f.Name + " не атакует: " + reason);
                __result = false;
                return false;
            }
            if (!f.Guard.PriorityAttackWindow && !IsSafeToAttack(f))
            {
                __result = false;
                return false;
            }
            return true;
        }

        // Своя атака безопасна, только если НИ ОДИН враг рядом, целящийся в спутника, не ударит
        // раньше, чем закончится собственный замах (+ запас по стилю боя).
        private static bool IsSafeToAttack(FollowerAI f)
        {
            var tracker = f.Threats;
            if (tracker == null) return true;
            float ownDuration = EstimateOwnAttackDuration(f);
            float margin = Plugin.SafeWindowMargin.Value * CombatGuard.StyleMarginMul();
            float reach = Mathf.Max(Plugin.BlockReach.Value, Plugin.DodgeReach.Value);
            foreach (var t in tracker.Threats)
            {
                if (!t.TargetingMe || t.Dist > reach) continue;
                if (t.Attacking)
                {
                    if (t.TimeToHit < ownDuration + margin) { f.OnWaitingWindow(); return false; }
                    continue;
                }
                // ГЛАВНЫЙ ФИКС (главная проблема этапа): враг рядом ПОКА не бьёт, но может начать
                // замах в любой момент — а Attack.Stop() для обычного (не зацикленного) замаха НЕ
                // прерывает уже идущую анимацию (IL: InAttack() держится тегом аниматора, не нашим
                // кодом, см. CombatGuard.StopOwnAttack) — своя атака, которая дольше, чем враг может
                // ждать до следующего удара, обречена попасть под "в_своей_атаке=True". Раньше гейт
                // проверял только t.Attacking==true (уже начавшийся замах) и пропускал этот случай.
                float readyIn = EstimateTimeUntilCanStartAttack(t.Enemy, t.EnemyBody);
                if (readyIn >= float.MaxValue) continue; // не ИИ MonsterAI (игрок/спутник) — не считаем угрозой здесь
                float windup;
                if (!AttackProfiles.TryGetWindup(t.Enemy, out windup)) windup = AttackProfiles.GetConservativeDefault();
                if (readyIn + windup < ownDuration + margin)
                {
                    f.OnWaitingWindow();
                    return false;
                }
            }
            return true;
        }

        // Раньше какого момента враг физически НЕ МОЖЕТ начать новый замах — по данным самой игры
        // (MonsterAI.m_minAttackInterval — общий кулдаун ИИ, SharedData.m_aiAttackInterval — кулдаун
        // конкретного оружия, оба отсчитываются от Character.GetTimeSinceLastAttack()), а не по
        // догадке. Это и есть «безопасное окно» правила Ильи (откат врага после удара) — если это
        // окно короче собственного замаха, атаковать нельзя, если длиннее — можно смело бить.
        private static float EstimateTimeUntilCanStartAttack(Character enemy, Humanoid enemyBody)
        {
            if (enemy == null) return float.MaxValue;
            var ai = enemy.GetBaseAI() as MonsterAI;
            if (ai == null) return 0f; // ИИ не MonsterAI/неизвестен — консервативно считаем «готов бить прямо сейчас»
            float since = enemy.GetTimeSinceLastAttack();
            float wait = Mathf.Max(0f, ai.m_minAttackInterval - since);
            var weapon = enemyBody != null ? enemyBody.GetCurrentWeapon() : null;
            if (weapon != null && weapon.m_shared != null)
                wait = Mathf.Max(wait, weapon.m_shared.m_aiAttackInterval - since);
            return wait;
        }

        // Оценка длительности СВОЕГО замаха — тот же профиль (AttackProfiles), что и для врагов:
        // ключ по текущему оружию, уровень цепочки неизвестен ДО старта — берём 0 (большинство
        // первых ударов серии). ИСПОЛЬЗУЕМ BusyDuration (Start -> InAttack()==false, вся занятость
        // включая возврат оружия), а не Windup (Start -> удар) — иначе угроза, чей удар попадает в
        // "хвост" атаки после собственного удара, на старте атаки не видна (combatlab_design.md §1,
        // проблема Б). Если BusyDuration ещё не откалиброван — Windup как более консервативная
        // (меньшая) оценка лучше нуля, иначе дефолтный windup.
        private static float EstimateOwnAttackDuration(FollowerAI f)
        {
            var weapon = f.Body != null ? f.Body.GetCurrentWeapon() : null;
            string anim = weapon != null && weapon.m_shared != null && weapon.m_shared.m_attack != null
                ? weapon.m_shared.m_attack.m_attackAnimation : null;
            if (anim == null) return Plugin.DefaultAttackWindup.Value;
            string key = AttackProfiles.PrefabNameOf(f.Body) + "/" + anim + "/0";
            float busy; int nb;
            if (AttackProfiles.TryGetBusyDurationByKey(key, out busy, out nb)) return busy;
            float w; int nw;
            if (AttackProfiles.TryGetWindupByKey(key, out w, out nw)) return w;
            return Plugin.DefaultAttackWindup.Value;
        }
    }

    // «Ко мне» в бою (ветка recall) — НЕМЕДЛЕННЫЙ выход из боя. Attack.Stop() (CombatGuard.
    // StopOwnAttack) не прерывает уже идущий клип мгновенно: InAttack() держится тегом аниматора
    // ещё ~0.4-0.8с ("хвост", см. комментарий в CombatGuard.StopOwnAttack) — и всё это время
    // Humanoid.IsBlocking() по IL принудительно возвращает false при InAttack()==true (ДАЖЕ если
    // m_blocking=true), а CompanionDodge.TryDodge сам отказывает по той же причине. Итог без
    // этого патча: прямо после команды летящий удар проходит БЕЗ защиты — ни кувырок, ни щит не
    // успевают сработать (найдено по логу: «щит под удар» залогирован, [BLOCK] не вызван вообще,
    // урон прошёл практически полным — [HITLOG] подтвердил «в_своей_атаке=True»). Раз мы и так уже
    // явно остановили атаку (Guard.StopAttack в FollowerAI.Recall) — для спутника в Recalling
    // считаем её прерванной СРАЗУ, не ждём хвост. ВАЖНО: патчим Humanoid, а не Character — у
    // Humanoid СВОЙ override InAttack() (dump8 подтвердил, Character.InAttack — отдельный, не
    // вызывается для Humanoid), патч на Character для наших бойцов был бы мёртвым кодом.
    [HarmonyPatch(typeof(Humanoid), "InAttack")]
    static class CompanionRecallInAttackPatch
    {
        static void Postfix(Character __instance, ref bool __result)
        {
            if (!__result) return;
            FollowerAI f;
            if (FollowerAI.Registry.TryGetValue(__instance, out f) && f.Recalling) __result = false;
        }
    }

    // Окно неуязвимости кувырка — только для спутников (своя реализация в CompanionDodge).
    [HarmonyPatch(typeof(Character), "InDodge")]
    static class CompanionInDodgePatch
    {
        static void Postfix(Character __instance, ref bool __result)
        {
            FollowerAI f;
            if (FollowerAI.Registry.TryGetValue(__instance, out f) && f.Dodge.InDodge) __result = true;
        }
    }

    [HarmonyPatch(typeof(Character), "IsDodgeInvincible")]
    static class CompanionDodgeInvinciblePatch
    {
        static void Postfix(Character __instance, ref bool __result)
        {
            FollowerAI f;
            if (FollowerAI.Registry.TryGetValue(__instance, out f) && f.Dodge.Invincible) __result = true;
        }
    }

    // Без Skills-компонента (намеренно убран из шаблона спутника, CompanionFactory.Build)
    // Character.GetSkillFactor(...) всегда возвращает 0 (IL подтверждено: база просто
    // "ldc.r4 0; ret" — реальный расчёт по уровню навыка есть только в Player.GetSkillFactor,
    // который читает Player.m_skills). Из-за этого у спутника навсегда нулевой скилл: блок
    // щитом даёт только голый базовый block power без бонуса навыка (ItemData.GetBlockPower =
    // base + base*skillFactor*0.5), скорость натяжения лука и расход стамины на удар — тоже без
    // скилл-бонуса (Humanoid.GetAttackDrawPercentage/Attack.GetAttackStamina — оба честно вызывают
    // этот же виртуальный метод для ЛЮБОГО Humanoid, не только Player, IL подтверждено).
    //
    // Этап «Навыки как у игрока»: вместо фиксированного числа — РЕАЛЬНЫЙ уровень конкретного
    // спутника по CompanionSkills (хранится за его именем, растёт от использования через
    // CompanionRaiseSkillPatch ниже). CompanionSkillFactor остаётся смыслом «стартовый уровень
    // нового спутника» (CompanionSkills.GetOrCreate) — так нетронутый навыком спутник ведёт себя
    // как раньше, а не откатывается к багу «вечный ноль».
    [HarmonyPatch(typeof(Character), "GetSkillFactor")]
    static class CompanionSkillFactorPatch
    {
        static bool Prefix(Character __instance, Skills.SkillType skill, ref float __result)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return true;
            __result = CompanionSkills.GetFactor(f.Name, skill);
            return false;
        }
    }

    // Character.GetSkillLevel(...) у спутника без Skills-компонента всегда 0 (GetSkills()==null →
    // "ldc.r4 0; ret", IL подтверждено) — используется, например, в HitData.m_skillLevel
    // (Attack.DoMeleeAttack) и в длительности статусных эффектов идеального блока.
    [HarmonyPatch(typeof(Character), "GetSkillLevel")]
    static class CompanionSkillLevelPatch
    {
        static bool Prefix(Character __instance, Skills.SkillType skillType, ref float __result)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return true;
            __result = CompanionSkills.GetLevel(f.Name, skillType);
            return false;
        }
    }

    // Character.RaiseSkill(...) у НЕ-Player — это ветка "приручённый питомец поднимает навык
    // ХОЗЯИНА" (Tameable.m_levelUpOwnerSkill, IL подтверждено) и для спутника (не приручён,
    // Tameable нет) всегда сразу return. Игра вызывает этот метод штатно и для спутника тоже —
    // Attack.DoMeleeAttack после удара (оружейный навык, фактор x1.5 если целью был Character)
    // и Humanoid.BlockAttack после блока (Blocking, x2 при парировании) — оба раза на САМОГО
    // ударившего/блокировавшего (m_character), не на хозяина. Перехватываем и ведём реальный
    // уровень по CompanionSkills (точная формула Skill.Raise) вместо пустого перехода в базу.
    [HarmonyPatch(typeof(Character), "RaiseSkill")]
    static class CompanionRaiseSkillPatch
    {
        static bool Prefix(Character __instance, Skills.SkillType skill, float value)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return true;
            CompanionSkills.Raise(f.Name, skill, value);
            return false;
        }
    }

    // Доп. бонус урона от навыка оружия — ТОЛЬКО для спутников. В ваниле Skills.SkillType НЕ
    // влияет на саму цифру урона удара (подтверждено по IL Attack.DoMeleeAttack/ModifyDamage:
    // там только Character.GetRandomSkillFactor — это RNG-разброс 0.75..1 от Random.Range,
    // НЕ зависящий от уровня навыка, и Attack.GetLevelDamageFactor — это уровень МОБА/звёзды,
    // не Skills; у игрока рост навыка реально снижает расход стамины и поднимает силу
    // блока/натяжения лука — оба уже покрыты перехватом GetSkillFactor выше). Чтобы «урон растёт
    // с навыком» было видно и измеримо (требование Ильи, тест skills_effect), для спутника
    // добавляем свой бонус: линейно до +30% урона при навыке своим оружием = 100.
    [HarmonyPatch(typeof(Attack), "ModifyDamage")]
    static class CompanionWeaponSkillDamagePatch
    {
        private static readonly FieldInfo AttackCharacterField = AccessTools.Field(typeof(Attack), "m_character");
        private static readonly FieldInfo AttackWeaponField = AccessTools.Field(typeof(Attack), "m_weapon");

        static void Postfix(Attack __instance, HitData hitData)
        {
            if (hitData == null || AttackCharacterField == null || AttackWeaponField == null) return;
            var ch = AttackCharacterField.GetValue(__instance) as Character;
            FollowerAI f;
            if (ch == null || !FollowerAI.Registry.TryGetValue(ch, out f)) return;
            var weapon = AttackWeaponField.GetValue(__instance) as ItemDrop.ItemData;
            if (weapon == null || weapon.m_shared == null) return;
            float factor = CompanionSkills.GetFactor(f.Name, weapon.m_shared.m_skillType);
            if (factor <= 0f) return;
            hitData.ApplyModifier(1f + factor * Plugin.CompanionSkillDamageBonus.Value);
        }
    }

    // Статистика блоков/парирований: m_blockTimer читаем ДО возможной следующей UpdateBlock —
    // тот же критерий, что в Humanoid.BlockAttack (< 0.25 с = окно парирования).
    [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
    static class CompanionBlockStatsPatch
    {
        private static readonly FieldInfo BlockTimerField = AccessTools.Field(typeof(Humanoid), "m_blockTimer");

        // Таймер блока читаем ДО BlockAttack (в Prefix) — именно его видит игра при решении «парирование».
        static void Prefix(Humanoid __instance, out float __state)
        {
            __state = BlockTimerField != null ? (float)BlockTimerField.GetValue(__instance) : -1f;
        }

        static void Postfix(Humanoid __instance, bool __result, float __state)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return;
            Plugin.Log.LogInfo("[BLOCK] " + f.Name + " результат=" + __result + " таймер_блока=" + __state.ToString("0.000")
                + (__state >= 0f && __state < 0.25f ? " → ПАРИРОВАНИЕ" : " → обычный блок"));
            if (!__result) return;
            f.Blocks++;
            if (__state >= 0f && __state < 0.25f)
            {
                f.Parries++;
                f.Guard.NotifyParrySuccess(); // безопасное окно для контратаки — правило Ильи
            }
        }
    }

    // Метрики этапа «Бой v2а»: каждый реальный удар по спутнику — урон ПОСЛЕ всех мициаций
    // (Character.Damage применяет блок/броню до записи в HP), сверка с решением CombatGuard
    // (допустимо/КУВЫРОК-или-ПАРИРОВАНИЕ) → "безопасно" = урон <= допустимого.
    [HarmonyPatch(typeof(Character), "Damage")]
    static class CompanionDamageMetricsPatch
    {
        static void Postfix(Character __instance, HitData hit)
        {
            if (hit == null) return;
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return;
            var attacker = hit.GetAttacker();
            // Дружественный огонь гасится NoFriendlyFirePatch (Patches.cs) на уровне RPC_Damage —
            // HP не меняется, но этот Postfix на Character.Damage всё равно сработает; не считать.
            if (attacker != null && (FollowerAI.Registry.ContainsKey(attacker) || attacker is Player)) return;
            // Метрика «безопасно» считается по РЕАЛЬНОЙ потере HP — в CompanionDamageLedgerPatch (ApplyDamage),
            // здесь урон ещё до блока/брони (на нём парированный удар выглядел «опасным»).
        }
    }
}

namespace AICompanions
{
    // Журнал последнего удара по спутнику (итоговый урон после брони/множителей, HP до/после) —
    // для уведомления о смерти «кто и сколько снял».
    [HarmonyLib.HarmonyPatch(typeof(Character), "ApplyDamage")]
    static class CompanionDamageLedgerPatch
    {
        static void Prefix(Character __instance, out float __state) { __state = __instance.GetHealth(); }

        static void Postfix(Character __instance, HitData hit, float __state)
        {
            if (hit == null) return;
            var atk = hit.GetAttacker();
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f))
            {
                // Жертва — НЕ спутник: если атакующий спутник, засчитываем урон ПО врагу (метрика
                // troll_solo «урон по троллю» — реальная потеря HP цели, не сырой урон атаки).
                FollowerAI af;
                if (atk != null && FollowerAI.Registry.TryGetValue(atk, out af))
                {
                    float dealt = Mathf.Max(0f, __state - __instance.GetHealth());
                    af.DamageDealt += dealt;
                    if (dealt > 0f) af.HitsDealt++; // для «урон за удар» (skills_effect, фикс нестабильности)
                }
                return;
            }
            float armor = 0f;
            var h = __instance as Humanoid;
            try { if (h != null) armor = h.GetBodyArmor(); } catch { }
            if (atk == null || (!FollowerAI.Registry.ContainsKey(atk) && !(atk is Player)))
                f.OnIncomingDamage(atk, Mathf.Max(0f, __state - __instance.GetHealth()));
            f.LastHitText = (atk != null ? Localization.instance.Localize(atk.m_name) : "неизвестно")
                + " — " + hit.GetTotalDamage().ToString("0") + " урона (HP было " + __state.ToString("0") + "/"
                + __instance.GetMaxHealth().ToString("0") + ", броня " + armor.ToString("0") + ")";
        }
    }

    // Уведомление о смерти спутника: кто убил, сколько снял последним ударом, сколько было HP и брони.
    [HarmonyLib.HarmonyPatch(typeof(Character), "OnDeath")]
    static class CompanionDeathNoticePatch
    {
        static void Prefix(Character __instance)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return;
            CompanionMapPins.Remove(f); // метка на карте — сразу в момент смерти, не ждать удаления тела
            string text = "☠ " + f.Name + " погиб: " + (string.IsNullOrEmpty(f.LastHitText) ? "причина неизвестна" : f.LastHitText);
            Plugin.Log.LogWarning("[DEATH] " + text);
            if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.Center, text);
        }
    }
}

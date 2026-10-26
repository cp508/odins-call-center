using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // «Криминалист боя» (разовая задача, НЕ часть боевой логики — код ничего не решает, только
    // читает уже принятые решения CombatGuard/CompanionDodge и пишет диагностику):
    //   [HITLOG]    — на КАЖДЫЙ удар врага, нацеленный в спутника или геометрически его задевший:
    //                 прогноз замаха vs факт, было решение (блок/кувырок), был ли спутник лицом
    //                 к врагу, не был ли занят своей атакой, стамина, шкала оглушения, итоговый
    //                 урон vs допустимый, БЕЗОПАСНО/ПРОПУЩЕН + конкретная причина.
    //   [CLIPPROBE] — для Skeleton/Greydwarf/Greydwarf_Elite/Troll: что реально отдаёт Animator
    //                 (клип, length, events[], speed, normalizedTime) в момент старта замаха и
    //                 в момент фактического удара — проверка, можно ли брать тайминг из
    //                 AnimationClip.events вместо калибровки по факту (_research/cv2a_0.md "Вариант A").
    public static class CombatForensics
    {
        private static readonly FieldInfo StaggerDamageField = AccessTools.Field(typeof(Character), "m_staggerDamage");
        private static readonly FieldInfo AnimatorField = AccessTools.Field(typeof(Character), "m_animator");
        private static readonly FieldInfo ChainLevelField = AccessTools.Field(typeof(Attack), "m_currentAttackCainLevel");

        private static readonly string[] ProbedPrefabs = { "Skeleton", "Greydwarf", "Greydwarf_Elite", "Troll" };

        // Один замах врага = один Snapshot, живёт от Prefix до Postfix того же вызова OnAttackTrigger
        // (момент удара синхронный — RPC_Damage по спутнику успевает отработать ВНУТРИ этого окна).
        private class Snapshot
        {
            public float FactWindup;       // факт: сколько сек реально шёл замах (StartAttack -> OnAttackTrigger)
            public float PredictedWindup;  // прогноз: откалиброванный windup (или дефолт), каким его знал CombatGuard
            public bool PredictionKnown;
            public bool Dodgeable;
            public bool Blockable;
            public bool IsArea;
            public string AnimName = "?";
            public string WeaponName = "?";
            public readonly Dictionary<FollowerAI, CompanionSnap> Companions = new Dictionary<FollowerAI, CompanionSnap>();
        }

        private class CompanionSnap
        {
            public float DamageBefore;
            public bool Blocking;
            public bool OwnAttack;
            public bool DodgeInvincible;
            public float Stamina;
            public float StaggerDamage;
            public float StaggerThreshold;
            public float Bearing;
            public float Allowed;
            public string DecisionLabel = "?";
            public bool Targeting;
            public bool InZone;
        }

        private static readonly Dictionary<Humanoid, Snapshot> _pending = new Dictionary<Humanoid, Snapshot>();

        private static bool IsProbedPrefab(string prefab)
        {
            for (int i = 0; i < ProbedPrefabs.Length; i++)
                if (ProbedPrefabs[i] == prefab) return true;
            return false;
        }

        // ---- HITLOG: Prefix (снимок ДО удара) ----

        public static void OnAttackPrefix(Humanoid attacker)
        {
            try
            {
                var enemy = attacker as Character;
                if (enemy == null || enemy is Player) return;
                if (FollowerAI.Registry.ContainsKey(enemy)) return; // интересуют только удары НЕ-спутников

                var atk = AttackProfiles.GetCurrentAttack(attacker);
                var weapon = atk != null ? atk.GetWeapon() : null;
                if (weapon == null) weapon = attacker.GetCurrentWeapon();

                Snapshot snap = null;
                for (int i = 0; i < FollowerAI.All.Count; i++)
                {
                    var f = FollowerAI.All[i];
                    if (f == null || f.Body == null || f.Body.IsDead()) continue;

                    var eai = enemy.GetBaseAI();
                    bool targeting = eai != null && (Character)f.Body == eai.GetTargetCreature();
                    bool inZone = atk != null && AttackProfiles.InDangerZone(enemy, atk, f.Body.transform.position);
                    float dist = Vector3.Distance(enemy.transform.position, f.Body.transform.position);
                    float reach = Mathf.Max(Plugin.BlockReach.Value, Plugin.DodgeReach.Value) + 2f;
                    if (!targeting && !inZone && dist > reach) continue;

                    if (snap == null)
                    {
                        snap = new Snapshot();
                        snap.AnimName = atk != null ? atk.m_attackAnimation : "?";
                        snap.WeaponName = weapon != null && weapon.m_shared != null ? weapon.m_shared.m_name : "?";
                        snap.IsArea = atk != null && atk.m_attackType == Attack.AttackType.Area;
                        snap.Dodgeable = weapon != null && weapon.m_shared != null && weapon.m_shared.m_dodgeable;
                        snap.Blockable = weapon != null && weapon.m_shared != null && weapon.m_shared.m_blockable && !snap.IsArea;
                        snap.FactWindup = Mathf.Max(0f, AttackProfiles.TimeSinceStart(enemy));
                        float pw;
                        snap.PredictionKnown = AttackProfiles.TryGetWindup(enemy, out pw);
                        snap.PredictedWindup = snap.PredictionKnown ? pw : Plugin.DefaultAttackWindup.Value;
                    }

                    var cs = new CompanionSnap();
                    cs.DamageBefore = f.DamageTaken;
                    cs.Blocking = f.Body.IsBlocking();
                    cs.OwnAttack = f.Body.InAttack();
                    cs.DodgeInvincible = f.Dodge.Invincible;
                    cs.Stamina = f.Stamina.Current;
                    cs.StaggerDamage = StaggerDamageField != null ? (float)StaggerDamageField.GetValue(f.Body) : -1f;
                    cs.StaggerThreshold = f.Body.GetMaxHealth() * f.Body.m_staggerDamageFactor;
                    Vector3 toEnemy = enemy.transform.position - f.Body.transform.position;
                    toEnemy.y = 0f;
                    cs.Bearing = toEnemy.sqrMagnitude > 0.01f ? Vector3.Angle(f.Body.transform.forward, toEnemy) : 0f;
                    float allowed; string label;
                    bool known = f.Guard.TryGetPending(enemy, out allowed, out label);
                    cs.Allowed = known ? allowed : f.Guard.CurrentAllowedFallback();
                    cs.DecisionLabel = known ? label : "?";
                    cs.Targeting = targeting;
                    cs.InZone = inZone;
                    snap.Companions[f] = cs;
                }
                if (snap != null) _pending[attacker] = snap;

                string prefabForProbe = AttackProfiles.PrefabNameOf(enemy);
                if (IsProbedPrefab(prefabForProbe)) LogClipProbe(enemy, attacker, "ТРИГГЕР(до удара)");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[HITLOG] Prefix ошибка: " + ex.Message); }
        }

        // ---- HITLOG: Postfix (после удара — сверка факта) ----

        public static void OnAttackPostfix(Humanoid attacker)
        {
            try
            {
                Snapshot snap;
                if (!_pending.TryGetValue(attacker, out snap)) return;
                _pending.Remove(attacker);

                var enemy = attacker as Character;
                string prefab = AttackProfiles.PrefabNameOf(enemy);
                float predErr = snap.PredictionKnown ? Mathf.Abs(snap.PredictedWindup - snap.FactWindup) : -1f;

                foreach (var kv in snap.Companions)
                {
                    var f = kv.Key; var cs = kv.Value;
                    if (f == null || f.Body == null) continue;
                    float dmg = f.DamageTaken - cs.DamageBefore;
                    bool dodgedFully = cs.DodgeInvincible && dmg <= 0.01f;
                    bool safe = dmg <= cs.Allowed + 0.01f;
                    string reason = ClassifyReason(snap, cs, dmg, safe, dodgedFully);

                    Plugin.Log.LogInfo("[HITLOG] " + prefab + "/" + snap.AnimName + "(" + snap.WeaponName + ")"
                        + " | прогноз_удара_с=" + (snap.PredictionKnown ? snap.PredictedWindup.ToString("0.000") : "нет_данных(" + snap.PredictedWindup.ToString("0.000") + ")")
                        + " | факт_удара_с=" + snap.FactWindup.ToString("0.000")
                        + (predErr >= 0f ? " (ошибка=" + predErr.ToString("0.000") + "с" + (predErr > 0.1f ? " ⚠" : "") + ")" : "")
                        + " | решение=" + cs.DecisionLabel
                        + " | блок_активен=" + cs.Blocking
                        + " | лицом_к_врагу=" + cs.Bearing.ToString("0") + "°"
                        + " | в_своей_атаке=" + cs.OwnAttack
                        + " | окно_неуязвимости_активно=" + cs.DodgeInvincible
                        + " | стамина=" + cs.Stamina.ToString("0.0")
                        + " | ошеломление=" + (cs.StaggerDamage >= 0f ? cs.StaggerDamage.ToString("0.0") : "?") + "/" + cs.StaggerThreshold.ToString("0.0")
                        + " | урон=" + dmg.ToString("0.0")
                        + " | допустимо=" + cs.Allowed.ToString("0.0")
                        + " | " + (safe ? "БЕЗОПАСНО" : "ПРОПУЩЕН") + " " + reason
                        + " | цель=" + cs.Targeting + " взоне=" + cs.InZone
                        + " blockable=" + snap.Blockable + " dodgeable=" + snap.Dodgeable + " area=" + snap.IsArea
                        + " | боец=" + f.Name);
                }

                if (IsProbedPrefab(prefab)) LogClipProbe(enemy, attacker, "ТРИГГЕР(после удара)");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[HITLOG] Postfix ошибка: " + ex.Message); }
        }

        // Классификация причины ПРОПУЩЕН/БЕЗОПАСНО — чистая диагностика по уже принятому решению,
        // ничего не меняет и не решает заново (решение всегда от CombatGuard/CompanionDodge).
        private static string ClassifyReason(Snapshot snap, CompanionSnap cs, float dmg, bool safe, bool dodgedFully)
        {
            if (safe)
            {
                if (dodgedFully) return "(кувырок сработал)";
                if (cs.Blocking) return "(блок/парирование удержали)";
                if (dmg <= 0.01f) return "(не задело геометрически)";
                return "(в пределах допустимого)";
            }
            if (cs.OwnAttack) return "(СВОЯ_АТАКА — блок/кувырок недоступны)";
            if (cs.Bearing > 90f) return "(СПИНОЙ к врагу, угол " + cs.Bearing.ToString("0") + "°)";
            if (cs.Stamina <= 0.5f) return "(СТАМИНА_0)";
            if (cs.StaggerThreshold > 0f && cs.StaggerDamage >= cs.StaggerThreshold * 0.99f) return "(ОШЕЛОМЛЁН)";
            if (cs.DecisionLabel == "КУВЫРОК")
            {
                if (!snap.Dodgeable) return "(НЕВЕРНОЕ_РЕШЕНИЕ: кувырок выбран на неуклоняемый удар)";
                if (!cs.DodgeInvincible) return "(КУВЫРОК не выполнен/опоздал — не в окне неуязвимости)";
                return "(КУВЫРОК не спас — неуклоняемый удар добрался?)";
            }
            if (cs.DecisionLabel == "ПАРИРОВАНИЕ")
            {
                if (!cs.Blocking) return "(БЛОК не поднят/поднят поздно)";
                return "(НЕВЕРНОЕ_РЕШЕНИЕ: блок поднят, но расчёт допустимого урона разошёлся с фактом)";
            }
            return "(причина не распознана — нет сохранённого решения на этот замах)";
        }

        // ---- CLIPPROBE ----

        public static void OnAttackStartClipProbe(Humanoid attacker)
        {
            try
            {
                var enemy = attacker as Character;
                if (enemy == null) return;
                string prefab = AttackProfiles.PrefabNameOf(enemy);
                if (!IsProbedPrefab(prefab)) return;
                LogClipProbe(enemy, attacker, "СТАРТ(StartAttack)");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[CLIPPROBE] ошибка: " + ex.Message); }
        }

        private static void LogClipProbe(Character enemy, Humanoid body, string phase)
        {
            var animator = AnimatorField != null ? AnimatorField.GetValue(enemy) as Animator : null;
            var atk = AttackProfiles.GetCurrentAttack(body);
            string animName = atk != null ? atk.m_attackAnimation : "?";
            int chain = atk != null && ChainLevelField != null ? (int)ChainLevelField.GetValue(atk) : -1;

            if (animator == null)
            {
                Plugin.Log.LogInfo("[CLIPPROBE] " + phase + " враг=" + AttackProfiles.PrefabNameOf(enemy)
                    + " атака=" + animName + " уровень=" + chain + " animator=НЕТ (поле m_animator пусто)");
                return;
            }

            AnimatorStateInfo state;
            AnimatorClipInfo[] clips;
            try
            {
                state = animator.GetCurrentAnimatorStateInfo(0);
                clips = animator.GetCurrentAnimatorClipInfo(0);
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogInfo("[CLIPPROBE] " + phase + " враг=" + AttackProfiles.PrefabNameOf(enemy)
                    + " атака=" + animName + " уровень=" + chain + " ошибка_чтения_animator=" + ex.Message);
                return;
            }

            var sb = new StringBuilder();
            if (clips == null || clips.Length == 0) sb.Append("нетКлипов(слой0)");
            else
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    var ci = clips[i];
                    var clip = ci.clip;
                    if (i > 0) sb.Append(" ;; ");
                    if (clip == null) { sb.Append("клип" + i + "=null"); continue; }
                    AnimationEvent[] events = null;
                    try { events = clip.events; } catch { }
                    sb.Append("клип=" + clip.name + " length=" + clip.length.ToString("0.000")
                        + " weight=" + ci.weight.ToString("0.00")
                        + " events=" + (events == null ? "null" : events.Length.ToString()));
                    if (events != null && events.Length > 0)
                    {
                        sb.Append(" [");
                        for (int j = 0; j < events.Length; j++)
                        {
                            if (j > 0) sb.Append(",");
                            sb.Append(events[j].time.ToString("0.000") + ":" + events[j].functionName);
                        }
                        sb.Append("]");
                    }
                }
            }

            Plugin.Log.LogInfo("[CLIPPROBE] " + phase + " враг=" + AttackProfiles.PrefabNameOf(enemy)
                + " атака=" + animName + " уровень=" + chain
                + " animator.speed=" + animator.speed.ToString("0.000")
                + " normalizedTime=" + state.normalizedTime.ToString("0.000")
                + " stateLength=" + state.length.ToString("0.000")
                + " | " + sb);
        }
    }

    // Момент старта замаха — точный дискретный момент (не раз-в-кадр поллинг ThreatTracker).
    [HarmonyPatch(typeof(Humanoid), "StartAttack")]
    static class CombatForensicsStartPatch
    {
        static void Postfix(Humanoid __instance, bool __result)
        {
            if (__result) CombatForensics.OnAttackStartClipProbe(__instance);
        }
    }

    // Момент реального удара (хук на Humanoid.OnAttackTrigger — он синхронно оборачивает
    // Attack.OnAttackTrigger() и, для Horizontal/Vertical/Area, доводит удар до Character.Damage
    // ДО того, как наш Postfix здесь отработает — поэтому разница DamageTaken "до/после" внутри
    // одного вызова корректно приписывается именно этому замаху).
    [HarmonyPatch(typeof(Humanoid), "OnAttackTrigger")]
    static class CombatForensicsHitLogPatch
    {
        static void Prefix(Humanoid __instance) { CombatForensics.OnAttackPrefix(__instance); }
        static void Postfix(Humanoid __instance) { CombatForensics.OnAttackPostfix(__instance); }
    }
}

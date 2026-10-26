using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Решение «парировать / кувырок / блок как лучшее из худшего» на каждый летящий в спутника
    // удар (этап «Бой v2а»). Источник угроз — ThreatTracker (видит ВСЕХ врагов рядом, не только
    // текущую цель MonsterAI — важно для боя 1-против-толпы). На каждый новый замах считаем
    // решение один раз (не каждый кадр) по формуле допустимого урона (min(MaxHitOfMax*maxHP,
    // MaxHitOfCurrent*HP), в режиме «ранен» — отдельный маленький порог с гистерезисом).
    public class CombatGuard
    {
        private static readonly FieldInfo BlockingField = AccessTools.Field(typeof(Character), "m_blocking");
        private static readonly FieldInfo BlockTimerField = AccessTools.Field(typeof(Humanoid), "m_blockTimer");
        private static readonly FieldInfo CurrentAttackField = AccessTools.Field(typeof(Humanoid), "m_currentAttack");
        private static readonly FieldInfo ZAnimField = AccessTools.Field(typeof(Character), "m_zanim");
        private static readonly FieldInfo BlockingHashField = AccessTools.Field(typeof(Humanoid), "s_blocking");
        private static readonly FieldInfo BodyRbField = AccessTools.Field(typeof(Character), "m_body");
        private static readonly FieldInfo LeftItemField = AccessTools.Field(typeof(Humanoid), "m_leftItem");
        private static readonly FieldInfo AttackTimeField = AccessTools.Field(typeof(Attack), "m_time");
        private static readonly FieldInfo StaggerDamageField = AccessTools.Field(typeof(Character), "m_staggerDamage");
        private static readonly FieldInfo AttackZAnimField = AccessTools.Field(typeof(Attack), "m_zanim");

        private struct Decision
        {
            public bool WantDodge;
            public bool Blockable;
            // Прогноз этого УДАРА (динамически, по текущим HP/стамине/броне/прокачке блока — НЕ
            // привязано к конкретному мобу) пробивает допустимый урон даже ПОСЛЕ блока, или урон
            // неблокируемый/площадной — для такой атаки блок/парирование как «запасной план при
            // нехватке стамины на кувырок» запрещён (правило Ильи): либо кувырок, либо ничего —
            // лучше отступить заранее, чем стоять под гарантированно опасным ударом с щитом.
            public bool TooStrongToBlock;
            public float Allowed;
            public float PredictedAfterBlock;
            public string Info;
        }

        private Character _character;
        private Humanoid _body;
        private CompanionDodge _dodge;
        private string _logName = "?";
        private bool _blocking;
        private Rigidbody _bodyRb;

        private readonly Dictionary<Character, bool> _attackingPrev = new Dictionary<Character, bool>();
        private readonly Dictionary<Character, Decision> _decisions = new Dictionary<Character, Decision>();
        private readonly List<Threat> _relevantBuf = new List<Threat>();

        private float _blockerCacheTimer;
        private bool _hasBlockerCached;
        private float _blockPowerCached;
        private float _timedBonusCached;

        private float _blockHoldUntil;
        private float _priorityUntil;
        private bool _injured;

        public bool WantsBlock { get; private set; }
        public bool WaitingWindow { get; private set; }
        public bool IsInjured { get { return _injured; } }
        public bool PriorityAttackWindow { get { return Time.time < _priorityUntil; } }
        // Успешный кувырок = удар отклонён неуязвимостью (Character.Damage не вызовется вовсе) —
        // в метриках "безопасных ударов" считается безопасным, но никогда не доходит до
        // CompanionDamageMetricsPatch (нет события урона), поэтому считаем его здесь отдельно.
        public int DodgeSuccesses { get; private set; }
        // Безопасное окно для контратаки (правило Ильи «бить в каждое безопасное окно»): открывается
        // после успешного кувырка (враг промахнулся), успешного парирования или оглушения врага —
        // на это время CompanionAttackReservePatch пропускает свою атаку без обычной проверки «безопасно
        // стартовать». WindowsAvailable — сколько раз окно открылось; «использовано» считает FollowerAI
        // (знает момент старта своей атаки) по PriorityAttackWindow на этот момент.
        public int WindowsAvailable { get; private set; }
        // «Попытки блока/парирования, которые по прогнозу были недопустимы» (TooStrongToBlock) —
        // должно быть 0; живой счётчик-страховка на случай, если решение всё же дошло сюда помимо
        // явного запрета в фоллбэке выше (см. метрику troll_solo).
        public int UnsafeBlockAttempts { get; private set; }

        private void OpenAttackWindow(float dur)
        {
            _priorityUntil = Time.time + dur;
            WindowsAvailable++;
        }

        // Вызывается из CombatPatches.CompanionBlockStatsPatch при успешном ПАРИРОВАНИИ (таймер
        // блока < 0.25с) — тот же тип «безопасного окна», что и после удачного кувырка.
        public void NotifyParrySuccess()
        {
            OpenAttackWindow(Plugin.AttackWindowDuration.Value);
        }

        public void Init(Humanoid body, CompanionDodge dodge, string name)
        {
            _body = body;
            _character = body;
            _dodge = dodge;
            _logName = name;
            _bodyRb = BodyRbField != null ? BodyRbField.GetValue(_character) as Rigidbody : null;
        }

        // ---- точки вызова для «Ко мне» в бою (CompanionRecall.cs, ветка recall) ----
        // Пока идёт отзыв, Tick() этого класса НЕ вызывается (CompanionRecall решает сам,
        // направление кувырка другое — к хозяину, а не в сторону от удара) — эти обёртки
        // дают ему те же готовые механизмы блока/стамины/разворота без копирования кода.
        public void StopAttack() { if (_body != null) StopOwnAttack(); }
        // WantsBlock тоже обновляем — иначе плашка (FollowerAI.Update) показывает статус блока
        // по значению, оставшемуся от ПРОШЛОГО обычного боя (Guard.Tick не зовётся, пока Recalling).
        public void ForceBlock(bool on, float dt) { if (_character == null) return; SetBlock(on, dt); WantsBlock = on; }
        public bool HasBlockerNow(float dt) { return _body != null && HasBlocker(dt); }
        public void FaceTarget(Character target) { if (_character != null) FaceExact(target); }

        public void Tick(ThreatTracker tracker, float dt)
        {
            if (_character == null || tracker == null) { WantsBlock = false; SetBlock(false, dt); return; }
            UpdateInjured();

            // Экстренный выход из кольца — приоритетнее обычного решения блок/кувырок.
            if (tracker.Surrounded && tracker.SurroundedFor > 0.4f && !_dodge.InDodge)
            {
                StopOwnAttack(); // та же асимметрия блок/кувырок (combatlab_design.md §1) — иначе TryDodge молча откажет
                Vector3 escape = Quaternion.Euler(0f, tracker.FreeArcCenterDeg, 0f) * _character.transform.forward;
                if (_dodge.TryDodge(escape))
                {
                    Plugin.Log.LogInfo("[CombatGuard] " + _logName + " в кольце (" + tracker.EnemiesNear
                        + " врагов, свободно " + (int)tracker.FreeArcDeg + "°) — кувырок из окружения");
                    WantsBlock = false;
                    SetBlock(false, dt);
                    return;
                }
            }

            // Новые замахи — считаем решение один раз на старт (не каждый кадр).
            foreach (var t in tracker.Threats)
            {
                if (t.Enemy == null) continue; // умер/исчез между сканированием ThreatTracker и этим тиком
                bool prevAtt;
                _attackingPrev.TryGetValue(t.Enemy, out prevAtt);
                if (t.Attacking && !prevAtt)
                {
                    var d = ComputeDecision(t);
                    _decisions[t.Enemy] = d;
                    Plugin.Log.LogInfo("[CombatGuard] " + _logName + " " + d.Info);
                }
                _attackingPrev[t.Enemy] = t.Attacking;
            }

            // Угрозы, на которые реагируем прямо сейчас: атакуют, целятся в меня, в досягаемости.
            _relevantBuf.Clear();
            Threat dodgePrimary = null, parryPrimary = null;
            float dodgeRemain = float.MaxValue, parryRemain = float.MaxValue;
            foreach (var t in tracker.Threats)
            {
                if (t.Enemy == null || !t.Attacking || !t.TargetingMe) continue;
                Decision d;
                if (!_decisions.TryGetValue(t.Enemy, out d)) continue;
                float reach = d.WantDodge ? Mathf.Max(Plugin.BlockReach.Value, Plugin.DodgeReach.Value) : Plugin.BlockReach.Value;
                if (t.Dist > reach) continue;
                _relevantBuf.Add(t);
                if (d.WantDodge) { if (t.TimeToHit < dodgeRemain) { dodgeRemain = t.TimeToHit; dodgePrimary = t; } }
                else { if (t.TimeToHit < parryRemain) { parryRemain = t.TimeToHit; parryPrimary = t; } }
            }

            // Активная проверка ВО ВРЕМЯ своей атаки (не только на старте, см. CompanionAttackReservePatch):
            // если среди релевантных угроз есть удар, который придёт раньше, чем закончится наш
            // текущий замах — прерываем атаку сразу, не дожидаясь, пока это (не) сработает через
            // блок/кувырок ниже (combatlab_design.md §1, проблема Б — 70% пропущенных ударов).
            if (_body.InAttack() && _relevantBuf.Count > 0)
            {
                float remaining = OwnAttackRemaining();
                for (int i = 0; i < _relevantBuf.Count; i++)
                {
                    if (_relevantBuf[i].TimeToHit < remaining) { StopOwnAttack(); break; }
                }
            }

            // Доворот: ТОЧНО на угрозу, которую прямо сейчас парируем/кувыркаем (Humanoid.BlockAttack
            // засчитывает блок только если удар пришёл не со спины — доворот на "центр толпы" здесь
            // мог бы увести от реального угла и дать полный урон сквозь поднятый блок). Центр толпы —
            // только когда никого конкретного не защищаем прямо сейчас (общая манера боя 1-на-толпу).
            Threat faceTarget = parryPrimary != null ? parryPrimary : dodgePrimary;
            if (faceTarget != null) FaceExact(faceTarget.Enemy);
            else if (_relevantBuf.Count > 0) FaceThreats(_relevantBuf);

            if (dodgePrimary != null)
            {
                // Целимся так, чтобы удар пришёл в СЕРЕДИНУ окна неуязвимости (правило Ильи —
                // симметричный запас на ошибку прогноза в обе стороны), не впритык к концу окна,
                // как было раньше. DodgeSafetyMargin — доп. допуск на известную погрешность
                // прогноза windup врага, а не смещение к краю (combatlab_design.md §6).
                float lead = Mathf.Max(0.05f, _dodge.CalibratedDuration * 0.5f + Plugin.DodgeSafetyMargin.Value);
                float freshD;
                if (AnimTiming.TryTimeToHit(dodgePrimary.Enemy, out freshD)) dodgeRemain = freshD;
                bool alreadyDodged = dodgePrimary.Atk != null && ReferenceEquals(dodgePrimary.Atk, _dodgedAtk);
                if (dodgeRemain <= lead && !alreadyDodged)
                {
                    // Та же отмена своей атаки, что уже есть в parry-ветке ниже — без неё
                    // CompanionDodge.TryDodge сам откажет (InAttack()==true), и кувырок, в отличие
                    // от блока, никак не отменяет свою атаку (combatlab_design.md §1, проблема А).
                    StopOwnAttack();
                    Vector3 dir = ChooseDodgeDirection(dodgePrimary.Enemy, tracker);
                    if (_dodge.TryDodge(dir))
                    {
                        _dodgedAtk = dodgePrimary.Atk;
                        DodgeSuccesses++;
                        // Враг только что промахнулся и сейчас восстанавливается — безопасное окно
                        // для контратаки (правило Ильи), не только оборона.
                        OpenAttackWindow(Plugin.AttackWindowDuration.Value);
                        Plugin.Log.LogInfo("[CombatGuard] " + _logName + " увернулся от " + AttackProfiles.PrefabNameOf(dodgePrimary.Enemy));
                        WantsBlock = false;
                        SetBlock(false, dt);
                        return;
                    }
                    // Кувырком не вышло (нет стамины) — блок как лучшее из худшего, НО ТОЛЬКО если
                    // прогноз говорит, что блок реально помогает (dd.Blockable) И урон после блока
                    // всё равно укладывается в допустимое (!dd.TooStrongToBlock) — правило Ильи:
                    // против гарантированно слишком сильного удара запасной блок запрещён, лучше
                    // пропустить удар честно (и этот факт будет виден в "безопасно"), чем стоять с
                    // неубедительным щитом перед ударом, который прогнозом уже признан непосильным.
                    Decision dd;
                    if (parryPrimary == null && _decisions.TryGetValue(dodgePrimary.Enemy, out dd) && dd.Blockable && !dd.TooStrongToBlock)
                        parryPrimary = dodgePrimary;
                }
            }

            if (parryPrimary != null)
            {
                bool hasBlocker = HasBlocker(dt);
                if (hasBlocker)
                {
                    float since = AttackProfiles.TimeSinceStart(parryPrimary.Enemy);
                    // Свежий прогноз прямо сейчас (скан угроз идёт 10 раз/с и может отставать на кадры).
                    float tti = parryPrimary.TimeToHit, fresh;
                    if (AnimTiming.TryTimeToHit(parryPrimary.Enemy, out fresh)) tti = fresh;
                    bool raiseNow = tti < float.MaxValue
                        ? tti <= Plugin.BlockLeadTime.Value + Time.deltaTime
                        : ShouldRaiseBlockNow(parryPrimary.Enemy, since);
                    bool wasBlocking = _blocking;
                    if (raiseNow)
                    {
                        if (!wasBlocking)
                        {
                            Decision pd;
                            if (_decisions.TryGetValue(parryPrimary.Enemy, out pd) && pd.TooStrongToBlock)
                            {
                                UnsafeBlockAttempts++;
                                Plugin.Log.LogInfo("[CombatGuard] " + _logName + " ВНИМАНИЕ: поднимаю блок против удара, прогноз которого уже недопустим ("
                                    + AttackProfiles.PrefabNameOf(parryPrimary.Enemy) + ") — нарушение правила, должно быть 0");
                            }
                        }
                        StopOwnAttack();
                        SetBlock(true, dt);
                        // «Перехват» щита под НОВЫЙ удар: если щит уже был поднят (держим с прошлого удара),
                        // сбрасываем таймер блока — как игрок, который заново жмёт блок перед ударом.
                        // Иначе таймер > 0.25с и парирования не бывает никогда.
                        if (parryPrimary.Atk != null && !ReferenceEquals(parryPrimary.Atk, _parryArmedAtk))
                        {
                            _parryArmedAtk = parryPrimary.Atk;
                            if (wasBlocking && BlockTimerField != null) { _blockRaisedAt = Time.time; BlockTimerField.SetValue(_body, 0f); }
                        }
                        _blockHoldUntil = Time.time + Plugin.BlockLinger.Value;
                        if (!wasBlocking) LogBlockRaised(parryPrimary.Enemy, since);
                    }
                    else if (Time.time < _blockHoldUntil)
                    {
                        SetBlock(true, dt);
                    }
                    else SetBlock(false, dt);
                    WantsBlock = raiseNow || Time.time < _blockHoldUntil;
                }
                else { SetBlock(false, dt); WantsBlock = false; }

                if (parryPrimary.Enemy.IsStaggering())
                {
                    OpenAttackWindow(0.5f);
                    _blockHoldUntil = 0f;
                    SetBlock(false, dt);
                    WantsBlock = false;
                }
            }
            else if (Time.time < _blockHoldUntil && HasBlocker(dt))
            {
                WantsBlock = true;
                SetBlock(true, dt);
            }
            else
            {
                WantsBlock = false;
                SetBlock(false, dt);
            }
        }

        // ---- решение по конкретной угрозе ----

        private void UpdateInjured()
        {
            float maxHp = _character.GetMaxHealth();
            if (maxHp <= 0f) return;
            float frac = _character.GetHealth() / maxHp;
            if (!_injured && frac <= Plugin.InjuredHpFraction.Value)
            {
                _injured = true;
                Plugin.Log.LogInfo("[CombatGuard] " + _logName + " РАНЕН: HP " + (frac * 100f).ToString("0") + "%");
            }
            else if (_injured && frac >= Plugin.RecoveredHpFraction.Value)
            {
                _injured = false;
                Plugin.Log.LogInfo("[CombatGuard] " + _logName + " вышел из «ранен»: HP " + (frac * 100f).ToString("0") + "%");
            }
        }

        public static float StyleRiskMul()
        {
            switch (Plugin.Style.Value)
            {
                case CombatStyle.Aggressive: return 1.6f;
                case CombatStyle.Balanced: return 1.25f;
                default: return 1.0f;
            }
        }

        public static float StyleMarginMul()
        {
            switch (Plugin.Style.Value)
            {
                case CombatStyle.Aggressive: return 0.45f;
                case CombatStyle.Balanced: return 0.7f;
                default: return 1.0f;
            }
        }

        public float CurrentAllowedFallback()
        {
            if (_character == null) return 0f;
            float maxHp = _character.GetMaxHealth();
            float hp = _character.GetHealth();
            if (_injured) return Plugin.InjuredAllowedFraction.Value * maxHp;
            return Mathf.Min(Plugin.MaxHitOfMax.Value * maxHp, Plugin.MaxHitOfCurrent.Value * hp) * StyleRiskMul();
        }

        public bool TryGetPending(Character attacker, out float allowed, out string label)
        {
            allowed = 0f; label = "?";
            Decision d;
            if (attacker == null || !_decisions.TryGetValue(attacker, out d)) return false;
            allowed = d.Allowed;
            label = d.WantDodge ? "КУВЫРОК" : "ПАРИРОВАНИЕ";
            return true;
        }

        // ПАРИРОВАНИЕ или КУВЫРОК по формуле допустимого урона (спека этапа «Бой v2а»):
        // допустимо = МЕНЬШЕЕ из (MaxHitOfMax*maxHP, MaxHitOfCurrent*HP), в «ранен» — отдельный
        // маленький порог. Неблокируемый/площадной удар или урон после блока выше порога — кувырок
        // (если он вообще применим к этому удару), иначе парирование/блок.
        private Decision ComputeDecision(Threat t)
        {
            var d = new Decision();
            float maxHp = _character.GetMaxHealth();
            float hp = _character.GetHealth();
            d.Allowed = CurrentAllowedFallback();

            var atk = t.Atk;
            var weapon = t.Weapon;
            string prefab = AttackProfiles.PrefabNameOf(t.Enemy);
            if (weapon == null || weapon.m_shared == null || atk == null)
            {
                d.WantDodge = false;
                d.Blockable = true;
                d.Info = "враг=" + prefab + " — нет данных об атаке, блокируем по умолчанию, допустимо=" + d.Allowed.ToString("0.0");
                return d;
            }

            bool isArea = atk.m_attackType == Attack.AttackType.Area;
            bool blockable = weapon.m_shared.m_blockable && !isArea;
            bool dodgeable = weapon.m_shared.m_dodgeable;
            d.Blockable = blockable;

            float mul = atk.m_damageMultiplier * (1f + Mathf.Max(t.Enemy.GetLevel() - 1, 0) * 0.5f);
            float raw = weapon.GetDamage().GetTotalDamage() * mul;
            // Неблокируемые компоненты (m_damage/m_chop/m_pickaxe, см. _research/2b2fix_1.md §2.6)
            // проходят ПОЛНОСТЬЮ независимо от блока — раньше вся сумма шла через ApplyArmor,
            // занижая прогноз почти до нуля для оружий типа Dragur axe (combatlab_design.md §5,
            // combatlab_forensics.md §6.2: допустимо≈14, факт≈22-25 при "послеБлока≈0").
            float blockableRaw = weapon.GetDamage().GetTotalBlockableDamage() * mul;
            float unblockableRaw = Mathf.Max(0f, raw - blockableRaw);
            float blockPower = BlockPowerNow(0f);
            float timedBonus = _timedBonusCached;
            float effectivePower = timedBonus > 1f ? blockPower * timedBonus : blockPower;
            float afterBlock = unblockableRaw + HitData.DamageTypes.ApplyArmor(blockableRaw, effectivePower);
            d.PredictedAfterBlock = afterBlock;

            bool unblockableOrArea = isArea || !weapon.m_shared.m_blockable;
            d.TooStrongToBlock = unblockableOrArea || afterBlock > d.Allowed;
            bool wantDodge = d.TooStrongToBlock;

            // Прогноз шкалы ошеломления (правило Ильи: держать ≤70% порога перед блоком, иначе
            // кувырок). Стаггер копится от урона ПОСЛЕ блока, и если он уже близко к порогу —
            // формально "безопасный" блок может срывом стаггера пропустить удар целиком
            // (_research/2b2fix_1.md §5: стаггер + следующий удар ×2, combatlab_design.md §7).
            float staggerNow = StaggerDamageField != null ? (float)StaggerDamageField.GetValue(_body) : 0f;
            float staggerThreshold = maxHp * _character.m_staggerDamageFactor;
            float predictedStaggerFrac = staggerThreshold > 0f ? (staggerNow + afterBlock) / staggerThreshold : 0f;
            bool staggerRisk = !wantDodge && blockable && predictedStaggerFrac >= Plugin.StaggerForecastFraction.Value;
            if (staggerRisk) wantDodge = true;

            if (wantDodge && !dodgeable) wantDodge = false; // кувырок бесполезен — остаётся блок, если возможен
            d.WantDodge = wantDodge;

            d.Info = "враг=" + prefab + " оружие=" + weapon.m_shared.m_name + " уровень=" + t.Enemy.GetLevel()
                + " сырой=" + raw.ToString("0.0") + " (неблок.=" + unblockableRaw.ToString("0.0") + ")"
                + " blockPower=" + blockPower.ToString("0.0")
                + " (пар.x" + timedBonus.ToString("0.0") + "=" + effectivePower.ToString("0.0") + ")"
                + " послеБлока=" + afterBlock.ToString("0.0") + " допустимо=" + d.Allowed.ToString("0.0")
                + " ошеломление_прогноз=" + (predictedStaggerFrac * 100f).ToString("0") + "%"
                + (staggerRisk ? " [РИСК_ОГЛУШЕНИЯ]" : "")
                + (_injured ? " [РАНЕН]" : "") + " HP=" + hp.ToString("0.0") + "/" + maxHp.ToString("0.0")
                + " blockable=" + blockable + " dodgeable=" + dodgeable + " area=" + isArea
                + " стиль=" + Plugin.Style.Value
                + " => " + (wantDodge ? "КУВЫРОК" : "ПАРИРОВАНИЕ");
            return d;
        }

        // Поднимать блок не раньше, чем за Plugin.BlockLeadTime до расчётного момента удара
        // (калибровка AttackProfiles). Пока данных по этому врагу/анимации нет — консервативный
        // дефолт: минимальный Windup среди уже откалиброванных атак (а не фиксированная константа,
        // combatlab_design.md §4), раньше поднятый блок безопаснее опоздавшего.
        private bool ShouldRaiseBlockNow(Character target, float since)
        {
            if (since < 0f) return true;
            float windup;
            if (!AttackProfiles.TryGetWindup(target, out windup)) windup = AttackProfiles.GetConservativeDefault();
            // Без «windup*0.7»: у длинных замахов (скелет ~1с) щит вставал за ~0.3с до удара —
            // это больше окна парирования 0.25с, и каждый удар считался обычным блоком.
            float lead = Mathf.Max(0f, windup - Plugin.BlockLeadTime.Value);
            return since >= lead;
        }

        // Оставшееся время СВОЕГО текущего замаха (BusyDuration-Windup калибровка минус уже
        // прошедшее Attack.m_time) — используется для активной отмены атаки во время замаха,
        // не только на старте (combatlab_design.md §1, проблема Б).
        private float OwnAttackRemaining()
        {
            if (CurrentAttackField == null || AttackTimeField == null) return 0f;
            var atk = CurrentAttackField.GetValue(_body) as Attack;
            if (atk == null) return 0f;
            float elapsed;
            try { elapsed = (float)AttackTimeField.GetValue(atk); } catch { return 0f; }
            string key = AttackProfiles.KeyFor(_body);
            float busy; int nb;
            if (!AttackProfiles.TryGetBusyDurationByKey(key, out busy, out nb))
            {
                float w; int nw;
                busy = AttackProfiles.TryGetWindupByKey(key, out w, out nw) ? w : Plugin.DefaultAttackWindup.Value;
            }
            return Mathf.Max(0f, busy - elapsed);
        }

        // Отмена своей атаки — общий хелпер для dodge- и parry-веток (раньше была только в
        // parry, асимметрия была прямой причиной 70% пропусков, combatlab_design.md §1 проблема А).
        // IL ПОДТВЕРЖДЕНО (tuner_il1/il2.txt): Humanoid.InAttack() читает ТОЛЬКО тег аниматора
        // "attack" (текущий/следующий стейт) — не трогает m_currentAttack/Attack вообще. Attack.Stop()
        // шлёт зверю SetTrigger("attack_abort") ТОЛЬКО если m_loopingAttack==true; для обычного
        // (не зацикленного) замаха Stop() не создаёт перехода в Animator'е — клип доигрывает до конца,
        // InAttack() остаётся True все эти ~0.4-0.8с хвоста, и TryDodge()/блок всё это время молча
        // отказывают (это и было главной причиной пропусков "в_своей_атаке=True"). Поэтому шлём
        // "attack_abort" САМИ, без проверки m_loopingAttack — если в контроллере аниматора для этого
        // состояния нет такого перехода, триггер просто игнорируется (не хуже, чем раньше).
        private void StopOwnAttack()
        {
            if (!_body.InAttack() || CurrentAttackField == null) return;
            var atk = CurrentAttackField.GetValue(_body) as Attack;
            if (atk == null) return;
            atk.Stop();
            try
            {
                var zanim = AttackZAnimField != null ? AttackZAnimField.GetValue(atk) as ZSyncAnimation : null;
                if (zanim != null) zanim.SetTrigger("attack_abort");
            }
            catch { }
        }

        private void LogBlockRaised(Character target, float since)
        {
            float windup;
            bool known = AttackProfiles.TryGetWindup(target, out windup);
            string windupStr = known ? windup.ToString("0.00") : "нет данных(" + Plugin.DefaultAttackWindup.Value.ToString("0.00") + ")";
            float leadActual = known ? windup - since : -1f;
            Plugin.Log.LogInfo("[CombatGuard] " + _logName + " блок поднят, враг=" + AttackProfiles.PrefabNameOf(target)
                + " с начала замаха=" + since.ToString("0.00") + "с windup=" + windupStr
                + " (лид факт.=" + (known ? leadActual.ToString("0.00") : "?") + "с, цель=" + Plugin.BlockLeadTime.Value.ToString("0.00") + "с)");
        }

        // Применить новый поворот сразу к transform И к Rigidbody (m_body) — без второго физика
        // на следующем FixedUpdate утаскивает ориентацию назад к старому повороту тела, и удар
        // засчитывается как "со спины" в Humanoid.BlockAttack (Dot(hit.m_dir,forward)>0), блок/
        // парирование срывается вслепую. Тот же паттерн уже исправлен в CompanionDodge.TryDodge,
        // здесь не был повторён (combatlab_design.md §3, _research/cv2a_1.md п.0.2).
        private void ApplyFacing(Quaternion rot)
        {
            _character.transform.rotation = rot;
            if (_bodyRb != null) _bodyRb.rotation = rot;
        }

        // Довернуть точно на конкретного атакующего — критично для блока/парирования (угловая
        // проверка в Humanoid.BlockAttack), курсорный доворот "в среднем" тут не годится.
        private void FaceExact(Character target)
        {
            if (target == null) return;
            Vector3 face = target.transform.position - _character.transform.position;
            face.y = 0f;
            if (face.sqrMagnitude > 0.01f) ApplyFacing(Quaternion.LookRotation(face.normalized));
        }

        // Повернуть к "центру тяжести" угроз — держать всех врагов перед собой (бой 1-против-толпы),
        // когда прямо сейчас никого конкретного не парируем/кувыркаем. Вес 1/дистанция (не равный
        // вес всем) — ближний враг в тыл иначе "тонет" в сумме векторов рядом с дальним спереди и
        // доворот его не замечает, хотя именно он бьёт в непрокрываемую блоком спину
        // (combatlab_design.md §8).
        private void FaceThreats(List<Threat> relevant)
        {
            Vector3 sum = Vector3.zero;
            foreach (var t in relevant)
            {
                Vector3 d = t.Enemy.transform.position - _character.transform.position;
                d.y = 0f;
                if (d.sqrMagnitude > 0.01f) sum += d.normalized * (1f / Mathf.Max(0.5f, t.Dist));
            }
            if (sum.sqrMagnitude < 0.0001f) return;
            ApplyFacing(Quaternion.LookRotation(sum.normalized));
        }

        // Вбок от линии атаки основной угрозы; избегаем препятствий и приземления в зону ДРУГОЙ
        // атакующей угрозы (кувырок в безопасное окно сразу от всех, не только от primary).
        private Vector3 ChooseDodgeDirection(Character target, ThreatTracker tracker)
        {
            Vector3 toTarget = target.transform.position - _character.transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.01f) toTarget = _character.transform.forward;
            toTarget.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, toTarget);
            Vector3 origin = _character.transform.position + Vector3.up;
            const float probe = 2.2f;

            if (!DirBlocked(origin, side, probe) && !LandsInAnyZone(tracker, side, probe)) return side;
            if (!DirBlocked(origin, -side, probe) && !LandsInAnyZone(tracker, -side, probe)) return -side;
            if (!DirBlocked(origin, -toTarget, probe) && !LandsInAnyZone(tracker, -toTarget, probe)) return -toTarget;
            // Ничего идеального — хоть что-то без стены.
            if (!DirBlocked(origin, side, probe)) return side;
            if (!DirBlocked(origin, -side, probe)) return -side;
            return -toTarget;
        }

        private bool LandsInAnyZone(ThreatTracker tracker, Vector3 dir, float dist)
        {
            Vector3 landing = _character.transform.position + dir.normalized * dist;
            foreach (var t in tracker.Threats)
            {
                if (!t.Attacking || t.Atk == null) continue;
                if (AttackProfiles.InDangerZone(t.Enemy, t.Atk, landing)) return true;
            }
            return false;
        }

        private static bool DirBlocked(Vector3 origin, Vector3 dir, float dist)
        {
            RaycastHit hit;
            if (!Physics.Raycast(origin, dir, out hit, dist)) return false;
            return hit.collider != null && hit.collider.GetComponentInParent<Character>() == null;
        }

        private float BlockPowerNow(float dt)
        {
            HasBlocker(dt);
            return _blockPowerCached;
        }

        // ТОТ ЖЕ предмет, что реально использует игра (Humanoid.GetCurrentBlocker(), приватный —
        // по IL: m_leftItem, а если пусто — текущее оружие). НАЙДЕНО при доработке круга 1: старый
        // код брал МАКСИМАЛЬНЫЙ blockPower среди ВСЕХ экипированных предметов (GetEquippedItems) —
        // если у оружия в правой руке blockPower оказывался выше, чем у щита в левой, прогноз считал
        // блок сильнее, чем он есть реально (подтверждено логом: предсказанный blockPower=13.3,
        // реальный GetCurrentBlocker()=ShieldWood с blockPower=7.95 — расхождение ~1.7x, из-за
        // которого ПАРИРОВАНИЕ выглядело безопасным, хотя реально нет).
        // Публичный «подсмотреть» без траты кадра кэша (dt=0, тот же приём, что BlockPowerNow(0f)
        // в ComputeDecision) — для StaminaPolicy.Reserve (нужен только факт «есть щит», не блок-пауэр).
        public bool HasBlockerNow()
        {
            return HasBlocker(0f);
        }

        private bool HasBlocker(float dt)
        {
            // HasBlockerNow() — публичный вход для StaminaPolicy.HasShield(), вызывается из
            // CompanionAttackReservePatch.Prefix на КАЖДЫЙ Attack.Start() (событие игры, не наш
            // Update()). FollowerAI.Init() кладёт боца в Registry раньше, чем SetupOnce() вызовет
            // Guard.Init() и установит _body — в этом окне _body==null, без проверки
            // LeftItemField.GetValue(_body)/_body.GetCurrentWeapon() бросят исключение прямо из
            // Harmony-патча (ревью этапа «Фикс»).
            if (_body == null) return false;
            _blockerCacheTimer -= dt;
            if (_blockerCacheTimer > 0f) return _hasBlockerCached;
            _blockerCacheTimer = 0.25f;
            _hasBlockerCached = false;
            _blockPowerCached = 0f;
            _timedBonusCached = 1f;
            // Реальный (растущий, хранимый за именем) навык этого же бойца — тот же путь,
            // что и настоящий блок в бою (Humanoid.BlockAttack -> ItemData.GetBlockPower ->
            // GetSkillFactor, патчен в CompanionSkillFactorPatch на CompanionSkills.GetFactor).
            // Раньше тут была фиксированная Plugin.CompanionSkillFactor.Value — прогноз совпадал
            // с реальным блоком только пока навык не поднялся выше стартового.
            float skillFactor = _body.GetSkillFactor(Skills.SkillType.Blocking);
            var blocker = LeftItemField != null ? LeftItemField.GetValue(_body) as ItemDrop.ItemData : null;
            if (blocker == null) blocker = _body.GetCurrentWeapon();
            if (blocker != null && blocker.m_shared.m_blockPower > 0f)
            {
                _hasBlockerCached = true;
                _blockPowerCached = blocker.GetBlockPower(skillFactor);
                _timedBonusCached = blocker.m_shared.m_timedBlockBonus;
            }
            return _hasBlockerCached;
        }

        // m_blocking (Character) решает, поднят ли блок; m_blockTimer (Humanoid) — когда именно
        // (окно парирования в BlockAttack, <0.25с = идеальный блок). Ведём таймер сами — его
        // штатно крутит только Humanoid.UpdateBlock, которую вызывает лишь Player.
        private float _blockRaisedAt;  // когда поднят (или «перехвачен») щит — от него считается таймер парирования
        private Attack _parryArmedAtk; // замах врага, под который уже «перехвачен» щит
        private Attack _dodgedAtk;     // замах, от которого уже ушли кувырком — второй кувырок на него запрещён

        private void SetBlock(bool on, float dt)
        {
            if (BlockingField == null || _character == null) return;
            if (on != _blocking)
            {
                BlockingField.SetValue(_character, on);
                _blocking = on;
                _blockRaisedAt = Time.time;
                if (BlockTimerField != null) BlockTimerField.SetValue(_body, on ? 0f : -1f);
                TrySetBlockAnim(on);
            }
            else if (on && BlockTimerField != null)
            {
                // Таймер = реальное время с подъёма щита (раньше копили +dt, а SetBlock зовётся
                // несколько раз за кадр — таймер бежал вдвое быстрее и окно парирования «закрывалось»).
                BlockTimerField.SetValue(_body, Time.time - _blockRaisedAt);
            }
        }

        // Ручная эмуляция блока корректна по урону (m_blocking читается напрямую), но без этого
        // модель не поднимает щит визуально — Humanoid.UpdateBlock (только у Player) делает это
        // через ZSyncAnimation.SetBool(s_blocking,...), воспроизводим то же самое (_research/cv2a_1.md §4.4).
        private void TrySetBlockAnim(bool on)
        {
            try
            {
                var zanim = ZAnimField != null ? ZAnimField.GetValue(_character) as ZSyncAnimation : null;
                if (zanim == null || BlockingHashField == null) return;
                int hash = (int)BlockingHashField.GetValue(null);
                zanim.SetBool(hash, on);
            }
            catch { }
        }
    }
}

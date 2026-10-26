using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Кувырок спутника — своя реализация: у Character/Humanoid нет полей/методов кувырка,
    // только заглушки InDodge()/IsDodgeInvincible()=false (патчатся в CombatPatches.cs).
    // Окно неуязвимости калибруется по факту (анимационный тег "dodge" — число секунд не в коде,
    // только в Animator'е, та же идея, что AttackProfiles для врагов, _research/cv2a_1.md §2).
    public class CompanionDodge
    {
        private static readonly FieldInfo ZAnimField = AccessTools.Field(typeof(Character), "m_zanim");
        private static readonly FieldInfo BodyField = AccessTools.Field(typeof(Character), "m_body");
        private static readonly FieldInfo AnimatorField = AccessTools.Field(typeof(Character), "m_animator");
        private static readonly MethodInfo NextOrCurrentHashMethod = AccessTools.Method(typeof(Character), "GetNextOrCurrentAnimHash");
        private static readonly FieldInfo DodgeTagField = AccessTools.Field(typeof(Player), "s_animatorTagDodge");
        private static int _dodgeTagHash = int.MinValue;

        public bool InDodge;
        public bool Invincible;
        public int DodgeCount;
        // Попытка кувырнуться упёрлась именно в нехватку стамины (не в InAttack/мёртв/перегруз/
        // уже-кувыркается) — метрика этапа «модель стамины» (запрос Ильи, цель 0): StaminaPolicy
        // должен держать запас так, чтобы это не случалось.
        public int StaminaDenied;
        // Стоимость кувырка (как ComputeCost) — публично, чтобы StaminaPolicy.Reserve не дублировал формулу.
        public float Cost { get { return ComputeCost(); } }
        // Стартовое приближение = Plugin.DodgeDuration, дальше самообучается по реальной длине
        // анимационного тега "dodge" (CalibrateFromAnimator).
        public float CalibratedDuration;

        private int _samples;
        private float _timer;
        private float _elapsedSinceStart;
        private bool _wasTagActive;
        private Character _character;
        private Player _owner;
        private CompanionStamina _stamina;
        private Rigidbody _bodyRb;
        private Animator _animator;

        public void Init(Character character, Player owner, CompanionStamina stamina)
        {
            _character = character;
            _owner = owner;
            _stamina = stamina;
            _bodyRb = character != null && BodyField != null ? BodyField.GetValue(character) as Rigidbody : null;
            _animator = character != null && AnimatorField != null ? AnimatorField.GetValue(character) as Animator : null;
            CalibratedDuration = Plugin.DodgeDuration.Value;
            if (_dodgeTagHash == int.MinValue)
                _dodgeTagHash = DodgeTagField != null ? (int)DodgeTagField.GetValue(null) : 0;
        }

        // Экип-модификаторы кувырка (GetEquipmentMovementModifier/GetEquipmentDodgeStaminaModifier)
        // здесь НЕ зовём — тот же факт движка, что и у блока (StaminaPolicy.BlockCost): обе
        // проверены по IL (stamina_fix2_movedodge.txt), в Character — `ldc.r4 0; ret`, override
        // только в Player; у Humanoid (тело спутника) override нет, вызов всегда даёт 0 — мёртвый
        // код. Реально кувырок спутника зависит только от навыка (Lerp 1↔0.5), как у игрока ниже.
        private float ComputeCost()
        {
            if (_owner == null || _character == null) return 8f;
            float baseCost = _owner.m_dodgeStaminaUsage;
            // Этап «Навыки-2»: было Plugin.CompanionSkillFactor.Value (фикс. конфиг) — реальный
            // растущий навык Dodge бойца (Character.GetSkillFactor патчен на CompanionSkills,
            // CombatPatches.CompanionSkillFactorPatch), точная копия Player.GetDodgeStaminaUse
            // (IL _research/skills-2_journal.md): m_skills.GetSkillFactor(Dodge). Экип-модификаторы
            // не зовём (см. комментарий выше — мёртвый код у Humanoid).
            float skill = Mathf.Clamp01(_character.GetSkillFactor(Skills.SkillType.Dodge));
            return baseCost * Mathf.Lerp(1f, 0.5f, skill); // навык Dodge снижает стоимость вплоть до х0.5 (как у игрока)
        }

        public bool TryDodge(Vector3 dir)
        {
            if (InDodge || _character == null || _stamina == null) return false;
            if (!_character.IsOnGround() || _character.IsDead() || _character.InAttack()
                || _character.IsEncumbered() || _character.IsStaggering()) return false;
            float cost = ComputeCost();
            if (!_stamina.Have(cost)) { StaminaDenied++; return false; }
            _stamina.Use(cost);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = _character.transform.forward;
            var rot = Quaternion.LookRotation(dir.normalized);
            _character.transform.rotation = rot;
            // Игрок поворачивает И transform, И Rigidbody (m_body.rotation) — без этого физика на
            // следующем FixedUpdate тащит ориентацию назад, кувырок "косит" в сторону (_research/cv2a_1.md п.0.2).
            if (_bodyRb != null) _bodyRb.rotation = rot;
            try
            {
                var zanim = ZAnimField != null ? ZAnimField.GetValue(_character) as ZSyncAnimation : null;
                if (zanim != null) zanim.SetTrigger("dodge");
            }
            catch { }
            InDodge = true;
            Invincible = true;
            DodgeCount++;
            // Как Player.Dodge (skill 108=Dodge, значение 0.1, без доп. множителя — IL
            // _research/skills-2_journal.md) — виртуальный вызов, патчен на CompanionSkills.Raise.
            _character.RaiseSkill(Skills.SkillType.Dodge, 0.1f);
            _timer = CalibratedDuration;
            _elapsedSinceStart = 0f;
            _wasTagActive = false;
            return true;
        }

        public void Tick(float dt)
        {
            if (!InDodge) return;
            _elapsedSinceStart += dt;
            CalibrateFromAnimator();
            if (!InDodge) return; // калибровка могла уже закончить кувырок (тег аниматора вышел раньше таймера)
            _timer -= dt;
            if (_timer <= 0f) { InDodge = false; Invincible = false; }
        }

        // Самообучение реальной длительности анимационного тега "dodge": число секунд не в коде,
        // только побочный эффект Animator'а — надёжно измерить только по факту (как AttackProfiles
        // для windup врага). Заодно: если тег закончился РАНЬШЕ таймера — обрываем неуязвимость
        // сразу, а не ждём конфиг (иначе лишние секунды "стоим неуязвимые без движения").
        private void CalibrateFromAnimator()
        {
            if (_animator == null || NextOrCurrentHashMethod == null || _dodgeTagHash == 0) return;
            bool active;
            try { active = (int)NextOrCurrentHashMethod.Invoke(_character, null) == _dodgeTagHash; }
            catch { return; }
            if (_wasTagActive && !active && _elapsedSinceStart > 0.05f && _elapsedSinceStart < 3f)
            {
                CalibratedDuration = _samples > 0 ? CalibratedDuration * 0.7f + _elapsedSinceStart * 0.3f : _elapsedSinceStart;
                _samples++;
                Plugin.Log.LogInfo("[CompanionDodge] калибровка длительности кувырка: " + CalibratedDuration.ToString("0.000")
                    + "с (n=" + _samples + ", замер " + _elapsedSinceStart.ToString("0.000") + "с)");
                InDodge = false;
                Invincible = false;
            }
            _wasTagActive = active;
        }
    }
}

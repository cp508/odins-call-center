using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Стамина спутника. У Character/Humanoid полей стамины вообще нет (только у Player) —
    // счётчик свой, тик и формула регена — порт Player.UpdateStats(dt) (этап 2б-2), ТОЧНО по
    // IL (stamina_il_1.txt, проверено координатором и этим агентом 25.10):
    //   reg = (m_staminaRegen + (1 - stamina/max) * m_staminaRegen * m_staminaRegenTimeMultiplier) * k
    //   reg *= SEMan.ModifyStaminaRegen(ref 1f)
    //   stamina = Min(max, stamina + reg * dt * Game.m_staminaRegenRate), только если m_staminaRegenTimer<=0
    // k = 0.8 при блоке; 0 во время атаки/кувырка/заплыва-не-на-дне/перегруза (m_wallRunning в
    // игре тоже зануляет k, но у спутника своего "бега по стене" нет — не добавлено, не всплывало).
    // ВАЖНО (запрос Ильи 25.10): чем НИЖЕ стамина, тем БЫСТРЕЕ реген — порога «на половине» нет,
    // формула линейна по (1 - stamina/max). Раньше здесь не было SEMan и Game.m_staminaRegenRate —
    // добавлены для точности (мир может быть с модификатором регена, статус-эффекты — честно,
    // хотя своей системы эффектов у спутника сейчас нет, см. StaminaPolicy.cs).
    public class CompanionStamina
    {
        private static readonly FieldInfo SemanField = AccessTools.Field(typeof(Character), "m_seman");

        public float Current;
        public float Max = 50f;
        private float _regenTimer;
        private Player _owner;   // источник конфига стамины (m_staminaRegen и т.п.) — своего Player у спутника нет
        private Character _self; // СВОЙ Character спутника — SEMan-эффекты считаем на нём, не на хозяине
        private SEMan _seman;    // кэш m_seman СВОЕГО тела — резолвим рефлексией один раз в Init, не каждый Tick

        // Для StaminaPolicy.AttackCost — тот же кэш, чтобы не дублировать реф-резолв SemanField.
        public SEMan Seman { get { return _seman; } }

        // Константы боя для StaminaPolicy (запас на кувырок/блок) — один источник чтения "конфига
        // стамины" владельца, а не разбросанные по файлам owner.m_xxx.
        public float DodgeStaminaUsage { get { return _owner != null ? _owner.m_dodgeStaminaUsage : 8f; } }
        public float BlockStaminaDrain { get { return _owner != null ? _owner.m_blockStaminaDrain : 5f; } }
        public float PerfectBlockStaminaDrain { get { return _owner != null ? _owner.m_perfectBlockStaminaDrain : 2f; } }
        public float RegenDelay { get { return _owner != null ? _owner.m_staminaRegenDelay : 1f; } }

        public void Init(Player owner, Character self)
        {
            _owner = owner;
            _self = self;
            _seman = self != null && SemanField != null ? SemanField.GetValue(self) as SEMan : null;
            Max = owner != null ? owner.m_baseStamina : 50f;
            Current = Max;
        }

        public void SetMax(float max)
        {
            Max = Mathf.Max(1f, max);
            if (Current > Max) Current = Max;
        }

        // Как Player.HaveStamina: строго больше (cgt), не >=.
        public bool Have(float amount)
        {
            return Current > amount;
        }

        public void Use(float v)
        {
            if (v <= 0f) return;
            Current = Mathf.Max(0f, Current - v);
            _regenTimer = _owner != null ? _owner.m_staminaRegenDelay : 1f;
        }

        public void Add(float v)
        {
            Current = Mathf.Clamp(Current + v, 0f, Max);
        }

        // blocking/inAttack/inDodge/encumbered — как в формуле игрока: во время атаки/кувырка/
        // заплыва-не-на-дне/перегруза регена нет, во время блока — х0.8.
        public void Tick(float dt, bool blocking, bool inAttack, bool inDodge, bool swimmingNotGround, bool encumbered)
        {
            if (_owner == null) return;
            float mul = 1f;
            if (blocking) mul *= 0.8f;
            if (swimmingNotGround || inAttack || inDodge || encumbered) mul = 0f;
            float regen = _owner.m_staminaRegen * (1f + _owner.m_staminaRegenTimeMultiplier * (1f - Current / Max)) * mul;

            // SEMan.ModifyStaminaRegen — у спутника своей системы статус-эффектов пока нет
            // (CompanionFood не вешает SE), поэтому почти всегда вернёт множитель 1 без изменений,
            // но компонент m_seman на теле живой (не удалялся при сборке шаблона, CompanionFactory.cs) —
            // считаем честно по формуле игры, а не тихо пропускаем множитель. _seman кэширован в Init
            // (не резолвим рефлексией каждый кадр на каждого спутника, ревью этапа «Фикс»).
            float seMul = 1f;
            if (_seman != null) { try { _seman.ModifyStaminaRegen(ref seMul); } catch { seMul = 1f; } }
            regen *= seMul;

            _regenTimer -= dt;
            if (Current < Max && _regenTimer <= 0f)
                Current = Mathf.Min(Max, Current + regen * dt * Game.m_staminaRegenRate);
        }
    }
}

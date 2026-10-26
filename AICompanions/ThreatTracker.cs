using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Один летящий в спутника удар — снимок на момент последнего сканирования ThreatTracker.
    public class Threat
    {
        public Character Enemy;
        public Humanoid EnemyBody;
        public Attack Atk;
        public ItemDrop.ItemData Weapon;
        public bool Attacking;
        public float TimeToHit = float.MaxValue; // сек до предсказанного удара, 0 = прямо сейчас
        public bool Known;      // профиль тайминга уже откалиброван (иначе — дефолт)
        public bool InZone;     // спутник сейчас геометрически в зоне ЭТОГО удара
        public bool TargetingMe;
        public float Dist;
        public float BearingDeg; // угол (-180..180) от "вперёд" спутника до направления на врага
    }

    // ~10 Гц сканер угроз вокруг ОДНОГО спутника (этап «Бой v2а»): кто рядом враждебен, кто бьёт
    // прямо сейчас и когда/куда придётся удар, насколько спутник окружён. Общий источник данных
    // для CombatGuard (парирование/кувырок на каждый летящий удар, окна безопасной атаки) и для
    // схемы «1 против толпы» (держать врагов в секторе, кувырок из кольца при окружении).
    public class ThreatTracker
    {
        private Humanoid _body;
        private float _timer;
        private readonly List<float> _bearingsBuf = new List<float>();
        private static readonly List<Character> _scanBuf = new List<Character>(64);
        private bool _loggedEncircle;

        public readonly List<Threat> Threats = new List<Threat>();
        public int EnemiesNear;
        public int EnemiesTargetingMe;
        public float FreeArcDeg = 360f;      // самый широкий сектор вокруг спутника без врагов
        public float FreeArcCenterDeg;       // направление (от "вперёд") в середину этого сектора
        public bool Surrounded;
        public float SurroundedFor;
        public float MaxSurroundedSeen;

        public void Init(Humanoid body)
        {
            _body = body;
        }

        public void Tick(float dt)
        {
            _timer -= dt;
            if (_timer > 0f) return;
            float period = 1f / Mathf.Max(1f, Plugin.ThreatTickRate.Value);
            _timer = period;
            Scan(period);
        }

        private void Scan(float period)
        {
            Threats.Clear();
            EnemiesNear = 0;
            EnemiesTargetingMe = 0;
            _bearingsBuf.Clear();
            if (_body == null) { Surrounded = false; SurroundedFor = 0f; return; }

            float radius = Plugin.ThreatRadius.Value;
            _scanBuf.Clear();
            Character.GetCharactersInRange(_body.transform.position, radius, _scanBuf);

            foreach (var c in _scanBuf)
            {
                if (c == null || (Character)c == (Character)_body || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(c, _body)) continue;
                EnemiesNear++;

                Vector3 toEnemy = c.transform.position - _body.transform.position;
                float dist = toEnemy.magnitude;
                toEnemy.y = 0f;
                float bearing = toEnemy.sqrMagnitude > 0.01f
                    ? Vector3.SignedAngle(_body.transform.forward, toEnemy, Vector3.up) : 0f;
                _bearingsBuf.Add(bearing < 0f ? bearing + 360f : bearing);

                var eai = c.GetBaseAI();
                bool targetingMe = eai != null && eai.GetTargetCreature() == (Character)_body;
                if (targetingMe) EnemiesTargetingMe++;

                bool attacking = c.InAttack();
                var enemyBody = c as Humanoid;
                Attack atk = attacking && enemyBody != null ? AttackProfiles.GetCurrentAttack(enemyBody) : null;
                if (atk == null) attacking = false;

                // Старт замаха фиксируется централизованно хуком на Humanoid.StartAttack
                // (AttackProfiles.UniversalAttackStartPatch) — здесь только дублируем вызов на
                // случай, если скан заметил атаку раньше срабатывания хука; дедуп по ссылке на
                // Attack (MarkAttackStart) делает повтор безопасным, не входит в гонку (combatlab_design.md §2).
                if (attacking) AttackProfiles.MarkAttackStart(c, atk);

                float timeToHit = float.MaxValue;
                bool known = false;
                ItemDrop.ItemData weapon = null;
                bool inZone = false;
                if (attacking)
                {
                    float tti;
                    if (AnimTiming.TryTimeToHit(c, out tti))
                    {
                        timeToHit = tti; // точно по событию удара в анимации; MaxValue = откат
                        known = true;
                    }
                    else if (AttackProfiles.IsHitDone(atk))
                    {
                        timeToHit = float.MaxValue; // удар этого замаха уже был — враг в откате
                    }
                    else
                    {
                        float since = Mathf.Max(0f, AttackProfiles.TimeSinceStart(c));
                        float windup;
                        known = AttackProfiles.TryGetWindup(c, out windup);
                        if (!known) windup = AttackProfiles.GetConservativeDefault();
                        timeToHit = Mathf.Max(0f, windup - since);
                    }
                    weapon = atk.GetWeapon();
                    if (weapon == null && enemyBody != null) weapon = enemyBody.GetCurrentWeapon();
                    inZone = AttackProfiles.InDangerZone(c, atk, _body.transform.position);
                }

                Threats.Add(new Threat
                {
                    Enemy = c,
                    EnemyBody = enemyBody,
                    Atk = atk,
                    Weapon = weapon,
                    Attacking = attacking,
                    TimeToHit = timeToHit,
                    Known = known,
                    InZone = inZone,
                    TargetingMe = targetingMe,
                    Dist = dist,
                    BearingDeg = bearing
                });
            }

            Threats.Sort(delegate (Threat a, Threat b) { return a.TimeToHit.CompareTo(b.TimeToHit); });
            UpdateEncirclement(period);
        }

        private void UpdateEncirclement(float period)
        {
            if (_bearingsBuf.Count < 2) { FreeArcDeg = 360f; FreeArcCenterDeg = 0f; Surrounded = false; SurroundedFor = 0f; _loggedEncircle = false; return; }
            _bearingsBuf.Sort();
            float maxGap = -1f;
            float gapStart = 0f;
            for (int i = 0; i < _bearingsBuf.Count; i++)
            {
                float a = _bearingsBuf[i];
                float b = i + 1 < _bearingsBuf.Count ? _bearingsBuf[i + 1] : _bearingsBuf[0] + 360f;
                float gap = b - a;
                if (gap > maxGap) { maxGap = gap; gapStart = a; }
            }
            FreeArcDeg = maxGap;
            float center = gapStart + maxGap / 2f;
            if (center > 180f) center -= 360f;
            FreeArcCenterDeg = center;

            bool nowSurrounded = FreeArcDeg < Plugin.SurroundFreeArc.Value;
            if (nowSurrounded)
            {
                SurroundedFor += period;
                if (SurroundedFor > MaxSurroundedSeen) MaxSurroundedSeen = SurroundedFor;
                if (SurroundedFor > Plugin.EncircleLogSeconds.Value && !_loggedEncircle)
                {
                    _loggedEncircle = true;
                    Plugin.Log.LogInfo("[ThreatTracker] окружён " + SurroundedFor.ToString("0.0")
                        + "с подряд (свободный сектор " + (int)FreeArcDeg + "°, врагов=" + EnemiesNear + ")");
                }
            }
            else
            {
                SurroundedFor = 0f;
                _loggedEncircle = false;
            }
            Surrounded = nowSurrounded;
        }
    }
}

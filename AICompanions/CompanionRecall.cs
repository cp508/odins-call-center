using UnityEngine;

namespace AICompanions
{
    // «Ко мне» в бою (запрос Ильи 25.10, ветка recall): по K боец сразу выходит из боя (не
    // начинает новых атак — CompanionAttackReservePatch.Prefix в CombatPatches.cs блокирует
    // Attack.Start целиком, пока FollowerAI.Recalling), а летящие в него удары гасит кувырком
    // ИМЕННО К ХОЗЯИНУ (не в сторону, как обычный бой — см. CombatGuard.ChooseDodgeDirection).
    //
    // Пока идёт отзыв, CombatGuard.Tick для этого бойца НЕ вызывается (FollowerAI.TickBody
    // переключает) — иначе он бы параллельно решал кувырок «в сторону» на тот же удар. Здесь
    // своя простая версия того же решения (тайминг — та же формула, что в CombatGuard: удар
    // должен попасть в середину окна неуязвимости), но без альтернативы «парировать» — только
    // кувырок к хозяину, а если на него не осталось стамины — развернуться к врагу (не спиной!)
    // и поднять щит на время этого удара, затем снова бежать. Использует ТОТ ЖЕ экземпляр
    // CombatGuard/CompanionDodge, что и обычный бой — состояние блока/стамины общее, без гонки.
    public class CompanionRecall
    {
        private Humanoid _body;
        private CompanionDodge _dodge;
        private CombatGuard _guard;
        private ThreatTracker _threats;
        private Player _owner;
        private string _logName = "?";

        private Attack _handledAtk;  // замах, на который уже приняли решение (кувырок/блок/честный пропуск) — повторно не реагируем
        private float _blockHoldUntil;
        private Character _blockTarget; // кого держим в блоке — довернуть к нему нужно КАЖДЫЙ тик (бег к хозяину иначе разворачивает обратно)

        public int Dodges { get; private set; }
        public int FallbackBlocks { get; private set; } // «нет стамины на кувырок» — поднял щит под удар
        public int MissedHits { get; private set; }      // ни кувырка, ни щита не нашлось — удар дошёл честно
        // Угол последнего/самого большого отклонения кувырка от направления на хозяина — для теста
        // (правило Ильи: кувырок должен уводить К хозяину, угол < 60°). Прицел всегда точно на
        // хозяина, поэтому в норме ~0° — метрика подтверждает это по факту, а не по намерению.
        public float WorstDodgeAngleDeg { get; private set; }

        public void Init(Humanoid body, CompanionDodge dodge, CombatGuard guard, ThreatTracker threats, Player owner, string name)
        {
            _body = body;
            _dodge = dodge;
            _guard = guard;
            _threats = threats;
            _owner = owner;
            _logName = name;
        }

        // FollowerAI.Recall() — сброс состояния перед новым заходом (повторное «Ко мне»
        // посреди уже идущего отзыва не должно унаследовать старый «висящий» блок/кувырок).
        public void OnRecallStart()
        {
            _handledAtk = null;
            _blockHoldUntil = 0f;
            _blockTarget = null;
            Dodges = 0;
            FallbackBlocks = 0;
            MissedHits = 0;
            WorstDodgeAngleDeg = 0f;
        }

        // Вызывается из FollowerAI.TickBody ВМЕСТО Guard.Tick(...), пока FollowerAI.Recalling.
        public void Tick(float dt)
        {
            if (_body == null || _owner == null || _threats == null) return;

            // Угроза, которая реально бьёт именно нас и близко — та же выборка, что CombatGuard
            // использует для "кого сейчас защищаем" (BlockReach/DodgeReach). СКАНИРУЕМ КАЖДЫЙ тик,
            // даже пока держим фоллбэк-блок от ПРЕЖНЕГО удара — иначе второй (следующий) замах той
            // же цели не замечается, пока не истечёт старый _blockHoldUntil, и бьёт без защиты ровно
            // в момент опускания щита (баг из ревью: "блок_фоллбэк=1" в логе, но следом ещё и удар
            // прошёл честно — старая версия просто не смотрела на угрозы во время удержания).
            float reach = Mathf.Max(Plugin.BlockReach.Value, Plugin.DodgeReach.Value);
            Threat primary = null;
            float bestTti = float.MaxValue;
            foreach (var t in _threats.Threats)
            {
                if (t.Enemy == null || !t.Attacking || !t.TargetingMe || t.Dist > reach) continue;
                if (t.TimeToHit < bestTti) { bestTti = t.TimeToHit; primary = t; }
            }

            bool holding = Time.time < _blockHoldUntil;
            bool alreadyHandled = primary != null && primary.Atk != null && ReferenceEquals(primary.Atk, _handledAtk);

            // Ничего нового (не обработанного) — либо держим прежний блок, пока не истёк, либо бежим.
            if (primary == null || alreadyHandled)
            {
                if (holding) { if (_blockTarget != null) _guard.FaceTarget(_blockTarget); _guard.ForceBlock(true, dt); }
                else { _blockTarget = null; _guard.ForceBlock(false, dt); }
                return;
            }

            float tti = primary.TimeToHit;
            float fresh;
            if (AnimTiming.TryTimeToHit(primary.Enemy, out fresh)) tti = fresh;

            // Прицел в СЕРЕДИНУ окна неуязвимости кувырка — та же формула, что в CombatGuard
            // (DodgeSafetyMargin — запас на погрешность прогноза, не смещение к краю окна).
            float lead = Mathf.Max(0.05f, _dodge.CalibratedDuration * 0.5f + Plugin.DodgeSafetyMargin.Value);
            if (tti > lead)
            {
                // Рано для ЭТОГО (нового) замаха — но если всё ещё держим блок от прежнего, не бросаем щит раньше срока.
                if (holding) { if (_blockTarget != null) _guard.FaceTarget(_blockTarget); _guard.ForceBlock(true, dt); }
                else { _blockTarget = null; _guard.ForceBlock(false, dt); }
                return;
            }

            _guard.StopAttack(); // на всякий случай — новых атак и так не бывает (см. CompanionAttackReservePatch)

            Vector3 toOwner = _owner.transform.position - _body.transform.position;
            toOwner.y = 0f;
            if (toOwner.sqrMagnitude < 0.01f) toOwner = _body.transform.forward;
            toOwner.Normalize();

            if (_dodge.TryDodge(toOwner))
            {
                _handledAtk = primary.Atk;
                _blockHoldUntil = 0f; // кувырок важнее недоистёкшего блока от предыдущего замаха
                _blockTarget = null;
                Dodges++;
                // Кувырок разворачивает тело точно на toOwner (CompanionDodge.TryDodge) — угол
                // считаем всё равно по факту направления тела после кувырка, не по намерению.
                float angle = Vector3.Angle(_body.transform.forward, toOwner);
                if (angle > WorstDodgeAngleDeg) WorstDodgeAngleDeg = angle;
                Plugin.Log.LogInfo("[RECALL] " + _logName + " кувырок к хозяину от " + AttackProfiles.PrefabNameOf(primary.Enemy)
                    + " угол=" + angle.ToString("0") + "° до_удара=" + tti.ToString("0.00") + "с");
                return;
            }

            // Нет стамины на кувырок — развернуться К ВРАГУ (не ловить удар спиной) и поднять щит,
            // если он есть; удерживаем блок (с доворотом каждый тик, см. выше) через сам момент
            // удара, потом снова бежим. В любом случае — замах обработан, повторно на него не
            // реагируем (без этого MissedHits/лог сыпались бы каждый кадр до самого удара).
            _handledAtk = primary.Atk;
            if (_guard.HasBlockerNow(dt))
            {
                _blockTarget = primary.Enemy;
                _guard.FaceTarget(_blockTarget);
                _guard.ForceBlock(true, dt);
                _blockHoldUntil = Time.time + Mathf.Max(0.15f, tti) + Plugin.BlockLinger.Value;
                FallbackBlocks++;
                Plugin.Log.LogInfo("[RECALL] " + _logName + " кувырок недоступен (" + DodgeBlockedReason() + ") — щит под удар " + AttackProfiles.PrefabNameOf(primary.Enemy));
            }
            else
            {
                MissedHits++;
                Plugin.Log.LogInfo("[RECALL] " + _logName + " кувырок недоступен (" + DodgeBlockedReason() + "), щита нет — удар " + AttackProfiles.PrefabNameOf(primary.Enemy) + " пройдёт честно");
            }
        }

        // Честная диагностика (а не всегда «нет стамины») — те же условия, что CompanionDodge.
        // TryDodge проверяет сам; найдено по логу: реальная причина отказа часто «в своей атаке»
        // (хвост анимации до ~0.8с, см. CompanionRecallInAttackPatch) или «оглушён», не стамина.
        private string DodgeBlockedReason()
        {
            if (_body.InAttack()) return "в своей атаке";
            if (_body.IsStaggering()) return "оглушён";
            if (!_body.IsOnGround()) return "не на земле";
            if (_body.IsEncumbered()) return "перегруз";
            return "нет стамины";
        }
    }
}

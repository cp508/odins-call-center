using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AICompanions
{
    // Мозг спутника. Ходьба и удары — штатный MonsterAI игры,
    // мы решаем «кого бить»: только врагов рядом с хозяином (этап 2б).
    public class FollowerAI : MonoBehaviour
    {
        // Быстрый поиск «это наш спутник?» для патча BaseAI.IsEnemy.
        public static readonly Dictionary<Character, FollowerAI> Registry = new Dictionary<Character, FollowerAI>();
        public static readonly Dictionary<BaseAI, FollowerAI> ByAI = new Dictionary<BaseAI, FollowerAI>();
        // Живые спутники — без FindObjectsOfType (он перебирает всю сцену и давал лаги).
        public static readonly List<FollowerAI> All = new List<FollowerAI>();

        public enum Stance { Defend, Passive }
        private static readonly FieldInfo TargetField =
            typeof(MonsterAI).GetField("m_targetCreature", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo CanSense =
            typeof(BaseAI).GetMethod("CanSenseTarget", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Character) }, null);
        // Счётчик для роли классов (CompanionRole по кругу Tank->Universal->Archer->Fighter) — сбрасывается
        // в 0 в CompanionManager.DismissAll, поэтому первый боец после чистого отзыва всегда Танк
        // (= старый общий набор по умолчанию, одиночные бой-тесты прошлых этапов не меняют поведение).
        private static int _classCounter;
        public static void ResetClassRotation() { _classCounter = 0; }

        public string Name = "?";
        public CompanionRole Role = CompanionRole.Universal;
        public int ColorIndex;
        private bool _manualName; // игрок сам переименовал в меню Ё — автоимя (роль/цвет) больше не перезаписывает
        public bool IsSpawned;
        public string Status = "";
        public string LastHitText; // последний удар: кто, сколько, HP/броня (для уведомления о смерти)
        // Удар «в спину»: игра умножает урон по НЕ-игроку, у которого ИИ «не насторожен» (BaseAI.IsAlerted),
        // раз в 300с (m_backstabTime). Спутник считался ненасторожённым — парированный удар скелета 25
        // превращался в ~99 и убивал с полного HP. Держим m_backstabTime свежим — бонус не срабатывает.
        private static readonly FieldInfo BackstabTimeField =
            typeof(Character).GetField("m_backstabTime", BindingFlags.NonPublic | BindingFlags.Instance);
        public Stance Mode = Stance.Defend;
        private float _recallUntil;
        private ItemDrop _loot;
        private float _lootSearchTimer;
        private float _lootGiveUp;

        // Этап 2б-2: тело и бой как у игрока.
        public readonly CompanionStamina Stamina = new CompanionStamina();
        public readonly CompanionFood Food = new CompanionFood();
        public readonly CompanionDodge Dodge = new CompanionDodge();
        public readonly CombatGuard Guard = new CombatGuard();
        // Этап «Бой v2а»: сенсор угроз (все враги рядом, не только текущая цель MonsterAI).
        public readonly ThreatTracker Threats = new ThreatTracker();
        // «Ко мне» в бою (ветка recall): кувырок К ХОЗЯИНУ вместо обычного боя, пока Recalling.
        public readonly CompanionRecall RecallLogic = new CompanionRecall();
        // Удары, дошедшие ВО ВРЕМЯ отзыва (цель 0 — правило Ильи «не ловить удар в спину при Ко мне»).
        public int RecallHitsTaken;
        public float RecallDamageTaken;
        // Этап «Классы», часть 2: лучшее оружие/стрелы из набора класса по резисту целей рядом.
        public readonly WeaponSwitch Weapons = new WeaponSwitch();
        public int Blocks, Parries;
        public int ReserveViolations;
        // Метрики боя (спека этапа): безопасно = урон удара <= допустимого на момент решения.
        public int SafeHits, UnsafeHits;
        public float DamageTaken;
        public float DamageDealt; // реальная потеря HP у врага от ударов спутника (CompanionDamageLedgerPatch)
        public int HitsDealt; // число реальных попаданий с DamageDealt>0 (CompanionDamageLedgerPatch) — для «урон за удар»
        // Использовано безопасных окон (CombatGuard.WindowsAvailable — сколько открылось) — своя
        // атака СТАРТОВАЛА, пока окно было открыто (PriorityAttackWindow). Метрика «окна: доступно/
        // использовано» правила Ильи «бить в каждое безопасное окно».
        public int WindowsUsed;
        // Правило Ильи «ни одного оглушения спутника» — счётчик фактов IsStaggering() (rising edge).
        public int Staggered;
        // safeAttack% (доработка «значительно круче»): доля собственных окон атаки (Start ->
        // InAttack()==false), за время которых спутник НЕ получил пропущенный удар — проверка
        // правила «атака только в безопасное окно» по факту, не только по решению на старте.
        public int OwnAttackWindows, OwnAttackPunished;
        private bool _ownAttackPunished;
        private bool _waitingWindowFlag;
        private bool _wasAttacking;
        private bool _wasStaggering;
        private float _ownAttackStart;
        private string _ownAttackKey;
        private float _eatTimer;
        private float _eatMessageUntil;

        // «Ко мне»: бросить цель и бежать к хозяину (не телепорт). Снимается, когда добежал.
        public bool Recalling { get { return Time.time < _recallUntil; } }
        private Player _owner;
        private Humanoid _body;
        private MonsterAI _ai;
        private Rigidbody _rb;
        private bool _setupDone;
        private float _weaponFixDeadline; // >0 пока ждём повторной попытки доэкипировать набор класса (см. SetupOnce)
        // Навыки движения (этап «Навыки-2»): у Humanoid нет Player.CheckRun/OnSwimming (там и только
        // там навык Run/Swim поднимается, IL _research/skills-2_journal.md) — копия таймеров-триггеров
        // игрока (раз в 1с активности, см. TickBody) поверх штатного Character.RaiseSkill (патчен,
        // CombatPatches.CompanionRaiseSkillPatch).
        private float _runSkillTimer;
        private float _swimSkillTimer;
        private float _followTimer;
        private float _thinkTimer;
        private float _scanTimer;
        private float _stuckTimer;
        private Vector3 _lastPos;

        public Humanoid Body { get { return _body; } }
        // Для CompanionDeath: переставить цель следования на надгробие/на хозяина при возрождении.
        public MonsterAI Ai { get { return _ai; } }
        // Возрождение после смерти (CompanionDeath) — null = обычный боец, иначе голый, бежит/одевается у надгробия.
        public CompanionDeath.RevivalState Revival;

        public void Init(Player owner, int colorIndex)
        {
            _owner = owner;
            _body = GetComponent<Humanoid>();
            _ai = GetComponent<MonsterAI>();
            _rb = GetComponent<Rigidbody>();
            IsSpawned = true;
            if (_body != null) Registry[_body] = this;
            if (_ai != null) ByAI[_ai] = this;
            if (!All.Contains(this)) All.Add(this);
            Mode = Plugin.DefendByDefault.Value ? Stance.Defend : Stance.Passive;
            ColorIndex = colorIndex;
            // Класс — по кругу Tank->Universal->Archer->Fighter (счётчик сброшен в DismissAll,
            // см. _classCounter) — набор вещей выдаст SetupOnce (ClassKits), а не наоборот.
            Role = (CompanionRole)(_classCounter % 4);
            _classCounter++;
            ApplyAutoName();
            CopyLook(owner);
            if (_ai != null) _ai.SetFollowTarget(owner.gameObject);
            _lastPos = transform.position;
        }

        private void OnDestroy()
        {
            if (_body != null) Registry.Remove(_body);
            if (_ai != null) ByAI.Remove(_ai);
            All.Remove(this);
            if (_loot != null) CompanionInventory.Claimed.Remove(_loot);
        }

        public void Recall()
        {
            _recallUntil = Time.time + Plugin.RecallTimeout.Value;
            Guard.StopAttack(); // сразу выйти из боя — не достраивать текущий удар/комбо (правило Ильи)
            RecallLogic.OnRecallStart();
            DropTarget();
        }

        public void SetStance(Stance mode)
        {
            Mode = mode;
            if (mode == Stance.Passive) DropTarget();
        }

        private void DropTarget()
        {
            if (_ai != null && TargetField != null) TargetField.SetValue(_ai, null);
        }

        // Ручное переименование из меню Ё — дальше смена роли имя не трогает.
        // Отклоняем: пустое имя, '|'/'=' (ломают формат сейва CompanionSkills.cs "Имя|Тип=уровень")
        // и имя, занятое ДРУГИМ живым спутником (CompanionSkills хранит навыки по имени —
        // одинаковое имя у двоих значило бы общую запись навыков на обоих, требование Ильи
        // «у каждого спутника свои уровни» иначе не выполняется).
        private string _lastRejectedName; // анти-спам: Plugin.cs зовёт SetName на каждый OnGUI
                                           // (Layout+Repaint за кадр), пока поле ввода в фокусе
                                           // держит тот же отклонённый текст — без дедупликации
                                           // Message()/Alive() летели бы десятки раз в секунду
        public void SetName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Trim() == "") return;
            if (name == _lastRejectedName) return; // тот же отказ, что и в прошлый кадр — молча
            if (name.IndexOf('|') >= 0 || name.IndexOf('=') >= 0)
            {
                _lastRejectedName = name;
                var p = Player.m_localPlayer;
                if (p != null) p.Message(MessageHud.MessageType.TopLeft, "Имя не может содержать | или =");
                return;
            }
            foreach (var other in CompanionManager.Alive())
            {
                if (other != this && other.Name == name)
                {
                    _lastRejectedName = name;
                    var p = Player.m_localPlayer;
                    if (p != null) p.Message(MessageHud.MessageType.TopLeft, "Это имя уже занято: " + name);
                    return;
                }
            }
            _lastRejectedName = null;
            _manualName = true;
            Name = name;
            if (_body != null) _body.m_name = name;
        }

        private void ApplyAutoName()
        {
            Name = CompanionIdentity.BuildName(Role, ColorIndex);
            if (_body != null) _body.m_name = Name;
        }

        // Кнопка в меню Ё — циклическая смена класса: имя пересчитывается (если не переименован
        // руками), набор вещей меняется на набор нового класса (старый набор снимается,
        // ClassKits.Apply) И набор еды меняется на набор нового класса (CompanionFood.SwitchKit) —
        // иначе боец после смены класса в меню продолжал бы носить и есть еду старого класса.
        public void CycleRole()
        {
            // Голый после возрождения (Revival != null, CompanionDeath) — своё забирает из
            // надгробия (CompanionDeath.TickRevival), менять класс сейчас нельзя: ApplyClassKit/
            // Food.SwitchKit тут же экипируют оружие/еду НОВОГО класса через inv.AddItem/
            // CompanionEquip.TryEquip, которые про Revival не знают — боец на глазах перестанет
            // быть голым, хотя по правилу «в бой — только полностью одетым» должен только бежать
            // к могиле. Смена откладывается до конца возрождения (как и WantsToFight, см. ниже).
            if (Revival != null)
            {
                Plugin.Log.LogInfo(Name + ": смена класса отложена — боец возрождается голым");
                return;
            }
            var previousKit = ClassKits.For(Role);
            Role = CompanionIdentity.NextRole(Role);
            if (!_manualName) ApplyAutoName();
            if (_setupDone && _body != null)
            {
                ApplyClassKit(previousKit);
                Food.SwitchKit(ClassKits.For(Role).FoodKitIndex, previousKit.FoodKitIndex);
            }
        }

        public Color GetColor()
        {
            return CompanionIdentity.Colors[ColorIndex].Value;
        }

        public string RoleLabel()
        {
            return CompanionIdentity.RoleName(Role);
        }

        // Тело/лицо/волосы — как у хозяина; вещи у спутника свои.
        private void CopyLook(Player owner)
        {
            var vis = GetComponent<VisEquipment>();
            if (vis != null)
            {
                object model = CompanionFactory.GetField(owner, "m_modelIndex");
                object skin = CompanionFactory.GetField(owner, "m_skinColor");
                if (model is int) vis.SetModel((int)model);
                if (skin is Vector3) vis.SetSkinColor((Vector3)skin);
                vis.SetHairColor(owner.GetHairColor());
            }
            if (_body == null) return;
            _body.SetHair(owner.GetHair());
            _body.SetBeard(owner.GetBeard());
        }

        // После Humanoid.Start (он выдаёт стартовые вещи): набор класса, еда, стамина.
        // Здоровье больше не фиксированное (Combat.Health убран) — считается из еды, как у игрока.
        private void SetupOnce()
        {
            _setupDone = true;
            if (_body == null) return;

            // Возрождение голым (CompanionDeath): набор класса и еду не даём — всё своё боец
            // забирает из надгробия и надевает сам (CompanionDeath.TickRevival). Ванильный
            // Humanoid.Start (уже отработал к этому моменту) сам выдаёт дефолтные предметы
            // независимо от Revival — без явной чистки боец возрождался не с пустыми руками
            // (найдено тестом respawn_naked: вещей=2 вместо 0).
            if (Revival != null)
            {
                // UnequipAllItems ПЕРЕД RemoveAll — иначе дефолтный факел остаётся "в руке"
                // (m_rightItem/m_leftItem ссылаются на уже удалённый ItemData), это и давало
                // зависший InAttack/битое EquipItem при дальнейшем одевании у надгробия (та же
                // причина, по которой CreateTombstone тоже сначала снимает всё, потом чистит).
                _body.UnequipAllItems();
                _body.GetInventory().RemoveAll();
                Stamina.Init(_owner, _body);
                Food.Init(_body, _owner, Stamina);
                Dodge.Init(_body, _owner, Stamina);
                Guard.Init(_body, Dodge, Name);
                Threats.Init(_body);
                // После смерти это НОВЫЙ FollowerAI (CompanionDeath.cs) с полем Weapons по
                // умолчанию (_body=null) — без Init WeaponSwitch.Tick молча выходит навсегда
                // (WeaponSwitch.cs, if (_body == null) return;), переключение оружия/стрел по
                // резисту отключается без ошибки. Сам набор класса не трогаем — вещи боец
                // надевает из надгробия (CompanionDeath.TickRevival), это только восстанавливает
                // ссылки для уже надетых предметов того же ClassKits.For(Role).
                Weapons.Init(_body, Threats);
                _body.SetHealth(_body.GetMaxHealth());
                Plugin.Log.LogInfo(Name + ": возродился голым, бежит к своему надгробию");
                return;
            }

            var given = ApplyClassKit(null);

            Stamina.Init(_owner, _body);
            Food.Init(_body, _owner, Stamina);
            Dodge.Init(_body, _owner, Stamina);
            Guard.Init(_body, Dodge, Name);
            Threats.Init(_body);
            RecallLogic.Init(_body, Dodge, Guard, Threats, _owner, Name);
            Weapons.Init(_body, Threats);
            Food.GiveStartKit(ClassKits.For(Role).FoodKitIndex);
            // Сразу «доесть» стартовый набор — спутник не должен голодать первые секунды после призыва.
            for (int i = 0; i < 3; i++) Food.AutoEat();
            Food.Tick(0f);
            _body.SetHealth(_body.GetMaxHealth());

            Plugin.Log.LogInfo(Name + " [" + RoleLabel() + "]: набор " + string.Join("+", given.ToArray())
                + ", HP " + (int)_body.GetMaxHealth() + ", стамина " + (int)Stamina.Max);
        }

        // Выдать набор текущего класса (ClassKits.Apply — через inv.AddItem, не GiveDefaultItem, см.
        // ClassKits) и экипировать активную часть (оружие[0]/щит/стрелы[0]). previousKit != null —
        // смена класса в меню Ё, сначала снимается именно его набор. Экипировка может штатно
        // отказать (Character.InAttack()/InDodge() в этот кадр, например на кадре спавна, когда
        // Humanoid.Start только выдал дефолтный факел) — тогда TickBody повторяет до 3с.
        private List<string> ApplyClassKit(ClassKits.Kit previousKit)
        {
            var given = ClassKits.Apply(_body, Role, previousKit);
            if (!TryEquipClassActive()) _weaponFixDeadline = Time.time + 3f;
            else _weaponFixDeadline = 0f;
            return given;
        }

        private bool TryEquipClassActive()
        {
            return ClassKits.EquipInitial(_body, ClassKits.For(Role));
        }

        // Еда/стамина/блок/кувырок — каждый кадр, как тело игрока (этап 2б-2).
        private void TickBody(float dt)
        {
            // Повтор доэкипировки набора класса (см. ApplyClassKit) — первая попытка в SetupOnce/
            // CycleRole могла молча отказать (Humanoid.EquipItem возвращает false при InAttack()/
            // InDodge() в этот кадр), тогда спутник остаётся драться дефолтным факелом. Пробуем,
            // пока не получится или не истечёт окно (не висеть вечно, если оружие и правда не дали).
            if (_weaponFixDeadline > 0f)
            {
                if (!_body.InAttack() && !_body.InDodge() && TryEquipClassActive()) _weaponFixDeadline = 0f;
                else if (Time.time > _weaponFixDeadline) { _weaponFixDeadline = 0f; Plugin.Log.LogWarning(Name + ": не удалось доэкипировать набор класса за 3с"); }
            }
            _waitingWindowFlag = false; // OnWaitingWindow() выставит снова этим же кадром, если атака отложена
            bool inAttack = _body.InAttack();
            bool blocking = _body.IsBlocking();
            bool swimmingNotGround = _body.IsSwimming() && !_body.IsOnGround();
            bool encumbered = _body.IsEncumbered();

            Stamina.Tick(dt, blocking, inAttack, Dodge.InDodge, swimmingNotGround, encumbered);
            Food.Tick(dt);
            Dodge.Tick(dt);
            Threats.Tick(dt);
            TickMovementSkills(dt);
            // «Ко мне» в бою: своя логика (кувырок К ХОЗЯИНУ) вместо обычного решения; смена оружия — только в обычном бою
            if (Recalling) RecallLogic.Tick(dt);
            else
            {
                Guard.Tick(Threats, dt);
                Weapons.Tick(dt, Role);
            }

            // Независимая от патча проверка: атака реально началась, а стамина уже была ниже
            // порога StaminaPolicy.CanStartAttack (Reserve — 1 кувырок [+блок] — плюс стоимость
            // ВСЕЙ серии ударов, StaminaSeriesHits, не одного удара) — та же формула, что гейтит
            // старт атаки в CompanionAttackReservePatch, проверена здесь ЕЩЁ раз от патча.
            if (inAttack && !_wasAttacking)
            {
                float need = StaminaPolicy.Reserve(this) + StaminaPolicy.AttackCost(this) * Mathf.Max(1f, Plugin.StaminaSeriesHits.Value);
                if (Stamina.Current < need - 0.05f) ReserveViolations++;
                _ownAttackPunished = false;
                _ownAttackStart = Time.time;
                _ownAttackKey = AttackProfiles.KeyFor(_body);
                if (Guard.PriorityAttackWindow) WindowsUsed++;
            }
            if (!inAttack && _wasAttacking)
            {
                // Калибровка ПОЛНОЙ занятости своей атаки (Start -> InAttack()==false), не только
                // windup до удара — CompanionAttackReservePatch.EstimateOwnAttackDuration читает
                // именно это (combatlab_design.md §1).
                AttackProfiles.RecordBusyDuration(_ownAttackKey, Time.time - _ownAttackStart);
                OwnAttackWindows++;
                if (_ownAttackPunished) OwnAttackPunished++;
            }
            _wasAttacking = inAttack;

            bool staggeringNow = _body.IsStaggering();
            if (staggeringNow && !_wasStaggering)
            {
                Staggered++;
                Plugin.Log.LogInfo("[CombatGuard] " + Name + " ОШЕЛОМЛЁН (нарушение правила — должно быть 0)");
            }
            _wasStaggering = staggeringNow;

            _eatTimer -= dt;
            if (_eatTimer <= 0f)
            {
                _eatTimer = Mathf.Max(0.1f, Plugin.EatCheckInterval.Value);
                if (Food.AutoEat())
                {
                    _eatMessageUntil = Time.time + 2f;
                    Plugin.Log.LogInfo(Name + " ест: " + Food.LastAte);
                }
            }

            if (Revival != null) CompanionDeath.TickRevival(this, dt);
        }

        // Run/Swim — у игрока растут в Player.CheckRun/OnSwimming (IL _research/skills-2_journal.md):
        // раз в 1с активности RaiseSkill(тип, 1f) без доп. множителя. Humanoid.CheckRun вызывает БАЗОВЫЙ
        // Character.CheckRun (без подъёма навыка) — IsRunning()/m_running — это именно его результат
        // (Character.UpdateWalking: m_running = this.CheckRun(...)), то же условие, что у игрока.
        // OnSwimming у Humanoid вообще не переопределён (виртуальный вызов идёт в пустую базу) —
        // IsSwimming()+скорость>0.1 — тот же гейт (targetVel.magnitude>0.1), что у Player.OnSwimming.
        private void TickMovementSkills(float dt)
        {
            if (_body.IsRunning())
            {
                _runSkillTimer += dt;
                if (_runSkillTimer > 1f) { _runSkillTimer = 0f; _body.RaiseSkill(Skills.SkillType.Run, 1f); }
            }
            if (_body.IsSwimming() && _body.GetVelocity().magnitude > 0.1f)
            {
                _swimSkillTimer += dt;
                if (_swimSkillTimer > 1f) { _swimSkillTimer = 0f; _body.RaiseSkill(Skills.SkillType.Swim, 1f); }
            }
        }

        // Бить только врагов рядом с хозяином; зверей (кабаны/олени) не трогать.
        public bool WantsToFight(Character other)
        {
            if (other == null || _owner == null || other.IsDead()) return false;
            if (Revival != null) return false; // голый после возрождения — не атакует (CompanionDeath)
            if (Mode == Stance.Passive || Recalling) return false;
            if (other.GetFaction() == Character.Faction.AnimalsVeg) return false;
            float r = Plugin.DefendRadius.Value;
            return Vector3.Distance(other.transform.position, _owner.transform.position) <= r
                && Vector3.Distance(transform.position, _owner.transform.position) <= r + 10f;
        }

        // Сбор лута: без боя подходит к выпавшему рядом и поднимает. Ведёт штатный ИИ (следовать за предметом).
        private void UpdateLoot()
        {
            if ((object)_loot != null)
            {
                if (_loot == null || !_loot.gameObject.activeInHierarchy || Time.time > _lootGiveUp) { ReleaseLoot(); return; }
                if (Vector3.Distance(transform.position, _loot.transform.position) < 3.2f)
                {
                    if (CompanionInventory.CanTake(_body, _loot)) _loot.Pickup(_body);
                    ReleaseLoot();
                }
                return;
            }
            _lootSearchTimer -= 0.25f;
            if (_lootSearchTimer > 0f) return;
            _lootSearchTimer = 1f;
            var found = CompanionInventory.FindLoot(_body, transform.position, _owner.transform.position);
            if (found == null) return;
            _loot = found;
            CompanionInventory.Claimed.Add(found);
            _lootGiveUp = Time.time + 15f;
            if (_ai != null) _ai.SetFollowTarget(found.gameObject);
        }

        private void ReleaseLoot()
        {
            if ((object)_loot == null) return;
            CompanionInventory.Claimed.Remove(_loot);
            _loot = null;
            if (_ai != null && _owner != null) _ai.SetFollowTarget(_owner.gameObject);
        }

        public int GiveAllToOwner()
        {
            return _owner != null && _body != null ? CompanionInventory.GiveAllTo(_body, _owner) : 0;
        }

        // Безопасные удары = реально дошедшие и оказавшиеся <= допустимого, ПЛЮС успешные кувырки
        // (удар вообще не дошёл до HP — неуязвимость). Общий знаменатель для метрик автотестов.
        public void GetSafetyStats(out int safe, out int total)
        {
            safe = SafeHits + Guard.DodgeSuccesses;
            total = safe + UnsafeHits;
        }

        // CompanionAttackReservePatch.IsSafeToAttack зовёт это, когда откладывает атаку — статус "жду окна".
        public void OnWaitingWindow()
        {
            _waitingWindowFlag = true;
        }

        // CompanionDamageMetricsPatch: реальный удар дошёл до HP. Допустимо/решение — из CombatGuard
        // (предсказание на момент начала ЭТОГО замаха), безопасно = урон <= допустимого (спека этапа).
        public void OnIncomingDamage(Character attacker, float dmg)
        {
            DamageTaken += dmg;
            // «Удар в спину» во время отзыва — именно НЕОТРАЖЁННЫЙ удар (кувырком вообще не
            // доходит до Damage — неуязвимость; через фоллбэк-блок проходит частично смягчённым,
            // это ожидаемая работа защиты, не брак). Считаем только то, что застало без защиты.
            if (Recalling && !_body.IsBlocking()) { RecallHitsTaken++; RecallDamageTaken += dmg; }
            float allowed; string label;
            bool known = Guard.TryGetPending(attacker, out allowed, out label);
            if (!known) allowed = Guard.CurrentAllowedFallback();
            bool safe = dmg <= allowed + 0.01f;
            if (safe) SafeHits++; else UnsafeHits++;
            if (!safe && _body != null && _body.InAttack()) _ownAttackPunished = true;
            Plugin.Log.LogInfo("[Combat] " + Name + " получил удар от " + AttackProfiles.PrefabNameOf(attacker)
                + " урон=" + dmg.ToString("0.0") + " допустимо=" + allowed.ToString("0.0")
                + " решение=" + (known ? label : "?") + " безопасно=" + safe);
        }

        // safeAttack% — доля окон своей атаки, за время которых спутник НЕ получил пропущенный
        // удар (проверка правила «своя атака только в безопасное окно» по факту).
        public float GetSafeAttackPct()
        {
            return OwnAttackWindows > 0 ? (float)(OwnAttackWindows - OwnAttackPunished) / OwnAttackWindows * 100f : 100f;
        }

        // Для подробной плашки (клавиша I): русская подпись стойки — тот же текст, что и на кнопке в меню Ё.
        public string StanceLabel()
        {
            return Mode == Stance.Defend ? "Защищает" : "Пассив";
        }

        // Для подробной плашки: имя текущей цели боя или null, если цели нет.
        public string CurrentTargetName()
        {
            var t = _ai != null ? _ai.GetTargetCreature() : null;
            return t == null ? null : Localization.instance.Localize(t.m_name);
        }

        // Диагностика: как враг, с которым бьёмся, видит спутника.
        private string EnemyView()
        {
            var target = _ai != null ? _ai.GetTargetCreature() : null;
            var eai = target != null ? target.GetBaseAI() : null;
            if (eai == null) return "";
            var t = eai.GetTargetCreature();
            bool sense = false;
            try { sense = CanSense != null && (bool)CanSense.Invoke(eai, new object[] { _body }); } catch { }
            return " | враг целится в: " + (t == null ? "никого" : t == _body ? "МЕНЯ" : t.m_name)
                + ", считает врагом: " + BaseAI.IsEnemy(target, _body) + ", видит: " + sense;
        }

        public void PlaceNear(Player player, float dist)
        {
            Vector3 p = player.transform.position - player.transform.forward * dist + Vector3.up * 0.5f;
            transform.position = p;
            if (_rb != null) { _rb.position = p; _rb.velocity = Vector3.zero; }
            _stuckTimer = 0f;
            _lastPos = p;
        }

        private void Update()
        {
            if (!IsSpawned) return;
            if (_owner == null) _owner = Player.m_localPlayer;
            if (_owner == null || _body == null) return;
            if (!_setupDone) SetupOnce();
            if (BackstabTimeField != null) BackstabTimeField.SetValue(_body, Time.time);

            TickBody(Time.deltaTime);

            float d = Vector3.Distance(transform.position, _owner.transform.position);

            _thinkTimer -= Time.deltaTime;
            if (_thinkTimer <= 0f)
            {
                _thinkTimer = 0.25f;
                var target = _ai != null ? _ai.GetTargetCreature() : null;
                if (target != null && !WantsToFight(target))
                {
                    DropTarget();
                    target = null;
                }
                if (Recalling && d < Plugin.RecallArriveRadius.Value) _recallUntil = 0f;
                if (target == null && !Recalling && Revival == null) UpdateLoot();
                else ReleaseLoot();
                if (Revival == null)
                {
                    string mode = Mode == Stance.Passive ? "[пассив] " : "";
                    string hurt = Guard.IsInjured ? " [ранен]" : "";
                    if (Dodge.InDodge) Status = "кувырок!" + hurt;
                    else if (_body.IsBlocking()) Status = (Guard.WantsBlock ? "парирую" : "блок!") + hurt;
                    else if (_waitingWindowFlag) Status = "жду окна" + hurt;
                    else if (Time.time < _eatMessageUntil) Status = "ест: " + Food.LastAte;
                    else if (Recalling) Status = "бегу к тебе!";
                    else if (_loot != null) Status = "собираю: " + Localization.instance.Localize(_loot.m_itemData.m_shared.m_name);
                    else if (target != null)
                        Status = "бой: " + Localization.instance.Localize(target.m_name) + " " + (int)Vector3.Distance(transform.position, target.transform.position) + "м" + hurt;
                    else Status = mode + (d > 6f ? "иду к тебе" : "рядом") + hurt;
                }
                // Revival != null — статус уже выставлен CompanionDeath.TickRevival (вызывается из TickBody)
            }

            // Цель «следовать» переутверждаем раз в 2 сек (ИИ мог сбросить после драки).
            _followTimer -= Time.deltaTime;
            if (_followTimer <= 0f)
            {
                _followTimer = 2f;
                if (_ai != null && _loot == null && Revival == null && _ai.GetFollowTarget() != _owner.gameObject)
                    _ai.SetFollowTarget(_owner.gameObject);
            }

            // Отбаговка: только если реально застрял (8 сек без движения и хозяин дальше 15м).
            if (Vector3.Distance(transform.position, _lastPos) < 0.5f) _stuckTimer += Time.deltaTime;
            else { _stuckTimer = 0f; _lastPos = transform.position; }
            if (_stuckTimer > 8f && d > 15f)
            {
                Plugin.Log.LogInfo(Name + " застрял (" + (int)d + "м) — подтянул к хозяину");
                PlaceNear(_owner, 3f);
            }

            if (Plugin.DebugSensor.Value)
            {
                _scanTimer -= Time.deltaTime;
                if (_scanTimer <= 0f)
                {
                    _scanTimer = 5f;
                    int safeTotal = SafeHits + Guard.DodgeSuccesses;
                    int totalHits = safeTotal + UnsafeHits;
                    string safePct = totalHits > 0 ? (safeTotal * 100 / totalHits) + "%" : "н/д";
                    Plugin.Log.LogInfo(Name + " | HP " + (int)_body.GetHealth() + "/" + (int)_body.GetMaxHealth()
                        + " | стамина " + (int)Stamina.Current + "/" + (int)Stamina.Max
                        + " | желудок " + Food.Slots.Count + "/3 | блоки " + Blocks + " парирования " + Parries
                        + " кувырки " + Dodge.DodgeCount + " нарушений " + ReserveViolations + " кувырок_не_хватило_стамины " + Dodge.StaminaDenied
                        + " | безопасно " + safePct + " (" + SafeHits + "/" + totalHits + ") урон_всего=" + DamageTaken.ToString("0")
                        + " | враги_рядом=" + Threats.EnemiesNear + " окружён=" + Threats.Surrounded
                        + " | " + Status + " | до хозяина " + (int)d + "м" + EnemyView());
                }
            }
        }
    }
}

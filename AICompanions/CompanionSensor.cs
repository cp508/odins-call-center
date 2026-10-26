using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Что нашёл сенсор: деревья/руда — на сколько рубить/копать, собираемое — готово ли,
    // лут — сколько в стаке, моб — уровень/HP/опасность, станция — тип (верстак/сундук/костёр/кухня).
    public enum SensorCategory { Tree, Ore, Pickable, Loot, Mob, Station }

    // Один найденный объект — переиспользуемый (Reset переписывает поля, не создаём новый на каждый скан).
    public class SensorHit
    {
        public SensorCategory Category;
        public Component Comp;   // исходный компонент (TreeBase/MineRock5/Pickable/ItemDrop/CraftingStation...)
        public Character Char;   // заполнено только у категории Mob
        public string Name;
        public float Distance;
        public int MinToolTier = -1; // Tree/Ore: нужный тир инструмента, иначе -1
        public bool PickableReady;   // Pickable: можно собрать прямо сейчас
        public int Stack;            // Loot: сколько штук в стаке
        public bool AutoPickup;      // Loot: m_autoPickup (брошенное игроком спутники не берут)
        public string StationType;   // Station: "Верстак"/"Сундук"/"Костёр"/"Кухня"
        public int Level;            // Mob
        public float Hp, MaxHp;      // Mob
        public bool Hostile;         // Mob: относительно того, кого передали в Scan(relativeTo)
        public float Danger;         // Mob: грубая эвристика силы врага / силы relativeTo (без relativeTo — абсолютная шкала/100)

        public void Reset()
        {
            Category = SensorCategory.Tree;
            Comp = null; Char = null; Name = null; Distance = 0f; MinToolTier = -1;
            PickableReady = false; Stack = 0; AutoPickup = false; StationType = null;
            Level = 0; Hp = 0f; MaxHp = 0f; Hostile = false; Danger = 0f;
        }
    }

    // Сенсор мира: «что рядом с точкой в радиусе R» по категориям (_research/ahead_sensor_scout.md).
    // Общий сервис — отдельный экземпляр на потребителя (дебаг-оверлей вокруг игрока, позже —
    // свой у каждого спутника для профессий/разведки). Физика — Physics.OverlapSphereNonAlloc по
    // слоям + Character.GetCharactersInRange (обе функции пишут в переданный буфер, без аллокаций);
    // результатные списки и пул SensorHit — свои на экземпляр, сканы по таймеру/по запросу, а не
    // каждый кадр. НЕ FindObjectsOfType — он перебирает всю сцену (причина лагов 0.4.0, см. задачу).
    public class CompanionSensor
    {
        // Подтверждено по IL (_research/ahead_sensor_scout.md §3.1): этими слоями ZoneSystem проверяет
        // столкновения со статичным миром (деревья/руда/кусты/постройки). Если в игре окажется, что
        // Pickable (ягоды/грибы) не находятся — у них другой слой: раскомментировать лог ниже,
        // посмотреть LayerMask.LayerToName(col.gameObject.layer) на непойманном объекте и добавить слой сюда.
        private static int _worldMask = -1;
        private static int _itemMask = -1; // подтверждено: MonsterAI.m_itemMask = LayerMask.GetMask("item")

        private static void EnsureMasks()
        {
            if (_worldMask != -1) return;
            _worldMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece");
            _itemMask = LayerMask.GetMask("item");
        }

        // Сколько раз в секунду пересканировать и радиус по умолчанию — свой конфиг модуля,
        // Config.Bind — в Plugin.Awake (правило проекта), здесь только публичные поля.
        public static BepInEx.Configuration.ConfigEntry<float> TickRate;
        public static BepInEx.Configuration.ConfigEntry<float> Radius;
        public static BepInEx.Configuration.ConfigEntry<bool> DebugLog;

        // Общие статические буферы — скан всегда синхронный на главном потоке Unity, два скана
        // параллельно не идут, поэтому шарить буфер между экземплярами безопасно и дешевле.
        // 256 — с запасом: маска включает слой "piece" (все постройки), у базы в радиусе 25м
        // коллайдеров легко больше 128.
        private static readonly Collider[] _overlapBuf = new Collider[256];
        private static readonly List<Character> _charBuf = new List<Character>(64);
        private static readonly HashSet<Component> _seen = new HashSet<Component>();
        private static readonly MethodInfo _getPrefabNameMethod = AccessTools.Method(typeof(ZNetView), "GetPrefabName");

        public readonly List<SensorHit> Trees = new List<SensorHit>();
        public readonly List<SensorHit> Ores = new List<SensorHit>();
        public readonly List<SensorHit> Pickables = new List<SensorHit>();
        public readonly List<SensorHit> Loot = new List<SensorHit>();
        public readonly List<SensorHit> Mobs = new List<SensorHit>();
        public readonly List<SensorHit> Stations = new List<SensorHit>();

        // Пул SensorHit — одни и те же объекты из скана в скан, без new на каждый запрос (кроме разогрева).
        private readonly List<SensorHit> _pool = new List<SensorHit>();
        private int _poolUsed;

        private float _timer;
        private float _nextLogAt;
        public float LastScanMs; // стоимость последнего скана — читает дебаг-оверлей/лог

        // По таймеру (Plugin.Update/FollowerAI.TickBody зовёт каждый кадр, сам сенсор решает, когда пора).
        public void Tick(float dt, Vector3 origin, float radius, Character relativeTo)
        {
            _timer -= dt;
            if (_timer > 0f) return;
            _timer = 1f / Mathf.Max(0.1f, TickRate.Value);
            Scan(origin, radius, relativeTo);
        }

        // По запросу — сразу, без таймера (так зовут автотесты).
        public void Scan(Vector3 origin, float radius, Character relativeTo = null)
        {
            EnsureMasks();
            var sw = Stopwatch.StartNew();

            Trees.Clear(); Ores.Clear(); Pickables.Clear(); Loot.Clear(); Mobs.Clear(); Stations.Clear();
            _poolUsed = 0;
            _seen.Clear();

            ScanWorldObjects(origin, radius);
            ScanStations(origin, radius);
            ScanLoot(origin, radius);
            ScanMobs(origin, radius, relativeTo);

            SortByDistance(Trees); SortByDistance(Ores); SortByDistance(Pickables);
            SortByDistance(Loot); SortByDistance(Mobs); SortByDistance(Stations);

            sw.Stop();
            LastScanMs = (float)sw.Elapsed.TotalMilliseconds;
            if (DebugLog.Value && Time.time > _nextLogAt)
            {
                _nextLogAt = Time.time + 2f;
                Plugin.Log.LogInfo("[Sensor] r=" + (int)radius + "м деревья=" + Trees.Count + " руда=" + Ores.Count
                    + " сбор=" + Pickables.Count + " лут=" + Loot.Count + " мобы=" + Mobs.Count
                    + " станции=" + Stations.Count + " время=" + LastScanMs.ToString("0.00") + "мс");
            }
        }

        private static void SortByDistance(List<SensorHit> list)
        {
            list.Sort(delegate (SensorHit a, SensorHit b) { return a.Distance.CompareTo(b.Distance); });
        }

        // Одна OverlapSphereNonAlloc на "статичный мир" обслуживает деревья/руду/собираемое/станции —
        // это ровно те слои, которыми сама ZoneSystem проверяет столкновения с этими объектами.
        private float _nextOverflowLogAt;

        private void ScanWorldObjects(Vector3 origin, float radius)
        {
            int n = Physics.OverlapSphereNonAlloc(origin, radius, _overlapBuf, _worldMask);
            if (n >= _overlapBuf.Length && Time.time > _nextOverflowLogAt)
            {
                _nextOverflowLogAt = Time.time + 2f;
                Plugin.Log.LogWarning("[Sensor] буфер коллайдеров переполнен (" + n + ") — увеличить размер");
            }
            for (int i = 0; i < n; i++)
            {
                var col = _overlapBuf[i];
                if (col == null) continue;
                ClassifyWorldCollider(col, origin);
            }
        }

        // Станции крафта (верстак/кузница/точило/котелок-артизан и т.п.) — готовый реестр игры
        // CraftingStation.Instances (см. _research/ahead_sensor_scout.md §2,§8.5), без Overlap-скана.
        private void ScanStations(Vector3 origin, float radius)
        {
            // Instances типизирован интерфейсом IMonoUpdater (общий паттерн движка), но реально
            // кладёт/убирает себя сюда только сам CraftingStation (CraftingStation.OnEnable/OnDisable).
            var list = CraftingStation.Instances;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var st = list[i] as CraftingStation;
                if (st == null) continue;
                float d = Vector3.Distance(origin, st.transform.position);
                if (d > radius) continue;
                string label = !string.IsNullOrEmpty(st.m_name) ? Localization.instance.Localize(st.m_name) : "Станция";
                AddStation(st, label, origin);
            }
        }

        // От специфичного к общему; дедуп по найденному компоненту (у дерева/жилы руды несколько
        // коллайдеров на один компонент — считать её/его один раз).
        private void ClassifyWorldCollider(Collider col, Vector3 origin)
        {
            // CraftingStation (верстак/кузница/точило...) сюда НЕ попадает — у неё свой готовый
            // реестр CraftingStation.Instances (см. ScanStations), Overlap-скан для неё избыточен
            // (_research/ahead_sensor_scout.md §2,§8.5). Overlap+GetComponentInParent остаётся только
            // для Container/Fireplace/CookingStation — у них своего реестра в игре нет.
            var container = col.GetComponentInParent<Container>();
            if (container != null) { AddStation(container, "Сундук", origin); return; }
            var fire = col.GetComponentInParent<Fireplace>();
            if (fire != null) { AddStation(fire, "Костёр", origin); return; }
            var cook = col.GetComponentInParent<CookingStation>();
            if (cook != null) { AddStation(cook, "Кухня", origin); return; }

            var tree = col.GetComponentInParent<TreeBase>();
            if (tree != null) { AddResource(SensorCategory.Tree, Trees, tree, tree.m_minToolTier, origin); return; }
            var log = col.GetComponentInParent<TreeLog>();
            if (log != null) { AddResource(SensorCategory.Tree, Trees, log, log.m_minToolTier, origin); return; }

            var ore5 = col.GetComponentInParent<MineRock5>();
            if (ore5 != null) { AddResource(SensorCategory.Ore, Ores, ore5, ore5.m_minToolTier, origin); return; }
            var ore = col.GetComponentInParent<MineRock>();
            if (ore != null) { AddResource(SensorCategory.Ore, Ores, ore, ore.m_minToolTier, origin); return; }

            // Найдено в игре (25.10, selftest sensor_basic): MineRock_Tin на деле — обычный
            // Destructible (тот же набор компонентов, что у декоративных Rock_3/Rock_4/Bush01),
            // класс MineRock/MineRock5 не использует вовсе. Структурно руду от декорации не отличить —
            // только по имени префаба (OreDestructibleNames). Список расширять по новым находкам.
            var destr = col.GetComponentInParent<Destructible>();
            if (destr != null)
            {
                string name = PrefabName(destr);
                if (OreDestructibleNames.Contains(name))
                {
                    AddResource(SensorCategory.Ore, Ores, destr, destr.m_minToolTier, origin);
                    return;
                }
            }

            var pick = col.GetComponentInParent<Pickable>();
            if (pick != null) { AddPickable(pick, origin); return; }

            // Диагностика: коллайдер попал в маску мира, но ни один известный компонент/имя не
            // подошёл — печатаем слой и имя, чтобы понять, какой компонент/слой/имя добавить.
            // Не чаще раза в 2с, чтобы не заспамить лог в реальной игре (лес кишит декорацией).
            if (DebugLog.Value && Time.time > _nextUnclassifiedLogAt)
            {
                _nextUnclassifiedLogAt = Time.time + 2f;
                Plugin.Log.LogInfo("[Sensor] НЕ РАСПОЗНАН: \"" + col.gameObject.name + "\" слой="
                    + LayerMask.LayerToName(col.gameObject.layer));
            }
        }

        private float _nextUnclassifiedLogAt;

        // Имена префабов-Destructible, которые на деле руда (не декорация) — см. комментарий выше.
        // MineRock_Tin подтверждён в игре; Obsidian/Meteorite добавлены по той же схеме именования
        // (не проверены живьём — если оба реально через класс MineRock, лишняя запись здесь безвредна).
        private static readonly HashSet<string> OreDestructibleNames = new HashSet<string>
        {
            "MineRock_Tin", "MineRock_Obsidian", "MineRock_Meteorite"
        };

        private void AddResource(SensorCategory cat, List<SensorHit> into, Component comp, int minToolTier, Vector3 origin)
        {
            if (!_seen.Add(comp)) return;
            var h = GetHit();
            h.Category = cat;
            h.Comp = comp;
            h.Name = PrefabName(comp);
            h.Distance = Vector3.Distance(origin, comp.transform.position);
            h.MinToolTier = minToolTier;
            into.Add(h);
        }

        private void AddPickable(Pickable p, Vector3 origin)
        {
            if (!_seen.Add(p)) return;
            var h = GetHit();
            h.Category = SensorCategory.Pickable;
            h.Comp = p;
            h.Name = PickableItemName(p);
            h.Distance = Vector3.Distance(origin, p.transform.position);
            bool ready = false;
            try { ready = p.CanBePicked(); } catch { }
            h.PickableReady = ready;
            Pickables.Add(h);
        }

        private static string PickableItemName(Pickable p)
        {
            if (p.m_itemPrefab == null) return PrefabName(p);
            var drop = p.m_itemPrefab.GetComponent<ItemDrop>();
            if (drop != null && drop.m_itemData != null && drop.m_itemData.m_shared != null)
                return Localization.instance.Localize(drop.m_itemData.m_shared.m_name);
            return p.m_itemPrefab.name;
        }

        private void AddStation(Component comp, string typeLabel, Vector3 origin)
        {
            if (!_seen.Add(comp)) return;
            var h = GetHit();
            h.Category = SensorCategory.Station;
            h.Comp = comp;
            h.Name = PrefabName(comp);
            h.StationType = typeLabel;
            h.Distance = Vector3.Distance(origin, comp.transform.position);
            Stations.Add(h);
        }

        // Лут на земле: тот же паттерн, что MonsterAI.FindClosestConsumableItem (подтверждено IL,
        // _research/ahead_sensor_scout.md §3.1) — слой "item", компонент через attachedRigidbody.
        private float _nextLootOverflowLogAt;

        private void ScanLoot(Vector3 origin, float radius)
        {
            int n = Physics.OverlapSphereNonAlloc(origin, radius, _overlapBuf, _itemMask);
            if (n >= _overlapBuf.Length && Time.time > _nextLootOverflowLogAt)
            {
                _nextLootOverflowLogAt = Time.time + 2f;
                Plugin.Log.LogWarning("[Sensor] буфер коллайдеров переполнен (" + n + ") при скане лута — увеличить размер");
            }
            for (int i = 0; i < n; i++)
            {
                var col = _overlapBuf[i];
                if (col == null) continue;
                var rb = col.attachedRigidbody;
                if (rb == null) continue;
                var drop = rb.GetComponent<ItemDrop>();
                if (drop == null || drop.IsPiece() || !_seen.Add(drop)) continue;
                var nview = drop.GetComponent<ZNetView>();
                if (nview != null && !nview.IsValid()) continue;

                var h = GetHit();
                h.Category = SensorCategory.Loot;
                h.Comp = drop;
                h.Name = drop.m_itemData != null && drop.m_itemData.m_shared != null
                    ? Localization.instance.Localize(drop.m_itemData.m_shared.m_name) : PrefabName(drop);
                h.Distance = Vector3.Distance(origin, drop.transform.position);
                h.Stack = drop.m_itemData != null ? drop.m_itemData.m_stack : 0;
                h.AutoPickup = drop.m_autoPickup;
                Loot.Add(h);
            }
        }

        // Мобы — готовый реестр игры (Character.GetCharactersInRange пишет в переданный список,
        // без Physics). Опасность — грубая эвристика (HP*уровень*босс), не боевая формула урона
        // (та — в _research/2b2fix_1.md, сюда не тащим, не нужна для «видим мир»).
        private void ScanMobs(Vector3 origin, float radius, Character relativeTo)
        {
            _charBuf.Clear();
            Character.GetCharactersInRange(origin, radius, _charBuf);
            for (int i = 0; i < _charBuf.Count; i++)
            {
                var c = _charBuf[i];
                if (c == null || c.IsDead()) continue;
                if (c is Player) continue; // игроков мобами не считаем
                if (relativeTo != null && c == relativeTo) continue;
                if (FollowerAI.Registry.ContainsKey(c)) continue; // союзных спутников тоже не считаем мобами

                bool hostile = relativeTo != null
                    ? BaseAI.IsEnemy(c, relativeTo)
                    : c.GetFaction() != Character.Faction.AnimalsVeg;

                var h = GetHit();
                h.Category = SensorCategory.Mob;
                h.Char = c;
                h.Comp = c;
                h.Name = AttackProfiles.PrefabNameOf(c);
                h.Distance = Vector3.Distance(origin, c.transform.position);
                h.Level = c.GetLevel();
                h.Hp = c.GetHealth();
                h.MaxHp = c.GetMaxHealth();
                h.Hostile = hostile;
                h.Danger = ComputeDanger(c, relativeTo);
                Mobs.Add(h);
            }
        }

        private static float ComputeDanger(Character enemy, Character relativeTo)
        {
            float enemyScore = Mathf.Max(1f, enemy.GetMaxHealth()) * (1f + enemy.GetLevel() * 0.5f) * (enemy.IsBoss() ? 3f : 1f);
            if (relativeTo == null) return enemyScore / 100f; // без опоры — грубая абсолютная шкала
            float myScore = Mathf.Max(1f, relativeTo.GetMaxHealth());
            return enemyScore / myScore;
        }

        private SensorHit GetHit()
        {
            SensorHit h;
            if (_poolUsed < _pool.Count) { h = _pool[_poolUsed]; h.Reset(); }
            else { h = new SensorHit(); _pool.Add(h); }
            _poolUsed++;
            return h;
        }

        private static string PrefabName(Component c)
        {
            if (c == null) return "?";
            var nview = c.GetComponentInParent<ZNetView>();
            if (nview != null && _getPrefabNameMethod != null)
            {
                try
                {
                    var n = _getPrefabNameMethod.Invoke(nview, null) as string;
                    if (!string.IsNullOrEmpty(n)) return n;
                }
                catch { }
            }
            return c.gameObject.name;
        }
    }
}

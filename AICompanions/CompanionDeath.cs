using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AICompanions
{
    // Смерть и возрождение бойца (фаза 1, без базы). Коротко:
    //  1) Character.OnDeath (Prefix, см. CompanionTombstonePatch) — пока боец (Humanoid+MonsterAI,
    //     не Player) ещё цел, переносим его вещи в надгробие (как игрок — Player.m_tombstone,
    //     Inventory.MoveInventoryToGrave) и запоминаем личность (имя/роль/цвет/стойка) в Queue —
    //     сам GameObject секундами позже уничтожит штатный Character.OnDeath (ZNetScene.Destroy).
    //  2) Через RespawnDelay сек (Tick) — новый боец на суше ≥RespawnMinDistance от хозяина
    //     (в воде — у хозяина), голый, с тем же именем/ролью/цветом/стойкой (CompanionManager не
    //     трогаем — своя мини-версия спавна, т.к. SetupOnce не должен выдавать стартовый набор).
    //  3) RevivalState на FollowerAI.Revival (точка FollowerAI.TickBody) — бежит к своему надгробию
    //     (MonsterAI.FollowTarget), забирает вещи, надевает по одной с паузой; дойдёт до кувырков/
    //     парирования — это уже делают CombatGuard/CompanionDodge/ThreatTracker сами (ничего
    //     своего не дублируем, они тикают для любого FollowerAI независимо от Revival).
    public static class CompanionDeath
    {
        public enum Phase { Running, Looting, Dressing }

        // Личность, которая переживает уничтожение старого GameObject (Character.OnDeath сам
        // его удаляет). Строка в плашке HUD строится по этому списку, пока нет живого FollowerAI
        // (см. Plugin.cs — добавление в _hudRows).
        public class Pending
        {
            public string Name;
            public CompanionRole Role;
            public int ColorIndex;
            public FollowerAI.Stance Mode;
            public GameObject Tomb;      // null — инвентарь был пуст, надгробия нет
            public bool DiedInWater;
            public float LastMaxHp;      // только для HUD (полоска при "мёртв")
            public float RespawnAt;      // Time.time
            // Снимок "что было надето/в рюкзаке ДО смерти" — для режима I (баг Ильи: по I у мёртвого
            // бойца ничего не видно). Берётся в OnCompanionDeath ДО UnequipAllItems/MoveInventoryToGrave
            // (иначе "надето" уже не отличить от рюкзака) и живёт, пока боец не разделся обратно в
            // обычное (FinishRevival снимает Revival — тогда HUD возвращается к реальному телу).
            public List<CompanionInventory.ItemSum> EquipSnapshot;
            public CompanionInventory.Summary BagSnapshot;
        }

        // Состояние голого бойца после возрождения — висит на FollowerAI.Revival, тикает из
        // FollowerAI.TickBody каждый кадр (TickRevival), пока не станет null (полностью одет).
        public class RevivalState
        {
            public GameObject Tomb;
            public Phase Stage = Phase.Running;
            public List<ItemDrop.ItemData> ToEquip = new List<ItemDrop.ItemData>();
            public int EquipIndex;
            public float NextEquipAt;
            public int LoggedFailIndex = -1; // чтобы не спамить лог каждый кадр при застревании
            public float FailingSince = -1f; // Time.time первой неудачи на текущем предмете (сброс при смене/успехе)
            public Vector3 Anchor; // место бывшего надгробия — малая зона, где боец одевается (ТЗ Ильи)
            // Тот же снимок, что в Pending (копируется в DoRespawn) — пока Revival != null, HUD
            // (режим I) показывает ЕГО, а не реальное (голое/частично одетое) тело.
            public List<CompanionInventory.ItemSum> EquipSnapshot;
            public CompanionInventory.Summary BagSnapshot;
        }

        public static readonly List<Pending> Queue = new List<Pending>();

        // Метка на карте для каждого надгробия (цвет бойца) — отдельно от CompanionMapPins (те
        // только для живых). Убирается, когда надгробие забрали/оно пропало/при сбросе состояния.
        private static readonly Dictionary<GameObject, Minimap.PinData> _tombPins = new Dictionary<GameObject, Minimap.PinData>();
        private static readonly List<Character> _guardScanBuf = new List<Character>(32);
        private static long _fakeOwnerSeq = -1000000L;
        private static GameObject _tombPrefabCache;
        private const int LandSearchAttempts = 40;

        // ВАЖНО: Humanoid.EquipItem(ItemData, bool) в текущей версии игры ПУБЛИЧНЫЙ (dump8 по IL) —
        // без BindingFlags.Public GetMethod возвращает null, одевание молча не начинается (см. тот же
        // фикс в FollowerAI.cs — баг общий для обоих мест).
        private static readonly MethodInfo EquipItemMethod = typeof(Humanoid).GetMethod("EquipItem",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(ItemDrop.ItemData), typeof(bool) }, null);
        private static readonly FieldInfo MonsterTargetField = AccessTools.Field(typeof(MonsterAI), "m_targetCreature");
        // Та же reflection на BaseAI.MoveTo, что FollowRunPatch (Patches.cs) — вручную держать бойца
        // в малой зоне у места бывшего надгробия во время одевания (FollowTarget туда больше не указывает).
        private static readonly MethodInfo MoveToMethod = AccessTools.Method(typeof(BaseAI), "MoveTo",
            new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) });

        public static ConfigEntry<float> RespawnDelay;
        public static ConfigEntry<float> RespawnMinDistance;
        public static ConfigEntry<float> RespawnDistanceSpread;
        public static ConfigEntry<float> DressRadius;
        public static ConfigEntry<float> DressPause;
        public static ConfigEntry<float> OwnerGuardRadius;

        public static void Init(ConfigFile config)
        {
            RespawnDelay = config.Bind("Death", "RespawnDelay", 15f, "Через сколько сек после смерти боец возрождается (голым)");
            RespawnMinDistance = config.Bind("Death", "RespawnMinDistance", 100f, "Без базы — не ближе этого к хозяину ищем точку возрождения на суше, м");
            RespawnDistanceSpread = config.Bind("Death", "RespawnDistanceSpread", 60f, "Разброс поверх минимума при поиске точки на суше, м (итог: Min..Min+Spread)");
            DressRadius = config.Bind("Death", "DressRadius", 3.5f, "Радиус у надгробия, где боец забирает вещи и одевается, м");
            DressPause = config.Bind("Death", "DressPause", 1.2f, "Пауза между надеванием каждой вещи после возрождения, сек (как у игрока — по одной)");
            OwnerGuardRadius = config.Bind("Death", "OwnerGuardRadius", 15f, "P0: враг ближе этого к хозяину — голый боец перетягивает его агро на себя и ведёт кувырками дальше своим маршрутом (к надгробию/вещам), а не бросает хозяина без защиты");
            if (EquipItemMethod == null) Plugin.Log.LogWarning("CompanionDeath: не нашёл Humanoid.EquipItem reflection'ом — одевание после возрождения не будет работать");
        }

        // Hot reload (Plugin.OnDestroy) — НЕ трогаем сами надгробия (сетевые объекты с вещами
        // бойца должны переживать перезагрузку мода, как настоящие надгробия игрока), только
        // свои метки на карте и очередь возрождения (боец без Pending просто не оживёт сам —
        // цена простоты, надгробие с вещами всё равно останется в мире).
        public static void Shutdown()
        {
            foreach (var kv in _tombPins) if (kv.Value != null && Minimap.instance != null) Minimap.instance.RemovePin(kv.Value);
            _tombPins.Clear();
            Queue.Clear();
        }

        public static void OnWorldLeft()
        {
            _tombPins.Clear();
            Queue.Clear();
        }

        // Только для автотестов (SelfTest.RunAll, между сценариями) — в отличие от Shutdown
        // ЗДЕСЬ надгробия намеренно уничтожаем, чтобы тест не оставлял мусор для следующего.
        public static void ResetForTest()
        {
            foreach (var kv in _tombPins)
            {
                if (kv.Value != null && Minimap.instance != null) Minimap.instance.RemovePin(kv.Value);
                if (kv.Key != null && ZNetScene.instance != null) ZNetScene.instance.Destroy(kv.Key);
            }
            _tombPins.Clear();
            Queue.Clear();
        }

        public static string StatusText(Pending p)
        {
            float left = p.RespawnAt - Time.time;
            return left > 0f ? "мёртв · возрождение через " + Mathf.CeilToInt(left) + "с" : "возрождается…";
        }

        // Единая точка для HUD (режим I, Plugin.cs DrawHud): вещи мёртвого бойца, ждущего в очереди
        // возрождения — те же типы (ItemSum/Summary), что у живого, так отрисовка их не отличает.
        public static void InventorySnapshot(Pending p, out List<CompanionInventory.ItemSum> equip, out CompanionInventory.Summary bag)
        {
            equip = p.EquipSnapshot;
            bag = p.BagSnapshot;
        }

        // ---- момент смерти (из CompanionTombstonePatch) ----

        public static void OnCompanionDeath(FollowerAI f)
        {
            var body = f.Body;
            if (body == null) return;
            bool diedInWater = body.IsSwimming() || body.InWater();

            // Повторная смерть ГОЛОГО бойца (Revival ещё висел) ДО того, как он добрался до своего
            // первого надгробия (r.Tomb ещё не null, т.е. ещё не забрал/не уничтожил его в TickLooting):
            // переносим ссылку на то же, непустое, старое надгробие дальше, а не теряем её — иначе
            // боец навечно застревал голым (TickRunning: r.Tomb==null → Revival не снимается), а
            // старое надгробие с вещами и меткой на карте оставалось брошенным навсегда.
            GameObject tomb;
            List<CompanionInventory.ItemSum> equipSnap;
            CompanionInventory.Summary bagSnap;
            var oldRevival = f.Revival;
            if (oldRevival != null && oldRevival.Tomb != null)
            {
                // Повторная смерть голого/недоодетого бойца ДО того, как он добрался до надгробия —
                // тело и так пустое (ничего не изменилось с первой смерти), снимок для HUD берём
                // старый (тот же, что уже показывался), а не считаем заново по пустому телу.
                tomb = oldRevival.Tomb;
                equipSnap = oldRevival.EquipSnapshot;
                bagSnap = oldRevival.BagSnapshot;
            }
            else
            {
                // ДО CreateTombstone (внутри — UnequipAllItems/MoveInventoryToGrave): иначе "надето"
                // уже не отличить от рюкзака — то, что останется в надгробии, всё окажется "в рюкзаке".
                equipSnap = CompanionInventory.SummarizeEquipped(body);
                bagSnap = CompanionInventory.Summarize(body);
                var inv = body.GetInventory();
                tomb = (inv != null && inv.NrOfItems() > 0) ? CreateTombstone(f, body) : null;
            }

            var p = new Pending();
            p.Name = f.Name;
            p.Role = f.Role;
            p.ColorIndex = f.ColorIndex;
            p.Mode = f.Mode;
            p.Tomb = tomb;
            p.EquipSnapshot = equipSnap;
            p.BagSnapshot = bagSnap;
            p.DiedInWater = diedInWater;
            p.LastMaxHp = body.GetMaxHealth();
            p.RespawnAt = Time.time + RespawnDelay.Value;
            Queue.Add(p);

            Plugin.Log.LogInfo("[Death] " + f.Name + (tomb != null ? ": надгробие создано" : ": инвентарь был пуст — надгробия нет")
                + ", возрождение через " + RespawnDelay.Value.ToString("0") + "с"
                + (diedInWater ? " (в воде — у хозяина)" : " (на суше)"));
        }

        private static GameObject CreateTombstone(FollowerAI f, Humanoid body)
        {
            var tombPrefab = TombstonePrefab();
            if (tombPrefab == null) { Plugin.Log.LogWarning("[Death] нет префаба надгробия (Player.m_tombstone)"); return null; }

            body.UnequipAllItems(); // как у игрока перед CreateTombStone — иначе часть вещей "зависает" надетой на пустом теле
            Vector3 pos = body.GetCenterPoint();
            Quaternion rot = body.transform.rotation;
            var tomb = Object.Instantiate(tombPrefab, pos, rot);

            var container = tomb.GetComponent<Container>();
            if (container != null) container.GetInventory().MoveInventoryToGrave(body.GetInventory());

            var ts = tomb.GetComponent<TombStone>();
            if (ts != null) ts.Setup(f.Name, NextFakeOwnerId());

            var pin = CompanionMapPins.CreatePin(pos, f.Name + " †");
            if (pin != null) pin.m_icon = CompanionMapPins.SpriteFor(f.ColorIndex);
            _tombPins[tomb] = pin;
            return tomb;
        }

        private static GameObject TombstonePrefab()
        {
            if (_tombPrefabCache != null) return _tombPrefabCache;
            if (ZNetScene.instance == null) return null;
            var playerPrefab = ZNetScene.instance.GetPrefab("Player");
            var player = playerPrefab != null ? playerPrefab.GetComponent<Player>() : null;
            _tombPrefabCache = player != null ? player.m_tombstone : null;
            return _tombPrefabCache;
        }

        private static long NextFakeOwnerId() { return _fakeOwnerSeq--; }

        private static void RemoveTomb(GameObject tomb)
        {
            Minimap.PinData pin;
            if (_tombPins.TryGetValue(tomb, out pin))
            {
                _tombPins.Remove(tomb);
                if (pin != null && Minimap.instance != null) Minimap.instance.RemovePin(pin);
            }
            if (tomb != null && ZNetScene.instance != null) ZNetScene.instance.Destroy(tomb);
        }

        // ---- тик очереди возрождения (Plugin.Update) ----

        public static void Tick(float dt)
        {
            if (Queue.Count == 0) return;
            for (int i = Queue.Count - 1; i >= 0; i--)
            {
                var p = Queue[i];
                if (Time.time >= p.RespawnAt) DoRespawn(p);
            }
        }

        private static void DoRespawn(Pending p)
        {
            var owner = Player.m_localPlayer;
            if (owner == null) return; // мир не загружен — попробуем следующим кадром, запись остаётся

            if (CompanionManager.Alive().Count >= Plugin.MaxCompanions.Value)
            {
                Plugin.Log.LogWarning("[Death] " + p.Name + ": лимит бойцов — возрождение отложено (надгробие цело)");
                p.RespawnAt = Time.time + 5f;
                return;
            }

            Vector3 pos; string how;
            if (p.DiedInWater)
            {
                pos = owner.transform.position + owner.transform.forward * 2f + Vector3.up * 0.5f;
                how = "у хозяина (умер в воде)";
            }
            else if (TryFindLandPoint(owner.transform.position, out pos))
            {
                how = "на суше, " + (int)Vector3.Distance(pos, owner.transform.position) + "м от хозяина";
            }
            else
            {
                pos = owner.transform.position + owner.transform.forward * 3f + Vector3.up * 0.5f;
                how = "у хозяина (не нашли точку на суше)";
            }

            var tpl = CompanionFactory.GetTemplate();
            if (tpl == null)
            {
                // Как и ветка лимита бойцов выше — откладываем повтор, а не оставляем RespawnAt в
                // прошлом (иначе Tick(dt) звал бы DoRespawn КАЖДЫЙ кадр: внутри GetTemplate() заново
                // Build() — Instantiate+reflection CopyFields — и спам LogWarning, пока шаблон не готов).
                Plugin.Log.LogWarning("[Death] " + p.Name + ": нет шаблона бойца, возрождение отложено (повторю через 5с)");
                p.RespawnAt = Time.time + 5f;
                return;
            }
            var rot = Quaternion.LookRotation(-owner.transform.forward);
            var go = Object.Instantiate(tpl, pos, rot);
            var f = go.GetComponent<FollowerAI>();

            var revival = new RevivalState();
            revival.Tomb = p.Tomb;
            revival.EquipSnapshot = p.EquipSnapshot;
            revival.BagSnapshot = p.BagSnapshot;
            f.Revival = revival;

            f.Init(owner, p.ColorIndex);
            f.Role = p.Role;
            f.SetName(p.Name);
            f.SetStance(p.Mode);

            Queue.Remove(p);
            Plugin.Log.LogInfo("[Death] " + p.Name + " возродился голым " + how);
        }

        // Случайная точка на суше: XZ в кольце Min..Min+Spread вокруг center, высота —
        // ZoneSystem.GetSolidHeight (без учёта воды/растений), годится если выше уровня воды
        // и не "в стене" (IsBlocked). Не нашли за N попыток — вызывающий код сам решает фоллбэк.
        private static bool TryFindLandPoint(Vector3 center, out Vector3 point)
        {
            var zs = ZoneSystem.instance;
            if (zs == null) { point = center; return false; }
            float min = RespawnMinDistance.Value;
            float max = min + Mathf.Max(0f, RespawnDistanceSpread.Value);
            for (int i = 0; i < LandSearchAttempts; i++)
            {
                float angle = Random.Range(0f, 360f);
                float dist = Random.Range(min, max);
                Vector3 cand = center + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * dist;
                float h;
                if (!zs.GetSolidHeight(cand, out h)) continue;
                if (h <= zs.m_waterLevel + 0.1f) continue; // вода/урез воды
                cand.y = h + 0.3f;
                if (zs.IsBlocked(cand)) continue;
                point = cand;
                return true;
            }
            point = center;
            return false;
        }

        // ---- голый боец после возрождения (из FollowerAI.TickBody каждый кадр) ----

        public static void TickRevival(FollowerAI f, float dt)
        {
            var r = f.Revival;
            if (r == null) return;
            var body = f.Body;
            if (body == null || body.IsDead()) return; // умер по дороге — CompanionTombstonePatch сам всё обработает заново

            if (f.Dodge.InDodge) { f.Status = "кувырок!"; return; } // не мешаем кувырку — команды подождут кадр

            // P0: серьёзная угроза хозяину — не бросаем его без защиты, но и не бросаем свою задачу
            // (к вещам/в строй). Перетягиваем агро конкретного врага на голого бойца (решаем сразу,
            // не ждём удар, как AggroOnCompanionPatch) и ведём его дальше обычным маршрутом —
            // он уводит врага кувырками (общий CombatGuard/CompanionDodge) к своему надгробию/вещам.
            var owner = Player.m_localPlayer;
            Character threat = owner != null ? FindOwnerThreat(owner) : null;
            if (threat != null) PullAggro(threat, body);

            switch (r.Stage)
            {
                case Phase.Running: TickRunning(f, r, body, threat != null); break;
                case Phase.Looting: TickLooting(f, r, body); break;
                case Phase.Dressing: TickDressing(f, r, body, dt); break;
            }
        }

        private static void TickRunning(FollowerAI f, RevivalState r, Humanoid body, bool leadingThreatAway)
        {
            if (r.Tomb == null)
            {
                // Нечего забирать (умер пустым — надгробия не было вовсе, либо оно пропало по дороге,
                // например кто-то сам разграбил/истлело). Надевать так и так нечего — ОСТАЁМСЯ голым
                // навсегда (Revival не снимаем): по требованию голый не атакует и не блокирует, а
                // "забрать вещи и одеться" тут физически невозможно — безопаснее тихо держаться рядом,
                // чем выходить в бой безоружным и беззащитным.
                f.Status = leadingThreatAway ? "увожу врага от тебя (нечего надеть)" : "безоружен — нечего надеть, держится рядом";
                return;
            }
            if (f.Ai != null && f.Ai.GetFollowTarget() != r.Tomb) f.Ai.SetFollowTarget(r.Tomb);
            f.Status = leadingThreatAway ? "увожу врага от тебя к вещам" : "бежит к надгробию";
            if (Vector3.Distance(body.transform.position, r.Tomb.transform.position) <= DressRadius.Value)
                r.Stage = Phase.Looting;
        }

        private static void TickLooting(FollowerAI f, RevivalState r, Humanoid body)
        {
            f.Status = "забирает вещи";
            var tomb = r.Tomb;
            // Надгробие пропало (истлело/разграблено не нашим кодом) или у него вдруг нет Container —
            // забрать нечего. НЕ выходим из Revival (f.Revival = null) — это выпустило бы бойца в бой
            // полностью голым (WantsToFight блокирует атаку только при Revival != null, FollowerAI.cs:390).
            // Переходим в тот же безопасный режим, что и TickRunning при r.Tomb == null: боец останется
            // голым навсегда, но без боя.
            if (tomb == null) { r.Tomb = null; r.Stage = Phase.Running; return; }
            var container = tomb.GetComponent<Container>();
            var tombInv = container != null ? container.GetInventory() : null;
            if (tombInv == null) { RemoveTomb(tomb); r.Tomb = null; r.Stage = Phase.Running; return; }

            r.Anchor = tomb.transform.position; // малая зона одевания — центр на месте надгробия (пока оно не уничтожено)
            int pickedCount = tombInv.GetAllItems().Count;
            body.GetInventory().MoveAll(tombInv);
            Plugin.Log.LogInfo(f.Name + ": забрал из надгробия " + pickedCount + " предметов");

            // ВАЖНО: Inventory.MoveAll не переносит те же объекты ItemData — внутри AddItem каждый
            // предмет КЛОНИРУЕТСЯ (Clone()), если в целевом инвентаре нет подходящего стека под тем
            // же x/y (проверено по IL). Поэтому ссылки из tombInv (как было раньше — "picked") после
            // MoveAll уже не лежат в body.GetInventory(): Humanoid.EquipItem внутри себя делает
            // Inventory.ContainsItem(item) — List.Contains по ссылке — и молча возвращает false для
            // чужого объекта. Из-за этого dress_up зависал на 0/N навечно. Берём предметы ТОЛЬКО из
            // реального инвентаря бойца ПОСЛЕ переноса (он гарантированно был пуст до этого).
            r.ToEquip = new List<ItemDrop.ItemData>();
            var ownItems = body.GetInventory().GetAllItems();
            for (int i = 0; i < ownItems.Count; i++)
            {
                var it = ownItems[i];
                if (it != null && it.m_shared != null && it.IsEquipable()) r.ToEquip.Add(it);
            }
            r.EquipIndex = 0;
            r.NextEquipAt = Time.time; // первую вещь — сразу, без паузы

            RemoveTomb(tomb); // пусто — убираем штатно (ZNetScene.Destroy), просьба Ильи
            r.Tomb = null;

            if (r.ToEquip.Count == 0) { FinishRevival(f); return; }
            r.Stage = Phase.Dressing;
        }

        private static void TickDressing(FollowerAI f, RevivalState r, Humanoid body, float dt)
        {
            f.Status = "одевается (" + r.EquipIndex + "/" + r.ToEquip.Count + ")";
            if (r.EquipIndex >= r.ToEquip.Count) { FinishRevival(f); return; }

            // Малая зона у места бывшего надгробия (ТЗ: "держится в малой зоне, отходит шагом/
            // кувырками"). Надгробие уже уничтожено (TickLooting) — FollowTarget на него не
            // переустановить, а штатный ре-ассерт цели на хозяина для Revival отключен
            // (FollowerAI.cs:541) — иначе MonsterAI без цели мог увести бойца куда угодно во время
            // долгого одевания под атакой. Держим вручную тем же MoveTo, что FollowRunPatch.
            if (Vector3.Distance(body.transform.position, r.Anchor) > DressRadius.Value)
            {
                f.Status = "одевается (" + r.EquipIndex + "/" + r.ToEquip.Count + ") — возвращаюсь в зону";
                if (MoveToMethod != null && f.Ai != null)
                    MoveToMethod.Invoke(f.Ai, new object[] { dt, r.Anchor, DressRadius.Value * 0.5f, false });
                return; // идём назад в зону — одевание подождёт кадр
            }

            if (Time.time < r.NextEquipAt) return;
            if (body.InAttack() || body.InDodge()) return; // EquipItem молча откажет — попробуем следующий кадр

            var item = r.ToEquip[r.EquipIndex];
            if (item == null) { r.EquipIndex++; r.FailingSince = -1f; return; }
            bool ok = false;
            bool threw = false;
            try { ok = EquipItemMethod != null && (bool)EquipItemMethod.Invoke(body, new object[] { item, true }); }
            catch (System.Exception ex)
            {
                threw = true;
                // Тот же дедуп, что у обычного отказа ниже — иначе исключение КАЖДОЙ вещи (не
                // транзиентное, а стабильное — несовместимый/битый ItemData) спамило бы лог и
                // reflection-инвок каждый кадр, пока боец "одевается" (TickBody — горячий путь).
                if (r.LoggedFailIndex != r.EquipIndex)
                {
                    r.LoggedFailIndex = r.EquipIndex;
                    Plugin.Log.LogWarning(f.Name + ": ошибка надевания " + item.m_shared.m_name + ": " + ex.Message);
                }
            }
            if (!ok)
            {
                if (!threw && r.LoggedFailIndex != r.EquipIndex) // исключение уже залогировано выше — не дублируем
                {
                    r.LoggedFailIndex = r.EquipIndex;
                    Plugin.Log.LogInfo(f.Name + ": НЕ получилось надеть " + item.m_shared.m_name
                        + " (слот=" + item.m_shared.m_itemType + " InAttack=" + body.InAttack() + " InDodge=" + body.InDodge() + ") — повторю");
                }
                if (r.FailingSince < 0f) r.FailingSince = Time.time;
                else if (Time.time - r.FailingSince > 3f) // предмет не надевается никак 3с — не виснем навечно
                {
                    Plugin.Log.LogWarning(f.Name + ": " + item.m_shared.m_name + " так и не наделся за 3с — пропускаю вещь");
                    r.EquipIndex++;
                    r.FailingSince = -1f;
                    r.LoggedFailIndex = -1;
                    r.NextEquipAt = Time.time; // следующую вещь — сразу
                }
                return; // не получилось прямо сейчас — попробуем снова следующий кадр
            }

            r.FailingSince = -1f;
            Plugin.Log.LogInfo(f.Name + ": надел " + Localization.instance.Localize(item.m_shared.m_name));
            r.EquipIndex++;
            r.NextEquipAt = Time.time + DressPause.Value;
        }

        private static void FinishRevival(FollowerAI f)
        {
            f.Revival = null;
            Plugin.Log.LogInfo(f.Name + ": полностью одет, вернулся в строй");
        }

        // P0: ближайший к хозяину враг в радиусе — не тот же фильтр, что FollowerAI.WantsToFight
        // (там нужен конкретный боец), здесь просто "кого из угрожающих хозяину тянуть на себя"
        // (ближайший — предсказуемее для теста и для самого манёвра, чем первый из сканера).
        private static Character FindOwnerThreat(Player owner)
        {
            _guardScanBuf.Clear();
            Character.GetCharactersInRange(owner.transform.position, OwnerGuardRadius.Value, _guardScanBuf);
            Character best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < _guardScanBuf.Count; i++)
            {
                var c = _guardScanBuf[i];
                if (c == null || c.IsDead() || c == (Character)owner) continue;
                if (c.GetFaction() == Character.Faction.AnimalsVeg) continue;
                if (FollowerAI.Registry.ContainsKey(c)) continue;
                float d = Vector3.Distance(c.transform.position, owner.transform.position);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        // Решаем сразу, не ждём удар по бойцу (в отличие от AggroOnCompanionPatch, который
        // переключает цель моба только на уроне) — тот же приём, что CombatV2aTests использует
        // в тестах для принудительной цели моба (TargetField.SetValue).
        private static void PullAggro(Character enemy, Character me)
        {
            if (MonsterTargetField == null || enemy == null || me == null) return;
            var ai = enemy.GetComponent<MonsterAI>();
            if (ai == null) return;
            var cur = MonsterTargetField.GetValue(ai) as Character;
            if (cur == me) return;
            MonsterTargetField.SetValue(ai, me);
        }
    }

    // Пока боец (Humanoid+MonsterAI) ещё цел — создаём надгробие с его вещами (см. CompanionDeath).
    // Отдельно от CompanionDeathNoticePatch (CombatPatches.cs) — тот только уведомление, не трогаем.
    [HarmonyPatch(typeof(Character), "OnDeath")]
    internal static class CompanionTombstonePatch
    {
        static void Prefix(Character __instance)
        {
            FollowerAI f;
            if (!FollowerAI.Registry.TryGetValue(__instance, out f)) return;
            CompanionDeath.OnCompanionDeath(f);
        }
    }
}

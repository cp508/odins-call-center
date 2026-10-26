using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Автотесты «Смерть и возрождение» (фаза 1, без базы): надгробие, таймер, голый спавн,
    // одевание. Чтобы не ждать боевую смерть и не зависеть от террейна теста — убиваем через
    // SetHealth(0) (тот же CheckDeath/OnDeath, что и в реальном бою) и на время теста сжимаем
    // дальность/паузы возрождения конфигом (restore в finally).
    public static class DeathTests
    {
        public static void Register()
        {
            SelfTest.Register("death_tombstone", DeathTombstone, 30f);
            SelfTest.Register("respawn_naked", RespawnNaked, 45f);
            SelfTest.Register("dress_up", DressUp, 90f);
            SelfTest.Register("hud_dead_details", HudDeadDetails, 140f);
            SelfTest.Register("demo_death", DemoDeath, 500f);
        }

        // Боец со стартовым набором умирает → должна появиться запись в очереди возрождения
        // с непустым надгробием (его вещи).
        private static IEnumerator DeathTombstone()
        {
            var c = SelfTest.SpawnCompanion(2f, 0f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(2.5f); // SetupOnce успевает выдать набор (щит+дубина)
            if (c.Body == null) { SelfTest.Fail("боец пропал до смерти"); yield break; }

            int itemsBefore = c.Body.GetInventory().NrOfItems();
            string name = c.Name;
            if (itemsBefore == 0) { SelfTest.Fail("у бойца пусто перед смертью — надгробие и не ожидается"); yield break; }

            c.Body.SetHealth(0f);
            var died = new SelfTest.WaitResult();
            yield return SelfTest.WaitUntil(delegate { return c.Body == null; }, 5f, died);
            if (!died.Ok) { SelfTest.Fail("боец не умер за 5с (HP=0)"); yield break; }

            CompanionDeath.Pending pending = null;
            foreach (var p in CompanionDeath.Queue) if (p.Name == name) pending = p;
            if (pending == null) { SelfTest.Fail("нет записи в очереди возрождения"); yield break; }
            if (pending.Tomb == null) { SelfTest.Fail("надгробие не создано (вещей было " + itemsBefore + ")"); yield break; }

            var container = pending.Tomb.GetComponent<Container>();
            int itemsInTomb = container != null ? container.GetInventory().NrOfItems() : -1;
            string info = "вещей было=" + itemsBefore + " в надгробии=" + itemsInTomb
                + " возрождение через=" + (pending.RespawnAt - Time.time).ToString("0.0") + "с";
            if (itemsInTomb <= 0) { SelfTest.Fail("надгробие пустое | " + info); yield break; }

            SelfTest.Pass(info);
        }

        // Пустой инвентарь перед смертью (RemoveAll) — надгробия не будет (как у игрока), боец
        // возрождается и остаётся голым стабильно (без гонки с процессом одевания): проверяем
        // имя/роль/цвет/стойку того же бойца, таймер возрождения и что он не в воде.
        private static IEnumerator RespawnNaked()
        {
            float oldDelay = CompanionDeath.RespawnDelay.Value;
            float oldMin = CompanionDeath.RespawnMinDistance.Value;
            float oldSpread = CompanionDeath.RespawnDistanceSpread.Value;
            CompanionDeath.RespawnDelay.Value = 4f;
            CompanionDeath.RespawnMinDistance.Value = 10f;
            CompanionDeath.RespawnDistanceSpread.Value = 10f;
            try
            {
                var c = SelfTest.SpawnCompanion(2f, 0f);
                if (c == null) { SelfTest.Fail("не призвался"); yield break; }
                yield return new WaitForSeconds(2.5f);
                if (c.Body == null) { SelfTest.Fail("боец пропал до смерти"); yield break; }

                string name = c.Name;
                CompanionRole role = c.Role;
                int colorIndex = c.ColorIndex;
                FollowerAI.Stance mode = c.Mode;
                Vector3 ownerPos = SelfTest.Me.transform.position;

                c.Body.GetInventory().RemoveAll(); // без вещей — без надгробия, тест только про сам респавн
                c.Body.SetHealth(0f);
                var died = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return c.Body == null; }, 5f, died);
                if (!died.Ok) { SelfTest.Fail("боец не умер за 5с"); yield break; }

                FollowerAI revived = null;
                var spawned = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate
                {
                    foreach (var f in FollowerAI.All) if (f != null && f.Name == name) { revived = f; return true; }
                    return false;
                }, 15f, spawned);
                if (!spawned.Ok || revived == null) { SelfTest.Fail("не возродился за 15с (ожидали ~4с)"); yield break; }

                if (revived.Role != role) { SelfTest.Fail("роль не сохранилась: " + revived.Role + " != " + role); yield break; }
                if (revived.ColorIndex != colorIndex) { SelfTest.Fail("цвет не сохранился: " + revived.ColorIndex + " != " + colorIndex); yield break; }
                if (revived.Mode != mode) { SelfTest.Fail("стойка не сохранилась: " + revived.Mode + " != " + mode); yield break; }
                if (revived.Revival == null) { SelfTest.Fail("боец не в режиме голого возрождения (без надгробия ожидали постоянный Revival)"); yield break; }

                int itemCount = revived.Body != null ? revived.Body.GetInventory().NrOfItems() : -1;
                bool swimming = revived.Body != null && (revived.Body.IsSwimming() || revived.Body.InWater());
                float dist = Vector3.Distance(revived.transform.position, ownerPos);
                string info = "вещей=" + itemCount + " дистанция_от_хозяина=" + dist.ToString("0.0") + "м в_воде=" + swimming
                    + " роль=" + revived.Role + " цвет=" + revived.ColorIndex + " стойка=" + revived.Mode;

                if (itemCount != 0) { SelfTest.Fail("не голый | " + info); yield break; }
                if (swimming) { SelfTest.Fail("в воде (ожидали сушу) | " + info); yield break; }
                if (dist < CompanionDeath.RespawnMinDistance.Value - 3f) { SelfTest.Fail("слишком близко к хозяину | " + info); yield break; }

                SelfTest.Pass(info);
            }
            finally
            {
                CompanionDeath.RespawnDelay.Value = oldDelay;
                CompanionDeath.RespawnMinDistance.Value = oldMin;
                CompanionDeath.RespawnDistanceSpread.Value = oldSpread;
            }
        }

        // Полный цикл: умер с набором → надгробие → через короткий таймер возродился рядом →
        // добежал → забрал → надел по одной (пауза сжата, но > 0) → статус "в строю", с оружием.
        private static IEnumerator DressUp()
        {
            float oldDelay = CompanionDeath.RespawnDelay.Value;
            float oldMin = CompanionDeath.RespawnMinDistance.Value;
            float oldSpread = CompanionDeath.RespawnDistanceSpread.Value;
            float oldPause = CompanionDeath.DressPause.Value;
            CompanionDeath.RespawnDelay.Value = 3f;
            CompanionDeath.RespawnMinDistance.Value = 5f;
            CompanionDeath.RespawnDistanceSpread.Value = 5f;
            CompanionDeath.DressPause.Value = 0.4f;
            try
            {
                var c = SelfTest.SpawnCompanion(2f, 0f);
                if (c == null) { SelfTest.Fail("не призвался"); yield break; }
                yield return new WaitForSeconds(2.5f);
                if (c.Body == null) { SelfTest.Fail("боец пропал до смерти"); yield break; }

                int kitItems = c.Body.GetInventory().NrOfItems();
                if (kitItems == 0) { SelfTest.Fail("у бойца пусто перед смертью — тест не проверит одевание"); yield break; }
                string name = c.Name;

                c.Body.SetHealth(0f);
                var died = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return c.Body == null; }, 5f, died);
                if (!died.Ok) { SelfTest.Fail("боец не умер за 5с"); yield break; }

                FollowerAI revived = null;
                var spawned = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate
                {
                    foreach (var f in FollowerAI.All) if (f != null && f.Name == name) { revived = f; return true; }
                    return false;
                }, 10f, spawned);
                if (!spawned.Ok || revived == null) { SelfTest.Fail("не возродился за 10с"); yield break; }

                var dressed = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return revived.Revival == null; }, 40f, dressed);
                if (!dressed.Ok) { SelfTest.Fail("не закончил одевание за 40с, статус=" + revived.Status); yield break; }
                if (revived.Body == null) { SelfTest.Fail("боец пропал во время одевания"); yield break; }

                var weapon = revived.Body.GetCurrentWeapon();
                int itemsAfter = revived.Body.GetInventory().NrOfItems();
                bool armed = weapon != null;
                string info = "вещей было=" + kitItems + " после одевания=" + itemsAfter
                    + " оружие=" + (armed ? weapon.m_shared.m_name : "нет");
                if (!armed) { SelfTest.Fail("не вооружился | " + info); yield break; }
                if (itemsAfter < kitItems) { SelfTest.Fail("часть вещей потерялась | " + info); yield break; }

                SelfTest.Pass(info);
            }
            finally
            {
                CompanionDeath.RespawnDelay.Value = oldDelay;
                CompanionDeath.RespawnMinDistance.Value = oldMin;
                CompanionDeath.RespawnDistanceSpread.Value = oldSpread;
                CompanionDeath.DressPause.Value = oldPause;
            }
        }

        // БАГ Ильи: "боец умер быстро, по I у мёртвого ничего не видно". Проверяем, что данные
        // для режима I (CompanionDeath.InventorySnapshot/RevivalState.*Snapshot) есть на всех трёх
        // фазах и список вещей (по именам и количеству) совпадает с тем, что было ДО смерти.
        private static IEnumerator HudDeadDetails()
        {
            // Дистанция/пауза — как в respawn_naked/dress_up (малая, проверенная в тест-мире):
            // дефолт 100-160м даёт слишком долгий бег (видел FAIL "не закончил одевание за 40с,
            // статус=бежит к надгробию" — тест проверяет ДАННЫЕ для HUD, а не реальный бег).
            float oldDelay = CompanionDeath.RespawnDelay.Value;
            float oldMin = CompanionDeath.RespawnMinDistance.Value;
            float oldSpread = CompanionDeath.RespawnDistanceSpread.Value;
            float oldPause = CompanionDeath.DressPause.Value;
            CompanionDeath.RespawnDelay.Value = 3f;
            CompanionDeath.RespawnMinDistance.Value = 5f;
            CompanionDeath.RespawnDistanceSpread.Value = 5f;
            CompanionDeath.DressPause.Value = 0.4f;
            try
            {
                var c = SelfTest.SpawnCompanion(2f, 0f);
                if (c == null) { SelfTest.Fail("не призвался"); yield break; }
                yield return new WaitForSeconds(2.5f); // SetupOnce выдаёт набор
                if (c.Body == null) { SelfTest.Fail("боец пропал до смерти"); yield break; }
                string name = c.Name;

                // "Как было" — тем же способом, что и сама плашка у живого бойца.
                var equipBefore = CompanionInventory.SummarizeEquipped(c.Body);
                var bagBefore = CompanionInventory.Summarize(c.Body).Items;
                if (equipBefore.Count == 0 && bagBefore.Count == 0) { SelfTest.Fail("у бойца пусто перед смертью — тест не проверит вещи"); yield break; }

                c.Body.SetHealth(0f);
                var died = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return c.Body == null; }, 5f, died);
                if (!died.Ok) { SelfTest.Fail("боец не умер за 5с"); yield break; }

                // Фаза 1: "мёртв · возрождение через Nс" — данные из очереди (Pending), FollowerAI ещё нет.
                CompanionDeath.Pending pending = null;
                foreach (var p in CompanionDeath.Queue) if (p.Name == name) pending = p;
                if (pending == null) { SelfTest.Fail("нет записи в очереди возрождения"); yield break; }
                string statusDead = CompanionDeath.StatusText(pending);
                if (!statusDead.Contains("мёртв")) { SelfTest.Fail("статус мёртвого не похож на 'мёртв': " + statusDead); yield break; }

                List<CompanionInventory.ItemSum> equipSnap; CompanionInventory.Summary bagSnap;
                CompanionDeath.InventorySnapshot(pending, out equipSnap, out bagSnap);
                string diff = CompareItems("надето(мёртв)", equipBefore, equipSnap);
                if (diff == null) diff = CompareItems("рюкзак(мёртв)", bagBefore, bagSnap != null ? bagSnap.Items : null);
                if (diff != null) { SelfTest.Fail(diff); yield break; }

                // Фаза 2: возродился голым — те же данные должны перейти в RevivalState живого бойца.
                FollowerAI revived = null;
                var spawned = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate
                {
                    foreach (var f in FollowerAI.All) if (f != null && f.Name == name) { revived = f; return true; }
                    return false;
                }, 15f, spawned);
                if (!spawned.Ok || revived == null) { SelfTest.Fail("не возродился за 15с"); yield break; }
                if (revived.Revival == null) { SelfTest.Fail("возродился без Revival — снимок не проверить"); yield break; }
                diff = CompareItems("надето(возродился)", equipBefore, revived.Revival.EquipSnapshot);
                if (diff == null) diff = CompareItems("рюкзак(возродился)", bagBefore, revived.Revival.BagSnapshot != null ? revived.Revival.BagSnapshot.Items : null);
                if (diff != null) { SelfTest.Fail(diff); yield break; }

                // Статус должен смениться на фазу бега/забора/одевания (не застыть на "мёртв").
                var moved = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate
                {
                    return revived.Status.Contains("беж") || revived.Status.Contains("забира") || revived.Status.Contains("одева") || revived.Status.Contains("увож");
                }, 20f, moved);
                string statusRunning = revived.Status;
                if (!moved.Ok) { SelfTest.Fail("статус не сменился за 20с, остался: " + statusRunning); yield break; }

                // Фаза 3: полностью одет — Revival снят, вещи сверяем с реальным телом ("как обычно").
                var dressed = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return revived.Revival == null; }, 30f, dressed);
                if (!dressed.Ok) { SelfTest.Fail("не закончил одевание за 30с, статус=" + revived.Status); yield break; }
                if (revived.Body == null) { SelfTest.Fail("боец пропал во время одевания"); yield break; }
                var equipAfter = CompanionInventory.SummarizeEquipped(revived.Body);
                diff = CompareItems("надето(одет)", equipBefore, equipAfter);
                if (diff != null) { SelfTest.Fail(diff); yield break; }

                SelfTest.Pass("фазы: '" + statusDead + "' -> '" + statusRunning + "' -> одет; вещи совпали на всех фазах");
            }
            finally
            {
                CompanionDeath.RespawnDelay.Value = oldDelay;
                CompanionDeath.RespawnMinDistance.Value = oldMin;
                CompanionDeath.RespawnDistanceSpread.Value = oldSpread;
                CompanionDeath.DressPause.Value = oldPause;
            }
        }

        // По именам и суммарному количеству (нескольким стакам одного имени — сумма). null-список
        // считается ошибкой (БАГ Ильи — "по I ничего не видно" — именно так и выглядел).
        private static string CompareItems(string label, List<CompanionInventory.ItemSum> expected, List<CompanionInventory.ItemSum> actual)
        {
            if (actual == null) return label + ": снимок пуст (null)";
            var exp = new Dictionary<string, int>();
            foreach (var it in expected) exp[it.Name] = (exp.ContainsKey(it.Name) ? exp[it.Name] : 0) + it.Count;
            var act = new Dictionary<string, int>();
            foreach (var it in actual) act[it.Name] = (act.ContainsKey(it.Name) ? act[it.Name] : 0) + it.Count;
            foreach (var kv in exp)
            {
                int a = 0; act.TryGetValue(kv.Key, out a);
                if (a != kv.Value) return label + ": " + kv.Key + " было=" + kv.Value + " снимок=" + a;
            }
            foreach (var kv in act)
            {
                if (!exp.ContainsKey(kv.Key)) return label + ": лишний предмет в снимке " + kv.Key + "=" + kv.Value;
            }
            return null;
        }

        // Пауза на шаге показа ≥floor секунд, даже если реальное действие (бег/одевание) закончилось
        // раньше — иначе Илья не успеет нажать I и посмотреть (ТЗ: "пауза на каждом шаге 10-15с").
        private static IEnumerator PadTo(float since, float floor)
        {
            float left = floor - (Time.time - since);
            if (left > 0f) yield return new WaitForSeconds(left);
        }

        // ПОКАЗ ДЛЯ ИЛЬИ (demo_death): медленно, с крупными подписями по центру экрана, ~2-3 мин.
        // Та же механика, что и в death_tombstone/respawn_naked/dress_up (SetHealth(0) — надёжно,
        // не зависит от боя), но без сжатия таймингов — специально "понятно глазами".
        private static IEnumerator DemoDeath()
        {
            float oldDelay = CompanionDeath.RespawnDelay.Value;
            float oldMin = CompanionDeath.RespawnMinDistance.Value;
            float oldSpread = CompanionDeath.RespawnDistanceSpread.Value;
            float oldPause = CompanionDeath.DressPause.Value;
            object oldDetail = Plugin.DetailTarget;
            // 15с таймер — буквально то, что увидит Илья в статусе; дистанция — та же, что уже
            // проверена в respawn_naked (10м), чтобы не зависеть от террейна; пауза одевания крупная,
            // чтобы каждую надетую вещь было видно отдельно (кит — 2 предмета: дубина+щит).
            CompanionDeath.RespawnDelay.Value = 15f * Plugin.DevDemoPace.Value;
            CompanionDeath.RespawnMinDistance.Value = 10f;
            CompanionDeath.RespawnDistanceSpread.Value = 10f;
            CompanionDeath.DressPause.Value = 6f;
            try
            {
                var c = SelfTest.SpawnCompanion(3f, 0f);
                if (c == null) { SelfTest.Fail("не призвался"); yield break; }
                string name = c.Name;
                Plugin.DetailTarget = c; // подробности (I) сразу на экране — показ не требует, чтобы Илья сам жал I
                yield return new WaitForSeconds(2.5f);
                if (c.Body == null) { SelfTest.Fail("боец пропал до смерти"); yield break; }
                int itemsBefore = c.Body.GetInventory().NrOfItems();

                yield return SelfTest.Caption("ПОКАЗ: смерть и возрождение бойца (1/7)\nБоец призван со стартовым набором. Нажми I — видно его вещи.", 12f * Plugin.DevDemoPace.Value);

                c.Body.SetHealth(0f);
                var died = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return c.Body == null; }, 5f, died);
                if (!died.Ok) { SelfTest.Fail("боец не умер за 5с"); yield break; }

                CompanionDeath.Pending pending = null;
                foreach (var p in CompanionDeath.Queue) if (p.Name == name) pending = p;
                if (pending == null) { SelfTest.Fail("нет записи в очереди возрождения"); yield break; }
                int tombItems = -1;
                if (pending.Tomb != null)
                {
                    var cont = pending.Tomb.GetComponent<Container>();
                    if (cont != null) tombItems = cont.GetInventory().NrOfItems();
                }
                Plugin.DetailTarget = pending; // старая ссылка (c) умерла — подробности на записи в очереди

                yield return SelfTest.Caption("(2/7) Погиб. Надгробие с его вещами стоит на месте смерти,\nметка появилась на карте. Нажми I — вещи те же, что были.", 12f * Plugin.DevDemoPace.Value);

                float t0 = Time.time;
                SelfTest.ShowCaption("(3/7) Ждём возрождение — таймер в статусе бойца на плашке\n(«мёртв · возрождение через …с»), всего ~" + (int)(15f * Plugin.DevDemoPace.Value) + " секунд.", 17f * Plugin.DevDemoPace.Value);
                yield return new WaitForSeconds(15f * Plugin.DevDemoPace.Value);
                yield return PadTo(t0, 12f * Plugin.DevDemoPace.Value);

                FollowerAI revived = null;
                var spawned = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate
                {
                    foreach (var f in FollowerAI.All) if (f != null && f.Name == name) { revived = f; return true; }
                    return false;
                }, 15f, spawned);
                if (!spawned.Ok || revived == null) { SelfTest.Fail("не возродился за 15+15с"); yield break; }
                Plugin.DetailTarget = revived; // та же ссылка живёт через бег/одевание/"как обычно" — повторно наводить не нужно

                yield return SelfTest.Caption("(4/7) Ожил ГОЛЫМ — без вещей. Respawn без базы ищет точку\nнедалеко от тебя (фаза 1); сейчас побежит к надгробию.", 12f);

                t0 = Time.time;
                SelfTest.ShowCaption("(5/7) Бежит к надгробию за своими вещами — статус на плашке:\n«бежит к надгробию» (или «увожу врага…», если рядом угроза).", 40f * Plugin.DevDemoPace.Value);
                var arrived = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return revived.Revival == null || revived.Revival.Stage != CompanionDeath.Phase.Running; }, 60f, arrived);
                if (!arrived.Ok) { SelfTest.Fail("не добежал до надгробия за 60с, статус=" + revived.Status); yield break; }
                yield return PadTo(t0, 12f * Plugin.DevDemoPace.Value);

                t0 = Time.time;
                SelfTest.ShowCaption("(6/7) У надгробия одевается по одной вещи с паузой.\nНажми I: счётчик «одевается N/M» растёт на глазах.", 40f * Plugin.DevDemoPace.Value);
                var dressed = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return revived.Revival == null; }, 60f, dressed);
                if (!dressed.Ok) { SelfTest.Fail("не закончил одевание за 60с, статус=" + revived.Status); yield break; }
                if (revived.Body == null) { SelfTest.Fail("боец пропал во время одевания"); yield break; }
                yield return PadTo(t0, 12f * Plugin.DevDemoPace.Value);

                int itemsAfter = revived.Body.GetInventory().NrOfItems();
                yield return SelfTest.Caption("(7/7) Готово! Все вещи на месте: " + itemsAfter + " из " + itemsBefore + ".\nБоец снова в строю, одет как прежде.", 10f * Plugin.DevDemoPace.Value);
                SelfTest.ClearCaption();
                yield return SelfTest.Caption("ПОКАЗ ЗАКОНЧЕН", 5f * Plugin.DevDemoPace.Value);

                string info = "было=" + itemsBefore + " в надгробии=" + tombItems + " после одевания=" + itemsAfter;
                if (itemsAfter < itemsBefore) { SelfTest.Fail("часть вещей потерялась | " + info); yield break; }
                SelfTest.Pass(info);
            }
            finally
            {
                CompanionDeath.RespawnDelay.Value = oldDelay;
                CompanionDeath.RespawnMinDistance.Value = oldMin;
                CompanionDeath.RespawnDistanceSpread.Value = oldSpread;
                CompanionDeath.DressPause.Value = oldPause;
                Plugin.DetailTarget = oldDetail;
                SelfTest.ClearCaption();
            }
        }
    }
}

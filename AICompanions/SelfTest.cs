using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AICompanions
{
    // Автотесты прямо в игре. Dev.SelfTest = "smoke,combat_basic" → после входа в тестовый мир
    // сценарии идут по очереди, каждый пишет в лог «[SELFTEST] имя PASS|FAIL|INVALID детали».
    // В конце «[SELFTEST] ALL DONE»; при Dev.SelfTestQuit игра закрывается сама (освобождает DLL).
    // Запуск снаружи: bash selftest.sh "smoke,combat_basic"
    public static class SelfTest
    {
        public const float DefaultTimeout = 240f;

        private class Entry { public Func<IEnumerator> Run; public float Timeout; public bool ExpectTimeout; }
        private static readonly Dictionary<string, Entry> Scenarios = new Dictionary<string, Entry>();
        private static bool _started;
        private static float _sinceLoad;
        private static string _current;
        private static int _idx, _total;
        // Для оверлея клона: какой сценарий идёт сейчас и который по счёту
        public static string CurrentName { get { return _current ?? ""; } }
        public static string NowRunning()
        {
            if (string.IsNullOrEmpty(_current)) return "";
            return _current + " (" + _idx + "/" + _total + ")";
        }
        private static string _verdict;

        public static void Register(string name, Func<IEnumerator> run, float timeout)
        {
            Scenarios[name] = new Entry { Run = run, Timeout = timeout };
        }

        public static void Register(string name, Func<IEnumerator> run)
        {
            Register(name, run, DefaultTimeout);
        }

        // Только для проверки самого стенда (StandTests): сценарий, который ОБЯЗАН упереться в таймаут —
        // тогда это PASS, а вернул ли стенд всё на место, проверяет следующий сценарий (stand_clean_check).
        public static void RegisterTimeoutProbe(string name, Func<IEnumerator> run, float timeout)
        {
            Scenarios[name] = new Entry { Run = run, Timeout = timeout, ExpectTimeout = true };
        }

        public static void Log(string msg) { Plugin.Log.LogInfo("[SELFTEST] " + _current + " " + msg); }
        public static void Pass(string details) { Verdict("PASS", details); }
        public static void Fail(string details) { Verdict("FAIL", details); }
        // Третий исход (26.10): СЦЕНА НЕ СЛОЖИЛАСЬ — проверяемая ситуация не случилась (враг не атаковал, n мало,
        // боец умер до начала проверки). Это не «фича сломана», а «тест не поставил сцену» — чинить тест.
        public static void Invalid(string details) { Verdict("INVALID", details); }
        // Предусловие сцены: `if (!SelfTest.Need(n >= 100, "ударов врага n=" + n + " < 100")) yield break;`
        public static bool Need(bool ok, string whatMissing)
        {
            if (!ok) Invalid(whatMissing);
            return ok;
        }

        // ---- крупная подпись по центру экрана для показов (demo_*) ----
        // Общий помощник: сценарий зовёт `yield return SelfTest.Caption("текст", 12f)` — подпись
        // появляется и держится ровно столько секунд (сценарий в это время стоит и ждёт, можно жать
        // I и смотреть). ShowCaption/ClearCaption — без ожидания, для шагов с переменной длительностью
        // (бег/одевание): подпись держим на весь шаг, а реальное условие ждём отдельным WaitUntil.
        private static string _captionText;
        private static float _captionUntil;
        private static GUIStyle _captionStyle, _captionShadow, _captionBox;

        public static void ShowCaption(string text, float seconds)
        {
            _captionText = text;
            _captionUntil = Time.time + Mathf.Max(0f, seconds);
            Log("CAPTION " + text); // лог по каждой подписи (не только Caption()) — снимки по шагам снаружи ловят момент по этой строке
        }

        public static void ClearCaption() { _captionText = null; }

        public static IEnumerator Caption(string text, float seconds)
        {
            ShowCaption(text, seconds);
            yield return new WaitForSeconds(seconds);
        }

        // Снимок окна клона в ключевой момент: строку «SHOT …» ловит tools/autoshot.sh (testrun.sh) и
        // снимает окно; сценарий стоит, пока снимок делается (powershell ~2-3 с).
        public static IEnumerator Shot(string what)
        {
            Log("SHOT " + what);
            yield return new WaitForSeconds(3.5f);
        }

        // Вызывается из Plugin.OnGUI (уже под масштабом IMGUI) — верх экрана по центру, свободен
        // от плашки бойцов (та справа под миникартой). Крупный читаемый шрифт — для окна-четверти.
        public static void DrawCaption(float width, float height)
        {
            if (string.IsNullOrEmpty(_captionText) || Time.time >= _captionUntil) return;
            if (_captionStyle == null)
            {
                _captionStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter, wordWrap = true };
                _captionStyle.normal.textColor = Color.white;
                _captionShadow = new GUIStyle(_captionStyle);
                _captionShadow.normal.textColor = new Color(0f, 0f, 0f, 0.9f);
                // Плотный фон: через стандартный полупрозрачный box просвечивали подписи кругов (снимок demo_skills 26.10)
                var bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.88f));
                bg.Apply();
                _captionBox = new GUIStyle(GUI.skin.box);
                _captionBox.normal.background = bg;
            }
            // БАГ (ревью по скрину): фикс.высота 110 обрезала 3-ю строку снизу, а ширина 86% экрана
            // заезжала на панель бойцов (справа под миникартой). Уже: ширина — ровно под текст (не
            // шире окна), высота — по CalcHeight (сколько строк ни вышло), ниже верхнего края экрана.
            const float padX = 16f, padY = 10f;
            float w = Mathf.Min(width * 0.6f, 760f);
            float innerW = w - padX * 2f;
            float textH = _captionStyle.CalcHeight(new GUIContent(_captionText), innerW);
            float h = textH + padY * 2f;
            float y = Mathf.Max(60f, height * 0.09f);
            var r = new Rect((width - w) / 2f, y, w, h);
            GUI.Box(r, GUIContent.none, _captionBox);
            var inner = new Rect(r.x + padX, r.y + padY, innerW, textH);
            GUI.Label(new Rect(inner.x + 2f, inner.y + 2f, inner.width, inner.height), _captionText, _captionShadow);
            GUI.Label(inner, _captionText, _captionStyle);
        }

        private static void Verdict(string v, string details)
        {
            if (_verdict != null) return;
            _verdict = v;
            Plugin.Log.LogInfo("[SELFTEST] " + _current + " " + v + " " + details);
        }

        public static void OnWorldLeft() { _sinceLoad = 0f; }

        private static bool _running;
        private static bool _inRunAll;
        public static bool IsRunning { get { return _inRunAll; } }

        // Запуск по команде загрузчика «selftest a,b» — в уже загруженном мире, без выхода из игры.
        public static void RunNow(string list)
        {
            if (string.IsNullOrEmpty(list) || _running || Plugin.Instance == null)
            {
                Plugin.Log.LogWarning("[SELFTEST] RunNow пропущен (пусто/уже идёт/нет плагина)");
                return;
            }
            _running = true;
            _started = true;
            Plugin.Instance.StartCoroutine(RunNowWrapper(list));
        }

        private static IEnumerator RunNowWrapper(string list)
        {
            yield return RunAll(list);
            _running = false;
        }

        public static void Tick(MonoBehaviour host, float dt)
        {
            string list = Plugin.DevSelfTest.Value;
            if (_started || string.IsNullOrEmpty(list) || !DevTools.InTestWorld()) return;
            _sinceLoad += dt;
            if (_sinceLoad < 6f) return;
            _started = true;
            host.StartCoroutine(RunAll(list));
        }

        private static IEnumerator RunAll(string list)
        {
            RegisterBuiltIns();
            Plugin.Log.LogInfo("[SELFTEST] BEGIN " + list + " (v" + Plugin.Version + ")");
            // Команда selftest может прийти, пока мир ещё грузится (selftest.sh видит «AutoStart: мир» раньше, чем появится
            // игрок) — тогда Ahead() падал с NullReference и logcheck включал стоп-кран. Ждём игрока и перенос клона
            // на открытое место (DevTools.CloneView), до 90 с.
            float waitView = 0f;
            while ((Player.m_localPlayer == null || !DevTools.CloneViewReady) && waitView < 90f) { waitView += 0.5f; yield return new WaitForSeconds(0.5f); }
            if (Player.m_localPlayer == null)
            {
                Plugin.Log.LogInfo("[SELFTEST] запуск FAIL игрок так и не появился в мире за 90с — прогон отменён");
                Plugin.Log.LogInfo("[SELFTEST] ALL DONE");
                yield break;
            }
            // Щадим ноутбук: на время автотестов не больше 30 кадров/с (бой и тайминги от FPS не зависят).
            _inRunAll = true;
            int prevFps = Application.targetFrameRate;
            Application.targetFrameRate = 30;
            _idx = 0;
            _total = 0;
            foreach (var raw in list.Split(',')) if (raw.Trim() != "") _total++;
            foreach (var raw in list.Split(','))
            {
                string name = raw.Trim();
                if (name == "") continue;
                _idx++;
                _current = name;
                _verdict = null;
                Entry e;
                if (!Scenarios.TryGetValue(name, out e)) { Fail("неизвестный сценарий"); continue; }
                Log("START");
                ResetArena("перед");
                var cfg = SnapshotConfig();
                yield return new WaitForSeconds(1f);
                IEnumerator it = null;
                try { it = e.Run(); } catch (Exception ex) { Fail("EXCEPTION " + ex); }
                // Вложенные корутины (yield return Caption(...)/WaitUntil(...)) крутим сами, стеком: так таймаут
                // проверяется на каждом шаге, а при таймауте/исключении Dispose доходит до КАЖДОГО уровня —
                // срабатывают их finally (сценарий возвращает ночь/конфиг/бессмертие, как задумал автор).
                var stack = new Stack<IEnumerator>();
                if (it != null) stack.Push(it);
                float t0 = Time.time;
                bool timedOut = false;
                while (stack.Count > 0)
                {
                    if (Time.time - t0 > e.Timeout) { timedOut = true; break; }
                    var top = stack.Peek();
                    object cur = null;
                    bool done = false;
                    try
                    {
                        if (!top.MoveNext()) done = true;
                        else cur = top.Current;
                    }
                    catch (Exception ex) { Fail("EXCEPTION " + ex); break; }
                    // Как у Unity: «yield return вложенная корутина» стоит минимум кадр, даже если она кончилась
                    // сразу (WaitUntil с уже выполненным условием). Без этого цикл сценария вида
                    // while(…) { yield return WaitUntil(уже да); } крутился в одном кадре вечно — игра зависала
                    // (death_naked_dodge 26.10 12:39: лог клона встал на 27 мин).
                    if (done) { stack.Pop(); if (stack.Count > 0) yield return null; continue; }
                    var nested = cur as IEnumerator;
                    if (nested != null) { stack.Push(nested); continue; }
                    yield return cur;
                }
                int disposed = DisposeAll(stack);
                if (timedOut)
                {
                    if (e.ExpectTimeout) Pass("таймаут " + (int)e.Timeout + "с сработал как задумано, закрыто уровней корутин: " + disposed);
                    else Fail("TIMEOUT " + (int)e.Timeout + "с (закрыто уровней корутин: " + disposed + ", их finally выполнены)");
                }
                if (_verdict == null) Fail("сценарий закончился без вердикта");
                int restored = RestoreConfig(cfg);
                if (restored > 0) Log("стенд: конфиг возвращён как до сценария, настроек: " + restored);
                ClearCaption();
                ResetArena("после");
                yield return new WaitForSeconds(1f);
            }
            _current = "";
            Application.targetFrameRate = prevFps;
            _inRunAll = false;
            Plugin.Log.LogInfo("[SELFTEST] ALL DONE");
            if (Plugin.DevSelfTestQuit.Value)
            {
                yield return new WaitForSecondsRealtime(3f);
                Application.Quit();
            }
        }

        private static int DisposeAll(Stack<IEnumerator> stack)
        {
            int n = 0;
            while (stack.Count > 0)
            {
                var d = stack.Pop() as IDisposable;
                if (d == null) continue;
                try { d.Dispose(); n++; } catch (Exception ex) { Log("стенд: Dispose корутины: " + ex.Message); }
            }
            return n;
        }

        // Конфиг (все Config.Bind мода) — снимок до сценария и возврат после: сценарий, упавший по таймауту
        // или исключению до своего «вернуть как было», не оставляет следующему RespawnDelay=999 и т.п.
        private static Dictionary<ConfigEntryBase, string> SnapshotConfig()
        {
            var snap = new Dictionary<ConfigEntryBase, string>();
            if (Plugin.Instance == null) return snap;
            foreach (var en in Plugin.Instance.Config.GetConfigEntries())
                if (en != null) snap[en] = en.GetSerializedValue();
            return snap;
        }

        private static int RestoreConfig(Dictionary<ConfigEntryBase, string> snap)
        {
            int n = 0;
            foreach (var kv in snap)
            {
                try
                {
                    if (kv.Key.GetSerializedValue() == kv.Value) continue;
                    kv.Key.SetSerializedValue(kv.Value);
                    n++;
                }
                catch (Exception ex) { Log("стенд: не вернул " + kv.Key.Definition + ": " + ex.Message); }
            }
            return n;
        }

        // Одинаковый старт каждого сценария (Илья 26.10): нет бойцов, очереди возрождения, надгробий,
        // голых/одевающихся, мобов и лута от прошлого; в клоне — полдень и ясно; хозяин как в конфиге
        // (Dev.GodMode — бессмертен и невидим), полное HP и стамина, без эффектов (мокрый/горит/яд).
        public static void ResetArena(string when)
        {
            CompanionManager.DismissAll();
            CompanionDeath.ResetForTest();
            int mobs = CleanupArena(ArenaRadius);
            int tombs = RemoveCompanionTombs(ArenaRadius);
            int junk = RemoveLooseJunk(ArenaRadius);
            if (Plugin.CloneView()) DevTools.ForceDaylight();
            var me = Me;
            if (me != null && DevTools.InTestWorld())
            {
                bool god = Plugin.DevGodMode.Value;
                me.SetGodMode(god);
                me.SetGhostMode(god);
                if (!me.IsDead())
                {
                    me.SetHealth(me.GetMaxHealth());
                    me.AddStamina(me.GetMaxStamina());
                }
                if (me.GetSEMan() != null) me.GetSEMan().RemoveAllStatusEffects(true);
            }
            Log("стенд " + when + ": арена очищена (мобов " + mobs + ", надгробий бойцов " + tombs + ", лута/снарядов/тел " + junk
                + ")" + (Plugin.CloneView() ? ", полдень и ясно" : "") + TakeNotes());
        }

        // Радиус уборки между сценариями: голый боец возрождается в 100-160 м и уводит врага к надгробию —
        // 80 м мало, хвосты возвращались в следующий сценарий. Дальше ~150-200 м игра зоны не держит.
        public const float ArenaRadius = 300f;

        private static readonly System.Reflection.MethodInfo TombOwnerMethod = HarmonyLib.AccessTools.Method(typeof(TombStone), "GetOwner");

        // Надгробия бойцов (владелец — отрицательный id, см. CompanionDeath.NextFakeOwnerId), в т.ч. сироты
        // после горячей перезагрузки (Shutdown не трогает надгробия). Надгробие игрока не трогаем.
        public static int RemoveCompanionTombs(float radius)
        {
            if (Me == null || ZNetScene.instance == null || !DevTools.InTestWorld() || TombOwnerMethod == null) return 0;
            int n = 0;
            foreach (var ts in Object.FindObjectsOfType<TombStone>())
            {
                if (ts == null || Vector3.Distance(ts.transform.position, Me.transform.position) > radius) continue;
                long owner;
                try { owner = (long)TombOwnerMethod.Invoke(ts, null); } catch { continue; }
                if (owner >= 0) continue;
                DestroyNet(ts.gameObject);
                n++;
            }
            return n;
        }

        // Лут на земле (бойцы его подбирают — иначе бегут за хвостами прошлого боя), летящие снаряды, тела.
        public static int RemoveLooseJunk(float radius)
        {
            if (Me == null || ZNetScene.instance == null || !DevTools.InTestWorld()) return 0;
            int n = 0;
            var pos = Me.transform.position;
            var junk = new List<Component>();
            junk.AddRange(Object.FindObjectsOfType<ItemDrop>());
            junk.AddRange(Object.FindObjectsOfType<Projectile>());
            junk.AddRange(Object.FindObjectsOfType<Ragdoll>());
            foreach (var j in junk)
            {
                if (j == null) continue;
                float dist = Vector3.Distance(j.transform.position, pos);
                if (dist > radius) continue;
                Note(j.gameObject, dist);
                DestroyNet(j.gameObject);
                n++;
            }
            return n;
        }

        // Чужой ZDO (нет владельца — загружен из сейва вдали) ZNetScene.Destroy НЕ удаляет: объект тут же
        // создаётся заново (прогон stand 26.10 11:14: «удалено 20» перед каждым сценарием, а они снова тут).
        // Поэтому сначала забираем владение — в тестовом мире одиночной игры это безопасно.
        private static bool DestroyNet(GameObject go)
        {
            var nv = go.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid())
            {
                if (!nv.IsOwner()) nv.ClaimOwnership();
                ZNetScene.instance.Destroy(go);
            }
            else Object.Destroy(go);
            return true;
        }

        // Что именно убрали — в лог (проверяющий видит, хвост это прошлого сценария или мир).
        private static readonly Dictionary<string, int> _noted = new Dictionary<string, int>();
        private static float _notedMin = float.MaxValue, _notedMax;

        private static void Note(GameObject go, float dist)
        {
            string nm = go.name.Replace("(Clone)", "").Trim();
            int k; _noted.TryGetValue(nm, out k); _noted[nm] = k + 1;
            _notedMin = Mathf.Min(_notedMin, dist); _notedMax = Mathf.Max(_notedMax, dist);
        }

        private static string TakeNotes()
        {
            if (_noted.Count == 0) return "";
            var parts = new List<string>();
            foreach (var kv in _noted) parts.Add(kv.Key + "×" + kv.Value);
            string r = " [" + string.Join(", ", parts.ToArray()) + "; " + (int)_notedMin + "–" + (int)_notedMax + " м]";
            _noted.Clear(); _notedMin = float.MaxValue; _notedMax = 0f;
            return r;
        }

        // Между сценариями убираем всех живых не-игроков и не-спутников в 80м (мобы из прошлого сценария
        // иначе «перетекают» в следующий и ломают его). Работает только в тестовом мире.
        public static int CleanupArena()
        {
            return CleanupArena(80f);
        }

        public static int CleanupArena(float radius)
        {
            if (Me == null || ZNetScene.instance == null || !DevTools.InTestWorld()) return 0;
            int n = 0;
            foreach (var c in new List<Character>(Character.GetAllCharacters()))
            {
                if (c == null || c is Player || FollowerAI.Registry.ContainsKey(c)) continue;
                float d = Vector3.Distance(c.transform.position, Me.transform.position);
                if (d > radius) continue;
                Note(c.gameObject, d);
                DestroyNet(c.gameObject);
                n++;
            }
            if (n > 0) Log("уборка арены: удалено существ " + n);
            return n;
        }

        // ---- помощники для сценариев ----

        public static Player Me { get { return Player.m_localPlayer; } }

        // Для сценариев «защита хозяина»: временно снять бессмертие/невидимость (Dev.GodMode в клоне),
        // чтобы мобы реально атаковали игрока, а бойцы его защищали; в конце сценария — вернуть на true.
        public static void SetGhost(bool on)
        {
            if (Me == null) return;
            Me.SetGodMode(on);
            Me.SetGhostMode(on);
        }

        // Точка перед игроком на земле (offset: x вправо, z вперёд).
        public static Vector3 Ahead(float forward, float right)
        {
            var p = Me.transform;
            Vector3 pos = p.position + p.forward * forward + p.right * right + Vector3.up * 1f;
            float h;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(pos, out h)) pos.y = h + 0.3f;
            return pos;
        }

        public static FollowerAI SpawnCompanion(float forward, float right)
        {
            return CompanionManager.SpawnAt(Ahead(forward, right));
        }

        public static GameObject SpawnPrefab(string prefab, float forward, float right)
        {
            var p = ZNetScene.instance.GetPrefab(prefab);
            if (p == null) { Log("нет префаба " + prefab); return null; }
            return Object.Instantiate(p, Ahead(forward, right), Quaternion.LookRotation(-Me.transform.forward));
        }

        // То же, но сразу выставляет звёзды (0-2) мобу — без ручного mc.SetLevel(stars+1) в сценарии.
        public static GameObject SpawnPrefab(string prefab, float forward, float right, int stars)
        {
            var go = SpawnPrefab(prefab, forward, right);
            if (go != null)
            {
                var c = go.GetComponent<Character>();
                if (c != null) c.SetLevel(Mathf.Clamp(stars, 0, 2) + 1);
            }
            return go;
        }

        // Ждать условие не дольше timeout. Результат — в WaitResult.Ok после цикла.
        public class WaitResult { public bool Ok; }

        public static IEnumerator WaitUntil(Func<bool> cond, float timeout, WaitResult r)
        {
            float t0 = Time.time;
            while (Time.time - t0 < timeout)
            {
                bool ok = false;
                try { ok = cond(); } catch { }
                if (ok) { r.Ok = true; yield break; }
                yield return new WaitForSeconds(0.25f);
            }
            r.Ok = false;
        }

        // ---- базовые сценарии; этапы добавляют свои через Register в своих файлах ----

        private static bool _builtIns;

        private static void RegisterBuiltIns()
        {
            if (_builtIns) return;
            _builtIns = true;
            Register("smoke", Smoke, 60f);
            Register("combat_basic", CombatBasic, 90f);
            // Сценарии этапов регистрируются здесь же вызовом их Register-методов.
            StageTests.RegisterAll();
        }

        // Спутник в 12м доходит до игрока сам.
        private static IEnumerator Smoke()
        {
            var c = SpawnCompanion(12f, 0f);
            if (c == null) { Fail("не призвался"); yield break; }
            var r = new WaitResult();
            yield return WaitUntil(() => Vector3.Distance(c.transform.position, Me.transform.position) < 4f, 30f, r);
            float d = Vector3.Distance(c.transform.position, Me.transform.position);
            if (r.Ok) Pass("дошёл, дистанция " + d.ToString("0.0") + "м");
            else Fail("не дошёл за 30с, дистанция " + d.ToString("0.0") + "м");
        }

        // Спутник рядом, грейлинг в 12м: грейлинг должен умереть, спутник — выжить.
        private static IEnumerator CombatBasic()
        {
            var c = SpawnCompanion(2f, 2f);
            yield return new WaitForSeconds(2f);
            var mob = SpawnPrefab("Greyling", 12f, 0f);
            var mc = mob != null ? mob.GetComponent<Character>() : null;
            if (c == null || mc == null) { Fail("не создались участники"); yield break; }
            var r = new WaitResult();
            yield return WaitUntil(() => mc == null || mc.IsDead(), 60f, r);
            bool alive = c != null && c.Body != null && !c.Body.IsDead();
            string hp = alive ? ((int)c.Body.GetHealth()).ToString() : "мёртв";
            if (r.Ok && alive) Pass("грейлинг убит, HP бойца " + hp);
            else Fail("грейлинг " + (r.Ok ? "убит" : "жив") + ", боец " + hp);
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Общая таблица профилей атак (этап «Бой v2а»): для ЛЮБОГО Humanoid (враг или сам спутник) —
    // сколько секунд от начала замаха до удара (самообучение по факту, Harmony-хук на
    // Humanoid.OnAttackTrigger — то же, что раньше AttackTiming.cs, этот файл его заменяет и
    // расширяет геометрией) + геометрия зоны удара (поля Attack читаются прямо с объекта атаки,
    // учить не нужно — расшифровка в _research/cv2a_0.md §3).
    public static class AttackProfiles
    {
        private static readonly FieldInfo CurrentAttackField = AccessTools.Field(typeof(Humanoid), "m_currentAttack");
        private static readonly FieldInfo ChainLevelField = AccessTools.Field(typeof(Attack), "m_currentAttackCainLevel");
        private static readonly MethodInfo GetPrefabNameMethod = AccessTools.Method(typeof(ZNetView), "GetPrefabName");

        // BusyDuration — полная занятость СВОЕГО замаха (Start -> InAttack() true->false), в
        // отличие от Windup (Start -> реальный удар OnAttackTrigger) — см. _research/combatlab_design.md §1.
        private class Profile { public float Windup; public int Samples; public float BusyDuration; public int BusySamples; }

        // Ключ "новый это замах или тот же" — ссылка на объект Attack (игра сама клонирует новый
        // Attack на каждый StartAttack, _research/2b2fix_0.md), а не per-tracker edge-detection —
        // чинит race при 2+ спутниках на одной цели (combatlab_forensics.md §4, выброс 0.640с).
        private static readonly Dictionary<Character, KeyValuePair<Attack, float>> _starts = new Dictionary<Character, KeyValuePair<Attack, float>>();
        private static readonly Dictionary<string, Profile> _profiles = new Dictionary<string, Profile>();
        // Замахи, удар которых уже прилетел: враг в откате, новый «старт» по тому же Attack не регистрируем
        // (раньше после удара старт стирался, скан тут же записывал «новую атаку» с нуля → лишние кувырки/блоки).
        private static readonly HashSet<Attack> _hitDone = new HashSet<Attack>();

        public static bool IsHitDone(Attack atk) { return atk != null && _hitDone.Contains(atk); }
        private static bool _loaded;
        private static bool _dirty;
        private static float _saveTimer;

        // ---- тайминг замаха (самообучение) ----

        // Пишет новый старт ТОЛЬКО если atk отличается от уже сохранённого — т.е. момент старта
        // фиксируется один раз на реальный новый объект Attack, независимо от того, сколько раз и
        // когда это заметил каждый из трекеров (combatlab_design.md §2).
        public static void MarkAttackStart(Character enemy, Attack atk)
        {
            if (enemy == null || atk == null || _hitDone.Contains(atk)) return;
            if (_hitDone.Count > 256) _hitDone.Clear();
            KeyValuePair<Attack, float> existing;
            if (_starts.TryGetValue(enemy, out existing) && existing.Key == atk) return;
            _starts[enemy] = new KeyValuePair<Attack, float>(atk, Time.time);
        }

        public static void ClearStart(Character enemy)
        {
            if (enemy != null) _starts.Remove(enemy);
        }

        // >=0 — сколько секунд уже идёт текущий замах; -1 — замаха не зафиксировано.
        public static float TimeSinceStart(Character enemy)
        {
            KeyValuePair<Attack, float> v;
            if (enemy == null || !_starts.TryGetValue(enemy, out v)) return -1f;
            return Time.time - v.Value;
        }

        public static bool TryGetWindup(Character enemy, out float windup)
        {
            int n;
            return TryGetWindup(enemy, out windup, out n);
        }

        public static bool TryGetWindup(Character enemy, out float windup, out int samples)
        {
            windup = 0f; samples = 0;
            var body = enemy as Humanoid;
            Profile p;
            if (body == null || !_profiles.TryGetValue(KeyFor(body), out p)) return false;
            windup = p.Windup; samples = p.Samples;
            return true;
        }

        public static bool TryGetWindupByKey(string key, out float windup, out int samples)
        {
            windup = 0f; samples = 0;
            Profile p;
            if (key == null || !_profiles.TryGetValue(key, out p)) return false;
            windup = p.Windup; samples = p.Samples;
            return true;
        }

        // Полная занятость своей атаки (windup+удар+возврат оружия) — используется вместо Windup
        // там, где важно знать, когда СОВСЕМ освободится InAttack() (combatlab_design.md §1).
        public static bool TryGetBusyDurationByKey(string key, out float busy, out int samples)
        {
            busy = 0f; samples = 0;
            Profile p;
            if (key == null || !_profiles.TryGetValue(key, out p) || p.BusySamples <= 0) return false;
            busy = p.BusyDuration; samples = p.BusySamples;
            return true;
        }

        public static void RecordBusyDuration(string key, float measured)
        {
            if (key == null || measured <= 0.02f || measured > 8f) return;
            Profile p;
            if (!_profiles.TryGetValue(key, out p)) { p = new Profile(); _profiles[key] = p; }
            p.BusyDuration = p.BusySamples > 0 ? p.BusyDuration * 0.7f + measured * 0.3f : measured;
            p.BusySamples++;
            _dirty = true;
            Plugin.Log.LogInfo("[AttackProfiles] занятость(busy) " + key + ": " + p.BusyDuration.ToString("0.000")
                + "с (n=" + p.BusySamples + ", замер " + measured.ToString("0.000") + "с)");
        }

        // Консервативный дефолт для windup, когда профиль КОНКРЕТНОЙ атаки ещё не известен —
        // минимальный Windup среди уже откалиброванных (а не фиксированная константа): раньше
        // поднятый блок безопаснее, чем опоздавший (combatlab_design.md §4).
        public static float GetConservativeDefault()
        {
            float min = float.MaxValue;
            foreach (var kv in _profiles)
                if (kv.Value.Samples > 0 && kv.Value.Windup < min) min = kv.Value.Windup;
            return min < float.MaxValue ? min : Plugin.DefaultAttackWindup.Value;
        }

        public static string PrefabNameOf(Character c)
        {
            if (c == null) return "?";
            var nview = c.GetComponent<ZNetView>();
            if (nview != null && GetPrefabNameMethod != null)
            {
                try
                {
                    var n = GetPrefabNameMethod.Invoke(nview, null) as string;
                    if (!string.IsNullOrEmpty(n)) return n;
                }
                catch { }
            }
            return c.m_name;
        }

        public static Attack GetCurrentAttack(Humanoid body)
        {
            return body != null && CurrentAttackField != null ? CurrentAttackField.GetValue(body) as Attack : null;
        }

        // Постфикс Humanoid.OnAttackTrigger — момент реального хита. Срабатывает для ВСЕХ Humanoid
        // в мире (игрок, спутники, враги) — для большинства записи m_starts не будет, выходим сразу.
        public static void OnHit(Humanoid attacker)
        {
            var enemy = attacker as Character;
            if (enemy == null) return;
            var curAtk = GetCurrentAttack(attacker);
            if (curAtk != null) _hitDone.Add(curAtk);
            KeyValuePair<Attack, float> v;
            if (!_starts.TryGetValue(enemy, out v)) return;
            _starts.Remove(enemy);
            float measured = Time.time - v.Value;
            if (measured <= 0.02f || measured > 5f) return; // мусор/повторный хит без нового старта замаха

            string key = KeyFor(attacker);
            Profile p;
            if (!_profiles.TryGetValue(key, out p)) { p = new Profile(); _profiles[key] = p; }
            p.Windup = p.Samples > 0 ? p.Windup * 0.7f + measured * 0.3f : measured;
            p.Samples++;
            _dirty = true;
            Plugin.Log.LogInfo("[AttackProfiles] профиль " + key + ": " + p.Windup.ToString("0.000")
                + "с (n=" + p.Samples + ", замер " + measured.ToString("0.000") + "с)");
        }

        public static string KeyFor(Humanoid body)
        {
            string prefab = PrefabNameOf(body);
            var atk = GetCurrentAttack(body);
            string anim = atk != null ? atk.m_attackAnimation : "?";
            int chain = atk != null && ChainLevelField != null ? (int)ChainLevelField.GetValue(atk) : 0;
            return prefab + "/" + anim + "/" + chain;
        }

        // ---- геометрия зоны удара (апроксимация, _research/cv2a_0.md §3) ----

        // Приближённая проверка «точка point попадёт под этот удар», без повтора веера SphereCast
        // игры — конус (±m_attackAngle/2 от текущего "вперёд" атакующего, запас 2° на шаг веера 4°)
        // для Horizontal/Vertical, сфера на m_attackRange впереди для Area (там m_attackRange —
        // смещение центра взрыва, а не дальность руки, см. исследование).
        public static bool InDangerZone(Character attacker, Attack atk, Vector3 point)
        {
            if (attacker == null || atk == null) return false;
            Transform t = attacker.transform;
            Vector3 originBase = t.position + Vector3.up * atk.m_attackHeight + t.right * atk.m_attackOffset;

            if (atk.m_attackType == Attack.AttackType.Area)
            {
                Vector3 center = originBase + t.forward * atk.m_attackRange;
                return Vector3.Distance(point, center) <= Mathf.Max(0.3f, atk.m_attackRayWidth);
            }

            Vector3 toPoint = point - originBase;
            toPoint.y = 0f;
            float dist = toPoint.magnitude;
            if (dist > atk.m_attackRange + Mathf.Max(0f, atk.m_attackRayWidth) + 0.5f) return false;
            Vector3 fwd = t.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f || toPoint.sqrMagnitude < 0.0001f) return true;
            float angle = Vector3.Angle(fwd, toPoint);
            float half = atk.m_attackAngle > 0.1f ? atk.m_attackAngle / 2f : 60f; // угол неизвестен — консервативно широкий конус
            return angle <= half + 2f;
        }

        // ---- сохранение/загрузка таблицы (Combat.SaveAttackProfiles) ----

        private static string FilePath()
        {
            try { return Path.Combine(BepInEx.Paths.ConfigPath, "AICompanions.attackprofiles.txt"); }
            catch { return null; }
        }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            string path = FilePath();
            if (path == null || !File.Exists(path)) return;
            try
            {
                // ФИКС (найдено при доработке боя): старый парсинг брал LastIndexOf('=')/(';') и
                // резал подстроки в неверном порядке (i1<i2 всегда для формата "key=w;n", поэтому
                // "key"-подстрока включала "=w", а Substring(w) получал ОТРИЦАТЕЛЬНУЮ длину) —
                // Load() гарантированно бросал исключение на первой же валидной строке и тихо
                // (LogWarning) терял ВСЮ таблицу калибровки при каждом старте/reload. Отсюда часть
                // жалобы «не попадает в тайминги» — калибровка никогда не переживала перезапуск.
                // Новый формат читает по ПЕРВОМУ '=' (ключ без '=' гарантирован — префаб/анимация/
                // индекс через '/'), остаток — поля через ';' (3-е/4-е поля — BusyDuration/BusySamples,
                // опциональны для совместимости со старыми файлами).
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq);
                    string[] parts = line.Substring(eq + 1).Split(';');
                    if (parts.Length < 2) continue;
                    float w;
                    int n;
                    if (!float.TryParse(parts[0], out w)) continue;
                    if (!int.TryParse(parts[1], out n)) n = 1;
                    float busy = 0f; int bn = 0;
                    if (parts.Length >= 4)
                    {
                        float.TryParse(parts[2], out busy);
                        int.TryParse(parts[3], out bn);
                    }
                    _profiles[key] = new Profile { Windup = w, Samples = n, BusyDuration = busy, BusySamples = bn };
                }
                Plugin.Log.LogInfo("[AttackProfiles] загружено профилей: " + _profiles.Count + " из " + path);
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[AttackProfiles] загрузка не удалась: " + ex.Message); }
        }

        public static void Tick(float dt)
        {
            if (!_dirty) return;
            _saveTimer -= dt;
            if (_saveTimer > 0f) return;
            _saveTimer = 5f;
            Save();
        }

        public static void Save()
        {
            if (!Plugin.SaveAttackProfiles.Value) { _dirty = false; return; }
            string path = FilePath();
            if (path == null) return;
            try
            {
                var lines = new List<string>();
                foreach (var kv in _profiles)
                    lines.Add(kv.Key + "=" + kv.Value.Windup.ToString("0.000") + ";" + kv.Value.Samples
                        + ";" + kv.Value.BusyDuration.ToString("0.000") + ";" + kv.Value.BusySamples);
                File.WriteAllLines(path, lines.ToArray());
                _dirty = false;
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("[AttackProfiles] сохранение не удалось: " + ex.Message); }
        }

        // Выгрузка ядра (горячая перезагрузка) — дописать таблицу на диск, чтобы не терять
        // накопленную калибровку между reload.
        public static void Shutdown()
        {
            Save();
        }
    }

    [HarmonyPatch(typeof(Humanoid), "OnAttackTrigger")]
    static class EnemyAttackProfilePatch
    {
        static void Postfix(Humanoid __instance)
        {
            AttackProfiles.OnHit(__instance);
        }
    }

    // Точный момент старта замаха — ОТ ИГРЫ (Humanoid.StartAttack), а не polling ThreatTracker.
    // Единый источник правды для ЛЮБОГО Humanoid (враг или сам спутник) — чинит race §2 И заодно
    // впервые даёт калибровку windup/BusyDuration для СВОИХ атак спутника (ThreatTracker никогда
    // не сканирует спутника как угрозу самому себе — без этого хука профиль "свой_префаб/анимация/0"
    // не наполнялся бы вообще, см. combatlab_design.md §1 проблема Б).
    [HarmonyPatch(typeof(Humanoid), "StartAttack")]
    static class UniversalAttackStartPatch
    {
        static void Postfix(Humanoid __instance, bool __result)
        {
            if (!__result) return;
            var ch = __instance as Character;
            if (ch == null || ch is Player) return;
            var atk = AttackProfiles.GetCurrentAttack(__instance);
            AttackProfiles.MarkAttackStart(ch, atk);
        }
    }
}

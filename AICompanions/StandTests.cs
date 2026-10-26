using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Проверка самого стенда (SelfTest.RunAll/ResetArena, Илья 26.10): каждый сценарий стартует с одной и той же
    // чистой арены — полдень и ясно, нет бойцов, мёртвых в очереди, надгробий, голых, мобов и лута от прошлого,
    // конфиг как до сценария, хозяин как в Dev.GodMode; таймаут закрывает корутины сценария (их finally);
    // живой и мёртвый боец не делят цвет. Прогон по порядку:
    //   stand_dirty,stand_clean_check,stand_timeout_probe,stand_clean_check,stand_color_unique
    public static class StandTests
    {
        public static void Register()
        {
            SelfTest.Register("stand_dirty", Dirty, 90f);
            SelfTest.Register("stand_clean_check", CleanCheck, 60f);
            SelfTest.RegisterTimeoutProbe("stand_timeout_probe", TimeoutProbe, 10f);
            SelfTest.Register("stand_color_unique", ColorUnique, 90f);
        }

        // Что сделал прошлый сценарий — stand_clean_check проверяет, что стенд всё вернул.
        private static bool _dirtied, _probed, _outerFinally, _innerFinally;
        private static float _origRespawnDelay = -1f;
        private static int _origMax = -1;

        // Сорим как настоящие сценарии: боец погиб и бежит голым к надгробию, второй мёртв и ждёт
        // возрождения, мобы рядом и за прежним радиусом уборки (80 м), лут на земле, ночь и дождь,
        // конфиг изменён, хозяин уязвим и ранен.
        private static IEnumerator Dirty()
        {
            _dirtied = false;
            _origRespawnDelay = CompanionDeath.RespawnDelay.Value;
            _origMax = Plugin.MaxCompanions.Value;
            CompanionDeath.RespawnDelay.Value = 2f;
            var a = SelfTest.SpawnCompanion(3f, -1.5f);
            var b = SelfTest.SpawnCompanion(3f, 1.5f);
            if (a == null || b == null) { SelfTest.Fail("бойцы не призвались"); yield break; }
            yield return new WaitForSeconds(2.5f); // набор выдан — будет надгробие с вещами
            string aName = a.Name, bName = b.Name;
            a.Body.SetHealth(0f);
            var r = new SelfTest.WaitResult();
            // сначала дождаться смерти (OnDeath — следующим кадром), иначе «живым» считается ещё не умершее тело
            yield return SelfTest.WaitUntil(delegate { return InQueue(aName); }, 6f, r);
            if (!r.Ok) { SelfTest.Fail("боец " + aName + " не попал в очередь возрождения"); yield break; }
            yield return SelfTest.WaitUntil(delegate { return FindRevived(aName) != null; }, 20f, r);
            if (!r.Ok) { SelfTest.Fail("боец " + aName + " не возродился за 20с"); yield break; }

            CompanionDeath.RespawnDelay.Value = 999f;
            if (b.Body == null) { SelfTest.Fail("боец " + bName + " пропал раньше смерти"); yield break; }
            b.Body.SetHealth(0f);
            yield return SelfTest.WaitUntil(delegate { return InQueue(bName); }, 6f, r);
            if (!r.Ok) { SelfTest.Fail("боец " + bName + " не попал в очередь возрождения"); yield break; }

            SelfTest.SpawnPrefab("Skeleton", 25f, 0f);
            SelfTest.SpawnPrefab("Skeleton", 110f, 0f);
            SelfTest.SpawnPrefab("Wood", 4f, 2f);
            SelfTest.SpawnPrefab("Stone", 4f, -2f);
            Plugin.MaxCompanions.Value = 3;
            SelfTest.SetGhost(false);
            SelfTest.Me.SetHealth(SelfTest.Me.GetMaxHealth() * 0.5f);
            if (EnvMan.instance != null)
            {
                EnvMan.instance.m_debugTimeOfDay = true;
                EnvMan.instance.m_debugTime = 0f;
                EnvMan.instance.m_debugEnv = "Rain";
            }
            yield return new WaitForSeconds(3f);

            var rev = FindRevived(aName);
            string facts = "голый " + aName + "=" + (rev != null ? "бежит/одевается" : "нет")
                + ", в очереди=" + CompanionDeath.Queue.Count
                + ", надгробий бойцов=" + CountCompanionTombs(SelfTest.ArenaRadius)
                + ", мобов=" + CountMobs(SelfTest.ArenaRadius) + " (дальний ~110м)"
                + ", лута=" + CountDrops(SelfTest.ArenaRadius)
                + ", ночь=" + EnvMan.IsNight() + ", погода=" + EnvName()
                + ", RespawnDelay=" + CompanionDeath.RespawnDelay.Value + ", MaxCompanions=" + Plugin.MaxCompanions.Value
                + ", хозяин бессмертен=" + SelfTest.Me.InGodMode() + " HP=" + (int)(SelfTest.Me.GetHealthPercentage() * 100f) + "%";
            yield return SelfTest.Shot("насорено: " + facts);
            if (rev == null || CompanionDeath.Queue.Count == 0 || CountCompanionTombs(SelfTest.ArenaRadius) < 1 || CountMobs(SelfTest.ArenaRadius) < 2)
            {
                SelfTest.Fail("не удалось насорить как задумано | " + facts);
                yield break;
            }
            _dirtied = true;
            SelfTest.Pass("насорено | " + facts);
        }

        // Старт сразу после «грязного» сценария (или таймаута): всё должно быть как в начале сессии клона.
        private static IEnumerator CleanCheck()
        {
            var r = new SelfTest.WaitResult();
            // погода меняется с переходом (EnvMan.QueueEnvironment) — ждём, не дольше 10 с
            if (Plugin.CloneView()) yield return SelfTest.WaitUntil(delegate { return EnvName() == "Clear"; }, 10f, r);
            var bad = new List<string>();
            int alive = 0;
            foreach (var f in FollowerAI.All) if (f != null) alive++;
            int tombs = CountCompanionTombs(400f), mobs = CountMobs(SelfTest.ArenaRadius), drops = CountDrops(SelfTest.ArenaRadius);
            if (alive > 0) bad.Add("бойцов осталось " + alive);
            if (CompanionDeath.Queue.Count > 0) bad.Add("мёртвых в очереди " + CompanionDeath.Queue.Count);
            if (tombs > 0) bad.Add("надгробий бойцов " + tombs);
            if (mobs > 0) bad.Add("мобов в " + (int)SelfTest.ArenaRadius + "м " + mobs);
            if (drops > 0) bad.Add("лута на земле " + drops);
            float day = EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : -1f;
            if (Plugin.CloneView())
            {
                if (Mathf.Abs(day - 0.5f) > 0.02f) bad.Add("время суток " + day.ToString("0.00") + " (нужно 0.50 — полдень)");
                if (EnvName() != "Clear") bad.Add("погода " + EnvName() + " (нужно Clear)");
            }
            bool god = Plugin.DevGodMode.Value;
            if (SelfTest.Me.InGodMode() != god || SelfTest.Me.InGhostMode() != god)
                bad.Add("хозяин бессмертен=" + SelfTest.Me.InGodMode() + " невидим=" + SelfTest.Me.InGhostMode() + " (по конфигу " + god + ")");
            if (SelfTest.Me.GetHealthPercentage() < 0.99f) bad.Add("HP хозяина " + (int)(SelfTest.Me.GetHealthPercentage() * 100f) + "%");
            if (_origRespawnDelay >= 0f && !Mathf.Approximately(CompanionDeath.RespawnDelay.Value, _origRespawnDelay))
                bad.Add("RespawnDelay=" + CompanionDeath.RespawnDelay.Value + " (было " + _origRespawnDelay + ")");
            if (_origMax >= 0 && Plugin.MaxCompanions.Value != _origMax)
                bad.Add("MaxCompanions=" + Plugin.MaxCompanions.Value + " (было " + _origMax + ")");
            if (_probed && !_outerFinally) bad.Add("после таймаута не выполнен finally сценария");
            if (_probed && !_innerFinally) bad.Add("после таймаута не выполнен finally вложенной корутины");

            string after = _dirtied ? "после stand_dirty" : _probed ? "после таймаута stand_timeout_probe" : "без предыдущего грязного сценария";
            string facts = after + ": бойцов=" + alive + ", в очереди=" + CompanionDeath.Queue.Count + ", надгробий=" + tombs
                + ", мобов=" + mobs + ", лута=" + drops + ", время=" + day.ToString("0.00") + ", погода=" + EnvName()
                + ", хозяин бессмертен=" + SelfTest.Me.InGodMode() + ", RespawnDelay=" + CompanionDeath.RespawnDelay.Value
                + ", MaxCompanions=" + Plugin.MaxCompanions.Value
                + (_probed ? ", finally сценария=" + _outerFinally + " вложенной=" + _innerFinally : "");
            yield return SelfTest.Shot("чистая арена " + facts);
            bool wasDirty = _dirtied || _probed;
            _dirtied = false; _probed = false;
            if (!wasDirty) { SelfTest.Fail("перед проверкой не было stand_dirty/stand_timeout_probe — нечего проверять | " + facts); yield break; }
            if (bad.Count > 0) SelfTest.Fail(string.Join("; ", bad.ToArray()) + " | " + facts);
            else SelfTest.Pass(facts);
        }

        // Сценарий, который висит дольше своего таймаута (10 с) во ВЛОЖЕННОЙ корутине. Его finally
        // только ставят флаги — ночь, конфиг и уязвимость хозяина должен вернуть сам стенд.
        private static IEnumerator TimeoutProbe()
        {
            _probed = true; _outerFinally = false; _innerFinally = false;
            if (_origRespawnDelay < 0f) _origRespawnDelay = CompanionDeath.RespawnDelay.Value;
            if (_origMax < 0) _origMax = Plugin.MaxCompanions.Value;
            try
            {
                CompanionDeath.RespawnDelay.Value = 777f;
                SelfTest.SetGhost(false);
                if (EnvMan.instance != null)
                {
                    EnvMan.instance.m_debugTimeOfDay = true;
                    EnvMan.instance.m_debugTime = 0f;
                    EnvMan.instance.m_debugEnv = "Rain";
                }
                SelfTest.SpawnCompanion(3f, 0f);
                SelfTest.SpawnPrefab("Skeleton", 20f, 0f);
                yield return Hang();
            }
            finally { _outerFinally = true; }
        }

        private static IEnumerator Hang()
        {
            try { while (true) yield return new WaitForSeconds(1f); }
            finally { _innerFinally = true; }
        }

        // Мёртвый в очереди держит свой цвет и место в лимите; новый боец получает другой цвет;
        // возрождённый — снова свой, повторов нет ни в один момент.
        private static IEnumerator ColorUnique()
        {
            int oldMax = Plugin.MaxCompanions.Value;
            float oldDelay = CompanionDeath.RespawnDelay.Value, oldMin = CompanionDeath.RespawnMinDistance.Value, oldSpread = CompanionDeath.RespawnDistanceSpread.Value;
            try
            {
                CompanionDeath.RespawnDelay.Value = 10f;
                CompanionDeath.RespawnMinDistance.Value = 10f; // возродиться в кадре окна клона
                CompanionDeath.RespawnDistanceSpread.Value = 5f;
                var list = new List<FollowerAI>();
                for (int i = 0; i < 3; i++)
                {
                    var c = SelfTest.SpawnCompanion(3f, -2f + 2f * i);
                    if (c == null) { SelfTest.Fail("боец " + (i + 1) + " не призвался"); yield break; }
                    list.Add(c);
                }
                yield return new WaitForSeconds(2.5f);
                var dead = list[1];
                int deadColor = dead.ColorIndex;
                string deadName = dead.Name;
                dead.Body.SetHealth(0f);
                var r = new SelfTest.WaitResult();
                yield return SelfTest.WaitUntil(delegate { return InQueue(deadName); }, 6f, r);
                if (!r.Ok) { SelfTest.Fail(deadName + " не попал в очередь возрождения"); yield break; }

                var fresh = SelfTest.SpawnCompanion(3f, 4f);
                if (fresh == null) { SelfTest.Fail("новый боец не призвался при 2 живых + 1 мёртвом (лимит " + Plugin.MaxCompanions.Value + ")"); yield break; }
                string colorsNow = Colors();
                yield return SelfTest.Shot("цвета: 3 живых + мёртвый " + deadName + " в плашке — все разные: " + colorsNow);
                if (fresh.ColorIndex == deadColor) { SelfTest.Fail("новый " + fresh.Name + " получил цвет мёртвого " + deadName + " | " + colorsNow); yield break; }
                if (!Distinct()) { SelfTest.Fail("повтор цвета при мёртвом в очереди | " + colorsNow); yield break; }

                // лимит: 3 живых + 1 мёртвый = 4 места
                Plugin.MaxCompanions.Value = 4;
                var extra = SelfTest.SpawnCompanion(3f, 6f);
                bool limitOk = extra == null;
                if (extra != null) CompanionManager.Dismiss(extra);
                Plugin.MaxCompanions.Value = oldMax;
                if (!limitOk) { SelfTest.Fail("лимит 4 не учёл мёртвого: призвался 5-й | " + colorsNow); yield break; }

                yield return SelfTest.WaitUntil(delegate { return FindRevived(deadName) != null; }, 25f, r);
                var back = FindRevived(deadName);
                if (back == null) { SelfTest.Fail(deadName + " не возродился за 25с | " + Colors()); yield break; }
                string colorsAfter = Colors();
                yield return SelfTest.Shot("после возрождения " + deadName + " — свой цвет, повторов нет: " + colorsAfter);
                if (back.ColorIndex != deadColor) { SelfTest.Fail(deadName + " возродился другим цветом | " + colorsAfter); yield break; }
                if (!Distinct()) { SelfTest.Fail("повтор цвета после возрождения | " + colorsAfter); yield break; }
                SelfTest.Pass("пока мёртв: " + colorsNow + "; лимит 4 учёл мёртвого (5-й не призван); после возрождения: " + colorsAfter);
            }
            finally
            {
                Plugin.MaxCompanions.Value = oldMax;
                CompanionDeath.RespawnDelay.Value = oldDelay;
                CompanionDeath.RespawnMinDistance.Value = oldMin;
                CompanionDeath.RespawnDistanceSpread.Value = oldSpread;
            }
        }

        // ---- помощники ----

        private static FollowerAI FindRevived(string name)
        {
            foreach (var f in FollowerAI.All) if (f != null && f.Name == name && f.Body != null && !f.Body.IsDead()) return f;
            return null;
        }

        private static bool InQueue(string name)
        {
            foreach (var p in CompanionDeath.Queue) if (p.Name == name) return true;
            return false;
        }

        private static string Colors()
        {
            var parts = new List<string>();
            foreach (var f in FollowerAI.All) if (f != null && f.Body != null && !f.Body.IsDead()) parts.Add(f.Name + "=" + f.ColorIndex);
            foreach (var p in CompanionDeath.Queue) parts.Add(p.Name + "(мёртв)=" + p.ColorIndex);
            return string.Join(", ", parts.ToArray());
        }

        private static bool Distinct()
        {
            var seen = new HashSet<int>();
            foreach (var f in FollowerAI.All) if (f != null && f.Body != null && !f.Body.IsDead() && !seen.Add(f.ColorIndex)) return false;
            foreach (var p in CompanionDeath.Queue) if (!seen.Add(p.ColorIndex)) return false;
            return true;
        }

        private static readonly System.Reflection.MethodInfo TombOwner = HarmonyLib.AccessTools.Method(typeof(TombStone), "GetOwner");

        private static int CountCompanionTombs(float radius)
        {
            int n = 0;
            foreach (var ts in Object.FindObjectsOfType<TombStone>())
            {
                if (ts == null || Vector3.Distance(ts.transform.position, SelfTest.Me.transform.position) > radius) continue;
                long owner = 0;
                try { owner = (long)TombOwner.Invoke(ts, null); } catch { }
                if (owner < 0) n++;
            }
            return n;
        }

        private static int CountMobs(float radius)
        {
            int n = 0;
            foreach (var c in Character.GetAllCharacters())
                if (c != null && !(c is Player) && !FollowerAI.Registry.ContainsKey(c) && !c.IsDead()
                    && Vector3.Distance(c.transform.position, SelfTest.Me.transform.position) <= radius) n++;
            return n;
        }

        private static int CountDrops(float radius)
        {
            int n = 0;
            foreach (var d in Object.FindObjectsOfType<ItemDrop>())
                if (d != null && Vector3.Distance(d.transform.position, SelfTest.Me.transform.position) <= radius) n++;
            return n;
        }

        private static string EnvName()
        {
            var env = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment() : null;
            return env != null ? env.m_name : "?";
        }
    }
}

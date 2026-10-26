using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    public static class CompanionManager
    {
        public static List<FollowerAI> Alive()
        {
            FollowerAI.All.RemoveAll(f => f == null);
            return new List<FollowerAI>(FollowerAI.All);
        }

        public static void SpawnOne()
        {
            SpawnOne(2f);
        }

        public static void SpawnOne(float distanceAhead)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            SpawnAt(player.transform.position + player.transform.forward * distanceAhead + Vector3.up * 0.5f);
        }

        public static FollowerAI SpawnAt(Vector3 pos)
        {
            var player = Player.m_localPlayer;
            if (player == null) return null;
            var alive = Alive();
            if (alive.Count >= Plugin.MaxCompanions.Value)
            {
                player.Message(MessageHud.MessageType.TopLeft, "Лимит бойцов: " + Plugin.MaxCompanions.Value);
                return null;
            }
            var tpl = CompanionFactory.GetTemplate();
            if (tpl == null) return null;

            // Цвет уникален среди живых — первый свободный индекс палитры (9 цветов, ровно MaxCompanions).
            var usedColors = new HashSet<int>();
            foreach (var a in alive) usedColors.Add(a.ColorIndex);
            int colorIndex = 0;
            for (int i = 0; i < CompanionIdentity.Colors.Length; i++)
            {
                if (!usedColors.Contains(i)) { colorIndex = i; break; }
            }

            var rot = Quaternion.LookRotation(-player.transform.forward);
            var go = Object.Instantiate(tpl, pos, rot);
            var f = go.GetComponent<FollowerAI>();
            f.Init(player, colorIndex);

            player.Message(MessageHud.MessageType.TopLeft, f.Name + " призван");
            Plugin.Log.LogInfo("Призван " + f.Name + " в " + (int)Vector3.Distance(pos, player.transform.position) + "м");
            return f;
        }

        // ZNetScene.Destroy, а не Object.Destroy: иначе сетевой объект остаётся
        // в мире и игра воскрешает его — отсюда были «не пропадают / множатся».
        public static void Dismiss(FollowerAI c)
        {
            if (c == null) return;
            CompanionMapPins.Remove(c); // не ждать отложенный Object.Destroy — метка на карте убирается сразу
            if (ZNetScene.instance != null) ZNetScene.instance.Destroy(c.gameObject);
            else Object.Destroy(c.gameObject);
        }

        public static void DismissAll()
        {
            foreach (var c in Alive()) Dismiss(c);
            // Сброс круга классов (Tank->Universal->Archer->Fighter) — после полного отзыва следующий
            // призванный снова Танк (= старый общий набор по умолчанию), независимо от того, сколько
            // бойцов было призвано раньше в этой же игровой сессии (важно для автотестов: каждый
            // сценарий начинается с DismissAll, см. SelfTest.RunAll).
            FollowerAI.ResetClassRotation();
            if (Player.m_localPlayer != null)
                Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, "Бойцы отозваны");
        }

        public static void RecallAll()
        {
            foreach (var c in Alive()) c.Recall();
        }

        public static void SetStanceAll(FollowerAI.Stance mode)
        {
            foreach (var c in Alive()) c.SetStance(mode);
        }

        // Остатки прошлых тестов (до 0.3.0 сохранялись в мир): Хальдоры и ПРИРУЧЁННЫЕ волки.
        // Вызывается только в тестовом мире (DevTools).
        // Мобы, которых спавнят сценарии автотестов (SelfTest.SpawnPrefab/DevTools.SpawnTestMob) —
        // создаются прямым Object.Instantiate и остаются персистентными ZDO в сейве мира
        // тест_мода навсегда, если их не убить явно. includeTestMobs=true чистит и их —
        // только на самом первом проходе после загрузки (до старта сценариев), чтобы не
        // случайно снести моба, которого только что создал ТЕКУЩИЙ сценарий.
        // Список строим из ТЕКУЩЕГО Dev.TestMobPool/TestMobPrefab (а не хардкодим) — иначе при
        // правке пула (Plugin.cs) список чистки расходится сам по себе и новый моб из пула
        // навсегда остаётся в сейве (поймано: Draugr не чистился, пул содержал его, а этот
        // список — нет). Greyling/Troll — исторические имена старых пулов, их тоже чистим,
        // хотя нынешний код их уже не спавнит.
        private static readonly string[] LegacyTestMobPrefabs = { "Greyling", "Skeleton", "Troll" };

        private static IEnumerable<string> TestMobPrefabs()
        {
            foreach (var p in LegacyTestMobPrefabs) yield return p;
            if (Plugin.DevTestMobPrefab != null) yield return Plugin.DevTestMobPrefab.Value;
            string pool = Plugin.DevTestMobPool != null ? Plugin.DevTestMobPool.Value : null;
            if (!string.IsNullOrEmpty(pool))
            {
                foreach (var part in pool.Split(','))
                {
                    string name = part.Trim();
                    if (name != "") yield return name;
                }
            }
        }

        public static int PurgeOldTestJunk(float radius, bool includeTestMobs = false)
        {
            var player = Player.m_localPlayer;
            if (player == null || ZNetScene.instance == null) return 0;
            int n = 0;
            foreach (var nv in Object.FindObjectsOfType<ZNetView>())
            {
                if (nv == null || !nv.IsValid()) continue;
                string pn = nv.gameObject.name.Replace("(Clone)", "").Trim();
                bool junk = pn == "Haldor";
                if (pn == "Wolf")
                {
                    var c = nv.GetComponent<Character>();
                    junk = c != null && c.IsTamed();
                }
                if (!junk && includeTestMobs)
                {
                    foreach (var tm in TestMobPrefabs()) if (pn == tm) { junk = true; break; }
                }
                if (!junk || Vector3.Distance(nv.transform.position, player.transform.position) > radius) continue;
                ZNetScene.instance.Destroy(nv.gameObject);
                n++;
            }
            if (n > 0) Plugin.Log.LogInfo("Удалено старых тест-объектов: " + n);
            return n;
        }
    }
}

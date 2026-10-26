using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AICompanions
{
    // Шаблон спутника = клон префаба игрока:
    //  - Player/PlayerController убраны (иначе клон слушает клавиатуру),
    //  - вместо них Humanoid с теми же настройками тела, скоростей, анимаций,
    //  - MonsterAI (как у призванного скелета) ведёт его штатным pathfinding игры.
    // Итог: физика, шаги по склонам/камням и анимации — те же, что у игрока.
    public static class CompanionFactory
    {
        public const string PrefabName = "AICompanion";
        private const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        private static GameObject _holder;
        private static GameObject _template;
        private static ZNetScene _registeredIn;

        public static GameObject GetTemplate()
        {
            if (ZNetScene.instance == null) return null;
            if (_template == null) _template = Build();
            if (_template != null && _registeredIn != ZNetScene.instance) Register(_template);
            return _template;
        }

        // Горячая перезагрузка: убрать шаблон из ZNetScene, иначе новая сборка добавит дубликат префаба.
        public static void Shutdown()
        {
            if (ZNetScene.instance != null && _registeredIn == ZNetScene.instance && _template != null)
            {
                ZNetScene.instance.m_prefabs.Remove(_template);
                var f = typeof(ZNetScene).GetField("m_namedPrefabs", BindingFlags.NonPublic | BindingFlags.Instance);
                var named = f != null ? f.GetValue(ZNetScene.instance) as Dictionary<int, GameObject> : null;
                if (named != null) named.Remove(PrefabName.GetStableHashCode());
            }
            if (_holder != null) Object.Destroy(_holder);
            _holder = null;
            _template = null;
            _registeredIn = null;
        }

        private static GameObject Build()
        {
            var playerPrefab = ZNetScene.instance.GetPrefab("Player");
            if (playerPrefab == null && Game.instance != null) playerPrefab = Game.instance.m_playerPrefab;
            if (playerPrefab == null) { Plugin.Log.LogError("Нет префаба Player"); return null; }

            // Неактивный родитель: Awake у клона не вызывается, пока мы его собираем.
            _holder = new GameObject("AICompanions_Templates");
            _holder.SetActive(false);
            Object.DontDestroyOnLoad(_holder);

            var tpl = Object.Instantiate(playerPrefab, _holder.transform);
            tpl.name = PrefabName;

            var player = tpl.GetComponent<Player>();
            var hum = tpl.AddComponent<Humanoid>();
            CopyFields(typeof(Character), player, hum);
            CopyFields(typeof(Humanoid), player, hum);
            // Вещи — свои (стартовый набор префаба: факел, тряпки, плащ), не копия хозяина.
            // Уникальный набор под класс — этап 4.
            hum.m_name = "Боец";
            Plugin.Log.LogInfo("Тело бойца: faction=" + hum.m_faction + " group='" + hum.m_group + "' aiSkipTarget=" + hum.m_aiSkipTarget
                + " layer=" + LayerMask.LayerToName(tpl.layer));
            hum.m_aiSkipTarget = false; // мобы должны видеть спутника целью, как игрока
            hum.m_group = "";

            // Этап «Навыки как у игрока»: снять m_increseStep по каждому SkillType с НАСТОЯЩЕГО
            // Skills-компонента игрока, пока он тут есть — своя система (CompanionSkills) растит
            // навык спутника по той же формуле и с теми же шагами, что и у игрока.
            CompanionSkills.CaptureSkillDefs(tpl.GetComponent<Skills>());

            // Сначала зависимые от Player компоненты, потом сам Player.
            foreach (var typeName in new[] { "PlayerController", "Skills", "Player" })
            {
                foreach (var c in tpl.GetComponents<Component>())
                    if (c != null && c.GetType().Name == typeName) Object.DestroyImmediate(c);
            }
            if (tpl.GetComponent<Player>() != null) Plugin.Log.LogError("Player не удалился из шаблона");

            var ai = tpl.AddComponent<MonsterAI>();
            var src = SourceAI();
            if (src != null)
            {
                CopyFields(typeof(BaseAI), src, ai);
                CopyFields(typeof(MonsterAI), src, ai);
            }
            // Чужие звуки (скелет) не нужны.
            foreach (var t in new[] { typeof(BaseAI), typeof(MonsterAI) })
                foreach (var f in t.GetFields(Fields))
                    if (f.FieldType == typeof(EffectList)) f.SetValue(ai, new EffectList());
            ConfigureDefender(ai);

            var nview = tpl.GetComponent<ZNetView>();
            if (nview != null) nview.m_persistent = false; // не сохраняется в мир → нет дублей после перезахода

            tpl.AddComponent<FollowerAI>();

            var comps = new List<string>();
            foreach (var c in tpl.GetComponents<Component>()) if (c != null) comps.Add(c.GetType().Name);
            Plugin.Log.LogInfo("Шаблон бойца собран: " + string.Join(", ", comps.ToArray())
                + " | ИИ скопирован с: " + (src != null ? src.name : "нет, дефолт"));
            return tpl;
        }

        private static MonsterAI SourceAI()
        {
            foreach (var n in new[] { "Skeleton_Friendly", "Dverger", "Skeleton" })
            {
                var p = ZNetScene.instance.GetPrefab(n);
                var ai = p != null ? p.GetComponent<MonsterAI>() : null;
                if (ai != null) return ai;
            }
            return null;
        }

        // Этап 2б: видит врагов в радиусе защиты; кого именно бить — решает FollowerAI.WantsToFight.
        // Не жрёт, не прыгает без нужды, не убегает при ранении.
        private static void ConfigureDefender(MonsterAI ai)
        {
            ai.m_viewRange = Plugin.DefendRadius.Value;
            ai.m_hearRange = Plugin.DefendRadius.Value;
            ai.m_fleeIfLowHealth = 0f;
            ai.m_fleeIfNotAlerted = false;
            ai.m_fleeIfHurtWhenTargetCantBeReached = false;
            ai.m_enableHuntPlayer = false;
            ai.m_attackPlayerObjects = false;
            ai.m_consumeItems = new List<ItemDrop>();
            ai.m_consumeSearchRange = 0f;
            ai.m_randomMoveRange = 0f;
            ai.m_jumpInterval = 0f;
            ai.m_afraidOfFire = false;
            ai.m_avoidFire = false;
            ai.m_pathAgentType = Pathfinding.AgentType.Humanoid;
        }

        private static void Register(GameObject tpl)
        {
            var scene = ZNetScene.instance;
            if (!scene.m_prefabs.Contains(tpl)) scene.m_prefabs.Add(tpl);
            var f = typeof(ZNetScene).GetField("m_namedPrefabs", BindingFlags.NonPublic | BindingFlags.Instance);
            var named = f != null ? f.GetValue(scene) as Dictionary<int, GameObject> : null;
            if (named != null) named[PrefabName.GetStableHashCode()] = tpl;
            else Plugin.Log.LogWarning("ZNetScene.m_namedPrefabs не найден — префаб не зарегистрирован");
            _registeredIn = scene;
        }

        public static void CopyFields(Type t, object from, object to)
        {
            foreach (var f in t.GetFields(Fields))
            {
                try { f.SetValue(to, f.GetValue(from)); }
                catch (Exception e) { Plugin.Log.LogWarning("Поле " + t.Name + "." + f.Name + ": " + e.Message); }
            }
        }

        public static object GetField(object o, string name)
        {
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Fields);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }
    }
}

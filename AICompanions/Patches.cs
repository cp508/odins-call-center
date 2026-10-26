using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Пока открыто меню спутников: курсор свободен, персонаж и камера не реагируют на ввод.
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    static class PlayerControllerTakeInputPatch
    {
        static void Postfix(ref bool __result) { if (Plugin.MenuOpen) __result = false; }
    }

    [HarmonyPatch(typeof(Player), "TakeInput")]
    static class PlayerTakeInputPatch
    {
        static void Postfix(ref bool __result) { if (Plugin.MenuOpen) __result = false; }
    }

    [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
    static class MouseCapturePatch
    {
        static void Postfix()
        {
            if (!Plugin.MenuOpen) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    // Кого спутник считает врагом: только тех, кого решил бить FollowerAI.
    // Так ИИ игры не выбирает в цель зверей и далёких мобов, а не «выбирает и бросает».
    [HarmonyPatch(typeof(BaseAI), "IsEnemy", new[] { typeof(Character), typeof(Character) })]
    static class IsEnemyPatch
    {
        static void Postfix(Character a, Character b, ref bool __result)
        {
            if (!__result || a == null) return;
            FollowerAI f;
            if (FollowerAI.Registry.TryGetValue(a, out f) && !f.WantsToFight(b)) __result = false;
        }
    }

    // Свои своих не бьют: урон от спутника по тебе/другим спутникам и от тебя по спутнику отменяется.
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    static class NoFriendlyFirePatch
    {
        static bool Prefix(Character __instance, HitData hit)
        {
            var attacker = hit != null ? hit.GetAttacker() : null;
            if (attacker == null || attacker == __instance) return true;
            bool victimIsCompanion = FollowerAI.Registry.ContainsKey(__instance);
            bool attackerIsCompanion = FollowerAI.Registry.ContainsKey(attacker);
            if (attackerIsCompanion && (victimIsCompanion || __instance is Player)) return false;
            if (attacker is Player && victimIsCompanion) return false;
            return true;
        }
    }

    // Моб, которого ударил спутник, переключается на него, если спутник не дальше текущей цели.
    // В игре моб меняет цель только раз в 2 сек и только если цели нет — из-за этого «не отвечал».
    [HarmonyPatch(typeof(MonsterAI), "OnDamaged")]
    static class AggroOnCompanionPatch
    {
        static readonly System.Reflection.FieldInfo Target = AccessTools.Field(typeof(MonsterAI), "m_targetCreature");

        static void Postfix(MonsterAI __instance, Character attacker)
        {
            if (attacker == null || Target == null || !FollowerAI.Registry.ContainsKey(attacker)) return;
            var cur = Target.GetValue(__instance) as Character;
            if (cur == attacker) return;
            Vector3 me = __instance.transform.position;
            if (cur == null || Vector3.Distance(me, attacker.transform.position) <= Vector3.Distance(me, cur.transform.position) + 1f)
                Target.SetValue(__instance, attacker);
        }
    }

    // «Ко мне»: штатное следование идёт шагом, пока ближе 10м. В режиме отзыва — всегда бегом.
    [HarmonyPatch(typeof(BaseAI), "Follow")]
    static class FollowRunPatch
    {
        static readonly System.Reflection.MethodInfo MoveTo = AccessTools.Method(typeof(BaseAI), "MoveTo",
            new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) });

        static bool Prefix(BaseAI __instance, GameObject go, float dt)
        {
            FollowerAI f;
            if (go == null || MoveTo == null || !FollowerAI.ByAI.TryGetValue(__instance, out f)) return true;
            // «Ко мне» и бег к своему надгробию после возрождения (CompanionDeath) — всегда бегом.
            bool forceRun = f.Recalling || (f.Revival != null && f.Revival.Stage == CompanionDeath.Phase.Running);
            if (!forceRun) return true;
            MoveTo.Invoke(__instance, new object[] { dt, go.transform.position, 2.5f, true });
            return false;
        }
    }

    // Dev-режим: без вступительного ролика. TryPlayIntroCinematic сам — это просто "return
    // new <TryPlayIntroCinematic>d__10(...)" (14 байт IL), Mono инлайнит его в FejdStartup.Start —
    // патч на сам метод не срабатывает. Патчим MoveNext сгенерированного компилятором энумератора:
    // возвращаем false (корутина "как бы закончилась") на первом же тике, ролик не успевает начаться.
    [HarmonyPatch]
    static class SkipIntroPatch
    {
        static MethodBase TargetMethod()
        {
            var original = AccessTools.Method(typeof(FejdStartup), "TryPlayIntroCinematic");
            return AccessTools.EnumeratorMoveNext(original);
        }

        static bool Prefix(ref bool __result)
        {
            if (!Plugin.DevAutoStart.Value) return true;
            __result = false;
            return false;
        }
    }

    // Клон (AIC_INST): сейвы/мир — не через Steam Cloud (тот игнорирует -savedir и тащит РЕАЛЬНЫЕ
    // миры/персонажей Ильи), а строго локально из своей -savedir. FileHelpers.GetCloudStorageSupportedAndEnabled
    // — единая точка, откуда Utils.GetSaveDataPath решает Cloud или Local; гасим Cloud → всегда Local
    // (m_saveDataOverride из -savedir). Включается только Dev.ForceLocalSaves (у Ильи — выключено).
    [HarmonyPatch(typeof(FileHelpers), "get_CloudStorageSupportedAndEnabled")]
    static class ForceLocalSavesPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (!Plugin.DevForceLocalSaves.Value) return true;
            __result = false;
            return false;
        }
    }

    // Клон (Dev.LowGraphics): минимальная дальность загрузки зон — меньше активных зон вокруг игрока
    // = меньше память (мобы/террейн/объекты только у бойцов рядом, тестам хватает ~40-60м).
    // m_simulationDistance — приватное поле, SimulationDistance.GetSimulationDistance(0) — самый низкий уровень.
    [HarmonyPatch(typeof(ZoneSystem), "Awake")]
    static class LowSimDistancePatch
    {
        static void Postfix(ZoneSystem __instance)
        {
            if (!Plugin.DevLowGraphics.Value) return;
            try
            {
                var sd = SimulationDistance.GetSimulationDistance(0);
                var f = typeof(ZoneSystem).GetField("m_simulationDistance", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) f.SetValue(__instance, sd);
                Plugin.Log.LogInfo("Dev.LowGraphics: SimulationDistance=0 (минимум)");
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("LowGraphics SimulationDistance: " + ex.Message); }
        }
    }

    // Спутник подбирает предмет, которым ещё не владеет: игра ставит отложенный PickupUpdate и там делает
    // (m_pickupRequester as Player).Pickup(...) — для спутника (Humanoid, не Player) это NullReference в логе
    // (IL ItemDrop.PickupUpdate), а проверка лога включает стоп-кран. Для спутника подбираем сами через Pickup(Humanoid):
    // он сам повторит запрос владения, пока предмет не станет нашим.
    [HarmonyPatch(typeof(ItemDrop), "PickupUpdate")]
    static class CompanionPickupUpdatePatch
    {
        static readonly FieldInfo RequesterField = AccessTools.Field(typeof(ItemDrop), "m_pickupRequester");

        static bool Prefix(ItemDrop __instance)
        {
            object raw = RequesterField.GetValue(__instance);
            if (raw == null || raw is Player) return true;
            __instance.CancelInvoke("PickupUpdate");
            var h = raw as Humanoid;
            if (h != null && !h.IsDead()) __instance.Pickup(h);
            return false;
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AICompanions
{
    // Точный момент удара врага прямо из его анимации: в клипах атак зашито событие
    // OnAttackTrigger/Hit — в этот момент игра и наносит урон. Позиция в клипе + время события
    // = сколько секунд до удара. Не зависит от того, поймали ли мы начало атаки (там были гонки).
    public static class AnimTiming
    {
        private static readonly FieldInfo AnimField =
            typeof(Character).GetField("m_animator", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly List<AnimatorClipInfo> Buf = new List<AnimatorClipInfo>(4);
        private static readonly Dictionary<AnimationClip, float[]> HitTimes = new Dictionary<AnimationClip, float[]>();

        private static float[] GetHitTimes(AnimationClip clip)
        {
            float[] t;
            if (HitTimes.TryGetValue(clip, out t)) return t;
            var list = new List<float>();
            foreach (var ev in clip.events) // .events копирует массив — поэтому кэш
                if (ev.functionName == "OnAttackTrigger" || ev.functionName == "Hit") list.Add(ev.time);
            list.Sort();
            t = list.ToArray();
            HitTimes[clip] = t;
            return t;
        }

        // true — время определено по анимации: tti = сек до следующего удара этого замаха,
        // float.MaxValue — ударов в этом замахе больше не будет (враг в откате = окно для атаки).
        // false — в анимации нет событий удара (лук и т.п.) — использовать профиль.
        public static bool TryTimeToHit(Character c, out float tti)
        {
            tti = float.MaxValue;
            var an = AnimField != null ? AnimField.GetValue(c) as Animator : null;
            if (an == null || !an.isActiveAndEnabled) return false;
            bool trans = an.IsInTransition(0);
            AnimatorStateInfo st = trans ? an.GetNextAnimatorStateInfo(0) : an.GetCurrentAnimatorStateInfo(0);
            Buf.Clear();
            if (trans) an.GetNextAnimatorClipInfo(0, Buf); else an.GetCurrentAnimatorClipInfo(0, Buf);
            AnimationClip clip = null;
            float bestW = -1f;
            float[] times = null;
            for (int i = 0; i < Buf.Count; i++)
            {
                var ci = Buf[i];
                if (ci.clip == null) continue;
                var ht = GetHitTimes(ci.clip);
                if (ht.Length == 0 || ci.weight <= bestW) continue;
                bestW = ci.weight;
                clip = ci.clip;
                times = ht;
            }
            if (clip == null || clip.length <= 0f) return false;
            float n = st.normalizedTime;
            float tIn = (st.loop ? n - Mathf.Floor(n) : Mathf.Min(n, 1f)) * clip.length;
            // st.length уже учитывает скорость аниматора/состояния (у тролля 1.6с клип → 1.067с при speed 1.5)
            float scale = st.length > 0.01f ? st.length / clip.length : 1f / Mathf.Max(0.05f, an.speed);
            for (int i = 0; i < times.Length; i++)
            {
                if (times[i] > tIn + 0.01f)
                {
                    tti = (times[i] - tIn) * scale;
                    break;
                }
            }
            return true;
        }

        public static void Shutdown() { HitTimes.Clear(); Buf.Clear(); }
    }
}

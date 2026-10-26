using System.Collections;
using UnityEngine;

namespace AICompanions
{
    // Автотесты задачи «модель стамины» (запрос Ильи 25.10, ветка stamina).
    public static class StaminaTests
    {
        public static void Register()
        {
            SelfTest.Register("stamina_regen_curve", RegenCurve, 120f);
            SelfTest.Register("stamina_policy", Policy, 110f);
        }

        private static readonly float[] Fractions = { 0.10f, 0.30f, 0.50f, 0.70f, 0.90f };
        private const float MeasureWindow = 0.4f; // короткое окно — реген нелинеен по текущей стамине, за 0.4с дрейф пренебрежим

        private class Measure { public float Actual; public float Expected; public float Err; }

        // reg = m_staminaRegen * (1 + m_staminaRegenTimeMultiplier * (1 - frac)) * Game.m_staminaRegenRate * SEMan
        // (CompanionStamina.Tick / IL Player.UpdateStats, stamina_il_1.txt) — без k (мы меряем стоя,
        // не в блоке/атаке/кувырке/заплыве). SEMan честно учтён (боевая проверка 25.10, full1: хозяин —
        // копия РЕАЛЬНОГО персонажа Ильи, у него бывают активные баффы на реген — без множителя тест
        // ловил ложный FAIL на хозяине >5% при чистом коде; на лёгком клоне эффектов не было, не
        // всплывало). Множитель считаем одинаково для хозяина и спутника — честно, не угадывая 1.
        private static float ExpectedRate(float regenBase, float timeMul, float frac, SEMan seman)
        {
            float rate = regenBase * (1f + timeMul * (1f - frac)) * Game.m_staminaRegenRate;
            float seMul = 1f;
            if (seman != null) { try { seman.ModifyStaminaRegen(ref seMul); } catch { seMul = 1f; } }
            return rate * seMul;
        }

        private static string WeaponName(FollowerAI c)
        {
            var w = c.Body != null ? c.Body.GetCurrentWeapon() : null;
            return w != null && w.m_shared != null ? w.m_shared.m_name : "?";
        }

        // Хозяина сажаем на нужный уровень штатным UseStamina/AddStamina (публичный API игры — тот
        // же путь, что реальная трата в бою), ждём m_staminaRegenDelay (задержка регена после ЛЮБОЙ
        // траты — иначе замер попадёт в окно "регена ещё нет" и ожидаемо даст 0) и меряем короткое окно.
        // OwnerTrials — боевая проверка full1 25.10: один замер на хозяине шумел до Δ6% на
        // full-клоне (много модов, нестабильный FPS — реальный RPC-путь UseStamina/GetStamina
        // чувствителен к этому), хотя спутник (прямое чтение поля) стабильно держал ~1%. НЕ драфт
        // (удлинять одно окно нельзя — формула нелинейна, подряд несколько окон накопят снос
        // фракции), а 3 НЕЗАВИСИМЫХ коротких замера с полным ресетом хозяина на тот же target
        // перед каждым — гасит разовый тайминг-шум усреднением, снос фракции не копится.
        private const int OwnerTrials = 3;

        private static IEnumerator MeasureOwner(Player me, float frac, Measure r)
        {
            float max = me.GetMaxStamina();
            float target = max * frac;
            float sumRate = 0f;
            float sumExpected = 0f;
            for (int i = 0; i < OwnerTrials; i++)
            {
                float cur = me.GetStamina();
                // ВСЕГДА заканчиваем настройку реальным UseStamina (подняв запас заранее при нужде):
                // по IL RPC_UseStamina это единственный путь, который взводит m_staminaRegenTimer=
                // m_staminaRegenDelay (stamina_il_rpcuse.txt:19-22). AddStamina таймер НЕ трогает
                // (stamina_il_use.txt:40-56) — если бы мы остановились на нём, реген мог уже идти,
                // и за ожидание delay+0.2 стамина успевала упереться в потолок до начала замера
                // (это и валило 90%-уровень: факт=0,00 — хозяин был уже на максимуме).
                if (cur < target + 0.5f) { me.AddStamina(target + 0.5f - cur); cur = target + 0.5f; }
                me.UseStamina(cur - target);
                yield return new WaitForSeconds(me.m_staminaRegenDelay + 0.2f);
                // ВАЖНО (найдено на этой же боевой проверке): за буфер 0.2с после истечения delay
                // реген уже чуть идёт — "start" тут НЕ равен номинальному target, а несколько выше
                // (регена успело накопиться). Expected считаем от РЕАЛЬНОГО start/max каждой попытки
                // (как и до OwnerTrials), а не от номинального frac — иначе Expected окажется
                // систематически завышен (frac чуть ниже истинного start → формула даёт более
                // высокий реген, чем на самом деле должен быть в точке замера), и усреднение по
                // трём попыткам стабильно покажет Actual<Expected на всех уровнях (ровно то, что
                // поймали первым прогоном этого фикса — не случайный шум, а systematic bias правки).
                float start = me.GetStamina();
                float t0 = Time.time;
                yield return new WaitForSeconds(MeasureWindow);
                float end = me.GetStamina();
                float dtReal = Mathf.Max(0.0001f, Time.time - t0);
                sumRate += (end - start) / dtReal;
                sumExpected += ExpectedRate(me.m_staminaRegen, me.m_staminaRegenTimeMultiplier, start / max, me.GetSEMan());
            }
            r.Actual = sumRate / OwnerTrials;
            r.Expected = sumExpected / OwnerTrials;
            r.Err = r.Expected > 0.001f ? Mathf.Abs(r.Actual - r.Expected) / r.Expected : 0f;
        }

        // Спутника сажаем напрямую (Stamina.Current — публичное поле) — задержку регена не трогаем
        // (не звали Use()), она и так была 0 (не в бою перед этим), мерить можно сразу.
        private static IEnumerator MeasureCompanion(Player me, FollowerAI c, float frac, Measure r)
        {
            c.Stamina.Current = c.Stamina.Max * frac;
            float start = c.Stamina.Current;
            float t0 = Time.time;
            yield return new WaitForSeconds(MeasureWindow);
            if (c.Body == null) { r.Actual = 0f; r.Expected = 0f; r.Err = 0f; yield break; }
            float end = c.Stamina.Current;
            float dtReal = Mathf.Max(0.0001f, Time.time - t0);
            r.Actual = (end - start) / dtReal;
            r.Expected = ExpectedRate(me.m_staminaRegen, me.m_staminaRegenTimeMultiplier, start / c.Stamina.Max, c.Stamina.Seman);
            r.Err = r.Expected > 0.001f ? Mathf.Abs(r.Actual - r.Expected) / r.Expected : 0f;
        }

        // 1) Снимаем реальные числа (конфиг стамины хозяина + стоимости кувырка/блока/удара спутника).
        // 2) На уровнях 10/30/50/70/90% максимума замеряем фактический реген хозяина И спутника,
        // сравниваем с формулой (допуск 5%, запрос Ильи — проверить числа в игре, не угадывать).
        private static IEnumerator RegenCurve()
        {
            var me = SelfTest.Me;
            if (me == null) { SelfTest.Fail("нет игрока"); yield break; }
            var c = SelfTest.SpawnCompanion(4f, 0f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }

            SelfTest.Log("конфиг: owner.m_staminaRegen=" + me.m_staminaRegen.ToString("0.00")
                + " m_staminaRegenTimeMultiplier=" + me.m_staminaRegenTimeMultiplier.ToString("0.00")
                + " m_staminaRegenDelay=" + me.m_staminaRegenDelay.ToString("0.00")
                + " m_dodgeStaminaUsage=" + me.m_dodgeStaminaUsage.ToString("0.00")
                + " Game.m_staminaRegenRate=" + Game.m_staminaRegenRate.ToString("0.00")
                + " | боец: кувырок=" + c.Dodge.Cost.ToString("0.00")
                + " блок=" + c.Stamina.BlockStaminaDrain.ToString("0.00")
                + " парир=" + c.Stamina.PerfectBlockStaminaDrain.ToString("0.00")
                + " удар(" + WeaponName(c) + ")=" + StaminaPolicy.AttackCost(c).ToString("0.00"));

            int failCount = 0;
            var report = new System.Text.StringBuilder();
            var ownerR = new Measure();
            var compR = new Measure();
            foreach (float frac in Fractions)
            {
                yield return MeasureOwner(me, frac, ownerR);
                yield return MeasureCompanion(me, c, frac, compR);
                if (c.Body == null) { SelfTest.Fail("боец пропал во время замера" + report); yield break; }
                // "0.1" в ToString — литерал (не формат!): "1" после точки не placeholder, значит
                // ВСЕГДА печаталась ровно цифра 1 после разделителя, маскируя реальный Δ (нашли на
                // расхождении факт/ожид, которое на глаз не билось с печатаемым Δ). Верно — "0.0".
                report.Append(" | " + Mathf.RoundToInt(frac * 100f) + "%: хозяин факт=" + ownerR.Actual.ToString("0.00")
                    + " ожид=" + ownerR.Expected.ToString("0.00") + " Δ=" + (ownerR.Err * 100f).ToString("0.0") + "%"
                    + " боец факт=" + compR.Actual.ToString("0.00") + " ожид=" + compR.Expected.ToString("0.00")
                    + " Δ=" + (compR.Err * 100f).ToString("0.0") + "%");
                if (ownerR.Err > 0.05f || compR.Err > 0.05f) failCount++;
            }

            // Не оставляем хозяина "ограбленным" после замеров.
            me.AddStamina(me.GetMaxStamina());

            string info = report.ToString();
            SelfTest.Log(info);
            if (failCount > 0) { SelfTest.Fail("расхождение >5% на " + failCount + " из " + Fractions.Length + " уровнях" + info); yield break; }
            SelfTest.Pass("все " + Fractions.Length + " уровней в допуске 5%" + info);
        }

        // Дуэль со Skeleton 2★ до 90с или до убийства: удары/мин (1-я/2-я половина — без просадки темпа
        // на меньшей стамине, правило Ильи «не ждать половины/полной») и случаи «не хватило стамины на
        // кувырок» (CompanionDodge.StaminaDenied, цель 0 — StaminaPolicy.Reserve должен не допускать этого).
        private static IEnumerator Policy()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            yield return new WaitForSeconds(1.5f);
            var mob = SelfTest.SpawnPrefab("Skeleton_NoArcher", 10f, 0f);
            if (mob == null) mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;
            if (mc == null) { SelfTest.Fail("нет префаба Skeleton"); yield break; }
            mc.SetLevel(2);

            const float duration = 90f;
            const float half = duration * 0.5f;
            int windowsAtHalf = -1;
            float t0 = Time.time;
            float killTime = -1f;
            while (Time.time - t0 < duration)
            {
                if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }
                if (windowsAtHalf < 0 && Time.time - t0 >= half) windowsAtHalf = c.OwnAttackWindows;
                if (mc == null || mc.IsDead()) { killTime = Time.time - t0; break; }
                yield return new WaitForSeconds(1f);
            }
            if (windowsAtHalf < 0) windowsAtHalf = c.OwnAttackWindows;

            float elapsed = Time.time - t0;
            bool alive = c.Body != null && !c.Body.IsDead();
            int totalAttacks = c.OwnAttackWindows;
            int secondHalfAttacks = totalAttacks - windowsAtHalf;
            float firstHalfMin = Mathf.Min(half, elapsed) / 60f;
            float secondHalfMin = Mathf.Max(0f, elapsed - half) / 60f;
            float apmFirst = firstHalfMin > 0.001f ? windowsAtHalf / firstHalfMin : 0f;
            float apmSecond = secondHalfMin > 0.001f ? secondHalfAttacks / secondHalfMin : 0f;
            float apmTotal = elapsed > 0.1f ? totalAttacks / (elapsed / 60f) : 0f;

            int safe, total;
            c.GetSafetyStats(out safe, out total);
            float safePct = total > 0 ? (float)safe / total * 100f : 100f;

            string info = "удары_всего=" + totalAttacks + " удары/мин=" + apmTotal.ToString("0.0")
                + " (1-я_половина=" + apmFirst.ToString("0.0") + " 2-я_половина=" + apmSecond.ToString("0.0") + ")"
                + " кувырок_не_хватило_стамины=" + c.Dodge.StaminaDenied
                + " безопасно=" + safePct.ToString("0") + "% (" + safe + "/" + total + ")"
                + " HP=" + (alive ? ((int)c.Body.GetHealth()).ToString() : "мёртв")
                + " скелетУбит=" + (killTime >= 0f ? killTime.ToString("0.0") + "с" : "нет");
            SelfTest.Log(info);
            if (!alive) { SelfTest.Fail("боец мёртв | " + info); yield break; }
            if (c.Dodge.StaminaDenied > 0) { SelfTest.Fail("не хватило стамины на кувырок (цель 0) | " + info); yield break; }
            // ВАЖНО (ревью этапа «Фикс»): этот тест НЕ проверяет планку защиты 98%/выборка≥100
            // (ZADACHI/ai-companions.md, ПЛАНКА ЗАЩИТЫ) — один скелет 2★ дохнет за 8-17с, выборка
            // total почти всегда 1-3 удара врага, порог любой величины на ней бессмыслен (шум, не
            // метрика). Планку держат parry_drill/troll_solo (там выборка достаточна). Здесь
            // safePct только в info для наглядности; stamina_policy проверяет СВОЁ: темп атаки
            // (удары/мин) и StaminaDenied (цель 0) — что и есть задача ветки stamina.
            if (totalAttacks < 3) { SelfTest.Fail("слишком мало своих ударов для оценки темпа | " + info); yield break; }
            SelfTest.Pass(info);
        }
    }
}

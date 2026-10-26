using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AICompanions
{
    // Автотесты этапа «Навыки как у игрока»: свой учёт Skills.SkillType за спутником (Skills-
    // компонент у него намеренно убран, CompanionFactory.Build) — рост от использования по
    // формуле игрока, персистентность за именем, реальное влияние на игру через перехват
    // Character.GetSkillFactor/GetSkillLevel/RaiseSkill (CombatPatches.cs, CompanionSkills.cs).
    public static class SkillsTests
    {
        public static void Register()
        {
            SelfTest.Register("skills_raise", SkillsRaise, 90f);
            SelfTest.Register("skills_persist", SkillsPersist, 60f);
            // Было 150с (суммарный урон за 25с — нестабильно). Теперь ≥100 попаданий на уровень
            // (планка брифа), сам бой дольше — таймаут поднят с запасом (_research/skills-2_journal.md).
            SelfTest.Register("skills_effect", SkillsEffect, 1100f);
            SelfTest.Register("skills_rate_parity", SkillsRateParity, 30f);
            SelfTest.Register("skills_death_keep", SkillsDeathKeep, 60f);
            SelfTest.Register("demo_skills", DemoSkills, 400f);
        }

        // Реальный шаг прироста (SkillDef.m_increseStep) у ЖИВОГО игрока — независимая проверка
        // (не из кэша CompanionSkills._increaseStep, снятого один раз в CaptureSkillDefs) для
        // skills_rate_parity. Skills.m_skills — публичный список (как в CompanionSkills.CaptureSkillDefs).
        private static float PlayerIncreaseStep(Skills.SkillType type)
        {
            var player = SelfTest.Me;
            var skills = player != null ? player.GetSkills() : null;
            if (skills == null || skills.m_skills == null) return -1f;
            foreach (var def in skills.m_skills)
                if (def != null && def.m_skill == type) return def.m_increseStep;
            return -1f;
        }

        // Против Skeleton 45с (респавн по гибели): удары оружием (кит по умолчанию — Club/Clubs)
        // и блоки щитом должны поднять навык. Старт — ЧИСТЫЙ 0 (SetLevel): стартовый уровень
        // нетронутого спутника = Plugin.CompanionSkillFactor*100 (~65) — его почти не сдвинуть за
        // 45с, см. формулу GetNextLevelRequirement в CompanionSkills (растёт как level^1.5).
        private static IEnumerator SkillsRaise()
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            c.SetName("НавыкТест_рост");
            CompanionSkills.ResetAll(c.Name);
            CompanionSkills.SetLevel(c.Name, Skills.SkillType.Clubs, 0f);
            CompanionSkills.SetLevel(c.Name, Skills.SkillType.Blocking, 0f);
            float clubsBefore = CompanionSkills.GetLevel(c.Name, Skills.SkillType.Clubs);
            float blockBefore = CompanionSkills.GetLevel(c.Name, Skills.SkillType.Blocking);
            yield return new WaitForSeconds(1.5f);

            var mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;

            float t0 = Time.time;
            while (Time.time - t0 < 45f)
            {
                if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }
                if (mc == null || mc.IsDead())
                {
                    mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
                    mc = mob != null ? mob.GetComponent<Character>() : null;
                }
                yield return new WaitForSeconds(1f);
            }

            float clubsAfter = CompanionSkills.GetLevel(c.Name, Skills.SkillType.Clubs);
            float blockAfter = CompanionSkills.GetLevel(c.Name, Skills.SkillType.Blocking);
            string info = "Дубины " + clubsBefore.ToString("0") + "->" + clubsAfter.ToString("0")
                + " | Блок " + blockBefore.ToString("0") + "->" + blockAfter.ToString("0")
                + " | урон_нанесён=" + c.DamageDealt.ToString("0") + " блоков=" + c.Blocks;
            if (clubsAfter <= clubsBefore && blockAfter <= blockBefore)
            {
                SelfTest.Fail("навык не вырос ни разу | " + info);
                yield break;
            }
            SelfTest.Pass(info);
        }

        // Уровень ставим напрямую (SetLevel) — рост от боя уже проверен в skills_raise, здесь
        // только хранение по имени: Dismiss/призыв, смерть, запись на диск (= переживает выход
        // из мира/горячую перезагрузку ядра — тот же файл читает CompanionSkills.Load()).
        private static IEnumerator SkillsPersist()
        {
            string name = "НавыкТест_память";
            CompanionSkills.ResetAll(name);
            CompanionSkills.SetLevel(name, Skills.SkillType.Clubs, 42f);

            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            c.SetName(name);
            yield return new WaitForSeconds(0.5f);
            float afterSpawn = CompanionSkills.GetLevel(name, Skills.SkillType.Clubs);

            // Dismiss/призыв — новый объект GameObject, то же имя.
            CompanionManager.Dismiss(c);
            yield return new WaitForSeconds(0.5f);
            var c2 = SelfTest.SpawnCompanion(2f, 2f);
            if (c2 == null) { SelfTest.Fail("не возродился после Dismiss"); yield break; }
            c2.SetName(name);
            float afterRespawn = CompanionSkills.GetLevel(name, Skills.SkillType.Clubs);

            // Смерть — реальный урон через Character.Damage (как в бою), не просто SetHealth
            // (та не триггерит OnDeath, см. IL skills_il10.txt).
            var hit = new HitData();
            hit.m_damage.m_damage = 99999f;
            hit.m_point = c2.Body.transform.position;
            hit.m_dir = Vector3.down;
            c2.Body.Damage(hit);
            yield return new WaitForSeconds(1.5f);
            float afterDeath = CompanionSkills.GetLevel(name, Skills.SkillType.Clubs);
            bool reallyDied = c2.Body == null || c2.Body.IsDead();

            // На диске — переживает перезагрузку мода/выход из мира (читается CompanionSkills.Load()
            // при следующем Awake; здесь проверяем сам файл, не перезапуская процесс).
            CompanionSkills.Save();
            string path = Path.Combine(BepInEx.Paths.ConfigPath, "AICompanions.companionskills.txt");
            bool onDisk = File.Exists(path)
                && File.ReadAllText(path).Contains(name + "|" + Skills.SkillType.Clubs + "=42");

            string info = "после_призыва=" + afterSpawn.ToString("0") + " после_Dismiss=" + afterRespawn.ToString("0")
                + " после_смерти=" + afterDeath.ToString("0") + " (погиб=" + reallyDied + ") на_диске=" + onDisk;
            if (afterSpawn != 42f || afterRespawn != 42f || afterDeath != 42f || !onDisk)
            {
                SelfTest.Fail(info);
                yield break;
            }
            SelfTest.Pass(info);
        }

        // Сравнение ПРИРОСТА (не реального фарма 10000 действий) за одно и то же действие у
        // игрока-хозяина и у бойца при одинаковом уровне, для каждого навыка, который боец может
        // поднимать (весовой, Блок, Прыжок, Бег, Плавание, Кувырок — см. «Итог этапа»,
        // _research/skills-2_status.md; остальные навыки у бойца сейчас физически недостижимы).
        // "Игрок" — ОТДЕЛЬНЫЙ одноразовый объект Skills+Skill (публичный класс, m_info/m_level/
        // m_accumulator — как Skills.SkillDef уже используется напрямую в CaptureSkillDefs) с
        // шагом прироста, снятым НЕЗАВИСИМО с живого Skills-компонента Ильи (PlayerIncreaseStep) —
        // настоящий сейв/прогресс Ильи НЕ трогаем ни читом, ни уроном. factor=0.01 — заведомо ниже
        // порога левелапа на уровне 50 при любом разумном Game.m_skillGainRate (не обрывает
        // аккумулятор до сравнения).
        private static IEnumerator SkillsRateParity()
        {
            // Гарантируем, что CompanionSkills.CaptureSkillDefs уже снял шаги с шаблона (обычно уже
            // снято предыдущими тестами/спавнами в сессии, но явный спавн — не завязываться на порядок).
            var warm = SelfTest.SpawnCompanion(2f, 2f);
            if (warm != null) { yield return new WaitForSeconds(0.3f); CompanionManager.Dismiss(warm); }

            var types = new[]
            {
                Skills.SkillType.Clubs, Skills.SkillType.Blocking, Skills.SkillType.Jump,
                Skills.SkillType.Run, Skills.SkillType.Swim, Skills.SkillType.Dodge,
            };
            const string name = "НавыкТест_паритет";
            const float level = 50f;
            const float factor = 0.01f;
            var lines = new List<string>();
            bool allOk = true;
            foreach (var type in types)
            {
                float realStep = PlayerIncreaseStep(type);
                if (realStep < 0f) { lines.Add(type + ":нет_SkillDef"); allOk = false; continue; }

                var def = new Skills.SkillDef();
                def.m_skill = type;
                def.m_increseStep = realStep;
                var skill = new Skills.Skill(def);
                skill.m_level = level;
                skill.m_accumulator = 0f;
                skill.Raise(factor);
                float playerGain = skill.m_accumulator;

                CompanionSkills.ResetAll(name);
                CompanionSkills.SetLevel(name, type, level);
                CompanionSkills.Raise(name, type, factor);
                float companionGain = CompanionSkills.GetAccumulator(name, type);

                float diffPct = playerGain > 0f ? Mathf.Abs(companionGain - playerGain) / playerGain * 100f : 100f;
                if (diffPct > 1f) allOk = false;
                lines.Add(type + " игрок=" + playerGain.ToString("0.00000") + " боец=" + companionGain.ToString("0.00000")
                    + " Δ=" + diffPct.ToString("0.00") + "%");
            }
            CompanionSkills.ResetAll(name);
            string info = string.Join(" | ", lines.ToArray());
            if (!allOk) { SelfTest.Fail(info); yield break; }
            SelfTest.Pass(info);
        }

        // Боец умирает реальным смертельным уроном (Character.Damage → OnDeath, не SetHealth) с
        // несколькими навыками на разных уровнях — все должны остаться БЕЗ ИЗМЕНЕНИЙ (в отличие от
        // игрока — Skills.OnDeath снимает level*m_DeathLowerFactor*Game.m_skillReductionRate, IL
        // _research/skills-2_journal.md). Для сравнения формула применяется к ОТДЕЛЬНОМУ числу
        // (playerClubsLevel), настоящий Skills-компонент Ильи не трогаем.
        private static IEnumerator SkillsDeathKeep()
        {
            const string name = "НавыкТест_смерть2";
            CompanionSkills.ResetAll(name);
            CompanionSkills.SetLevel(name, Skills.SkillType.Clubs, 37f);
            CompanionSkills.SetLevel(name, Skills.SkillType.Blocking, 61f);
            CompanionSkills.SetLevel(name, Skills.SkillType.Run, 20f);

            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { SelfTest.Fail("не призвался"); yield break; }
            c.SetName(name);
            yield return new WaitForSeconds(0.5f);

            float clubsBefore = CompanionSkills.GetLevel(name, Skills.SkillType.Clubs);
            float blockBefore = CompanionSkills.GetLevel(name, Skills.SkillType.Blocking);
            float runBefore = CompanionSkills.GetLevel(name, Skills.SkillType.Run);

            var hit = new HitData();
            hit.m_damage.m_damage = 99999f;
            hit.m_point = c.Body.transform.position;
            hit.m_dir = Vector3.down;
            c.Body.Damage(hit);
            yield return new WaitForSeconds(1.5f);
            bool died = c.Body == null || c.Body.IsDead();

            float clubsAfter = CompanionSkills.GetLevel(name, Skills.SkillType.Clubs);
            float blockAfter = CompanionSkills.GetLevel(name, Skills.SkillType.Blocking);
            float runAfter = CompanionSkills.GetLevel(name, Skills.SkillType.Run);

            // Штатная потеря игрока — ДЛЯ СРАВНЕНИЯ, не применяется ни к бойцу, ни к Илье.
            var realSkills = SelfTest.Me != null ? SelfTest.Me.GetSkills() : null;
            float deathFactor = realSkills != null ? realSkills.m_DeathLowerFactor * Game.m_skillReductionRate : -1f;
            float playerWouldBe = clubsBefore - clubsBefore * Mathf.Max(0f, deathFactor);

            string info = "боец Дубины " + clubsBefore.ToString("0") + "->" + clubsAfter.ToString("0")
                + " Блок " + blockBefore.ToString("0") + "->" + blockAfter.ToString("0")
                + " Бег " + runBefore.ToString("0") + "->" + runAfter.ToString("0")
                + " (погиб=" + died + ") | игрок_потеря=" + (deathFactor * 100f).ToString("0.0")
                + "% (Дубины_было_бы " + clubsBefore.ToString("0") + "->" + playerWouldBe.ToString("0.0") + ")";

            if (!died) { SelfTest.Fail("боец не умер | " + info); yield break; }
            if (clubsAfter != clubsBefore || blockAfter != blockBefore || runAfter != runBefore)
            { SelfTest.Fail("навык бойца изменился после смерти | " + info); yield break; }
            if (deathFactor <= 0f) { SelfTest.Fail("не удалось снять формулу потери игрока для сравнения | " + info); yield break; }
            SelfTest.Pass(info);
        }

        private class DmgResult { public float Dealt; public int Hits; }

        // Боец с именем skillName дерётся с респавнящимся Skeleton, навык Clubs выставлен level
        // ДО боя, пока не набьёт targetHits попаданий (план Ильи: «за удар, среднее по ≥100», а не
        // суммарный урон за фикс. время — тот был нестабилен, main: 0=176 100=153 FAIL) или не
        // истечёт maxDuration (страховка — отдельное «мало данных», не тихий провал по времени).
        // Иммунный к смерти «манекен» (HP завышен один раз после спавна) — первый прогон на
        // реально респавнящемся Skeleton (main) дал n=36/28 за 360с: большую часть времени съедает
        // не бой, а «убит → жди respawn → новый скелет заметил/добежал» (~1 удар/10с), а не сам
        // обмен ударами. Без цикла смерти измерение быстрее и стабильнее, формула урона за удар
        // та же (реальный Character.Damage/ApplyDamage, просто цель не умирает).
        private static IEnumerator MeasureDamage(string skillName, float level, int targetHits, float maxDuration, DmgResult result)
        {
            var c = SelfTest.SpawnCompanion(2f, 2f);
            if (c == null) { result.Dealt = -1f; yield break; }
            c.SetName(skillName);
            CompanionSkills.ResetAll(c.Name);
            CompanionSkills.SetLevel(c.Name, Skills.SkillType.Clubs, level);
            // Этот тест мерит ТОЛЬКО урон/удар (наступление) — не защиту (её мерят block_parry/
            // stamina_reserve). Манекен без смерти означает непрерывный бой дольше обычного, и
            // боец, который раньше гас между убийствами скелета, на такой длине реально умирал
            // (попытка 2 с бессмертной целью: n=8/32 — FAIL). Пробовал SetMaxHealth(1000000) на
            // бойце — не удержалось: CompanionFood.ApplyTotals() раз в 1с сама зовёт
            // _body.SetMaxHealth(hp) по еде (как у игрока) и ЗАТИРАЕТ наш лимит обратно до обычных
            // ~50-100 (попытка 3: n=28/75 — опять смерть бойца). Поэтому не трогаем MaxHealth —
            // каждую секунду цикла долечиваем бойца до текущего максимума (SetHealth), это не
            // конфликтует с CompanionFood и изолирует переменную «урон», не «выживание».
            yield return new WaitForSeconds(1f);

            var mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
            Character mc = mob != null ? mob.GetComponent<Character>() : null;
            if (mc != null) { mc.SetMaxHealth(1000000f); mc.SetHealth(1000000f); }
            float t0 = Time.time;
            int lastHits = c.HitsDealt;
            float lastProgress = Time.time;
            while (Time.time - t0 < maxDuration && c.HitsDealt < targetHits)
            {
                if (c.Body == null) break;
                c.Body.SetHealth(c.Body.GetMaxHealth());
                // КОРЕНЬ попытки 3 (0=29,4 100=26,6 — выше при НИЗКОМ навыке, подозрение на баг):
                // навык Clubs у бойца реально растёт от боя (CompanionRaiseSkillPatch — это и есть
                // цель всей ветки) — на длинном непрерывном бою «level0» не держится на 0, за
                // ~28 попаданий успевает подрасти (в main 45с/0->5 — в разы быстрее, чем кажется).
                // Сам путь урон←навык проверен по IL (CombatPatches.CompanionWeaponSkillDamagePatch,
                // Attack.ModifyDamage: порядок в DoMeleeAttack — GetRandomSkillFactor → ItemData.
                // GetDamage → ModifyDamage/наш Postfix ПОСЛЕДНИЙ, hitData.ApplyModifier(float) честно
                // умножает m_damage.* на месте — патч НЕ сломан (это и объясняет 4/4 PASS с большим
                // запасом на main в коротких 25с прогонах). Фикс — не чинить формулу, а держать
                // уровень ЗАФИКСИРОВАННЫМ на весь замер (как делают skills_persist/rate_parity).
                CompanionSkills.SetLevel(c.Name, Skills.SkillType.Clubs, level);
                if (c.HitsDealt != lastHits) { lastHits = c.HitsDealt; lastProgress = Time.time; }
                // КОРЕНЬ попытки 6 (0=25,19(n=25) 100=31,06(n=100) — level0 не успел набрать n за
                // maxDuration): Skeleton иногда спавнится С ЛУКОМ (ванильный рандом экипировки) и
                // кайтит — отходит на дистанцию и стреляет, а бессмертная цель (SetMaxHealth
                // 1000000) никогда не ловит "mc.IsDead()" respawn — боец простаивал («враги_рядом=0»
                // десятки секунд подряд в логе клона i2). Фикс: если ПОПАДАНИЙ нет ≥20с (не только
                // "цель умерла") — цель считаем потерянной, сносим и спавним свежую.
                bool stalled = Time.time - lastProgress > 20f;
                if (mc == null || mc.IsDead() || stalled)
                {
                    if (mob != null && ZNetScene.instance != null) ZNetScene.instance.Destroy(mob);
                    mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
                    mc = mob != null ? mob.GetComponent<Character>() : null;
                    if (mc != null) { mc.SetMaxHealth(1000000f); mc.SetHealth(1000000f); }
                    lastProgress = Time.time;
                }
                yield return new WaitForSeconds(1f);
            }
            result.Dealt = c.DamageDealt;
            result.Hits = c.HitsDealt;
            CompanionManager.Dismiss(c);
        }

        // 1) Сила блока: один и тот же щит, навык Blocking 0 → 100 — настоящий вызов
        //    ItemData.GetBlockPower через Character.GetSkillFactor (виртуальный метод, патченный
        //    CompanionSkillFactorPatch) — проверяет именно «влияние штатно», а не свою формулу.
        //    Стабильно (чистая формула, без боя) — оставлено без изменений.
        // 2) Урон: Clubs 0 vs 100 против одного и того же противника (Skeleton) — свой бонус
        //    спутника (CompanionWeaponSkillDamagePatch, до +30% на максимуме; в ваниле Skills НЕ
        //    двигает цифру урона напрямую, см. комментарий патча). Было: сумма урона за 25с
        //    (нестабильно от числа долетевших ударов, main FAIL 0=176/100=153). Теперь: средний
        //    урон ЗА УДАР по ≥100 попаданиям на каждый уровень (планка брифа) — число попаданий
        //    фиксировано, шум RNG (0.75..1.0 от GetRandomSkillFactor) усредняется по n.
        private static IEnumerator SkillsEffect()
        {
            var cb = SelfTest.SpawnCompanion(2f, -2f);
            if (cb == null) { SelfTest.Fail("не призвался (блок)"); yield break; }
            cb.SetName("НавыкТест_блок");
            yield return new WaitForSeconds(1.5f);
            var shield = cb.Body != null ? cb.Body.LeftItem : null;
            if (shield == null) { SelfTest.Fail("нет щита в левой руке у бойца"); yield break; }

            CompanionSkills.SetLevel(cb.Name, Skills.SkillType.Blocking, 0f);
            float blockPowerZero = shield.GetBlockPower(cb.Body.GetSkillFactor(Skills.SkillType.Blocking));
            CompanionSkills.SetLevel(cb.Name, Skills.SkillType.Blocking, 100f);
            float blockPowerFull = shield.GetBlockPower(cb.Body.GetSkillFactor(Skills.SkillType.Blocking));
            CompanionManager.Dismiss(cb);
            SelfTest.CleanupArena();
            yield return new WaitForSeconds(1f);

            const int targetHits = 100;
            const float maxDuration = 480f;
            var zero = new DmgResult();
            yield return MeasureDamage("НавыкТест_урон0", 0f, targetHits, maxDuration, zero);
            SelfTest.CleanupArena();
            yield return new WaitForSeconds(1f);
            var full = new DmgResult();
            yield return MeasureDamage("НавыкТест_урон100", 100f, targetHits, maxDuration, full);

            float avgZero = zero.Hits > 0 ? zero.Dealt / zero.Hits : 0f;
            float avgFull = full.Hits > 0 ? full.Dealt / full.Hits : 0f;
            string info = "сила_блока 0=" + blockPowerZero.ToString("0.0") + " 100=" + blockPowerFull.ToString("0.0")
                + " | урон_за_удар 0=" + avgZero.ToString("0.00") + "(n=" + zero.Hits + ") 100=" + avgFull.ToString("0.00") + "(n=" + full.Hits + ")";
            if (blockPowerFull <= blockPowerZero) { SelfTest.Fail("сила блока не выросла | " + info); yield break; }
            if (zero.Hits < targetHits || full.Hits < targetHits) { SelfTest.Fail("мало данных (n<" + targetHits + ") | " + info); yield break; }
            if (avgFull <= avgZero) { SelfTest.Fail("урон за удар не выше при максимуме навыка | " + info); yield break; }
            SelfTest.Pass(info);
        }

        // ПОКАЗ ДЛЯ ИЛЬИ (demo_skills, 26.10): крупные подписи, ~40с/шаг (PROJECT_BRIEF.md
        // «ПОКАЗ ДЛЯ ИЛЬИ»). Весь ТЗ этапа «Навыки-2» по шагам: подробности бойца по I (навык
        // показан в DetailRow.Skill — Plugin.cs, был плейсхолдер "фикс. ..." до этого фикса,
        // теперь CompanionSkills.Summary), рост от ударов/блоков, урон/сила блока на низком и
        // высоком навыке, навык не теряется при смерти (в отличие от игрока).
        private static IEnumerator DemoSkills()
        {
            string name = "ПоказНавыков";
            CompanionSkills.ResetAll(name);
            object oldDetail = Plugin.DetailTarget;
            try
            {
                var c = SelfTest.SpawnCompanion(3f, 0f);
                if (c == null) { SelfTest.Fail("не призвался"); yield break; }
                c.SetName(name);
                Plugin.DetailTarget = c; // подробности на экране сразу — Илье не нужно самому жать I
                yield return new WaitForSeconds(1.5f);
                if (c.Body == null) { SelfTest.Fail("боец пропал"); yield break; }

                yield return SelfTest.Caption("ПОКАЗ: навыки бойца растут как у игрока (1/5)\n"
                    + "Нажми I — у свежего бойца «Навыки: пока без опыта» (стартовый уровень скрыт, пока не вырос).",
                    13f * Plugin.DevDemoPace.Value);

                CompanionSkills.SetLevel(name, Skills.SkillType.Clubs, 0f);
                CompanionSkills.SetLevel(name, Skills.SkillType.Blocking, 0f);
                // Щит берём свежим каждый раз (не кэшируем до боя) — ссылка на ItemData может
                // устареть, если WeaponSwitch переэкипирует бойца во время боя (шаг 3, слито из main).
                var shield = c.Body != null ? c.Body.LeftItem : null;
                float bpLow = shield != null ? shield.GetBlockPower(c.Body.GetSkillFactor(Skills.SkillType.Blocking)) : 0f;
                yield return SelfTest.Caption("(2/5) НИЗКИЙ навык (0): сила блока щита = " + bpLow.ToString("0.0")
                    + ".\nНажми I — Блок 0, Дубины 0 (пока без опыта — специально обнулили для показа).",
                    13f * Plugin.DevDemoPace.Value);

                float t0 = Time.time;
                float demoFightLen = 40f * Plugin.DevDemoPace.Value;
                SelfTest.ShowCaption("(3/5) Дерётся со скелетом ~" + (int)demoFightLen + "с — КАЖДЫЙ удар и блок поднимают\nнавык, как у игрока. Нажми I — цифры «Дубины»/«Блок» растут на глазах.\n(лечение включено — показ только навыков, не живучести)", demoFightLen);
                var mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f);
                Character mc = mob != null ? mob.GetComponent<Character>() : null;
                while (Time.time - t0 < demoFightLen)
                {
                    if (c.Body == null) break;
                    // Долечиваем — показ не должен срываться из-за того, что боец реально погиб
                    // в бою шага 3/5 (найдено по скриншоту: сила блока шага 4/5 вышла 0,0, потому
                    // что боец уже был мёртв) — выживание тут не предмет показа, он отдельно в
                    // block_parry/stamina_reserve; демо-смерть у нас СВОЯ, скриптованная, в 5/5.
                    c.Body.SetHealth(c.Body.GetMaxHealth());
                    if (mc == null || mc.IsDead()) { mob = SelfTest.SpawnPrefab("Skeleton", 10f, 0f); mc = mob != null ? mob.GetComponent<Character>() : null; }
                    yield return new WaitForSeconds(1f);
                }
                float clubsGrown = CompanionSkills.GetLevel(name, Skills.SkillType.Clubs);
                float blockGrown = CompanionSkills.GetLevel(name, Skills.SkillType.Blocking);
                if (mob != null && ZNetScene.instance != null) ZNetScene.instance.Destroy(mob); // убрать скелета, боец остаётся для показа

                CompanionSkills.SetLevel(name, Skills.SkillType.Blocking, 100f);
                var shieldNow = c.Body != null ? c.Body.LeftItem : null; // свежая ссылка — см. комментарий у bpLow
                float bpHigh = c.Body != null && shieldNow != null ? shieldNow.GetBlockPower(c.Body.GetSkillFactor(Skills.SkillType.Blocking)) : 0f;
                yield return SelfTest.Caption("(4/5) ВЫСОКИЙ навык (100): сила блока щита = " + bpHigh.ToString("0.0")
                    + " (было " + bpLow.ToString("0.0") + " на нуле) — щит держит сильнее.\n"
                    + "Урон оружием растёт до +" + (Plugin.CompanionSkillDamageBonus.Value * 100f).ToString("0") + "% на максимуме навыка.",
                    14f * Plugin.DevDemoPace.Value);

                CompanionSkills.SetLevel(name, Skills.SkillType.Clubs, 37f);
                float beforeDeath = CompanionSkills.GetLevel(name, Skills.SkillType.Clubs);
                yield return SelfTest.Caption("(5/5) Навык Дубины сейчас " + beforeDeath.ToString("0") + ". Боец сейчас УМРЁТ.\n"
                    + "У игрока смерть снижает навыки — у бойца НЕТ (посмотрим после).", 13f * Plugin.DevDemoPace.Value);
                // Боец мог уже погибнуть САМ в реальном бою шага 3/5 (не рига — настоящая смерть) —
                // это тоже валидный показ «не теряется», просто смертельный удар наносить не нужно.
                var died = new SelfTest.WaitResult { Ok = true };
                if (c.Body != null)
                {
                    var hit = new HitData();
                    hit.m_damage.m_damage = 99999f;
                    hit.m_point = c.Body.transform.position;
                    hit.m_dir = Vector3.down;
                    c.Body.Damage(hit);
                    yield return SelfTest.WaitUntil(delegate { return c.Body == null; }, 5f, died);
                }
                float afterDeath = CompanionSkills.GetLevel(name, Skills.SkillType.Clubs);
                SelfTest.ClearCaption();
                yield return SelfTest.Caption("ГОТОВО: Дубины было " + beforeDeath.ToString("0") + ", после смерти — " + afterDeath.ToString("0")
                    + " (не изменился). У игрока навык после смерти УПАЛ БЫ.", 14f * Plugin.DevDemoPace.Value);
                SelfTest.ClearCaption();
                yield return SelfTest.Caption("ПОКАЗ ЗАКОНЧЕН", 5f * Plugin.DevDemoPace.Value);

                string info = "сила_блока 0=" + bpLow.ToString("0.0") + " 100=" + bpHigh.ToString("0.0")
                    + " | бой Дубины->" + clubsGrown.ToString("0") + " Блок->" + blockGrown.ToString("0")
                    + " | смерть Дубины " + beforeDeath.ToString("0") + "->" + afterDeath.ToString("0") + " (погиб=" + died.Ok + ")";
                if (!died.Ok) { SelfTest.Fail("боец не умер за 5с | " + info); yield break; }
                if (bpHigh <= bpLow) { SelfTest.Fail("сила блока не выросла | " + info); yield break; }
                if (afterDeath != beforeDeath) { SelfTest.Fail("навык изменился после смерти | " + info); yield break; }
                SelfTest.Pass(info);
            }
            finally
            {
                Plugin.DetailTarget = oldDetail;
                SelfTest.ClearCaption();
            }
        }
    }
}

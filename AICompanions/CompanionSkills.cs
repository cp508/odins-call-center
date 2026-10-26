using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace AICompanions
{
    // Навыки спутника — свой аналог компонента Skills игрока (у спутника он намеренно убран,
    // CompanionFactory.Build — иначе клон слушает ввод/читает хозяйский сейв). Формулы — ТОЧНАЯ
    // копия игровых, снятые по IL (_research/skills_status.md): Skills.RaiseSkill → Skill.Raise →
    // GetNextLevelRequirement (dotnet il8.dll Skills+Skill.Raise/GetNextLevelRequirement):
    //   accumulator += SkillDef.m_increseStep * factor * Game.m_skillGainRate
    //   req = floor(level+1)^1.5 * 0.5 + 0.5
    //   accumulator>=req → level=clamp(level+1,0,100), accumulator=0
    // Хранится ПО ИМЕНИ спутника (FollowerAI.Name) в отдельном файле на диске — переживает
    // Dismiss/призыв, смерть, горячую перезагрузку ядра и выход из мира (как AttackProfiles.cs).
    //
    // НОВЫЙ спутник (имя без записей) получает стартовый уровень = Plugin.CompanionSkillFactor*100,
    // НЕ ноль: этот конфиг раньше был единственным (фиксированным) «навыком» спутника (см.
    // комментарий CombatPatches.CompanionSkillFactorPatch) — держим его смысл как БАЗОВЫЙ уровень,
    // чтобы поведение нетронутого спутника не откатилось к багу «вечный ноль навыка» (проваленный
    // block_parry до "Боя v2а"). Дальше уровень растёт/падает только от использования, по имени.
    public static class CompanionSkills
    {
        private class Entry
        {
            public float Level;
            public float Accumulator;
            // true только после РЕАЛЬНОГО повышения уровня в Raise() — отдельно от Level,
            // потому что при создании Entry уровень сразу = стартовый (БАЗОВЫЙ, ~65 по
            // умолчанию), и сравнение одного Level с порогом не отличает «навык не трогали» от
            // «навык честно выращен» (Summary() ниже).
            public bool Grew;
        }

        private static readonly Dictionary<string, Dictionary<Skills.SkillType, Entry>> _data
            = new Dictionary<string, Dictionary<Skills.SkillType, Entry>>();

        // m_increseStep по типу навыка — снят с настоящего Skills-компонента игрока ОДИН раз,
        // пока CompanionFactory.Build его ещё не удалил из клона (CaptureSkillDefs).
        private static readonly Dictionary<Skills.SkillType, float> _increaseStep
            = new Dictionary<Skills.SkillType, float>();

        private static readonly FieldInfo SkillGainRateField = AccessTools.Field(typeof(Game), "m_skillGainRate");

        private static bool _loaded;
        private static bool _dirty;
        private static float _saveTimer;

        // ---- SkillDef (m_increseStep) — один раз, с шаблона игрока ----

        public static void CaptureSkillDefs(Skills skills)
        {
            if (skills == null || _increaseStep.Count > 0) return;
            var list = skills.m_skills; // List<Skills.SkillDef>, публичное поле
            if (list == null) return;
            foreach (var def in list)
                if (def != null) _increaseStep[def.m_skill] = def.m_increseStep;
            Plugin.Log.LogInfo("[Skills] снято определений навыков с шаблона игрока: " + _increaseStep.Count);
        }

        private static float IncreaseStepOf(Skills.SkillType t)
        {
            float v;
            // 1 — дефолт игры для большинства навыков (Blocking/Swords/... = 1 в ваниле), если
            // шаблон почему-то не снялся (напр. тест без CompanionFactory.GetTemplate()).
            return _increaseStep.TryGetValue(t, out v) && v > 0f ? v : 1f;
        }

        // ---- данные ----

        private static Entry GetOrCreate(string name, Skills.SkillType type)
        {
            Dictionary<Skills.SkillType, Entry> byType;
            if (!_data.TryGetValue(name, out byType))
            {
                byType = new Dictionary<Skills.SkillType, Entry>();
                _data[name] = byType;
            }
            Entry e;
            if (!byType.TryGetValue(type, out e))
            {
                e = new Entry { Level = Mathf.Clamp01(Plugin.CompanionSkillFactor.Value) * 100f, Accumulator = 0f };
                byType[type] = e;
                _dirty = true;
            }
            return e;
        }

        // Уровень 0..100, как Skills.GetSkillLevel (floor, без учёта SEMan-баффов — у спутника их
        // влияющих на навык сейчас нет).
        public static float GetLevel(string name, Skills.SkillType type)
        {
            if (string.IsNullOrEmpty(name) || type == Skills.SkillType.None) return 0f;
            return Mathf.Floor(GetOrCreate(name, type).Level);
        }

        // Фактор 0..1 = уровень/100, как Skills.GetSkillFactor.
        public static float GetFactor(string name, Skills.SkillType type)
        {
            if (type == Skills.SkillType.None) return 0f;
            return Mathf.Clamp01(GetLevel(name, type) / 100f);
        }

        // Точная копия Skills+Skill.Raise(factor) (IL skills_il4.txt): true — если поднялся уровень.
        public static bool Raise(string name, Skills.SkillType type, float factor)
        {
            if (string.IsNullOrEmpty(name) || type == Skills.SkillType.None || factor <= 0f) return false;
            var e = GetOrCreate(name, type);
            if (e.Level >= 100f) return false;
            float rate = SkillGainRateField != null ? (float)SkillGainRateField.GetValue(null) : 1f;
            float gain = IncreaseStepOf(type) * factor * rate;
            e.Accumulator += gain;
            _dirty = true;
            float req = NextLevelRequirement(e.Level);
            if (e.Accumulator < req) return false;
            e.Level = Mathf.Clamp(e.Level + 1f, 0f, 100f);
            e.Accumulator = 0f;
            e.Grew = true;
            Plugin.Log.LogInfo("[Skills] " + name + ": " + type + " → уровень " + (int)e.Level);
            return true;
        }

        // Skill.GetNextLevelRequirement(): floor(level+1)^1.5*0.5+0.5 (IL skills_il4.txt).
        private static float NextLevelRequirement(float level)
        {
            return Mathf.Pow(Mathf.Floor(level + 1f), 1.5f) * 0.5f + 0.5f;
        }

        // Для тестов/отладки — поставить уровень напрямую (аналог Skills.CheatRaiseSkill/
        // CheatResetSkill игры). Сбрасывает accumulator, чтобы следующий Raise считался от
        // чистого уровня.
        public static void SetLevel(string name, Skills.SkillType type, float level)
        {
            if (string.IsNullOrEmpty(name) || type == Skills.SkillType.None) return;
            var e = GetOrCreate(name, type);
            e.Level = Mathf.Clamp(level, 0f, 100f);
            e.Accumulator = 0f;
            e.Grew = true; // прямая установка (тест/читы) считаем как «реальный» уровень для Summary
            _dirty = true;
        }

        public static void ResetAll(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _data.Remove(name);
            _dirty = true;
        }

        public static void Reset(string name, Skills.SkillType type)
        {
            Dictionary<Skills.SkillType, Entry> byType;
            if (!string.IsNullOrEmpty(name) && _data.TryGetValue(name, out byType)) byType.Remove(type);
            _dirty = true;
        }

        // Для теста/отчёта — число записей навыков у имени (0, если нет).
        public static int CountFor(string name)
        {
            Dictionary<Skills.SkillType, Entry> byType;
            return !string.IsNullOrEmpty(name) && _data.TryGetValue(name, out byType) ? byType.Count : 0;
        }

        // Для теста skills_rate_parity — текущий накопитель (аккумулятор) до левелапа, тот же
        // смысл, что публичное поле Skills+Skill.m_accumulator у настоящего навыка игрока.
        public static float GetAccumulator(string name, Skills.SkillType type)
        {
            Dictionary<Skills.SkillType, Entry> byType;
            Entry e;
            if (string.IsNullOrEmpty(name) || !_data.TryGetValue(name, out byType) || !byType.TryGetValue(type, out e))
                return 0f;
            return e.Accumulator;
        }

        // ---- сводка для меню Ё ----

        private static readonly Skills.SkillType[] DisplaySkills =
        {
            Skills.SkillType.Swords, Skills.SkillType.Knives, Skills.SkillType.Clubs,
            Skills.SkillType.Polearms, Skills.SkillType.Spears, Skills.SkillType.Axes,
            Skills.SkillType.Unarmed, Skills.SkillType.Blocking, Skills.SkillType.Bows,
            Skills.SkillType.Crossbows, Skills.SkillType.ElementalMagic, Skills.SkillType.BloodMagic,
            // Этап «Навыки-2»: Jump растёт штатно (BaseAI.MoveTowards → Character.Jump),
            // Run/Swim/Dodge — через новые триггеры (FollowerAI.TickBody/CompanionDodge).
            Skills.SkillType.Jump, Skills.SkillType.Run, Skills.SkillType.Swim, Skills.SkillType.Dodge,
        };

        private static readonly Dictionary<Skills.SkillType, string> RuNames = new Dictionary<Skills.SkillType, string>
        {
            { Skills.SkillType.Swords, "Мечи" }, { Skills.SkillType.Knives, "Ножи" },
            { Skills.SkillType.Clubs, "Дубины" }, { Skills.SkillType.Polearms, "Копья(дв)" },
            { Skills.SkillType.Spears, "Копья" }, { Skills.SkillType.Axes, "Топоры" },
            { Skills.SkillType.Unarmed, "Безоруж." }, { Skills.SkillType.Blocking, "Блок" },
            { Skills.SkillType.Bows, "Луки" }, { Skills.SkillType.Crossbows, "Арбалеты" },
            { Skills.SkillType.ElementalMagic, "Стихии" }, { Skills.SkillType.BloodMagic, "Кровь" },
            { Skills.SkillType.Jump, "Прыжок" }, { Skills.SkillType.Run, "Бег" },
            { Skills.SkillType.Swim, "Плав." }, { Skills.SkillType.Dodge, "Кувырок" },
        };

        // Короткая строка для строки меню — только навыки, которые РЕАЛЬНО поднялись хоть раз
        // (Entry.Grew, ставится в Raise()/SetLevel, а не по значению Level — Entry создаётся
        // сразу со стартовым уровнем при первом же обращении, Level>0.5 было бы true и без
        // единого реального роста), чтобы не захламлять плашку базовыми значениями.
        public static string Summary(string name)
        {
            Dictionary<Skills.SkillType, Entry> byType;
            if (string.IsNullOrEmpty(name) || !_data.TryGetValue(name, out byType) || byType.Count == 0)
                return "Навыки: пока без опыта";
            var parts = new List<string>();
            foreach (var t in DisplaySkills)
            {
                Entry e;
                if (byType.TryGetValue(t, out e) && e.Grew)
                {
                    string label;
                    if (!RuNames.TryGetValue(t, out label)) label = t.ToString();
                    parts.Add(label + " " + (int)e.Level);
                }
            }
            return parts.Count > 0 ? "Навыки: " + string.Join(" · ", parts.ToArray()) : "Навыки: пока без опыта";
        }

        // ---- сохранение/загрузка (формат и приём — как AttackProfiles.cs) ----

        private static string FilePath()
        {
            try { return Path.Combine(BepInEx.Paths.ConfigPath, "AICompanions.companionskills.txt"); }
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
                int n = 0;
                foreach (var line in File.ReadAllLines(path))
                {
                    // Формат строки: Имя|SkillType=уровень;аккумулятор;выросло(0/1)
                    // '|' и '=' запрещены в имени спутника (FollowerAI.SetName) — иначе здесь
                    // нельзя было бы надёжно отличить разделитель от символа внутри имени.
                    int eq = line.IndexOf('=');
                    int bar = line.IndexOf('|');
                    if (eq < 0 || bar < 0 || bar > eq)
                    {
                        Plugin.Log.LogWarning("[Skills] пропущена нераспознанная строка сейва: " + line);
                        continue;
                    }
                    string name = line.Substring(0, bar);
                    string typeStr = line.Substring(bar + 1, eq - bar - 1);
                    Skills.SkillType type;
                    try { type = (Skills.SkillType)Enum.Parse(typeof(Skills.SkillType), typeStr); }
                    catch { Plugin.Log.LogWarning("[Skills] пропущена строка с неизвестным типом навыка: " + line); continue; }
                    string[] parts = line.Substring(eq + 1).Split(';');
                    float lvl;
                    if (parts.Length < 1 || !float.TryParse(parts[0], out lvl)) continue;
                    float acc = 0f;
                    if (parts.Length >= 2) float.TryParse(parts[1], out acc);
                    // Старые файлы (до этого фикса) третьего поля не имеют — по умолчанию считаем
                    // «выросло», раз запись вообще была сохранена на диск прошлой версией мода.
                    bool grew = true;
                    if (parts.Length >= 3)
                    {
                        string g = parts[2];
                        grew = g == "1";
                    }
                    Dictionary<Skills.SkillType, Entry> byType;
                    if (!_data.TryGetValue(name, out byType))
                    {
                        byType = new Dictionary<Skills.SkillType, Entry>();
                        _data[name] = byType;
                    }
                    byType[type] = new Entry { Level = lvl, Accumulator = acc, Grew = grew };
                    n++;
                }
                Plugin.Log.LogInfo("[Skills] загружено записей навыков: " + n + " из " + path);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Skills] загрузка не удалась: " + ex.Message); }
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
            string path = FilePath();
            if (path == null) return;
            try
            {
                var lines = new List<string>();
                foreach (var byName in _data)
                    foreach (var byType in byName.Value)
                        lines.Add(byName.Key + "|" + byType.Key + "=" + byType.Value.Level.ToString("0.000")
                            + ";" + byType.Value.Accumulator.ToString("0.000")
                            + ";" + (byType.Value.Grew ? "1" : "0"));
                File.WriteAllLines(path, lines.ToArray());
                _dirty = false;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("[Skills] сохранение не удалось: " + ex.Message); }
        }

        // Выгрузка ядра (горячая перезагрузка) — дописать на диск, чтобы не терять прогресс.
        public static void Shutdown()
        {
            Save();
        }
    }
}

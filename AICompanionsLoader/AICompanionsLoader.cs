using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using Mono.Cecil;

namespace AICompanionsLoader
{
    // Загрузчик ядра AICompanions без перезапуска игры.
    // Сам Chainloader грузит только этот плагин (он лежит в BepInEx/plugins/AICompanionsLoader).
    // Ядро (AICompanions.dll) лежит ВНЕ plugins — в BepInEx/AICompanionsCore — поэтому Chainloader
    // его не трогает, файл не лочится, и build.sh может пересобирать ядро при запущенной игре.
    // Команды — через файл <корень BepInEx>\aic_cmd.txt (свой канал на каждый клон игры, см.
    // PROJECT_BRIEF.md раздел «Клоны»); для профиля Ильи (Stas_modpack) — по-прежнему
    // C:\temp\aic\cmd.txt, обратная совместимость со старыми привычками и скриптами:
    //   "reload"            — выгрузить текущее ядро и загрузить заново из файла на диске
    //   "selftest a,b,c"    — позвать AICompanions.SelfTest.RunNow("a,b,c") в уже загруженном ядре
    // Лог: все строки с префиксом "[LOADER] ".
    [BepInPlugin("com.aicompanions.loader", "AICompanionsLoader", "0.1.0")]
    public class Loader : BaseUnityPlugin
    {
        // Профиль Ильи узнаём по пути BepInEx-корня (…\profiles\Stas_modpack\BepInEx) — у клонов
        // корень лежит в C:\temp\aic\inst\<имя>\BepInEx, там имени Stas_modpack не бывает.
        private static string CmdPath()
        {
            string root = Paths.BepInExRootPath;
            if (root != null && root.IndexOf("Stas_modpack", StringComparison.OrdinalIgnoreCase) >= 0)
                return @"C:\temp\aic\cmd.txt";
            return Path.Combine(root, "aic_cmd.txt");
        }

        public static ManualLogSource Log;

        // Компоненты ядра, добавленные на этот же GameObject (по одному на каждый найденный
        // BaseUnityPlugin-тип сборки). При reload их нужно уничтожить ДО загрузки новой сборки —
        // иначе OnDestroy старого ядра (снятие Harmony-патчей с тем же ID) выполнится ПОСЛЕ того,
        // как новое ядро уже успеет что-то запатчить, и снесёт заодно и новые патчи.
        private readonly List<Component> _coreComponents = new List<Component>();

        private float _timer;
        private int _loadVersion;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("[LOADER] старт, core: " + CoreDllPath() + ", канал команд: " + CmdPath());
            LoadCore();
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 0.5f) return;
            _timer = 0f;
            CheckCommand();
        }

        private static string CoreDllPath()
        {
            // BepInEx/AICompanionsCore/AICompanions.dll — вне plugins, Chainloader туда не заглядывает.
            return Path.Combine(Path.Combine(Paths.BepInExRootPath, "AICompanionsCore"), "AICompanions.dll");
        }

        // ---- командный файл ----

        private void CheckCommand()
        {
            string cmdPath = CmdPath();
            if (!File.Exists(cmdPath)) return;
            string text;
            try { text = File.ReadAllText(cmdPath); }
            catch (IOException) { return; } // файл ещё дописывается хостом — попробуем следующим тиком

            try { File.Delete(cmdPath); }
            catch (IOException ex) { Log.LogWarning("[LOADER] не удалось удалить cmd.txt: " + ex.Message); }

            HandleCommand(text);
        }

        private void HandleCommand(string raw)
        {
            string cmd = raw == null ? "" : raw.Trim();
            if (cmd.Length == 0) return;

            if (cmd == "reload")
            {
                Reload();
                return;
            }
            if (cmd.StartsWith("selftest"))
            {
                string list = cmd.Substring("selftest".Length).Trim();
                RunSelfTest(list);
                return;
            }
            Log.LogWarning("[LOADER] неизвестная команда: " + cmd);
        }

        // ---- загрузка / перезагрузка ядра ----

        private void Reload()
        {
            UnloadCore();
            LoadCore();
        }

        private void UnloadCore()
        {
            foreach (var c in _coreComponents)
            {
                if (c == null) continue;
                try
                {
                    // DestroyImmediate, а не Destroy: Destroy откладывает удаление до конца кадра,
                    // а нам нужно, чтобы OnDestroy (снятие патчей, DismissAll) отработал ПРЯМО СЕЙЧАС,
                    // до того как LoadCore() добавит компоненты новой сборки.
                    UnityEngine.Object.DestroyImmediate(c);
                }
                catch (Exception ex)
                {
                    Log.LogError("[LOADER] ошибка при выгрузке старого ядра (" + c.GetType().FullName + "): " + ex);
                }
            }
            _coreComponents.Clear();
        }

        private void LoadCore()
        {
            string dllPath = CoreDllPath();
            if (!File.Exists(dllPath))
            {
                Log.LogWarning("[LOADER] core dll не найден: " + dllPath);
                return;
            }

            try
            {
                byte[] raw = File.ReadAllBytes(dllPath);
                byte[] renamed = RenameAssembly(raw);
                Assembly asm = Assembly.Load(renamed);

                int added = 0;
                Type[] types = SafeGetTypes(asm);
                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract) continue;
                    if (!typeof(BaseUnityPlugin).IsAssignableFrom(type)) continue;
                    Component comp = null;
                    try { comp = gameObject.AddComponent(type); }
                    catch (Exception ex) { Log.LogError("[LOADER] AddComponent(" + type.FullName + "): " + ex); }
                    if (comp != null)
                    {
                        _coreComponents.Add(comp);
                        added++;
                        // Регистрируем ядро в списке плагинов — иначе Config Manager (F1) не покажет его настройки.
                        var bp = comp as BaseUnityPlugin;
                        if (bp != null && bp.Info != null && bp.Info.Metadata != null)
                        {
                            // Config Manager берёт плагины из PluginInfos и пропускает те, у кого Info.Instance == null
                            // (у нас Info создан без Chainloader) — проставляем Instance через внутренний сеттер.
                            // ВАЖНО: TypeName и Location тоже обязательны — Jotunn у ДРУГИХ модов перебирает
                            // PluginInfos и зовёт assembly.GetType(TypeName); null → ArgumentNullException и
                            // чужой мод (UnderTheSea и др.) падает на старте. Без полного Info — не регистрируем.
                            bool okInstance = SetInfo(bp.Info, "Instance", bp);
                            bool okTypeName = SetInfo(bp.Info, "TypeName", type.FullName);
                            bool okLocation = SetInfo(bp.Info, "Location", dllPath);
                            // Перечитываем через reflection (не bp.Info.TypeName/Instance/Location напрямую —
                            // у этого BepInEx геттер TypeName объявлен internal, csc его снаружи не видит и даёт
                            // CS1061; GetInfo читает тем же приёмом, что SetInfo пишет).
                            bool filled = okInstance && okTypeName && okLocation
                                && GetInfo(bp.Info, "Instance") != null
                                && !string.IsNullOrEmpty((string)GetInfo(bp.Info, "TypeName"))
                                && !string.IsNullOrEmpty((string)GetInfo(bp.Info, "Location"));
                            if (filled)
                                BepInEx.Bootstrap.Chainloader.PluginInfos[bp.Info.Metadata.GUID] = bp.Info;
                            else
                                Log.LogWarning("[LOADER] PluginInfo ядра неполный — в список плагинов не добавлен");
                        }
                    }
                }

                _loadVersion++;
                if (added == 0)
                    Log.LogWarning("[LOADER] reload v" + _loadVersion + ": ни одного BaseUnityPlugin не найдено в " + dllPath);
                else
                    Log.LogInfo("[LOADER] reload OK v" + _loadVersion + " (" + added + " компонент(ов))");
            }
            catch (Exception ex)
            {
                Log.LogError("[LOADER] ошибка загрузки core v" + (_loadVersion + 1) + ": " + ex);
            }
        }

        // Переименовывает сборку (имя Assembly + имя главного модуля) на уникальное перед Assembly.Load.
        // Без этого Mono при повторной загрузке с тем же именем вернёт СТАРУЮ сборку из кэша домена —
        // новый код просто не применится. Тот же приём использует BepInEx.ScriptEngine.
        private static byte[] RenameAssembly(byte[] rawAssembly)
        {
            using (var inStream = new MemoryStream(rawAssembly))
            {
                AssemblyDefinition def = AssemblyDefinition.ReadAssembly(inStream);
                try
                {
                    string unique = "AICompanions_" + System.Guid.NewGuid().ToString("N");
                    def.Name.Name = unique;
                    def.MainModule.Name = unique + ".dll";

                    using (var outStream = new MemoryStream())
                    {
                        def.Write(outStream);
                        return outStream.ToArray();
                    }
                }
                finally
                {
                    def.Dispose();
                }
            }
        }

        private Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle)
            {
                foreach (var le in rtle.LoaderExceptions)
                    Log.LogWarning("[LOADER] LoaderException при GetTypes: " + le);
                var ok = new List<Type>();
                foreach (var t in rtle.Types) if (t != null) ok.Add(t);
                return ok.ToArray();
            }
        }

        // ---- selftest ----

        private void RunSelfTest(string list)
        {
            Type selfTestType = FindCoreType("AICompanions.SelfTest");
            if (selfTestType == null)
            {
                Log.LogWarning("[LOADER] ядро не загружено — команда selftest пропущена");
                return;
            }

            MethodInfo method = selfTestType.GetMethod("RunNow", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                Log.LogWarning("[LOADER] AICompanions.SelfTest.RunNow(string) не найден в текущем ядре — " +
                    "добавь его по INTEGRATION.md, пока selftest через reload недоступен");
                return;
            }

            try
            {
                method.Invoke(null, new object[] { list });
                Log.LogInfo("[LOADER] selftest запущен: " + list);
            }
            catch (Exception ex)
            {
                Log.LogError("[LOADER] selftest ошибка: " + ex);
            }
        }

        private Type FindCoreType(string fullName)
        {
            foreach (var c in _coreComponents)
            {
                if (c == null) continue;
                Type t = c.GetType().Assembly.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        // GetProperty(prop) БЕЗ BindingFlags ищет только публичные члены — у PluginInfo.TypeName
        // ОБА accessor'а internal (Assembly), поэтому без Public|NonPublic свойство вообще не находится
        // (p == null) и set молча остаётся null — запись не делает ничего и без ошибки. С флагами находит.
        private static bool SetInfo(PluginInfo info, string prop, object value)
        {
            try
            {
                var p = typeof(PluginInfo).GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var set = p != null ? p.GetSetMethod(true) : null;
                if (set == null) return false;
                set.Invoke(info, new object[] { value });
                return true;
            }
            catch (Exception ex) { Log.LogWarning("[LOADER] PluginInfo." + prop + " set: " + ex.Message); return false; }
        }

        // Чтение тем же приёмом (см. SetInfo) — у этого BepInEx TypeName.get объявлен internal,
        // поэтому прямой bp.Info.TypeName снаружи сборки BepInEx не компилируется (CS1061).
        private static object GetInfo(PluginInfo info, string prop)
        {
            try
            {
                var p = typeof(PluginInfo).GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var get = p != null ? p.GetGetMethod(true) : null;
                return get != null ? get.Invoke(info, null) : null;
            }
            catch (Exception ex) { Log.LogWarning("[LOADER] PluginInfo." + prop + " get: " + ex.Message); return null; }
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AICompanions
{
    // Метка бойца на миникарте и большой карте (это один и тот же Minimap — рисует обе) его цветом,
    // с подписью имени. ПОДВОДНЫЙ КАМЕНЬ (см. _research/ahead_death_base_map.md §3): Minimap.UpdatePins
    // каждый кадр сам перезаписывает pin.m_iconElement.color (белый/притушенный серый) — ручная
    // покраска через .color не держится, цвет нужно «запекать» в сам спрайт (текстуру) один раз на цвет.
    // save=false — метка не льётся в сейв мира, это чисто динамическое положение живого бойца.
    public static class CompanionMapPins
    {
        private static readonly Dictionary<FollowerAI, Minimap.PinData> _pins = new Dictionary<FollowerAI, Minimap.PinData>();
        private static readonly List<FollowerAI> _removeBuf = new List<FollowerAI>();
        private static readonly Sprite[] _spriteByColor = new Sprite[CompanionIdentity.Colors.Length];

        // AddPin(Vector3, PinType, string, bool, bool, long, PlatformUserID) — последний параметр
        // (структура из сборки, которую не подключали в build.sh) собираем через reflection
        // Activator.CreateInstance по реальному типу параметра = тому же default(PlatformUserID).
        private static readonly MethodInfo AddPinMethod = typeof(Minimap).GetMethod("AddPin");

        // public — переиспользует EnemyMapPins (та же проблема «цвет каждый кадр перезатирается
        // игрой», тот же обходной путь через AddPin+reflection на PlatformUserID) и CompanionDeath
        // для метки надгробия (тот же приём: цвет в спрайте).
        public static Minimap.PinData CreatePin(Vector3 pos, string name)
        {
            if (AddPinMethod == null || Minimap.instance == null) return null;
            var ps = AddPinMethod.GetParameters();
            object author = System.Activator.CreateInstance(ps[6].ParameterType);
            object result = AddPinMethod.Invoke(Minimap.instance, new object[] { pos, Minimap.PinType.Player, name, false, false, 0L, author });
            return (Minimap.PinData)result;
        }

        // 2 раза/с (просьба Ильи) — зовёт Plugin.Update по своему таймеру.
        public static void Tick()
        {
            if (Minimap.instance == null) { _pins.Clear(); return; }

            foreach (var c in CompanionManager.Alive())
            {
                if (c == null || c.Body == null || c.Body.IsDead()) continue;
                Minimap.PinData pin;
                if (!_pins.TryGetValue(c, out pin) || pin == null)
                {
                    pin = CreatePin(c.transform.position, c.Name);
                    if (pin == null) continue;
                    pin.m_icon = SpriteFor(c.ColorIndex);
                    _pins[c] = pin;
                }
                pin.m_pos = c.transform.position;
                if (pin.m_name != c.Name) pin.m_name = c.Name;
            }

            // Распущен/умер/объект пропал (не успел дойти Remove из Dismiss/OnDeath) — убрать пин.
            if (_pins.Count > 0)
            {
                _removeBuf.Clear();
                foreach (var kv in _pins)
                {
                    var f = kv.Key;
                    if (f == null || !f.IsSpawned || f.Body == null || f.Body.IsDead()) _removeBuf.Add(f);
                }
                for (int i = 0; i < _removeBuf.Count; i++) Remove(_removeBuf[i]);
            }
        }

        // Вызывают CompanionManager.Dismiss (уход) и CompanionDeathNoticePatch (смерть) — сразу,
        // не дожидаясь отложенного Object.Destroy.
        public static void Remove(FollowerAI c)
        {
            Minimap.PinData pin;
            if (c == null || !_pins.TryGetValue(c, out pin)) return;
            _pins.Remove(c);
            if (Minimap.instance != null && pin != null) Minimap.instance.RemovePin(pin);
        }

        // Plugin.OnDestroy (выгрузка мода/горячая перезагрузка) — финальная зачистка всех меток.
        public static void Shutdown()
        {
            if (Minimap.instance != null)
            {
                foreach (var kv in _pins) if (kv.Value != null) Minimap.instance.RemovePin(kv.Value);
            }
            _pins.Clear();
        }

        public static Sprite SpriteFor(int colorIndex)
        {
            if (colorIndex < 0 || colorIndex >= _spriteByColor.Length) colorIndex = 0;
            if (_spriteByColor[colorIndex] == null) _spriteByColor[colorIndex] = BuildCircleSprite(CompanionIdentity.Colors[colorIndex].Value);
            return _spriteByColor[colorIndex];
        }

        // Кружок цвета бойца с тёмной обводкой (виден на любом фоне карты) — печётся раз на цвет.
        public static Sprite BuildCircleSprite(Color color) { return BuildCircleSprite(color, 32); }

        // Перегрузка с размером — переиспользует EnemyMapPins для обычного значка врага (меньше, 8px:
        // правка Ильи 25.10 22:40 «значки врагов на миникарте в 4 раза меньше», своих бойцов не трогаем).
        public static Sprite BuildCircleSprite(Color color, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float r = size / 2f;
            var px = new Color32[size * size];
            Color32 fill = color;
            var border = CompanionIdentity.IsDark(color) ? new Color32(235, 235, 235, 255) : new Color32(20, 20, 20, 255); // чёрному — светлая обводка
            var clear = new Color32(0, 0, 0, 0);
            // Ревью (major): было фикс. "- 2.5f" независимо от size — на маленьких значках (враги,
            // size=8) обводка съедала почти весь кружок (d>1.5 из r=4 — ~86% площади), цветная точка
            // оставалась крошечной. Толщина теперь от size — у значков бойцов (size=32) число то же (2.5).
            float borderW = Mathf.Clamp(size * 0.15f, 1f, 2.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r, dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    px[y * size + x] = d > r ? clear : d > r - borderW ? border : fill;
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }
    }
}

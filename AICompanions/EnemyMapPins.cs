using System;
using System.Collections.Generic;
using UnityEngine;

namespace AICompanions
{
    // Пины врагов на миникарте/большой карте (просьба Ильи, следом за цветными пинами спутников):
    // красный значок — обычный враг, крупный особый значок (ромб с жёлтой каймой) — опасный
    // (босс/2★/тролль). Тот же приём, что в CompanionMapPins (цвет «запечён» в спрайт — Minimap.UpdatePins
    // каждый кадр перезаписывает pin.m_iconElement.color, см. _research/ahead_death_base_map.md §3),
    // и та же проблема «спрайт назначается только при создании UI-элемента пина» — иконка решается
    // один раз, в момент создания пина, дальше не меняется (опасность существа за жизнь не меняется).
    // Существа — только Character.GetAllCharacters() (статический список игры), без сканов сцены.
    public static class EnemyMapPins
    {
        private static readonly Dictionary<Character, Minimap.PinData> _pins = new Dictionary<Character, Minimap.PinData>();
        private static readonly List<Character> _removeBuf = new List<Character>();
        private static Sprite _normalSprite, _dangerSprite;

        // 2 раза/с — зовёт Plugin.Update по тому же таймеру, что и CompanionMapPins.Tick().
        public static void Tick()
        {
            if (Minimap.instance == null || Player.m_localPlayer == null) { ClearAll(); return; }
            var player = Player.m_localPlayer;
            // ОДИН общий радиус с полосками HP (правка Ильи 25.10 22:40): на карте только те враги,
            // у кого видно имя/полоску — дальности совпадают, не два разных числа в двух местах.
            // radius<=0 — та же трактовка, что в EnemyHudRange («фича выключена»), а не «безлимит»
            // (ревью minor): иначе при 0 полоски HP гаснут, а враги на карте остаются по всей карте.
            float radius = Plugin.EnemyHudRadius.Value;
            if (radius <= 0f) { ClearAll(); return; }
            float r2 = radius * radius;

            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c is Player || c.IsDead()) continue;
                if (FollowerAI.Registry.ContainsKey(c)) continue; // свои бойцы — отдельно, CompanionMapPins
                if (!BaseAI.IsEnemy(player, c)) continue; // мирные звери на карту не лезут, только враги
                if ((c.transform.position - player.transform.position).sqrMagnitude > r2) continue; // вне радиуса HP-полосок

                Minimap.PinData pin;
                if (!_pins.TryGetValue(c, out pin) || pin == null)
                {
                    pin = CompanionMapPins.CreatePin(c.transform.position, Localization.instance.Localize(c.m_name));
                    if (pin == null) continue;
                    pin.m_icon = IsDangerous(c) ? DangerSprite() : NormalSprite();
                    _pins[c] = pin;
                }
                pin.m_pos = c.transform.position;
            }

            if (_pins.Count > 0)
            {
                _removeBuf.Clear();
                foreach (var kv in _pins)
                {
                    var c = kv.Key;
                    // Unity-null (== null) ловит и уничтоженный/выгруженный объект — не звать методы раньше проверки.
                    bool outOfRadius = c != null && (c.transform.position - player.transform.position).sqrMagnitude > r2;
                    if (c == null || c.IsDead() || FollowerAI.Registry.ContainsKey(c) || !BaseAI.IsEnemy(player, c) || outOfRadius)
                        _removeBuf.Add(c);
                }
                for (int i = 0; i < _removeBuf.Count; i++) Remove(_removeBuf[i]);
            }
        }

        public static void Remove(Character c)
        {
            Minimap.PinData pin;
            if (c == null || !_pins.TryGetValue(c, out pin)) return;
            _pins.Remove(c);
            if (Minimap.instance != null && pin != null) Minimap.instance.RemovePin(pin);
        }

        public static void ClearAll()
        {
            if (Minimap.instance != null)
            {
                foreach (var kv in _pins) if (kv.Value != null) Minimap.instance.RemovePin(kv.Value);
            }
            _pins.Clear();
        }

        // Plugin.OnDestroy (выгрузка мода/горячая перезагрузка).
        public static void Shutdown() { ClearAll(); }

        // Боссы, 2★ (GetLevel()==3: 1=база, 2=1★, 3=2★ — то же, что level_2/level_3 в EnemyHud),
        // тролль по имени префаба (крупный зверь, а не по списку — новых крупных мобов не пропустим).
        public static bool IsDangerous(Character c)
        {
            if (c == null) return false;
            if (c.IsBoss()) return true;
            if (c.GetLevel() >= 3) return true;
            string n = c.gameObject != null ? c.gameObject.name : "";
            return n.IndexOf("Troll", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---- для автотестов: текущее состояние пина без реверс-инжиниринга приватных полей игры ----
        public static bool HasPin(Character c) { return c != null && _pins.ContainsKey(c); }

        public static bool IsDangerPinned(Character c)
        {
            Minimap.PinData p;
            return c != null && _pins.TryGetValue(c, out p) && p != null && p.m_icon == _dangerSprite;
        }

        // Размеры — в 4 раза меньше прежних (правка Ильи 25.10 22:40, скрин клона): «совсем
        // маленькие, но различимые»; значков СВОИХ бойцов (CompanionMapPins) это не касается.
        private const int NormalSize = 8;  // было 32 (общая BuildCircleSprite)
        private const int DangerSize = 12; // было 46, крупнее обычного — «опасно» видно и так

        private static Sprite NormalSprite()
        {
            if (_normalSprite == null) _normalSprite = CompanionMapPins.BuildCircleSprite(new Color(0.95f, 0.12f, 0.08f), NormalSize);
            return _normalSprite;
        }

        // Ромб (не круг — форма отличается от спутников/обычных врагов на вид, не только цветом),
        // крупнее обычного значка, жёлтая кайма — «опасно».
        private static Sprite DangerSprite()
        {
            if (_dangerSprite == null) _dangerSprite = BuildDiamondSprite(new Color(0.9f, 0.05f, 0.05f), new Color(1f, 0.85f, 0.1f), DangerSize);
            return _dangerSprite;
        }

        private static Sprite BuildDiamondSprite(Color fill, Color border, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            float r = size / 2f;
            var px = new Color32[size * size];
            Color32 f = fill, b = border, clear = new Color32(0, 0, 0, 0);
            // Ревью (major): было фикс. "- 4.5f" независимо от size — после уменьшения DangerSize
            // 46→12 (правка Ильи) обводка съедала ~94% площади ромба, жёлтая кайма забивала цвет.
            // Толщина теперь от size: на старом крупном размере (46) число то же (4.5).
            float borderW = Mathf.Clamp(size * 0.1f, 1f, 4.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - r), dy = Mathf.Abs(y + 0.5f - r);
                    float d = dx + dy; // «ромбовое» расстояние (манхэттен)
                    px[y * size + x] = d > r ? clear : d > r - borderW ? b : f;
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }
    }
}

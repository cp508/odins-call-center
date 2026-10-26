using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace AICompanions
{
    // Отладочная визуализация (запрос Ильи 25.10): (1) круги-радиусы вокруг хозяина каждые 10м —
    // глазами понять, какой радиус обзора нужен; (2) зоны атак (фиолетовые) союзников/хозяина/врагов —
    // куда придётся удар, от начала замаха до удара +0.2с. Геометрия и тайминг — через уже готовые
    // AttackProfiles (GetCurrentAttack/IsHitDone, поля Attack) и Character.InAttack(), без своих
    // Harmony-патчей и без копирования логики тайминга/геометрии (брифинг, «используй их, не дублируй»).
    // Включение — Plugin.DebugRings/Plugin.AttackZones (в клонах по умолчанию вкл, у Ильи — клавишами).
    public static class DevViz
    {
        // ---- общее: один материал на все линии/меши (Sprites/Default — точно есть в игре) ----

        private static Material _mat;

        private static Material Mat()
        {
            if (_mat == null)
            {
                _mat = new Material(Shader.Find("Sprites/Default"));
                _mat.hideFlags = HideFlags.HideAndDontSave;
            }
            return _mat;
        }

        // Camera.main ищет объект с тегом MainCamera (в игре такого тега нет — см. осторожный
        // null-гард в DevTools.ApplyLowGraphics) и возвращал null, из-за чего подписи колец не
        // рисовались вовсе. Реальная камера игрока — приватное поле GameCamera (как m_distance в
        // DevTools.ApplyCloneView), берём так же через reflection, с Camera.main как запасной вариант.
        private static readonly FieldInfo GameCameraCamField = AccessTools.Field(typeof(GameCamera), "m_camera");

        private static Camera ViewCamera()
        {
            var gc = GameCamera.instance;
            if (gc != null && GameCameraCamField != null)
            {
                var c = GameCameraCamField.GetValue(gc) as Camera;
                if (c != null) return c;
            }
            return Camera.main;
        }

        public static void Tick(float dt)
        {
            RingsTick(dt, Plugin.DebugRings.Value);
            ZonesTick(dt, Plugin.AttackZones.Value);
        }

        // Выход из мира: объекты не у ZNetView, сами не исчезают — убрать явно (как DevTools/SelfTest).
        public static void OnWorldLeft()
        {
            RingsShutdown();
            ZonesClearAll(true);
        }

        // Горячая перезагрузка/выгрузка плагина — то же самое + сам материал.
        public static void Shutdown()
        {
            RingsShutdown();
            ZonesClearAll(true);
            foreach (var v in _pool) DestroyVisual(v);
            _pool.Clear();
            if (_mat != null) { Object.Destroy(_mat); _mat = null; }
        }

        // ======================================================================================
        // 1) КРУГИ-РАДИУСЫ
        // ======================================================================================

        private static readonly float[] RingRadii = { 10f, 20f, 30f, 40f, 50f, 60f };
        private const int RingSegments = 48;
        private const float RingPeriod = 0.2f; // 5 раз/с — обновление не каждый кадр
        // Свой цвет на каждый круг (просьба Ильи по скрину) — хорошо видны на траве, без зелёного
        // (слился бы с травой) и без красного (путается с «враг», см. правку цвета бойца №1).
        private static readonly Color[] RingColors =
        {
            new Color(0.00f, 0.85f, 1.00f), // 10м циан
            new Color(1.00f, 0.92f, 0.05f), // 20м жёлтый
            new Color(1.00f, 0.15f, 0.85f), // 30м пурпурный
            new Color(1.00f, 0.55f, 0.05f), // 40м оранжевый
            new Color(1.00f, 1.00f, 1.00f), // 50м белый
            new Color(0.35f, 0.45f, 1.00f), // 60м сине-индиго
        };

        // Радиус обзора, выбранный Ильёй по итогу осмотра кругов (25.10) — толще линия + своя подпись.
        private const float ChosenRadius = 50f;

        private class RingVisual { public GameObject Go; public LineRenderer Lr; public float Radius; public Color Color; }
        private static RingVisual[] _rings;
        private static float _ringTimer;

        private static void RingsTick(float dt, bool enabled)
        {
            if (!enabled) { RingsShutdown(); return; }
            EnsureRings();
            _ringTimer -= dt;
            if (_ringTimer > 0f) return;
            _ringTimer = RingPeriod;
            var player = Player.m_localPlayer;
            if (player == null) return;
            Vector3 center = player.transform.position;
            foreach (var r in _rings) UpdateRing(r, center);
        }

        private static void EnsureRings()
        {
            if (_rings != null) return;
            _rings = new RingVisual[RingRadii.Length];
            for (int i = 0; i < RingRadii.Length; i++)
            {
                var go = new GameObject("AIC_DevViz_Ring_" + (int)RingRadii[i]);
                go.hideFlags = HideFlags.HideAndDontSave;
                var lr = go.AddComponent<LineRenderer>();
                lr.material = Mat();
                lr.useWorldSpace = true;
                lr.loop = true;
                lr.positionCount = RingSegments;
                // Выбранный радиус обзора (ChosenRadius) — жирнее остальных, их видно легко, он — сразу заметен.
                lr.widthMultiplier = RingRadii[i] == ChosenRadius ? 0.35f : 0.15f;
                Color col = RingColors[i % RingColors.Length];
                lr.startColor = col;
                lr.endColor = col;
                lr.shadowCastingMode = ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lr.numCapVertices = 0;
                lr.numCornerVertices = 0;
                _rings[i] = new RingVisual { Go = go, Lr = lr, Radius = RingRadii[i], Color = col };
            }
        }

        private static void UpdateRing(RingVisual r, Vector3 center)
        {
            var zs = ZoneSystem.instance;
            // SetPositions(Vector3[]) у LineRenderer в этой версии Unity цепляет перегрузку через
            // Span<T>, а netstandard.dll в игре не даёт его разрешить (CS0012) — ставим по одной точке.
            for (int i = 0; i < RingSegments; i++)
            {
                float a = i * (Mathf.PI * 2f) / RingSegments;
                Vector3 p = center + new Vector3(Mathf.Sin(a) * r.Radius, 0f, Mathf.Cos(a) * r.Radius);
                float h;
                p.y = (zs != null && zs.GetGroundHeight(p, out h)) ? h + 0.15f : center.y + 0.15f;
                r.Lr.SetPosition(i, p);
            }
        }

        private static void RingsShutdown()
        {
            if (_rings == null) return;
            foreach (var r in _rings) if (r.Go != null) Object.Destroy(r.Go);
            _rings = null;
        }

        // ---- подписи расстояний (экранные, рисуются из Plugin.OnGUI с GUI.matrix=identity) ----

        private static GUIStyle _labelStyle, _labelShadow;

        public static void DrawRingLabels()
        {
            if (_rings == null) return;
            var player = Player.m_localPlayer;
            var cam = ViewCamera();
            if (player == null || cam == null) return;
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = false, clipping = TextClipping.Overflow };
                _labelShadow = new GUIStyle(_labelStyle);
                _labelShadow.normal.textColor = new Color(0f, 0f, 0f, 0.95f);
            }
            // Направление ОТ камеры ЧЕРЕЗ игрока и дальше — не «к камере» (в клоне камера всего в
            // ~8м от игрока, а круги от 10 до 60м — «ближняя к камере» точка на любом круге оказывается
            // ЗА камерой, sp.z<0, подпись никогда не рисуется; проверено логом cam=Main Camera camDist=8.3,
            // ring r=10 sp.z=-1.99). Точка ВДОЛЬ оси взгляда камеры остаётся перед ней при любом радиусе.
            Vector3 away = player.transform.position - cam.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = player.transform.forward;
            away.Normalize();
            var zs = ZoneSystem.instance;
            // Веер вокруг общей оси — иначе все 6 точек лежат на одном луче камеры и подписи
            // накладываются друг на друга стопкой (видно на первом скрине проверки).
            float fanStep = 12f;
            float fanStart = -fanStep * (_rings.Length - 1) / 2f;
            for (int ri = 0; ri < _rings.Length; ri++)
            {
                var r = _rings[ri];
                // Точка кольца вдоль оси взгляда камеры (через игрока, дальше от камеры) — остаётся
                // в кадре при любом радиусе, в отличие от «ближайшей к камере» точки.
                Vector3 dir = Quaternion.AngleAxis(fanStart + ri * fanStep, Vector3.up) * away;
                Vector3 pt = player.transform.position + dir * r.Radius;
                float h;
                pt.y = (zs != null && zs.GetGroundHeight(pt, out h)) ? h + 1.3f : player.transform.position.y + 1.3f;
                Vector3 sp = cam.WorldToScreenPoint(pt);
                if (sp.z <= 0.1f) continue; // за спиной камеры
                // WorldToScreenPoint даёт пиксели камеры (Dev.LowGraphics рендерит в 480p и апскейлит
                // NearestNeighbor'ом — cam.pixelWidth/Height НЕ равны Screen.width/height) — пересчитать
                // в реальные экранные пиксели, иначе подписи улетали в угол за пределы видимого окна.
                float pw = cam.pixelWidth > 0 ? cam.pixelWidth : Screen.width;
                float ph = cam.pixelHeight > 0 ? cam.pixelHeight : Screen.height;
                float gx = sp.x / pw * Screen.width;
                float gy = Screen.height - sp.y / ph * Screen.height;
                bool chosen = r.Radius == ChosenRadius;
                string text = chosen ? (int)r.Radius + " м — обзор" : (int)r.Radius + " м";
                _labelStyle.fontSize = chosen ? 32 : 28; // выбранный радиус — крупнее остальных
                _labelShadow.fontSize = _labelStyle.fontSize;
                // ширина — по реальному тексту (было фикс. 190 — «50 м — обзор» переносилось на 2-ю строку и обрезалось)
                Vector2 sz = _labelStyle.CalcSize(new GUIContent(text));
                var rect = new Rect(gx - sz.x / 2f, gy - sz.y / 2f, sz.x + 4f, sz.y);
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, _labelShadow);
                _labelStyle.normal.textColor = r.Color; // подпись тем же цветом, что и круг
                GUI.Label(rect, text, _labelStyle);
            }
        }

        // ---- помощники для автотеста devviz_rings ----

        public static int RingCount { get { return _rings != null ? _rings.Length : 0; } }

        public static bool TryGetRingRadius(int index, out float radius)
        {
            radius = 0f;
            if (_rings == null || index < 0 || index >= _rings.Length) return false;
            radius = _rings[index].Radius;
            return true;
        }

        // Горизонтальная (XZ) дистанция от игрока до вершины кольца — должна точно совпадать с радиусом
        // (высота — отдельно, по земле, на неё дистанция не влияет).
        public static bool TryGetRingHorizontalDistance(int index, int vertex, Vector3 from, out float dist)
        {
            dist = 0f;
            if (_rings == null || index < 0 || index >= _rings.Length) return false;
            var lr = _rings[index].Lr;
            if (vertex < 0 || vertex >= lr.positionCount) return false;
            Vector3 p = lr.GetPosition(vertex);
            p.y = from.y;
            dist = Vector3.Distance(p, from);
            return true;
        }

        // ======================================================================================
        // 2) ЗОНЫ АТАК
        // ======================================================================================

        private const float ZoneScanRadius = 65f;
        private const float ZoneScanPeriod = 0.1f; // 10 раз/с
        private const float ZoneHideDelay = 0.2f; // после удара
        private const float ZoneMaxLifetime = 4f; // предохранитель: замах прервали (стан/смерть) без OnAttackTrigger
        private static readonly Color32 ZoneColor = new Color32(170, 40, 220, 115); // фиолетовый, полупрозрачный
        private static readonly Color32 AllyZoneColor = new Color32(40, 150, 255, 115); // союзники (бойцы и хозяин) — голубой (Илья 26.10)
        private const int ConeSegments = 16;
        private const int CircleSegments = 24;

        private class ZoneVisual { public GameObject Go; public Mesh Mesh; }
        private class ZoneState { public Character Attacker; public Attack Atk; public ZoneVisual Visual; public float CreatedAt; public float HideAt = -1f; }

        private static readonly Dictionary<Attack, ZoneState> _active = new Dictionary<Attack, ZoneState>();
        private static readonly List<ZoneVisual> _pool = new List<ZoneVisual>();
        private static readonly List<Character> _zoneScanBuf = new List<Character>(64);
        private static readonly List<Attack> _expireBuf = new List<Attack>();
        private static float _zoneTimer;

        // Сколько зон всего показано за сессию (не сбрасывается) — для автотеста «число зон > 0».
        public static int EverCreated;
        public static int ActiveZoneCount { get { return _active.Count; } }

        private static void ZonesTick(float dt, bool enabled)
        {
            if (!enabled)
            {
                if (_active.Count > 0) ZonesClearAll(false);
                return;
            }
            _zoneTimer -= dt;
            if (_zoneTimer > 0f) return;
            _zoneTimer = ZoneScanPeriod;
            ScanZones();
            ExpireZones();
        }

        private static void ScanZones()
        {
            var player = Player.m_localPlayer;
            if (player == null) { ZonesClearAll(false); return; }
            ProcessAttacker(player);
            _zoneScanBuf.Clear();
            Character.GetCharactersInRange(player.transform.position, ZoneScanRadius, _zoneScanBuf);
            foreach (var c in _zoneScanBuf) ProcessAttacker(c);
        }

        private static void ProcessAttacker(Character c)
        {
            if (c == null || c.IsDead()) return;
            var body = c as Humanoid;
            if (body == null || !c.InAttack()) return;
            var atk = AttackProfiles.GetCurrentAttack(body);
            if (atk == null) return;

            ZoneState zs;
            if (!_active.TryGetValue(atk, out zs))
            {
                zs = new ZoneState { Attacker = c, Atk = atk, Visual = RentVisual(), CreatedAt = Time.time };
                _active[atk] = zs;
                EverCreated++;
            }
            if (zs.HideAt < 0f)
            {
                BuildZoneMesh(zs.Visual.Mesh, c, atk);
                // Момент удара — уже посчитан в AttackProfiles (хук на Humanoid.OnAttackTrigger,
                // без фильтра по типу персонажа — работает и для игрока, и для спутника, и для врага).
                if (AttackProfiles.IsHitDone(atk)) zs.HideAt = Time.time + ZoneHideDelay;
            }
        }

        private static void ExpireZones()
        {
            _expireBuf.Clear();
            float now = Time.time;
            foreach (var kv in _active)
            {
                var zs = kv.Value;
                bool gone = zs.Attacker == null || zs.Attacker.IsDead();
                bool timedOut = now - zs.CreatedAt > ZoneMaxLifetime;
                bool hitElapsed = zs.HideAt >= 0f && now >= zs.HideAt;
                if (gone || timedOut || hitElapsed) _expireBuf.Add(kv.Key);
            }
            foreach (var k in _expireBuf)
            {
                ReturnVisual(_active[k].Visual);
                _active.Remove(k);
            }
        }

        // enabled=false (переключили клавишей/конфигом) — просто вернуть в пул; destroy=true (выход
        // из мира/выгрузка) — уничтожить совсем (GameObject'ы этого мира больше не нужны).
        private static void ZonesClearAll(bool destroy)
        {
            foreach (var kv in _active)
            {
                if (destroy) DestroyVisual(kv.Value.Visual);
                else ReturnVisual(kv.Value.Visual);
            }
            _active.Clear();
        }

        // ---- геометрия (апроксимация полей Attack — как в AttackProfiles.InDangerZone, тот же приём) ----

        private static readonly List<Vector3> _vbuf = new List<Vector3>(32);
        private static readonly List<int> _tbuf = new List<int>(64);
        private static readonly List<Color32> _cbuf = new List<Color32>(32);

        private static bool BuildZoneMesh(Mesh mesh, Character attacker, Attack atk)
        {
            if (attacker == null || atk == null) return false;
            Transform t = attacker.transform;
            Vector3 fwd = t.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward; else fwd.Normalize();
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            Vector3 origin = t.position + right * atk.m_attackOffset;

            _vbuf.Clear();
            _tbuf.Clear();

            if (atk.m_attackType == Attack.AttackType.Area)
            {
                // Площадная атака: m_attackRange — смещение центра взрыва вперёд, m_attackRayWidth — радиус
                // (та же формула, что в AttackProfiles.InDangerZone).
                Vector3 center = origin + fwd * atk.m_attackRange;
                float radius = Mathf.Max(0.3f, atk.m_attackRayWidth);
                AddFanFull(center, fwd, right, radius, CircleSegments);
            }
            else if (atk.m_attackType == Attack.AttackType.Projectile || atk.m_attackType == Attack.AttackType.TriggerProjectile)
            {
                // Снаряд: полоса по направлению — точная дальность полёта неизвестна заранее, дальность
                // показа консервативная (m_attackRange, если задан, иначе разумный дефолт).
                float len = atk.m_attackRange > 0.5f ? atk.m_attackRange : 25f;
                float halfW = Mathf.Max(0.4f, atk.m_attackRayWidth > 0.05f ? atk.m_attackRayWidth : 1f);
                Vector3 sl = origin + right * halfW, sr = origin - right * halfW;
                Vector3 el = origin + fwd * len + right * halfW, er = origin + fwd * len - right * halfW;
                AddQuad(sl, sr, er, el);
            }
            else
            {
                // Horizontal/Vertical (ближний бой): конус по m_attackAngle/m_attackRange — неизвестный
                // угол (<=0.1) — широкий дефолт 60° (как в AttackProfiles.InDangerZone, консервативно).
                float range = Mathf.Max(0.5f, atk.m_attackRange);
                float half = atk.m_attackAngle > 0.1f ? atk.m_attackAngle / 2f : 60f;
                AddFanSector(origin, fwd, right, range, half, ConeSegments);
            }

            if (_vbuf.Count == 0) return false;

            var zs = ZoneSystem.instance;
            for (int i = 0; i < _vbuf.Count; i++)
            {
                Vector3 v = _vbuf[i];
                float h;
                v.y = (zs != null && zs.GetGroundHeight(v, out h)) ? h + 0.1f : t.position.y + 0.1f;
                _vbuf[i] = v;
            }

            _cbuf.Clear();
            bool ally = attacker is Player || FollowerAI.Registry.ContainsKey(attacker);
            Color32 zc = ally ? AllyZoneColor : ZoneColor;
            for (int i = 0; i < _vbuf.Count; i++) _cbuf.Add(zc);
            // Классическое API через массивы (vertices/triangles/colors32) — overload'ы Mesh.Set*(List<T>)
            // в этой версии Unity тоже требуют Span<T>, который netstandard.dll игры не разрешает (CS0012).
            mesh.Clear();
            mesh.vertices = _vbuf.ToArray();
            mesh.triangles = _tbuf.ToArray();
            mesh.colors32 = _cbuf.ToArray();
            mesh.RecalculateBounds();
            return true;
        }

        private static void AddFanSector(Vector3 apex, Vector3 fwd, Vector3 right, float radius, float halfAngleDeg, int segments)
        {
            int baseIdx = _vbuf.Count;
            _vbuf.Add(apex);
            float half = halfAngleDeg * Mathf.Deg2Rad;
            for (int i = 0; i <= segments; i++)
            {
                float a = -half + i * (2f * half) / segments;
                _vbuf.Add(apex + fwd * (Mathf.Cos(a) * radius) + right * (Mathf.Sin(a) * radius));
            }
            for (int i = 1; i <= segments; i++)
            {
                _tbuf.Add(baseIdx); _tbuf.Add(baseIdx + i); _tbuf.Add(baseIdx + i + 1);
            }
        }

        private static void AddFanFull(Vector3 center, Vector3 fwd, Vector3 right, float radius, int segments)
        {
            int baseIdx = _vbuf.Count;
            _vbuf.Add(center);
            for (int i = 0; i <= segments; i++)
            {
                float a = i * (Mathf.PI * 2f) / segments;
                _vbuf.Add(center + fwd * (Mathf.Cos(a) * radius) + right * (Mathf.Sin(a) * radius));
            }
            for (int i = 1; i <= segments; i++)
            {
                _tbuf.Add(baseIdx); _tbuf.Add(baseIdx + i); _tbuf.Add(baseIdx + i + 1);
            }
        }

        private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int baseIdx = _vbuf.Count;
            _vbuf.Add(a); _vbuf.Add(b); _vbuf.Add(c); _vbuf.Add(d);
            _tbuf.Add(baseIdx); _tbuf.Add(baseIdx + 1); _tbuf.Add(baseIdx + 2);
            _tbuf.Add(baseIdx); _tbuf.Add(baseIdx + 2); _tbuf.Add(baseIdx + 3);
        }

        // ---- пул визуалов (переиспользуем меши/GameObject'ы — без аллокаций каждый кадр) ----

        private static ZoneVisual RentVisual()
        {
            for (int i = _pool.Count - 1; i >= 0; i--)
            {
                var v = _pool[i];
                _pool.RemoveAt(i);
                if (v.Go == null) continue; // объект убит снаружи (горячая перезагрузка) — пропустить
                v.Go.SetActive(true);
                return v;
            }
            var go = new GameObject("AIC_DevViz_Zone");
            go.hideFlags = HideFlags.HideAndDontSave;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mr.material = Mat();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            var mesh = new Mesh();
            mesh.MarkDynamic();
            mf.mesh = mesh;
            return new ZoneVisual { Go = go, Mesh = mesh };
        }

        private static void ReturnVisual(ZoneVisual v)
        {
            if (v.Go != null) v.Go.SetActive(false);
            _pool.Add(v);
        }

        private static void DestroyVisual(ZoneVisual v)
        {
            if (v.Mesh != null) Object.Destroy(v.Mesh);
            if (v.Go != null) Object.Destroy(v.Go);
        }
    }
}

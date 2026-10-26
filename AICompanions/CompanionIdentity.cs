using UnityEngine;

namespace AICompanions
{
    // Роль бойца — влияет только на имя/UI (боевое поведение по ролям — будущий этап).
    public enum CompanionRole { Tank, Universal, Archer, Fighter }

    // Имя бойца = "<Роль> <Цвет>" (просьба Ильи): Role по умолчанию — из стартового снаряжения,
    // Color — уникален среди живых бойцов. Единое место для роли/цвета — здесь же их будет читать
    // HUD/меню Ё (обводка строки), подпись над головой и метки на карте (этап «цвет виден везде»).
    public static class CompanionIdentity
    {
        private static readonly string[] RoleNames = { "Танк", "Универсал", "Лучник", "Боец" };

        public struct ColorDef
        {
            public string Name;
            public Color Value;
            public ColorDef(string name, Color value) { Name = name; Value = value; }
        }

        // 9 хорошо различимых цветов — ровно по лимиту MaxCompanions, всем хватает уникального.
        public static readonly ColorDef[] Colors = new ColorDef[]
        {
            new ColorDef("Чёрный",      new Color(0.06f, 0.06f, 0.06f)), // не «Красный» (красные имена у врагов); Илья 25.10 выбрал чёрный
            new ColorDef("Синий",       new Color(0.20f, 0.40f, 1.00f)),
            new ColorDef("Зелёный",     new Color(0.15f, 0.80f, 0.20f)),
            new ColorDef("Жёлтый",      new Color(1.00f, 0.90f, 0.10f)),
            new ColorDef("Фиолетовый",  new Color(0.60f, 0.20f, 0.90f)),
            new ColorDef("Оранжевый",   new Color(1.00f, 0.55f, 0.00f)),
            new ColorDef("Голубой",     new Color(0.20f, 0.85f, 0.95f)),
            new ColorDef("Розовый",     new Color(1.00f, 0.40f, 0.70f)),
            new ColorDef("Белый",       new Color(0.95f, 0.95f, 0.95f)),
        };

        public static string RoleName(CompanionRole r)
        {
            int i = (int)r;
            return i >= 0 && i < RoleNames.Length ? RoleNames[i] : "?";
        }

        // Кнопка в меню Ё крутит роль по кругу: Танк → Универсал → Лучник → Боец → Танк.
        public static CompanionRole NextRole(CompanionRole r)
        {
            return (CompanionRole)(((int)r + 1) % RoleNames.Length);
        }

        // Тёмный цвет (чёрный) на тёмной плашке не виден — такие полоски/метки рисуем со светлой обводкой
        public static bool IsDark(Color c)
        {
            return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b < 0.3f;
        }

        public static string BuildName(CompanionRole role, int colorIndex)
        {
            string color = colorIndex >= 0 && colorIndex < Colors.Length ? Colors[colorIndex].Name : "?";
            return RoleName(role) + " " + color;
        }
    }
}

using System;
using System.Reflection;

namespace AICompanions
{
    // Humanoid.EquipItem/UnequipItem — у игры объявлены public, но csc против ref-сборок игры их
    // прямым вызовом не даёт (нужен reflection). БАГ, который уже чинили в FollowerAI (commit
    // e9d83a9, "EquipItem — публичный метод, reflection искал только NonPublic") и ЗАНОВО внесли
    // при переносе сюда: поиск только по BindingFlags.NonPublic на PUBLIC методе молча возвращает
    // null — EquipMethod/UnequipMethod оставались null, набор класса никогда не экипировался,
    // спутник весь бой держал дефолтный факел (см. _research/classes_journal.md). НЕ убирать Public.
    public static class CompanionEquip
    {
        private static readonly MethodInfo EquipMethod = typeof(Humanoid).GetMethod("EquipItem",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(ItemDrop.ItemData), typeof(bool) }, null);
        private static readonly MethodInfo UnequipMethod = typeof(Humanoid).GetMethod("UnequipItem",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(ItemDrop.ItemData), typeof(bool) }, null);

        // true = экипировано прямо сейчас. false = штатный отказ (Character.InAttack()/InDodge() в
        // этот кадр) — не ошибка, вызывающий код сам решает, повторить позже или промолчать.
        public static bool TryEquip(Humanoid body, ItemDrop.ItemData item)
        {
            if (EquipMethod == null || body == null || item == null) return false;
            try { return (bool)EquipMethod.Invoke(body, new object[] { item, false }); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("EquipItem(" + (item.m_shared != null ? item.m_shared.m_name : "?") + "): " + ex.Message);
                return false;
            }
        }

        public static void Unequip(Humanoid body, ItemDrop.ItemData item)
        {
            if (UnequipMethod == null || body == null || item == null) return;
            try { UnequipMethod.Invoke(body, new object[] { item, false }); }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("UnequipItem(" + (item.m_shared != null ? item.m_shared.m_name : "?") + "): " + ex.Message);
            }
        }
    }
}

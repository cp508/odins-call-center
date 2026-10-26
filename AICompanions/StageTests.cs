namespace AICompanions
{
    // Точка регистрации автотестов этапов: каждый этап добавляет сюда строку вида
    //   CombatTests.Register();
    // а сами сценарии держит в своём файле (SelfTest.Register("имя", Метод, таймаут)).
    public static class StageTests
    {
        public static void RegisterAll()
        {
            Stage2b2Tests.Register();
            CombatV2aTests.Register();
            RecallTests.Register();
            StaminaTests.Register();
            ClassesTests.Register();
            DevVizTests.Register();
            Hud150Tests.Register();
            DevToolsTests.Register();
            SensorTests.Register();
            DeathTests.Register();
            SkillsTests.Register();
            ProposalsDumpTests.Register();
        }
    }
}

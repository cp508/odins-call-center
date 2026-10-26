# Odin's Call Center — AICompanions для Valheim

ИИ-напарники с телом игрока: ходят за тобой, дерутся, едят, качают навыки, умирают и возрождаются. В разработке.

## Установка
1. BepInEx 5 для Valheim (r2modman / Thunderstore).
2. Собрать (ниже), затем:
   - `AICompanionsLoader.dll` → `BepInEx\plugins\AICompanionsLoader\`
   - `AICompanions.dll` → `BepInEx\AICompanionsCore\`

## Сборка (Windows, SDK не нужен)
```
powershell -ExecutionPolicy Bypass -File build.ps1 -BepInEx "<профиль>\BepInEx" [-Valheim "<папка Valheim>"] [-Install]
```
Результат — `out\`. `-Install` сразу кладёт DLL в профиль. Пересобрать при запущенной игре: `-Install`, затем строка `reload` в `BepInEx\aic_cmd.txt`.

## Клавиши
J призвать · K все ко мне · U защита/пассив · I подробности · ` (Ё) меню · N сенсор · Y круги дальности · H зоны атак

Настройки: `BepInEx\config\com.aicompanions.valheim.cfg`.

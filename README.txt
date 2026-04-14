DanceMode
=============

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  ДЛЯ ПОЛЬЗОВАТЕЛЕЙ — папка "Ready To Use"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Требования:
  • Script Hook V            — http://www.dev-c.com/gtav/scripthookv/
  • Script Hook V .NET 3     — https://github.com/scripthookvdotnet/scripthookvdotnet-nightly/releases
  • LemonUI.SHVDN3.dll       — https://gta5-mods.com/tools/lemonui

Установка:
  1. Установите Script Hook V и Script Hook V .NET в корень GTA V
  2. Скачайте LemonUI и скопируйте LemonUI.SHVDN3.dll в GTA V\scripts\
  3. Скопируйте DanceMode.dll в GTA V\scripts\
  4. Папка DanceModeSaves создаётся автоматически при первом автосохранении

В GTA V\scripts\ должно быть:
  • DanceMode.dll
  • LemonUI.SHVDN3.dll

==========================
  ИСПОЛЬЗОВАНИЕ
==========================

Горячие клавиши:
  • Y — открыть/закрыть главное меню
  • Backspace — навигация назад в подменю
  • F5 — отладочная информация

Главное меню (DanceMode):
  • Добавить педа — добавить NPC в список танцующих
  • Выбрать танец — выбрать танец из FavouriteAnims.xml (Menyoo)
  • Сохранить танец — ручное сохранение (или Ctrl+S)
  • Загрузить автосохранение — загрузить сохранённый танец

Танцы:
  • Все танцы загружаются из FavouriteAnims.xml (Menyoo)
  • Мод автоматически ищет файл в: scripts/, menyooStuff/, корне игры

Автосохранение:
  • Сохраняет танец и позиции педов при выходе
  • При загрузке карты мод находит педов и запускает танец
  • Файлы: scripts/DanceModeSaves/AutoSave.json

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  ДЛЯ РАЗРАБОТЧИКОВ — папка "Source Code"
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Исходный код проекта, решение Visual Studio и зависимости.
Сборка через DanceMode.csproj в Visual Studio (x64, .NET Framework 4.8).

Или через командную строку:
  cd "Source Code"
  dotnet build -c Release

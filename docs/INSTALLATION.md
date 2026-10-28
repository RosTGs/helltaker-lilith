# Установка Helltaker — Лилит

## Требования

Windows x64, собственная установленная Helltaker из Steam и [BepInEx 5.4.23.6 Windows x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6). Проверенная версия игры: build 6791993, Unity 2019.2.9f1 Mono.

## Подготовка отдельной копии

1. В Steam откройте Helltaker → свойства → установленные файлы → обзор.
2. Скопируйте всю папку игры в отдельный каталог, например `C:/Games/Helltaker-Lilith`.
3. Сохраните резервную копию своего профиля. Ручной профиль мода после запуска хранится в `HelltakerAILab/profile.json`.
4. Распакуйте `BepInEx_win_x64_5.4.23.6.zip` рядом с `Helltaker.exe` в отдельной копии.
5. Распакуйте архив мода из [Releases](https://github.com/RosTGs/helltaker-lilith/releases/latest) в ту же папку, объединяя каталоги.

Итоговая структура:

```text
Helltaker-Lilith/
  Helltaker.exe
  Helltaker_Data/
  winhttp.dll
  doorstop_config.ini
  BepInEx/
    core/
    plugins/HelltakerAILab/
      HelltakerAILab.dll
      Newtonsoft.Json.dll
  HelltakerAILab/
    visuals.json
    localization.json
    packs/lilith/
    packs/lilith-breathing/
    localizations/ru-lilith/
  Play-Lilith.cmd
```

`winhttp.dll`, `doorstop_config.ini` и `BepInEx/core` поставляются в архиве BepInEx. Папки игры создаются копированием собственной установки. Архив мода содержит плагин, пакеты, команды запуска и документацию.

## Запуск

Откройте Steam под владельцем Helltaker и запустите `Play-Lilith.cmd`. Команда использует папку своего расположения, первый монитор, Direct3D 11 и окно 1280×720. Если появляется штатный выбор экрана Unity, выберите разрешение 1280×720 и оконный режим.

Для проверки загрузки откройте `BepInEx/LogOutput.log`: строки `Localization ru-lilith: 47 files, 586 lines` и `Visual pack lilith` подтверждают подключение пакетов.

Меню и диалоги используют русский текст; варианты ответов сохраняют исходную логику игры. Образ и имя главной героини — Лилит. Examtaker использует образ Лилит в роли подопытной № 67.

## Обновление

Завершите игру. Скопируйте `HelltakerAILab/profile.json`, `visuals.json`, `localization.json` и собственные пакеты в резервную папку. Распакуйте новый релиз с заменой файлов. Затем восстановите выбранные настройки и пользовательские пакеты из резервной копии.

## Переключение и возврат

`Use-Dance.cmd`, `Use-Breathing.cmd` и `Use-Original-Visuals.cmd` выбирают графику. Нажатие F8 в игре применяет выбор. `Use-Russian.cmd` и `Use-Original-Text.cmd` выбирают текст для следующего запуска.

Для исходной Helltaker запустите сохранённую установку через Steam. Для отключения плагина в тестовой копии завершите игру и перенесите папку `BepInEx/plugins/HelltakerAILab` в резервный каталог за пределами `BepInEx/plugins`.

## Локальный интерфейс

Мод создаёт `HelltakerAILab/connection.json` с адресом `127.0.0.1`, портом и токеном текущего процесса. Этот файл предназначен для локального Python-клиента. Храните его рядом с установленным модом. Учебные эпизоды отделяют прогресс агента от ручного профиля; перезапуск игры возвращает обычный игровой режим.

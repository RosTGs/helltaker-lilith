# Интерфейс агента и сборка

## Сборка плагина

Установите .NET SDK с поддержкой SDK-style проектов и подготовьте копию Helltaker с BepInEx 5.4.23.6. NuGet загрузит reference assemblies для .NET Framework 4.7.2.

```powershell
dotnet build src/HelltakerAILab.csproj -c Release -p:GameDir="C:/Games/Helltaker-Lilith" -p:LoaderDir="C:/Games/Helltaker-Lilith"
python scripts/package_release.py
```

Результат: `dist/Helltaker-Lilith-v0.2.0.zip` и файл SHA-256. Упаковщик берёт собственный плагин, Newtonsoft.Json с лицензией MIT, готовые пакеты и команды запуска. Игровые сборки используются как ссылки при компиляции и остаются в пользовательской установке.

## Python-клиент

```powershell
python -m venv .venv
.venv/Scripts/python -m pip install -e .
$env:HELLTAKER_CONNECTION = "C:/Games/Helltaker-Lilith/HelltakerAILab/connection.json"
.venv/Scripts/python -m helltaker_lab observe --full
.venv/Scripts/python -m helltaker_lab scenes
.venv/Scripts/python -m helltaker_lab reset 1
.venv/Scripts/python -m helltaker_lab step down
.venv/Scripts/python -m helltaker_lab render frame.png
.venv/Scripts/python -m helltaker_lab release
```

Один клиент управляет одним эпизодом. После каждого `step` используйте свежий `observation`. Поля головоломки: `player.cell`, `will`, `entities`, `ready`, `goal_reached`. Диалоги: `dialogues`, действия `confirm`, `up`, `down`. Завершайте управление командой `release`; обычный профиль восстанавливается при перезапуске игры.

| Команда | Назначение |
|---|---|
| `observe`, `scenes`, `hierarchy` | Состояние, сцены и объекты |
| `reset`, `step`, `release` | Управление эпизодом |
| `render` | Фактический кадр Unity |
| `catalog`, `export_texture` | Каталог и экспорт текстур собственной игры |
| `visual_status`, `reload_visuals` | Проверка и обновление графики |
| `localization_status` | Пакет перевода и параметры текста |

`player_visual` сообщает кадр, спрайт, позицию, опору, скорость анимации и геометрию. `idle_sync` содержит трек, BPM, фазу и номер кадра. `step` с `trace_animation: true` возвращает покадровую трассу.

## Обучение

Для локальных экспериментов: `pip install -e ".[learning]"`. Модули `puzzle.py`, `learning.py` и `gym_env.py` предоставляют симулятор, обучение по демонстрациям, экспериментальный DQN и Gymnasium.

Головоломки I–VIII проверены на 196 переходах с Unity. Политика по демонстрациям прошла главу I за 23 действия. Веса и частные трассы хранятся в локальном окружении исследователя. Самостоятельная политика всей кампании, диалоги и босс — дальнейшие направления работы.

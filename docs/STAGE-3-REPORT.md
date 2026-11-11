# Этап 3 — Интерфейс: отчёт

## Итог

Интерфейс собран и проверяется автоматически: решение собирается **без ошибок и без предупреждений**,
тесты проходят (`37/37`), UI-тесты запускаются headless (без окна) и проверяют, что дизайн-система
реально загружается и применяется к контролам.

| Проверка | Команда | Результат |
| --- | --- | --- |
| Сборка | `dotnet build EHOT.Launcher.slnx -c Debug` | 0 ошибок, 0 предупреждений |
| Тесты | `scripts/build.sh test` | 37/37 (29 unit + 8 headless UI) |

## Что сделано

### Дизайн-система (`DesignSystem/Tokens.axaml`, `DesignSystem/Controls.axaml`)
- Токены: фон `#0B0D13` / `#151923` / `#20263A`, акценты `#8B5CF6` и `#38BDF8`, текст `#F8FAFC` / `#94A3B8`,
  статусы `#34D399` / `#FBBF24` / `#F87171`, радиусы 8/12/18/24/pill, градиент `AccentGradient`.
- `ControlTheme` (`EhotButton`, `EhotPrimaryButton`, `EhotGhostButton`, `EhotDangerButton`, `EhotIconButton`,
  `EhotTextBox`, `NavItemTheme`) перенесены в `Styles.Resources`: в Avalonia 12 `ControlTheme` не может лежать
  напрямую в корне `<Styles>`.
- Тень кнопки живёт на темплейтном `Border` (`^ /template/ Border#Root`), потому что `BoxShadow` не является
  свойством `Button`.
- `EhotTextBox` больше не переопределяет шаблон целиком, а наследует Fluent (`BasedOn`): свои шаблоны
  TextBox в Avalonia 12 требуют `PART_TextPresenter`/`PART_TextBox` и легко ломаются. Переопределены только
  палитра, геометрия и `PlaceholderForeground`.

### Оболочка и навигация
- Кастомный titlebar (`ExtendClientAreaToDecorationsHint`), кнопки окна, индикатор Game Network.
- Реестр навигации на 13 разделов, ленивое создание ViewModel, загрузка данных в `OnNavigatedToAsync`.
- Панель «Состояние систем» выводит настоящие `Capability` из `ICapabilityRegistry`
  (доступно / ограничено / недоступно + причина) вместо несуществующего типа `CapabilityRow` —
  теперь это реальный `CapabilityRow`, оборачивающий `EHOT.Launcher.Core.Platform.Capability`.
- Глобальный поиск: пингвин-бар в titlebar + `Ctrl/Cmd+K`.

### Экраны
Главная, Играть, Версии, Моды, Сборки, Текстуры, Шейдеры, Карты, Game Network, Аккаунты, Загрузки,
Настройки, О программе. Пять разделов каталога — тонкие хосты одного `ContentBrowseView`
(одна логика поиска/фильтров/установки на все категории).

### Совместимость с Avalonia 12 (что пришлось исправить)
| Было | Стало | Причина |
| --- | --- | --- |
| `ExtendClientAreaChromeHints="NoChrome"` | удалено | свойства нет в 12.x |
| `TextBox.CharacterCasing` | нормализация `JoinCode` в VM | свойства нет в 12.x |
| `Watermark` | `PlaceholderText` | `Watermark` устарел |
| `ControlTheme` в корне `Styles` | `Styles.Resources` | иначе «unable to find suitable setter … Content» |
| `BoxShadow` у `Button` | тень на темплейтном `Border` | не свойство Button |
| собственный шаблон TextBox | `BasedOn="{StaticResource {x:Type TextBox}}"` | иначе отсутствует `PART_TextPresenter` |

## Инфраструктура, добавленная по ходу

- Артефакты сборки вынесены в `build/` (`BaseOutputPath`/`BaseIntermediateOutputPath` в `Directory.Build.props`):
  раньше `bin/obj` на 12 проектов давали ~1.3 ГБ, что упиралось в квоту рабочего пространства.
- `scripts/ensure-sdk.sh` ставит .NET 10.0.403 в `$HOME/.cache/dotnet` и задаёт `TMPDIR` внутри проекта:
  в песочнице `/tmp` — маленький tmpfs, распаковка SDK из него падала с «No space left on device».
- Тестовые проекты переведены на **xUnit v3** — `Avalonia.Headless.XUnit` 12.1.6 собран на v3,
  иначе типы `xunit` дублируются (CS0433).
- `EHOT.Launcher.UI.Tests` получил `AssemblyName`/`UseAppHost`: без этого app host тестов «прятался»
  за app host UI-проекта (у обоих `EHOT.Launcher`), и запуск тестов падал с «Could not find app host executable».

## Побочные улучшения качества (по требованию «не игнорировать предупреждения»)

- `IHttpService` получил параметр `headers`, `CurseForgeProvider` теперь ходит через общий HTTP-слой
  (пул соединений, таймауты, повторы, белый список хостов) вместо собственного `HttpClient`;
  `x-api-key` передаётся заголовком. `JavaRuntimeManager` кэширует `CompositeFormat`.
- Удалены неиспользуемые зависимости конструкторов (`GameInstaller`, `LoaderInstaller`, `GameLauncher`,
  `ContentInstallService`) — DI собирает граф сам, а тесты стали проще.
- Устранены CA1822/CA1826/CA1860/CA1862/CA1068/CS0067/CS9113 без подавлений, кроме трёх осознанных
  мест с комментарием-обоснованием: фабрики `OperationResult<T>.Ok/Fail/FromException` (CA1000) и
  неиспользуемое событие `StatsUpdated` в null/macOS-адаптерах (CS0067).

## Что осталось за границей этапа

- Тесты API через `WebApplicationFactory`, тесты `RuleEvaluator`/`LaunchArgumentBuilder`, нагрузочные
  проверки Download Manager.
- Подпись Windows-драйвера туннеля, macOS Network Extension, нотаризация, публичный relay — требуют
  инфраструктуры и секретов, вне репозитория. Лаунчер в этих случаях честно сообщает об ограничении.

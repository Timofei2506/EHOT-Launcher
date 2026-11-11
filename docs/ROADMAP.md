# EHOT Launcher — ROADMAP

> Легенда: ✅ готово и проверено сборкой/тестами · 🟡 в работе · ⬜ запланировано · ⚠️ ограничено внешними факторами

## Этап 1 — Исследование и архитектура ✅
- [x] Проверка .NET 10 (SDK 10.0.403, LTS, поддержка до 14.11.2028)
- [x] Проверка Avalonia 12.1.6, CommunityToolkit.Mvvm 8.4.2, EF Core 10, xUnit v3 3.2.2 (Avalonia.Headless.XUnit собран на v3)
- [x] Проверка внешних API: piston-meta (release 26.3), Modrinth, Mojang news
- [x] `docs/TECHNICAL-ANALYSIS.md`, `docs/ARCHITECTURE.md`, `docs/GAME-NETWORK-ARCHITECTURE.md`
- [x] Оценка рисков Windows/macOS, границы честности

## Этап 2 — Основа ✅
- [x] Монорепозиторий `EHOT-Launcher`, solution `EHOT.Launcher.slnx`
- [x] Central Package Management (`Directory.Packages.props`), `Directory.Build.props`, `global.json`
- [x] 11 проектов из ТЗ + `Platform.Linux` (для CI) + `UI.Tests`
- [x] DI-контейнер, MVVM-каркас, `ViewLocator`, `NavigationService`
- [x] Публикация под `win-x64`, `osx-arm64`, `osx-x64`
- [x] Сборка и тесты зелёные: `dotnet build EHOT.Launcher.slnx` — 0 ошибок, 0 предупреждений

## Этап 3 — Интерфейс ✅ (первый проход)
- [x] Дизайн-система: токены, кнопки, карточки, поля, бейджи, прогресс, тосты, диалоги, пустые состояния
- [x] Кастомный titlebar, glassmorphism-панели, адаптивная навигация
- [x] Главная страница (логотип, аккаунт, профиль, версия, ИГРАТЬ, статус, новости, тренды, недавнее, Game Network)
- [x] Экраны: Играть, Версии, Моды, Сборки, Текстуры, Шейдеры, Карты, Game Network, Аккаунты, Загрузки, Настройки, О программе
- [x] Глобальный поиск (пингвин-бар + Ctrl+K)
- [x] Дизайн-система переведена на `Styles.Resources` (ControlTheme как ресурсы) — совместимо с Avalonia 12
- [x] Поле ввода наследует шаблон Fluent (`BasedOn`) вместо собственного шаблона; `Watermark` → `PlaceholderText`
- [x] Панель «Состояние систем» показывает реальные `Capability` (доступно / ограничено / недоступно)
- [x] **Вход по ссылке-приглашению**: `ehot://join/CODE` (установщик, браузер, ярлык) подставляет код
      в Game Network; `ehot://install?file=…` открывает установку модпака; один экземпляр приложения
- [x] Копирование адреса комнаты в настоящий системный буфер обмена (через `IClipboardService`)

## Этап 4 — Minecraft Engine ✅
- [x] Манифест версий и правило-движок (`rules`, os/features)
- [x] Установка: client, libraries, natives, assets (проверка SHA-1, атомарная запись)
- [x] Fabric / Quilt / Forge / NeoForge
- [x] Автообнаружение Java, подбор JRE по `javaVersion`, скачивание Adoptium
- [x] Запуск: classpath, JVM/game args, логи, ошибки, история запусков
- [x] Профили: независимые папки, RAM, JVM-аргументы, лоадер

## Этап 5 — Контент ✅
- [x] Modrinth: поиск, проект, версии, категории, загрузка
- [x] CurseForge: провайдер за ключом, иначе `Unavailable`
- [x] Dependency resolver + конфликты + проверка хэшей
- [x] Установка/удаление/обновление контента, `SafeZip` против zip-slip/bomb
- [x] Modpack manager: `.mrpack` импорт/экспорт, бэкапы, клонирование

## Этап 6 — EHOT Game Network 🟡
- [x] Control Plane (ASP.NET Core + PostgreSQL + EF Core + JWT + rate limit)
- [x] Комнаты, инвайты с HMAC, участники, кик, отзыв, heartbeat, WebSocket-события
- [x] Клиент: `RoomSession`, переподключение, латентность, статистика
- [x] Windows-адаптер (Wintun/WireGuard, elevated helper) — **Experimental**, требует подписи драйвера и прогона
- [x] macOS-адаптер (NEPacketTunnelProvider + fallback) — **Experimental**, требует entitlement
- [x] LAN-интеграция Minecraft (адрес комнаты, копирование, UDP-детект 4445)
- ⬜ Relay-узел: референсная реализация UDP-релея в `tools/relay/` (в работе)
- ⚠️ Публичная relay-инфраструктура: требует хостинга, не входит в репозиторий

## Этап 7 — Тестирование 🟡
Что реально покрыто тестами (43 теста, `scripts/build.sh test`):
- [x] `PathGuardTests` — zip-slip, абсолютные пути, соседние каталоги с общим префиксом
- [x] `SafeZipExtractorTests` — нормальный архив, zip-slip, симлинки, лимит записей, лимит размера, отмена, чтение индекса
- [x] `DependencyResolverTests` — совместимость по версии игры/лоадеру, «универсальные» версии
- [x] `InviteServiceTests` — генерация base32-кодов, HMAC-хэш и постоянное сравнение, подсеть комнаты `10.{64..79}.0.0/24`, исчерпание адресов
- [x] `UiSmokeTests` (headless Avalonia) — загрузка `App.axaml` + дизайн-системы, рендер кнопки на `ControlTheme`, форматирование размеров
- [x] `ControlPlaneApiTests` — интеграционные тесты через `WebApplicationFactory` (SQLite): регистрация identity,
      создание комнаты, вступление по коду, отказ при неизвестном коде, закрытие комнаты владельцем, защита эндпоинтов
      (найдено и исправлено: запросы с `DateTimeOffset.UtcNow` в предикате EF падали в 500 на SQLite)
- [ ] Тесты правил Mojang и сборки аргументов запуска (`RuleEvaluator`, `LaunchArgumentBuilder`)
- [ ] Нагрузочные проверки Download Manager (параллелизм, повторы, докачка по Range)

## Этап 8 — Релиз 🟡
- [x] CI: matrix windows/macos/ubuntu (build + test + publish + installers) — все 9 работ зелёные
- [x] **Установщик Windows** (`packaging/windows/ehot-launcher.iss`, Inno Setup): ярлыки, удаление,
      лицензия в мастере, ассоциация `ehot://`
- [x] **macOS .app + .dmg** (`packaging/macos/make-dmg.sh`): бандл с `Info.plist` и `.icns`,
      образ с drag-and-drop в Applications, ad-hoc подпись для локального запуска
- [x] Иконка приложения `.icns` (генерируется из PNG), `LICENSE.txt`
- [x] Релиз `v0.1.0-beta.1` с 6 файлами (3 zip + 3 установщика)
- [ ] Нотаризация Apple (требует Developer ID) и подпись Windows EV — нужны сертификаты и secrets
- [ ] Автообновление (signed manifest + delta)

## Журнал верификации

| Проверка | Команда | Результат |
| --- | --- | --- |
| Сборка решения | `dotnet build EHOT.Launcher.slnx -c Debug` | 0 ошибок, 0 предупреждений |
| Тесты | `dotnet test EHOT.Launcher.slnx` | 43/43 пройдено (`EHOT.Launcher.Tests` 35, `EHOT.Launcher.UI.Tests` 8) |
| Исходники в git | `git push origin main` | github.com/Timofei2506/EHOT-Launcher (private) |

Ограничения, которые остаются честными (не «сделано», пока нет подписи/хостинга):
- Windows-туннель требует подписанного драйвера Wintun/VPN-плагина; macOS — подписанного Network Extension
  (`NEPacketTunnelProvider`). Без этого лаунчер сообщает о деградации, а не имитирует подключение.
- Публичный relay-узел требует хостинга; в репозитории только клиент и control-plane.
- Apple notarization и Windows EV-подпись выполняются в CI с секретами, локально не воспроизводятся.

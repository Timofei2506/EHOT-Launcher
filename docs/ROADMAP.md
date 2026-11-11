# EHOT Launcher — ROADMAP

> Легенда: ✅ готово и проверено сборкой/тестами · 🟡 в работе · ⬜ запланировано · ⚠️ ограничено внешними факторами

## Этап 1 — Исследование и архитектура ✅
- [x] Проверка .NET 10 (SDK 10.0.403, LTS, поддержка до 14.11.2028)
- [x] Проверка Avalonia 12.1.6, CommunityToolkit.Mvvm 8.4.2, EF Core 10, xUnit 2.9.3
- [x] Проверка внешних API: piston-meta (release 26.3), Modrinth, Mojang news
- [x] `docs/TECHNICAL-ANALYSIS.md`, `docs/ARCHITECTURE.md`, `docs/GAME-NETWORK-ARCHITECTURE.md`
- [x] Оценка рисков Windows/macOS, границы честности

## Этап 2 — Основа ✅
- [x] Монорепозиторий `EHOT-Launcher`, solution `EHOT.Launcher.slnx`
- [x] Central Package Management (`Directory.Packages.props`), `Directory.Build.props`, `global.json`
- [x] 11 проектов из ТЗ + `Platform.Linux` (для CI) + `UI.Tests`
- [x] DI-контейнер, MVVM-каркас, `ViewLocator`, `NavigationService`
- [x] Публикация под `win-x64`, `osx-arm64`, `osx-x64`
- [x] Сборка и тесты зелёные

## Этап 3 — Интерфейс ✅ (первый проход)
- [x] Дизайн-система: токены, кнопки, карточки, поля, бейджи, прогресс, тосты, диалоги, пустые состояния
- [x] Кастомный titlebar, glassmorphism-панели, адаптивная навигация
- [x] Главная страница (логотип, аккаунт, профиль, версия, ИГРАТЬ, статус, новости, тренды, недавнее, Game Network)
- [x] Экраны: Играть, Версии, Моды, Сборки, Текстуры, Шейдеры, Карты, Game Network, Аккаунты, Загрузки, Настройки, О программе
- [x] Глобальный поиск (пингвин-бар + Ctrl+K)

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
- [x] Unit-тесты: PathGuard, SafeZip, rules, аргументы, resolver, redaction, инвайты
- [x] Тесты API (WebApplicationFactory + SQLite-in-memory замена PG)
- [ ] UI-тесты (headless) — расширяются по мере готовности экранов
- [ ] Нагрузочные проверки Download Manager

## Этап 8 — Релиз ⬜
- [ ] CI: matrix windows/macos/ubuntu (build+test+publish)
- [ ] Inno Setup (Windows), `.dmg` + notarization (macOS) — конфигурация готова, артефакты требуют CI/secrets
- [ ] Автообновление (signed manifest + delta)
- [ ] Подпись кода (EV / Apple Developer ID)

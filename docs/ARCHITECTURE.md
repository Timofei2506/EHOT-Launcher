# EHOT Launcher — Архитектура

Состояние документа соответствует коду в репозитории. Дата: 2026-11-10.

## 1. Принципы

1. **Границы модулей важнее краткости.** Каждая подсистема — отдельная сборка с узким публичным контрактом.
2. **Платформенный код — только за интерфейсами.** В `EHOT.Launcher.Core` нет ни `DllImport`, ни путей вида `C:\` или `~/Library`.
3. **UI не содержит бизнес-логики.** ViewModel вызывает сервисы; View — только разметка/анимации.
4. **Асинхронность по умолчанию.** Любая операция > 50 мс — `async`, с `CancellationToken` и прогрессом.
5. **Честность статусов.** Каждая возможность имеет состояние: `Available` / `Degraded(reason)` / `Unavailable(reason)`. Никаких «зелёных» плашек без проверки.
6. **Никаких секретов в коде и логах.**

## 2. Слои и зависимости

```
                    ┌──────────────────────────┐
                    │      EHOT.Launcher.UI     │  Avalonia/MVVM
                    └────────────┬─────────────┘
        ┌───────────────┬─────────┴─────────┬──────────────┐
        ▼               ▼                   ▼              ▼
  EHOT.Launcher.Game  EHOT.Launcher.Content  EHOT.Launcher.Network  EHOT.Launcher.Auth
        └───────────────┴─────────┬─────────┴──────────────┘
                                  ▼
                        ┌──────────────────┐
                        │ EHOT.Launcher.Core│  контракты, модели, ошибки
                        └────────┬─────────┘
              ┌──────────────────┼──────────────────┐
              ▼                  ▼                  ▼
    EHOT.Launcher.Storage  EHOT.Launcher.Platform  (Platform.Windows / Platform.MacOS)
                                  ▲
                                  └── выбирается в composition root (UI/Program.cs)
```

- `EHOT.Launcher.API` (ASP.NET Core Control Plane) — отдельная сборка, **не** ссылается на клиент и не разделяет с ним внутренние модели
  (общие контракты продублированы осознанно — версионирование API важнее DRY).
- `EHOT.Launcher.Tests`, `EHOT.Launcher.UI.Tests` — тестовые проекты.

Направление ссылок проверяется `dotnet build` (циклы запрещены структурой).

## 3. Модули

### EHOT.Launcher.Core
Контракты и модели, не зависящие от UI и ОС:

| Область | Типы |
|---|---|
| Результаты | `OperationResult`, `OperationResult<T>`, `ErrorCode` |
| Прогресс | `DownloadProgress`, `TaskProgress` |
| Файлы | `IFileSystem` (atomic write, safe paths), `IArchiveExtractor`, `SafeZipExtractor`, `PathGuard` |
| Сеть | `IHttpService`, `HttpService` (retry, timeout, whitelist), `IRateLimiter` |
| Секреты | `ISecretStore`, `ITokenProtector` (реализации платформенные) |
| Загрузки | `DownloadManager` (очередь, пауза/резюм, хэши, история) — см. §7 |
| Логирование | `ILogService`, `RedactingLogSink`, `LogRotator` |
| Платформа | `IPlatformInfo`, `IAppPaths`, `IProcessRunner`, `INotificationService`, `IJavaLocator`, `INetworkPrivileges` |
| Возможности | `Capability`, `CapabilityRegistry` (Available/Degraded/Unavailable) |
| Каталог | `IContentProvider`, `ContentProject`, `ContentVersion`, `ContentFile`, `LoaderKind` |

### EHOT.Launcher.Auth
- `MicrosoftAuthService` — OAuth2 **device code** flow (MSA) → Xbox Live (`user.auth.xboxlive.com`) → XSTS → `api.minecraftservices.com/authentication/login_with_xbox` → профиль.
- `LocalProfileService` — оффлайн-профиль с честным статусом `AccountKind.Local` (не претендует на лицензию; UUID детерминированный v3-style от ника, как принято в оффлайн-режиме, но помечен отдельно от официальных UUID).
- `MojangProfileLookup` — `api.mojang.com/users/profiles/minecraft/{name}` для проверки существования ника и подтягивания настоящего UUID/скина.
- `SecretStore`-интеграция: refresh/access token только в защищённом хранилище.

### EHOT.Launcher.Game
- `MinecraftVersionRepository` — манифест версий (кэш 6 ч), правила (`rules`), библиотеки, нативы, asset index.
- `GameInstaller` — скачивание client/versions/libraries/assets/natives, распаковка, проверка SHA-1.
- `LoaderInstaller` — Fabric (`meta.fabricmc.net`), Quilt (`meta.quiltmc.org`), Forge/NeoForge (installer JSON через Maven).
- `JavaLocator` + `JavaRuntimeManager` — обнаружение, проверка версии, скачивание Adoptium.
- `GameLauncher` — сборка classpath, JVM/game аргументов (`arguments` с rules и placeholder-подстановкой), запуск процесса, стрим логов, exit-код → человекочитаемая ошибка.
- `ProfileService` — независимые профили: своя папка `instances/<id>`, версия, лоадер, RAM, JVM-аргументы, Java.
- `LaunchHistory` — история запусков и логи.

### EHOT.Launcher.Content
- `ModrinthProvider` — поиск (`/v2/search`), проект, версии, фильтры загрузчиков/версий.
- `CurseForgeProvider` — при наличии `Content:CurseForge:ApiKey`; иначе `Capability.Unavailable`.
- `DependencyResolver` — обход `dependencies` версии (required/optional/incompatible), проверка циклов, конфликт-детектор, топологическая сортировка.
- `ModpackService` — `.mrpack` (Modrinth format 1) импорт/экспорт, создание сборки из профиля.
- `ContentInstallService` — безопасная установка в `mods/`, `resourcepacks/`, `shaderpacks/`, `saves/`; контроль путей, хэшей, откат при ошибке.
- `ContentUpdateService` — проверка обновлений установленного.

### EHOT.Launcher.Network
- `ControlPlaneClient` — комнаты, инвайты, участники (REST + JWT).
- `RoomSession` — состояние комнаты, латентность, переподключение (exponential backoff + jitter).
- `INetworkAdapter` — платформенный туннель (`Start/Stop/Status`).
- `IceConnectionService` — STUN binding, кандидаты, приоритет Direct → NAT → Relay.
- Детали: `docs/GAME-NETWORK-ARCHITECTURE.md`.

### EHOT.Launcher.Storage
- `SqliteDatabase` — инициализация схемы (versioned, идемпотентно), WAL.
- Репозитории: `ProfilesRepository`, `AccountsRepository`, `LaunchHistoryRepository`, `DownloadHistoryRepository`, `SettingsRepository`, `InstalledContentRepository`.

### EHOT.Launcher.Platform(.Windows|.MacOS)
- Реализации `IAppPaths`, `ITokenProtector` (DPAPI / Keychain), `INotificationService`, `IJavaLocator`, `IProcessRunner`, `IPlatformInfo`, `INetworkPrivileges`.
- Linux-сборка использует `Platform.Linux` (внутренняя, для CI/тестов) — задел на будущее.

### EHOT.Launcher.API
ASP.NET Core minimal API + EF Core + PostgreSQL: пользователи (анонимные identity лаунчера), комнаты, инвайты, участники, relay-список, health/metrics, rate limiting, structured logs. См. `docs/API.md`.

## 4. MVVM и DI

- `ViewLocator` маппит `XxxViewModel` → `XxxView` (convention over configuration).
- Навигация: `NavigationService` + `INavigationAware` (`OnNavigatedToAsync/OnNavigatedFrom`).
- DI: `Microsoft.Extensions.DependencyInjection` в `Program.cs`; каждый модуль регистрирует себя через extension `AddEhotGame(this IServiceCollection)`, `AddEhotContent(...)`, etc.
- Жизненный цикл ViewModel: transient для страниц, singleton для оболочки.

## 5. Дизайн-система

`src/EHOT.Launcher.UI/DesignSystem/`:
- `Tokens.axaml` — цвета (перечисленные в ТЗ + производные), радиусы, отступы, тени, шрифты.
- `Controls.axaml` — Button (Primary/Secondary/Ghost/Danger), Card, Input, Toggle, Progress, Badge, Dialog, Toast, Skeleton, EmptyState, NavigationItem.
- Анимации: `Transitions` (opacity/transform 150–220 мс, cubic ease) — уважают системный `ReduceMotion` (упрощаются до fade).
- Все стили — на основе `ControlTheme` Avalonia 12, без переопределения глобальных шаблонов сторонних тем.

## 6. Отказоустойчивость

- Все сетевые вызовы → `OperationResult` с `ErrorCode` (`Network`, `NotFound`, `RateLimited`, `Unauthorized`, `Validation`, `Io`, `Cancelled`, `DependencyMissing`, `Incompatible`).
- В UI ошибки отображаются `Toast`/`InlineError`; ничего не глотается молча (`ILogService.Warn`).
- При недоступном backend лаунчер продолжает работать автономно (профили, запуск, каталог).

## 7. Download Manager

```
DownloadRequest(id, url, dest, sha1|sha512, size, priority, group)
     │
     ▼
DownloadManager ── очередь (Channel) ──► N воркеров (SemaphoreSlim, 1..8)
     │                                   ├─ HTTP Range resume (.part + .meta json)
     │                                   ├─ прогресс (байты/сек, ETA) через IProgress<>
     │                                   ├─ retry (3, backoff, Retry-After)
     │                                   ├─ hash verify → rename atomically
     │                                   └─ history в SQLite
     └─ CancellationTokenSource на задачу (pause = cancel+meta, resume, cancel = удалить)
```

## 8. Тестирование

- `EHOT.Launcher.Tests`: чистая логика (PathGuard, SafeZip, DependencyResolver, rules, аргументы запуска, download resume, redaction логов, JWT/инвайты на сервере).
- `EHOT.Launcher.UI.Tests`: Avalonia.Headless + xUnit — навигация, привязки, команды.
- CI: build + test на ubuntu/windows/macos; публикация артефактов.

## 9. Что дальше

См. `ROADMAP.md` — актуальный статус этапов.

# EHOT Launcher — технический анализ (Этап 1)

> Документ фиксирует состояние технологий на **10 ноября 2026 г.**, оценку реалистичности
> каждой подсистемы, риски и принятые архитектурные решения.
> Все заявления ниже проверены по первоисточникам либо локальной сборкой.

---

## 1. Проверка технологического стека

| Компонент | Проверенная версия | Статус | Комментарий |
|---|---|---|---|
| .NET SDK | **10.0.403** | ✅ Актуально | .NET 10 — LTS, релиз 11.11.2025, поддержка до **14.11.2028**. .NET 8/9 EOL 10.11.2026 — миграция обязательна. |
| .NET Runtime | 10.0.14 | ✅ | Runtime 10.0.14 = latest patch. |
| Avalonia UI | **12.1.6** | ✅ Стабильно | Ветка 12.x стабильна с апреля 2026; 11.3.x в поддержке (LTS-ветка) до конца цикла. Мы берём 12.1.6 — она собрана под .NET 10 и является текущим recommended. |
| CommunityToolkit.Mvvm | **8.4.2** | ✅ | Source generators `[ObservableProperty]`, `[RelayCommand]`, `ObservableValidator`. |
| Avalonia.Themes.Fluent | 12.1.6 | ✅ | Используется только как каркас контролов; стили полностью перекрыты собственной дизайн-системой. |
| Microsoft.Data.Sqlite | **10.0.14** | ✅ | Локальное хранилище (profiles, history, settings, downloads). |
| EF Core (Npgsql) | 10.0.x | ✅ | Только серверная часть. Клиент использует SQLite без EF Core (меньше вес, прямой контроль миграций). |
| ASP.NET Core | 10.0.x | ✅ | Backend Control Plane. |
| xUnit | **2.9.3** + runner 4.0.1 | ✅ | Тесты ядра, движка, безопасности. |
| Avalonia.Headless | 12.1.6 | ✅ | UI-тесты ViewModel↔View без окна. |

**Отклонено:** WPF (только Windows), Electron (вес/память), MAUI (слабая поддержка десктопа в нужном объёме).

---

## 2. Реальность внешних API (проверено запросами 10.11.2026)

| Источник | Endpoint | Состояние |
|---|---|---|
| Mojang version manifest | `https://piston-meta.mojang.com/mc/game/version_manifest_v2.json` | ✅ Работает. Актуальный release: **26.3**, snapshot: **26.4-snapshot-7**. |
| Mojang launcher news | `https://launchercontent.mojang.com/v2/javaPatchNotes.json` | ✅ Работает, отдаёт патчноуты и картинки. |
| Modrinth | `https://api.modrinth.com/v2/*` | ✅ Работает без ключа. Есть `action=install` для модпаков (mrpack). Требуется `User-Agent` с названием проекта. |
| Mojang аутентификация (MSA→Xbox→Minecraft) | `login.live.com`, `sisu.xboxlive.com`, `api.minecraftservices.com` | ✅ Легальный OAuth2 device-code flow. **Требуется собственный Azure-клиент + его явное разрешение Mojang** (для сторонних лаунчеров). |
| Modrinth API (SCIM не нужен) | — | ✅ |
| CurseForge | `api.curseforge.com/v1` | ⚠️ Требует API-ключ (Eternal API key). Без ключа интеграция отключается конфигом `Content:CurseForge:ApiKey`. Код готов, но в UI категория помечается «недоступно» до ключа. |
| Skin lookup | `api.mojang.com/users/profiles/minecraft/{name}` | ✅ |
| Mojang status API | `https://status.mojang.com/check` (legacy) / `launchercontent` | ⚠️ Legacy-эндпоинт периодически меняется; используем `api.mojang.com` + локальный кэш и корректный fallback. |
| Forge/NeoForge installer JSON | `maven.minecraftforge.net`, `maven.neoforged.net` | ✅ Публичные Maven-репозитории. |
| Fabric/Quilt | `meta.fabricmc.net`, `meta.quiltmc.org` | ✅ |

**Вывод:** базовый функционал (установка/запуск/каталог) реализуем без собственных ключей.
Microsoft Login требует Azure-приложения — это внешняя зависимость, которую мы готовим кодом,
но включение возможно только после регистрации приложения в Azure и получения одобрения Mojang.

---

## 3. Оценка реалистичности функций

| # | Подсистема | Реалистичность | Ключевые ограничения |
|---|---|---|---|
| 1 | Дизайн-система + навигация + 13 экранов | **Высокая** | Только трудозатраты. |
| 2 | Установка/запуск Minecraft (Vanilla/Fabric/Forge/NeoForge/Quilt) | **Высокая** | Требует точного соблюдения форматов `version.json`, `version_manifest`, arguments, natives classifier. |
| 3 | Java runtime autodetect + скачивание Temurin/Adoptium | **Высокая** | Adoptium API публичный. |
| 4 | MS-авторизация | **Средняя (внешняя зависимость)** | Нужен Azure client id + одобрение Mojang. Код — device code flow; без ключа кнопка честно объясняет причину. |
| 5 | Каталог Modrinth | **Высокая** | Нет официальных лимитов, но обязателен rate limit и кэш. |
| 6 | CurseForge | **Средняя (зависит от ключа)** | Нет ключа → раздел отключён, не имитируем. |
| 7 | Modpack manager | **Высокая** | Формат mrpack — открытый. |
| 8 | Download Manager | **Высокая** | HTTP Range, очередь, хэш-проверка. |
| 9 | EHOT Game Network | **Сложная, честно ограниченная** | См. §5 и `docs/GAME-NETWORK-ARCHITECTURE.md`. |
| 10 | Backend Control Plane | **Высокая** | ASP.NET Core + PostgreSQL. |
| 11 | macOS-подпись/нотаризация | **Средняя (внешняя)** | Нужны Apple Developer ID + CI на macOS (наши сборки в Linux-песочнице лишь *готовят* конфигурацию). |

---

## 4. Критические риски Windows / macOS

### Windows 10/11 x64
| Риск | Влияние | Митигация |
|---|---|---|
| TUN-адаптер требует прав администратора | нет прямого соединения без UAC | Отдельная elevated-служба `EHOT.Network.Service` (Windows Service) + именованный канал; elevation запрашивается осознанно при старте комнаты. |
| WireGuard подпись драйвера | Установка Wintun/WireGuard в нестандартном виде | Используем официальный `wintun.dll` (подписан) и wireguard-go? → окончательное решение в `GAME-NETWORK-ARCHITECTURE.md` §Платформы. |
| Long path (>260) | Распаковка модов падает | `\\?\`-префикс + `LongPathsEnabled` проверка. |
| Defender блокирует запись exe-файлов игрой | Ложное срабатывание | Ничего не пишем в ProgramData без нужды; JRE и библиотеки в `%APPDATA%\.ehot`. |
| SmartScreen на неподписанном установщике | Потеря доверия | EV-сертификат + подпись MSIX/Inno, CI-шаг. |

### macOS (ARM64 / Intel x64)
| Риск | Влияние | Митигация |
|---|---|---|
| **Network Extension** требует entitlement + права пользователя | Game Network на macOS невозможна без подписи профилем | Пакет `EHOT.Network.macOS` **resolves only with entitlement**; без него режим — Direct/STUN через пользовательское пространство, а TUN честно помечен как `Unavailable(reason)`. |
| Gatekeeper / нотаризация | Пользователь не запустит `.dmg` | hardened runtime, `com.apple.security.network.client`, notarization через `notarytool` в CI (macOS-runner). |
| Отсутствие Apple Developer ID в репозитории | Нельзя собрать релиз из Linux | CI-workflow с `MACOS_CERT`/`APPLE_ID` secrets; локально документально описано. |
| Rosetta и JRE | x64 лаунчер на ARM | Отдельные RID: `osx-arm64`, `osx-x64`; JRE скачивается под архитектуру ОС. |
| Файловая система: `~/Library/Application Support` | Пользовательские пути | `IAppPaths` платформенная реализация. |
| Java из Homebrew `/opt/homebrew` или `/usr/local` | не найдётся в PATH | Поиск по списку известных путей + `/usr/libexec/java_home`. |

### Общее
| Риск | Митигация |
|---|---|
| Avalonia 12 — молодая major-ветка | Обновили все пакеты на 12.1.6 единовременно; API отличия от 11.x изолированы в `XAML`/`Styles`. |
| Память на слабых ПК | Виртуализация списков, кэш обложек с лимитом, `WeakReference` на загруженных bitmap. |
| Ошибки сторонних модов (java version) | Java Runtime валидация: версия по `version.json` → `javaVersion.majorVersion`. |

---

## 5. Архитектура EHOT Game Network (кратко; детали в отдельном документе)

Три уровня:

1. **Control Plane** (ASP.NET Core + PostgreSQL)
   комнаты, инвайты (HMAC-подпись + TTL), членство, публичные ключи участников, координация STUN/relay.
   Не видит игровой трафик в прямом соединении.
2. **Data Plane**
   - Прямой канал: WireGuard-туннель между узлами (userspace `wireguard-go`/`boringtun` + системный TUN или kernel WG),
     обмен ключами через контрольный сервер (публичный ключ каждого участника рассылается подписанным списком).
   - NAT traversal: STUN (RFC 5389) → sym./port-restricted detection → hole punching.
   - Relay: запасной вариант, трафик релеится по DTLS/UDP через relay-узел, с лимитом полосы/квоты.
3. **Platform Adapters**
   - `WindowsNetworkAdapter` — Wintun + WireGuard, elevated service, раздельные маршруты только на подсеть комнаты `10.x.y.0/24` (никогда не `0.0.0.0/0`).
   - `MacNetworkAdapter` — `NEPacketTunnelProvider` (требует entitlement) с userspace fallback → Direct/relay.
   - `NullNetworkAdapter` — только релей + прямое соединение через SOCKS-прокси для Java (без привилегий).

Приоритет: **Direct → NAT-traversal → Relay**. Всё, что не реализовано, честно возвращает `NetworkCapability.Unavailable`.

---

## 6. Производительность

- Запуск: ленивая инициализация тяжёлых сервисов (Content, Game) через `Lazy<T>`/async инициализацию.
- Списки: `ItemsRepeater`/`VirtualizingStackPanel`, постраничная подгрузка Modrinth (`offset/limit`).
- Изображения: собственный `ImageCache` с ограничением 64 МБ и LRU-вытеснением.
- HTTP: `HttpClient` единственный на домен, `SocketsHttpHandler` с `PooledConnectionLifetime`, отмена запросов при уходе со страницы.
- SQLite: WAL, индексы, батчевые транзакции.
- Бюджет: idle RAM ≤ 180 МБ, cold start ≤ 1.5 с на среднем ноутбуке, UI никогда не блокируется (>0.05 c → async).

---

## 7. Безопасность

| Требование | Решение |
|---|---|
| Токены | DPAPI на Windows, Keychain на macOS, AES-GCM+keyfile(0600) fallback на Linux; `ISecretStore`. В БД — только ссылки. |
| Секреты в коде | Нет. Только `appsettings.Development.json` (в .gitignore) / env / user-secrets. |
| Распаковка архивов | `SafeZip` с проверкой canonical path префикса, запрет `..`, абсолютных путей, симлинков, проверка числа записей и распакованного объёма (zip-bomb), лимит глубины. |
| Скачивание | HTTPS-only, whitelist доменов контента, SHA-1/SHA-512 из метаданных Mojang, SHA-512 Modrinth `hashes`, размер-лимит. |
| Логи | Redaction-фильтр: токены, cookies, `Authorization`, `accessToken`, JWT-паттерны. |
| Backend | rate limiting, JWT, проверка членства, отсутствие IDOR, валидация имён комнат, HMAC инвайтов. |

---

## 8. Структура solution (принята)

```
EHOT-Launcher/
├─ EHOT.Launcher.slnx
├─ Directory.Build.props          общие версии, nullable, warnings-as-errors (для CI)
├─ Directory.Packages.props       central package management
├─ global.json                    SDK pin 10.0.403
├─ src/
│  ├─ EHOT.Launcher.UI/           Avalonia, Views, ViewModels, Styles, DesignSystem
│  ├─ EHOT.Launcher.Core/         контракты, модели, сервисы, DI, download manager
│  ├─ EHOT.Launcher.Auth/         Microsoft device-code + локальные профили
│  ├─ EHOT.Launcher.Game/         версии, java, установка, запуск
│  ├─ EHOT.Launcher.Content/      Modrinth, CurseForge, dependency resolver, mrpack
│  ├─ EHOT.Launcher.Network/      control-plane client, wireguard/STUN/relay contracts
│  ├─ EHOT.Launcher.Storage/      SQLite репозитории, миграции
│  ├─ EHOT.Launcher.Platform/     IPlatform абстракции + общая логика путей
│  ├─ EHOT.Launcher.Platform.Windows/
│  ├─ EHOT.Launcher.Platform.MacOS/
│  ├─ EHOT.Launcher.API/          ASP.NET Core Control Plane
│  └─ EHOT.Network.Service.Windows/ elevated TUN-служба (Этап 6)
├─ tests/
│  ├─ EHOT.Launcher.Tests/        xUnit
│  └─ EHOT.Launcher.UI.Tests/     Avalonia.Headless
└─ docs/                          README, ARCHITECTURE, ROADMAP, ...
```

Правило зависимостей: `UI → Game/Content/Network/Auth → Core → Platform` (направление внутрь), `API` независим, `Platform.*` реализуют контракты `Core`.

---

## 9. Границы честности (что НЕ будет выдаваться за готовое)

- macOS Network Extension: код-адаптер + конфиг есть; **помечается `Experimental`**, пока нет подписи entitlement и прогонов на реальных машинах.
- CurseForge: раздел отключён без API-ключа.
- Microsoft Login: код device-code flow есть; без `AzureClientId` кнопка выдаёт инструкцию, не фейковый логин.
- Relay-инфраструктура: в репозитории — сервер и клиент; публичные бесплатные relay — не обещаем.
- Установщики Windows/macOS: конфигурация публикации готова, артефакты собираются в CI на соответствующих runners.

---

## 10. Решения по версиям

- `LangVersion` = 14 (по умолчанию в .NET 10), `Nullable=enable`, `TreatWarningsAsErrors` в CI (локально — warnings видны).
- EF Core — только в `EHOT.Launcher.API`; клиент — `Microsoft.Data.Sqlite` напрямую.
- Никакого `Avalonia.ReactiveUI` — только CommunityToolkit.Mvvm.

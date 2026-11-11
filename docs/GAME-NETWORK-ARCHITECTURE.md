# EHOT Game Network — архитектура

> Статус: **проектирование + реализация Control Plane и Data Plane клиента**.
> Платформенный туннель (TUN) помечен честными статусами. См. §7.

## 1. Цель

Дать игрокам возможность играть в Minecraft по локальной сети так, будто они в одном LAN:

- создать комнату → получить код-приглашение → друзья подключаются;
- в лаунчере появляется адрес вида `10.<x>.<y>.<n>:25565`, который можно скопировать и вставить в Minecraft;
- трафик шифруется end-to-end между участниками;
- трафик идёт **только** в подсеть комнаты, никогда во всю домашнюю сеть или интернет.

Не цель: замена корпоративному VPN, доступ к чужим устройствам, обход блокировок.

## 2. Уровни

```
                        ┌─────────────────────────────────────┐
                        │  Control Plane (ASP.NET Core + PG)   │
                        │  users, rooms, invites, membership,   │
                        │  pubkeys, STUN/relay discovery,       │
                        │  presence, metrics                    │
                        └───────┬─────────────┬────────────────┘
                                │ HTTPS/WSS   │
              ┌─────────────────▼──┐       ┌──▼─────────────────┐
              │ EHOT Launcher (A)  │       │ EHOT Launcher (B)  │
              │ RoomSession        │       │ RoomSession        │
              │  ├ IceConnection   │◄─────►│  ├ IceConnection   │
              │  └ INetworkAdapter │  UDP  │  └ INetworkAdapter │
              └─────────┬──────────┘       └─────────┬──────────┘
                        │ WireGuard (userspace)      │
                        └──────────┬─────────────────┘
                                   ▼
                          [ Relay node, if needed ]
```

### Control Plane
- **Не видит** игровой трафик при прямом соединении.
- Отдаёт: список участников с публичными ключами WireGuard (Curve25519), эндпоинтами, кандидатами STUN.
- Инвайт: `roomId` + `token` = HMAC-SHA256(secret, roomId|expiry|nonce) — сервер проверяет при join, TTL 24 ч, отзывается.
- Аутентификация: анонимная identity (Ed25519 keypair, генерируемый лаунчером) → JWT со сроком 30 дней; без e-mail.
- Авторизация: любой участник может создавать комнаты; владелец/админ — кикать, отзывать инвайты, закрывать комнату.

### Data Plane
- Транспорт — **WireGuard** (Noise IK, Curve25519, ChaCha20-Poly1305): проверенная криптография, не изобретаем свою.
- Каждый участник получает внутренний IP `10.<roomHash_octet>.0.<n>` и подсеть `/24`; маршрут объявляется только для этой подсети.
- Ключи WireGuard генерируются локально, приватный ключ никогда не покидает устройство; публичный — публикуется в комнате.
- Обмен ключами: сервер отдаёт подписанный (Ed25519) снапшот `RoomPeers` с `revision`; при изменении состава — повторная загрузка, автоматическое перестроение туннеля.

## 3. Порядок соединения

1. **Direct** — если у обоих публичный/переадресованный UDP-порт и STUN показывает consistent mapping.
2. **NAT traversal** — hole punching через STUN/MAPPED-ADDRESS, port prediction не используем (риск).
3. **Relay** — если 1–2 недостижимы: трафик идёт через relay-узел (DTLS 1.2 over UDP), который не может расшифровать WG-пейлоад (он релеит WG пакеты как opaque datagrams).

Оценка качества (latency, jitter, loss) отображается в UI; переключение Direct↔Relay — автоматическое с гистерезисом.

## 4. Интеграция с Minecraft

- Определение LAN-игр: лаунчер слушает UDP 4445 (broadcast `[MOTD]...[AD]...[/AD]`) в сетевом namespace комнаты (если адаптер разрешает) либо просит пользователя указать порт.
- В UI комната показывает: `Minecraft LAN: 10.x.0.3:25565` + кнопка «Копировать адрес».
- `Direct Connect`: автоматическая подстановка адреса комнаты в поле «Прямое подключение» — через буфер обмена и подсказку (не автоматизируем ввод в чужое окно).

## 5. Безопасность

| Угроза | Мера |
|---|---|
| Несанкционированный вход в комнату | инвайт-HMAC + TTL + отзыв + whitelist участников |
| Подмена участника | Ed25519-подпись снапшота состава, пиннинг ключей |
| Прослушивание | WireGuard E2E; relay видит только шифротекст |
| Доступ к домашней сети | маршрутизируется только подсеть комнаты; `AllowedIPs` строго `/24`; на клиенте — sysctl/route-таблица без default route |
| DoS на control plane | rate limiting по IP/identity, размер запроса, очередь на создание комнат |
| Абуз relay | квоты на комнату, счётчики байт, автоматическая блокировка аномалий |
| Утечка ключей | приватные ключи в ОС-хранилище (DPAPI/Keychain), права 0600 |

## 6. Протокол Control Plane (REST, подробнее — `docs/API.md`)

```
POST /v1/identity/register        → {identityId, token, publicKeyEd25519}
POST /v1/rooms                    → {roomId, joinCode, iceServers, relayPolicy}
POST /v1/rooms/{id}/join          {inviteToken, wgPublicKey} → {peers[], myIp, revision, signature}
GET  /v1/rooms/{id}               → {state, peers, revision, signature}
POST /v1/rooms/{id}/heartbeat     → {expiresAt}
DELETE /v1/rooms/{id}/members/{m} → 204 (owner/admin)
DELETE /v1/rooms/{id}             → 204
GET  /v1/rooms/{id}/events        (WS) presence, peer-updated, kicked
GET  /v1/relays                   → [{host, port, publicKey, region, load}]
GET  /healthz, /metrics
```

## 7. Платформенные адаптеры — честный статус

| Платформа | Механизм | Статус в коде | Что требуется |
|---|---|---|---|
| Windows 10/11 | Wintun + WireGuard (userspace, elevated helper) | `Experimental` | Подписанный `wintun.dll`, служба с правами админа, тест на реальной машине |
| macOS ARM64 | `NEPacketTunnelProvider` | `Experimental`, требуeт entitlement | Apple Developer ID, entitlement `packet-tunnel-provider`, notarization |
| macOS (без entitlement) | SOCKS5-прокси + relay | `Available` (ограниченно) | Работает только для игр, поддерживающих прокси, либо через relay |
| Linux | `wireguard-go`/kernel WG | `Unavailable` (задел) | Вне текущего объёма |

**Правило:** если адаптер не может заявить `Available`, UI показывает причину и предлагает fallback (relay / прокси), а не имитацию подключения.

## 8. Инфраструктура

- `EHOT.Launcher.API` — control plane, PostgreSQL.
- Relay — отдельный stateless-процесс (UDP), масштабируется горизонтально; список отдаётся через API.
- STUN — встроенный лёгкий responder на relay-узлах (RFC 5389 binding только).
- Метрики: Prometheus endpoint, логи структурные, алерты на рост отказов.

## 9. Что НЕ обещаем

- Бесплатный неограниченный relay-трафик.
- Проброс портов «одной кнопкой» без согласия пользователя.
- Обход NAT типа symmetric для UDP при отсутствии relay.

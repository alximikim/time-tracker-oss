# Деплой сервера (`server/TimeTracker.Server`)

ASP.NET Core + SQLite. Полная замена Google Sheets/Apps Script — вся база
хранится в одном файле SQLite рядом с сервером.

## 1. Сборка

Self-contained публикация — не требует установки .NET на целевой машине.

```powershell
# Windows
dotnet publish server/TimeTracker.Server -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/win-x64

# Linux
dotnet publish server/TimeTracker.Server -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux-x64
```

Результат — один файл `TimeTracker.Server.exe` (или `TimeTracker.Server` на
Linux) в папке `publish/...`.

## 2. Конфигурация

Секреты (`SharedToken`, `AdminPassword`) **не** лежат в репозитории — задаются
одним из двух способов.

| Ключ | Переменная окружения | Описание | Пример |
|---|---|---|---|
| `SharedToken` | `TimeTracker__SharedToken` | Секрет, который передаёт клиент в каждом запросе — **обязателен** | длинная случайная строка |
| `AdminUsername` | `TimeTracker__AdminUsername` | Логин для `/admin` (Basic Auth) | `admin` (по умолчанию) |
| `AdminPassword` | `TimeTracker__AdminPassword` | Пароль для `/admin` — **обязателен** | сильный пароль |
| `DbPath` | `TimeTracker__DbPath` | Путь к файлу SQLite | `data/timetracker.db` (по умолчанию) |
| `ListenUrl` | `TimeTracker__ListenUrl` | Адрес и порт, на котором слушает сервер | `http://0.0.0.0:5140` (по умолчанию) |

**Способ А — переменные окружения** (годится для systemd/Docker/Task
Scheduler):

```powershell
$env:TimeTracker__SharedToken = "..."
$env:TimeTracker__AdminPassword = "..."
.\TimeTracker.Server.exe
```

**Способ Б — файл рядом с exe.** Скопируйте `appsettings.Example.json` →
`appsettings.Local.json` (он в `.gitignore`, случайно не закоммитится),
впишите реальные значения. Сервер подхватывает его автоматически при старте.

Без хотя бы одного из способов сервер откажется стартовать вне
Development-режима — это специально: не даём случайно поднять сервер с
пустым токеном.

## 3. Запуск

**Windows** — либо просто запустить exe (останется в консоли), либо обернуть
[NSSM](https://nssm.cc/) в службу Windows, чтобы поднимался при старте
системы:

```powershell
nssm install TimeTrackerServer "C:\путь\TimeTracker.Server.exe"
nssm set TimeTrackerServer AppEnvironmentExtra TimeTracker__SharedToken=... TimeTracker__AdminPassword=...
nssm start TimeTrackerServer
```

**Linux** — пример unit-файла `systemd` (`/etc/systemd/system/timetracker.service`):

```ini
[Unit]
Description=TimeTracker Server
After=network.target

[Service]
ExecStart=/opt/timetracker/TimeTracker.Server
Environment=TimeTracker__SharedToken=...
Environment=TimeTracker__AdminPassword=...
Restart=always
User=timetracker

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now timetracker
```

**Docker** — см. `server/TimeTracker.Server/Dockerfile` и
`server/docker-compose.example.yml`:

```bash
cp server/docker-compose.example.yml server/docker-compose.yml
# отредактировать TimeTracker__SharedToken / TimeTracker__AdminPassword
cd server && docker compose up -d
```

## 4. Проверка вручную

```powershell
$url = "http://localhost:5140"

# health-check
Invoke-RestMethod -Uri "$url/api/health"

# тестовый старт сессии
$body = @{
  token = "<SharedToken>"
  employeeId = "test"
  employeeName = "Тестовый Сотрудник"
  eventType = "start"
  clientTimestamp = (Get-Date).ToString("o")
  sessionId = [guid]::NewGuid().ToString()
  machineId = "TEST-PC"
} | ConvertTo-Json

Invoke-RestMethod -Uri "$url/api/events" -Method Post -Body $body -ContentType "application/json"
```

Повторная отправка того же `sessionId` с `eventType: start` не должна
создавать вторую строку (идемпотентность).

## 5. Веб-панель администратора

Откройте `http://<адрес-сервера>:<порт>/admin` в браузере — попросит логин/
пароль (`AdminUsername`/`AdminPassword`). Фильтры по сотруднику и датам,
итоги по часам, кнопка "Экспорт CSV" (открывается в Excel).

Если сервер доступен из интернета (не только из локальной сети) —
обязательно поставьте перед ним reverse-proxy с HTTPS (Caddy/nginx/Traefik),
Basic Auth сам по себе передаёт пароль в открытом виде без TLS.

## 6. Резервное копирование

```bash
sqlite3 data/timetracker.db ".backup backup-$(date +%F).db"
```

Не копируйте файл `.db` "вживую" (`cp`) во время работы сервера — из-за
WAL-режима это может дать неконсистентный снимок; `.backup` — безопасный
способ.

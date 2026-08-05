# StudyLife

[![CI/CD](https://github.com/lukislp/studylife/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/lukislp/studylife/actions/workflows/ci-cd.yml)
[![Release](https://img.shields.io/github/v/release/lukislp/studylife)](https://github.com/lukislp/studylife/releases)
[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

> Personal study organization - calendar, focus timer, and learning goals in one modern Blazor WebAssembly app. Multi-user capable with passkey login, designed to run on your own home network.

---

## Features

### Accounts & Login
- Passwordless login via passkey (WebAuthn) - no password to remember or leak
- Multiple users possible (e.g. family members): each has their own courses, sessions, notes, and settings, completely separated
- Add another device of your own either directly on the device itself or via a linking code from an already logged-in device (no cross-device Bluetooth fumbling needed)
- New devices/additional passkeys must first be approved from an already logged-in device before they can be used
- Device management: rename passkeys, view the list, remove individually
- Emergency access via recovery codes: generate 8 one-time codes in Setup (shown only once) to sign in if the only registered device/passkey is ever lost

### Language
- Fully usable in 26 languages: all 24 official EU languages (English, German, French, Spanish, Italian, Portuguese, Dutch, Danish, Swedish, Finnish, Greek, Polish, Czech, Slovak, Hungarian, Romanian, Bulgarian, Croatian, Slovenian, Estonian, Latvian, Lithuanian, Maltese, Irish) plus Ukrainian and Russian
- Switcher in the top right (positioned the same way on desktop and mobile): click the current flag, choose a language from the popup; the selection persists across restarts/browser sessions

### Calendar
- Week view with an hourly timeline (07:00 - 22:00), with an optional day view on mobile devices
- Create, edit, and delete study sessions
- Course, topic, start and end time per session
- Create recurring appointments (e.g. lectures) up to an end date in a single step - selectable interval (weekly/every 2/3/4 weeks) and multiple weekdays at once (e.g. Mon+Wed+Fri)
- Session templates (quick-add): save frequently recurring appointments (e.g. "Calculus lecture, 90 min, Mondays 10:00") as a template and apply it to the calendar with a click
- Current-time indicator
- Horizontally scrollable on mobile devices - times stay fixed
- Subscribable iCalendar feed for external calendar apps (Google/Apple Calendar, see Setup)
- Search (course/topic) and course filter (show/hide by clicking course pills)
- Warning for time-overlapping sessions (can still be saved)
- Delete confirmation for sessions (two clicks instead of immediate deletion)
- For recurring appointments: selectively delete "just this session", "this and all following", or "the entire series"
- Print the weekly schedule/export as PDF
- Automatic topic suggestion when creating a new session (from the course's still-open topics)
- Swipe gesture on mobile devices (swipe the header or calendar grid left/right) for quick week/day switching

### Study Planner
- Exam planner: choose a course and exam date - automatically distributes the still-open topics as study sessions across free time slots in the calendar up to the date (session length and total hours configurable, suggestion editable/removable before acceptance)
- Weekly plan assistant: suggests study sessions for the current week to fill the weekly quota (25-30 h) - weighted toward courses that haven't been studied for the longest or whose exam is approaching soonest
- Both suggestions respect existing calendar appointments and are only created as actual sessions after confirmation

### Focus Timer
- Predefined modes: Pomodoro, Flow, Ultradian, Claude, Sprint
- Animated timer with progress ring
- Keeps running in the background when switching pages (singleton service)
- Automatic switching between focus and break phases
- Browser tab title shows the remaining time while the timer is running
- Sound and vibration feedback when a session completes
- Reflection prompt after a session ends ("What did you learn?"), saved directly as a linked note

### Dashboard
- Daily overview with active/next session
- Weekly statistics (sessions, hours, streak)
- Weekly quota (goal: 25-30 h) with progress bar and warning if falling short
- Monthly quota - grows dynamically with the weeks of the month
- Course overview as pills
- Weekly trend of the last 8 weeks as a bar chart
- Upcoming course goals: the next 5 open goals with countdown or overdue notice
- Study progress tile: ECTS progress and weighted grade average at a glance
- Mini donut chart: course time distribution over the last 30 days
- Today tile: progress ring (hours today vs. daily goal) and 7-day streak bar
- Weekly comparison: delta in study hours versus the previous week
- Most recently completed sessions as a mini list
- Preview of the most recently edited note
- Balance check: which active course hasn't been studied for the longest
- Achievements: permanent milestone badges (total hours, longest streak, sessions, completed courses, all courses completed)
- Topic progress: checked-off course topics across all courses
- Inactivity notice directly in the dashboard (visible even without push notifications enabled)
- Series icon on today's sessions that are part of a recurring series
- ECTS forecast: expected completion date at the current study pace
- Target-completion tile: given a self-set target date, how many hours/week are needed for it
- Productivity hint: suggests the best time of day for the next session based on your study rhythm so far
- Month/year comparison: study hours versus the previous month and (if enough data is available) the same month in the previous year
- Course tags as small badges on the course pills (e.g. "exam soon")

### Notes
- Free-text notes, optionally assigned to a course
- Automatic saving while typing
- Search (title/content) and course filter
- Delete confirmation (two clicks instead of immediate deletion)
- Link to the triggering focus session visible (🔗), if created from the reflection prompt

### Evaluation
- Hours studied and sessions per course
- ECTS-weighted grade average
- Study progress (achieved / total ECTS)
- Study heatmap: year view of daily study intensity (GitHub-style)
- Course time distribution as a donut chart
- Study rhythm: distribution by weekday and time of day
- Monthly course history (last 6 months) as a stacked bar chart
- Year in review: "Wrapped"-style summary (total hours, strongest course, most productive day/time, longest streak, total sessions)
- ECTS forecast and month comparison (previous month) in the study progress tile
- Study report as a printable PDF: total hours, hours per course, ECTS progress including grade average, course goal status - for scholarship applications or academic advising

### Share Progress
- Optional, public read-only link (no login) with a compact progress snapshot (ECTS, grade average, topic progress of active courses) - for sharing with parents or a mentor
- Deliberately shows no notes, calendar details, or settings
- Can be disabled at any time or reissued with a new link

### Backup & Export
- JSON export of your own data (sessions, notes, course goals, settings)
- Full database download, optionally encrypted with a self-chosen password (AES-256)
- Restore from a previously downloaded backup, including the encrypted variant
- Weekly automatic background backup (the last 4 weeks are retained)
- Database download/restore is reserved for the account's original setup user; the JSON export is available to every account

### Browser Notifications
- Automatic reminder 10 minutes before a planned study phase
- Permission is requested on the first app start
- Each session is notified only once (duplicate protection)
- Reminders before a course's target date (default 14/7/3/1/0 days ahead)
- Motivating reminder if no study session has taken place for several days (default 5 days)
- Weekly review via push (Sunday evening)
- All reminder thresholds individually configurable in Setup

### Setup
- Manually switch theme: System / Light / Dark
- Activate/complete courses, set a target date per course
- Optionally record a grade (German grading system) and completion note when completing
- Topic checklist per course: check off individual topics, progress display (N/M)
- Set a course tag (free text, e.g. "exam soon") per course
- Choose a preferred motivation profile
- Calendar subscription URL for copying
- Reminder thresholds (session lead time, course goal lead time, inactivity threshold) individually adjustable
- Study windows: hours (from/to) and weekdays adjustable, in which the exam planner and weekly plan assistant are allowed to suggest sessions
- Manage passkeys (rename, add more, remove)
- Generate recovery codes for emergency access (shown once, invalidates previous codes)
- Generate/revoke API key for the Home Assistant integration
- Manage backup/export and the share-progress link

### PWA
- App icon shortcuts (long-press on the home screen icon): start focus, new note, calendar

### Native Apps
- [StudyLife App](https://github.com/lukislp/studylife-app) is a separate .NET MAUI Blazor Hybrid shell (iOS/Android/Mac/Windows) built on top of this repo's Blazor Client, adding native notifications, home screen widgets, Live Activities, Siri Shortcuts, and an Apple Watch companion app.

---

## Technology

| Layer | Technology |
|---|---|
| Frontend | Blazor WebAssembly (.NET 10) |
| Backend | ASP.NET Core (.NET 10) |
| Login | Passkey/WebAuthn (Fido2NetLib) |
| Database | SQLite via Entity Framework Core (default) - optionally PostgreSQL for horizontally scalable operation, see below |
| Deployment | Docker + Watchtower (default) - optionally Kubernetes/K3s for horizontal scaling, see below |
| CI/CD | GitLab CI with Semantic Release |

---

## Deployment

### Prerequisites
- Docker and Docker Compose
- Access to the private registry `registry.example.com`

### Quick Start

```bash
git clone https://github.com/lukislp/studylife.git
cd studylife
chmod +x setup.sh
./setup.sh
```

The setup script:
1. Creates `.env` from `.env.example`
2. Interactively asks for registry credentials
3. Logs Docker into the registry
4. Pulls the current image
5. Starts all services via `docker compose up -d`

On the very first start (no user registered yet), the server outputs a one-time setup code to the logs (`docker compose logs`) - this code is requested during the first passkey registration and protects against someone else on the same network claiming the initial registration before the actual operator. Every subsequent registration (e.g. family members) does not need this code.

### Environment Variables

| Variable | Default | Description |
|---|---|---|
| `REGISTRY_URL` | `registry.example.com` | Docker registry |
| `REGISTRY_USER` | - | Registry username |
| `REGISTRY_PASSWORD` | - | Registry password |
| `PORT` | `8080` | Public port |

### Horizontally Scalable Operation (optional)

For more than a handful of users/higher load, the same server can also be run against PostgreSQL (instead of SQLite) and Redis (instead of the in-memory cache) and scaled horizontally across multiple pods/containers (`Database:Provider=Postgres`, `Cache:Provider=Redis`, see `docker-compose.scale.yml`). A complete, production-operated reference architecture for this (2-node K3s cluster on Raspberry Pi, Postgres HA via CloudNativePG, Redis Cluster, NGINX Gateway Fabric, HorizontalPodAutoscaler, monitoring stack), including all lessons learned, is documented in [docs/SCALING.md](docs/SCALING.md).

---

## Automatic Updates

Watchtower is integrated in `docker-compose.yml` and checks every 5 minutes whether a new image is available. As soon as the GitLab pipeline publishes a new image, the container is restarted automatically.

Only containers with the label `com.centurylinklabs.watchtower.enable=true` are updated.

---

## Development

### Prerequisites
- .NET 10 SDK
- Visual Studio 2022+ or Rider

### Starting

```bash
cd src/StudyLife.Server
dotnet run
```

The app is then reachable at `https://localhost:5001`. The SQLite database is automatically created under `app_data/studylife.db`.

### Project Structure

```
src/
|-- StudyLife.Client/       # Blazor WebAssembly frontend
|   |-- Pages/              # Razor pages (Dashboard, Calendar, Focus, Setup, Login/Register)
|   |-- Services/           # AppStateService, TimerService, NotificationService
|   |-- Models/             # Client-side models
|   `-- wwwroot/            # Static assets, CSS, index.html
|-- StudyLife.Server/       # ASP.NET Core backend
|   |-- Controllers/        # API endpoints
|   `-- Data/               # EF Core DbContext, SQLite/Postgres
`-- StudyLife.Shared/       # DTOs, course catalog, shared metrics logic
```

Architecture, API reference, and notes for changes: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

---

## Home Assistant Integration

[StudyLife for Home Assistant](https://github.com/lukislp/studylife-hacs) is a separate HACS custom integration that maps dashboard and evaluation data (active/next session, weekly/monthly statistics, streak including the longest ever achieved series, quotas, grade average, ECTS progress, ECTS forecast, month comparison, achievements, topic progress, course tags, course catalog, live timer phase, weekly review as an event) as sensors, binary sensors (including inactivity warning), and calendars (sessions plus course goals) in Home Assistant, plus a dropdown of active courses (`select.studylife_active_course`) and services for creating/editing/deleting sessions and course goals. The pairing runs via a per-user API key generated once on the Setup page (see [Security](#security)). Installation and details are in that repo's README.

## Security

Login runs exclusively via passkey (WebAuthn) - there is no password and no unauthenticated API access anymore. The very first registration on a fresh installation additionally requires the setup code output once to the server logs (see Deployment above); every subsequent registration (e.g. for family members) is then open and creates its own account, completely separate from other users. A session token extends on a sliding basis with active use (90 days), but forces a fresh login after 180 days at the latest. An additional device can either be registered directly or paired via a time-limited linking code from an already logged-in device - in both cases an already logged-in device must first approve the new device via device management before it can be used.

For non-interactive integrations like Home Assistant, which cannot maintain a passkey session, a long-lived, **per-user** API key can be generated on the Setup page (`X-Api-Key` header) - it does not rotate automatically, but can be revoked immediately at any time; a leaked key therefore only ever compromises exactly one account. The subscribable iCalendar feed and an optional public share-progress link each use their own separate tokens instead of the API key. Details on the complete security model (including the results of a targeted security review): [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#security).

---

## CI/CD Pipeline

| Stage | Job | Description |
|---|---|---|
| test | secret_detection | GitLab's built-in secret-detection template |
| test | test:unit | Full `dotnet test` run (Shared + Server) |
| test | test:i18n | `check-i18n.py` - all 26 languages, every table |
| test | test:lint | `dotnet format --verify-no-changes` |
| test | test:security | NuGet vulnerability scan (non-blocking, fails visibly on High/Critical) |
| test | test:k8s-manifests | `kubeconform` schema validation of `k8s/` |
| test | test:compose-scale | Syntax/interpolation check of `docker-compose.scale.yml` |
| build | build | Restore and build all projects |
| version | get-version | Semantic Release dry run |
| publish | publish:server | dotnet publish + ZIP artifact |
| docker | docker:server | Multi-arch Docker image in registry |
| docker | trivy:server | Container vulnerability scan of the published image |
| release | semantic-release | GitLab release + changelog |

Versioning via Conventional Commits:
- feat: minor version
- fix: patch version
- BREAKING CHANGE: major version

---

## License

Copyright (C) 2026 Lukas Koerber

[AGPL-3.0](LICENSE) - if you run a modified version of this app as a network service, you
must make your modified source available to its users. The Home Assistant integration is
maintained as a separate, MIT-licensed repository: see [Home Assistant Integration](#home-assistant-integration).

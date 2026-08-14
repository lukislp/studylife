## [1.14.4](https://github.com/lukislp/studylife/compare/v1.14.3...v1.14.4) (2026-08-14)


### Bug Fixes

* **auth:** stop redirecting Kubernetes' own health probes to HTTPS ([5b774b6](https://github.com/lukislp/studylife/commit/5b774b62c9f887d2c373f578a57e294c713318da))

## [1.14.3](https://github.com/lukislp/studylife/compare/v1.14.2...v1.14.3) (2026-08-14)


### Bug Fixes

* **auth:** eliminate three startup warnings found via aggregated logs ([93147b7](https://github.com/lukislp/studylife/commit/93147b7d09b2fe8bc72b938aaccf82cf923e574b))
* **monitoring:** exclude NGF gRPC-reconnect noise from the warnings panel too ([1bcb9ae](https://github.com/lukislp/studylife/commit/1bcb9aeb6177e8abd6a26a660c49a7e8faf1a612))
* **monitoring:** upgrade kube-state-metrics to stop watching deprecated Endpoints ([d248bde](https://github.com/lukislp/studylife/commit/d248bde3464ad53b429a0f9d4097e3e781711623))

## [1.14.2](https://github.com/lukislp/studylife/compare/v1.14.1...v1.14.2) (2026-08-14)


### Bug Fixes

* **monitoring:** fix Loki OOMKilling itself, pin to the node with more RAM ([c2359b5](https://github.com/lukislp/studylife/commit/c2359b5e1fc3848c6e50ce7568fc012d0bdb764c))

## [1.14.1](https://github.com/lukislp/studylife/compare/v1.14.0...v1.14.1) (2026-08-14)


### Bug Fixes

* **ci:** make the Docker version-tag guard self-healing instead of a hard stop ([4540f9b](https://github.com/lukislp/studylife/commit/4540f9ba0dbb2c9d4f7cc7624be23ea8a3da20bd))

# [1.14.0](https://github.com/lukislp/studylife/compare/v1.13.0...v1.14.0) (2026-08-14)


### Bug Fixes

* **auth:** persist DataProtection key ring to Redis across replicas ([43d9ed7](https://github.com/lukislp/studylife/commit/43d9ed7ba3c1dce0f303e36c2ad7e9df7e029013)), closes [hi#severity](https://github.com/hi/issues/severity)
* **monitoring:** allow Prometheus to scrape Loki/Promtail, add 7 alert rules ([6ef2e6a](https://github.com/lukislp/studylife/commit/6ef2e6a175504cc4ecb0f0f780f429d9245f4e84))
* **monitoring:** exclude CoreDNS/NGF infra noise from the logs dashboard warnings ([0e08fb4](https://github.com/lukislp/studylife/commit/0e08fb4c14dfcb6b5376de457c238dcc0c45c419))
* **monitoring:** exclude NGF gRPC-reconnect and TLS-scanner noise from error panels ([0e37bea](https://github.com/lukislp/studylife/commit/0e37bea3a5c1daceab551d2836dfda6e532b4de6))
* **monitoring:** exclude redis_exporter's MOVED noise from the logs dashboard ([8b5de26](https://github.com/lukislp/studylife/commit/8b5de263a9d6e0558dc10f8aa2338f0e4e772d2b))
* **monitoring:** stop the studylife-mcp error-rate alert from firing on no data ([5d8c382](https://github.com/lukislp/studylife/commit/5d8c38234465a23fc98dc46de6f4a396fad91f4c))


### Features

* **monitoring:** alert on Uptime Kuma itself being down ([3af124c](https://github.com/lukislp/studylife/commit/3af124c3dc734b2886262e6c3b5988beb1ed6651))

# [1.13.0](https://github.com/lukislp/studylife/compare/v1.12.3...v1.13.0) (2026-08-14)


### Features

* **monitoring:** scrape Loki/Promtail, add a proper Kubernetes Logs dashboard ([1a6b87c](https://github.com/lukislp/studylife/commit/1a6b87c7890cbf563026f4bcaff447fd2867ab43))

## [1.12.3](https://github.com/lukislp/studylife/compare/v1.12.2...v1.12.3) (2026-08-14)


### Bug Fixes

* **monitoring:** deploy Loki log aggregation, drop broken k8s SD from promtail ([9caef8c](https://github.com/lukislp/studylife/commit/9caef8c76e3cb9a5dcd46bd81104364f60c14094))

## [1.12.2](https://github.com/lukislp/studylife/compare/v1.12.1...v1.12.2) (2026-08-14)


### Bug Fixes

* break .app-shell out of flex layout for print, restore pagination ([220fe23](https://github.com/lukislp/studylife/commit/220fe2332005bc7d36cc56cdb1d6d25338eda20a))
* reset html/body overflow for print, not just the inner containers ([39898c1](https://github.com/lukislp/studylife/commit/39898c19ddfb774d0b8dceeba9eb8a5fdac9682c))
* use "Notizen" as the notes PDF export's own document heading ([c222b37](https://github.com/lukislp/studylife/commit/c222b371481d19f2ee95d049d0747f7fd31e8731))

## [1.12.1](https://github.com/lukislp/studylife/compare/v1.12.0...v1.12.1) (2026-08-14)


### Bug Fixes

* remove the redundant "Woche drucken" button from the calendar ([c97e38f](https://github.com/lukislp/studylife/commit/c97e38fa273b854e9bae2506df9549451a3a576c))

# [1.12.0](https://github.com/lukislp/studylife/compare/v1.11.6...v1.12.0) (2026-08-14)


### Bug Fixes

* hide the visible focus ring FocusOnNavigate leaves on page headings ([5723b27](https://github.com/lukislp/studylife/commit/5723b27fb680a9e2233630761f324269a66b61f3))
* unclip the 00:00 label at the top of the calendar hour grid ([e799d33](https://github.com/lukislp/studylife/commit/e799d33397f8a23f4f10ed88c0812271e7bceebe))


### Features

* mark the end of the day at 24:00 in the calendar hour grid ([161aa1c](https://github.com/lukislp/studylife/commit/161aa1c765363b5650aad7a3b834a95d0fa4aa70))

## [1.11.6](https://github.com/lukislp/studylife/compare/v1.11.5...v1.11.6) (2026-08-14)


### Bug Fixes

* drop bottom-navbar safe-area padding, keep it flush again ([a07569f](https://github.com/lukislp/studylife/commit/a07569f4f073f05d0b5c8d1c7de8ad3577f7ff99))

## [1.11.5](https://github.com/lukislp/studylife/compare/v1.11.4...v1.11.5) (2026-08-14)


### Bug Fixes

* pad mobile page content below the notch/status bar ([04d6274](https://github.com/lukislp/studylife/commit/04d62747b59d4b8d2bfa27d9e60289743ab1c7ed))

## [1.11.4](https://github.com/lukislp/studylife/compare/v1.11.3...v1.11.4) (2026-08-14)


### Bug Fixes

* extend bottom navbar flush to the physical screen edge on iOS ([c16bc0d](https://github.com/lukislp/studylife/commit/c16bc0df81ce8f14691e6ca9e0e0c81a48c4f195))
* prevent iOS auto-zoom on focus for all text inputs, not just chat ([4a35450](https://github.com/lukislp/studylife/commit/4a35450460213f918c013717d8773dccfdc2f5b2))

## [1.11.3](https://github.com/lukislp/studylife/compare/v1.11.2...v1.11.3) (2026-08-14)


### Bug Fixes

* prevent iOS auto-zoom on chat input focus ([7b9cc26](https://github.com/lukislp/studylife/commit/7b9cc26b755ff0cd2e887be182cfcd01e319176c))

## [1.11.2](https://github.com/lukislp/studylife/compare/v1.11.1...v1.11.2) (2026-08-13)


### Bug Fixes

* **k8s:** correct every listener hostname to match the live Gateway ([a0a5952](https://github.com/lukislp/studylife/commit/a0a5952547aa12124277bcb6c0406ad9a2dee833))

## [1.11.1](https://github.com/lukislp/studylife/compare/v1.11.0...v1.11.1) (2026-08-13)


### Bug Fixes

* **tests:** assert AiApiKeyHash stays null for the seeded demo user ([d5fbd2a](https://github.com/lukislp/studylife/commit/d5fbd2a1f705252f358ec194b302ff8f70420507)), closes [#2](https://github.com/lukislp/studylife/issues/2)

# [1.11.0](https://github.com/lukislp/studylife/compare/v1.10.3...v1.11.0) (2026-08-13)


### Bug Fixes

* **monitoring:** scope currencyUSD formatting to the Value column only ([6e9437c](https://github.com/lukislp/studylife/commit/6e9437c25b0dbea997fbd4e70131b80ddfe270fc))


### Features

* **monitoring:** add 30-day total cost per user panel to studylife-ai dashboard ([87f5056](https://github.com/lukislp/studylife/commit/87f5056034b5e1c6938951bbdf104c06cc7d9e15))

## [1.10.3](https://github.com/lukislp/studylife/compare/v1.10.2...v1.10.3) (2026-08-13)


### Bug Fixes

* **monitoring:** raise Prometheus retention to 30d and PVC to 20Gi ([585a9a2](https://github.com/lukislp/studylife/commit/585a9a2c14d71d83c5238b1fa293f4493d0daa7d))

## [1.10.2](https://github.com/lukislp/studylife/compare/v1.10.1...v1.10.2) (2026-08-12)


### Bug Fixes

* **client:** broaden the chat input placeholder beyond just "notes" ([ec4bc17](https://github.com/lukislp/studylife/commit/ec4bc177d015d4b0e4a1965c3c2a2d1a1b795e85))

## [1.10.1](https://github.com/lukislp/studylife/compare/v1.10.0...v1.10.1) (2026-08-12)


### Bug Fixes

* **monitoring:** update stale NPM scrape targets to the Pis' real IPs ([1ff509f](https://github.com/lukislp/studylife/commit/1ff509fda0d142d69faab74235ea19b9db0036ee))

# [1.10.0](https://github.com/lukislp/studylife/compare/v1.9.4...v1.10.0) (2026-08-12)


### Features

* scrape studylife-ai metrics, add its own Grafana dashboard folder ([51918fe](https://github.com/lukislp/studylife/commit/51918fecdcd984b773ca42e397e4062644c48a7f))

## [1.9.3](https://github.com/lukislp/studylife/compare/v1.9.2...v1.9.3) (2026-08-11)


### Bug Fixes

* **client:** auto-scroll AI chat/agent messages into view ([b720cf6](https://github.com/lukislp/studylife/commit/b720cf643eeb9cff02946cf58b310055abc97900))

## [1.9.2](https://github.com/lukislp/studylife/compare/v1.9.1...v1.9.2) (2026-08-11)


### Bug Fixes

* **k8s:** allow studylife-ai to reach studylife-web on 8443, not 8080 ([376362f](https://github.com/lukislp/studylife/commit/376362f84c6ce2531d954a95df68ba870c458ae2))

## [1.9.1](https://github.com/lukislp/studylife/compare/v1.9.0...v1.9.1) (2026-08-11)


### Bug Fixes

* **k8s:** allow studylife-ai to reach studylife-web internally ([8cd35cd](https://github.com/lukislp/studylife/commit/8cd35cd52395e561040b59fc32917681af4052c3))

# [1.9.0](https://github.com/lukislp/studylife/compare/v1.8.1...v1.9.0) (2026-08-11)


### Features

* **client:** AI chat/agent modal, opened from the FAB speed dial ([941029b](https://github.com/lukislp/studylife/commit/941029b937692c72a8347e8b737896d97740c200))
* **shared:** DTOs for the studylife-ai chat/agent proxy endpoints ([c9777e7](https://github.com/lukislp/studylife/commit/c9777e7c5a8362155dbfc0b0f6b440b6aaf96e1d))

## [1.8.1](https://github.com/lukislp/studylife/compare/v1.8.0...v1.8.1) (2026-08-11)


### Bug Fixes

* **k8s:** reuse studylife-git-auth for studylife-ai instead of a new PAT ([e0ea772](https://github.com/lukislp/studylife/commit/e0ea7727306dd3572cb0d9291f30d2c81fd514c4))

# [1.8.0](https://github.com/lukislp/studylife/compare/v1.7.0...v1.8.0) (2026-08-11)


### Features

* **k8s:** onboard studylife-ai as a second Flux GitOps source ([204fed5](https://github.com/lukislp/studylife/commit/204fed5217685f8a9ce99b223583518c7ceb0b26))

# [1.7.0](https://github.com/lukislp/studylife/compare/v1.6.0...v1.7.0) (2026-08-11)


### Features

* add the studylife-ai proxy (token signing, client, controller) ([97d0008](https://github.com/lukislp/studylife/commit/97d0008bfbf848d14a3d5d8189ba4fd08bce6c21))
* register/revoke a user's AiApiKey with studylife-ai on generate/revoke ([c4de44f](https://github.com/lukislp/studylife/commit/c4de44fc903508d38998c8757b738957e1adda0d))

# [1.6.0](https://github.com/lukislp/studylife/compare/v1.5.9...v1.6.0) (2026-08-10)


### Features

* **server:** add dedicated studylife-ai API key slot ([f9b856a](https://github.com/lukislp/studylife/commit/f9b856a22f6ddfd658d478bda5801247a785dc48))

## [1.5.9](https://github.com/lukislp/studylife/compare/v1.5.8...v1.5.9) (2026-08-08)


### Bug Fixes

* point Flux GitOps at GitHub/GHCR instead of the retired GitLab instance ([3df1a5c](https://github.com/lukislp/studylife/commit/3df1a5cdb5aef337e9fb2683d4d06e2c7bb7fb0c))

## [1.5.8](https://github.com/lukislp/studylife/compare/v1.5.7...v1.5.8) (2026-08-08)


### Bug Fixes

* move the ambient-sound toggle next to the timer controls ([02e26f3](https://github.com/lukislp/studylife/commit/02e26f3e5f70835284c3a7526a969e88ce2f07c3))

## [1.5.7](https://github.com/lukislp/studylife/compare/v1.5.6...v1.5.7) (2026-08-08)


### Bug Fixes

* defuse the other hardcoded session date before it drifts stale too ([86447ec](https://github.com/lukislp/studylife/commit/86447ec955d0e00f7e72003754bd80d8894df274))

## [1.5.6](https://github.com/lukislp/studylife/compare/v1.5.5...v1.5.6) (2026-08-08)


### Bug Fixes

* stop session-creating tests from hardcoding a date that drifts stale ([0cceedc](https://github.com/lukislp/studylife/commit/0cceedcf9e278a7733a7b00ea5d87b11dc495777))
* stop the focus page clipping content when it grows past the viewport ([1184e1a](https://github.com/lukislp/studylife/commit/1184e1ab6570f8dc2a6043ec3a6fe80367eef584))

## [1.5.5](https://github.com/lukislp/studylife/compare/v1.5.4...v1.5.5) (2026-08-08)


### Bug Fixes

* relocalize dashboard/focus/stats/planner/wrapped text on a live language switch ([90d871c](https://github.com/lukislp/studylife/commit/90d871c44ca51e772937eece9f7b2f5f79f9955a))

## [1.5.4](https://github.com/lukislp/studylife/compare/v1.5.3...v1.5.4) (2026-08-07)


### Bug Fixes

* localize dashboard tagline, focus quotes, timer mode names, and focus tab title ([5519964](https://github.com/lukislp/studylife/commit/5519964359996a0dbac343ba23a0980b823dc558))

## [1.5.3](https://github.com/lukislp/studylife/compare/v1.5.2...v1.5.3) (2026-08-07)


### Bug Fixes

* remove the leftover .gitlab-ci.yml pipeline ([3667f01](https://github.com/lukislp/studylife/commit/3667f01a22c9a5f0d9690d1258ae580db838476f))

## [1.5.2](https://github.com/lukislp/studylife/compare/v1.5.1...v1.5.2) (2026-08-07)


### Bug Fixes

* point docker-compose at the real public GHCR image, drop dead registry prompts ([62fc731](https://github.com/lukislp/studylife/commit/62fc731a1959a0f1c8350d4c49f9abf4fbb8d804))
* re-trigger CI after the previous push's webhook was dropped during a GitHub Actions incident ([baaa73f](https://github.com/lukislp/studylife/commit/baaa73fa82e7820b759deb545c93aa61dbf6afcc))

## [1.5.1](https://github.com/lukislp/studylife/compare/v1.5.0...v1.5.1) (2026-08-06)


### Bug Fixes

* remove duplicate hour suffix in monthly goal warning text ([8e18c56](https://github.com/lukislp/studylife/commit/8e18c56f3634b1c2e22bfd9ac203ad60baed7411))

# [1.5.0](https://github.com/lukislp/studylife/compare/v1.4.0...v1.5.0) (2026-08-06)


### Features

* add a TimeProvider seam to BackgroundTaskService ([aaff82f](https://github.com/lukislp/studylife/commit/aaff82f52f09328434a143796a877962140bfff8))

# [1.4.0](https://github.com/lukislp/studylife/compare/v1.3.0...v1.4.0) (2026-08-05)


### Bug Fixes

* make DemoSeeder's wipe actually delete multi-tenant tables ([0c86acc](https://github.com/lukislp/studylife/commit/0c86acc5f0f6ef0167dff41ae0308b5f7de66e90))
* make the restore-swap failure test platform-independent ([26a4d2d](https://github.com/lukislp/studylife/commit/26a4d2d4f44893822693a6697eece831467ef009))
* show and apply the full monthly goal instead of the elapsed-weeks proration ([fef72a6](https://github.com/lukislp/studylife/commit/fef72a6afbefc7f7ffdd54bb4f17c588ce763c1a))


### Features

* raise real test coverage from 83% to 93%, exclude generated migrations from measurement ([d1dd1b9](https://github.com/lukislp/studylife/commit/d1dd1b92fe662e329c592a76dbcd9a0e8d998b2e))

# [1.3.0](https://github.com/lukislp/studylife/compare/v1.2.6...v1.3.0) (2026-08-05)


### Features

* add a self-hosted test coverage badge ([b41ab73](https://github.com/lukislp/studylife/commit/b41ab73c6ebad20ce039c782f3d71c0b04fcb9ab))

## [1.2.6](https://github.com/lukislp/studylife/compare/v1.2.5...v1.2.6) (2026-08-05)


### Bug Fixes

* explain the WASM boot wait on demo instances ([c09b63a](https://github.com/lukislp/studylife/commit/c09b63aab31d5e02c42df475ac7f02475768c674)), closes [#app](https://github.com/lukislp/studylife/issues/app)

## [1.2.5](https://github.com/lukislp/studylife/compare/v1.2.4...v1.2.5) (2026-08-05)


### Bug Fixes

* add a dashboard screenshot to the README ([d7467a7](https://github.com/lukislp/studylife/commit/d7467a765779a4e5400759ae43b0eaadcde0b7b9))

## [1.2.4](https://github.com/lukislp/studylife/compare/v1.2.3...v1.2.4) (2026-08-05)


### Bug Fixes

* use the dynamic license badge like the other repos ([0820d8d](https://github.com/lukislp/studylife/commit/0820d8d57a26611830362436949ad983c7c0daa3))

## [1.2.3](https://github.com/lukislp/studylife/compare/v1.2.2...v1.2.3) (2026-08-05)


### Bug Fixes

* persistent DEMO chip in the sidebar, suppress the backup banner on demo instances ([ad799e1](https://github.com/lukislp/studylife/commit/ad799e18fa6e9ff4b901cf03f99a4ce599f5bc1d))

## [1.2.2](https://github.com/lukislp/studylife/compare/v1.2.1...v1.2.2) (2026-08-05)


### Bug Fixes

* surface the public live demo link in the README ([ad2129f](https://github.com/lukislp/studylife/commit/ad2129fa6d1c4cfa1d9f48501feeda5a1e2db475))

## [1.2.1](https://github.com/lukislp/studylife/compare/v1.2.0...v1.2.1) (2026-08-05)


### Bug Fixes

* hide passkey/push management and backup cards on demo instances ([b83fcbe](https://github.com/lukislp/studylife/commit/b83fcbe6a8e7ff9095f5274586749c44d5a868bd))

# [1.2.0](https://github.com/lukislp/studylife/compare/v1.1.0...v1.2.0) (2026-08-05)


### Bug Fixes

* block the entire /api/backup path on demo instances ([e9cc293](https://github.com/lukislp/studylife/commit/e9cc293b4d5c8ef5fc8127d65423a219b25bd18d))


### Features

* seed a realistic demo dataset on every DEMO_MODE startup ([773a424](https://github.com/lukislp/studylife/commit/773a4245edf03e43354c7fad1b5f32c56517bcfe))

# [1.1.0](https://github.com/lukislp/studylife/compare/v1.0.2...v1.1.0) (2026-08-05)


### Features

* add DEMO_MODE for public read-only demo instances ([0d6cb24](https://github.com/lukislp/studylife/commit/0d6cb248273664a71d9947cd4d4484d3b278908c))

## [1.0.2](https://github.com/lukislp/studylife/compare/v1.0.1...v1.0.2) (2026-08-05)


### Bug Fixes

* use standard AGPL-3.0 license text so GitHub detects it correctly ([ceb85bc](https://github.com/lukislp/studylife/commit/ceb85bc086e7c90d7a0f0a929c0b721c93af738c))

## [1.0.1](https://github.com/lukislp/studylife/compare/v1.0.0...v1.0.1) (2026-08-04)


### Bug Fixes

* bump actions/checkout to v5 to resolve Node.js 20 deprecation warning ([71bacd8](https://github.com/lukislp/studylife/commit/71bacd86f6d98667feb37bcd99bb8e182ae003ed))
* correct artifact download path and bump remaining actions to Node 24 ([55709c2](https://github.com/lukislp/studylife/commit/55709c22a25c75c31004b2f5d4b88eb1a861a304))

# Changelog

All notable changes to this project will be documented in this file. This file is
maintained automatically by [semantic-release](https://semantic-release.gitbook.io/)
from [Conventional Commits](https://www.conventionalcommits.org/) — entries appear here
starting with the first release published from this repository.

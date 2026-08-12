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

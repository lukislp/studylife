# [2.15.0](https://github.com/lukislp/studylife/compare/v2.14.0...v2.15.0) (2026-08-29)


### Bug Fixes

* allow the marketplace's GitHub calls through CSP, cache listings for 24h ([6e6575c](https://github.com/lukislp/studylife/commit/6e6575ccc0df7f43fc39cf122f732dc92af04ef3))


### Features

* browse and install marketplace add-ons from the Setup page ([7f4629f](https://github.com/lukislp/studylife/commit/7f4629f5e3c0dc4c7c8eb04c18f007be1c930707))

# [2.14.0](https://github.com/lukislp/studylife/compare/v2.13.0...v2.14.0) (2026-08-29)


### Features

* wire studylife-developers into prod (config, network policy, shared secret) ([103ad8a](https://github.com/lukislp/studylife/commit/103ad8aa4d734a4c8ada7a348fd4d90ebd711dca))

# [2.13.0](https://github.com/lukislp/studylife/compare/v2.12.0...v2.13.0) (2026-08-29)


### Features

* add a dedicated toggle-style API key for the studylife-developers portal ([14c8825](https://github.com/lukislp/studylife/commit/14c8825b20b1d75a41d8d3f5db8cce3a8373b12c))

# [2.12.0](https://github.com/lukislp/studylife/compare/v2.11.0...v2.12.0) (2026-08-29)


### Features

* add a generic OAuth-style client registry for third-party add-ons ([6107ebb](https://github.com/lukislp/studylife/commit/6107ebb2c9596ae8d24dd7af7a202b07f7fc8ed2))

# [2.11.0](https://github.com/lukislp/studylife/compare/v2.10.0...v2.11.0) (2026-08-29)


### Features

* support multiple named webhooks API keys per user ([8146231](https://github.com/lukislp/studylife/commit/8146231d48fb13465245ea2eaaed10bd79caed82))

# [2.10.0](https://github.com/lukislp/studylife/compare/v2.9.0...v2.10.0) (2026-08-29)


### Features

* publish webhook events for notes, goals, resources, templates, programs, and plans ([81685b7](https://github.com/lukislp/studylife/commit/81685b740f1fa7b79d9d8c866b8505f57b8d269a))

# [2.9.0](https://github.com/lukislp/studylife/compare/v2.8.1...v2.9.0) (2026-08-29)


### Features

* add a Setup page card for the webhooks API key ([72473f1](https://github.com/lukislp/studylife/commit/72473f1adc0dda085d505d38d33245d14b9c079a))

## [2.8.1](https://github.com/lukislp/studylife/compare/v2.8.0...v2.8.1) (2026-08-29)


### Bug Fixes

* disable Redis AUTH (requirepass/masterauth) in prod ([ef804ad](https://github.com/lukislp/studylife/commit/ef804adfa8c50e5a6250f338a844d0b95bce9916))

# [2.8.0](https://github.com/lukislp/studylife/compare/v2.7.0...v2.8.0) (2026-08-29)


### Features

* wire studylife-webhooks BaseUrl/SharedSecret into prod config ([e0bbd31](https://github.com/lukislp/studylife/commit/e0bbd314210f76f6c5efcf6da5df4dd583c2b852))

# [2.7.0](https://github.com/lukislp/studylife/compare/v2.6.1...v2.7.0) (2026-08-29)


### Bug Fixes

* authenticate webhook registration with its own API key, not a session ([56a7521](https://github.com/lukislp/studylife/commit/56a7521fab9c8b9370e0b6f66fc67b3107c2bcef))


### Features

* add studylife-tray API key slot and a webhook event system ([bf8a3e3](https://github.com/lukislp/studylife/commit/bf8a3e382a0a140fed02685d05159db7e681c51c))

## [2.6.1](https://github.com/lukislp/studylife/compare/v2.6.0...v2.6.1) (2026-08-29)


### Bug Fixes

* dispatch the timer-state browser event unconditionally ([92a31ff](https://github.com/lukislp/studylife/commit/92a31ff430a871c99f24419d2832ec443ceca11c))

# [2.6.0](https://github.com/lukislp/studylife/compare/v2.5.1...v2.6.0) (2026-08-29)


### Features

* dispatch a browser event on every timer state change ([b1ce2f7](https://github.com/lukislp/studylife/commit/b1ce2f78d3d89ff7ba6035d4d2e49642861adf2b))

## [2.5.1](https://github.com/lukislp/studylife/compare/v2.5.0...v2.5.1) (2026-08-29)


### Bug Fixes

* regenerate openapi.json to drop stale TimeTrack DTOs ([5708e3f](https://github.com/lukislp/studylife/commit/5708e3f168505c0bab540b7eb487a9db222b0084))
* remove TimeTrack browser-extension API key slot ([a9f56c4](https://github.com/lukislp/studylife/commit/a9f56c4d1a97382516ea4eedaa801bb6fce80cf4))

# [2.5.0](https://github.com/lukislp/studylife/compare/v2.4.1...v2.5.0) (2026-08-28)


### Features

* add studylife-timetrack and studylife-focustunes API key slots ([ec92cdd](https://github.com/lukislp/studylife/commit/ec92cdde0ba7c5cc34d9cbe407c6efb8b4a25ded))

## [2.4.1](https://github.com/lukislp/studylife/compare/v2.4.0...v2.4.1) (2026-08-28)


### Bug Fixes

* hide dashboard backup-staleness hint when raw backup is unavailable ([9da997e](https://github.com/lukislp/studylife/commit/9da997e2dde0f4f65e06a2a744c98481542126d9))

# [2.4.0](https://github.com/lukislp/studylife/compare/v2.3.4...v2.4.0) (2026-08-28)


### Features

* add studylife-focusguard API key slot (identity contract v1, 5th consumer) ([efc5adc](https://github.com/lukislp/studylife/commit/efc5adc0d0d3aafca8e606fbab8e728684d2616e))

## [2.3.4](https://github.com/lukislp/studylife/compare/v2.3.3...v2.3.4) (2026-08-28)


### Bug Fixes

* restore StudyLifeAi secrets in sealed studylife-secrets ([2ef8797](https://github.com/lukislp/studylife/commit/2ef8797ee736b11c5d6712d057f5cd96719cc50b))

## [2.3.3](https://github.com/lukislp/studylife/compare/v2.3.2...v2.3.3) (2026-08-27)


### Bug Fixes

* planner course lookup goes through CourseResolver ([0f0c63a](https://github.com/lukislp/studylife/commit/0f0c63a97748e6d8e9a4982b628537e793b0f520))
* split AuthController into concern-partials (no contract change) ([df48714](https://github.com/lukislp/studylife/commit/df48714f8d9e976ccdce297d48e13ed4e44681a6))

## [2.3.2](https://github.com/lukislp/studylife/compare/v2.3.1...v2.3.2) (2026-08-27)


### Bug Fixes

* document consent-assertion provisioning in ARCHITECTURE.md ([0f76391](https://github.com/lukislp/studylife/commit/0f763914e78d644660483c53948c359e33b0870d))

## [2.3.1](https://github.com/lukislp/studylife/compare/v2.3.0...v2.3.1) (2026-08-27)


### Bug Fixes

* bring README in line with consent-flow provisioning and the invite gate ([3c6e6b4](https://github.com/lukislp/studylife/commit/3c6e6b4a1b4a15b0c9af55f831b787f2f9c69e6c))

# [2.3.0](https://github.com/lukislp/studylife/compare/v2.2.1...v2.3.0) (2026-08-27)


### Bug Fixes

* regenerate the OpenAPI spec after the main merge ([c227f89](https://github.com/lukislp/studylife/commit/c227f896091dffd35fe40b81029217f093c4513b))


### Features

* ai toggle and a single external-connections card ([006f4ff](https://github.com/lukislp/studylife/commit/006f4fff3caaec01299ef4c323fd572e58635a08))

## [2.2.1](https://github.com/lukislp/studylife/compare/v2.2.0...v2.2.1) (2026-08-27)


### Bug Fixes

* cut the release for the setup-card rework ([3efe251](https://github.com/lukislp/studylife/commit/3efe251236cc43c04de6232073e6106653283046))

# [2.2.0](https://github.com/lukislp/studylife/compare/v2.1.9...v2.2.0) (2026-08-27)


### Bug Fixes

* regenerate the OpenAPI spec after the main merge ([ddd5f4c](https://github.com/lukislp/studylife/commit/ddd5f4c47b00a4ff5724b4a8ec0fd2f1a83c95bd))


### Features

* consent provisioning for capture and the stdio loopback ([70f6eb2](https://github.com/lukislp/studylife/commit/70f6eb21abfc83ffb7695cca559dad491b57a52d))

## [2.1.9](https://github.com/lukislp/studylife/compare/v2.1.8...v2.1.9) (2026-08-27)


### Bug Fixes

* enable Redis AUTH (requirepass + masterauth) ([1dde58f](https://github.com/lukislp/studylife/commit/1dde58f8875ed67d32d08eff2ba5e0f1c7c06980))

## [2.1.8](https://github.com/lukislp/studylife/compare/v2.1.7...v2.1.8) (2026-08-27)


### Bug Fixes

* migrate the nine remaining pages to LocalizedComponentBase ([ca1d1e1](https://github.com/lukislp/studylife/commit/ca1d1e1330f989f7338ab92c48642763fcbcfb56))

## [2.1.7](https://github.com/lukislp/studylife/compare/v2.1.6...v2.1.7) (2026-08-27)


### Bug Fixes

* validate note course and session references, degrade gracefully elsewhere ([8982c36](https://github.com/lukislp/studylife/commit/8982c3606e4a5d08dd75e70bee194839f2a00bb7)), closes [hi#frequency](https://github.com/hi/issues/frequency)

## [2.1.6](https://github.com/lukislp/studylife/compare/v2.1.5...v2.1.6) (2026-08-27)


### Bug Fixes

* remove the obsolete tuple compatibility shims ([8a3287b](https://github.com/lukislp/studylife/commit/8a3287b05425b91d973cb76ba2e7a26290b23638)), closes [#7](https://github.com/lukislp/studylife/issues/7)

## [2.1.5](https://github.com/lukislp/studylife/compare/v2.1.4...v2.1.5) (2026-08-26)


### Bug Fixes

* cache immutable framework assets at the gateway during rollouts ([62fecfc](https://github.com/lukislp/studylife/commit/62fecfc9e334e708f187b8b4a1888030c5859333))

## [2.1.4](https://github.com/lukislp/studylife/compare/v2.1.3...v2.1.4) (2026-08-26)


### Bug Fixes

* migrate shared tuple contracts to record structs ([bd429f6](https://github.com/lukislp/studylife/commit/bd429f6795bfbd7a886278985e7136f7ae71a54b))

## [2.1.3](https://github.com/lukislp/studylife/compare/v2.1.2...v2.1.3) (2026-08-26)


### Bug Fixes

* route all grade formatting through StudyMetrics.FormatGrade ([ad0998a](https://github.com/lukislp/studylife/commit/ad0998ae83e83040bf80d8ed158461d17626c490))

## [2.1.2](https://github.com/lukislp/studylife/compare/v2.1.1...v2.1.2) (2026-08-26)


### Bug Fixes

* gate speech services behind Speech:Enabled, off for the k8s worker ([1bf417a](https://github.com/lukislp/studylife/commit/1bf417aa7344f262f8d9e32f0f3379014d989ad0))

## [2.1.1](https://github.com/lukislp/studylife/compare/v2.1.0...v2.1.1) (2026-08-26)


### Bug Fixes

* address ai internal calls via StudyLifeAi InternalBaseUrl ([3d8b0a2](https://github.com/lukislp/studylife/commit/3d8b0a2cbf07242dec6a6dd00da11fff4819a611))

# [2.1.0](https://github.com/lukislp/studylife/compare/v2.0.1...v2.1.0) (2026-08-26)


### Features

* validate CourseId on writes, derive course name and color server-side ([f04986f](https://github.com/lukislp/studylife/commit/f04986f7f24775388b639cb278b8d9f5d433fff4))

## [2.0.1](https://github.com/lukislp/studylife/compare/v2.0.0...v2.0.1) (2026-08-26)


### Bug Fixes

* serve all client script from external files, drop script-src unsafe-inline ([f8abb0f](https://github.com/lukislp/studylife/commit/f8abb0feae1c10b0e2360f65285207760093baf0))

# [2.0.0](https://github.com/lukislp/studylife/compare/v1.44.1...v2.0.0) (2026-08-26)


### Features

* registration gate with owner invites ([fbf8962](https://github.com/lukislp/studylife/commit/fbf896262e923458d4b7fbf5b2bd77d33fe23ea3))


### BREAKING CHANGES

* self-registration on an already-bootstrapped instance
now requires an invite link unless Registration__Mode=open is set.

## [1.44.1](https://github.com/lukislp/studylife/compare/v1.44.0...v1.44.1) (2026-08-26)


### Bug Fixes

* stop accepting query-string api keys, mask share token for key auth, fix history time windows ([76222db](https://github.com/lukislp/studylife/commit/76222db3bafe07ae74b0815279e7dbf2c05e71e4))

# [1.44.0](https://github.com/lukislp/studylife/compare/v1.43.3...v1.44.0) (2026-08-26)


### Features

* metrics API - every calculation lives once in Shared ([55b7ad0](https://github.com/lukislp/studylife/commit/55b7ad000b38897596b15eacd90e285d77192f6a))

## [1.43.3](https://github.com/lukislp/studylife/compare/v1.43.2...v1.43.3) (2026-08-26)


### Bug Fixes

* FormatGrade must not construct a culture under invariant globalization ([73ae5ca](https://github.com/lukislp/studylife/commit/73ae5cae1fc67c3e841b178601eb5120522f0348))

## [1.43.2](https://github.com/lukislp/studylife/compare/v1.43.1...v1.43.2) (2026-08-26)


### Bug Fixes

* DB-aware health probes and opt-in Redis AUTH preparation ([e214af5](https://github.com/lukislp/studylife/commit/e214af50c4c5e3e7f97726c2643f5b9d50362cb0))
* hygiene sweep - i18n guard base class, NaN quota, dead code, AOT shapes ([51d9547](https://github.com/lukislp/studylife/commit/51d954721b4c32cf4cfee00592a3f814ad3cdfeb))

## [1.43.1](https://github.com/lukislp/studylife/compare/v1.43.0...v1.43.1) (2026-08-26)


### Bug Fixes

* account-scoped caches, ordered timer pushes, session fetch dedup ([73c72a4](https://github.com/lukislp/studylife/commit/73c72a47a7ce14a0e31b1caee0606fb4051ffa37))
* allow the ha key slot to read study-program details ([acb8abb](https://github.com/lukislp/studylife/commit/acb8abb55e918f4cca5470eff8c8c4fdbc98488b))
* single migration owner, honest pull policy, dead legacy path removed ([cca2627](https://github.com/lukislp/studylife/commit/cca262769bdafb8eac064873853defb1c9f06e6e))

# [1.43.0](https://github.com/lukislp/studylife/compare/v1.42.1...v1.43.0) (2026-08-26)


### Features

* complete JSON export and per-user import with id remapping ([8addaef](https://github.com/lukislp/studylife/commit/8addaef8e784e3634be6a76f1927e25251059f64))

## [1.42.1](https://github.com/lukislp/studylife/compare/v1.42.0...v1.42.1) (2026-08-26)


### Bug Fixes

* explicit instance-owner flag instead of lowest-id semantics ([7ee2bdc](https://github.com/lukislp/studylife/commit/7ee2bdc2cfe8d3271fa17543c0512936fc311100))

# [1.42.0](https://github.com/lukislp/studylife/compare/v1.41.0...v1.42.0) (2026-08-26)


### Bug Fixes

* skip OpenAPI generation on RID-specific publishes ([65b7d0c](https://github.com/lukislp/studylife/commit/65b7d0cea0357a08a2db2b49a4ca8a1ca244ca02))


### Features

* publish the API contract as generated OpenAPI ([b187115](https://github.com/lukislp/studylife/commit/b187115224d78a248df7279cb8b3cf72408c7236))

# [1.41.0](https://github.com/lukislp/studylife/compare/v1.40.6...v1.41.0) (2026-08-26)


### Features

* scope each API-key slot to the endpoints its integration uses ([273c324](https://github.com/lukislp/studylife/commit/273c324a29626ecd4b57f52be7447ff41e5361f9))

## [1.40.6](https://github.com/lukislp/studylife/compare/v1.40.5...v1.40.6) (2026-08-26)


### Bug Fixes

* dedicated signing and internal-API secrets for the AI proxy ([efe62f1](https://github.com/lukislp/studylife/commit/efe62f147545e9a0930e5dc3cf4520323fbc14d2))

## [1.40.5](https://github.com/lukislp/studylife/compare/v1.40.4...v1.40.5) (2026-08-26)


### Bug Fixes

* settings versioning, non-forgeable backup timestamp, structural hash ([f6a2c77](https://github.com/lukislp/studylife/commit/f6a2c77758332eeba6041cc84bc426086d5dc06a))

## [1.40.4](https://github.com/lukislp/studylife/compare/v1.40.3...v1.40.4) (2026-08-26)


### Bug Fixes

* single timer-mode catalog and phase math in Shared ([3c3bc59](https://github.com/lukislp/studylife/commit/3c3bc599df80ad04b8e12b1676c81bb97d4b12d5))

## [1.40.3](https://github.com/lukislp/studylife/compare/v1.40.2...v1.40.3) (2026-08-26)


### Bug Fixes

* replace hand-rolled auth middleware with AuthenticationHandler and policies ([43f9e55](https://github.com/lukislp/studylife/commit/43f9e5554f540f95b3f5fdda0a5995c5c13bb8b3))

## [1.40.2](https://github.com/lukislp/studylife/compare/v1.40.1...v1.40.2) (2026-08-26)


### Bug Fixes

* **ci:** release from the branch tip, immune to Flux [skip ci] bump race ([5295d15](https://github.com/lukislp/studylife/commit/5295d15a7179646354839cd87ecbddf372eea0d5)), closes [#64](https://github.com/lukislp/studylife/issues/64) [#64](https://github.com/lukislp/studylife/issues/64)
* coordinate the offline write queue across browser tabs ([1fc43b8](https://github.com/lukislp/studylife/commit/1fc43b83e606be8cb042603c7bdae5dcd85f22d1))

## [1.40.1](https://github.com/lukislp/studylife/compare/v1.40.0...v1.40.1) (2026-08-26)


### Bug Fixes

* tolerant id-list parsing and per-user uniqueness for Settings/TimerState ([fa0ad38](https://github.com/lukislp/studylife/commit/fa0ad381c93acdbfc5cf91ae007ad01603ed2ecd))

# [1.40.0](https://github.com/lukislp/studylife/compare/v1.39.12...v1.40.0) (2026-08-26)


### Features

* make StudyLife the identity authority for its satellites ([9c46b57](https://github.com/lukislp/studylife/commit/9c46b5771560a4723d22ae64273b8876f268c3dd))

## [1.39.12](https://github.com/lukislp/studylife/compare/v1.39.11...v1.39.12) (2026-08-25)


### Bug Fixes

* single achievement catalog in Shared, ending live threshold drift ([83b3eb6](https://github.com/lukislp/studylife/commit/83b3eb6c5655dede2aef3dd2fc9fd10c1a235834))

## [1.39.11](https://github.com/lukislp/studylife/compare/v1.39.10...v1.39.11) (2026-08-25)


### Bug Fixes

* require explicit confirmation before DEMO_MODE wipes the database ([22c9dd6](https://github.com/lukislp/studylife/commit/22c9dd636acad46df0c2c10ade2e34b95f309a88))

## [1.39.10](https://github.com/lukislp/studylife/compare/v1.39.9...v1.39.10) (2026-08-25)


### Bug Fixes

* detect content-only session edits in the 30s poll ([742893a](https://github.com/lukislp/studylife/commit/742893a54661a36335d2947891fac72860a53ef4))
* drop first-user fallback in the API user-resolution middleware ([ffcb370](https://github.com/lukislp/studylife/commit/ffcb3702e833a67434eb30663ca4982ee70104f0)), closes [#1](https://github.com/lukislp/studylife/issues/1)
* stop offline write queue being wiped by auth expiry and server errors ([769d2db](https://github.com/lukislp/studylife/commit/769d2db553a8e183fea107df18a86b44a825a722))

## [1.39.9](https://github.com/lukislp/studylife/compare/v1.39.8...v1.39.9) (2026-08-25)


### Bug Fixes

* move learning-cluster placeholder secrets out of the bulk k8s apply set ([ed7c48b](https://github.com/lukislp/studylife/commit/ed7c48bd853ad87acfb3e3901b22b569ca231c6a))

## [1.39.8](https://github.com/lukislp/studylife/compare/v1.39.7...v1.39.8) (2026-08-25)


### Bug Fixes

* migrate redis-cluster PVCs to Longhorn for cross-node replication ([a13b2dc](https://github.com/lukislp/studylife/commit/a13b2dc1c65e6fd74a3fcd2ed4eb804f334d9556))

## [1.39.7](https://github.com/lukislp/studylife/compare/v1.39.6...v1.39.7) (2026-08-25)


### Bug Fixes

* migrate studylife-pg PVCs to Longhorn for cross-node replication ([4043b01](https://github.com/lukislp/studylife/commit/4043b019fe35b15294ec411daa844a9d1562cd6f))

## [1.39.6](https://github.com/lukislp/studylife/compare/v1.39.5...v1.39.6) (2026-08-24)


### Performance Improvements

* **ci:** cut test-unit time (drop workload restore, restore once, manifest tool) ([17f58d6](https://github.com/lukislp/studylife/commit/17f58d640d0fbdc34aa88c952cd5b609e64441ad))

## [1.39.5](https://github.com/lukislp/studylife/compare/v1.39.4...v1.39.5) (2026-08-24)


### Performance Improvements

* **ci:** native per-arch docker builds instead of QEMU emulation ([9e069e3](https://github.com/lukislp/studylife/commit/9e069e3c2f317151079705a29a71afe8a590102b))

## [1.39.4](https://github.com/lukislp/studylife/compare/v1.39.3...v1.39.4) (2026-08-24)


### Bug Fixes

* **k8s:** redis non-root, exporter hardening and probes, pooler liveness ([fb85894](https://github.com/lukislp/studylife/commit/fb8589448bbac2758a003eda580b200e2253bd7f))

## [1.39.3](https://github.com/lukislp/studylife/compare/v1.39.2...v1.39.3) (2026-08-22)


### Bug Fixes

* complete cluster-wide-infra split to homelab-infra ([fe8777b](https://github.com/lukislp/studylife/commit/fe8777bcd4e6820409b9c976943a0bcc824946f4))
* remove app Flux wiring now owned by each app's own repo ([a49e19a](https://github.com/lukislp/studylife/commit/a49e19aadeae60d8f8917bcb281c890795ce7331))

## [1.39.2](https://github.com/lukislp/studylife/compare/v1.39.1...v1.39.2) (2026-08-22)


### Bug Fixes

* correct MetalLB pool range and pin Gateway LB IP ([4d1150c](https://github.com/lukislp/studylife/commit/4d1150c3a9a6f6c01a7c12077ec502f514afacfa))

## [1.39.1](https://github.com/lukislp/studylife/compare/v1.39.0...v1.39.1) (2026-08-22)


### Bug Fixes

* **k8s:** backfill the unifidashboard Gateway listeners into the repo ([811c859](https://github.com/lukislp/studylife/commit/811c859a0e40d16488323c85ec871f305a8d25c9))

# [1.39.0](https://github.com/lukislp/studylife/compare/v1.38.0...v1.39.0) (2026-08-21)


### Features

* **flux:** onboard UnifiProtectDashboard into Flux-managed GitOps ([4203202](https://github.com/lukislp/studylife/commit/42032023652ffe35285227ff189a7d64dec67ec5))

# [1.38.0](https://github.com/lukislp/studylife/compare/v1.37.0...v1.38.0) (2026-08-21)


### Features

* match favicon/PWA icon to the native app's actual logo ([ae782a3](https://github.com/lukislp/studylife/commit/ae782a34256c26ce60fa9519d78fea1eaeec59c8))

# [1.37.0](https://github.com/lukislp/studylife/compare/v1.36.0...v1.37.0) (2026-08-21)


### Features

* bounded retry for capture enrichment + scope course matching to active courses ([14a1436](https://github.com/lukislp/studylife/commit/14a1436e0556f889b337e28143680bcdfaa3b493))

# [1.36.0](https://github.com/lukislp/studylife/compare/v1.35.0...v1.36.0) (2026-08-21)


### Features

* related notes for capture-enriched notes (S3) ([67c7ca6](https://github.com/lukislp/studylife/commit/67c7ca69874487258e2480e9778305aef57fc523))

# [1.35.0](https://github.com/lukislp/studylife/compare/v1.34.0...v1.35.0) (2026-08-21)


### Features

* AI enrichment for studylife-capture notes (S2) ([a4669b0](https://github.com/lukislp/studylife/commit/a4669b01eb10eed0c6d29377e0eaef37272a2581))

# [1.34.0](https://github.com/lukislp/studylife/compare/v1.33.0...v1.34.0) (2026-08-21)


### Features

* add per-user studylife-capture API key slot ([6c2c2d4](https://github.com/lukislp/studylife/commit/6c2c2d4ec479a63dc8dab1ae2e10451cc9280a4c))

# [1.33.0](https://github.com/lukislp/studylife/compare/v1.32.1...v1.33.0) (2026-08-21)


### Bug Fixes

* add sourceUrl to NoteDto contract-test snapshot ([9f9c40c](https://github.com/lukislp/studylife/commit/9f9c40c7d7bfc08e0b19d3f20f7b45700af5b790)), closes [#34](https://github.com/lukislp/studylife/issues/34)


### Features

* add SourceUrl to notes for external capture ([0fe7c9e](https://github.com/lukislp/studylife/commit/0fe7c9e9acf472d9ece57675fc56443591b21738))

## [1.32.1](https://github.com/lukislp/studylife/compare/v1.32.0...v1.32.1) (2026-08-21)


### Bug Fixes

* replace LINQ with plain loops in BuildCardioFitnessTrend to avoid iOS AOT crash ([b1cc145](https://github.com/lukislp/studylife/commit/b1cc145cc3bd5d48627f44b6b0a69faedc93ef5e))

# [1.32.0](https://github.com/lukislp/studylife/compare/v1.31.0...v1.32.0) (2026-08-20)


### Features

* add cardio fitness (VO2max) trend to Stats page (client-side, S3b) ([88226c4](https://github.com/lukislp/studylife/commit/88226c4cb106f5d7daa76cc74dd4f744fa473255))

# [1.31.0](https://github.com/lukislp/studylife/compare/v1.30.0...v1.31.0) (2026-08-20)


### Features

* add movement-break reminder to Focus Timer (client-side, S2b) ([ccc6cac](https://github.com/lukislp/studylife/commit/ccc6cacae0fd491b34ed65b65db22be59b5acb52))

# [1.30.0](https://github.com/lukislp/studylife/compare/v1.29.0...v1.30.0) (2026-08-20)


### Features

* add sleep consistency dashboard tile (client-side, S1b) ([d573ced](https://github.com/lukislp/studylife/commit/d573cedf7eb56a0d2f49d0e74c6baa2799ef85fd))

# [1.29.0](https://github.com/lukislp/studylife/compare/v1.28.0...v1.29.0) (2026-08-20)


### Features

* **dashboard:** show raw HRV value + baseline on the readiness card ([2352876](https://github.com/lukislp/studylife/commit/2352876848e0ba201c7ecf9451db30d25ac41e12))

# [1.28.0](https://github.com/lukislp/studylife/compare/v1.27.0...v1.28.0) (2026-08-20)


### Features

* **dashboard:** add HRV readiness score tile (SL-4 S3) ([de9512d](https://github.com/lukislp/studylife/commit/de9512def93830481b29d71dadce80e704cb2efb))

# [1.27.0](https://github.com/lukislp/studylife/compare/v1.26.1...v1.27.0) (2026-08-20)


### Features

* **dashboard:** add INativeHealthData abstraction for HRV readiness score (SL-4 S2) ([7af617b](https://github.com/lukislp/studylife/commit/7af617b01897244cb6fe057d29c917afae57db03))

## [1.26.1](https://github.com/lukislp/studylife/compare/v1.26.0...v1.26.1) (2026-08-20)


### Bug Fixes

* **notes:** move Vorlesen/Diktieren to their own toolbar row ([239cbbb](https://github.com/lukislp/studylife/commit/239cbbba6553f889fafd66230f1851eeffe58652))

# [1.26.0](https://github.com/lukislp/studylife/compare/v1.25.1...v1.26.0) (2026-08-20)


### Features

* **stt:** handle silent recordings, add browser recognition fallback (SL-3 S5) ([8afe392](https://github.com/lukislp/studylife/commit/8afe392f8872257e79b64b913d5644fb8f81aa54))

## [1.25.1](https://github.com/lukislp/studylife/compare/v1.25.0...v1.25.1) (2026-08-20)


### Bug Fixes

* **stt:** pin Whisper thread count to the cgroup-visible CPU count ([ad6518a](https://github.com/lukislp/studylife/commit/ad6518a4a63b2d1150bc64b676fef1ac04ec2f8b))

# [1.25.0](https://github.com/lukislp/studylife/compare/v1.24.0...v1.25.0) (2026-08-20)


### Features

* **stt:** cap dictation length, raise pod memory for the model (SL-3 S4) ([e87c743](https://github.com/lukislp/studylife/commit/e87c743f1169362260bd323ff90f617f00bc185e))

# [1.24.0](https://github.com/lukislp/studylife/compare/v1.23.0...v1.24.0) (2026-08-20)


### Features

* **stt:** add microphone recording UI to Notes (SL-3 S3) ([c18c582](https://github.com/lukislp/studylife/commit/c18c58214b11b05299306a0330389e54e9e31a9d))

# [1.23.0](https://github.com/lukislp/studylife/compare/v1.22.0...v1.23.0) (2026-08-20)


### Features

* **stt:** add voice dictation endpoint (SL-3 S2) ([e29782c](https://github.com/lukislp/studylife/commit/e29782cb55f8016aa5c5c4615ad8653018ae8a6e))

# [1.22.0](https://github.com/lukislp/studylife/compare/v1.21.6...v1.22.0) (2026-08-20)


### Features

* **stt:** add native Whisper transcription core (SL-3 S1) ([06f5e6e](https://github.com/lukislp/studylife/commit/06f5e6edcef86813ecaf81000d167b2821324d1b))

## [1.21.6](https://github.com/lukislp/studylife/compare/v1.21.5...v1.21.6) (2026-08-19)


### Bug Fixes

* **tts:** recognize more punctuation as pauses, dedupe slow retries ([989b7d1](https://github.com/lukislp/studylife/commit/989b7d1397183e86c1f5b76ebc6481dd50610815))

## [1.21.5](https://github.com/lukislp/studylife/compare/v1.21.4...v1.21.5) (2026-08-19)


### Bug Fixes

* **tts:** version the cache key so pipeline changes bust stale audio ([8366d9f](https://github.com/lukislp/studylife/commit/8366d9fea4af670fb5ce70287e8d91963cc9d3fb))

## [1.21.4](https://github.com/lukislp/studylife/compare/v1.21.3...v1.21.4) (2026-08-19)


### Bug Fixes

* **k8s:** replace unsupported HTTPRoute timeout with NGF's ProxySettingsPolicy ([f6d8c1e](https://github.com/lukislp/studylife/commit/f6d8c1e619f29c85052c1318680cd83e70a16e31))
* **tts:** retry once on timeout - server keeps working after the client gives up ([bc8d673](https://github.com/lukislp/studylife/commit/bc8d6733313bdd2a98a8385bf99308ebffecfc84))

## [1.21.3](https://github.com/lukislp/studylife/compare/v1.21.2...v1.21.3) (2026-08-19)


### Bug Fixes

* **tts:** raise gateway timeout, split per sentence, add inter-chunk silence ([d6da8ca](https://github.com/lukislp/studylife/commit/d6da8cac9c2a6c5b8d7b12fba29d21c1619438e8))

## [1.21.2](https://github.com/lukislp/studylife/compare/v1.21.1...v1.21.2) (2026-08-19)


### Bug Fixes

* **tts:** chunk long notes instead of one ONNX call for the whole text ([d32985b](https://github.com/lukislp/studylife/commit/d32985b11800dc66be5a5c8098a9d6334a92811c))

## [1.21.1](https://github.com/lukislp/studylife/compare/v1.21.0...v1.21.1) (2026-08-19)


### Bug Fixes

* **tts:** disable ONNX Runtime's CPU memory arena, raise pod limit again ([077e774](https://github.com/lukislp/studylife/commit/077e77416c8cc9b0fab7d209a329b472aa2a8592))

# [1.21.0](https://github.com/lukislp/studylife/compare/v1.20.0...v1.21.0) (2026-08-19)


### Bug Fixes

* **k8s:** raise studylife-web memory limit for the TTS feature ([5f1913f](https://github.com/lukislp/studylife/commit/5f1913f20a2e489096cc6859dfe2553fcfc779b4))


### Features

* **tts:** cache synthesized audio, close out the SL-2 plan (S5) ([3d69876](https://github.com/lukislp/studylife/commit/3d69876f296cf5a324e9de6d2a6e4e9ae1057a73))

# [1.20.0](https://github.com/lukislp/studylife/compare/v1.19.0...v1.20.0) (2026-08-19)


### Features

* **tts:** bake English voice + Web Speech API fallback (SL-2 S4) ([cf0a0ca](https://github.com/lukislp/studylife/commit/cf0a0ca1accab8e2126768a70346d4c4809185c8))

# [1.19.0](https://github.com/lukislp/studylife/compare/v1.18.0...v1.19.0) (2026-08-19)


### Features

* **tts:** add read-aloud endpoint, playback UI, and CSP fix (SL-2 S3) ([195a197](https://github.com/lukislp/studylife/commit/195a1979f34ea297fb8e41e152846efd32abb3b9))

# [1.18.0](https://github.com/lukislp/studylife/compare/v1.17.0...v1.18.0) (2026-08-19)


### Features

* **tts:** add markdown-to-speech-text preprocessing (SL-2 S2) ([742cd45](https://github.com/lukislp/studylife/commit/742cd45150d3d4d7de5853beb483a748901c6057))

# [1.17.0](https://github.com/lukislp/studylife/compare/v1.16.1...v1.17.0) (2026-08-19)


### Features

* **tts:** add native Piper voice synthesis core (SL-2 S1) ([b31c717](https://github.com/lukislp/studylife/commit/b31c7177eac72d0c7f69946814391c7be7032849))

## [1.16.1](https://github.com/lukislp/studylife/compare/v1.16.0...v1.16.1) (2026-08-16)


### Bug Fixes

* **notes:** default markdown notes to preview when opened ([6b4d050](https://github.com/lukislp/studylife/commit/6b4d05044618ab7a4c2fd65c00d726507e6bd5db))

# [1.16.0](https://github.com/lukislp/studylife/compare/v1.15.4...v1.16.0) (2026-08-16)


### Bug Fixes

* **docs:** mention the new Markdown note mode in the README ([1fbf60a](https://github.com/lukislp/studylife/commit/1fbf60a2ff56e65d562a044b48ec6e356f33b749))


### Features

* add a togglable Markdown mode for notes ([66e6f28](https://github.com/lukislp/studylife/commit/66e6f286da5665c3e85345585508b0808bcfeb04))

## [1.15.4](https://github.com/lukislp/studylife/compare/v1.15.3...v1.15.4) (2026-08-15)


### Bug Fixes

* parallelize independent data fetches across every page's initial load ([10208e8](https://github.com/lukislp/studylife/commit/10208e81d933880fb51047e3a75df40d0f0fef33))

## [1.15.3](https://github.com/lukislp/studylife/compare/v1.15.2...v1.15.3) (2026-08-15)


### Bug Fixes

* remove dead styles.css link, stop premature restore/status call ([2647426](https://github.com/lukislp/studylife/commit/264742689167d61d09883e2889cb8a17e6e97704))

## [1.15.2](https://github.com/lukislp/studylife/compare/v1.15.1...v1.15.2) (2026-08-15)


### Bug Fixes

* allow piwatch ingress to Prometheus for PVC usage queries ([10f9782](https://github.com/lukislp/studylife/commit/10f9782f59dcf7a126d90a848d6c5ac8f14d3601))

## [1.15.1](https://github.com/lukislp/studylife/compare/v1.15.0...v1.15.1) (2026-08-15)


### Bug Fixes

* use 100dvh instead of 100vh for the app-shell layout ([ad23345](https://github.com/lukislp/studylife/commit/ad2334592151de114a582a71354369a0f362a6ed))

# [1.15.0](https://github.com/lukislp/studylife/compare/v1.14.7...v1.15.0) (2026-08-15)


### Features

* **flux:** onboard piwatch into Flux-managed GitOps ([6be9556](https://github.com/lukislp/studylife/commit/6be955679b571be7a7157c2e9828ea0543df8064))

## [1.14.7](https://github.com/lukislp/studylife/compare/v1.14.6...v1.14.7) (2026-08-15)


### Bug Fixes

* **k8s:** add PodDisruptionBudgets for coredns, pg-pooler, nginx-gateway ([e3cd343](https://github.com/lukislp/studylife/commit/e3cd34382b5d068cc9cd44f8bc4102e47014dd4e)), closes [#3](https://github.com/lukislp/studylife/issues/3)

## [1.14.6](https://github.com/lukislp/studylife/compare/v1.14.5...v1.14.6) (2026-08-14)


### Bug Fixes

* **monitoring:** exclude Loki's own internal querier<->scheduler noise ([62ea3c4](https://github.com/lukislp/studylife/commit/62ea3c45b9344ac0057b05073a296b51e4e6e4aa))

## [1.14.5](https://github.com/lukislp/studylife/compare/v1.14.4...v1.14.5) (2026-08-14)


### Bug Fixes

* **monitoring:** exclude studylife's architectural Kestrel binding log ([0fd1752](https://github.com/lukislp/studylife/commit/0fd175275eae9b42e8ae9f560bf0c22d4d5e447f))

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

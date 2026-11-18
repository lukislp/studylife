# Privacy policy template for self-hosted StudyAtrium servers

**This is a template, not legal advice.** If you run a StudyAtrium server for anyone besides yourself (family, friends, a class, a company), you are the controller under the GDPR for the data on that server. The publisher's policy at legal.lktec.org covers the app, not your installation. Copy this text to a page you control, replace every `TODO`, delete what does not apply, have the result checked if you can, and publish its address as `Legal__PrivacyUrl` (see the README, "Your own privacy policy and imprint"). Put your imprint at `Legal__ImprintUrl` if you need one.

Where this template says "we", it means you, the operator.

---

# Privacy policy of the StudyAtrium server at TODO: address

Last updated: TODO: date

## 1. Controller

- **TODO:** name and postal address of the person or organisation that runs this server
- **TODO:** e-mail address for privacy requests
- **TODO:** data protection officer, if you have to appoint one

## 2. What this server stores

StudyAtrium is a study planner. For each account the server stores what the user enters or the app records:

- **Account:** display name, login name and a password hash (if password sign-in is on), passkey public keys, recovery codes (hashed), sign-in sessions with the time and device description of each.
- **Study data:** courses, study programs, sessions and their timing, goals, notes, grades, settings.
- **Notifications:** push subscription endpoints, if the user turns push on (browser push services or Apple Push Notification service for the native app).
- **Technical data:** server logs with IP address, time and request path. **TODO:** retention, for example 7 days. Optional metrics and traces if you enabled `Telemetry`. **TODO:** state whether you did.

The data is stored in **TODO:** SQLite file or PostgreSQL database at hosting provider / location. Backups: **TODO:** how often, where, how long.

## 3. Purposes and legal bases

- Providing the study planner to the user, Art. 6 (1) (b) GDPR (contract) or, for a private group you run for free, Art. 6 (1) (f) (legitimate interest in running it). **TODO:** pick what fits.
- Security and abuse prevention (logs, rate limits), Art. 6 (1) (f) GDPR.
- Push notifications and the optional AI assistant, Art. 6 (1) (a) GDPR (consent, withdrawable in Setup at any time).

## 4. Recipients and add-ons

Data leaves this server only in these cases. Delete the ones that do not apply.

- **Hosting provider:** TODO: name, location, data processing agreement.
- **Push services:** if push is on, the browser vendor's push service or Apple (APNs) receives the notification payload and a device token.
- **AI provider (only if `StudyLifeAi` is configured):** when a user turns on the AI assistant and consents, the text they send to it and the study context needed to answer is sent to **TODO: provider name, location, link to its privacy policy**. Nothing is sent without that consent.
- **Add-ons, API keys, webhooks, Home Assistant, Telegram, MCP and other integrations:** these receive data only after a user created a key or connection for them in Setup, and only the data that key is allowed to read or write. **TODO:** list the add-ons you run or allow, who operates them, and where they run. Users can see and revoke every connection in Setup.
- **Text to speech / speech to text (only if configured):** **TODO:** provider, or "runs on our own server".
- **Other:** TODO: e-mail delivery, monitoring, error reporting, support tools.

## 5. Retention

- Account and study data: until the user deletes the account (Setup, Delete account) or we delete it on request. Deletion removes the data from the live database; **TODO:** state when it disappears from backups.
- Sessions: until they expire or the user signs out.
- Logs: TODO: period.
- **TODO:** whether you delete inactive accounts and after how long (StudyAtrium does not do this by itself).

## 6. Your rights

You can ask us for access (Art. 15), rectification (Art. 16), erasure (Art. 17), restriction (Art. 18), data portability (Art. 20) and object to processing based on legitimate interests (Art. 21). Most of this you can do yourself: Setup has a data export and an account deletion. For everything else write to **TODO: contact address**. If a consent is the basis, you can withdraw it at any time with effect for the future.

You can complain to a data protection supervisory authority, for example the one in your country of residence or the one responsible for us: **TODO:** name and address.

## 7. Cookies and local storage

The web client stores a sign-in session and your settings in your browser so it works; it uses no advertising or tracking cookies. **TODO:** confirm, and add anything you added yourself (analytics, a reverse proxy that sets cookies).

## 8. Changes

**TODO:** how you announce changes, and where older versions can be read.

<table align="center">
  <tr>
    <td align="center" width="240">
      <img src="https://raw.githubusercontent.com/scryer-media/scryer/main/apps/scryer-web/public/scryer-logo.svg" alt="Scryer logo" width="180" />
    </td>
    <td align="center" width="100">
      <h1>+</h1>
    </td>
    <td align="center" width="240">
      <img src="https://raw.githubusercontent.com/jellyfin/jellyfin-ux/master/logos/SVG/jellyfin-icon--color-on-light.svg" alt="Jellyfin logo" width="180" />
    </td>
  </tr>
</table>

<h1 align="center">Scryer for Jellyfin</h1>

<p align="center">
  Scryer-powered discovery, requests, calendar, and download status inside Jellyfin.
</p>

Scryer for Jellyfin adds Scryer features directly to the Jellyfin web interface. Each
Jellyfin user connects their own Scryer account through OAuth; the plugin does not use a
shared Scryer identity or ask users for their Jellyfin password.

This project is currently Alpha software. Its behavior and security contract are defined
by [RFC 153](https://github.com/scryer-media/scryer-docs/blob/main/plans/153-jellyfin-plugin-scryer-alpha-rfc.md).

## What it provides

- Discovery and search backed by Scryer's catalog.
- Movie, series, and anime request workflows governed by Scryer permissions.
- A calendar of upcoming media.
- Read-only active-download and download-history views.
- Native Scryer Discovery browsing and heart-to-add/request on stock Android TV 12+.
- Per-user OAuth connections with S256 PKCE and rotating refresh grants.
- Jellyfin theme and custom-CSS compatibility through the retained
  `Web/scryer-styles.js` runtime layer.

Scryer remains the authority for library visibility, request permissions, approval, and
title management. Being a Jellyfin administrator only grants access to plugin
configuration; it does not grant Scryer permissions.

## Requirements

- Jellyfin 10.11.x with the bundled Jellyfin web client.
- The current Scryer release, including its built-in Jellyfin plugin OAuth setup.
- Browser-reachable HTTPS URLs for Scryer and Jellyfin. Plain HTTP is accepted only for
  loopback development; private-network HTTP for the internal Scryer connection requires
  an explicit insecure opt-in.
- A separate Scryer account for each person who will use the plugin.
- Android TV discovery requires Android TV 12/API 31 or newer because its stock image
  decoder supplies the AVIF support used by Scryer posters.

Native clients other than the Android TV surface documented below, and unrelated
third-party Jellyfin interfaces, are not part of the Alpha support target.

## Install the plugin

### From the Jellyfin plugin catalog

In Jellyfin, open **Dashboard > Plugins > Repositories**, add a repository named
`Scryer`, and use this manifest URL:

```text
https://raw.githubusercontent.com/scryer-media/jellyfin-plugin-scryer/main/manifest.json
```

After saving the repository, leave and reopen **Plugins** (or reload the page) so the
already-open catalog does not keep its previous package list. Install **Scryer**, restart
Jellyfin, and confirm **Dashboard > Plugins** reports Scryer as **Active**. The
repository manifest describes the currently published packages; unreleased source changes
are not included. Use the `main` URL above rather than a feature-branch manifest URL.

### From a release archive or local build

Download the plugin archive from the repository's Releases page and extract its DLL into
a dedicated Scryer directory beneath Jellyfin's plugin directory, then restart Jellyfin.
The exact plugin directory is installation-specific; Docker installations normally
persist it beneath the container's `/config/plugins` mount.

To build the current checkout instead:

```sh
dotnet build -c Release --no-restore
```

Copy `bin/Release/net9.0/Jellyfin.Plugin.Scryer.dll` into the same dedicated plugin
directory and restart Jellyfin. The browser assets are embedded in the DLL; do not copy
the `Web` directory separately.

## Set up Jellyfin plugin OAuth in Scryer

Current Scryer releases include guided OAuth setup for the Jellyfin plugin. Scryer derives
the exact callback, creates the correctly scoped public OAuth client, and provides the
client ID needed by the plugin. No client secret or manual custom-application form is
required.

1. Determine the browser-visible public Jellyfin base URL, including its HTTPS port when
   it uses a non-standard port, such as `https://jellyfin.example.com:8443`.
2. In Scryer, open **Settings > Security > OAuth applications** and find
   **Jellyfin plugin OAuth**.
3. Enter the public Jellyfin base URL. Scryer displays the derived callback:

   ```text
   <public-jellyfin-url>/Scryer/Auth/Callback
   ```

4. Select **Create Jellyfin plugin client**. If one eligible client already uses that
   exact callback, Scryer safely reuses it instead of creating a duplicate.
5. Copy the displayed OAuth client ID into the Jellyfin plugin settings.

If Scryer has exactly one enabled Jellyfin media-server connection matching the same
public URL, with account linking and its API key configured, the setup also reports
automatic account linking as ready. That media-server connection is a convenience: it
can prefill the URL and enable automatic identity linking, but it is not required to
create the plugin OAuth client or configure the plugin.

## Configure the plugin in Jellyfin

Open **Dashboard > Plugins > Scryer** and enter:

1. **Internal Scryer URL** — the address the Jellyfin server can reach. In a macOS or
   Windows Docker development setup this may resemble
   `http://host.docker.internal:18480`; enable insecure internal HTTP only on an
   isolated, trusted development network.
2. **Public Scryer URL** — the Scryer address users' browsers can reach for OAuth
   authorization, normally an HTTPS URL such as `https://scryer.example.com`.
3. **Scryer OAuth Client ID** — the public client ID created in Scryer. This is not a
   secret.
4. **Public Jellyfin URL** — the exact browser-visible Jellyfin base URL, normally an
   HTTPS URL such as `https://jellyfin.example.com`.
5. Enable the desired Discovery, Requests, Calendar, and Downloads pages.
6. Save the configuration.

The plugin derives this callback from the public Jellyfin URL:

```text
<public-jellyfin-url>/Scryer/Auth/Callback
```

The callback displayed by Jellyfin must exactly match the callback displayed by the
Scryer OAuth client. URLs must be absolute HTTP(S) URLs without credentials, query
strings, or fragments.

After saving, select **Run diagnostics**. A healthy result confirms configuration, OAuth
metadata, the required GraphQL contract, and Jellyfin web injection. Diagnostics are
read-only and intentionally omit credentials, OAuth material, response bodies, and
linked-user identities.

## Connect a user

Each person completes these steps independently:

1. Sign in to Jellyfin with their own Jellyfin account.
2. Open **Scryer > Discover**, **Calendar**, **Requests**, or **Downloads** in the
   Jellyfin sidebar.
3. Select **Connect Scryer**.
4. Sign in to Scryer and approve the requested access.
5. Return to Jellyfin and confirm that the page reports **Scryer connected**.

The browser never receives the stored Scryer refresh grant. Switching Jellyfin users
changes the active plugin identity, cache, permissions, and Scryer grant. Do not share a
Jellyfin login between people who need separate Scryer identities.

## Android TV discovery

The Android TV discovery channel is **off by default**. Turn it on with **Enable the
Android TV discovery channel** on the plugin configuration page; it also requires
**Enable discovery**.

It is opt-in because a Jellyfin channel is not a live view. Jellyfin persists every
channel item it fetches as a row in its own library database, separately for each Jellyfin
user, and re-fetches them from the daily **Refresh Channels** scheduled task. On a server
with many users this creates a correspondingly large number of rows.

How much the channel publishes is bounded, because Scryer's discovery query takes no size
arguments and the server decides how many sections and titles to send. **Android TV rails
to publish** caps the rails, defaulting to 8 and never exceeding 25. **Titles per rail**
caps each rail's contents, defaulting to 20 and never exceeding 100. Both are applied
before Jellyfin sees the response, so they bound the rows and posters stored on the
Jellyfin server rather than merely what a television draws. Lowering a limit also makes
the rails it excluded unreachable, so an old folder id cannot reopen them.

The plugin retracts the rows it caused. A cleanup sweep runs once at server startup and
again whenever the plugin configuration is saved. With the channel off it removes every
Scryer discovery channel item Jellyfin has stored, together with the metadata directories
and downloaded posters that belong to them. With the channel on it removes the legacy
`Series` rows written by 0.1.14.0, which shipped the channel enabled, and any guidance stub
such as **Connect Scryer in Jellyfin Web**; the current channel emits container folders
only, so favourites set on valid entries survive. The sweep skips rows it cannot delete,
records them in the log, and is safe to repeat. The empty **Scryer Discovery** channel
entry itself is a Jellyfin-owned object: Jellyfin recreates it on every **Refresh
Channels** run, so the plugin never deletes it.

A guidance stub describes a moment rather than content, so it must not outlive that moment.
Jellyfin retires a channel row only when it re-queries the channel, and it re-queries only
when the channel's cache key changes or its own three-hour cache lapses. The cache key
therefore includes whether that Jellyfin user has a stored Scryer grant, so connecting
Scryer immediately invalidates a **Connect Scryer in Jellyfin Web** card rather than
leaving it on screen for a connected user.

Every guidance card also explains itself in the Jellyfin log. A **Scryer Discovery
unavailable** card is written at warning level together with the Scryer failure code that
produced it, such as `scryer_offline` or `invalid_response`, so a card on a television
corresponds to a line an administrator can act on. A user who has simply not linked an
account yet is an ordinary state and is recorded at debug level instead.

Once enabled, the unmodified Jellyfin Android TV client exposes **Scryer Discovery** as a
channel tile under **My Media**. Android TV users must first link the same Jellyfin user to
Scryer by following **Connect a user** in Jellyfin Web; the television does not run a
separate OAuth flow.

Open the channel to browse up to five **More like...** rails derived from that Jellyfin
user's recent watch history, followed by their available Scryer discovery sections.
Titles intentionally appear as non-playable container folders, including movies, so the
stock client does not offer a broken Play action for media that is not yet present and
Jellyfin does not store them as real Series entries or run external metadata lookups
against their names.

Use the standard Favorite heart on a title to send it to Scryer:

- With `MANAGE_TITLES`, the title is added directly to the single compatible default
  Scryer library.
- Otherwise, with `REQUEST`, a normal media request is submitted.
- The default library must have its appropriate default quality profile configured.
- Successful actions keep the heart selected. Clearing the heart does not remove or
  cancel anything in Scryer; it only permits a later retry.

The action always uses `MONITORED`. Jellyfin displays a native success or failure message
on the active user's clients. Scryer's AVIF posters are handed to Jellyfin unchanged.
Jellyfin downloads and caches each poster, and because its server-side Skia encoder cannot
transcode AVIF it serves the original bytes regardless of the requested size, so the
Android TV 12+ client decodes them itself. Older clients show placeholder artwork.

## Permissions and feature flags

Pages and actions follow the connected Scryer user's library grants:

- `VIEW` permits visible library data, calendar, and read-only downloads.
- `REQUEST` permits request submission.
- `AUTO_APPROVE_REQUESTS` controls automatic approval in Scryer.
- `MANAGE_TITLES` permits supported request and title-management actions.

Disabling a feature in the plugin removes its capability and causes its direct plugin
endpoint to reject use. It is not merely a cosmetic navigation setting.

## Troubleshooting

- **Scryer is missing from the plugin catalog:** confirm the repository is enabled and
  uses the exact `main` manifest URL above, then leave and reopen **Plugins** or reload
  Jellyfin Web. If it is still missing, run **Dashboard > Scheduled Tasks > Update
  Plugins**, wait for completion, and reload the catalog.
- **Scryer pages do not appear:** confirm the plugin is enabled, restart Jellyfin after
  installation, use the bundled Jellyfin web client, and run plugin diagnostics.
- **Callback mismatch:** copy the exact calculated callback; check scheme, hostname,
  port, path prefix, and reverse-proxy configuration on both sides.
- **OAuth opens but cannot return:** verify that the browser can reach both public URLs
  and that the public Jellyfin URL is the same origin the user opened.
- **Diagnostics cannot reach Scryer:** verify the internal URL from the Jellyfin server
  or container, not only from the browser. Do not substitute the public URL unless it is
  also reachable from Jellyfin.
- **Permission denied or pages are missing:** inspect the connected Scryer user's library
  grants. Jellyfin administrator status does not add Scryer permissions.
- **Posters or links use the wrong host:** verify the public Scryer URL separately from
  the internal server-to-server URL.

## Security model

The inherited shared API-key transport is intentionally not used for normal plugin
operation. Each feature request resolves the currently authenticated Jellyfin user's own
protected Scryer grant. Authorization Code with S256 PKCE is one-time, browser-bound, and
short-lived; access tokens remain memory-only and refresh grants are stored server-side
in protected form.

Do not restore a shared Scryer API key as a troubleshooting workaround. Do not expose the
internal Scryer URL, Jellyfin administrator API key, OAuth grants, or plugin data directory
to untrusted clients.

### Encrypted grant storage

Protected refresh grants live in `<jellyfin data>/plugins/scryer/oauth-grants`, and the
encryption key ring that reads them lives in `<jellyfin data>/plugins/scryer/keys`. The
plugin owns this key ring rather than using Jellyfin's own DataProtection provider,
because that provider is ephemeral whenever the server runs without a writable user
profile — the standard Docker deployment — which would silently disconnect every linked
user on each restart.

Back up both directories together and restore them together. If the key ring is lost or
replaced, existing grants can no longer be decrypted: the plugin renames each affected
record with an `.undecryptable-<timestamp>` suffix instead of deleting it, logs one
warning, and every affected user must reconnect Scryer from Jellyfin Web.

## Theme compatibility

`Web/scryer-styles.js` is an intentional compatibility layer, not a generated artifact.
It installs minimal, idempotent layout rules while relying on Jellyfin component classes
so active themes and server custom CSS continue to shape the UI. Browser-runtime changes
must retain it in the ordered injection path and preserve the single `scryer-style`
installation guard.

## Development and validation

No dependencies are installed by this repository. With the required .NET SDK and existing
package cache available, build with:

```sh
dotnet build --no-restore
node --test Web/tests/scryer-browser-lifecycle.test.js Web/tests/scryer-runtime.test.js
```

To verify the published repository through Jellyfin's real package installer, run the
Docker smoke test (requires Docker, curl, jq, and Python 3):

```sh
tests/catalog-install-smoke.sh
```

The smoke test starts a clean Jellyfin 10.11.11 instance, discovers Scryer through the
public `main` manifest, installs the archive, restarts Jellyfin, and verifies that the
plugin is active and serves the current OAuth configuration page rather than the legacy
API-key page. Pass another manifest URL as the first argument when validating a candidate
repository.

Real-instance validation should use multiple Jellyfin users mapped to distinct Scryer
users with different permissions. Do not test against production without separate,
explicit environment authorization.

## License and source adaptations

The plugin is GPL-3.0-only. See [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).

The Jellyfin mark shown above is the official SVG from the
[Jellyfin UX repository](https://github.com/jellyfin/jellyfin-ux/tree/master/logos/SVG);
Jellyfin is a trademark of its respective owner.

SeerrFin is an MIT-licensed UX oracle and Jellyfin Enhanced is a GPL-3.0 integration
oracle. Behavioral study does not require a notice. Before copying or substantially
adapting source from either project, follow [NOTICE.md](NOTICE.md) in the same change.

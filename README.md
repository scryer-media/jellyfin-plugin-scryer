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
- Per-user OAuth connections with S256 PKCE and rotating refresh grants.
- Jellyfin theme and custom-CSS compatibility through the retained
  `Web/scryer-styles.js` runtime layer.

Scryer remains the authority for library visibility, request permissions, approval, and
title management. Being a Jellyfin administrator only grants access to plugin
configuration; it does not grant Scryer permissions.

## Requirements

- Jellyfin 10.11.x with the bundled Jellyfin web client.
- Scryer 0.19.8 with manual custom OAuth application registration.
- Browser-reachable HTTPS URLs for Scryer and Jellyfin. Plain HTTP is accepted only for
  loopback development; private-network HTTP for the internal Scryer connection requires
  an explicit insecure opt-in.
- A separate Scryer account for each person who will use the plugin.

Native television clients and unrelated third-party Jellyfin interfaces are not part of
the Alpha support target.

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

## Set up Scryer OAuth

Scryer 0.19.8 uses manual custom OAuth application registration. Its setup does not
prefill values from a Jellyfin media-server connection and does not automatically link a
Jellyfin identity to a Scryer account.

The plugin uses a public OAuth client with a client ID and no client secret:

1. Determine the browser-visible public Jellyfin URL. The required callback is exactly:

   ```text
   <public-jellyfin-url>/Scryer/Auth/Callback
   ```

2. In Scryer, open **Settings > Security > OAuth applications** and select
   **Register an application**.
3. Enter a descriptive application name such as `Scryer for Jellyfin`.
4. Enter the exact callback in **HTTPS callback URLs**. Scryer 0.19.8 accepts one exact
   HTTPS callback URL per line.
5. Create the application and copy its OAuth client ID into the Jellyfin plugin settings.

A Scryer Jellyfin media-server connection is not required for this manual OAuth client
registration.

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

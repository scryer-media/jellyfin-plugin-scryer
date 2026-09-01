# Jellyfin Plugin Scryer

Scryer is a GPL-3.0-only Jellyfin companion for Scryer. Alpha work follows
[RFC 153](https://github.com/scryer-media/scryer-docs/blob/main/plans/153-jellyfin-plugin-scryer-alpha-rfc.md).

## Supported contract

- **Jellyfin:** 10.11.x web and embedded-web clients. Native TV clients and unrelated
  third-party UIs are outside Alpha.
- **Scryer:** 0.19.6, with OAuth Authorization Code + S256 PKCE, rotating refresh-token
  grants, the separately consented `library jellyfin-link` scope pair, OAuth-bound Jellyfin
  account linking, and the explicit GraphQL operations recorded in RFC 153.
- **License:** GPL-3.0-only. See [LICENSE](LICENSE) and [NOTICE.md](NOTICE.md).

## Security architecture

Jellyfin administrator status only controls plugin configuration. It never grants a
person Scryer media, request, or moderation authority. OAuth stores one protected grant
per Jellyfin user, while access tokens remain memory-only. Authorization Code + S256 PKCE
flows are one-time, browser-bound, and expire after ten minutes. The browser receives only
bounded plugin DTOs and connection state, never Scryer bearer tokens, refresh tokens,
authorization codes, or PKCE verifiers.

The inherited shared API-key transport has been removed from normal operation. Every enabled
feature endpoint resolves the authenticated Jellyfin user's own protected Scryer grant. Do not
restore a shared server key as a workaround.

## Administrator setup

1. Configure the **Internal Scryer URL**, reachable by the Jellyfin server.
   HTTPS is required unless the URL is loopback. Cleartext HTTP to another private-network
   address requires the explicit insecure opt-in and exposes OAuth material in transit.
2. Configure the browser-visible **Public Scryer URL**.
3. In Scryer, open **Security > OAuth applications** and use **Jellyfin plugin OAuth**.
   Enter the browser-visible public Jellyfin URL. This standalone setup does not require
   a Scryer media-server connection or an existing account link. If exactly one eligible
   Jellyfin connection already has that external URL, Scryer may prefill it as a convenience.
4. Copy the generated public **Scryer OAuth Client ID** into the plugin. There is no client
   secret. Configure the same browser-visible **Public Jellyfin URL** in the plugin.
5. Confirm that Scryer and the plugin display the same exact callback URI:

   ```text
   <public-jellyfin-url>/Scryer/Auth/Callback
   ```

   Both configuration surfaces compute and display the normalized exact value. Public URLs
   must use HTTPS except for loopback development. All URLs must be absolute HTTP(S) URLs
   without credentials, query strings, or fragments.
6. Save, then use **Run diagnostics**. It performs bounded, read-only OAuth metadata
   and GraphQL reachability checks and shows injection status. It never displays
   credentials, OAuth material, response bodies, or linked-user identities.

OAuth sign-in works with the standalone client. Automatic durable Jellyfin-account linking
additionally requires exactly one matching Scryer Jellyfin connection that is enabled,
linking-enabled, and credentialed so Scryer can independently verify the Jellyfin user.

## Alpha scope

The planned product surface is discovery/search, request submission and management,
calendar, and read-only downloads, all gated by the connected Scryer account's library
permissions. Disabled features must ultimately be hidden from navigation and reject
direct server calls. Direct Radarr/Sonarr integration, global-search injection, queue
mutation, native-TV support, and browser-held credentials are out of scope.

## Theme compatibility

`Web/scryer-styles.js` is an intentional compatibility layer and remains part of the
supported browser runtime. It supplies idempotent, minimal layout rules while relying on
Jellyfin's own component classes so active themes and server custom CSS continue to shape
the UI. Browser-runtime refactors must keep this script in the ordered injection path,
preserve the single `scryer-style` installation guard, and include a materially different
community theme in visual smoke testing.

## Validation

No dependencies are installed by this repository. With the existing SDK and package
cache available, run:

```sh
dotnet build --no-restore
```

Before a real-instance smoke test, verify the Scryer OAuth client registration matches
the displayed callback URI and use two distinct Jellyfin/Scryer identities. Do not test
against production without separately authorized environment access.

## Third-party source adaptations

SeerrFin is an MIT-licensed UX oracle and Jellyfin Enhanced is a GPL-3.0 integration
oracle. Behavioral study does not need a notice. Before copying or substantially
adapting source from either project, follow [NOTICE.md](NOTICE.md) in the same change.

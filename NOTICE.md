# Third-party source notice and adaptation process

This repository is licensed **GPL-3.0-only**. At the time this notice was added, no
third-party source code had been copied into this repository. The projects below are
used as behavioral and architectural oracles only:

| Project | Role | Upstream license | Source material copied at this revision |
|---|---|---|---|
| [SeerrFin](https://github.com/varunaditya-plus/SeerrFin) | Discovery and request UX | MIT | No |
| [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced) | Browser lifecycle and integration boundaries | GPL-3.0 | No |

## Required process before adapting third-party source

In the same change that introduces copied or substantially adapted source:

1. Add an entry to the table above (or a dated section below) with the upstream project,
   immutable source revision/URL, original file path, license, copyright notice, and
   each affected local file.
2. Preserve upstream license and copyright notices in the affected source where
   required. For GPL-derived material, add a prominent modification notice with the
   date and local file path.
3. Include any upstream notice file required for redistribution and ensure its terms are
   compatible with this repository's GPL-3.0-only license.
4. State whether the work is a direct copy, a modification, or an independent
   implementation informed only by behavior. Do not claim code was copied when it was
   not.
5. Have the change reviewed for license compatibility before it is distributed.

Behavioral study, screenshots, API observations, and independently written code do not
by themselves require a copied-source entry. This notice is a review record, not legal
advice.

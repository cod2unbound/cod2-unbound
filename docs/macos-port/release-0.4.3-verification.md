# CoD2 Unbound 0.4.3 verification

Recorded on 22 November 2026, on the same Mac as 0.4.2: a Mac mini (Apple M6,
macOS 27, the macOS 27 SDK) with one 5K display. GitHub Actions were still
switched off, so the checks below were run on that Mac.

0.4.3 changes the launcher only. When GitHub does not answer, the launcher
asks cod2unbound.com for the release list, for an update's files and for the
news on Home (issue 92). No engine source file changed since 0.4.2.

**No installed copy has yet updated itself from cod2unbound.com.** 0.4.2 and
earlier know only GitHub, so the first update that can take the second road is
the one after this release.

## What was run

- **The engines are 0.4.2's.** No file under `src/` differs from 0.4.2.
- **The update check falls back, and only when it must.** In the launcher's
  tests the release list is asked of GitHub first. A time-out, a refused
  connection, a 404, a 451, a 500 and a reply that is not a release list each
  lead to one request to the website's list, whose offer is then accepted;
  when GitHub answers, the website is never asked; when both fail the check
  fails as before.
- **Only this project's addresses are accepted.** An offer is refused when
  its file is listed outside the releases folder, over http, at a host that
  only begins with the site's name, with a user name in the address, or with
  `..` in its path, plain or percent-encoded, on either host.
  A release's link is accepted from github.com and cod2unbound.com and from
  nowhere else.
- **Every check on an update is unchanged.** Over a local server: the
  checksum file and the archive are each retried once at the website when
  GitHub's copy cannot be fetched, never a third time; a file of the right
  size with the wrong bytes fails its checksum and is not fetched from the
  other place; a wrong checksum file and an archive larger than listed fail;
  a redirect to plain http is refused. The tests that an update must be this
  app, the offered version, validly signed and from the same developer pass
  as before.
- **News.** GitHub's file is used when it answers; a 404 there still means no
  news; any other failure tries the website's copy; a damaged file from either
  place keeps the news already shown.
- **The website's real list parses.** One read of
  `https://cod2unbound.com/update/releases.json` gave an offer for the newest
  release, with its files listed at `https://cod2unbound.com/releases/`. The
  three files of 0.4.2 downloaded through that address match that release's
  checksums.
- **The launcher's tests without windows**, at `0049359e`: 153 passes, no
  failure.
- **Captures of what changed, and only that.** Settings › Updates while it
  checks, and the Settings › Privacy scenes, in both appearances, at full
  size and at the smallest: 24 captures, a clean shape audit. The other
  screens draw nothing that changed, so the full suite was not run.
- **The package.** Built from `0049359e`, signed with the project's Developer
  ID and notarized. From the disk image: the checksums match, the app passes
  `codesign --verify --deep --strict`, Gatekeeper's assessment and the bundle
  audit, and reports version 0.4.3. `tests/packaging/release_smoke.py` started
  all six engines from it: both games with OpenGL and with Metal loaded
  Toujane and quit cleanly, and both dedicated servers answered.

## Known differences

- **Settings › Updates no longer names GitHub while it checks.** It says
  "Checking for updates…".
- **The update check does not follow a redirect.** If GitHub ever answers
  the check with one, the launcher asks the website instead.

## What was not

- **A real update taken from cod2unbound.com.** It needs an installed copy
  that knows the second address and a moment when GitHub does not answer. The
  parts were tested separately, as above.
- **The new line in Settings › Privacy in a picture.** The list of what the
  app contacts is below the fold of that pane, and the captures stop above it.
  The words are checked by reading; the row wraps like its neighbours.
- **Player Setups.** The catalogue is still read from GitHub alone (issue 93).

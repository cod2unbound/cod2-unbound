# CoD2 Unbound 0.4.2 verification

Recorded on 22 November 2026, on the same Mac as 0.4.1: a Mac mini (Apple M6,
macOS 27, the macOS 27 SDK) with one 5K display. GitHub Actions were still
switched off, so the checks below were run on that Mac.

0.4.2 changes the launcher only. It adds the question about usage data
(issue 44), an order on Player Setups for when there are figures to order
by, links that open a setup's page, and the fix for faint text on sheets
(issue 77). No engine source file changed since 0.4.1.

**No usage data has been sent by the app itself yet.** Every send below went
to a stand-in on this Mac. The first player who says yes in the published
app sends the first real event.

This is the 0.4.2 verification record. That release could replay setup steps from
before the player's answer after a yes. The current Mac launcher starts recording
with consent and discards the retired setup journal. See the
[launcher usage data notes](../../launcher/README.md#usage-data).

## What was run

- **The engines are 0.4.1's.** `python3 tests/fixes13/legacy.py --base main`
  reports 0 changed source files and 0 mismatches. The games and servers in
  the package were built again from the same source.
- **Nothing is sent without a yes.** `tests/launcher/usage_data.sh` runs the
  launcher's own sending code against a receiver on this Mac that records
  every request. It covers: no request before an answer, after a no, or in a
  build made without the usage key; a yes given in setup is held on the Mac
  until setup ends, then sent; turning usage data off stops sending and
  forgets the install number; Reset Number starts a new one; a failed send
  is retried with the same bytes and a stalled one is cancelled; what waits
  on the Mac is capped by count and by age, oldest removed first. Reset
  Settings keeps the answer, and settings saved by older versions still
  load.
- **What is sent is what the app says.** All 21 kinds of event were built
  from hostile inputs (names, addresses, paths and typed text in every field
  that could carry them) and checked against the list in See What's Sent:
  none of it reaches the request. The example shown in See What's Sent is
  made by the same code that makes the real request, and the test compares
  the two field for field.
- **Where it goes.** Before the release, events sent by hand to the service
  the app uses were read back: the network address is discarded, and of the
  place worked out from it only the country and continent are kept. City,
  region, coordinates and time zone came back empty.
- **Order on Player Setups.** The figures file is signed like the catalogue.
  The tests cover a good file, a bad signature, a missing file, an older
  file offered after a newer one, a reply from another address, a copy kept
  for offline use, and setups with too few counts to be ranked. With no figures file the Order menu is
  not shown and nothing on the page mentions figures. No figures are
  published yet, so that is what the released app shows.
- **Setup links.** `cod2unbound://setup/…` accepts one catalogue ID and
  nothing else; hostile links are refused. A link opens that setup's page
  whether the launcher was open or not, waits until setup has finished, and
  never imports anything by itself.
- **Sheets** (issue 77). No sheet sets its own background; the system draws
  it. Every text colour was measured on the system's sheet background and on
  the wells inside it: all pass 4.5:1 except the faintest text in Dark,
  which was 4.22:1 and is now 4.86:1.
- **The launcher's tests and the capture suite**, in one run at `518dafe2`,
  the commit the package was built from: every check passed, and the capture
  suite passed all 17 of its phases the first time. 454 screens (436 in
  0.4.1) in both appearances, at full and at minimum size and with the
  accessibility settings, their accessibility trees, the real clicks, and
  the shape audit with no finding. The new screens (the setup step, the
  question sheet, See What's Sent, Settings › Privacy with usage data off
  and on, and Player Setups in each order) were compared with their boards
  on the design canvas in both appearances.
- **The package.** `scripts/package-release.sh` built both games twice (for
  Metal and for OpenGL), both dedicated servers and the launcher from
  `518dafe2`, signed the app with a Developer ID and had Apple notarize it,
  then built the disk image, signed it and had Apple notarize that too; both
  tickets are stapled. The app in the ZIP and the app in the disk image are
  identical, file for file. It passes the checksum, the signature check,
  Gatekeeper's assessment and the bundle audit, reports version 0.4.2, and
  carries the crash-report address and the usage key.
  `tests/packaging/release_smoke.py` started all six engines from it: both
  games, with Metal and with OpenGL, reached the menu, loaded Toujane,
  played it and quit cleanly, and both servers answered a status query and
  stopped.
- **The website.** Its privacy notice describes usage data and went live
  before the release. The site's tests, type check and build pass.

## Known differences

- **In the question sheet, keyboard focus starts on See What's Sent…**, and
  its focus ring is a rectangle rather than the button's shape (issue 78).
  The crash-reports sheet does the same.
- **See What's Sent shows the real request**, with the service's own field
  names, where its board shows a simplified example.
- **The Order menu is absent** until figures are published.

## What was not

- **A real send**, as above, and the dashboards that read it.
- **The question sheet on a real update.** That it appears the first time
  0.4.2 opens for someone already set up is shown by the tests and the
  captures, not by updating an installed 0.4.1. Install Update from 0.4.1 to
  0.4.2 needs the published release.
- **The Order menu in the real app**, and the menu drawn open: the suite
  cannot capture a menu while it is open.
- **Clicking a `cod2unbound://` link in a browser.** That needs the
  installed app; the tests hand the link to the launcher directly.
- **See What's Sent on a small display.** The sheet is 796 points tall. Over
  the Settings window the capture shows only its top; over the main window
  it is complete. Whether it fits a 13-inch display set to larger text was
  not tried (issue 85).
- **How the system's sheet background reacts** to Increase Contrast and
  Reduce Transparency switched on in System Settings. The suite sets them
  for the app's own colours only.
- A whole match with Metal, voice with a real microphone, a Mac other than
  this one, and VoiceOver itself, as before.

Everything in [0.4.1's record](release-0.4.1-verification.md) that this
record does not replace still applies.

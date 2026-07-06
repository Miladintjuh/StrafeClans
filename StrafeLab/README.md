# StrafeLab

Full desktop build for CS2 counter-strafe, peek-practice, mouse-trace and summary-stat tracking.

This version follows the current public-release decisions:

- Full app enabled: local trainer, Peek mode, reports, conclusions, clans, admin/mod views, profiles and Supabase sync.
- Cloud-preferred, local-optional onboarding.
- Supabase URL and publishable key are bundled in the desktop app.
- No database password and no service-role key are shipped.
- Admin is manual database edit only: insert the promoted profile UUID into `public.admin_users`.
- Public release focus: Windows desktop packaging first, then visual polish, then expanded clan features.

## Current Supabase project

The desktop client uses only this public client configuration:

```text
Supabase URL: https://bacxpanhdtlwjfgaqufo.supabase.co
Publishable key: sb_publishable_u9Bl6EVZXYtmzqfS9e6ILg_4otgK_k2
```

The publishable key is expected to be visible in a client app. Security comes from Supabase Auth, RLS policies and RPC checks. Never add a Postgres connection string, service-role key or database password to this repo.

## Run from source

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

If no window opens, run the built executable directly:

```powershell
.\bin\Release\net9.0-windows\win-x64\StrafeLab.exe
```

If startup fails, check:

```text
%LOCALAPPDATA%\StrafeLab\startup-error.txt
```

## Package for Windows

Portable publish:

```powershell
.\publish-windows.ps1
```

Portable ZIP and optional Inno Setup installer:

```powershell
.\package-windows.ps1
```

If Inno Setup is installed and `iscc` is on PATH, the installer script also creates:

```text
publish\StrafeLab-Setup-2.0.0.exe
```

## Supabase database setup

Run once in the Supabase SQL editor:

```text
supabase/schema.sql
```

It creates:

- `profiles`
- `clans`
- `clan_members`
- `clan_invites`
- `stat_sessions`
- `admin_users`
- `mods`
- RLS policies
- RPC functions used by the app

## Manual admin setup

Admin is no longer automatic. After creating your account in StrafeLab, manually promote it in Supabase:

```sql
select id, username, created_at
from public.profiles
order by created_at asc;

insert into public.admin_users(user_id)
values ('PASTE_YOUR_PROFILE_UUID')
on conflict do nothing;
```

Then sign out and sign in again. The Admin button appears only when Supabase RPC confirms the signed-in user is in `public.admin_users` or `public.mods`.

## User flow

1. Open StrafeLab.
2. Create a cloud account, sign in, use anonymous sharing, or choose local-only.
3. Start a Normal or Peek session.
4. Stop and save.
5. Review attempts, replay, mouse traces and conclusions.
6. If signed in, summary stats upload automatically.
7. Use Clans to create teams, invite players, compare stats and get team coaching.

## Privacy model

- Raw keyboard events stay local.
- Raw mouse movement stays local.
- Mouse traces stay local.
- Screenshots and CSV exports stay local.
- Only saved summary stats upload when a user is signed in or anonymous sharing is enabled.
- Local-only mode uses no cloud account and no cloud sync.

## Release checklist

See `RELEASE_CHECKLIST.md` before publishing a public build.


## v2.0.1 clan invite fix

- Clan invites now use a tolerant server-side username resolver.
- The invite RPC accepts copied usernames with @, whitespace, invisible clipboard characters, or missing punctuation such as `miladinc7e53b` for `miladin.c7e53b` when the compact match is unique.
- If the invite target cannot be found, the error now includes the normalized username and possible suggestions.
- Re-run `supabase/schema.sql` in Supabase after updating so the fixed `public.invite_to_clan` RPC replaces the old one.

## v2.1.0 UI rebuild pass

This build applies the first user-friendly layout pass based on the new product decisions:

- Full app remains enabled.
- Cloud remains preferred, with local-only still available.
- Reports are removed from the main header; Conclusions becomes the primary review surface.
- Header is mode-aware and status-aware: Start is visible only while idle, Stop is visible only while recording, and Arm rep appears only in Peek mode.
- Added a guided fake-data demo from the first-run screen so users can learn the table, mouse trace, replay, and conclusions flow before recording.
- Main gameplay screen now adapts between side-by-side and stacked layout based on rendered app size.
- Login/auth errors are translated into cleaner user-facing messages.
- Clan invite acceptance now asks whether to sync existing summaries or start a fresh clan season with a saved pre-clan snapshot.



## v2.1.2 build fix

- Fixed `ClanView.xaml.cs` multiline MessageBox string that caused CS1010/CS1003 build errors in Release builds.


## v2.2.3 guided demo rebuild

- Replaced the separate fake demo screen with an in-app guided tour over the real Settings, main trainer, attempt table, mouse trace, replay, and Conclusions screens.
- The sensitivity step now opens the real editable Settings calibration card so DPI/sensitivity can be entered and saved.
- The demo waits for 20 real click-confirmed attempts before continuing to table/trace/replay/conclusions guidance.
- The selected attempt key replay is highlighted as an actual app panel, with the user guided to try 5x slow replay.
- Finishing or exiting the tour clears the demo marker and shows a final friendly completion overlay.

## v2.2.4 real-session guided demo data

The guided demo now uses bundled real manual-test data instead of generated fake rows. The package includes `DemoData/attempts.csv`, `DemoData/mouse_traces.csv`, `DemoData/events.csv`, and `DemoData/summary.json`. When the demo reaches the trainer screen, StrafeLab loads those files into the normal analyzer/table/trace/replay/conclusions UI so the highlighted tour explains the actual app with realistic examples.


## v2.2.5 UX and capture stability polish

- Guided demo bubble now uses a warm trainer-style color scheme and moves away from the highlighted UI region instead of behaving like a normal app card.
- Demo completion clears the bundled demo session and returns the trainer to Idle after the OK button.
- Current coaching is reduced to one live tip based on the last five click-confirmed attempts; the old focus dropdown is hidden.
- Mouse trace overlay behavior changed: one selected attempt shows all traces with the selected trace on top; multiple selected attempts show only those selected traces for comparison.
- Mistake wording is clearer: Late counter, Late click, Early click, Moving shot, Overlap, No shot.
- Added capture tuning controls for counter pair window, normal trace duration, Peek trace duration, Peek trace point cap, and stop-after-click timeout.
- Peek mouse recording is capped by duration and point count to reduce lag/crash risk when reset movement is not detected.


## v2.2.9 UI and Peek rep fixes

- Settings hides the Show me the demo button while the guided demo is already running.
- Dashboard coaching no longer shows a duplicate Conclusions button; the header Conclusions button is the only route.
- Selected attempt key replay is now placed above the mouse trace overlay on the main trainer screen.
- Switching to Account, Clans, Settings, Conclusions, or Peek mode while a session is recording asks before ending/switching, with a remember option.
- Peek mode removed duplicate in-page Back to trainer and Conclusions buttons; use the header Gameplay mode/Conclusions controls.
- Arm rep now records a single temporary Peek attempt without starting a continuous session, shows a clear result overlay, and W/S or F8 re-arms the next rep.


## v2.2.9 UI/demo viewport patch

- Guided demo now dims non-target UI areas while keeping the explanation bubble and highlighted section readable.
- Main trainer content is inside a vertical scroll area so smaller windows can still reach the replay and mouse trace sections during the demo.
- Responsive stacked layout caps the attempts table height and keeps replay/mouse panels accessible below it.
- Demo mouse-trace step now highlights the real mouse trace panel instead of the whole right-side review card.


## v2.2.10 guided demo focus patch

- Removed the extra top-level demo highlight rectangle from v2.2.9.
- Restored the original 3px highlight directly on the focused section.
- Added sibling dimming inside Settings/Conclusions so non-focused cards/tabs are visually de-emphasized like the main trainer screen.


## v2.2.13 first-run demo dimming fix

- First-run guided demo now reapplies focus/dimming after Settings and Conclusions finish layout.
- Settings and Conclusions dim their non-focused sections consistently with the trainer demo steps.

## v2.2.13 guided demo dimming consistency

- Replaced per-view opacity dimming with a shared spotlight overlay for every guided demo step.
- Settings, trainer, and Conclusions now use the same strong dimming level while leaving the focused section clear with the normal 3px highlight border.
- Keeps demo copy, step order, and navigation unchanged.


## v2.2.16 replay and demo usability patch

- Demo trace-selection step now dims non-trace attempts so the user is guided toward selectable rows with mouse movement.
- Settings hotkeys now use a record-a-key button instead of dropdown selection.
- Peek Live button stays disabled until a continuous Peek session has been started.
- Selected attempt replay now supports a custom slow factor, draggable playhead scrubbing, and elapsed-time markers on the timeline.
- Replay key preview labels now show key/click state only, while the status line uses the same mistake label as the attempts table.


## v2.2.16 rankings and local hotkeys

- Header Clans button is now Rankings.
- Rankings page has Personal and Clans sections.
- Personal rankings list only manually public profiles and includes a username search.
- Public profile leaderboard is backed by `public.public_profile_rankings`. Re-run `supabase/schema.sql` after updating.
- Hotkeys are now app-local instead of Windows global hotkeys, so typing in text fields is no longer blocked by Start/Peek/Remove keys.


## v2.2.17 layout refinement

- The selected attempt key replay and mouse trace overlay are now separate cards instead of subsections inside one larger card.
- The selected attempt key replay sits above the mouse trace overlay in the right-hand review column.
- The mouse trace card has a lower minimum height so the right column uses space more efficiently on 1080p and 2K displays.

## v2.2.18 demo table guidance

- Attempts table now includes a permanent Trace column so users can see which attempts have mouse path data.
- During the guided demo trace-selection step, the table switches to five curated attempts with trace rows listed first.
- Non-trace demo rows remain clickable for key replay inspection but stay dimmed; selecting a trace row automatically advances to the mouse trace step.
- Action-based demo steps now auto-advance when the required action is completed, such as selecting a trace attempt or clicking 5x slow.
- Demo dimming now uses section opacity only, avoiding geometry overlay mismatch when window size changes.


## v2.2.19

- Added Show last mode: listens for attempts and displays only the latest attempt replay, mouse trace, details, and solution overlay.
- Reduced Raw Input mouse-move load while not actively listening or recording.
- Reset internal held-key state on focus changes to recover from lost key-up events when switching between StrafeLab and CS2.


## v2.2.20 replay timing refinement

- Added five evenly spaced millisecond labels to the selected-attempt replay timeline and Show Last replay.
- Added M1 release tracking so the M1 bar ends at the real mouse-up event when available.
- While waiting for mouse-up, M1 shows only a short provisional pulse instead of a long fixed bar.
- Session exports now include `click_up_ms` for future replay accuracy.


## v2.2.21 Show Last null-state crash fix

- Fixed a crash in Show Last when the view updated during the no-attempt / partial-input state between input edges.
- The empty Show Last view now resets A, D, and M1 key visuals safely instead of reading fields from a null attempt.


## v2.2.22 fixed trainer layout

- Window/header title includes build version.
- Escape clears the selected attempt row(s).
- Main trainer uses a fixed proportional two-column layout: coaching/table on the left, replay/trace on the right. Sections scale inside the window instead of moving between columns during resize.


## v2.2.23 attempt table readability

- Simplified recent-attempt table labels for smaller windows. Removed visible Use checkbox column, shortened direction to arrows, renamed timing columns to Key wait/Click wait, added check/cross trace values, shortened moving-shot result to Moving, and made What happened use concise explanation text.


## v2.2.25 keyboard navigation

- Up/Down arrow keys move to the previous/next visible attempt when an attempt is selected and StrafeLab is focused.
- Text entry is not intercepted, so arrow keys still work normally inside text fields.


## v2.2.28 replay and total time

- New click-confirmed attempts remain selected after follow-up input edges.
- Replay time labels are attempt-relative instead of absolute session timestamps.
- Attempts table includes Total time, calculated from original key release to M1.
- Conclusions include total-time average/spread and total-time consistency in tracked metrics.


## v2.2.28 selection stability

- Fixed automatic newest-attempt selection being cleared by follow-up key-up or M1-up events.
- Replaced remove/reinsert row refresh with property-change notification so selected attempt identity is preserved.

## v2.2.28 guided demo and backend roles

- The guided demo no longer skips immediately after pressing 5x slow. It waits for the 5x replay to finish, then shows a replay-controls step with Back/Next.
- Added a guided demo step for the Conclusions Mistakes tab.
- Supabase role access now supports backend selection through `public.profiles.app_role` with values `user`, `moderator`, or `admin`.
- Existing `public.admin_users` and `public.mods` rows are still honored for backwards compatibility, but new installs should set `app_role` in Supabase.
- Authenticated desktop clients are not granted update access to `profiles.app_role`; change roles from Supabase using owner/service-role access.

## v2.2.29 what-happened timing labels
- Added clean-attempt timing buckets for the What happened column: fast clean, perfect clean, controlled clean, and just in time clean.
- Added editable timing thresholds in Settings > Timing judgment for clean fast max, clean perfect max, and just-in-time min.
- Moving shots are now split into shot too soon when M1 happens before the counter key and shot too late when M1 happens after the counter key while movement is still held.
- Show Last uses the same concise What happened wording as the main attempts table.

## v2.2.31 Show Last replay parity
- Show Last replay timeline now uses attempt-relative ms labels instead of absolute session timestamps.
- Show Last attempt-details text explicitly uses the app text brush so it is white on first open.
- Show Last now includes replay controls for 1x, 5x slow, 10x slow, custom slow factor, and pause.

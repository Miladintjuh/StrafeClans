# StrafeLab

Local Windows input analyzer for counter-strafe and 5e_aimhub Peek mode practice.

This build uses an explicit WPF startup path instead of StartupUri, so the main window is shown directly and startup errors are reported instead of silently exiting.

## Run

```powershell
Remove-Item -Recurse -Force .\bin, .\obj
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

If no window opens, run the built executable directly:

```powershell
.\bin\Release\net9.0-windows\StrafeLab.exe
```

If a startup error occurs, check:

```text
%LOCALAPPDATA%\StrafeLab\startup-error.txt
```


## Startup fix 2

This package fixes the missing `BackgroundBrush` XAML resource in the one-window mode host.

If you previously saw `Cannot find resource named BackgroundBrush`, delete `bin` and `obj`, restore, build, and run again.

## UI cleanup / moving-shot model

This build adds the requested result model:

- Attempts can now have multiple mistakes, for example `Overlap, Moving`.
- Each attempt has one result, for example `Accurate`, `Moving`, or `Inaccurate`.
- If A or D is still held at the M1 click, the attempt is categorized as `Moving` and logged as a moving-at-shot result.
- Attempts CSV now includes `mistakes`, `result`, `moving_at_click`, and `held_keys_at_click`.
- The recent attempts table uses shorter wording and shows `Click delay`, `Mistakes`, `Result`, and concise notes.
- Key replay now uses a `Pause` button and highlights moving shots.
- Mouse trace overlay uses wheel zoom at cursor position, drag-to-pan, and an in-graph reset button.

## Settings / reports / one-window conclusions

This build adds:

- Header settings button (`⚙`) with column visibility, color profiles, hotkey choices, and display defaults.
- Header `Reports` button with a drag-and-drop report builder.
- Embedded conclusions view inside the main StrafeLab window instead of opening a separate window.
- State-aware session controls: only `Start` is visible when inactive; only `Stop + Save` is visible while recording.

Preferences are saved locally to:

```text
%LOCALAPPDATA%\StrafeLab\preferences.json
```

## Current patch

This build adds:

- Application icon.
- Delete-key removal for selected normal attempts and selected Peek attempts.
- Open data folder moved to Settings only.
- Fully customizable colors in Settings with temporary Test and saved Apply workflow.
- Peek timing thresholds moved out of the Peek screen and into Settings.
- Peek mode uses the global Start/Stop button and global Conclusions button.
- Peek mode button changes to Back to main while Peek mode is active.
- Peek graph uses edge arrows for extreme outliers so one bad rep no longer flattens the whole chart.
- Peek table scales down on smaller screens to remain readable on 1080p.

## Supabase login and clans

This build includes optional Supabase sync for accounts, clans, and aggregated clan statistics.

### Important security note

The desktop app uses the Supabase project URL and publishable key only. Do not place your Postgres connection string in the app. If you shared a Postgres connection string anywhere public, rotate the database password in Supabase before distributing the app.

### Database setup

Before using Accounts/Clans, run this SQL file once in the Supabase SQL editor:

```text
supabase/schema.sql
```

It creates:

- `profiles`
- `clans`
- `clan_members`
- `clan_invites`
- `stat_sessions`
- RLS policies
- RPC functions used by the app

### App flow

1. Click **Login** in the header.
2. Create an account with email, password, and username.
3. If email verification is enabled in Supabase, verify email and sign in.
4. Click **Clans**.
5. Create a clan or accept an invite.
6. Save local StrafeLab sessions. When signed in, session summaries upload automatically.
7. In **Clans**, click **Upload local summaries** to backfill previous sessions.

Raw local key/mouse CSV files are not uploaded by this integration. Only session summaries are sent to Supabase.

## Patch notes: profile/clan + theme polish

This build adds an App background color separate from the page background, tightens the Settings layout, anchors color pickers next to the clicked color button, and auto-creates the Supabase profile row for existing authenticated users who signed in before a profile row existed. Clan creation now preserves the real error instead of immediately overwriting it with a refresh message.

If the Supabase table editor still shows zero rows after opening Profile or creating a clan, sign out and sign in again so the app can create the missing profile row with the current auth session.

## Supabase RPC/RLS fix

If clan selection shows `structure of query does not match function result type`, rerun `supabase/schema.sql` in the Supabase SQL editor. The schema now drops/recreates all table-returning RPCs and casts every returned column to the exact declared type. It also removes stale policies before recreating non-recursive RLS policies.

After login, StrafeLab now redirects directly to the Profile page instead of leaving the login form visible.

## Anonymous stats sharing + mode switching guard

This build adds an account-free sharing option on the Account page:

```text
Share non-personal information (hits, misses, timings) to improve app development
```

This creates an anonymous Supabase Auth user with a private profile and uploads saved session summaries only. Raw key events, raw mouse movement, mouse traces, screenshots, and CSV files stay local.

Enable Anonymous sign-ins in Supabase Auth settings before using this option.

The updated schema also adds:

- `profiles.is_anonymous`
- `profiles.share_development_stats`
- `admin_users`
- admin RPCs for aggregate stats

To make your own account an admin, insert your profile ID into `public.admin_users` in Supabase.

Mode switching is now protected. If a session is running and the user switches between the main trainer and Peek mode, StrafeLab asks whether to end the current session first and includes a "Remember choice" option. This prevents both modes from recording at the same time.


## Local-only opt-out

The first-run account screen includes a full opt-out button. It clears any stored cloud session locally and does not persist an opt-out flag, so the app starts with the first-run choice screen again on the next launch.

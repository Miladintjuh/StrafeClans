# StrafeLab public release checklist

## Build and packaging

- Run `./publish-windows.ps1` on Windows.
- Launch `publish\StrafeLab-win-x64\StrafeLab.exe`.
- Run `./package-windows.ps1` to create the portable ZIP.
- If Inno Setup is installed, confirm `publish\StrafeLab-Setup-2.0.0.exe` is created.
- Install on a clean Windows user account.

## Required Supabase setup

- Run `supabase/schema.sql` in the project SQL editor.
- Create/sign in to your first account in StrafeLab.
- Manually promote the admin account:

```sql
select id, username, created_at from public.profiles order by created_at asc;
insert into public.admin_users(user_id)
values ('PASTE_YOUR_PROFILE_UUID')
on conflict do nothing;
```

- Sign out and sign in again.
- Confirm the Admin button appears only after the manual DB promotion.

## Public-key safety check

- Confirm no service-role key or database password exists in the repo.
- Confirm only the publishable key is bundled.
- Confirm RLS is enabled on `profiles`, `clans`, `clan_members`, `clan_invites`, `stat_sessions`, `admin_users`, and `mods`.

## Smoke test

- Registered account can sign in.
- Local-only mode opens the app.
- Normal mode records and saves a session.
- Peek mode records and saves a session.
- Saved summary uploads while signed in.
- Clan can be created.
- Invite can be sent and accepted.
- Conclusions opens after a saved session.

## v2.1.0 UI rebuild checks

- [ ] Verify first-run screen shows Online account, Anonymous sharing, Local-only, and Guided demo.
- [ ] Verify email-not-confirmed and invalid-login errors show clean messages.
- [ ] Verify header only shows Start while idle and Stop while recording.
- [ ] Verify Arm rep is only visible in Peek mode and disabled during continuous Peek recording.
- [ ] Verify Reports is not visible in the main header.
- [ ] Verify main screen stacks attempts and review panels when the window is narrow/short.
- [ ] Verify Guided demo fake attempts select rows and redraw trace/replay text.
- [ ] Verify accepting a clan invite asks whether to sync old summaries or start a fresh clan season.



## v2.1.2 build fix

- Verify `dotnet build -c Release` passes after the clan stat-sync MessageBox string fix.

## v2.2.4 demo data check

- Confirm `DemoData` files are copied to output.
- Start the guided demo from a fresh profile.
- Settings step should allow editing/saving calibration.
- Trainer step should show the real bundled session with 22 attempts.
- Select #28, #31, or #36 to see mouse trace points.
- 5x slow replay step should require pressing the 5x slow button.
- Conclusions tabs should use the bundled real session.

## v2.2.28 checks

- [ ] Guided demo trace selection still auto-advances after selecting a traced attempt.
- [ ] Guided demo 5x replay waits until playback finishes, then shows the replay-controls tip with Back and Next.
- [ ] Guided demo includes the Conclusions Mistakes tab before Mouse / aim.
- [ ] In Supabase, set `public.profiles.app_role` to `admin` or `moderator` and verify the Admin button appears after sign-in/refresh.
- [ ] Verify a normal user cannot update `profiles.app_role` through authenticated client access.

## v2.2.29 checks
- Verify clean attempts show fast/perfect/controlled/just-in-time labels in What happened.
- Verify move-click-counter displays shot too soon.
- Verify move-hold-counter-click displays shot too late.
- Verify Settings > Timing judgment saves the clean timing bucket thresholds.

## v2.2.31 checks
- Verify Show Last timeline labels read as attempt-relative values such as 0 ms, 50 ms, 100 ms.
- Verify Show Last detail labels are white on first open after launch.
- Verify Show Last replay controls can play and pause the latest attempt.

## v2.2.31 checks

- [ ] Start a session, record attempts, stop/save, then start another session; earlier rows should remain in the table.
- [ ] Confirm a padded separator appears between attempts from different live sessions.
- [ ] Confirm the attempts table vertical scrollbar stays stable instead of flashing on/off during recording.
- [ ] Confirm the horizontal scrollbar is no longer shown in the attempts table.

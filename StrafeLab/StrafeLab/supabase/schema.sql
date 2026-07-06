-- StrafeLab Supabase schema
-- Run this once in the Supabase SQL editor for your project.
-- The desktop app uses the publishable key only. Do not put your Postgres connection string in the app.

create extension if not exists pgcrypto;

create table if not exists public.profiles (
    id uuid primary key references auth.users(id) on delete cascade,
    username text not null unique,
    display_name text not null,
    profile_public boolean not null default false,
    privacy_choice_made boolean not null default false,
    is_anonymous boolean not null default false,
    share_development_stats boolean not null default false,
    app_role text not null default 'user' check (app_role in ('user', 'moderator', 'admin')),
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    constraint profiles_username_format check (username ~ '^[a-z0-9_.]{3,32}$')
);

create table if not exists public.clans (
    id uuid primary key default gen_random_uuid(),
    name text not null,
    owner_id uuid not null references public.profiles(id) on delete cascade,
    created_at timestamptz not null default now()
);

create table if not exists public.clan_members (
    clan_id uuid not null references public.clans(id) on delete cascade,
    user_id uuid not null references public.profiles(id) on delete cascade,
    role text not null default 'member' check (role in ('owner', 'member')),
    joined_at timestamptz not null default now(),
    primary key (clan_id, user_id)
);

create table if not exists public.clan_invites (
    id uuid primary key default gen_random_uuid(),
    clan_id uuid not null references public.clans(id) on delete cascade,
    invited_user_id uuid not null references public.profiles(id) on delete cascade,
    invited_by uuid not null references public.profiles(id) on delete cascade,
    status text not null default 'pending' check (status in ('pending', 'accepted', 'declined')),
    created_at timestamptz not null default now(),
    responded_at timestamptz
);

create unique index if not exists clan_invites_pending_unique
    on public.clan_invites(clan_id, invited_user_id)
    where status = 'pending';

-- Legacy manual admin list. Preferred going forward: set public.profiles.app_role to 'admin' or 'moderator' in Supabase.
create table if not exists public.admin_users (
    user_id uuid primary key references public.profiles(id) on delete cascade,
    created_at timestamptz not null default now()
);

create table if not exists public.mods (
    user_id uuid primary key references public.profiles(id) on delete cascade,
    created_by uuid references public.profiles(id) on delete set null,
    created_at timestamptz not null default now()
);

create table if not exists public.stat_sessions (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references public.profiles(id) on delete cascade,
    session_id text not null,
    started_at timestamptz not null,
    ended_at timestamptz not null,
    mode text not null default 'trainer',
    attempts int not null default 0,
    clean int not null default 0,
    moving int not null default 0,
    overlap int not null default 0,
    slow int not null default 0,
    early int not null default 0,
    avg_counter_delay_ms double precision not null default 0,
    avg_click_delay_ms double precision not null default 0,
    raw jsonb not null default '{}'::jsonb,
    created_at timestamptz not null default now(),
    unique (user_id, session_id)
);

alter table public.profiles add column if not exists profile_public boolean not null default false;
alter table public.profiles add column if not exists privacy_choice_made boolean not null default false;
alter table public.profiles add column if not exists is_anonymous boolean not null default false;
alter table public.profiles add column if not exists share_development_stats boolean not null default false;
alter table public.profiles add column if not exists app_role text not null default 'user';

do $$
begin
    if not exists (
        select 1
          from pg_constraint
         where conname = 'profiles_app_role_check'
           and conrelid = 'public.profiles'::regclass
    ) then
        alter table public.profiles
            add constraint profiles_app_role_check check (app_role in ('user', 'moderator', 'admin'));
    end if;
end $$;

alter table public.profiles enable row level security;
alter table public.clans enable row level security;
alter table public.clan_members enable row level security;
alter table public.clan_invites enable row level security;
alter table public.stat_sessions enable row level security;
alter table public.admin_users enable row level security;
alter table public.mods enable row level security;

-- Clean up existing policies so older recursive policies cannot survive reruns.
do $$
declare
    p record;
begin
    for p in
        select schemaname, tablename, policyname
          from pg_policies
         where schemaname = 'public'
           and tablename in ('profiles', 'clans', 'clan_members', 'clan_invites', 'stat_sessions', 'admin_users', 'mods')
    loop
        execute format('drop policy if exists %I on %I.%I', p.policyname, p.schemaname, p.tablename);
    end loop;
end $$;

-- RLS helper functions. SECURITY DEFINER avoids recursive clan_members policies.
create or replace function public.is_clan_member(p_clan_id uuid, p_user_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select exists (
        select 1
          from public.clan_members cm
         where cm.clan_id = p_clan_id
           and cm.user_id = p_user_id
    );
$$;

create or replace function public.is_clan_owner(p_clan_id uuid, p_user_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select exists (
        select 1
          from public.clan_members cm
         where cm.clan_id = p_clan_id
           and cm.user_id = p_user_id
           and cm.role = 'owner'
    );
$$;

create or replace function public.shares_clan(p_target_user_id uuid, p_viewer_user_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select exists (
        select 1
          from public.clan_members me
          join public.clan_members them on them.clan_id = me.clan_id
         where me.user_id = p_viewer_user_id
           and them.user_id = p_target_user_id
    );
$$;

create or replace function public.is_admin(p_user_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    -- Backend-managed access. Preferred: set public.profiles.app_role = 'admin' in Supabase.
    -- Existing public.admin_users rows are still honored for backwards compatibility.
    select exists (select 1 from public.profiles p where p.id = p_user_id and p.app_role = 'admin')
        or exists (select 1 from public.admin_users a where a.user_id = p_user_id);
$$;

create or replace function public.is_mod(p_user_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    -- Backend-managed access. Preferred: set public.profiles.app_role = 'moderator' in Supabase.
    -- Existing public.mods rows are still honored for backwards compatibility.
    select exists (select 1 from public.profiles p where p.id = p_user_id and p.app_role = 'moderator')
        or exists (select 1 from public.mods m where m.user_id = p_user_id);
$$;

create or replace function public.can_view_admin(p_user_id uuid)
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select coalesce(public.is_admin(p_user_id), false) or coalesce(public.is_mod(p_user_id), false);
$$;

create or replace function public.current_user_is_admin()
returns boolean
language sql
security definer
stable
set search_path = public
as $$
    select coalesce(public.is_admin(auth.uid()), false);
$$;

create or replace function public.current_user_admin_access()
returns table(
    is_admin boolean,
    is_mod boolean,
    can_view_admin boolean,
    mod_count integer
)
language sql
security definer
stable
set search_path = public
as $$
    select public.is_admin(auth.uid())::boolean as is_admin,
           public.is_mod(auth.uid())::boolean as is_mod,
           public.can_view_admin(auth.uid())::boolean as can_view_admin,
           (select count(distinct user_id)::integer
              from (
                    select m.user_id from public.mods m
                    union
                    select p.id as user_id from public.profiles p where p.app_role = 'moderator'
                   ) role_sources
           ) as mod_count;
$$;

-- Grants used by PostgREST/RPC calls from the desktop client.
grant usage on schema public to authenticated;
revoke update on public.profiles from authenticated;
grant select, insert on public.profiles to authenticated;
grant update (username, display_name, profile_public, privacy_choice_made, is_anonymous, share_development_stats, updated_at) on public.profiles to authenticated;
-- Do not grant authenticated clients update access to profiles.app_role. Change app_role in Supabase with owner/service-role privileges.
grant select, insert, update on public.clans to authenticated;
grant select, insert, update on public.clan_members to authenticated;
grant select, insert, update on public.clan_invites to authenticated;
grant select, insert, update on public.stat_sessions to authenticated;
grant select on public.admin_users to authenticated;
grant select, insert, delete on public.mods to authenticated;

-- Policies.
create policy profiles_select_authenticated on public.profiles for select to authenticated using (
    id = auth.uid()
    or profile_public = true
    or public.shares_clan(id, auth.uid())
    or public.is_admin(auth.uid())
);
create policy profiles_insert_self on public.profiles for insert to authenticated with check (id = auth.uid() and app_role = 'user');
create policy profiles_update_self on public.profiles for update to authenticated using (id = auth.uid()) with check (id = auth.uid());

create policy clans_select_members on public.clans for select to authenticated using (
    public.is_clan_member(id, auth.uid())
);

create policy clan_members_select_clan_members on public.clan_members for select to authenticated using (
    public.is_clan_member(clan_id, auth.uid())
);

create policy clan_invites_select_relevant on public.clan_invites for select to authenticated using (
    invited_user_id = auth.uid()
    or public.is_clan_owner(clan_id, auth.uid())
);

create policy stat_sessions_select_self_or_clan_or_admin on public.stat_sessions for select to authenticated using (
    user_id = auth.uid()
    or public.shares_clan(user_id, auth.uid())
    or public.is_admin(auth.uid())
);
create policy admin_users_select_self_admin on public.admin_users for select to authenticated using (public.is_admin(auth.uid()));
create policy mods_select_admin on public.mods for select to authenticated using (public.is_admin(auth.uid()));
create policy mods_insert_admin on public.mods for insert to authenticated with check (public.is_admin(auth.uid()));
create policy mods_delete_admin on public.mods for delete to authenticated using (public.is_admin(auth.uid()));
create policy stat_sessions_insert_self on public.stat_sessions for insert to authenticated with check (user_id = auth.uid());
create policy stat_sessions_update_self on public.stat_sessions for update to authenticated using (user_id = auth.uid()) with check (user_id = auth.uid());

-- RPCs.
create or replace function public.create_clan(p_name text)
returns uuid
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_clan_id uuid;
begin
    if v_uid is null then
        raise exception 'not authenticated';
    end if;
    if length(trim(p_name)) < 2 then
        raise exception 'clan name is too short';
    end if;
    if not exists (select 1 from public.profiles p where p.id = v_uid) then
        raise exception 'create your profile first';
    end if;

    insert into public.clans(name, owner_id) values (trim(p_name), v_uid) returning id into v_clan_id;
    insert into public.clan_members(clan_id, user_id, role) values (v_clan_id, v_uid, 'owner');
    return v_clan_id;
end;
$$;

create or replace function public.invite_to_clan(p_clan_id uuid, p_username text)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_invited uuid;
    v_username text;
    v_compact text;
    v_match_count integer;
    v_suggestions text;
begin
    if v_uid is null then
        raise exception 'not authenticated';
    end if;
    if not exists (select 1 from public.clan_members cm where cm.clan_id = p_clan_id and cm.user_id = v_uid and cm.role = 'owner') then
        raise exception 'only the clan owner can invite players';
    end if;

    -- Robust invite lookup:
    -- 1. Accept usernames copied with @, whitespace, or invisible clipboard characters.
    -- 2. Prefer exact username/display_name match.
    -- 3. Fallback to punctuation-insensitive match so "miladinc7e53b" can find "miladin.c7e53b".
    v_username := lower(trim(both from coalesce(p_username, '')));
    v_username := regexp_replace(v_username, '^@+', '');
    v_username := regexp_replace(v_username, '[[:space:]]+', '', 'g');
    v_compact := regexp_replace(v_username, '[^a-z0-9]', '', 'g');

    if length(v_username) < 3 then
        raise exception 'username is too short';
    end if;

    select p.id into v_invited
      from public.profiles p
     where lower(p.username) = v_username
        or lower(coalesce(p.display_name, '')) = v_username
     order by p.created_at asc
     limit 1;

    if v_invited is null and length(v_compact) >= 3 then
        select count(*), min(p.id) into v_match_count, v_invited
          from public.profiles p
         where regexp_replace(lower(p.username), '[^a-z0-9]', '', 'g') = v_compact
            or regexp_replace(lower(coalesce(p.display_name, '')), '[^a-z0-9]', '', 'g') = v_compact;

        if coalesce(v_match_count, 0) > 1 then
            select string_agg('@' || p.username, ', ' order by p.username)
              into v_suggestions
              from public.profiles p
             where regexp_replace(lower(p.username), '[^a-z0-9]', '', 'g') = v_compact
                or regexp_replace(lower(coalesce(p.display_name, '')), '[^a-z0-9]', '', 'g') = v_compact;
            raise exception 'multiple username matches: %', coalesce(v_suggestions, v_username);
        end if;
    end if;

    if v_invited is null then
        select string_agg('@' || p.username, ', ' order by p.username)
          into v_suggestions
          from (
              select p.username
                from public.profiles p
               where lower(p.username) like '%' || left(v_username, greatest(3, least(8, length(v_username)))) || '%'
                  or lower(coalesce(p.display_name, '')) like '%' || left(v_username, greatest(3, least(8, length(v_username)))) || '%'
               order by p.username
               limit 5
          ) p;
        raise exception 'username not found: %.%', v_username, case when v_suggestions is null then '' else ' Did you mean ' || v_suggestions || '?' end;
    end if;

    if v_invited = v_uid then
        raise exception 'you cannot invite yourself';
    end if;
    if exists (select 1 from public.clan_members cm where cm.clan_id = p_clan_id and cm.user_id = v_invited) then
        raise exception 'player is already in this clan';
    end if;
    if exists (select 1 from public.clan_invites ci where ci.clan_id = p_clan_id and ci.invited_user_id = v_invited and ci.status = 'pending') then
        return;
    end if;

    insert into public.clan_invites(clan_id, invited_user_id, invited_by) values (p_clan_id, v_invited, v_uid);
end;
$$;

create or replace function public.respond_clan_invite(p_invite_id uuid, p_accept boolean)
returns void
language plpgsql
security definer
set search_path = public
as $$
declare
    v_uid uuid := auth.uid();
    v_clan_id uuid;
begin
    if v_uid is null then
        raise exception 'not authenticated';
    end if;

    select ci.clan_id into v_clan_id
      from public.clan_invites ci
     where ci.id = p_invite_id
       and ci.invited_user_id = v_uid
       and ci.status = 'pending';

    if v_clan_id is null then
        raise exception 'pending invite not found';
    end if;

    update public.clan_invites ci
       set status = case when p_accept then 'accepted' else 'declined' end,
           responded_at = now()
     where ci.id = p_invite_id;

    if p_accept then
        insert into public.clan_members(clan_id, user_id, role)
        values (v_clan_id, v_uid, 'member')
        on conflict (clan_id, user_id) do nothing;
    end if;
end;
$$;

-- Drop table-returning functions before recreating so Supabase cannot keep an old incompatible result signature.
drop function if exists public.my_clans();
drop function if exists public.my_pending_invites();
drop function if exists public.clan_dashboard(uuid);
drop function if exists public.my_profile_stats();
drop function if exists public.public_profile_stats(text);
drop function if exists public.public_profile_rankings(text);
drop function if exists public.admin_global_stats();
drop function if exists public.admin_player_stats();
drop function if exists public.moderator_count();
drop function if exists public.list_moderators();
drop function if exists public.search_moderator_candidates(text);
drop function if exists public.add_moderator(uuid);
drop function if exists public.remove_moderator(uuid);

create function public.my_clans()
returns table(
    clan_id uuid,
    name text,
    role text,
    owner_username text,
    members integer,
    created_at timestamptz
)
language sql
security definer
set search_path = public
as $$
    select c.id::uuid as clan_id,
           c.name::text as name,
           m.role::text as role,
           owner.username::text as owner_username,
           (select count(*)::integer from public.clan_members cm where cm.clan_id = c.id) as members,
           c.created_at::timestamptz as created_at
      from public.clan_members m
      join public.clans c on c.id = m.clan_id
      join public.profiles owner on owner.id = c.owner_id
     where m.user_id = auth.uid()
     order by c.created_at desc;
$$;

create function public.my_pending_invites()
returns table(
    invite_id uuid,
    clan_id uuid,
    clan_name text,
    invited_by_username text,
    created_at timestamptz
)
language sql
security definer
set search_path = public
as $$
    select i.id::uuid as invite_id,
           i.clan_id::uuid as clan_id,
           c.name::text as clan_name,
           p.username::text as invited_by_username,
           i.created_at::timestamptz as created_at
      from public.clan_invites i
      join public.clans c on c.id = i.clan_id
      join public.profiles p on p.id = i.invited_by
     where i.invited_user_id = auth.uid()
       and i.status = 'pending'
     order by i.created_at desc;
$$;

create function public.clan_dashboard(p_clan_id uuid)
returns table(
    user_id uuid,
    username text,
    profile_public boolean,
    sessions integer,
    attempts integer,
    clean integer,
    moving integer,
    overlap integer,
    slow integer,
    clean_rate double precision,
    moving_rate double precision,
    avg_counter_delay_ms double precision,
    avg_click_delay_ms double precision,
    last_session_at timestamptz
)
language plpgsql
security definer
set search_path = public
as $$
begin
    if auth.uid() is null then
        raise exception 'not authenticated';
    end if;
    if not exists (
        select 1
          from public.clan_members cm
         where cm.clan_id = p_clan_id
           and cm.user_id = auth.uid()
    ) then
        raise exception 'not a clan member';
    end if;

    return query
    select p.id::uuid as user_id,
           p.username::text as username,
           p.profile_public::boolean as profile_public,
           count(s.id)::integer as sessions,
           coalesce(sum(s.attempts), 0)::integer as attempts,
           coalesce(sum(s.clean), 0)::integer as clean,
           coalesce(sum(s.moving), 0)::integer as moving,
           coalesce(sum(s.overlap), 0)::integer as overlap,
           coalesce(sum(s.slow), 0)::integer as slow,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else (coalesce(sum(s.clean), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end as clean_rate,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else (coalesce(sum(s.moving), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end as moving_rate,
           coalesce(avg(s.avg_counter_delay_ms), 0)::double precision as avg_counter_delay_ms,
           coalesce(avg(s.avg_click_delay_ms), 0)::double precision as avg_click_delay_ms,
           max(s.ended_at)::timestamptz as last_session_at
      from public.clan_members m
      join public.profiles p on p.id = m.user_id
      left join public.stat_sessions s on s.user_id = p.id
     where m.clan_id = p_clan_id
     group by p.id, p.username, p.profile_public
     order by 10 desc, 5 desc, p.username;
end;
$$;

create function public.my_profile_stats()
returns table(
    user_id uuid,
    username text,
    display_name text,
    profile_public boolean,
    privacy_choice_made boolean,
    is_anonymous boolean,
    share_development_stats boolean,
    sessions integer,
    attempts integer,
    clean integer,
    moving integer,
    overlap integer,
    slow integer,
    clean_rate double precision,
    moving_rate double precision,
    avg_counter_delay_ms double precision,
    avg_click_delay_ms double precision,
    last_session_at timestamptz
)
language sql
security definer
set search_path = public
as $$
    select p.id::uuid as user_id,
           p.username::text as username,
           p.display_name::text as display_name,
           p.profile_public::boolean as profile_public,
           p.privacy_choice_made::boolean as privacy_choice_made,
           p.is_anonymous::boolean as is_anonymous,
           p.share_development_stats::boolean as share_development_stats,
           count(s.id)::integer as sessions,
           coalesce(sum(s.attempts), 0)::integer as attempts,
           coalesce(sum(s.clean), 0)::integer as clean,
           coalesce(sum(s.moving), 0)::integer as moving,
           coalesce(sum(s.overlap), 0)::integer as overlap,
           coalesce(sum(s.slow), 0)::integer as slow,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else (coalesce(sum(s.clean), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end as clean_rate,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else (coalesce(sum(s.moving), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end as moving_rate,
           coalesce(avg(s.avg_counter_delay_ms), 0)::double precision as avg_counter_delay_ms,
           coalesce(avg(s.avg_click_delay_ms), 0)::double precision as avg_click_delay_ms,
           max(s.ended_at)::timestamptz as last_session_at
      from public.profiles p
      left join public.stat_sessions s on s.user_id = p.id
     where p.id = auth.uid()
     group by p.id, p.username, p.display_name, p.profile_public, p.privacy_choice_made, p.is_anonymous, p.share_development_stats;
$$;

create function public.public_profile_stats(p_username text)
returns table(
    user_id uuid,
    username text,
    display_name text,
    profile_public boolean,
    privacy_choice_made boolean,
    is_anonymous boolean,
    share_development_stats boolean,
    sessions integer,
    attempts integer,
    clean integer,
    moving integer,
    overlap integer,
    slow integer,
    clean_rate double precision,
    moving_rate double precision,
    avg_counter_delay_ms double precision,
    avg_click_delay_ms double precision,
    last_session_at timestamptz
)
language plpgsql
security definer
set search_path = public
as $$
begin
    if auth.uid() is null then
        raise exception 'not authenticated';
    end if;

    if not exists (
        select 1
          from public.profiles p
         where p.username = lower(trim(leading '@' from p_username))
           and p.profile_public = true
    ) then
        raise exception 'this profile is private';
    end if;

    return query
    select p.id::uuid as user_id,
           p.username::text as username,
           p.display_name::text as display_name,
           p.profile_public::boolean as profile_public,
           p.privacy_choice_made::boolean as privacy_choice_made,
           p.is_anonymous::boolean as is_anonymous,
           p.share_development_stats::boolean as share_development_stats,
           count(s.id)::integer as sessions,
           coalesce(sum(s.attempts), 0)::integer as attempts,
           coalesce(sum(s.clean), 0)::integer as clean,
           coalesce(sum(s.moving), 0)::integer as moving,
           coalesce(sum(s.overlap), 0)::integer as overlap,
           coalesce(sum(s.slow), 0)::integer as slow,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else (coalesce(sum(s.clean), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end as clean_rate,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else (coalesce(sum(s.moving), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end as moving_rate,
           coalesce(avg(s.avg_counter_delay_ms), 0)::double precision as avg_counter_delay_ms,
           coalesce(avg(s.avg_click_delay_ms), 0)::double precision as avg_click_delay_ms,
           max(s.ended_at)::timestamptz as last_session_at
      from public.profiles p
      left join public.stat_sessions s on s.user_id = p.id
     where p.username = lower(trim(leading '@' from p_username))
       and p.profile_public = true
     group by p.id, p.username, p.display_name, p.profile_public, p.privacy_choice_made, p.is_anonymous, p.share_development_stats;
end;
$$;


create function public.public_profile_rankings(p_query text default '')
returns table(
    user_id uuid,
    username text,
    display_name text,
    profile_public boolean,
    privacy_choice_made boolean,
    is_anonymous boolean,
    share_development_stats boolean,
    sessions integer,
    attempts integer,
    clean integer,
    moving integer,
    overlap integer,
    slow integer,
    clean_rate double precision,
    moving_rate double precision,
    avg_counter_delay_ms double precision,
    avg_click_delay_ms double precision,
    last_session_at timestamptz
)
language plpgsql
security definer
set search_path = public
as $$
declare
    v_query text := lower(trim(leading '@' from coalesce(p_query, '')));
begin
    if auth.uid() is null then
        raise exception 'not authenticated';
    end if;

    return query
    select p.id::uuid as user_id,
           p.username::text as username,
           p.display_name::text as display_name,
           p.profile_public::boolean as profile_public,
           p.privacy_choice_made::boolean as privacy_choice_made,
           p.is_anonymous::boolean as is_anonymous,
           p.share_development_stats::boolean as share_development_stats,
           count(s.id)::integer as sessions,
           coalesce(sum(s.attempts), 0)::integer as attempts,
           coalesce(sum(s.clean), 0)::integer as clean,
           coalesce(sum(s.moving), 0)::integer as moving,
           coalesce(sum(s.overlap), 0)::integer as overlap,
           coalesce(sum(s.slow), 0)::integer as slow,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else (coalesce(sum(s.clean), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end as clean_rate,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else (coalesce(sum(s.moving), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end as moving_rate,
           coalesce(avg(s.avg_counter_delay_ms), 0)::double precision as avg_counter_delay_ms,
           coalesce(avg(s.avg_click_delay_ms), 0)::double precision as avg_click_delay_ms,
           max(s.ended_at)::timestamptz as last_session_at
      from public.profiles p
      left join public.stat_sessions s on s.user_id = p.id
     where p.profile_public = true
       and (v_query = '' or p.username ilike '%' || v_query || '%' or p.display_name ilike '%' || v_query || '%')
     group by p.id, p.username, p.display_name, p.profile_public, p.privacy_choice_made, p.is_anonymous, p.share_development_stats
     order by case when coalesce(sum(s.attempts), 0) >= 20 then 0 else 1 end,
              case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                   else (coalesce(sum(s.clean), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end desc,
              coalesce(sum(s.attempts), 0) desc,
              case when coalesce(sum(s.attempts), 0) = 0 then 100::double precision
                   else (coalesce(sum(s.moving), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision)::double precision end asc,
              p.username asc
     limit 100;
end;
$$;


create function public.admin_global_stats()
returns table(
    players integer,
    anonymous_players integer,
    sessions integer,
    attempts integer,
    clean integer,
    moving integer,
    overlap integer,
    slow integer,
    clean_rate double precision,
    moving_rate double precision
)
language plpgsql
security definer
set search_path = public
as $$
begin
    if auth.uid() is null or not public.can_view_admin(auth.uid()) then
        raise exception 'admin or moderator access required';
    end if;

    return query
    select count(distinct p.id)::integer as players,
           count(distinct p.id) filter (where p.is_anonymous)::integer as anonymous_players,
           count(s.id)::integer as sessions,
           coalesce(sum(s.attempts), 0)::integer as attempts,
           coalesce(sum(s.clean), 0)::integer as clean,
           coalesce(sum(s.moving), 0)::integer as moving,
           coalesce(sum(s.overlap), 0)::integer as overlap,
           coalesce(sum(s.slow), 0)::integer as slow,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else coalesce(sum(s.clean), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision end as clean_rate,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else coalesce(sum(s.moving), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision end as moving_rate
      from public.profiles p
      left join public.stat_sessions s on s.user_id = p.id;
end;
$$;

create function public.admin_player_stats()
returns table(
    user_id uuid,
    username text,
    is_anonymous boolean,
    profile_public boolean,
    sessions integer,
    attempts integer,
    clean integer,
    moving integer,
    overlap integer,
    slow integer,
    clean_rate double precision,
    moving_rate double precision,
    avg_counter_delay_ms double precision,
    avg_click_delay_ms double precision,
    last_session_at timestamptz
)
language plpgsql
security definer
set search_path = public
as $$
begin
    if auth.uid() is null or not public.can_view_admin(auth.uid()) then
        raise exception 'admin or moderator access required';
    end if;

    return query
    select p.id::uuid as user_id,
           p.username::text as username,
           p.is_anonymous::boolean as is_anonymous,
           p.profile_public::boolean as profile_public,
           count(s.id)::integer as sessions,
           coalesce(sum(s.attempts), 0)::integer as attempts,
           coalesce(sum(s.clean), 0)::integer as clean,
           coalesce(sum(s.moving), 0)::integer as moving,
           coalesce(sum(s.overlap), 0)::integer as overlap,
           coalesce(sum(s.slow), 0)::integer as slow,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else coalesce(sum(s.clean), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision end as clean_rate,
           case when coalesce(sum(s.attempts), 0) = 0 then 0::double precision
                else coalesce(sum(s.moving), 0)::double precision * 100.0 / nullif(coalesce(sum(s.attempts), 0), 0)::double precision end as moving_rate,
           coalesce(avg(s.avg_counter_delay_ms), 0)::double precision as avg_counter_delay_ms,
           coalesce(avg(s.avg_click_delay_ms), 0)::double precision as avg_click_delay_ms,
           max(s.ended_at)::timestamptz as last_session_at
      from public.profiles p
      left join public.stat_sessions s on s.user_id = p.id
     group by p.id, p.username, p.is_anonymous, p.profile_public
     order by attempts desc, sessions desc, p.username;
end;
$$;


create function public.moderator_count()
returns integer
language plpgsql
security definer
set search_path = public
as $$
begin
    if auth.uid() is null or not public.is_admin(auth.uid()) then
        raise exception 'admin access required';
    end if;
    return (
        select count(distinct user_id)::integer
          from (
                select m.user_id from public.mods m
                union
                select p.id as user_id from public.profiles p where p.app_role = 'moderator'
               ) role_sources
    );
end;
$$;

create function public.list_moderators()
returns table(
    user_id uuid,
    username text,
    email text,
    created_at timestamptz
)
language plpgsql
security definer
set search_path = public, auth
as $$
begin
    if auth.uid() is null or not public.is_admin(auth.uid()) then
        raise exception 'admin access required';
    end if;

    return query
    select p.id::uuid as user_id,
           p.username::text as username,
           coalesce(u.email, '')::text as email,
           coalesce(m.created_at, p.updated_at, p.created_at)::timestamptz as created_at
      from public.profiles p
      left join public.mods m on m.user_id = p.id
      left join auth.users u on u.id = p.id
     where not public.is_admin(p.id)
       and (p.app_role = 'moderator' or m.user_id is not null)
     order by p.username;
end;
$$;

create function public.search_moderator_candidates(p_query text)
returns table(
    user_id uuid,
    username text,
    email text
)
language plpgsql
security definer
set search_path = public, auth
as $$
declare
    v_query text := lower(trim(coalesce(p_query, '')));
begin
    if auth.uid() is null or not public.is_admin(auth.uid()) then
        raise exception 'admin access required';
    end if;
    if length(v_query) < 2 then
        raise exception 'enter at least 2 characters';
    end if;

    return query
    select p.id::uuid as user_id,
           p.username::text as username,
           coalesce(u.email, '')::text as email
      from public.profiles p
      left join auth.users u on u.id = p.id
     where not public.can_view_admin(p.id)
       and p.id <> auth.uid()
       and (p.username ilike '%' || v_query || '%' or coalesce(u.email, '') ilike '%' || v_query || '%')
     order by p.username
     limit 25;
end;
$$;

create function public.add_moderator(p_user_id uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if auth.uid() is null or not public.is_admin(auth.uid()) then
        raise exception 'admin access required';
    end if;
    if p_user_id is null or not exists (select 1 from public.profiles p where p.id = p_user_id) then
        raise exception 'user not found';
    end if;
    if public.is_admin(p_user_id) then
        raise exception 'the admin does not need to be added as a mod';
    end if;

    update public.profiles
       set app_role = 'moderator',
           updated_at = now()
     where id = p_user_id
       and app_role <> 'admin';

    insert into public.mods(user_id, created_by)
    values (p_user_id, auth.uid())
    on conflict (user_id) do nothing;
end;
$$;

create function public.remove_moderator(p_user_id uuid)
returns void
language plpgsql
security definer
set search_path = public
as $$
begin
    if auth.uid() is null or not public.is_admin(auth.uid()) then
        raise exception 'admin access required';
    end if;
    delete from public.mods m where m.user_id = p_user_id;
    update public.profiles
       set app_role = 'user',
           updated_at = now()
     where id = p_user_id
       and app_role = 'moderator';
end;
$$;

grant execute on function public.is_clan_member(uuid, uuid) to authenticated;
grant execute on function public.is_clan_owner(uuid, uuid) to authenticated;
grant execute on function public.shares_clan(uuid, uuid) to authenticated;
grant execute on function public.is_admin(uuid) to authenticated;
grant execute on function public.is_mod(uuid) to authenticated;
grant execute on function public.can_view_admin(uuid) to authenticated;
grant execute on function public.current_user_is_admin() to authenticated;
grant execute on function public.current_user_admin_access() to authenticated;
grant execute on function public.create_clan(text) to authenticated;
grant execute on function public.invite_to_clan(uuid, text) to authenticated;
grant execute on function public.respond_clan_invite(uuid, boolean) to authenticated;
grant execute on function public.my_clans() to authenticated;
grant execute on function public.my_pending_invites() to authenticated;
grant execute on function public.clan_dashboard(uuid) to authenticated;
grant execute on function public.my_profile_stats() to authenticated;
grant execute on function public.public_profile_stats(text) to authenticated;
grant execute on function public.public_profile_rankings(text) to authenticated;
grant execute on function public.admin_global_stats() to authenticated;
grant execute on function public.admin_player_stats() to authenticated;

grant execute on function public.moderator_count() to authenticated;
grant execute on function public.list_moderators() to authenticated;
grant execute on function public.search_moderator_candidates(text) to authenticated;
grant execute on function public.add_moderator(uuid) to authenticated;
grant execute on function public.remove_moderator(uuid) to authenticated;


-- Manual admin setup example:
-- 1) Create/sign in to a StrafeLab account so public.profiles contains your row.
-- 2) Find the row:
--      select id, username, created_at from public.profiles order by created_at asc;
-- 3) Promote the account manually:
--      insert into public.admin_users(user_id) values ('YOUR_PROFILE_UUID') on conflict do nothing;
-- 4) Sign out/in or refresh admin access in the app.

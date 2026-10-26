-- Creates (or refreshes) the limited PostgreSQL role the API connects with. Run by the "migrate" container, as the database
-- owner, right after the migrations, and safe to run any number of times: it only creates what is missing, re-applies the
-- same grants, and sets the password. It never touches any data.
--
-- Settings (session variables, set by the caller before running this file):
--   techstore.app_password  required. The role's password. The script stops if it is missing or empty.
--   techstore.app_role      optional. The role's name; defaults to techstore_app. (Tests use another name.)
--
-- What the role may do: read and write rows, and use sequences. It may NOT change the schema, drop or truncate anything,
-- create roles or databases, or use any superuser feature. It may delete rows only from user_roles (a user's role is
-- replaced); business records and the migration history are not deletable by it.

DO $provision$
DECLARE
    app_role text := coalesce(nullif(current_setting('techstore.app_role', true), ''), 'techstore_app');
    app_password text := current_setting('techstore.app_password', true);
BEGIN
    IF app_password IS NULL OR app_password = '' THEN
        RAISE EXCEPTION 'The application database password is not set (TECHSTORE_APP_DB_PASSWORD).';
    END IF;

    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = app_role) THEN
        EXECUTE format('CREATE ROLE %I LOGIN', app_role);
    END IF;

    -- Re-asserted on every run, so a role that was changed by hand is put back, and a changed password takes effect.
    EXECUTE format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L', app_role, app_password);

    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), app_role);
    EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', app_role);
    EXECUTE 'REVOKE CREATE ON SCHEMA public FROM PUBLIC';

    -- Everything that exists now ...
    EXECUTE format('GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO %I', app_role);
    EXECUTE format('GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO %I', app_role);
    EXECUTE format('GRANT DELETE ON TABLE public.user_roles TO %I', app_role);

    -- ... and everything the same user (the migration user, who runs this) creates later, so a future migration does not
    -- lock the API out of its new tables. A table the API must delete from needs one extra GRANT DELETE line above.
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE ON TABLES TO %I', app_role);
    EXECUTE format('ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I', app_role);

    -- The migration history is read by the API (to check migrations were applied) but only the migration user writes it.
    EXECUTE format('REVOKE INSERT, UPDATE, DELETE ON TABLE public."__EFMigrationsHistory" FROM %I', app_role);
END
$provision$;

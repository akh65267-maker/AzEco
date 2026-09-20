-- Post-deployment: create one PostgreSQL role per managed identity and grant it access
-- to exactly one database.
--
-- WHY THIS IS NOT BICEP
-- Azure has no resource type for "CREATE ROLE" or "GRANT". The server is an Azure
-- resource; the roles inside it are PostgreSQL state. Bicep provisions the former and
-- can say nothing about the latter. Pretending otherwise — for example by wrapping this
-- in a deploymentScript resource — buys you an ARM-shaped error message instead of a
-- SQL-shaped one, which is strictly worse when it fails at 2am.
--
-- HOW TO RUN
--   1. Authenticate as the Entra admin principal set in postgres.bicep (the GROUP).
--   2. Run this file ONCE PER DATABASE. The role names come from `main.bicep`'s
--      `identityNames` output — they are the managed identity resource NAMES, which is
--      how Azure maps a Postgres role to an Entra principal. A typo here produces a
--      login that fails with a generic authentication error and no hint.
--
--      TOKEN=$(az account get-access-token \
--                --resource-type oss-rdbms --query accessToken -o tsv)
--      PGPASSWORD=$TOKEN psql \
--        "host=<server>.postgres.database.azure.com dbname=catalogdb \
--         user=<entra-admin-upn> sslmode=require" \
--        -v role_name=id-catalog-<token> -f grant-managed-identities.sql
--
-- IDEMPOTENCY
-- Safe to re-run: pgaadauth_create_principal errors if the principal exists, so the
-- DO block swallows duplicate_object. Everything else is naturally idempotent.

\set ON_ERROR_STOP on

-- :role_name is the managed identity's resource name, e.g. 'id-catalog-dev7x3k9q'.
-- The second argument (isAdmin) is false: a service role is never a server admin.
-- The third (isMfa) is false: a managed identity cannot do MFA.
DO $$
BEGIN
    PERFORM pgaadauth_create_principal(:'role_name', false, false);
EXCEPTION
    WHEN duplicate_object THEN
        RAISE NOTICE 'Principal % already exists, continuing.', :'role_name';
END
$$;

-- CONNECT is granted to this role only. PUBLIC's implicit connect privilege is revoked
-- so that a role created for another service cannot open a session against this
-- database — the isolation between userdb/catalogdb/orderdb is enforced here, by GRANT,
-- not by anyone remembering which connection string to use.
REVOKE CONNECT ON DATABASE :"db_name" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db_name" TO :"role_name";

-- The service owns its schema outright: EF Core migrations need to CREATE TABLE, and
-- splitting DDL from DML across two roles is a real option but adds a second credential
-- path for a benefit this system does not yet need.
GRANT USAGE, CREATE ON SCHEMA public TO :"role_name";

GRANT SELECT, INSERT, UPDATE, DELETE
    ON ALL TABLES IN SCHEMA public TO :"role_name";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO :"role_name";

-- Future tables created by migrations inherit the same grants. Without this, every
-- migration that adds a table needs a follow-up GRANT, and the one everybody forgets
-- fails in production rather than in CI.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"role_name";
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO :"role_name";

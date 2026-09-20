-- Local mirror of the Azure database design: ONE server, THREE databases,
-- THREE roles, and no path from one service to another's tables.
--
-- The isolation is enforced by GRANTs, not by convention. If a developer can
-- accidentally join catalog.products to order.orders locally, someone will ship
-- that join, and the service boundary becomes fiction.
--
-- In Azure the passwords do not exist at all: each role is mapped to a service's
-- user-assigned Managed Identity and authenticates with an Entra token.

CREATE ROLE user_app     LOGIN PASSWORD 'localdev';
CREATE ROLE catalog_app  LOGIN PASSWORD 'localdev';
CREATE ROLE order_app    LOGIN PASSWORD 'localdev';

CREATE DATABASE userdb    OWNER user_app;
CREATE DATABASE catalogdb OWNER catalog_app;
CREATE DATABASE orderdb   OWNER order_app;

-- Revoke the implicit PUBLIC connect privilege, then grant it back to exactly
-- one role per database.
REVOKE CONNECT ON DATABASE userdb    FROM PUBLIC;
REVOKE CONNECT ON DATABASE catalogdb FROM PUBLIC;
REVOKE CONNECT ON DATABASE orderdb   FROM PUBLIC;

GRANT CONNECT ON DATABASE userdb    TO user_app;
GRANT CONNECT ON DATABASE catalogdb TO catalog_app;
GRANT CONNECT ON DATABASE orderdb   TO order_app;

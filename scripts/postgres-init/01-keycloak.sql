-- Runs once on first Postgres startup (dev). Keycloak gets its own database
-- in the same instance.
CREATE USER keycloak WITH PASSWORD 'keycloak';
CREATE DATABASE keycloak OWNER keycloak;

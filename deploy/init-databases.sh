#!/bin/sh
set -eu

export PERSONALFLOW_LIFE_RUNTIME="$(cat /run/secrets/life-runtime-password)"
export PERSONALFLOW_LIFE_MIGRATOR="$(cat /run/secrets/life-migrator-password)"
export PERSONALFLOW_LEARNING_RUNTIME="$(cat /run/secrets/learning-runtime-password)"
export PERSONALFLOW_LEARNING_MIGRATOR="$(cat /run/secrets/learning-migrator-password)"
export PERSONALFLOW_IDENTITY_RUNTIME="$(cat /run/secrets/identity-runtime-password)"
export PERSONALFLOW_IDENTITY_MIGRATOR="$(cat /run/secrets/identity-migrator-password)"

psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<'SQL'
\getenv life_runtime PERSONALFLOW_LIFE_RUNTIME
\getenv life_migrator PERSONALFLOW_LIFE_MIGRATOR
\getenv learning_runtime PERSONALFLOW_LEARNING_RUNTIME
\getenv learning_migrator PERSONALFLOW_LEARNING_MIGRATOR
\getenv identity_runtime PERSONALFLOW_IDENTITY_RUNTIME
\getenv identity_migrator PERSONALFLOW_IDENTITY_MIGRATOR

CREATE ROLE life LOGIN PASSWORD :'life_runtime';
CREATE ROLE life_migrator LOGIN PASSWORD :'life_migrator';
CREATE DATABASE life OWNER life_migrator;
REVOKE CONNECT ON DATABASE life FROM PUBLIC;
GRANT CONNECT ON DATABASE life TO life;
\connect life
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO life;
GRANT CREATE ON SCHEMA public TO life_migrator;
ALTER DEFAULT PRIVILEGES FOR ROLE life_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO life;
ALTER DEFAULT PRIVILEGES FOR ROLE life_migrator IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO life;

\connect postgres
CREATE ROLE learning LOGIN PASSWORD :'learning_runtime';
CREATE ROLE learning_migrator LOGIN PASSWORD :'learning_migrator';
CREATE DATABASE learning OWNER learning_migrator;
REVOKE CONNECT ON DATABASE learning FROM PUBLIC;
GRANT CONNECT ON DATABASE learning TO learning;
\connect learning
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO learning;
GRANT CREATE ON SCHEMA public TO learning_migrator;
ALTER DEFAULT PRIVILEGES FOR ROLE learning_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO learning;
ALTER DEFAULT PRIVILEGES FOR ROLE learning_migrator IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO learning;

\connect postgres
CREATE ROLE identity LOGIN PASSWORD :'identity_runtime';
CREATE ROLE identity_migrator LOGIN PASSWORD :'identity_migrator';
CREATE DATABASE identity OWNER identity_migrator;
REVOKE CONNECT ON DATABASE identity FROM PUBLIC;
GRANT CONNECT ON DATABASE identity TO identity;
\connect identity
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO identity;
GRANT CREATE ON SCHEMA public TO identity_migrator;
ALTER DEFAULT PRIVILEGES FOR ROLE identity_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO identity;
ALTER DEFAULT PRIVILEGES FOR ROLE identity_migrator IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO identity;
SQL

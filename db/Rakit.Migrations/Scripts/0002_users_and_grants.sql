-- Database users without a login. The application connects with its Windows identity (dbo)
-- and impersonates one of these users per connection (EXECUTE AS USER ... WITH NO REVERT).
-- dbo may impersonate any database user, so no GRANT IMPERSONATE is needed here.
IF DATABASE_PRINCIPAL_ID(N'rakit_app') IS NULL CREATE USER rakit_app WITHOUT LOGIN;
IF DATABASE_PRINCIPAL_ID(N'rakit_exec') IS NULL CREATE USER rakit_exec WITHOUT LOGIN;

-- rakit_app: owns the builder metadata, may read catalog metadata of app, nothing else.
GRANT CONTROL ON SCHEMA::rakit TO rakit_app;
GRANT VIEW DEFINITION ON SCHEMA::app TO rakit_app;

-- rakit_exec: runs business objects only. No access to rakit, not db_owner.
GRANT EXECUTE, SELECT ON SCHEMA::app TO rakit_exec;

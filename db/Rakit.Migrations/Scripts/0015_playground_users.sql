-- Database users for the Builder Playground (F6a). Like rakit_app / rakit_exec they have no login: the application
-- impersonates them per connection. Neither can see the rakit schema, other schemas or anything outside this database.
IF DATABASE_PRINCIPAL_ID(N'rakit_play_ro') IS NULL CREATE USER rakit_play_ro WITHOUT LOGIN;
IF DATABASE_PRINCIPAL_ID(N'rakit_play') IS NULL CREATE USER rakit_play WITHOUT LOGIN;

-- rakit_play_ro: reads and runs procedures in app (the Playground always rolls these runs back).
GRANT SELECT, EXECUTE, VIEW DEFINITION ON SCHEMA::app TO rakit_play_ro;

-- rakit_play: used only when Rakit:Playground:AllowWrite is true. Full control of the app schema, and the right to
-- create new objects in it (creating an object needs the database-level CREATE permission as well as ALTER on the schema).
GRANT CONTROL ON SCHEMA::app TO rakit_play;
GRANT CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION TO rakit_play;

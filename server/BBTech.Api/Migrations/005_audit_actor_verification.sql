-- Client audit entries name their actor themselves. The server records whether it could confirm that actor
-- (active user, allowed on the sending workstation, plausible time, client-side action). Unconfirmed entries are kept
-- as evidence but must not be read as proof of who did something.
ALTER TABLE audit_logs ADD COLUMN actor_verified boolean NOT NULL DEFAULT false;
-- Entries written by the server itself are attributed by the server.
-- (Existing rows are not rewritten: the table is append-only; server rows are recognisable by source = 'server'.)

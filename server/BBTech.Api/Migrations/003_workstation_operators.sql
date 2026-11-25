-- Optional operator allow-list per workstation (USB key). When a workstation has at least one row here,
-- the server accepts records ONLY for these operators from that workstation's key. Without rows, any active
-- operator is accepted (technicians have no PIN by design; see docs/SERVER.md "Operator identity").
CREATE TABLE workstation_operators (
    workstation_id uuid NOT NULL REFERENCES workstations(workstation_id) ON DELETE CASCADE,
    user_id        text NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
    PRIMARY KEY (workstation_id, user_id)
);

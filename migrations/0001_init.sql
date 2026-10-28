CREATE TABLE teams (
    id             serial PRIMARY KEY,
    preferred_name text NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_teams_preferred_name ON teams (preferred_name);

CREATE TABLE source_teams (
    id              serial PRIMARY KEY,
    team_id         int REFERENCES teams (id),
    source          text NOT NULL,
    source_team_id  text,
    name            text NOT NULL,
    normalized_name text NOT NULL,
    first_seen_at   timestamptz NOT NULL,
    last_seen_at    timestamptz NOT NULL
);
CREATE INDEX ix_source_teams_team_id ON source_teams (team_id);
CREATE INDEX ix_source_teams_normalized_name ON source_teams (normalized_name);
CREATE UNIQUE INDEX uq_source_teams_external_id ON source_teams (source, source_team_id)
    WHERE source_team_id IS NOT NULL;
CREATE UNIQUE INDEX uq_source_teams_name ON source_teams (source, name)
    WHERE source_team_id IS NULL;

CREATE TABLE source_team_names (
    id              serial PRIMARY KEY,
    source_team_id  int NOT NULL REFERENCES source_teams (id),
    name            text NOT NULL,
    normalized_name text NOT NULL,
    first_seen_at   timestamptz NOT NULL,
    last_seen_at    timestamptz NOT NULL,
    CONSTRAINT uq_source_team_names UNIQUE (source_team_id, name)
);
CREATE INDEX ix_source_team_names_source_team_id ON source_team_names (source_team_id);
CREATE INDEX ix_source_team_names_normalized_name ON source_team_names (normalized_name);

-- alias и canonical хранятся нормализованными.
CREATE TABLE team_aliases (
    id         serial PRIMARY KEY,
    alias      text NOT NULL UNIQUE,
    canonical  text NOT NULL,
    created_at timestamptz DEFAULT now()
);

CREATE TABLE team_match_proposals (
    id          serial PRIMARY KEY,
    left_id     int NOT NULL REFERENCES source_teams (id),
    right_id    int NOT NULL REFERENCES source_teams (id),
    status      text NOT NULL DEFAULT 'pending',
    reviewed_by text,
    reviewed_at timestamptz,
    created_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_team_match_proposal_pair UNIQUE (left_id, right_id),
    CONSTRAINT ck_team_match_proposal_order CHECK (left_id < right_id),
    CONSTRAINT ck_team_match_proposal_status CHECK (status IN ('pending', 'confirmed', 'rejected'))
);
CREATE INDEX ix_team_match_proposals_right_id ON team_match_proposals (right_id);
CREATE INDEX ix_team_match_proposals_status ON team_match_proposals (status);

CREATE TABLE team_match_evidence (
    id           serial PRIMARY KEY,
    proposal_id  int NOT NULL REFERENCES team_match_proposals (id),
    evidence_key text NOT NULL,
    score        double precision NOT NULL,
    details      jsonb NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_team_match_evidence UNIQUE (proposal_id, evidence_key)
);
-- Инвалидация улик ищет по api_match_id карточки.
CREATE INDEX ix_team_match_evidence_api_match_id
    ON team_match_evidence (((details ->> 'api_match_id')::int));

CREATE TABLE team_mapping_audit (
    id             serial PRIMARY KEY,
    source_team_id int NOT NULL REFERENCES source_teams (id),
    old_team_id    int REFERENCES teams (id),
    new_team_id    int REFERENCES teams (id),
    actor          text NOT NULL,
    method         text NOT NULL,
    proposal_id    int REFERENCES team_match_proposals (id),
    created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_team_mapping_audit_source_team_id ON team_mapping_audit (source_team_id);

-- Очередь UTC-дат на пересчёт матчером; generation — сколько раз дата менялась.
CREATE TABLE team_match_work (
    match_date date PRIMARY KEY,
    generation bigint NOT NULL DEFAULT 1
);

-- Вход матчера: события источников.
-- observed_at — когда источник видел событие: запоздавший повтор не затирает свежие данные.
CREATE TABLE fixtures (
    source      text NOT NULL,
    event_id    text NOT NULL,
    home        text NOT NULL,
    away        text NOT NULL,
    home_ext_id text,
    away_ext_id text,
    kickoff     timestamptz,
    league      text,
    observed_at timestamptz NOT NULL,
    PRIMARY KEY (source, event_id)
);
CREATE INDEX ix_fixtures_source_kickoff ON fixtures (source, kickoff);

CREATE TABLE IF NOT EXISTS invite_codes (
    id uuid PRIMARY KEY,
    code_hash varchar(64) NOT NULL UNIQUE,
    status integer NOT NULL,
    activated_device_id uuid UNIQUE,
    created_at timestamptz NOT NULL,
    activated_at timestamptz NULL
);

CREATE TABLE IF NOT EXISTS device_activations (
    id uuid PRIMARY KEY,
    device_fingerprint_hash varchar(64) NOT NULL UNIQUE,
    token_hash varchar(64) NOT NULL UNIQUE,
    created_at timestamptz NOT NULL,
    revoked_at timestamptz NULL,
    invite_code_id uuid NOT NULL REFERENCES invite_codes(id) ON DELETE RESTRICT
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_invite_codes_activated_device') THEN
        ALTER TABLE invite_codes ADD CONSTRAINT fk_invite_codes_activated_device
            FOREIGN KEY (activated_device_id) REFERENCES device_activations(id) ON DELETE RESTRICT;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS entitlements (
    id uuid PRIMARY KEY,
    activation_id uuid NOT NULL REFERENCES device_activations(id) ON DELETE CASCADE,
    plan integer NOT NULL,
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_entitlements_activation_plan_ends ON entitlements (activation_id, plan, ends_at);

CREATE TABLE IF NOT EXISTS quota_buckets (
    id uuid PRIMARY KEY,
    activation_id uuid NOT NULL REFERENCES device_activations(id) ON DELETE CASCADE,
    entitlement_id uuid NULL REFERENCES entitlements(id) ON DELETE RESTRICT,
    kind integer NOT NULL,
    remaining_seconds integer NOT NULL,
    reserved_seconds integer NOT NULL,
    expires_at timestamptz NULL,
    created_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_quota_buckets_activation_kind_expiry ON quota_buckets (activation_id, kind, expires_at);

CREATE TABLE IF NOT EXISTS usage_ledger (
    id uuid PRIMARY KEY,
    session_id varchar(128) NOT NULL UNIQUE,
    activation_id uuid NOT NULL,
    source varchar(64) NOT NULL,
    seconds integer NOT NULL,
    outcome varchar(64) NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS orders (
    id uuid PRIMARY KEY,
    order_no varchar(64) NOT NULL UNIQUE,
    activation_id uuid NOT NULL,
    product_id varchar(32) NOT NULL,
    amount_fen integer NOT NULL,
    status integer NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL
);

CREATE TABLE IF NOT EXISTS asr_sessions (
    id uuid PRIMARY KEY,
    activation_id uuid NOT NULL REFERENCES device_activations(id) ON DELETE RESTRICT,
    source varchar(64) NOT NULL,
    status integer NOT NULL,
    received_pcm_bytes bigint NOT NULL,
    created_at timestamptz NOT NULL,
    finished_at timestamptz NULL
);
CREATE INDEX IF NOT EXISTS ix_asr_sessions_activation_status ON asr_sessions (activation_id, status);

CREATE TABLE IF NOT EXISTS asr_session_reservations (
    id uuid PRIMARY KEY,
    session_id uuid NOT NULL REFERENCES asr_sessions(id) ON DELETE CASCADE,
    quota_bucket_id uuid NOT NULL REFERENCES quota_buckets(id) ON DELETE RESTRICT,
    reserved_seconds integer NOT NULL,
    UNIQUE (session_id, quota_bucket_id)
);

CREATE TABLE IF NOT EXISTS payment_notifications (
    id uuid PRIMARY KEY,
    provider varchar(32) NOT NULL,
    provider_notification_id varchar(128) NOT NULL,
    order_no varchar(64) NOT NULL,
    signature_verified boolean NOT NULL,
    received_at timestamptz NOT NULL,
    verified_at timestamptz NULL,
    UNIQUE (provider, provider_notification_id)
);

CREATE TABLE IF NOT EXISTS admin_audit_logs (
    id uuid PRIMARY KEY,
    actor varchar(128) NOT NULL,
    action varchar(128) NOT NULL,
    target_id varchar(64) NOT NULL,
    metadata varchar(2048) NOT NULL,
    created_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_admin_audit_logs_created_at ON admin_audit_logs (created_at);

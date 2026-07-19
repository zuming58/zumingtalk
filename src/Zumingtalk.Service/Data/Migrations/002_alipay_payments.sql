ALTER TABLE orders
    ADD COLUMN IF NOT EXISTS provider_trade_no varchar(128) NULL,
    ADD COLUMN IF NOT EXISTS refund_request_no varchar(64) NULL,
    ADD COLUMN IF NOT EXISTS expires_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS paid_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS closed_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS refunded_at timestamptz NULL;

UPDATE orders SET expires_at = created_at + interval '30 minutes' WHERE expires_at IS NULL;
ALTER TABLE orders ALTER COLUMN expires_at SET NOT NULL;
CREATE INDEX IF NOT EXISTS ix_orders_activation_status ON orders (activation_id, status);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_orders_activation') THEN
        ALTER TABLE orders ADD CONSTRAINT fk_orders_activation
            FOREIGN KEY (activation_id) REFERENCES device_activations(id) ON DELETE RESTRICT;
    END IF;
END $$;

ALTER TABLE payment_notifications
    ADD COLUMN IF NOT EXISTS event_type varchar(32) NOT NULL DEFAULT 'unknown',
    ADD COLUMN IF NOT EXISTS provider_trade_no varchar(128) NULL,
    ADD COLUMN IF NOT EXISTS amount_fen integer NULL,
    ADD COLUMN IF NOT EXISTS processed boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS processing_result varchar(64) NOT NULL DEFAULT 'received';

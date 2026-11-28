-- Add explicit operation state for conflicts. Older local payloads contain only
-- a field patch, so their delete intent cannot be inferred reliably.
ALTER TABLE "Conflicts"
    ADD COLUMN "LocalDeleted" boolean NOT NULL DEFAULT false;

ALTER TABLE "Conflicts"
    ADD COLUMN "ServerDeleted" boolean NOT NULL DEFAULT false;

UPDATE "Conflicts"
SET "ServerDeleted" = COALESCE((
        SELECT item."Deleted"
        FROM "Items" AS item
        WHERE item."Kind" = "Conflicts"."Kind"
          AND item."Id" = "Conflicts"."EntityId"
    ), false);

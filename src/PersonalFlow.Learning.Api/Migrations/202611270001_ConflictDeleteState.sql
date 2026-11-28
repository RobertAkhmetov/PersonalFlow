-- Add explicit operation state for conflicts. Older local payloads contain only
-- a field patch, so their delete intent cannot be inferred reliably.
ALTER TABLE "Conflicts"
    ADD COLUMN "LocalDeleted" boolean NOT NULL DEFAULT false;

ALTER TABLE "Conflicts"
    ADD COLUMN "ServerDeleted" boolean NOT NULL DEFAULT false;

UPDATE "Conflicts"
SET "ServerDeleted" = COALESCE((
        SELECT progress."Deleted"
        FROM "Progress" AS progress
        WHERE progress."LessonId" = "Conflicts"."LessonId"
          AND progress."ContentVersion" = "Conflicts"."ContentVersion"
    ), false);

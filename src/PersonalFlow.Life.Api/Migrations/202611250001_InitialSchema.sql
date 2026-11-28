-- Generated from the EF Core 10/Npgsql model. Immutable after first production application.
CREATE TABLE "Changes" (
    "Revision" bigint NOT NULL,
    "Kind" text NOT NULL,
    "EntityId" uuid NOT NULL,
    "Body" text NOT NULL,
    "Deleted" boolean NOT NULL,
    CONSTRAINT "PK_Changes" PRIMARY KEY ("Revision")
);


CREATE TABLE "Conflicts" (
    "Id" uuid NOT NULL,
    "Kind" text NOT NULL,
    "EntityId" uuid NOT NULL,
    "Base" text NOT NULL,
    "Local" text NOT NULL,
    "Server" text NOT NULL,
    "ServerRevision" bigint NOT NULL,
    "Resolved" boolean NOT NULL,
    CONSTRAINT "PK_Conflicts" PRIMARY KEY ("Id")
);


CREATE TABLE "Items" (
    "Kind" text NOT NULL,
    "Id" uuid NOT NULL,
    "Revision" bigint NOT NULL,
    "Body" text NOT NULL,
    "Deleted" boolean NOT NULL,
    CONSTRAINT "PK_Items" PRIMARY KEY ("Kind", "Id")
);


CREATE TABLE "Receipts" (
    "Id" uuid NOT NULL,
    "Digest" text NOT NULL,
    "Result" text NOT NULL,
    CONSTRAINT "PK_Receipts" PRIMARY KEY ("Id")
);


CREATE TABLE "SyncStates" (
    "Id" text NOT NULL,
    "Epoch" uuid NOT NULL,
    CONSTRAINT "PK_SyncStates" PRIMARY KEY ("Id")
);


CREATE INDEX "IX_Changes_Kind_EntityId" ON "Changes" ("Kind", "EntityId");


CREATE INDEX "IX_Conflicts_Resolved_EntityId" ON "Conflicts" ("Resolved", "EntityId");


CREATE INDEX "IX_Items_Revision" ON "Items" ("Revision");



INSERT INTO "SyncStates" ("Id", "Epoch") VALUES ('life', gen_random_uuid());

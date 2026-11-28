-- Generated from the EF Core 10/Npgsql model. Immutable after first production application.
CREATE TABLE "Changes" (
    "Revision" bigint NOT NULL,
    "LessonId" uuid NOT NULL,
    "ContentVersion" integer NOT NULL,
    "Body" text NOT NULL,
    "Deleted" boolean NOT NULL,
    CONSTRAINT "PK_Changes" PRIMARY KEY ("Revision")
);


CREATE TABLE "Conflicts" (
    "Id" uuid NOT NULL,
    "LessonId" uuid NOT NULL,
    "ContentVersion" integer NOT NULL,
    "Base" text NOT NULL,
    "Local" text NOT NULL,
    "Server" text NOT NULL,
    "ServerRevision" bigint NOT NULL,
    "Resolved" boolean NOT NULL,
    CONSTRAINT "PK_Conflicts" PRIMARY KEY ("Id")
);


CREATE TABLE "Lessons" (
    "Id" uuid NOT NULL,
    "Version" integer NOT NULL,
    "Date" date NOT NULL,
    "Content" text NOT NULL,
    CONSTRAINT "PK_Lessons" PRIMARY KEY ("Id", "Version")
);


CREATE TABLE "Packs" (
    "Id" uuid NOT NULL,
    "Version" integer NOT NULL,
    "Digest" text NOT NULL,
    "Content" text NOT NULL,
    CONSTRAINT "PK_Packs" PRIMARY KEY ("Id", "Version")
);


CREATE TABLE "Progress" (
    "LessonId" uuid NOT NULL,
    "ContentVersion" integer NOT NULL,
    "Revision" bigint NOT NULL,
    "Body" text NOT NULL,
    "Deleted" boolean NOT NULL,
    CONSTRAINT "PK_Progress" PRIMARY KEY ("LessonId", "ContentVersion")
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


CREATE INDEX "IX_Changes_LessonId_ContentVersion" ON "Changes" ("LessonId", "ContentVersion");


CREATE INDEX "IX_Conflicts_Resolved_LessonId" ON "Conflicts" ("Resolved", "LessonId");


CREATE INDEX "IX_Lessons_Date" ON "Lessons" ("Date");



INSERT INTO "SyncStates" ("Id", "Epoch") VALUES ('learning', gen_random_uuid());

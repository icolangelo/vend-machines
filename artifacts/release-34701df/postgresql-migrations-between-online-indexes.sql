SET lock_timeout = '5s';
START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260731113749_AddRefreshSessions') THEN
    CREATE TABLE "RefreshSessions" (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "FamilyId" uuid NOT NULL,
        "TokenHash" character varying(64) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "ExpiresAt" timestamp with time zone NOT NULL,
        "LastUsedAt" timestamp with time zone,
        "RevokedAt" timestamp with time zone,
        "ReplacedBySessionId" uuid,
        "CreatedByIp" character varying(64),
        "UserAgent" character varying(512),
        CONSTRAINT "PK_RefreshSessions" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_RefreshSessions_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260731113749_AddRefreshSessions') THEN
    CREATE INDEX "IX_RefreshSessions_FamilyId_RevokedAt" ON "RefreshSessions" ("FamilyId", "RevokedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260731113749_AddRefreshSessions') THEN
    CREATE UNIQUE INDEX "IX_RefreshSessions_TokenHash" ON "RefreshSessions" ("TokenHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260731113749_AddRefreshSessions') THEN
    CREATE INDEX "IX_RefreshSessions_UserId_ExpiresAt" ON "RefreshSessions" ("UserId", "ExpiresAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260731113749_AddRefreshSessions') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260731113749_AddRefreshSessions', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260924103052_AddTelemetryDeviceCredentials') THEN
    ALTER TABLE "Machines" ADD "TelemetryCredentialCreatedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260924103052_AddTelemetryDeviceCredentials') THEN
    ALTER TABLE "Machines" ADD "TelemetryCredentialHash" character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260924103052_AddTelemetryDeviceCredentials') THEN
    ALTER TABLE "Machines" ADD "TelemetryCredentialRevokedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260924103052_AddTelemetryDeviceCredentials') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260924103052_AddTelemetryDeviceCredentials', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926145346_AddAuditLogs') THEN
    CREATE TABLE "AuditLogs" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid,
        "ActorUserId" uuid,
        "Action" character varying(100) NOT NULL,
        "ResourceType" character varying(100) NOT NULL,
        "ResourceId" character varying(200),
        "IpAddress" character varying(64),
        "UserAgent" character varying(512),
        "MetadataJson" text,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_AuditLogs" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926145346_AddAuditLogs') THEN
    CREATE INDEX "IX_AuditLogs_ActorUserId_CreatedAt" ON "AuditLogs" ("ActorUserId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926145346_AddAuditLogs') THEN
    CREATE INDEX "IX_AuditLogs_CompanyId_CreatedAt" ON "AuditLogs" ("CompanyId", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926145346_AddAuditLogs') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926145346_AddAuditLogs', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926165731_AddUserActivation') THEN
    ALTER TABLE "Users" ADD "IsActive" boolean NOT NULL DEFAULT TRUE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926165731_AddUserActivation') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926165731_AddUserActivation', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926171348_AddUserInvitations') THEN
    CREATE TABLE "UserInvitations" (
        "Id" uuid NOT NULL,
        "CompanyId" uuid NOT NULL,
        "CreatedByUserId" uuid NOT NULL,
        "Email" character varying(320) NOT NULL,
        "Role" character varying(32) NOT NULL,
        "TokenHash" character varying(64) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "ExpiresAt" timestamp with time zone NOT NULL,
        "AcceptedAt" timestamp with time zone,
        CONSTRAINT "PK_UserInvitations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_UserInvitations_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926171348_AddUserInvitations') THEN
    CREATE INDEX "IX_UserInvitations_CompanyId" ON "UserInvitations" ("CompanyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926171348_AddUserInvitations') THEN
    CREATE UNIQUE INDEX "IX_UserInvitations_TokenHash" ON "UserInvitations" ("TokenHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926171348_AddUserInvitations') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926171348_AddUserInvitations', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926172341_AddPasswordResetTokens') THEN
    ALTER TABLE "Users" ADD "CredentialVersion" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926172341_AddPasswordResetTokens') THEN
    CREATE TABLE "PasswordResetTokens" (
        "Id" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "CreatedByUserId" uuid NOT NULL,
        "TokenHash" character varying(64) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "ExpiresAt" timestamp with time zone NOT NULL,
        "UsedAt" timestamp with time zone,
        "RevokedAt" timestamp with time zone,
        CONSTRAINT "PK_PasswordResetTokens" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_PasswordResetTokens_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926172341_AddPasswordResetTokens') THEN
    CREATE UNIQUE INDEX "IX_PasswordResetTokens_TokenHash" ON "PasswordResetTokens" ("TokenHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926172341_AddPasswordResetTokens') THEN
    CREATE INDEX "IX_PasswordResetTokens_UserId" ON "PasswordResetTokens" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926172341_AddPasswordResetTokens') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926172341_AddPasswordResetTokens', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926173525_AddWebhookInbox') THEN
    CREATE TABLE "WebhookInboxMessages" (
        "Id" uuid NOT NULL,
        "EventKey" character varying(64) NOT NULL,
        "PaymentId" character varying(128) NOT NULL,
        "EventType" character varying(64) NOT NULL,
        "EventAction" character varying(128) NOT NULL,
        "Format" character varying(64) NOT NULL,
        "AuthenticationMode" character varying(32) NOT NULL,
        "ReceivedAt" timestamp with time zone NOT NULL,
        "Status" character varying(16) NOT NULL,
        "Attempts" integer NOT NULL,
        "NextAttemptAt" timestamp with time zone,
        "ProcessingStartedAt" timestamp with time zone,
        "ProcessedAt" timestamp with time zone,
        "LastError" character varying(2000),
        CONSTRAINT "PK_WebhookInboxMessages" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926173525_AddWebhookInbox') THEN
    CREATE UNIQUE INDEX "IX_WebhookInboxMessages_EventKey" ON "WebhookInboxMessages" ("EventKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926173525_AddWebhookInbox') THEN
    CREATE INDEX "IX_WebhookInboxMessages_Status_NextAttemptAt_ReceivedAt" ON "WebhookInboxMessages" ("Status", "NextAttemptAt", "ReceivedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260926173525_AddWebhookInbox') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260926173525_AddWebhookInbox', '10.0.12');
    END IF;
END $EF$;
COMMIT;


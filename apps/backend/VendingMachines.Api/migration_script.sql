START TRANSACTION;
ALTER TABLE "Users" ALTER COLUMN "Role" TYPE text;

ALTER TABLE "Users" ALTER COLUMN "PrivacyPolicyAcceptedAt" TYPE timestamp with time zone;

ALTER TABLE "Users" ALTER COLUMN "PasswordHash" TYPE text;

ALTER TABLE "Users" ALTER COLUMN "Name" TYPE text;

ALTER TABLE "Users" ALTER COLUMN "Email" TYPE text;

ALTER TABLE "Users" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "Users" ALTER COLUMN "Cpf" TYPE text;

ALTER TABLE "Users" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "Users" ALTER COLUMN "AcceptedPrivacyPolicy" TYPE boolean;

ALTER TABLE "Users" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "TransactionId" TYPE uuid;

ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "Timestamp" TYPE timestamp with time zone;

ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "Message" TYPE text;

ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "LogType" TYPE text;

ALTER TABLE "TransactionTelemetryLogs" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "UserId" TYPE uuid;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "TransactionId" TYPE uuid;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "SessionId" TYPE uuid;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "MachineId" TYPE text;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "EventType" TYPE text;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "Detail" TYPE text;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "DataJson" TYPE text;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "TelemetryEvents" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "UserId" TYPE uuid;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "TransactionId" TYPE uuid;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "Status" TYPE text;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "SessionId" TYPE uuid;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "SentAt" TYPE timestamp with time zone;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "MessageId" TYPE integer;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "MachineId" TYPE text;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "Error" TYPE text;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "Direction" TYPE text;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "DataJson" TYPE text;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "Command" TYPE text;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "Attempts" TYPE integer;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "AckAt" TYPE timestamp with time zone;

ALTER TABLE "TelemetryCommands" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "SystemSettings" ALTER COLUMN "UpdatedAt" TYPE timestamp with time zone;

ALTER TABLE "SystemSettings" ALTER COLUMN "ApplicationFeePercent" TYPE numeric(5,2);

ALTER TABLE "SystemSettings" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "ProductTypes" ALTER COLUMN "OriginalId" TYPE text;

ALTER TABLE "ProductTypes" ALTER COLUMN "Name" TYPE text;

ALTER TABLE "ProductTypes" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "ProductTypes" ALTER COLUMN "Id" TYPE text;

ALTER TABLE "Products" ALTER COLUMN "TypeId" TYPE text;

ALTER TABLE "Products" ALTER COLUMN "OriginalId" TYPE text;

ALTER TABLE "Products" ALTER COLUMN "Name" TYPE text;

ALTER TABLE "Products" ALTER COLUMN "IsAlcoholic" TYPE boolean;

ALTER TABLE "Products" ALTER COLUMN "Description" TYPE text;

ALTER TABLE "Products" ALTER COLUMN "Cost" TYPE numeric(18,3);

ALTER TABLE "Products" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "Products" ALTER COLUMN "Code" TYPE text;

ALTER TABLE "Products" ALTER COLUMN "Id" TYPE text;

ALTER TABLE "ProductPerformances" ALTER COLUMN "Trend" TYPE double precision;

ALTER TABLE "ProductPerformances" ALTER COLUMN "TotalSold" TYPE integer;

ALTER TABLE "ProductPerformances" ALTER COLUMN "Revenue" TYPE numeric(18,2);

ALTER TABLE "ProductPerformances" ALTER COLUMN "IsTop" TYPE boolean;

ALTER TABLE "ProductPerformances" ALTER COLUMN "Name" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "Status" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "SendTelemetryToMachine" TYPE integer;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "RawResponse" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "QrCodeBase64" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "QrCode" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "MercadoPagoStatusDetail" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "MercadoPagoStatus" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "MercadoPagoPaymentId" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "MachineId" TYPE text;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "CompletedAt" TYPE timestamp with time zone;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "PaymentTransactions" ALTER COLUMN "ApplicationFee" TYPE numeric(18,2);

ALTER TABLE "PaymentTransactions" ALTER COLUMN "Amount" TYPE numeric(18,2);

ALTER TABLE "PaymentTransactions" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "OutboxMessages" ALTER COLUMN "TransactionId" TYPE uuid;

ALTER TABLE "OutboxMessages" ALTER COLUMN "Status" TYPE text;

ALTER TABLE "OutboxMessages" ALTER COLUMN "SessionId" TYPE uuid;

ALTER TABLE "OutboxMessages" ALTER COLUMN "ProcessedAt" TYPE timestamp with time zone;

ALTER TABLE "OutboxMessages" ALTER COLUMN "PayloadJson" TYPE text;

ALTER TABLE "OutboxMessages" ALTER COLUMN "NextAttemptAt" TYPE timestamp with time zone;

ALTER TABLE "OutboxMessages" ALTER COLUMN "MessageType" TYPE text;

ALTER TABLE "OutboxMessages" ALTER COLUMN "MachineId" TYPE text;

ALTER TABLE "OutboxMessages" ALTER COLUMN "LastError" TYPE text;

ALTER TABLE "OutboxMessages" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "OutboxMessages" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "OutboxMessages" ALTER COLUMN "Attempts" TYPE integer;

ALTER TABLE "OutboxMessages" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "UpdatedAt" TYPE timestamp with time zone;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "TradeName" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "RefreshToken" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "PublicKey" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "OwnerPhone" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "OwnerName" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "OwnerEmail" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "OwnerCpf" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "MercadoPagoUserId" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "MercadoPagoSiteId" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "MercadoPagoNickname" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "LastTokenValidationStatus" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "LastTokenValidationAt" TYPE timestamp with time zone;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "IsActive" TYPE boolean;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "Cnpj" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "ClientSecret" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "ClientId" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "BusinessPhone" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "BusinessName" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "BusinessEmail" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "AccessTokenExpiresAt" TYPE timestamp with time zone;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "AccessToken" TYPE text;

ALTER TABLE "MercadoPagoIntegrations" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "MachineSessions" ALTER COLUMN "TransactionId" TYPE uuid;

ALTER TABLE "MachineSessions" ALTER COLUMN "State" TYPE text;

ALTER TABLE "MachineSessions" ALTER COLUMN "StartedByUserId" TYPE uuid;

ALTER TABLE "MachineSessions" ALTER COLUMN "StartedAt" TYPE timestamp with time zone;

ALTER TABLE "MachineSessions" ALTER COLUMN "Source" TYPE text;

ALTER TABLE "MachineSessions" ALTER COLUMN "SelectionDeadlineAt" TYPE timestamp with time zone;

ALTER TABLE "MachineSessions" ALTER COLUMN "PaymentDeadlineAt" TYPE timestamp with time zone;

ALTER TABLE "MachineSessions" ALTER COLUMN "MachineId" TYPE text;

ALTER TABLE "MachineSessions" ALTER COLUMN "LastEventAt" TYPE timestamp with time zone;

ALTER TABLE "MachineSessions" ALTER COLUMN "ItemNumber" TYPE integer;

ALTER TABLE "MachineSessions" ALTER COLUMN "DeliveryDeadlineAt" TYPE timestamp with time zone;

ALTER TABLE "MachineSessions" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "MachineSessions" ALTER COLUMN "ClosedAt" TYPE timestamp with time zone;

ALTER TABLE "MachineSessions" ALTER COLUMN "CloseReason" TYPE text;

ALTER TABLE "MachineSessions" ALTER COLUMN "AmountCents" TYPE integer;

ALTER TABLE "MachineSessions" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "Machines" ALTER COLUMN "TotalSales30d" TYPE integer;

ALTER TABLE "Machines" ALTER COLUMN "StockLevel" TYPE integer;

ALTER TABLE "Machines" ALTER COLUMN "Status" TYPE text;

ALTER TABLE "Machines" ALTER COLUMN "SerialNumber" TYPE text;

ALTER TABLE "Machines" ALTER COLUMN "Revenue30d" TYPE numeric(18,2);

ALTER TABLE "Machines" ALTER COLUMN "NormalizedSerialNumber" TYPE text;

ALTER TABLE "Machines" ALTER COLUMN "Name" TYPE text;

ALTER TABLE "Machines" ALTER COLUMN "MercadoPagoEnabled" TYPE boolean;

ALTER TABLE "Machines" ALTER COLUMN "LocationId" TYPE uuid;

ALTER TABLE "Machines" ALTER COLUMN "Location" TYPE text;

ALTER TABLE "Machines" ALTER COLUMN "LastSync" TYPE text;

ALTER TABLE "Machines" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "Machines" ALTER COLUMN "ClientName" TYPE text;

ALTER TABLE "Machines" ALTER COLUMN "Id" TYPE text;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "MonitoringEnabled" TYPE boolean;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "MachineId" TYPE text;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "LastSeenAt" TYPE timestamp with time zone;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "IsOnline" TYPE boolean;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "DisconnectedAt" TYPE timestamp with time zone;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "DeactivatedByUserId" TYPE uuid;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "DeactivatedAt" TYPE timestamp with time zone;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "ConnectionId" TYPE text;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "ConnectedAt" TYPE timestamp with time zone;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "ActivatedByUserId" TYPE uuid;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "ActivatedAt" TYPE timestamp with time zone;

ALTER TABLE "MachineConnectionStates" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "Locations" ALTER COLUMN "UpdatedAt" TYPE timestamp with time zone;

ALTER TABLE "Locations" ALTER COLUMN "Name" TYPE text;

ALTER TABLE "Locations" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "Locations" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "Locations" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "DeliveryFailures" ALTER COLUMN "TransactionId" TYPE uuid;

ALTER TABLE "DeliveryFailures" ALTER COLUMN "SessionId" TYPE uuid;

ALTER TABLE "DeliveryFailures" ALTER COLUMN "Reason" TYPE text;

ALTER TABLE "DeliveryFailures" ALTER COLUMN "OriginalReason" TYPE text;

ALTER TABLE "DeliveryFailures" ALTER COLUMN "MachineId" TYPE text;

ALTER TABLE "DeliveryFailures" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "DeliveryFailures" ALTER COLUMN "CompanyId" TYPE uuid;

ALTER TABLE "DeliveryFailures" ALTER COLUMN "Id" TYPE uuid;

ALTER TABLE "Companies" ALTER COLUMN "Name" TYPE text;

ALTER TABLE "Companies" ALTER COLUMN "CreatedByUserId" TYPE uuid;

ALTER TABLE "Companies" ALTER COLUMN "CreatedAt" TYPE timestamp with time zone;

ALTER TABLE "Companies" ALTER COLUMN "Cnpj" TYPE text;

ALTER TABLE "Companies" ALTER COLUMN "Id" TYPE uuid;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260716001728_AddTelemetrySessionManagement', '9.0.1');

COMMIT;


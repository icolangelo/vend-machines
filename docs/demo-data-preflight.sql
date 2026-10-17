-- Read-only candidate inventory for a restricted PostgreSQL copy.
-- Returns category/count only; a match is a review lead, never permission to delete.
SELECT 'seed_company_marker' AS marker, COUNT(*) AS candidate_records
FROM "Companies"
WHERE "Id" = '11111111-1111-1111-1111-111111111111'
   OR "Name" = 'ACME Machines LTDA'
   OR "NormalizedCnpj" = '12345678000199'
UNION ALL
SELECT 'seed_location_marker', COUNT(*)
FROM "Locations"
WHERE "Id" IN (
    '44444444-4444-4444-4444-444444444441',
    '44444444-4444-4444-4444-444444444442'
)
UNION ALL
SELECT 'seed_machine_marker', COUNT(*)
FROM "Machines"
WHERE ("Id" = 'VM-001' AND "SerialNumber" = 'SN-123456')
   OR ("Id" = 'VM-002' AND "SerialNumber" = 'SN-987654')
   OR ("Id" = 'VM-003' AND "SerialNumber" = 'SN-555555')
   OR ("Id" = 'VM-004' AND "SerialNumber" = 'SN-004')
   OR ("Id" = 'VM-005' AND "SerialNumber" = 'SN-005')
   OR ("Id" = 'VM-006' AND "SerialNumber" = 'SN-006')
   OR ("Id" = 'VM-007' AND "SerialNumber" = 'SN-007')
   OR ("Id" = 'VM-008' AND "SerialNumber" = 'SN-008')
   OR ("Id" = 'VM-009' AND "SerialNumber" = 'SN-009')
   OR ("Id" = 'VM-010' AND "SerialNumber" = 'SN-010')
   OR ("Id" = 'VM-011' AND "SerialNumber" = 'SN-011')
   OR ("Id" = 'VM-012' AND "SerialNumber" = 'SN-012')
UNION ALL
SELECT 'seed_product_marker', COUNT(*)
FROM "Products"
WHERE "Id" IN ('p-1', 'p-2', 'p-3')
   OR "Code" IN ('BEB-001', 'ALC-001', 'SNA-001')
UNION ALL
SELECT 'seed_product_performance_rows', COUNT(*)
FROM "ProductPerformances"
WHERE "Name" IN (
    'Água Mineral 500ml', 'Coca-Cola Lata', 'Barra de Cereal', 'Suco Del Valle',
    'Biscoito Oreo', 'Chá Gelado Leão', 'Amendoim Japonês', 'Energético Monster',
    'Paçoca Amor', 'Vitamina Yakult'
)
UNION ALL
SELECT 'seed_user_marker', COUNT(*)
FROM "Users"
WHERE "Id" IN (
    '22222222-2222-2222-2222-222222222222',
    '33333333-3333-3333-3333-333333333333'
)
UNION ALL
SELECT 'mock_payment_ids', COUNT(*)
FROM "PaymentTransactions"
WHERE "MercadoPagoPaymentId" LIKE 'mock\_%' ESCAPE '\';

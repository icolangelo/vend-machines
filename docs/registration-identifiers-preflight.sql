-- Execute somente em uma cópia recente e restrita do banco PostgreSQL.
-- Os resultados contêm identificadores pessoais: não os salve em logs/tickets abertos.
-- A migration NormalizeRegistrationIdentifiers deve ser aplicada somente se estas
-- consultas não retornarem nenhuma linha.

SELECT lower(trim(COALESCE("Email", ''))) AS normalized_email, count(*) AS quantity
FROM "Users"
WHERE lower(trim(COALESCE("Email", ''))) <> ''
GROUP BY lower(trim(COALESCE("Email", '')))
HAVING count(*) > 1
ORDER BY quantity DESC;

SELECT regexp_replace(COALESCE("Cpf", ''), '[^0-9]', '', 'g') AS normalized_cpf, count(*) AS quantity
FROM "Users"
WHERE regexp_replace(COALESCE("Cpf", ''), '[^0-9]', '', 'g') <> ''
GROUP BY regexp_replace(COALESCE("Cpf", ''), '[^0-9]', '', 'g')
HAVING count(*) > 1
ORDER BY quantity DESC;

SELECT upper(regexp_replace(COALESCE("Cnpj", ''), '[^A-Za-z0-9]', '', 'g')) AS normalized_cnpj, count(*) AS quantity
FROM "Companies"
WHERE upper(regexp_replace(COALESCE("Cnpj", ''), '[^A-Za-z0-9]', '', 'g')) <> ''
GROUP BY upper(regexp_replace(COALESCE("Cnpj", ''), '[^A-Za-z0-9]', '', 'g'))
HAVING count(*) > 1
ORDER BY quantity DESC;

-- Ensure department managers exist for all departments (idempotent).
-- Adds manager for Департамент бизнес-аналитики if missing.

INSERT INTO app_users (login, password_hash, display_name, role)
SELECT 'dep_dba', 'CHANGE_ME', 'Менеджер департамента: Департамент бизнес-аналитики', 'DepartmentManager'
WHERE NOT EXISTS (SELECT 1 FROM app_users WHERE login = 'dep_dba');

INSERT INTO user_allowed_departments (user_id, department_id)
SELECT u.user_id, d.department_id
FROM app_users u
CROSS JOIN departments d
WHERE u.login = 'dep_dba'
  AND d.name = 'Департамент бизнес-аналитики'
ON CONFLICT DO NOTHING;

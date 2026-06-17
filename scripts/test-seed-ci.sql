-- Minimal seed for CI live tests (empty database after schema.sql).

INSERT INTO departments (name)
SELECT 'Департамент маркетинга'
WHERE NOT EXISTS (SELECT 1 FROM departments WHERE name = 'Департамент маркетинга');

INSERT INTO departments (name)
SELECT 'Департамент бизнес-аналитики'
WHERE NOT EXISTS (SELECT 1 FROM departments WHERE name = 'Департамент бизнес-аналитики');

INSERT INTO educational_programs (name, education_level, is_active, is_commercial)
SELECT 'Бизнес-информатика', 'магистратура', true, false
WHERE NOT EXISTS (SELECT 1 FROM educational_programs WHERE name = 'Бизнес-информатика' AND education_level = 'магистратура');

INSERT INTO educational_programs (name, education_level, is_active, is_commercial)
SELECT 'Бизнес-информатика', 'бакалавриат', true, false
WHERE NOT EXISTS (SELECT 1 FROM educational_programs WHERE name = 'Бизнес-информатика' AND education_level = 'бакалавриат');

INSERT INTO work_types (name, code)
SELECT 'Чтение лекций студентам', 'ЛЕКЦ'
WHERE NOT EXISTS (SELECT 1 FROM work_types WHERE code = 'ЛЕКЦ');

INSERT INTO work_types (name, code)
SELECT 'Проведение семинаров для студентов', 'СЕМ'
WHERE NOT EXISTS (SELECT 1 FROM work_types WHERE code = 'СЕМ');

INSERT INTO work_types (name, code)
SELECT 'Текущий контроль и экзамен', 'ТКЭ'
WHERE NOT EXISTS (SELECT 1 FROM work_types WHERE code = 'ТКЭ');

INSERT INTO study_plans (academic_year, direction, funding_type)
SELECT '2025-2026', '38.03.02 Менеджмент', 'бюджет'
WHERE NOT EXISTS (SELECT 1 FROM study_plans WHERE academic_year = '2025-2026');

INSERT INTO app_users (login, password_hash, display_name, role)
SELECT 'admin', 'CHANGE_ME', 'Администратор', 'Admin'
WHERE NOT EXISTS (SELECT 1 FROM app_users WHERE login = 'admin');

INSERT INTO app_users (login, password_hash, display_name, role)
SELECT 'dep_dm', 'CHANGE_ME', 'Менеджер департамента: маркетинг', 'DepartmentManager'
WHERE NOT EXISTS (SELECT 1 FROM app_users WHERE login = 'dep_dm');

INSERT INTO app_users (login, password_hash, display_name, role)
SELECT 'op_bi', 'CHANGE_ME', 'Менеджер ОП: Бизнес-информатика', 'OpManager'
WHERE NOT EXISTS (SELECT 1 FROM app_users WHERE login = 'op_bi');

INSERT INTO user_allowed_departments (user_id, department_id)
SELECT u.user_id, d.department_id
FROM app_users u
JOIN departments d ON d.name = 'Департамент маркетинга'
WHERE u.login = 'dep_dm'
  AND NOT EXISTS (
    SELECT 1 FROM user_allowed_departments x WHERE x.user_id = u.user_id AND x.department_id = d.department_id
  );

INSERT INTO user_allowed_ops (user_id, op_name)
SELECT u.user_id, ep.name
FROM app_users u
JOIN educational_programs ep ON ep.name LIKE 'Бизнес-информатика%'
WHERE u.login = 'op_bi'
  AND NOT EXISTS (
    SELECT 1 FROM user_allowed_ops x WHERE x.user_id = u.user_id AND x.op_name = ep.name
  );

INSERT INTO plan_disciplines (plan_id, discipline_name, discipline_no, implementing_department_id, course_no, total_hours, dept_request_status)
SELECT sp.plan_id, 'Тестовая дисциплина CI', '101', d.department_id, 1, 36, 'draft'
FROM study_plans sp
CROSS JOIN departments d
WHERE d.name = 'Департамент маркетинга'
  AND NOT EXISTS (SELECT 1 FROM plan_disciplines WHERE discipline_name = 'Тестовая дисциплина CI');

INSERT INTO faculty_members (full_name, department_id, is_active, position)
SELECT 'CI Seed Faculty', d.department_id, true, 'Преподаватель'
FROM departments d
WHERE d.name = 'Департамент маркетинга'
  AND NOT EXISTS (SELECT 1 FROM faculty_members WHERE full_name = 'CI Seed Faculty');

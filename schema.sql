-- Полная схема БД для приложения «ВШБ Нагрузка».
-- Порядок: справочники → планы → дисциплины → назначения → представление.
-- Выполнять на пустой БД или когда таблиц ещё нет (CREATE IF NOT EXISTS).
--
-- Порядок применения:
-- 1. schema.sql       — основные таблицы и представление v_workload_by_worktype
-- 2. add_auth_tables.sql — пользователи и доступ (или создаются приложением при старте)
-- 3. add_aud_total_columns.sql — доп. колонки в plan_disciplines (если уже есть старая схема)

-- ==================== Справочники ====================
CREATE TABLE IF NOT EXISTS departments (
  department_id SERIAL PRIMARY KEY,
  name VARCHAR(500) NOT NULL
);

CREATE TABLE IF NOT EXISTS educational_programs (
  op_id SERIAL PRIMARY KEY,
  name VARCHAR(500) NOT NULL,
  education_level VARCHAR(100),
  study_format VARCHAR(100),
  is_active BOOLEAN NOT NULL DEFAULT true
);

CREATE TABLE IF NOT EXISTS faculty_members (
  faculty_id SERIAL PRIMARY KEY,
  full_name VARCHAR(500) NOT NULL,
  department_id INT REFERENCES departments(department_id) ON DELETE SET NULL,
  is_active BOOLEAN NOT NULL DEFAULT true,
  position VARCHAR(200),
  rate NUMERIC,
  nrd_share NUMERIC,
  zero_out_total BOOLEAN,
  track VARCHAR(200),
  auditory_load NUMERIC,
  norm_hours NUMERIC,
  performed_load NUMERIC,
  responsible_load NUMERIC
);

CREATE TABLE IF NOT EXISTS work_types (
  work_type_id SERIAL PRIMARY KEY,
  name VARCHAR(200) NOT NULL
);

-- ==================== Учебные планы ====================
CREATE TABLE IF NOT EXISTS study_plans (
  plan_id SERIAL PRIMARY KEY,
  academic_year VARCHAR(50),
  direction VARCHAR(500),
  funding_type VARCHAR(200)
);

CREATE TABLE IF NOT EXISTS plan_modules (
  module_id SERIAL PRIMARY KEY,
  plan_id INT NOT NULL REFERENCES study_plans(plan_id) ON DELETE CASCADE,
  module_name VARCHAR(500),
  module_number INT
);

CREATE TABLE IF NOT EXISTS plan_programs (
  plan_program_id SERIAL PRIMARY KEY,
  plan_id INT NOT NULL REFERENCES study_plans(plan_id) ON DELETE CASCADE,
  op_id INT NOT NULL REFERENCES educational_programs(op_id) ON DELETE CASCADE,
  UNIQUE (plan_id, op_id)
);

-- ==================== Дисциплины плана ====================
CREATE TABLE IF NOT EXISTS plan_disciplines (
  plan_discipline_id SERIAL PRIMARY KEY,
  plan_id INT NOT NULL REFERENCES study_plans(plan_id) ON DELETE CASCADE,
  module_id INT REFERENCES plan_modules(module_id) ON DELETE SET NULL,
  discipline_no VARCHAR(100),
  discipline_name VARCHAR(1000) NOT NULL,
  implementing_department_id INT REFERENCES departments(department_id) ON DELETE SET NULL,
  implementing_dep_parent VARCHAR(200),
  course_no INT,
  discipline_kind VARCHAR(200),
  is_key_seminar BOOLEAN,
  has_online_course BOOLEAN,
  has_mu_request BOOLEAN,
  language VARCHAR(100),
  mkd VARCHAR(100),
  credits NUMERIC,
  rup_lectures_hours NUMERIC,
  rup_seminars_hours NUMERIC,
  rup_total_hours NUMERIC,
  hours_module1 NUMERIC,
  hours_module2 NUMERIC,
  hours_module3 NUMERIC,
  hours_module4 NUMERIC,
  streams_count INT,
  groups_count INT,
  students_count INT,
  current_control_hours NUMERIC,
  aud_lecture_hours NUMERIC,
  aud_seminar_hours NUMERIC,
  aud_nis_ps_sn_hours NUMERIC,
  aud_total_hours NUMERIC,
  total_hours NUMERIC
);

CREATE TABLE IF NOT EXISTS plan_discipline_programs (
  plan_discipline_id INT NOT NULL REFERENCES plan_disciplines(plan_discipline_id) ON DELETE CASCADE,
  plan_program_id INT NOT NULL REFERENCES plan_programs(plan_program_id) ON DELETE CASCADE,
  PRIMARY KEY (plan_discipline_id, plan_program_id)
);

-- ==================== Назначения и часы ====================
CREATE TABLE IF NOT EXISTS teaching_assignments (
  assignment_id SERIAL PRIMARY KEY,
  plan_discipline_id INT NOT NULL REFERENCES plan_disciplines(plan_discipline_id) ON DELETE CASCADE,
  faculty_id INT NOT NULL REFERENCES faculty_members(faculty_id) ON DELETE CASCADE,
  role VARCHAR(200)
);

CREATE TABLE IF NOT EXISTS assignment_hours (
  assignment_id INT NOT NULL REFERENCES teaching_assignments(assignment_id) ON DELETE CASCADE,
  work_type_id INT NOT NULL REFERENCES work_types(work_type_id) ON DELETE CASCADE,
  hours NUMERIC NOT NULL,
  PRIMARY KEY (assignment_id, work_type_id)
);

-- ==================== Представление нагрузки ====================
CREATE OR REPLACE VIEW v_workload_by_worktype AS
SELECT
  ta.assignment_id,
  pd.plan_discipline_id,
  ta.faculty_id,
  ah.work_type_id,
  ta.role,
  sp.academic_year,
  pm.module_number,
  pm.module_name,
  pd.discipline_no,
  pd.discipline_name,
  ep.name AS op_name,
  ep.op_id,
  ep.education_level,
  d.name AS department_name,
  wt.name AS work_type,
  ah.hours,
  fm.full_name AS faculty_name
FROM teaching_assignments ta
JOIN assignment_hours ah ON ah.assignment_id = ta.assignment_id
JOIN plan_disciplines pd ON ta.plan_discipline_id = pd.plan_discipline_id
JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
JOIN educational_programs ep ON pp.op_id = ep.op_id
JOIN study_plans sp ON pd.plan_id = sp.plan_id
LEFT JOIN plan_modules pm ON pd.module_id = pm.module_id
LEFT JOIN departments d ON pd.implementing_department_id = d.department_id
JOIN faculty_members fm ON ta.faculty_id = fm.faculty_id
JOIN work_types wt ON ah.work_type_id = wt.work_type_id;

-- ==================== Минимальные данные для старта ====================
-- Один учебный план (если нет ни одного)
INSERT INTO study_plans (academic_year)
SELECT '2024/2025'
WHERE NOT EXISTS (SELECT 1 FROM study_plans LIMIT 1);

-- Типы работ (если пусто)
INSERT INTO work_types (name) SELECT 'Лекции' WHERE NOT EXISTS (SELECT 1 FROM work_types WHERE name = 'Лекции');
INSERT INTO work_types (name) SELECT 'Семинары' WHERE NOT EXISTS (SELECT 1 FROM work_types WHERE name = 'Семинары');
INSERT INTO work_types (name) SELECT 'Текущий контроль' WHERE NOT EXISTS (SELECT 1 FROM work_types WHERE name = 'Текущий контроль');

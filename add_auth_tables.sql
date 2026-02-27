-- Таблицы для авторизации и разграничения доступа по ОП и департаментам.
-- Роли: Admin — полный доступ; AcademicDirector — план; DepartmentManager — нагрузка/ППС по департаменту; User — только назначенные ОП и департаменты.

CREATE TABLE IF NOT EXISTS app_users (
  user_id SERIAL PRIMARY KEY,
  login VARCHAR(100) NOT NULL UNIQUE,
  password_hash VARCHAR(200) NOT NULL,
  display_name VARCHAR(200),
  role VARCHAR(30) NOT NULL DEFAULT 'User' CHECK (role IN ('Admin', 'User', 'AcademicDirector', 'DepartmentManager')),
  created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);

-- При необходимости обновить проверку ролей на уже существующей таблице:
DO $$
BEGIN
  ALTER TABLE app_users DROP CONSTRAINT IF EXISTS app_users_role_check;
  ALTER TABLE app_users ADD CONSTRAINT app_users_role_check CHECK (role IN ('Admin', 'User', 'AcademicDirector', 'DepartmentManager'));
EXCEPTION WHEN OTHERS THEN NULL;
END $$;

CREATE TABLE IF NOT EXISTS user_allowed_ops (
  user_id INT NOT NULL REFERENCES app_users(user_id) ON DELETE CASCADE,
  op_name VARCHAR(500) NOT NULL,
  PRIMARY KEY (user_id, op_name)
);

CREATE TABLE IF NOT EXISTS user_allowed_departments (
  user_id INT NOT NULL REFERENCES app_users(user_id) ON DELETE CASCADE,
  department_id INT NOT NULL,
  PRIMARY KEY (user_id, department_id)
);

-- Ссылка на департаменты (если таблица departments уже есть)
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'departments') THEN
    ALTER TABLE user_allowed_departments
      DROP CONSTRAINT IF EXISTS user_allowed_departments_department_id_fkey,
      ADD CONSTRAINT user_allowed_departments_department_id_fkey
        FOREIGN KEY (department_id) REFERENCES departments(department_id) ON DELETE CASCADE;
  END IF;
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

-- Создать первого админа (логин: admin, пароль: admin) — после первого входа смените пароль.
-- Пароль хешируется в приложении при первом запуске, если таблица пуста.
INSERT INTO app_users (login, password_hash, display_name, role)
SELECT 'admin', 'CHANGE_ME', 'Администратор', 'Admin'
WHERE NOT EXISTS (SELECT 1 FROM app_users LIMIT 1);

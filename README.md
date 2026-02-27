# ВШБ Нагрузка

Веб-приложение для учёта учебной нагрузки и распределения по преподавателям (ВШБ).

## Запуск

1. **PostgreSQL** — создайте базу и укажите строку подключения в `appsettings.json` (ConnectionStrings:Default).
2. **Схема БД** — выполните скрипты в таком порядке:
   - `schema.sql` — основные таблицы и представление `v_workload_by_worktype`
   - `add_auth_tables.sql` — пользователи и доступ по ОП/департаментам (или создаются приложением при первом входе)
   - `add_aud_total_columns.sql` — дополнительные колонки в `plan_disciplines`, если таблица уже была создана ранее
3. Запустите приложение. При старте приложение обновляет представление `v_workload_by_worktype` (CREATE OR REPLACE VIEW).

## Что поменялось в проекте (обновление)

### База данных

- **schema.sql** (новый) — полная схема БД:
  - Справочники: `departments`, `educational_programs`, `faculty_members`, `work_types`
  - Планы: `study_plans`, `plan_modules`, `plan_programs`
  - Дисциплины: `plan_disciplines` (все поля, включая `aud_*`, `total_hours`), `plan_discipline_programs`
  - Нагрузка: `teaching_assignments`, `assignment_hours`
  - Представление: `v_workload_by_worktype`
  - Минимальные вставки: один учебный план, типы работ (Лекции, Семинары, Текущий контроль)

- **add_auth_tables.sql** (обновлён):
  - Роли: `Admin`, `User`, `AcademicDirector`, `DepartmentManager`
  - Обновление проверки ролей на существующей таблице `app_users`
  - Внешний ключ `user_allowed_departments.department_id` → `departments(department_id)` (если таблица `departments` уже есть)

- **add_aud_total_columns.sql** — без изменений; добавляет колонки в `plan_disciplines` при необходимости.

### Приложение (Program.cs)

- При старте вызывается **EnsureWorkloadView**: выполняется `CREATE OR REPLACE VIEW v_workload_by_worktype`, чтобы представление всегда соответствовало схеме. Если таблиц ещё нет, ошибка игнорируется — тогда нужно выполнить `schema.sql`.

### Документация

- **docs/Структура_БД_и_Excel_ВШБ.md** — соответствие листов Excel («ПРОЕКТ - ВШБ.Reg.xlsx») и таблиц/полей БД, как должна работать система.

## Развёртывание на внутреннем сервере ВШЭ

### Что нужно на сервере

1. **.NET 10 Runtime** (или SDK, если собираете на сервере) — [загрузка](https://dotnet.microsoft.com/download).
2. **PostgreSQL** — сервер БД (можно на том же сервере или отдельном).
3. Доступ по сети к серверу (порты приложения и при необходимости 5432 к PostgreSQL).

### Шаги переноса

**1. Публикация приложения (у себя на ПК)**

```bash
cd WebApplication1
dotnet publish -c Release -o ./publish
```

В папке `publish` будут файлы приложения (DLL, wwwroot, appsettings.json). Перенесите эту папку на сервер (архив, общая папка, RDP и т.п.).

**2. База данных на сервере**

- Установите PostgreSQL (если ещё нет).
- Создайте базу, например: `createdb -U postgres workloaddb`.
- Выполните скрипты в порядке:
  - `schema.sql`
  - `add_auth_tables.sql`
  - при необходимости `add_aud_total_columns.sql`

**3. Настройка на сервере**

В папке с приложением отредактируйте **appsettings.json** (или задайте переменные окружения):

- **ConnectionStrings:Default** — строка подключения к PostgreSQL на сервере, например:
  - `Host=localhost;Port=5432;Database=workloaddb;Username=ваш_пользователь;Password=пароль`
  - или `Host=имя_сервера_бд.вшэ.рф;Port=5432;Database=workloaddb;...` если БД на другом хосте.

Для Production задайте среду:

- В **Windows** (PowerShell):  
  `$env:ASPNETCORE_ENVIRONMENT="Production"`
- В **Linux** или в systemd:  
  `ASPNETCORE_ENVIRONMENT=Production`
- В **appsettings.Production.json** (если создадите) можно переопределить только строку подключения, не храня пароль в основном appsettings.

**4. Запуск**

- **Вручную (проверка):**  
  `dotnet WebApplication1.dll`  
  По умолчанию приложение слушает http://localhost:5000 (или порт из `ASPNETCORE_URLS`).

- **Как служба Windows:**  
  Установите как Windows Service (например, через `sc.exe` или NSSM), укажите рабочую папку = папка с приложением, исполняемый файл = `dotnet.exe`, аргументы = `WebApplication1.dll`.

- **Под IIS (Windows):**  
  Установите Hosting Bundle для .NET, создайте пул приложений (без управляемого кода), сайт с указанием папки publish и при необходимости настройте URL (порт, привязку, обратный прокси с основного сайта ВШЭ).

- **Linux (systemd):**  
  Создайте unit-файл с `ExecStart=/usr/bin/dotnet /путь/к/publish/WebApplication1.dll`, `WorkingDirectory`, `ASPNETCORE_ENVIRONMENT=Production` и при необходимости `ASPNETCORE_URLS=http://0.0.0.0:5000`.

**5. Доступ с других машин**

- Либо задайте `ASPNETCORE_URLS=http://0.0.0.0:5240` (или нужный порт), чтобы слушать все интерфейсы.
- Либо поставьте перед приложением обратный прокси (IIS, nginx) с основного домена ВШЭ (например, `workload.вшэ.рф`) и при необходимости HTTPS.

**6. Первый вход**

- В среде Production маршрут `/login/reset-dev` **недоступен** (возвращает 404).
- Варианты создания первого администратора:
  - Один раз запустить приложение в **Development** (например, локально с подключением к серверной БД), открыть `/login/reset-dev` — создастся admin/admin; затем сменить пароль в интерфейсе и перевести приложение в Production.
  - Либо добавить запись в таблицу `app_users` вручную (логин, хеш пароля через AuthHelpers.HashPassword).
- Остальных пользователей создаёт администратор в разделе «Пользователи».

Кратко: собрали `dotnet publish` → перенесли папку на сервер → настроили БД и строку подключения → выставили Production → запустили как службу или под IIS/nginx.

## Роли

- **Admin** — полный доступ.
- **AcademicDirector** — учебный план, дисциплины.
- **DepartmentManager** — нагрузка, ППС по своим департаментам.
- **User** — просмотр по назначенным ОП и департаментам.

## Разделы

- Нагрузка — фильтры по ОП, курсу, департаменту, дисциплине; таблица по типам работ.
- Учебный план — дисциплины по плану и ОП.
- Дисциплины — справочник дисциплин.
- ППС — преподаватели, должности, департаменты, нагрузка.
- Пользователи (Admin) — учётные записи, привязка к ОП и департаментам.

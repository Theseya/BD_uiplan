# HANDOVER — краткий контекст проекта

Используйте этот файл при новом чате или переносе проекта, чтобы не опираться на длинную историю переписки.

## Стек и структура

- **ASP.NET Core** (Minimal API), **.NET 10**, **PostgreSQL** (Npgsql, `NpgsqlDataSource`).
- Вся логика в одном файле: **Program.cs** (~5700 строк). Доп. классы: **ParseHelpers**, **AuthHelpers**, **PlanRowReader**.
- Фронт: серверный HTML (Layout, формы, таблицы), **wwwroot/js/site.js**, **wwwroot/css/site.css**.
- Тесты: **WebApplication1.Tests** (xUnit, `WebApplicationFactory<Program>`, коллекция `WebAppCollection`).

## Основные разделы

- **Авторизация:** `/login`, `/logout` — Cookie, роли (Admin / по ОП и департаментам).
- **Главная:** `/` — сводная статистика.
- **Нагрузка:** `/uiworkload`, save-batch-form, import, export — распределение часов, нераспределённая нагрузка по дисциплине, автосохранение при изменении часов.
- **Учебный план:** `/uiplan`, update, import, export.
- **Преподаватели:** `/uifaculty`, save-batch-form, import, export.
- **Дисциплины:** `/uidisciplines`, save-batch-form, import, export.
- **ОП:** `/uiops`; **Админка:** `/admin/users`; **БД:** `/uidb`, `/uidbtable`.

## Сборка и тесты

- Перед `dotnet build` или тестами **остановить** процесс WebApplication1 (иначе блокируется exe).
- Тесты: `dotnet test WebApplication1.Tests\WebApplication1.Tests.csproj`. В xunit.runner.json — без параллелизма (меньше памяти).

## Стабильность Cursor / Electron (краши, OOM)

- **OOM** — в основном проблема самой IDE (объём памяти, индексация, длинные чаты). Решения: новый чат + HANDOVER.md, удаление старых чатов, откат Cursor до версии до 2.3.35 при известной утечке в новых версиях. Ошибки `[otel.error]`/OTLPExporter — со стороны Cursor/расширений, не проекта.
- **Частые краши окна (Electron):** иногда помогает **обновление видеодрайвера** до последней стабильной версии от производителя GPU (NVIDIA/AMD/Intel). Для OOM это не главный фактор, но может снизить падения самого окна.

-- Добавить колонки общей нагрузки в plan_disciplines (если их ещё нет).
-- Выполнить один раз в БД проекта.

ALTER TABLE plan_disciplines
  ADD COLUMN IF NOT EXISTS aud_lecture_hours NUMERIC,
  ADD COLUMN IF NOT EXISTS aud_seminar_hours NUMERIC,
  ADD COLUMN IF NOT EXISTS aud_nis_ps_sn_hours NUMERIC,
  ADD COLUMN IF NOT EXISTS aud_total_hours NUMERIC,
  ADD COLUMN IF NOT EXISTS total_hours NUMERIC;

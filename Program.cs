using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using WebApplication1;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics", LogLevel.Information);
builder.Services.AddControllers();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddSingleton(sp =>
{
    var cs = builder.Configuration.GetConnectionString("Default")
             ?? throw new InvalidOperationException("Connection string 'Default' not found.");
    return NpgsqlDataSource.Create(cs);
});

var app = builder.Build();

// Глобальная обработка необработанных исключений: логирование и ответ пользователю
app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        IExceptionHandlerPathFeature? feature = null;
        Exception? ex = null;
        try
        {
            feature = context.Features.Get<IExceptionHandlerPathFeature>();
            ex = feature?.Error;
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            if (ex != null)
            {
                if (ex is OperationCanceledException)
                    logger.LogWarning("Запрос отменён: {Path}", context.Request.Path);
                else
                    logger.LogError(ex, "Необработанное исключение при запросе {Method} {Path}", context.Request.Method, context.Request.Path);
            }
        }
        catch { /* логирование могло упасть */ }

        try
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            var env = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var accept = context.Request.Headers.Accept.ToString();
            bool wantsJson = accept.Contains("application/json", StringComparison.OrdinalIgnoreCase);

            if (wantsJson)
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                var msg = env.IsDevelopment() && ex != null ? (ex.Message ?? ex.ToString()) : "Произошла ошибка. Попробуйте позже.";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = msg }));
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            var userMessage = env.IsDevelopment() && ex != null
                ? ParseHelpers.H(ex?.Message ?? ex?.ToString() ?? "Ошибка")
                : "Произошла ошибка. Попробуйте позже или обратитесь к администратору.";
            var hint = (ex?.Message?.Contains("Reading as", StringComparison.OrdinalIgnoreCase) == true || ex?.Message?.Contains("DataTypeName", StringComparison.OrdinalIgnoreCase) == true)
                ? "<p class='hint'>Перезапустите приложение (остановите процесс и снова запустите). Если ошибка остаётся — откройте эту же страницу после перезапуска и напишите, какую вкладку открывали.</p>"
                : "";
            var body = $"<section class='card'><h2>Ошибка</h2><p class='error'>{userMessage}</p>{hint}<p><a href='/'>На главную</a></p></section>";
            var html = Layout("Ошибка", "dashboard", body, null, false);
            await context.Response.WriteAsync(html);
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Произошла ошибка.");
        }
    });
});

string[] defaultDepartmentNames = new[]
{
    "Департамент бизнес-информатики",
    "Департамент операционного менеджмента и логистики",
    "Департамент организационного поведения и управления человеческими ресурсами",
    "Департамент стратегического и международного менеджмента",
    "Департамент финансового менеджмента",
    "Департамент маркетинга"
};

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Требовать авторизацию для всех страниц, кроме логина и статики
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/login") ||
        context.Request.Path.StartsWithSegments("/css") ||
        context.Request.Path.StartsWithSegments("/js") ||
        context.Request.Path.Value == "/favicon.ico")
    {
        await next();
        return;
    }
    if (!context.User.Identity?.IsAuthenticated ?? true)
    {
        context.Response.Redirect("/login?returnUrl=" + Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
        return;
    }
    await next();
});

// ==================== Auth helpers ====================
static async Task<UserInfo?> GetCurrentUser(NpgsqlDataSource ds, ClaimsPrincipal user)
{
    if (user?.Identity?.IsAuthenticated != true) return null;
    var login = user.FindFirst(ClaimTypes.Name)?.Value ?? user.FindFirst("login")?.Value;
    if (string.IsNullOrEmpty(login)) return null;
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("SELECT user_id, login, COALESCE(display_name,''), role FROM app_users WHERE login = @login", conn);
    cmd.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return null;
    var userId = PlanRowReader.SafeReadInt32(r, 0);
    var displayName = r.GetString(2);
    var role = r.GetString(3)?.Trim() ?? "";
    await r.CloseAsync();
    var opNames = new List<string>();
    var deptIds = new List<int>();
    if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
    {
        await using var cmdOps = new NpgsqlCommand("SELECT op_name FROM user_allowed_ops WHERE user_id = @uid", conn);
        cmdOps.Parameters.AddWithValue("uid", NpgsqlDbType.Integer, userId);
        await using var ro = await cmdOps.ExecuteReaderAsync();
        while (await ro.ReadAsync()) opNames.Add(ro.GetString(0));
        await ro.CloseAsync();
        await using var cmdDepts = new NpgsqlCommand("SELECT department_id FROM user_allowed_departments WHERE user_id = @uid", conn);
        cmdDepts.Parameters.AddWithValue("uid", NpgsqlDbType.Integer, userId);
        await using var rd = await cmdDepts.ExecuteReaderAsync();
        while (await rd.ReadAsync()) deptIds.Add(PlanRowReader.SafeReadInt32(rd, 0));
    }
    return new UserInfo(userId, login, displayName, role, opNames.ToArray(), deptIds.ToArray());
}

// ==================== Helpers (see AuthHelpers.cs, ParseHelpers.cs) ====================
string[] OpMagistracy =
{
    "Бизнес-аналитика и системы больших данных",
    "Бизнес-информатика: цифровое предприятие и управление информационными системами",
    "Маркетинг - менеджмент",
    "Маркетинг: цифровые технологии и маркетинговые коммуникации",
    "Международный менеджмент",
    "Менеджмент в ритейле",
    "Управление B2C-бизнесом: технологии и инновации",
    "Производственные системы и операционная эффективность",
    "Операционная эффективность и производственные системы",
    "Стратегический менеджмент и консалтинг",
    "Стратегический менеджмент: инвестиции и консалтинг",
    "Управление людьми: цифровые технологии и организационное развитие",
    "Управление устойчивым развитием компании",
    "Управление цифровым продуктом",
    "Электронный бизнес и цифровые инновации",
    "Управление продуктом в ИТ-бизнесе"
};

string[] OpBachelor =
{
    "Бизнес-информатика",
    "Маркетинг и рыночная аналитика",
    "Международный бизнес",
    "Управление бизнесом",
    "Логистика и управление цепями поставок",
    "Управление цепями поставок и бизнес-аналитика",
    "Цифровые инновации в управлении предприятием",
    "Управление цифровым продуктом",
    "Технологии анализа данных в бизнесе"
};

async Task<(int created, string? error)> SeedOpsAsync(NpgsqlDataSource ds, IEnumerable<string> ops)
{
    var opList = ops.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
    if (opList.Length == 0) return (0, null);

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    await using var planCmd = new NpgsqlCommand(
        "SELECT plan_id FROM study_plans ORDER BY plan_id LIMIT 1",
        conn, tx);
    var planObj = await planCmd.ExecuteScalarAsync();
    if (planObj is not int planId) return (0, "No study plans");

    await using var facultyCmd = new NpgsqlCommand(
        "SELECT faculty_id FROM faculty_members WHERE is_active = true ORDER BY full_name LIMIT 1",
        conn, tx);
    var facultyIdObj = await facultyCmd.ExecuteScalarAsync();
    if (facultyIdObj is not int facultyId) return (0, "No active faculty");

    await using var workTypeCmd = new NpgsqlCommand(
        "SELECT work_type_id FROM work_types ORDER BY work_type_id LIMIT 1",
        conn, tx);
    var workTypeObj = await workTypeCmd.ExecuteScalarAsync();
    if (workTypeObj is not int workTypeId) return (0, "No work types");

    int created = 0;
    int index = 0;
    int moduleId;
    await using (var modCmd = new NpgsqlCommand(
        "SELECT module_id FROM plan_modules WHERE plan_id = @planId LIMIT 1",
        conn, tx))
    {
        modCmd.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planId);
        var modObj = await modCmd.ExecuteScalarAsync();
        if (modObj is int existingMod)
        {
            moduleId = existingMod;
        }
        else
        {
            await using var insMod = new NpgsqlCommand(@"
INSERT INTO plan_modules (plan_id, module_name, module_number)
VALUES (@planId, 'Модуль 1', 1)
RETURNING module_id;", conn, tx);
            insMod.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planId);
            moduleId = (int)(await insMod.ExecuteScalarAsync() ?? 0);
        }
    }

    foreach (var op in opList)
    {
        index++;
        var level = OpMagistracy.Contains(op) ? "магистратура" : "бакалавриат";
        int opId;
        await using (var opCmd = new NpgsqlCommand(
            "SELECT op_id FROM educational_programs WHERE name = @name",
            conn, tx))
        {
            opCmd.Parameters.AddWithValue("name", NpgsqlDbType.Text, op);
            var opObj = await opCmd.ExecuteScalarAsync();
            if (opObj is int existingOp)
            {
                opId = existingOp;
            }
            else
            {
                await using var insOp = new NpgsqlCommand(@"
INSERT INTO educational_programs (name, education_level, study_format, is_active)
VALUES (@name, @level, NULL, true)
RETURNING op_id;", conn, tx);
                insOp.Parameters.AddWithValue("name", NpgsqlDbType.Text, op);
                insOp.Parameters.AddWithValue("level", NpgsqlDbType.Text, level);
                opId = (int)(await insOp.ExecuteScalarAsync() ?? 0);
            }
        }

        int planProgramId;
        await using (var ppCmd = new NpgsqlCommand(
            "SELECT plan_program_id FROM plan_programs WHERE plan_id = @planId AND op_id = @opId",
            conn, tx))
        {
            ppCmd.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planId);
            ppCmd.Parameters.AddWithValue("opId", NpgsqlDbType.Integer, opId);
            var ppObj = await ppCmd.ExecuteScalarAsync();
            if (ppObj is int existingPp)
            {
                planProgramId = existingPp;
            }
            else
            {
                await using var insPp = new NpgsqlCommand(@"
INSERT INTO plan_programs (plan_id, op_id)
VALUES (@planId, @opId)
RETURNING plan_program_id;", conn, tx);
                insPp.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planId);
                insPp.Parameters.AddWithValue("opId", NpgsqlDbType.Integer, opId);
                planProgramId = (int)(await insPp.ExecuteScalarAsync() ?? 0);
            }
        }

        int? departmentId = null;
        await using (var deptCmd = new NpgsqlCommand(
            "SELECT department_id FROM departments ORDER BY department_id LIMIT 1",
            conn, tx))
        {
            var deptObj = await deptCmd.ExecuteScalarAsync();
            if (deptObj is int dId) departmentId = dId;
        }
        if (departmentId is null)
        {
            await using var insDept = new NpgsqlCommand(@"
INSERT INTO departments (name) VALUES ('Департамент маркетинга')
RETURNING department_id;", conn, tx);
            departmentId = (int)(await insDept.ExecuteScalarAsync() ?? 0);
        }

        int planDisciplineId;
        await using (var findPd = new NpgsqlCommand(@"
SELECT pd.plan_discipline_id
FROM plan_disciplines pd
JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
WHERE pdp.plan_program_id = @ppId
LIMIT 1;", conn, tx))
        {
            findPd.Parameters.AddWithValue("ppId", NpgsqlDbType.Integer, planProgramId);
            var pdObj = await findPd.ExecuteScalarAsync();
            if (pdObj is int existingPd)
            {
                planDisciplineId = existingPd;
            }
            else
            {
                var discNo = $"T-{index:000}-{DateTime.UtcNow:HHmmss}";
                await using var insDisc = new NpgsqlCommand(@"
INSERT INTO plan_disciplines (
    plan_id, module_id, discipline_no, discipline_name, implementing_department_id,
    course_no,
    implementing_dep_parent, discipline_kind, is_key_seminar, has_online_course,
    has_mu_request, language, mkd, credits, rup_lectures_hours, rup_seminars_hours,
    rup_total_hours, hours_module1, hours_module2, hours_module3, hours_module4,
    streams_count, groups_count, students_count, current_control_hours
)
VALUES (
    @planId, @moduleId, @discNo, @discName, @depId, 1,
    @depParent, @discKind, false, true,
    false, @language, @mkd, @credits, @rupLect, @rupSem,
    @rupTotal, @h1, @h2, @h3, @h4,
    @streams, @groups, @students, @current
)
RETURNING plan_discipline_id;", conn, tx);
                insDisc.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planId);
                insDisc.Parameters.AddWithValue("moduleId", NpgsqlDbType.Integer, moduleId);
                insDisc.Parameters.AddWithValue("discNo", NpgsqlDbType.Text, discNo);
                insDisc.Parameters.AddWithValue("discName", NpgsqlDbType.Text, $"Тестовая дисциплина {op}");
                insDisc.Parameters.AddWithValue("depId", NpgsqlDbType.Integer, departmentId.Value);
                insDisc.Parameters.AddWithValue("depParent", NpgsqlDbType.Text, "ВШБ");
                insDisc.Parameters.AddWithValue("discKind", NpgsqlDbType.Text, "обязательная");
                insDisc.Parameters.AddWithValue("language", NpgsqlDbType.Text, "русский");
                insDisc.Parameters.AddWithValue("mkd", NpgsqlDbType.Text, "МКД-1");
                insDisc.Parameters.AddWithValue("credits", NpgsqlDbType.Numeric, 3);
                insDisc.Parameters.AddWithValue("rupLect", NpgsqlDbType.Numeric, 12);
                insDisc.Parameters.AddWithValue("rupSem", NpgsqlDbType.Numeric, 12);
                insDisc.Parameters.AddWithValue("rupTotal", NpgsqlDbType.Numeric, 24);
                insDisc.Parameters.AddWithValue("h1", NpgsqlDbType.Numeric, 6);
                insDisc.Parameters.AddWithValue("h2", NpgsqlDbType.Numeric, 6);
                insDisc.Parameters.AddWithValue("h3", NpgsqlDbType.Numeric, 6);
                insDisc.Parameters.AddWithValue("h4", NpgsqlDbType.Numeric, 6);
                insDisc.Parameters.AddWithValue("streams", NpgsqlDbType.Integer, 1);
                insDisc.Parameters.AddWithValue("groups", NpgsqlDbType.Integer, 2);
                insDisc.Parameters.AddWithValue("students", NpgsqlDbType.Integer, 40);
                insDisc.Parameters.AddWithValue("current", NpgsqlDbType.Numeric, 4);
                planDisciplineId = (int)(await insDisc.ExecuteScalarAsync() ?? 0);

                await using var linkCmd = new NpgsqlCommand(@"
INSERT INTO plan_discipline_programs (plan_discipline_id, plan_program_id)
VALUES (@pdId, @ppId);", conn, tx);
                linkCmd.Parameters.AddWithValue("pdId", NpgsqlDbType.Integer, planDisciplineId);
                linkCmd.Parameters.AddWithValue("ppId", NpgsqlDbType.Integer, planProgramId);
                await linkCmd.ExecuteNonQueryAsync();
            }
        }

        await using (var ensureModule = new NpgsqlCommand(@"
UPDATE plan_disciplines
SET module_id = COALESCE(module_id, @moduleId)
WHERE plan_discipline_id = @pdId;", conn, tx))
        {
            ensureModule.Parameters.AddWithValue("moduleId", NpgsqlDbType.Integer, moduleId);
            ensureModule.Parameters.AddWithValue("pdId", NpgsqlDbType.Integer, planDisciplineId);
            await ensureModule.ExecuteNonQueryAsync();
        }

        await using var existingCmd = new NpgsqlCommand(@"
SELECT assignment_id
FROM teaching_assignments
WHERE plan_discipline_id = @planId AND faculty_id = @facultyId
LIMIT 1;", conn, tx);
        existingCmd.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planDisciplineId);
        existingCmd.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, facultyId);
        var existingObj = await existingCmd.ExecuteScalarAsync();
        int assignmentId;
        if (existingObj is int existingId)
        {
            assignmentId = existingId;
        }
        else
        {
            await using var insAssign = new NpgsqlCommand(@"
INSERT INTO teaching_assignments (plan_discipline_id, faculty_id, role)
VALUES (@planId, @facultyId, 'Преподаватель')
RETURNING assignment_id;", conn, tx);
            insAssign.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planDisciplineId);
            insAssign.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, facultyId);
            assignmentId = (int)(await insAssign.ExecuteScalarAsync() ?? 0);
        }

        await using var upsertHours = new NpgsqlCommand(@"
INSERT INTO assignment_hours (assignment_id, work_type_id, hours)
VALUES (@assignmentId, @workTypeId, @hours)
ON CONFLICT (assignment_id, work_type_id) DO UPDATE
SET hours = EXCLUDED.hours;", conn, tx);
        upsertHours.Parameters.AddWithValue("assignmentId", NpgsqlDbType.Integer, assignmentId);
        upsertHours.Parameters.AddWithValue("workTypeId", NpgsqlDbType.Integer, workTypeId);
        upsertHours.Parameters.AddWithValue("hours", NpgsqlDbType.Numeric, 8 + (index % 6) * 2);
        await upsertHours.ExecuteNonQueryAsync();
        created++;
    }

    await tx.CommitAsync();
    return (created, null);
}

string RenderOpCheckboxes(string[] selected, string[]? allowedOpNames = null)
{
    string[] mag, bach;
    if (allowedOpNames is null || allowedOpNames.Length == 0)
    {
        mag = OpMagistracy;
        bach = OpBachelor;
    }
    else
    {
        var allowedSet = new HashSet<string>(allowedOpNames, StringComparer.OrdinalIgnoreCase);
        mag = OpMagistracy.Where(o => allowedSet.Contains(o)).ToArray();
        bach = OpBachelor.Where(o => allowedSet.Contains(o)).ToArray();
        if (mag.Length == 0 && bach.Length == 0)
            return "<div class=\"op-checkboxes\"><div class=\"op-group__title\">ОП</div><p class=\"hint\">Нет доступных ОП</p></div>";
    }
    var set = new HashSet<string>(selected ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    var sb = new StringBuilder();
    void AppendGroup(string title, string[] ops)
    {
        if (ops.Length == 0) return;
        sb.Append("<div class=\"op-group\"><div class=\"op-group__title\">").Append(ParseHelpers.H(title)).Append("</div><div class=\"op-grid\">");
        foreach (var op in ops)
        {
            sb.Append("<label class=\"check\"><input type=\"checkbox\" name=\"opName\" value=\"").Append(ParseHelpers.H(op)).Append("\"");
            if (set.Contains(op)) sb.Append(" checked");
            sb.Append(">").Append(ParseHelpers.H(op)).Append("</label>");
        }
        sb.Append("</div></div>");
    }
    sb.Append("<div class=\"op-checkboxes\">");
    AppendGroup("Магистратура", mag);
    AppendGroup("Бакалавриат", bach);
    sb.Append("</div>");
    return sb.ToString();
}

static int[] ParseModuleNos(string[]? moduleNo)
{
    if (moduleNo is null || moduleNo.Length == 0) return Array.Empty<int>();
    var list = new List<int>();
    foreach (var s in moduleNo)
        if (int.TryParse(s?.Trim(), out var n) && n >= 1 && n <= 4 && !list.Contains(n))
            list.Add(n);
    return list.OrderBy(x => x).ToArray();
}

string RenderModuleCheckboxes(int[] selected)
{
    var set = new HashSet<int>(selected ?? Array.Empty<int>());
    var sb = new StringBuilder();
    sb.Append("<div class=\"module-checkboxes\"><div class=\"op-group__title\">Модуль</div><div class=\"op-grid\">");
    for (int i = 1; i <= 4; i++)
    {
        sb.Append("<label class=\"check\"><input type=\"checkbox\" name=\"moduleNo\" value=\"").Append(i).Append("\"");
        if (set.Contains(i)) sb.Append(" checked");
        sb.Append(">").Append(i).Append(" модуль</label>");
    }
    sb.Append("</div></div>");
    return sb.ToString();
}

static int[] ParseDepartmentIds(string[]? departmentId)
{
    if (departmentId is null || departmentId.Length == 0) return Array.Empty<int>();
    var list = new List<int>();
    foreach (var s in departmentId)
        if (int.TryParse(s?.Trim(), out var n) && n > 0 && !list.Contains(n))
            list.Add(n);
    return list.OrderBy(x => x).ToArray();
}

string RenderDepartmentCheckboxes(List<(int id, string name)> departments, int[] selectedIds, int[]? allowedDepartmentIds = null)
{
    var list = departments.AsEnumerable();
    if (allowedDepartmentIds is not null && allowedDepartmentIds.Length > 0)
    {
        var allowedSet = new HashSet<int>(allowedDepartmentIds);
        list = list.Where(d => allowedSet.Contains(d.id));
    }
    var set = new HashSet<int>(selectedIds ?? Array.Empty<int>());
    var sb = new StringBuilder();
    sb.Append("<div class=\"department-checkboxes\"><div class=\"op-group__title\">Департамент</div><div class=\"op-grid\">");
    foreach (var (id, name) in list)
    {
        sb.Append("<label class=\"check\"><input type=\"checkbox\" name=\"departmentId\" value=\"").Append(id).Append("\"");
        if (set.Contains(id)) sb.Append(" checked");
        sb.Append(">").Append(ParseHelpers.H(name)).Append("</label>");
    }
    sb.Append("</div></div>");
    return sb.ToString();
}

static string Layout(string title, string active, string body, string? userLogin = null, bool isAdmin = false)
{
    var userBlock = string.IsNullOrEmpty(userLogin) ? "" : $@"
      <div class=""sidebar__user"">
        <span class=""sidebar__user-name"">{ParseHelpers.H(userLogin)}</span>
        <form method=""post"" action=""/logout"" class=""sidebar__logout""><button type=""submit"" class=""btn btn--ghost btn--sm"">Выход</button></form>
      </div>";
    var adminLink = isAdmin ? @"<a class=""nav__link " + (active == "admin" ? "is-active" : "") + @""" href=""/admin/users"">Пользователи</a>" : "";
    return $@"
<!doctype html>
<html lang=""ru"">
<head>
  <meta charset=""utf-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
  <title>{ParseHelpers.H(title)}</title>
  <link rel=""icon"" type=""image/png"" href=""/favicon.png"">
  <link rel=""stylesheet"" href=""/css/hse.css?v=3"">
  <link rel=""stylesheet"" href=""/css/site.css?v=30"">
</head>
<body>
  <div class=""app"">
    <aside class=""sidebar"">
      <a href=""/"" class=""brand brand--link"">
        <div class=""brand__mark"">HSE</div>
        <div class=""brand__title"">ВШБ Нагрузка</div>
      </a>
      <nav class=""nav nav--side"">
        <a class=""nav__link {(active == "dashboard" ? "is-active" : "")}"" href=""/"">Главная</a>
        <a class=""nav__link {(active == "workload" ? "is-active" : "")}"" href=""/uiworkload"">Нагрузка</a>
        <a class=""nav__link {(active == "plan" ? "is-active" : "")}"" href=""/uiplan"">Учебный план</a>
        <a class=""nav__link {(active == "disciplines" ? "is-active" : "")}"" href=""/uidisciplines"">Дисциплины</a>
        <a class=""nav__link {(active == "faculty" ? "is-active" : "")}"" href=""/uifaculty"">ППС</a>
        {adminLink}
      </nav>
      {userBlock}
    </aside>
    <main class=""main"">
      <div class=""container"">
        {body}
      </div>
    </main>
  </div>
  <script src=""/js/site.js?v=23""></script>
</body>
</html>
";
}

static async Task EnsureDefaultDepartments(NpgsqlDataSource ds, string[] departmentNames)
{
    await using var conn = await ds.OpenConnectionAsync();
    foreach (var name in departmentNames)
    {
        await using var check = new NpgsqlCommand("SELECT 1 FROM departments WHERE name = @name LIMIT 1", conn);
        check.Parameters.AddWithValue("name", NpgsqlDbType.Text, name);
        if (await check.ExecuteScalarAsync() is null)
        {
            await using var ins = new NpgsqlCommand("INSERT INTO departments (name) VALUES (@name)", conn);
            ins.Parameters.AddWithValue("name", NpgsqlDbType.Text, name);
            await ins.ExecuteNonQueryAsync();
        }
    }
}

static async Task<List<(int id, string name)>> LoadIdNameList(NpgsqlDataSource ds, string sql)
{
    var list = new List<(int, string)>();
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(sql, conn);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        list.Add((PlanRowReader.SafeReadInt32(r, 0), r.IsDBNull(1) ? "" : (r.GetString(1) ?? "")));
    return list;
}

async Task<List<(int id, string name)>> LoadDepartments(NpgsqlDataSource ds)
{
    await EnsureDefaultDepartments(ds, defaultDepartmentNames);
    return await LoadIdNameList(ds, "SELECT department_id, name FROM departments ORDER BY name");
}

static Task<List<(int id, string name)>> LoadFaculty(NpgsqlDataSource ds) =>
    LoadIdNameList(ds, "SELECT faculty_id, full_name FROM faculty_members WHERE is_active = true ORDER BY full_name");
static Task<List<(int id, string name)>> LoadWorkTypes(NpgsqlDataSource ds) =>
    LoadIdNameList(ds, "SELECT work_type_id, name FROM work_types ORDER BY name");

/// <summary>Формирует фрагмент ORDER BY только из разрешённых колонок (columnMap/defaultColumn). Пользовательский ввод в SQL не подставляется — защита от инъекций.</summary>
static string BuildOrderBy(string? sortBy, string? sortOrder, IReadOnlyDictionary<string, string> columnMap, string defaultColumn)
{
    var dir = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase) ? " DESC" : " ASC";
    var col = columnMap.TryGetValue((sortBy ?? "").ToLowerInvariant(), out var c) ? c : defaultColumn;
    return col.Contains(" DESC") ? col : col + dir;
}

static string GetWorkloadOrderBy(string? sortBy, string? sortOrder) =>
    BuildOrderBy(sortBy, sortOrder, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "discipline_name", ["faculty"] = "faculty_name", ["hours"] = "hours",
        ["op"] = "op_name", ["type"] = "work_type", ["newest"] = "assignment_id DESC"
    }, "op_name, course_no, discipline_no, discipline_name, work_type");
static string GetPlanOrderBy(string? sortBy, string? sortOrder) =>
    BuildOrderBy(sortBy, sortOrder, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["no"] = "pd.discipline_no", ["course"] = "pd.course_no", ["module"] = "pm.module_name",
        ["department"] = "d.name", ["newest"] = "pd.plan_discipline_id DESC"
    }, "pd.discipline_name");
static string GetDisciplinesOrderBy(string? sortBy, string? sortOrder) =>
    BuildOrderBy(sortBy, sortOrder, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["no"] = "pd.discipline_no", ["course"] = "pd.course_no", ["department"] = "d.name",
        ["credits"] = "pd.credits", ["newest"] = "pd.plan_discipline_id DESC"
    }, "pd.discipline_name");
static string GetFacultyOrderBy(string? sortBy, string? sortOrder) =>
    BuildOrderBy(sortBy, sortOrder, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["department"] = "d.name", ["position"] = "fm.position", ["newest"] = "fm.faculty_id DESC"
    }, "fm.full_name");

/// <summary>Для импорта нагрузки: найти или создать plan_discipline по году, названию и опционально ОП/департаменту. Каждый запрос — новая команда.</summary>
static async Task<(int? planDisciplineId, string? error)> GetOrCreatePlanDisciplineForImport(
    NpgsqlConnection conn, NpgsqlTransaction tx,
    string yearNorm, string yearAlt, string? disciplineNo, string disciplineName, string? opName, string? departmentName)
{
    object Db(string? s) => string.IsNullOrWhiteSpace(s) ? DBNull.Value : s.Trim();

    // 1) Поиск по полному совпадению (год, №, название, ОП, департамент)
    await using (var cmd = new NpgsqlCommand(@"
SELECT pd.plan_discipline_id FROM plan_disciplines pd
JOIN study_plans sp ON pd.plan_id = sp.plan_id
JOIN plan_discipline_programs pdp ON pdp.plan_discipline_id = pd.plan_discipline_id
JOIN plan_programs pp ON pp.plan_program_id = pdp.plan_program_id
JOIN educational_programs ep ON ep.op_id = pp.op_id
LEFT JOIN departments d ON pd.implementing_department_id = d.department_id
WHERE (sp.academic_year = @y OR sp.academic_year = @y_alt)
  AND (COALESCE(@no,'') = '' OR TRIM(COALESCE(pd.discipline_no,''))::text = TRIM(COALESCE(@no,'')))
  AND LOWER(TRIM(pd.discipline_name)) = LOWER(TRIM(@name))
  AND (COALESCE(@op,'') = '' OR ep.name ILIKE '%' || TRIM(COALESCE(@op,'')) || '%')
  AND (COALESCE(@dept,'') = '' OR d.name ILIKE TRIM(COALESCE(@dept,'')))
LIMIT 1", conn, tx))
    {
        cmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, yearNorm);
        cmd.Parameters.AddWithValue("y_alt", NpgsqlDbType.Text, yearAlt);
        cmd.Parameters.AddWithValue("no", NpgsqlDbType.Text, Db(disciplineNo));
        cmd.Parameters.AddWithValue("name", NpgsqlDbType.Text, disciplineName);
        cmd.Parameters.AddWithValue("op", NpgsqlDbType.Text, Db(opName));
        cmd.Parameters.AddWithValue("dept", NpgsqlDbType.Text, Db(departmentName));
        var v = await cmd.ExecuteScalarAsync();
        if (v is int id1) return (id1, null);
    }

    // 2) Поиск только по году и названию
    await using (var cmd = new NpgsqlCommand(@"
SELECT pd.plan_discipline_id FROM plan_disciplines pd
JOIN study_plans sp ON pd.plan_id = sp.plan_id
WHERE (sp.academic_year = @y OR sp.academic_year = @y_alt)
  AND LOWER(TRIM(pd.discipline_name)) = LOWER(TRIM(@name))
ORDER BY pd.plan_discipline_id LIMIT 1", conn, tx))
    {
        cmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, yearNorm);
        cmd.Parameters.AddWithValue("y_alt", NpgsqlDbType.Text, yearAlt);
        cmd.Parameters.AddWithValue("name", NpgsqlDbType.Text, disciplineName);
        var v = await cmd.ExecuteScalarAsync();
        if (v is int id2) return (id2, null);
    }

    // 3) Создание: план → департамент → ОП → plan_program → plan_discipline → plan_discipline_programs
    try
    {
        int planId;
        await using (var cmd = new NpgsqlCommand("SELECT plan_id FROM study_plans WHERE academic_year = @y OR academic_year = @y2 LIMIT 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, yearAlt);
            cmd.Parameters.AddWithValue("y2", NpgsqlDbType.Text, yearNorm);
            var p = await cmd.ExecuteScalarAsync();
            if (p is int pid) planId = pid;
            else
            {
                await using var ins = new NpgsqlCommand("INSERT INTO study_plans (academic_year) VALUES (@y) RETURNING plan_id", conn, tx);
                ins.Parameters.AddWithValue("y", NpgsqlDbType.Text, yearAlt);
                planId = (int)(await ins.ExecuteScalarAsync() ?? 0);
            }
        }

        int? depId = null;
        if (!string.IsNullOrWhiteSpace(departmentName))
        {
            await using var cmd = new NpgsqlCommand("SELECT department_id FROM departments WHERE LOWER(TRIM(name)) = LOWER(TRIM(@n)) LIMIT 1", conn, tx);
            cmd.Parameters.AddWithValue("n", NpgsqlDbType.Text, departmentName.Trim());
            var d = await cmd.ExecuteScalarAsync();
            if (d is int did) depId = did;
            else
            {
                await using var ins = new NpgsqlCommand("INSERT INTO departments (name) VALUES (@n) RETURNING department_id", conn, tx);
                ins.Parameters.AddWithValue("n", NpgsqlDbType.Text, departmentName.Trim());
                depId = (int)(await ins.ExecuteScalarAsync() ?? 0);
            }
        }

        int opId;
        await using (var cmd = new NpgsqlCommand("SELECT op_id FROM educational_programs WHERE TRIM(@op) = '' OR name ILIKE '%' || TRIM(@op) || '%' LIMIT 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("op", NpgsqlDbType.Text, opName ?? "");
            var o = await cmd.ExecuteScalarAsync();
            if (o is int oid) opId = oid;
            else
            {
                await using var first = new NpgsqlCommand("SELECT op_id FROM educational_programs LIMIT 1", conn, tx);
                var f = await first.ExecuteScalarAsync();
                if (f is int fid) opId = fid;
                else
                {
                    await using var ins = new NpgsqlCommand("INSERT INTO educational_programs (name) VALUES (@n) RETURNING op_id", conn, tx);
                    ins.Parameters.AddWithValue("n", NpgsqlDbType.Text, "Импорт");
                    opId = (int)(await ins.ExecuteScalarAsync() ?? 0);
                }
            }
        }

        int planProgramId;
        await using (var cmd = new NpgsqlCommand("SELECT plan_program_id FROM plan_programs WHERE plan_id = @pid AND op_id = @oid LIMIT 1", conn, tx))
        {
            cmd.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, planId);
            cmd.Parameters.AddWithValue("oid", NpgsqlDbType.Integer, opId);
            var pp = await cmd.ExecuteScalarAsync();
            if (pp is int ppid) planProgramId = ppid;
            else
            {
                await using var ins = new NpgsqlCommand("INSERT INTO plan_programs (plan_id, op_id) VALUES (@pid, @oid) RETURNING plan_program_id", conn, tx);
                ins.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, planId);
                ins.Parameters.AddWithValue("oid", NpgsqlDbType.Integer, opId);
                planProgramId = (int)(await ins.ExecuteScalarAsync() ?? 0);
            }
        }

        int planDisciplineId;
        await using (var ins = new NpgsqlCommand(@"
INSERT INTO plan_disciplines (plan_id, discipline_no, discipline_name, implementing_department_id)
VALUES (@planId, @no, @name, @depId) RETURNING plan_discipline_id", conn, tx))
        {
            ins.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planId);
            ins.Parameters.AddWithValue("no", NpgsqlDbType.Text, Db(disciplineNo));
            ins.Parameters.AddWithValue("name", NpgsqlDbType.Text, disciplineName.Trim());
            ins.Parameters.AddWithValue("depId", NpgsqlDbType.Integer, depId ?? (object)DBNull.Value);
            planDisciplineId = (int)(await ins.ExecuteScalarAsync() ?? 0);
        }

        await using (var ins = new NpgsqlCommand("INSERT INTO plan_discipline_programs (plan_discipline_id, plan_program_id) VALUES (@pdid, @ppid)", conn, tx))
        {
            ins.Parameters.AddWithValue("pdid", NpgsqlDbType.Integer, planDisciplineId);
            ins.Parameters.AddWithValue("ppid", NpgsqlDbType.Integer, planProgramId);
            await ins.ExecuteNonQueryAsync();
        }
        return (planDisciplineId, null);
    }
    catch (Exception ex)
    {
        var msg = (ex.InnerException?.Message ?? ex.Message).Trim();
        if (msg.Length > 250) msg = msg.Substring(0, 247) + "...";
        return (null, msg);
    }
}

static Task<List<(int id, string name)>> LoadModules(NpgsqlDataSource ds) =>
    LoadIdNameList(ds, "SELECT module_id, module_name FROM plan_modules ORDER BY module_name");

static string OptionsList(List<(int id, string name)> opts, int? sel, string empty)
{
    var sb = new StringBuilder();
    sb.Append("<option value=\"\">").Append(ParseHelpers.H(empty)).Append("</option>");
    foreach (var (id, name) in opts)
    {
        sb.Append("<option value=\"").Append(id).Append("\"");
        if (sel == id) sb.Append(" selected");
        sb.Append(">").Append(ParseHelpers.H(name)).Append("</option>");
    }
    return sb.ToString();
}

static string OptionsListStrings(IEnumerable<string> opts, string? sel, string empty)
{
    var sb = new StringBuilder();
    sb.Append("<option value=\"\">").Append(ParseHelpers.H(empty)).Append("</option>");
    foreach (var name in opts)
    {
        sb.Append("<option value=\"").Append(ParseHelpers.H(name)).Append("\"");
        if (!string.IsNullOrWhiteSpace(sel) && string.Equals(sel, name, StringComparison.OrdinalIgnoreCase)) sb.Append(" selected");
        sb.Append(">").Append(ParseHelpers.H(name)).Append("</option>");
    }
    return sb.ToString();
}

// ==================== Routes ====================
app.MapGet("/login", (HttpContext ctx, IWebHostEnvironment env, string? returnUrl, string? error, string? first, string? reset) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true)
        return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/uiworkload" : returnUrl);
    var url = string.IsNullOrEmpty(returnUrl) ? "" : $"<input type=\"hidden\" name=\"returnUrl\" value=\"{ParseHelpers.H(returnUrl)}\">";
    var msg = error == "1" ? "<p class='error'>Неверный логин или пароль.</p><p class='hint'>Сбросить пароль: <a href=\"/login/reset-dev\">установить логин admin, пароль admin</a></p>" : "";
    if (first == "admin") msg = "<p class='hint'>Создан первый пользователь. Войдите: логин <strong>admin</strong>, пароль <strong>admin</strong>. Смените пароль в разделе Пользователи.</p>";
    if (reset == "1") msg = "<p class='hint'>Пароль пользователя admin сброшен на <strong>admin</strong>. Войдите и смените пароль в разделе Пользователи.</p>";
    if (reset == "0") msg = "<p class='error'>Пользователь admin не найден в базе.</p>";
    var resetLink = env.IsDevelopment() ? "<p class='hint' style='margin-top:1rem'><a href=\"/login/reset-dev\">Сбросить пароль admin на «admin»</a></p>" : "";
    var html = $@"<!doctype html><html lang=""ru""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1""><title>Вход</title>
  <link rel=""icon"" type=""image/png"" href=""/favicon.png"">
  <link rel=""stylesheet"" href=""/css/hse.css?v=3""><link rel=""stylesheet"" href=""/css/site.css?v=20""></head><body>
  <div class=""app""><main class=""main""><div class=""container"">
  <section class=""card"" style=""max-width:360px;margin:2rem auto;"">
    <h1 class=""page-title"">Вход</h1>
    {msg}
    <form method=""post"" action=""/login"" class=""login-form"">
      <div class=""login-form__field"">
        <label class=""label"">Логин</label>
        <input class=""input"" type=""text"" name=""login"" required autofocus>
      </div>
      <div class=""login-form__field"">
        <label class=""label"">Пароль</label>
        <input class=""input"" type=""password"" name=""password"" required>
      </div>
      {url}
      <div class=""login-form__actions"">
        <button class=""btn"" type=""submit"">Войти</button>
      </div>
    </form>
    {resetLink}
  </section></div></main></div></body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

// Сброс пароля: привести логин к "admin" и установить пароль "admin" (только Development)
app.MapGet("/login/reset-dev", async (NpgsqlDataSource ds, IWebHostEnvironment env) =>
{
    if (!env.IsDevelopment())
        return Results.NotFound();
    try
    {
        await EnsureAuthTables(ds);
        var hash = AuthHelpers.HashPassword("admin");
        await using var conn = await ds.OpenConnectionAsync();
        await using (var cmdAdmin = new NpgsqlCommand("SELECT user_id FROM app_users WHERE login = 'admin'", conn))
        {
            var userIdObj = await cmdAdmin.ExecuteScalarAsync();
            if (userIdObj is int uid1)
            {
                await SetPassword(ds, uid1, "admin");
                return Results.Redirect("/login?reset=1");
            }
        }
        await using var sel2 = new NpgsqlCommand("SELECT user_id FROM app_users WHERE LOWER(TRIM(login)) = 'admin' LIMIT 1", conn);
        var uid2 = await sel2.ExecuteScalarAsync();
        if (uid2 is int id)
        {
            await using var upd = new NpgsqlCommand("UPDATE app_users SET login = 'admin', password_hash = @hash WHERE user_id = @id", conn);
            upd.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
            upd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
            await upd.ExecuteNonQueryAsync();
            return Results.Redirect("/login?reset=1");
        }
        await using var ins = new NpgsqlCommand("INSERT INTO app_users (login, password_hash, display_name, role) VALUES ('admin', @hash, 'Администратор', 'Admin') ON CONFLICT (login) DO UPDATE SET password_hash = EXCLUDED.password_hash", conn);
        ins.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
        await ins.ExecuteNonQueryAsync();
        return Results.Redirect("/login?reset=1");
    }
    catch (NpgsqlException)
    {
        return Results.Redirect("/login?reset=0");
    }
});

app.MapPost("/login", async (NpgsqlDataSource ds, HttpContext ctx, IFormCollection form) =>
{
    var loginVal = form["login"].ToString().Trim();
    var passwordVal = form["password"].ToString() ?? "";
    var returnUrl = form["returnUrl"].ToString();
    if (string.IsNullOrEmpty(loginVal) || string.IsNullOrEmpty(passwordVal))
        return Results.Redirect("/login?error=1");
    try
    {
        await EnsureAuthTables(ds);
        if (loginVal == "admin" && passwordVal == "admin")
        {
            await using (var c = await ds.OpenConnectionAsync())
            await using (var cmdAdmin = new NpgsqlCommand("SELECT user_id FROM app_users WHERE login = 'admin'", c))
            {
                var uidObj = await cmdAdmin.ExecuteScalarAsync();
                if (uidObj is int uid)
                    await SetPassword(ds, uid, "admin");
                else
                    await CreateFirstAdmin(ds, "admin", "admin");
            }
            var claims = new List<Claim> { new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Admin"), new("login", "admin") };
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });
            return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/uiworkload" : returnUrl);
        }
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT user_id, login, password_hash, COALESCE(display_name,''), role FROM app_users WHERE login = @login", conn);
        cmd.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, loginVal);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            var anyUser = await HasAnyUser(ds);
            if (!anyUser)
            {
                await r.CloseAsync();
                await CreateFirstAdmin(ds, "admin", "admin");
                if (loginVal == "admin" && passwordVal == "admin")
                {
                    var claims = new List<Claim> { new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Admin"), new("login", "admin") };
                    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });
                    return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/uiworkload" : returnUrl);
                }
                return Results.Redirect("/login?first=admin");
            }
            return Results.Redirect("/login?error=1");
        }
        var userId = PlanRowReader.SafeReadInt32(r, 0);
        var storedHash = r.GetString(2);
        var role = r.GetString(4);
        await r.CloseAsync();
        if (storedHash == "CHANGE_ME") { await SetPassword(ds, userId, passwordVal); storedHash = (await GetPasswordHash(ds, userId)) ?? ""; }
        if (!AuthHelpers.VerifyPassword(passwordVal, storedHash))
            return Results.Redirect("/login?error=1");
        var claimsList = new List<Claim> { new(ClaimTypes.Name, loginVal), new(ClaimTypes.Role, role), new("login", loginVal) };
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claimsList, CookieAuthenticationDefaults.AuthenticationScheme)), new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });
        return Results.Redirect(string.IsNullOrEmpty(returnUrl) ? "/uiworkload" : returnUrl);
    }
    catch (NpgsqlException)
    {
        return Results.Redirect("/login?error=1");
    }
}).DisableAntiforgery();

static async Task EnsureAuthTables(NpgsqlDataSource ds)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(@"
CREATE TABLE IF NOT EXISTS app_users (
  user_id SERIAL PRIMARY KEY,
  login VARCHAR(100) NOT NULL UNIQUE,
  password_hash VARCHAR(200) NOT NULL,
  display_name VARCHAR(200),
  role VARCHAR(30) NOT NULL DEFAULT 'User' CHECK (role IN ('Admin', 'User', 'AcademicDirector', 'DepartmentManager')),
  created_at TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS user_allowed_ops (
  user_id INT NOT NULL REFERENCES app_users(user_id) ON DELETE CASCADE,
  op_name VARCHAR(500) NOT NULL,
  PRIMARY KEY (user_id, op_name)
);
CREATE TABLE IF NOT EXISTS user_allowed_departments (
  user_id INT NOT NULL REFERENCES app_users(user_id) ON DELETE CASCADE,
  department_id INT NOT NULL,
  PRIMARY KEY (user_id, department_id)
);", conn);
    await cmd.ExecuteNonQueryAsync();
    try
    {
        await using var dropC = new NpgsqlCommand("ALTER TABLE app_users DROP CONSTRAINT IF EXISTS app_users_role_check", conn);
        await dropC.ExecuteNonQueryAsync();
    }
    catch { }
    try
    {
        await using var addC = new NpgsqlCommand("ALTER TABLE app_users ADD CONSTRAINT app_users_role_check CHECK (role IN ('Admin', 'User', 'AcademicDirector', 'DepartmentManager'))", conn);
        await addC.ExecuteNonQueryAsync();
    }
    catch { /* constraint may already exist */ }
}

static async Task EnsureWorkloadView(NpgsqlDataSource ds)
{
    const string sql = @"
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
  fm.full_name AS faculty_name,
  NULL::int AS course_no
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
JOIN work_types wt ON ah.work_type_id = wt.work_type_id;";
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* таблицы могут ещё не существовать — тогда выполните schema.sql */ }
}

static async Task EnsureEmploymentTypeColumn(NpgsqlDataSource ds)
{
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("ALTER TABLE faculty_members ADD COLUMN IF NOT EXISTS employment_type VARCHAR(50)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* таблица может ещё не существовать */ }
}

static async Task<bool> HasAnyUser(NpgsqlDataSource ds)
{
    try
    {
        await using var c = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM app_users LIMIT 1", c);
        return (await cmd.ExecuteScalarAsync()) != null;
    }
    catch { return false; }
}

static async Task CreateFirstAdmin(NpgsqlDataSource ds, string login, string password)
{
    await using var conn = await ds.OpenConnectionAsync();
    var hash = AuthHelpers.HashPassword(password);
    await using var cmd = new NpgsqlCommand("INSERT INTO app_users (login, password_hash, display_name, role) VALUES (@login, @hash, 'Администратор', 'Admin') ON CONFLICT (login) DO UPDATE SET password_hash = EXCLUDED.password_hash", conn);
    cmd.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
    cmd.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, hash);
    await cmd.ExecuteNonQueryAsync();
}

static async Task SetPassword(NpgsqlDataSource ds, int userId, string password)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("UPDATE app_users SET password_hash = @hash WHERE user_id = @id", conn);
    cmd.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, AuthHelpers.HashPassword(password));
    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, userId);
    await cmd.ExecuteNonQueryAsync();
}

static async Task<string?> GetPasswordHash(NpgsqlDataSource ds, int userId)
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("SELECT password_hash FROM app_users WHERE user_id = @id", conn);
    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, userId);
    var o = await cmd.ExecuteScalarAsync();
    return o?.ToString();
}

/// <summary>Приводит результат COUNT(*) (bigint → long/int в C#) к int.</summary>
static int ScalarCountToInt(object? o)
{
    if (o is null or DBNull) return 0;
    if (o is long l) return (int)l;
    if (o is int i) return i;
    try { return Convert.ToInt32(o); } catch { return 0; }
}

app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/", async (HttpContext ctx, NpgsqlDataSource ds) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");

    await using var conn = await ds.OpenConnectionAsync();

    int workloadRows = 0, disciplinesCount = 0, facultyInWorkload = 0;
    decimal totalHours = 0;
    await using (var cmd = new NpgsqlCommand(@"
SELECT COUNT(*), COUNT(DISTINCT discipline_name), COUNT(DISTINCT faculty_id), COALESCE(SUM(hours), 0)
FROM v_workload_by_worktype", conn))
    await using (var r = await cmd.ExecuteReaderAsync())
    {
        if (await r.ReadAsync())
        {
            workloadRows = PlanRowReader.SafeReadInt32(r, 0);
            disciplinesCount = PlanRowReader.SafeReadInt32(r, 1);
            facultyInWorkload = PlanRowReader.SafeReadInt32(r, 2);
            totalHours = PlanRowReader.SafeReadDecimal(r, 3);
        }
    }

    var hoursByWorkType = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    await using (var cmd = new NpgsqlCommand(@"
SELECT wt.name, COALESCE(SUM(ah.hours), 0)
FROM assignment_hours ah
JOIN work_types wt ON ah.work_type_id = wt.work_type_id
GROUP BY wt.name", conn))
    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            var name = r.IsDBNull(0) ? "" : r.GetString(0);
            var hours = PlanRowReader.SafeReadDecimal(r, 1);
            if (!string.IsNullOrEmpty(name)) hoursByWorkType[name] = hours;
        }
    }

    decimal lecturesHours = 0, seminarsHours = 0, nisHours = 0;
    foreach (var kv in hoursByWorkType)
    {
        var k = kv.Key;
        if (k.IndexOf("лекц", StringComparison.OrdinalIgnoreCase) >= 0) lecturesHours += kv.Value;
        else if (k.IndexOf("семинар", StringComparison.OrdinalIgnoreCase) >= 0) seminarsHours += kv.Value;
        else if (k.IndexOf("НИС", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("Научно-исследовательск", StringComparison.OrdinalIgnoreCase) >= 0
            || k.IndexOf("Текущий контроль", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("ПС", StringComparison.OrdinalIgnoreCase) >= 0 || k.IndexOf("СН", StringComparison.OrdinalIgnoreCase) >= 0)
            nisHours += kv.Value;
    }

    int facultyTotal = 0, facultyStaff = 0, facultyGph = 0;
    await using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM faculty_members WHERE is_active = true", conn))
    {
        facultyTotal = ScalarCountToInt(await cmd.ExecuteScalarAsync());
    }
    try
    {
        await using (var cmd = new NpgsqlCommand("SELECT COALESCE(employment_type, 'штат'), COUNT(*) FROM faculty_members WHERE is_active = true GROUP BY COALESCE(employment_type, 'штат')", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var typ = r.IsDBNull(0) ? "штат" : (r.GetString(0) ?? "штат").Trim();
                var cnt = PlanRowReader.SafeReadInt32(r, 1);
                if (typ.IndexOf("ГПХ", StringComparison.OrdinalIgnoreCase) >= 0 || typ.Equals("гпх", StringComparison.OrdinalIgnoreCase))
                    facultyGph += cnt;
                else
                    facultyStaff += cnt;
            }
        }
    }
    catch { facultyStaff = facultyTotal; }

    int disciplinesInCatalog = 0;
    await using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM plan_disciplines", conn))
    {
        disciplinesInCatalog = ScalarCountToInt(await cmd.ExecuteScalarAsync());
    }
    int opsCount = 0;
    await using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM educational_programs WHERE is_active = true", conn))
    {
        opsCount = ScalarCountToInt(await cmd.ExecuteScalarAsync());
    }
    int deptCount = 0;
    await using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM departments", conn))
    {
        deptCount = ScalarCountToInt(await cmd.ExecuteScalarAsync());
    }

    var sb = new StringBuilder();
    sb.Append("<section class='page-header page-header--dashboard'>")
      .Append("<div class='dashboard-hero'>")
      .Append("<div class='dashboard-hero__icon' aria-hidden='true'>📊</div>")
      .Append("<div>")
      .Append("<h1 class='page-title'>Главная</h1>")
      .Append("<div class='page-subtitle'>Сводная статистика по нагрузке, преподавателям и справочникам</div>")
      .Append("</div>")
      .Append("</div>")
      .Append("</section>");

    sb.Append("<section class='card'><h2 class='h2 dashboard-section-title'><span class='dashboard-section-icon' aria-hidden='true'>📋</span> Нагрузка</h2>")
      .Append("<div class='stats-grid'>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon'>📄</div><span class='stat-badge stat-badge--green'>Строки</span></div><div class='stat-value'>").Append(workloadRows).Append("</div><div class='stat-label'>Всего строк в распределении</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--purple'>📚</div><span class='stat-badge stat-badge--blue'>Дисциплины</span></div><div class='stat-value'>").Append(disciplinesCount).Append("</div><div class='stat-label'>Дисциплин в нагрузке</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--green'>👤</div><span class='stat-badge stat-badge--purple'>ППС</span></div><div class='stat-value'>").Append(facultyInWorkload).Append("</div><div class='stat-label'>Преподавателей в нагрузке</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--orange'>⏱️</div><span class='stat-badge stat-badge--orange'>Часы</span></div><div class='stat-value'>").Append(totalHours.ToString("0.##", CultureInfo.InvariantCulture)).Append("</div><div class='stat-label'>Всего часов</div></div>")
      .Append("</div>")
      .Append("<p class='hint'><a href='/uiworkload' class='link'>Перейти к разделу Нагрузка →</a></p>")
      .Append("</section>");

    sb.Append("<section class='card'><h2 class='h2 dashboard-section-title'><span class='dashboard-section-icon' aria-hidden='true'>🎓</span> Часы по видам работ</h2>")
      .Append("<div class='stats-grid'>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon'>📖</div><span class='stat-badge stat-badge--green'>Лекции</span></div><div class='stat-value'>").Append(lecturesHours.ToString("0.##", CultureInfo.InvariantCulture)).Append("</div><div class='stat-label'>Часов лекций</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--purple'>💬</div><span class='stat-badge stat-badge--blue'>Семинары</span></div><div class='stat-value'>").Append(seminarsHours.ToString("0.##", CultureInfo.InvariantCulture)).Append("</div><div class='stat-label'>Часов семинаров</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--green'>🔬</div><span class='stat-badge stat-badge--purple'>НИС / прочее</span></div><div class='stat-value'>").Append(nisHours.ToString("0.##", CultureInfo.InvariantCulture)).Append("</div><div class='stat-label'>Часов (НИС, текущий контроль и прочее)</div></div>")
      .Append("</div>")
      .Append("</section>");

    sb.Append("<section class='card'><h2 class='h2 dashboard-section-title'><span class='dashboard-section-icon' aria-hidden='true'>👥</span> Преподаватели (ППС)</h2>")
      .Append("<div class='stats-grid'>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon'>👥</div><span class='stat-badge stat-badge--green'>Всего</span></div><div class='stat-value'>").Append(facultyTotal).Append("</div><div class='stat-label'>Активных преподавателей</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--purple'>🏢</div><span class='stat-badge stat-badge--blue'>В штате</span></div><div class='stat-value'>").Append(facultyStaff).Append("</div><div class='stat-label'>Преподавателей в штате</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--orange'>📝</div><span class='stat-badge stat-badge--orange'>На ГПХ</span></div><div class='stat-value'>").Append(facultyGph).Append("</div><div class='stat-label'>Преподавателей на ГПХ</div></div>")
      .Append("</div>")
      .Append("<p class='hint'>Укажите тип занятости (штат / ГПХ) в разделе <a href='/uifaculty' class='link'>ППС</a>.</p>")
      .Append("<p class='hint'><a href='/uifaculty' class='link'>Перейти к разделу ППС →</a></p>")
      .Append("</section>");

    sb.Append("<section class='card'><h2 class='h2 dashboard-section-title'><span class='dashboard-section-icon' aria-hidden='true'>📁</span> Справочники</h2>")
      .Append("<div class='stats-grid'>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon'>🎓</div><span class='stat-badge'>ОП</span></div><div class='stat-value'>").Append(opsCount).Append("</div><div class='stat-label'>Образовательных программ</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--purple'>📚</div><span class='stat-badge'>Дисциплины</span></div><div class='stat-value'>").Append(disciplinesInCatalog).Append("</div><div class='stat-label'>В учебном плане</div></div>")
      .Append("<div class='stat-card'><div class='stat-head'><div class='stat-icon stat-icon--green'>🏛️</div><span class='stat-badge'>Департаменты</span></div><div class='stat-value'>").Append(deptCount).Append("</div><div class='stat-label'>Департаментов</div></div>")
      .Append("</div>")
      .Append("<p class='hint'><a href='/uiplan' class='link'>Учебный план →</a> <a href='/uidisciplines' class='link'>Дисциплины →</a></p>")
      .Append("</section>");

    return Results.Content(Layout("Главная", "dashboard", sb.ToString(), user.Login, user.IsAdmin), "text/html; charset=utf-8");
});

app.MapGet("/uiworkload", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    string? year,
    string[]? opName,
    string? courseNo,
    string? level,
    string? qSubject,
    string[]? departmentId,
    string[]? moduleNo,
    string? sortBy,
    string? sortOrder,
    string? exportEmpty,
    string? importError,
    string? saveError
) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    var redirectQuery = ctx.Request.QueryString.Value?.TrimStart('?') ?? "";
    var opNames = (opName ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
    var opNamesLike = opNames.Length == 0 ? null : opNames.Select(n => $"%{n}%").ToArray();
    var courseNoValue = ParseHelpers.IntOrNull(courseNo);
    var moduleNos = ParseModuleNos(moduleNo);
    var faculty = await LoadFaculty(ds);
    var departments = await LoadDepartments(ds);
    var departmentIds = ParseDepartmentIds(departmentId);
    var departmentNames = departmentIds.Length == 0 ? null : departments.Where(d => departmentIds.Contains(d.id)).Select(d => d.name).ToArray();
    var workTypes = await LoadWorkTypes(ds);

    await using var conn = await ds.OpenConnectionAsync();
    await using var statsCmd = new NpgsqlCommand(@"
SELECT
  COUNT(*) AS total_rows,
  COUNT(DISTINCT discipline_name) AS disciplines,
  COUNT(DISTINCT faculty_id) AS faculty,
  SUM(hours) AS total_hours
FROM v_workload_by_worktype
WHERE assignment_id IS NOT NULL
  AND (@y IS NULL OR academic_year = @y) AND
  (@opNames IS NULL OR op_name ILIKE ANY(@opNames)) AND
  (@course IS NULL OR course_no = @course) AND
  (@lvl IS NULL OR education_level ILIKE @lvl) AND
  (@qs IS NULL OR discipline_name ILIKE @qs) AND
  (@depNames IS NULL OR array_length(@depNames, 1) IS NULL OR department_name = ANY(@depNames)) AND
  (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR module_number = ANY(@moduleNos));
", conn);
    await using var cmd = new NpgsqlCommand(@"
SELECT
  assignment_id,
  plan_discipline_id,
  faculty_id,
  work_type_id,
  role,
  academic_year,
  module_number,
  module_name,
  discipline_no,
  discipline_name,
  op_name,
  education_level,
  department_name,
  work_type,
  hours,
  faculty_name
FROM v_workload_by_worktype
WHERE assignment_id IS NOT NULL
  AND (@y IS NULL OR academic_year = @y) AND
  (@opNames IS NULL OR op_name ILIKE ANY(@opNames)) AND
  (@course IS NULL OR course_no = @course) AND
  (@lvl IS NULL OR education_level ILIKE @lvl) AND
  (@qs IS NULL OR discipline_name ILIKE @qs) AND
  (@depNames IS NULL OR array_length(@depNames, 1) IS NULL OR department_name = ANY(@depNames)) AND
  (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR module_number = ANY(@moduleNos))
ORDER BY " + GetWorkloadOrderBy(sortBy, sortOrder) + @"
LIMIT 500;
", conn);

    var moduleNosParam = moduleNos.Length == 0 ? (object?)null : moduleNos;
    statsCmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)year ?? DBNull.Value);
    statsCmd.Parameters.AddWithValue("opNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)opNamesLike ?? DBNull.Value);
    statsCmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
    statsCmd.Parameters.AddWithValue("lvl", NpgsqlDbType.Text, (object?)(string.IsNullOrWhiteSpace(level) ? null : $"%{level}%") ?? DBNull.Value);
    statsCmd.Parameters.AddWithValue("qs", NpgsqlDbType.Text, (object?)(string.IsNullOrWhiteSpace(qSubject) ? null : $"%{qSubject}%") ?? DBNull.Value);
    statsCmd.Parameters.AddWithValue("depNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)departmentNames ?? DBNull.Value);
    statsCmd.Parameters.AddWithValue("moduleNos", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)moduleNosParam ?? DBNull.Value);

    cmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)year ?? DBNull.Value);
    cmd.Parameters.AddWithValue("opNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)opNamesLike ?? DBNull.Value);
    cmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
    cmd.Parameters.AddWithValue("lvl", NpgsqlDbType.Text, (object?)(string.IsNullOrWhiteSpace(level) ? null : $"%{level}%") ?? DBNull.Value);
    cmd.Parameters.AddWithValue("qs", NpgsqlDbType.Text, (object?)(string.IsNullOrWhiteSpace(qSubject) ? null : $"%{qSubject}%") ?? DBNull.Value);
    cmd.Parameters.AddWithValue("depNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)departmentNames ?? DBNull.Value);
    cmd.Parameters.AddWithValue("moduleNos", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)moduleNosParam ?? DBNull.Value);

    var sb = new StringBuilder();
    sb.Append("<section class='page-header'>")
      .Append("<div>")
      .Append("<h1 class='page-title'>Нагрузка</h1>")
      .Append("<div class='page-subtitle'>Управление нагрузкой и распределением</div>")
      .Append("</div>")
      .Append("</section>");
    if (exportEmpty == "1")
      sb.Append("<section class='card'><p class='hint'>По выбранным фильтрам нет данных для выгрузки в Excel.</p></section>");
    if (!string.IsNullOrEmpty(importError))
      sb.Append("<section class='card'><p class='").Append(importError == "noFile" ? "hint" : "error").Append("'>")
        .Append(importError == "noFile" ? "Выберите файл Excel для загрузки." : ParseHelpers.H(importError))
        .Append("</p></section>");
    if (!string.IsNullOrEmpty(saveError))
      sb.Append("<section class='card'><p class='error'>").Append(ParseHelpers.H(saveError).Replace("\n", "<br>"))
        .Append("</p><p><a href='/uiworkload'>Вернуться к таблице</a></p></section>");

    var opQuery = opNames.Length == 0
        ? ""
        : string.Join("&", opNames.Select(n => $"opName={WebUtility.UrlEncode(n)}"));
    var moduleQuery = moduleNos.Length == 0 ? "" : string.Join("&", moduleNos.Select(n => $"moduleNo={n}"));
    var departmentQuery = departmentIds.Length == 0 ? "" : string.Join("&", departmentIds.Select(n => $"departmentId={n}"));
    var workloadExportUrl = $"/uiworkload/export?year={WebUtility.UrlEncode(year ?? "")}" +
                            (string.IsNullOrWhiteSpace(opQuery) ? "" : $"&{opQuery}") +
                            $"&courseNo={(courseNoValue?.ToString() ?? "")}" +
                            $"&level={WebUtility.UrlEncode(level ?? "")}" +
                            $"&qSubject={WebUtility.UrlEncode(qSubject ?? "")}" +
                            (string.IsNullOrWhiteSpace(departmentQuery) ? "" : $"&{departmentQuery}") +
                            (string.IsNullOrWhiteSpace(moduleQuery) ? "" : $"&{moduleQuery}");

    sb.Append("<dialog class=\"modal\" id=\"workload-filter\">")
      .Append("<form method=\"get\" action=\"/uiworkload\" class=\"form form--row\">")
      .Append("<div class=\"input-group\"><label class=\"input-group__label\">Учебный год</label>")
      .Append("<select class=\"input\" name=\"year\" aria-label=\"Учебный год\">").Append(SelectOptions.AcademicYearOptions(year)).Append("</select></div>")
      .Append(RenderOpCheckboxes(opNames, null))
      .Append(RenderModuleCheckboxes(moduleNos))
      .Append("<input class=\"input\" type=\"number\" name=\"courseNo\" placeholder=\"Курс\" value=\"").Append(courseNoValue?.ToString() ?? "").Append("\">")
      .Append("<div class=\"input-group\"><label class=\"input-group__label\">Уровень</label>")
      .Append("<select class=\"input\" name=\"level\">").Append(OptionsListStrings(SelectOptions.EducationLevels, level, "Все")).Append("</select></div>")
      .Append("<input class=\"input\" type=\"text\" name=\"qSubject\" placeholder=\"Дисциплина\" value=\"").Append(ParseHelpers.H(qSubject)).Append("\">")
      .Append(RenderDepartmentCheckboxes(departments, departmentIds, null))
      .Append("<input type='hidden' name='sortBy' value='").Append(ParseHelpers.H(sortBy ?? "name")).Append("'>")
      .Append("<input type='hidden' name='sortOrder' value='").Append(ParseHelpers.H(sortOrder ?? "asc")).Append("'>")
      .Append("<div class=\"modal__actions\">")
      .Append("<button class=\"btn\" type=\"submit\">Применить</button>")
      .Append("<button class=\"btn btn--ghost\" type=\"button\" data-dialog-close>Закрыть</button>")
      .Append("</div>")
      .Append("</form>")
      .Append("</dialog>");

    sb.Append("<section class=\"card\">")
      .Append("<div class=\"toolbar\">");
    if (user.CanEditWorkload)
    {
      sb.Append("<button class='btn' type='submit' form='wl-batch-form'>Сохранить все</button>")
        .Append("<button class='btn btn--danger' type='button' data-delete-selected='wl'>Удалить выбранные</button>")
        .Append("<button class='btn btn--ghost' type='button' data-wl-add>Добавить строку</button>");
    }
    else
      sb.Append("<span class=\"hint\">Только просмотр. Редактирование: менеджер департамента или администратор.</span>");
    sb.Append("<button class=\"btn btn--ghost\" type=\"button\" data-dialog-open=\"#workload-filter\">Фильтр</button>")
      .Append("<button class=\"btn btn--ghost\" type=\"button\" onclick=\"window.location.href='/uiworkload'\">Сброс</button>");
    var wlSortBase = "/uiworkload?" + (string.IsNullOrEmpty(redirectQuery) ? "" : redirectQuery + "&");
    sb.Append("<span class=\"toolbar-sort\"><label class=\"hint\" style=\"margin-right:6px\">Сортировка:</label>")
      .Append("<select class=\"input input--sm\" style=\"width:auto;display:inline-block\" onchange=\"window.location=this.value\" aria-label=\"Сортировка\">")
      .Append("<option value=\"").Append(wlSortBase).Append("sortBy=name&sortOrder=asc\"").Append((sortBy ?? "name") == "name" && sortOrder != "desc" ? " selected" : "").Append(">По дисциплине А–Я</option>")
      .Append("<option value=\"").Append(wlSortBase).Append("sortBy=name&sortOrder=desc\"").Append((sortBy ?? "") == "name" && sortOrder == "desc" ? " selected" : "").Append(">По дисциплине Я–А</option>")
      .Append("<option value=\"").Append(wlSortBase).Append("sortBy=faculty&sortOrder=asc\"").Append(sortBy == "faculty" && sortOrder != "desc" ? " selected" : "").Append(">По преподавателю</option>")
      .Append("<option value=\"").Append(wlSortBase).Append("sortBy=hours&sortOrder=desc\"").Append(sortBy == "hours" ? " selected" : "").Append(">По часам (убыв.)</option>")
      .Append("<option value=\"").Append(wlSortBase).Append("sortBy=op&sortOrder=asc\"").Append(sortBy == "op" ? " selected" : "").Append(">По ОП</option>")
      .Append("<option value=\"").Append(wlSortBase).Append("sortBy=newest&sortOrder=desc\"").Append(sortBy == "newest" ? " selected" : "").Append(">По новизне</option>")
      .Append("</select></span>")
      .Append("<a class=\"btn btn--excel\" data-export=\"excel\" href=\"").Append(workloadExportUrl.Replace("&", "&amp;")).Append("\" target=\"_blank\" title=\"Выгрузить в Excel\" aria-label=\"Выгрузить в Excel\">")
      .Append("<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"currentColor\" d=\"M12 3v10.17l3.59-3.58L17 11l-5 5-5-5 1.41-1.41L11 13.17V3h1zm-7 14h14v2H5v-2z\"/></svg>")
      .Append("<span>Выгрузить в Excel</span></a>");
    if (user.CanEditWorkload)
      sb.Append("<span class=\"toolbar-import\"><form method='post' action='/uiworkload/import' enctype='multipart/form-data'>")
        .Append("<input type='hidden' name='redirectQuery' value='").Append(ParseHelpers.H(redirectQuery)).Append("'>")
        .Append("<label class='btn btn--ghost btn--file'><input type='file' name='file' accept='.xlsx,.xls' required>Выберите файл</label>")
        .Append("<button type='submit' class='btn btn--ghost'>Загрузить из Excel</button>")
        .Append("</form></span>");
    sb.Append("</div>")
      .Append("<div class=\"hint\">Показано до 500 строк</div>")
      .Append("</section>");

    int totalRows = 0;
    int disciplines = 0;
    int facultyCount = 0;
    decimal totalHours = 0;
    await using (var s = await statsCmd.ExecuteReaderAsync())
    {
        if (await s.ReadAsync())
        {
            totalRows = PlanRowReader.SafeReadInt32(s, 0);
            disciplines = PlanRowReader.SafeReadInt32(s, 1);
            facultyCount = PlanRowReader.SafeReadInt32(s, 2);
            totalHours = PlanRowReader.SafeReadDecimal(s, 3);
        }
    }

    sb.Append("<section class='stats-grid'>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon'>📄</div><span class='stat-badge stat-badge--green'>Строки</span></div>")
      .Append("<div class='stat-label'>Всего строк</div>")
      .Append("<div class='stat-value'>").Append(totalRows).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--purple'>📚</div><span class='stat-badge stat-badge--blue'>Дисциплины</span></div>")
      .Append("<div class='stat-label'>Дисциплин</div>")
      .Append("<div class='stat-value'>").Append(disciplines).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--green'>👤</div><span class='stat-badge stat-badge--purple'>ППС</span></div>")
      .Append("<div class='stat-label'>Преподавателей</div>")
      .Append("<div class='stat-value'>").Append(facultyCount).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--orange'>⏱️</div><span class='stat-badge stat-badge--orange'>Часы</span></div>")
      .Append("<div class='stat-label'>Всего часов</div>")
      .Append("<div class='stat-value'>").Append(totalHours.ToString("0.##", CultureInfo.InvariantCulture)).Append("</div>")
      .Append("</div>")
      .Append("</section>");

    sb.Append("<section class=\"card card--flush\">")
      .Append("<form id='wl-batch-form' method='post' action='/uiworkload/save-batch-form'>")
      .Append("<datalist id='wl-faculty-list'>")
      .Append(string.Join("", faculty.Select(f => $"<option value=\"{ParseHelpers.H(f.name)}\"></option>")))
      .Append("</datalist>")
      .Append("<select id='wl-worktype-options' class='input' style='display:none'>")
      .Append(OptionsList(workTypes, null, "Вид работ"))
      .Append("</select>");
    var allOps = OpMagistracy.Concat(OpBachelor).Distinct().OrderBy(x => x).ToArray();
    var allDepartmentNames = departments.Select(d => d.name).OrderBy(x => x).ToArray();
    sb.Append("<select id='wl-year-options' class='input' style='display:none'>").Append(SelectOptions.AcademicYearOptions(null)).Append("</select>");
    sb.Append("<select id='wl-op-options' class='input' style='display:none'>").Append(OptionsListStrings(allOps, null, "ОП")).Append("</select>");
    sb.Append("<select id='wl-level-options' class='input' style='display:none'>").Append(OptionsListStrings(SelectOptions.EducationLevels, null, "Уровень")).Append("</select>");
    sb.Append("<select id='wl-dept-options' class='input' style='display:none'>").Append(OptionsListStrings(allDepartmentNames, null, "Департамент")).Append("</select>");
    var canEditWl = user.CanEditWorkload;
    sb.Append("<div class=\"table-scroll-top\"><div class=\"table-scroll-bar\" role=\"scrollbar\" aria-orientation=\"horizontal\"><div class=\"table-scroll-bar__inner\"></div></div><div class=\"table-wrap\"><table id=\"wl-table\" class=\"table\"><thead><tr>")
      .Append("<th><input type='checkbox' data-select-all='wl' aria-label='Выбрать все'").Append(canEditWl ? ">" : " disabled>").Append("</th>")
      .Append("<th class=\"col-year\">Год</th>")
      .Append("<th>Модуль</th>")
      .Append("<th>№ дисциплины</th>")
      .Append("<th>Дисциплина</th>")
      .Append("<th>Образовательная программа</th>")
      .Append("<th>Бюджет / Коммерческая</th>")
      .Append("<th>Уровень образования</th>")
      .Append("<th>Реализующий департамент</th>")
      .Append("<th>Вид работ / Часы</th>")
      .Append("<th>Нераспределённая нагрузка (ч)</th>")
      .Append("<th>Преподаватель</th>")
      .Append("</tr></thead><tbody>");

    var rows = new List<(int assignmentId, int planDisciplineId, int facultyId, int workTypeId, string role, string year, int? modNo, string modName, string discNo, string discName, string opName, string level, string dept, decimal hours, string facultyName)>();
    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            try
            {
                var v0 = r.GetValue(0); var v1 = r.GetValue(1); var v2 = r.GetValue(2); var v3 = r.GetValue(3);
                if (v0 == null || v0 == DBNull.Value || v1 == null || v1 == DBNull.Value || v2 == null || v2 == DBNull.Value || v3 == null || v3 == DBNull.Value) continue;
                var assignmentIdDb = Convert.ToInt32(v0);
                var planDisciplineIdDb = Convert.ToInt32(v1);
                var facultyIdDb = Convert.ToInt32(v2);
                var workTypeIdDb = Convert.ToInt32(v3);
                var roleDb = r.IsDBNull(4) ? "" : r.GetString(4);
                var yearVal = r.IsDBNull(5) ? "" : r.GetString(5);
                var modNoRaw = PlanRowReader.SafeReadIntOrStringAsString(r, 6);
                var modNo = int.TryParse(modNoRaw, out var modNoVal) ? (int?)modNoVal : null;
                var modName = r.IsDBNull(7) ? "" : r.GetString(7);
                var discNoVal = r.IsDBNull(8) ? "" : r.GetString(8);
                var discNameVal = r.IsDBNull(9) ? "" : r.GetString(9);
                var opVal = r.IsDBNull(10) ? "" : r.GetString(10);
                var levelVal = r.IsDBNull(11) ? "" : r.GetString(11);
                var deptVal = r.IsDBNull(12) ? "" : r.GetString(12);
                var hoursVal = r.IsDBNull(14) ? 0m : r.GetDecimal(14);
                var facultyName = r.IsDBNull(15) ? "" : r.GetString(15);
                rows.Add((assignmentIdDb, planDisciplineIdDb, facultyIdDb, workTypeIdDb, roleDb, yearVal, modNo, modName, discNoVal, discNameVal, opVal, levelVal, deptVal, hoursVal, facultyName));
            }
            catch { /* пропуск строки с некорректными данными */ }
        }
    }

    // Нераспределённая нагрузка по дисциплине (plan_discipline_id): часы плана минус уже распределённые между преподавателями
    var planDisciplineIds = rows.Select(x => x.planDisciplineId).Distinct().ToArray();
    var unallocatedByPlanId = new Dictionary<int, decimal>();
    var planTotalByPlanId = new Dictionary<int, decimal>();
    var initialAllocatedByPlanId = new Dictionary<int, decimal>();
    if (planDisciplineIds.Length > 0)
    {
        await using var unallocCmd = new NpgsqlCommand(@"
SELECT pd.plan_discipline_id,
  COALESCE(pd.rup_total_hours, COALESCE(pd.hours_module1,0)+COALESCE(pd.hours_module2,0)+COALESCE(pd.hours_module3,0)+COALESCE(pd.hours_module4,0), 0) AS plan_total,
  (SELECT COALESCE(SUM(ah.hours), 0) FROM assignment_hours ah JOIN teaching_assignments ta2 ON ah.assignment_id = ta2.assignment_id WHERE ta2.plan_discipline_id = pd.plan_discipline_id) AS allocated
FROM plan_disciplines pd
WHERE pd.plan_discipline_id = ANY(@ids)", conn);
        unallocCmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, planDisciplineIds);
        await using (var ur = await unallocCmd.ExecuteReaderAsync())
        {
            while (await ur.ReadAsync())
            {
                var pid = PlanRowReader.SafeReadInt32(ur, 0);
                var planTotal = PlanRowReader.SafeReadDecimal(ur, 1);
                var allocated = PlanRowReader.SafeReadDecimal(ur, 2);
                planTotalByPlanId[pid] = planTotal;
                initialAllocatedByPlanId[pid] = allocated;
                unallocatedByPlanId[pid] = Math.Max(0, planTotal - allocated);
            }
        }
    }

    var grouped = rows.GroupBy(x => x.assignmentId).ToList();
    foreach (var grp in grouped)
    {
        var first = grp.First();
        var rowKey = first.assignmentId.ToString();
        var modVal = $"{(first.modNo is null ? "" : first.modNo.ToString())} {first.modName}".Trim();
        var workTypeHoursList = grp.Select(x => (x.workTypeId, x.hours)).ToList();

        var planTotalForRow = planTotalByPlanId.TryGetValue(first.planDisciplineId, out var pt) ? pt : 0m;
        var initialAllocForRow = initialAllocatedByPlanId.TryGetValue(first.planDisciplineId, out var ia) ? ia : 0m;
        sb.Append("<tr data-row-key=\"").Append(ParseHelpers.H(rowKey)).Append("\" data-plan-discipline-id=\"").Append(first.planDisciplineId).Append("\" data-plan-total=\"").Append(planTotalForRow.ToString(CultureInfo.InvariantCulture)).Append("\" data-initial-allocated=\"").Append(initialAllocForRow.ToString(CultureInfo.InvariantCulture)).Append("\">");
        var assignmentIdForCheckbox = first.assignmentId;
        if (canEditWl)
            sb.Append("<td><input type='checkbox' class='row-select row-select-wl' name='selectAssignmentId' value='").Append(assignmentIdForCheckbox).Append("'></td>");
        else
            sb.Append("<td><input type='checkbox' class='row-select row-select-wl' disabled></td>");
        if (canEditWl)
            sb.Append("<td class=\"col-year\"><select class=\"input\" name=\"year_").Append(rowKey).Append("\">").Append(SelectOptions.AcademicYearOptions(first.year)).Append("</select></td>")
              .Append("<td><input class=\"input\" name=\"module_").Append(rowKey).Append("\" value=\"").Append(ParseHelpers.H(modVal)).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"disciplineNo_").Append(rowKey).Append("\" value=\"").Append(ParseHelpers.H(first.discNo)).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"disciplineName_").Append(rowKey).Append("\" value=\"").Append(ParseHelpers.H(first.discName)).Append("\"></td>")
              .Append("<td><select class=\"input\" name=\"opName_").Append(rowKey).Append("\">").Append(OptionsListStrings(allOps, first.opName, "ОП")).Append("</select></td>")
              .Append("<td>").Append(ParseHelpers.H(OpBudgetHelper.OpBudgetCommercial(first.opName))).Append("</td>")
              .Append("<td><select class=\"input\" name=\"educationLevel_").Append(rowKey).Append("\">").Append(OptionsListStrings(SelectOptions.EducationLevels, first.level, "Уровень")).Append("</select></td>")
              .Append("<td><select class=\"input\" name=\"departmentName_").Append(rowKey).Append("\">").Append(OptionsListStrings(allDepartmentNames, first.dept, "Департамент")).Append("</select></td>");
        else
            sb.Append("<td class=\"col-year\"><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(first.year)).Append("\" readonly></td>")
              .Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(modVal)).Append("\" readonly></td>")
              .Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(first.discNo)).Append("\" readonly></td>")
              .Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(first.discName)).Append("\" readonly></td>")
              .Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(first.opName)).Append("\" readonly></td>")
              .Append("<td>").Append(ParseHelpers.H(OpBudgetHelper.OpBudgetCommercial(first.opName))).Append("</td>")
              .Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(first.level)).Append("\" readonly></td>")
              .Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(first.dept)).Append("\" readonly></td>");

        sb.Append("<td class=\"wl-cell-worktypes\">")
          .Append("<input type=\"hidden\" name=\"rowId\" value=\"").Append(rowKey).Append("\">")
          .Append("<input type=\"hidden\" name=\"assignmentId_").Append(rowKey).Append("\" value=\"").Append(rowKey).Append("\">")
          .Append("<input type=\"hidden\" class=\"input input--readonly\" name=\"planDisciplineId_").Append(rowKey).Append("\" value=\"").Append(first.planDisciplineId).Append("\">");
        if (canEditWl)
        {
            sb.Append("<div class=\"wl-worktypes-list\">");
            for (var i = 0; i < workTypeHoursList.Count; i++)
            {
                var (wtId, hrs) = workTypeHoursList[i];
                sb.Append("<div class=\"wl-worktype-pair\">")
                  .Append("<select class=\"input\" name=\"workTypeId_").Append(i).Append("_").Append(rowKey).Append("\">")
                  .Append(OptionsList(workTypes, wtId, "Вид работ"))
                  .Append("</select>")
                  .Append("<input class=\"input\" name=\"hours_").Append(i).Append("_").Append(rowKey).Append("\" value=\"").Append(hrs.ToString(CultureInfo.InvariantCulture)).Append("\" placeholder=\"Часы\">")
                  .Append("</div>");
            }
            sb.Append("</div>")
              .Append("<button type=\"button\" class=\"btn btn--secondary btn--small wl-add-worktype\" data-row-key=\"").Append(ParseHelpers.H(rowKey)).Append("\">+ Вид работ</button>");
        }
        else
        {
            foreach (var (wtId, hrs) in workTypeHoursList)
            {
                var wtName = workTypes.FirstOrDefault(w => w.id == wtId).name ?? "";
                sb.Append("<div class=\"wl-worktype-pair\">")
                  .Append("<span class=\"input input--readonly\">").Append(ParseHelpers.H(wtName)).Append("</span> ")
                  .Append("<span class=\"input input--readonly\">").Append(hrs.ToString(CultureInfo.InvariantCulture)).Append(" ч.</span>")
                  .Append("</div>");
            }
        }
        var unallocatedVal = unallocatedByPlanId.TryGetValue(first.planDisciplineId, out var u) ? u : 0m;
        sb.Append("</td><td class=\"cell-unallocated\">").Append(unallocatedVal.ToString("0.##", CultureInfo.InvariantCulture)).Append("</td><td>");
        if (canEditWl)
            sb.Append("<input class=\"input\" name=\"facultyName_").Append(rowKey).Append("\" list=\"wl-faculty-list\" value=\"").Append(ParseHelpers.H(first.facultyName)).Append("\" placeholder=\"Преподаватель\">");
        else
            sb.Append("<input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(first.facultyName)).Append("\" readonly>");
        sb.Append("</td>");
        sb.Append("</tr>");
    }

    sb.Append("</tbody></table></div></div></form></section>");
    return Results.Content(Layout("Нагрузка", "workload", sb.ToString(), user.Login, user.IsAdmin), "text/html; charset=utf-8");
});

app.MapPost("/uiworkload/delete", async (HttpContext ctx, NpgsqlDataSource ds, int? assignmentId) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditWorkload) return Results.Forbid();
    if (assignmentId is null) return Results.Redirect("/uiworkload");
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    await using (var cmd = new NpgsqlCommand("DELETE FROM assignment_hours WHERE assignment_id = @id", conn, tx))
    {
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, assignmentId.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd2 = new NpgsqlCommand("DELETE FROM teaching_assignments WHERE assignment_id = @id", conn, tx))
    {
        cmd2.Parameters.AddWithValue("id", NpgsqlDbType.Integer, assignmentId.Value);
        await cmd2.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
    return Results.Redirect("/uiworkload");
});

app.MapPost("/uiworkload/delete-batch", async (HttpContext ctx, NpgsqlDataSource ds, int[]? assignmentIds) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditWorkload) return Results.Forbid();
    if (assignmentIds is null || assignmentIds.Length == 0) return Results.Redirect("/uiworkload");
    var ids = assignmentIds.Where(id => id > 0).ToArray();
    if (ids.Length == 0) return Results.Redirect("/uiworkload");
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    foreach (var id in ids)
    {
        await using (var cmd = new NpgsqlCommand("DELETE FROM assignment_hours WHERE assignment_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd2 = new NpgsqlCommand("DELETE FROM teaching_assignments WHERE assignment_id = @id", conn, tx))
        {
            cmd2.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
            await cmd2.ExecuteNonQueryAsync();
        }
    }
    await tx.CommitAsync();
    return Results.Redirect("/uiworkload");
});

app.MapGet("/uiworkload/export", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    string? year,
    string[]? opName,
    string? courseNo,
    string? level,
    string? qSubject,
    string[]? departmentId,
    string[]? moduleNo
) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    var opNames = (opName ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
    var opNamesLike = opNames.Length == 0 ? null : opNames.Select(n => $"%{n}%").ToArray();
    var courseNoValue = ParseHelpers.IntOrNull(courseNo);
    var moduleNos = ParseModuleNos(moduleNo);
    var departments = await LoadDepartments(ds);
    var departmentIds = ParseDepartmentIds(departmentId);
    var departmentNames = departmentIds.Length == 0 ? null : departments.Where(d => departmentIds.Contains(d.id)).Select(d => d.name).ToArray();

    // Пустые фильтры = «всё подряд»: в SQL передаём NULL, а не пустую строку
    var yearParam = string.IsNullOrWhiteSpace(year) ? null : year;
    var levelParam = string.IsNullOrWhiteSpace(level) ? null : $"%{level}%";
    var qSubjectParam = string.IsNullOrWhiteSpace(qSubject) ? null : $"%{qSubject}%";

    await using var conn = await ds.OpenConnectionAsync();
    var moduleNosParam = moduleNos.Length == 0 ? (object?)null : moduleNos;
    var unallocatedByPlanId = new Dictionary<int, decimal>();
    await using (var idCmd = new NpgsqlCommand(@"
SELECT DISTINCT plan_discipline_id FROM v_workload_by_worktype
WHERE
  (@y IS NULL OR academic_year = @y) AND
  (@opNames IS NULL OR op_name ILIKE ANY(@opNames)) AND
  (@course IS NULL OR course_no = @course) AND
  (@lvl IS NULL OR education_level ILIKE @lvl) AND
  (@qs IS NULL OR discipline_name ILIKE @qs) AND
  (@depNames IS NULL OR array_length(@depNames, 1) IS NULL OR department_name = ANY(@depNames)) AND
  (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR module_number = ANY(@moduleNos))", conn))
    {
        idCmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearParam ?? DBNull.Value);
        idCmd.Parameters.AddWithValue("opNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)opNamesLike ?? DBNull.Value);
        idCmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
        idCmd.Parameters.AddWithValue("lvl", NpgsqlDbType.Text, (object?)levelParam ?? DBNull.Value);
        idCmd.Parameters.AddWithValue("qs", NpgsqlDbType.Text, (object?)qSubjectParam ?? DBNull.Value);
        idCmd.Parameters.AddWithValue("depNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)departmentNames ?? DBNull.Value);
        idCmd.Parameters.AddWithValue("moduleNos", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)moduleNosParam ?? DBNull.Value);
        var ids = new List<int>();
        await using (var idr = await idCmd.ExecuteReaderAsync())
        {
            while (await idr.ReadAsync())
                ids.Add(PlanRowReader.SafeReadInt32(idr, 0));
        }
        if (ids.Count > 0)
        {
            await using var unallocCmd = new NpgsqlCommand(@"
SELECT pd.plan_discipline_id,
  COALESCE(pd.rup_total_hours, COALESCE(pd.hours_module1,0)+COALESCE(pd.hours_module2,0)+COALESCE(pd.hours_module3,0)+COALESCE(pd.hours_module4,0), 0) AS plan_total,
  (SELECT COALESCE(SUM(ah.hours), 0) FROM assignment_hours ah JOIN teaching_assignments ta2 ON ah.assignment_id = ta2.assignment_id WHERE ta2.plan_discipline_id = pd.plan_discipline_id) AS allocated
FROM plan_disciplines pd WHERE pd.plan_discipline_id = ANY(@ids)", conn);
            unallocCmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, ids.ToArray());
            await using (var ur = await unallocCmd.ExecuteReaderAsync())
            {
                while (await ur.ReadAsync())
                {
                    var pid = PlanRowReader.SafeReadInt32(ur, 0);
                    var planTotal = ur.IsDBNull(1) ? 0m : ur.GetDecimal(1);
                    var allocated = ur.IsDBNull(2) ? 0m : ur.GetDecimal(2);
                    unallocatedByPlanId[pid] = Math.Max(0, planTotal - allocated);
                }
            }
        }
    }

    await using var cmd = new NpgsqlCommand(@"
SELECT
  plan_discipline_id,
  academic_year,
  module_number,
  module_name,
  discipline_no,
  discipline_name,
  op_name,
  education_level,
  department_name,
  work_type,
  hours,
  faculty_name
FROM v_workload_by_worktype
WHERE
  (@y IS NULL OR academic_year = @y) AND
  (@opNames IS NULL OR op_name ILIKE ANY(@opNames)) AND
  (@course IS NULL OR course_no = @course) AND
  (@lvl IS NULL OR education_level ILIKE @lvl) AND
  (@qs IS NULL OR discipline_name ILIKE @qs) AND
  (@depNames IS NULL OR array_length(@depNames, 1) IS NULL OR department_name = ANY(@depNames)) AND
  (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR module_number = ANY(@moduleNos))
ORDER BY op_name, course_no, discipline_no, discipline_name, work_type;
", conn);
    cmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearParam ?? DBNull.Value);
    cmd.Parameters.AddWithValue("opNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)opNamesLike ?? DBNull.Value);
    cmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
    cmd.Parameters.AddWithValue("lvl", NpgsqlDbType.Text, (object?)levelParam ?? DBNull.Value);
    cmd.Parameters.AddWithValue("qs", NpgsqlDbType.Text, (object?)qSubjectParam ?? DBNull.Value);
    cmd.Parameters.AddWithValue("depNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)departmentNames ?? DBNull.Value);
    cmd.Parameters.AddWithValue("moduleNos", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)moduleNosParam ?? DBNull.Value);

    try
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Nagruzka");
        var headers = new[]
        {
            "Год", "Модуль", "№ дисциплины", "Дисциплина", "Образовательная программа",
            "Бюджет / Коммерческая", "Уровень образования", "Реализующий департамент", "Вид работ", "Часы",
            "Нераспределённая нагрузка (ч)", "Преподаватель"
        };
        for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];

        var row = 2;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var planDiscId = PlanRowReader.SafeReadInt32(r, 0);
                // Колонки: 2 = module_number, 3 = module_name, 4 = discipline_no
                object? modNoVal = r.IsDBNull(2) ? null : r.GetValue(2);
                var modNoStr = modNoVal is null ? "" : Convert.ToString(modNoVal);
                var modName = r.IsDBNull(3) ? "" : r.GetString(3);
                var rowOpName = r.IsDBNull(6) ? "" : r.GetString(6);
                ws.Cell(row, 1).Value = r.IsDBNull(1) ? "" : r.GetString(1);
                ws.Cell(row, 2).Value = $"{modNoStr} {modName}".Trim();
                ws.Cell(row, 3).Value = r.IsDBNull(4) ? "" : Convert.ToString(r.GetValue(4));
                ws.Cell(row, 4).Value = r.IsDBNull(5) ? "" : r.GetString(5);
                ws.Cell(row, 5).Value = rowOpName;
                ws.Cell(row, 6).Value = OpBudgetHelper.OpBudgetCommercial(rowOpName);
                ws.Cell(row, 7).Value = r.IsDBNull(7) ? "" : r.GetString(7);
                ws.Cell(row, 8).Value = r.IsDBNull(8) ? "" : r.GetString(8);
                ws.Cell(row, 9).Value = r.IsDBNull(9) ? "" : r.GetString(9);
                var hoursVal = r.IsDBNull(10) ? null : r.GetValue(10);
                ws.Cell(row, 10).Value = hoursVal is null ? "" : (hoursVal is decimal d ? d : Convert.ToDecimal(hoursVal));
                ws.Cell(row, 11).Value = unallocatedByPlanId.TryGetValue(planDiscId, out var u) ? u : 0m;
                ws.Cell(row, 12).Value = r.IsDBNull(11) ? "" : r.GetString(11);
                row++;
            }
        }

        if (row == 2)
        {
            var q = new List<string> { "exportEmpty=1" };
            if (!string.IsNullOrEmpty(year)) q.Add("year=" + WebUtility.UrlEncode(year));
            foreach (var n in opNames) q.Add("opName=" + WebUtility.UrlEncode(n));
            if (courseNoValue.HasValue) q.Add("courseNo=" + courseNoValue.Value);
            if (!string.IsNullOrWhiteSpace(level)) q.Add("level=" + WebUtility.UrlEncode(level));
            if (!string.IsNullOrWhiteSpace(qSubject)) q.Add("qSubject=" + WebUtility.UrlEncode(qSubject));
            foreach (var id in departmentIds) q.Add("departmentId=" + id);
            foreach (var m in moduleNos) q.Add("moduleNo=" + m);
            return Results.Redirect("/uiworkload?" + string.Join("&", q));
        }

        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            wb.SaveAs(stream, true);
            stream.Flush();
            bytes = stream.ToArray();
        }
        var fileName = $"workload_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
        const string excelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        return Results.File(bytes, excelContentType, fileDownloadName: fileName);
    }
    catch (Exception ex)
    {
        return Results.Problem("Ошибка выгрузки в Excel: " + ex.Message, statusCode: 500);
    }
});

app.MapPost("/uiworkload/import", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditWorkload) return Results.Forbid();
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        var q = form["redirectQuery"].ToString();
        return Results.Redirect("/uiworkload" + (string.IsNullOrEmpty(q) ? "?importError=noFile" : q + (q.Contains("?") ? "&" : "?") + "importError=noFile"));
    }
    var redirectQuery = form["redirectQuery"].ToString();
    var wlColumnMap = new Dictionary<string, string[]>
    {
        ["year"] = new[] { "Год", "Учебный год", "Год учебный" },
        ["module"] = new[] { "Модуль" },
        ["discipline_no"] = new[] { "№ дисциплины", "№ дисц.", "№ дисцип" },
        ["discipline_name"] = new[] { "Дисциплина", "Наименование дисциплины" },
        ["op_name"] = new[] { "Образовательная программа", "ОП", "Образова" },
        ["department"] = new[] { "Реализующий департамент", "Департамент" },
        ["work_type"] = new[] { "Вид работ", "Вид работ / Часы" },
        ["hours"] = new[] { "Часы" },
        ["faculty_name"] = new[] { "Преподаватель", "ФИО ППС", "ППС" }
    };
    List<Dictionary<string, string>> rows;
    try
    {
        await using var stream = file.OpenReadStream();
        rows = ExcelImportHelper.ParseSheet(stream, wlColumnMap);
    }
    catch (Exception ex)
    {
        return Results.Redirect("/uiworkload" + (string.IsNullOrEmpty(redirectQuery) ? "?" : redirectQuery + "&") + "importError=" + Uri.EscapeDataString(ex.Message));
    }
    var workTypes = await LoadWorkTypes(ds);
    var errors = new List<string>();
    var imported = 0;
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    foreach (var row in rows)
    {
        var yearVal = ExcelImportHelper.Get(row, "year");
        var disciplineNo = ExcelImportHelper.Get(row, "discipline_no");
        var disciplineName = (ExcelImportHelper.Get(row, "discipline_name") ?? "").Trim();
        var opName = ExcelImportHelper.Get(row, "op_name");
        var departmentName = ExcelImportHelper.Get(row, "department");
        var workTypeName = ExcelImportHelper.Get(row, "work_type");
        var hoursVal = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "hours"));
        var facultyName = (ExcelImportHelper.Get(row, "faculty_name") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(disciplineName) || string.IsNullOrWhiteSpace(facultyName) || workTypeName is null || hoursVal is null || hoursVal < 0) continue;
        var yearNorm = (yearVal ?? "").Trim();
        var yearAlt = yearNorm.Replace("-", "/");
        if (yearAlt.Length == 4 && int.TryParse(yearAlt, out var y) && y >= 2000 && y < 2100)
            yearAlt = $"{y}/{y + 1}";
        if (string.IsNullOrWhiteSpace(yearAlt)) yearAlt = yearNorm;

        var (planDisciplineId, createError) = await GetOrCreatePlanDisciplineForImport(conn, tx, yearNorm, yearAlt, disciplineNo, disciplineName, opName, departmentName);
        if (createError != null)
        {
            errors.Add($"Дисциплина «{yearVal} / {disciplineName}»: {createError}");
            continue;
        }
        if (planDisciplineId is null)
        {
            errors.Add($"Не найдена дисциплина: {yearVal} / {disciplineName}. Добавьте в Учебный план (год {yearAlt}).");
            continue;
        }
        int? facultyId = null;
        await using (var fc = new NpgsqlCommand("SELECT faculty_id FROM faculty_members WHERE LOWER(TRIM(full_name)) = LOWER(TRIM(@name)) LIMIT 1", conn, tx))
        {
            fc.Parameters.AddWithValue("name", NpgsqlDbType.Text, facultyName);
            var fid = await fc.ExecuteScalarAsync();
            if (fid is int fi) facultyId = fi;
        }
        if (facultyId is null)
        {
            await using (var insF = new NpgsqlCommand("INSERT INTO faculty_members (full_name) VALUES (@name) RETURNING faculty_id", conn, tx))
            {
                insF.Parameters.AddWithValue("name", NpgsqlDbType.Text, facultyName.Trim());
                var newId = await insF.ExecuteScalarAsync();
                if (newId is int nid) facultyId = nid;
            }
        }
        if (facultyId is null) { errors.Add($"Преподаватель не найден: {facultyName}"); continue; }
        var wt = workTypes.FirstOrDefault(w => string.Equals(w.name, workTypeName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (wt.id == 0) { errors.Add($"Вид работ не найден: {workTypeName}"); continue; }
        int assignmentId;
        await using (var ac = new NpgsqlCommand("SELECT assignment_id FROM teaching_assignments WHERE plan_discipline_id = @pd AND faculty_id = @fid LIMIT 1", conn, tx))
        {
            ac.Parameters.AddWithValue("pd", NpgsqlDbType.Integer, planDisciplineId.Value);
            ac.Parameters.AddWithValue("fid", NpgsqlDbType.Integer, facultyId.Value);
            var aid = await ac.ExecuteScalarAsync();
            if (aid is int ai) { assignmentId = ai; }
            else
            {
                await using var ins = new NpgsqlCommand("INSERT INTO teaching_assignments (plan_discipline_id, faculty_id, role) VALUES (@pd, @fid, '') RETURNING assignment_id", conn, tx);
                ins.Parameters.AddWithValue("pd", NpgsqlDbType.Integer, planDisciplineId.Value);
                ins.Parameters.AddWithValue("fid", NpgsqlDbType.Integer, facultyId.Value);
                assignmentId = (int)(await ins.ExecuteScalarAsync() ?? 0);
            }
        }
        await using var uh = new NpgsqlCommand(@"
INSERT INTO assignment_hours (assignment_id, work_type_id, hours) VALUES (@aid, @wtid, @h)
ON CONFLICT (assignment_id, work_type_id) DO UPDATE SET hours = EXCLUDED.hours", conn, tx);
        uh.Parameters.AddWithValue("aid", NpgsqlDbType.Integer, assignmentId);
        uh.Parameters.AddWithValue("wtid", NpgsqlDbType.Integer, wt.id);
        uh.Parameters.AddWithValue("h", NpgsqlDbType.Numeric, hoursVal.Value);
        await uh.ExecuteNonQueryAsync();
        imported++;
    }
    if (errors.Count > 10) errors.Add($"… всего ошибок: {errors.Count}");
    if (errors.Count > 0)
    {
        await tx.RollbackAsync();
        var errText = string.Join("; ", errors.Take(10));
        if (errText.Length > 800) errText = errText.Substring(0, 797) + "...";
        return Results.Redirect("/uiworkload" + (string.IsNullOrEmpty(redirectQuery) ? "?" : redirectQuery + "&") + "importError=" + Uri.EscapeDataString(errText));
    }
    await tx.CommitAsync();
    return Results.Redirect("/uiworkload" + (string.IsNullOrEmpty(redirectQuery) ? "" : redirectQuery));
}).DisableAntiforgery();

app.MapPost("/uiworkload", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    string? assignmentId,
    string? planDisciplineId,
    string? facultyId,
    string? workTypeId,
    string? role,
    string? hours
) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditWorkload) return Results.Forbid();
    var assignmentIdValue = ParseHelpers.IntOrNull(assignmentId);
    var planDisciplineIdValue = ParseHelpers.IntOrNull(planDisciplineId);
    var facultyIdValue = ParseHelpers.IntOrNull(facultyId);
    var workTypeIdValue = ParseHelpers.IntOrNull(workTypeId);
    var hoursValue = ParseHelpers.DecimalOrNull(hours);

    if (planDisciplineIdValue is null || facultyIdValue is null || workTypeIdValue is null || hoursValue is null)
        return Results.Redirect("/uiworkload");

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    if (assignmentIdValue is null)
    {
        await using var insertAssign = new NpgsqlCommand(@"
INSERT INTO teaching_assignments(plan_discipline_id, faculty_id, role)
VALUES (@planId, @facultyId, @role)
RETURNING assignment_id;
", conn, tx);
        insertAssign.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planDisciplineIdValue.Value);
        insertAssign.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, facultyIdValue.Value);
        insertAssign.Parameters.AddWithValue("role", NpgsqlDbType.Text, (object?)role ?? DBNull.Value);
        assignmentIdValue = (int?)await insertAssign.ExecuteScalarAsync();
    }
    else
    {
        await using var updateAssign = new NpgsqlCommand(@"
UPDATE teaching_assignments
SET plan_discipline_id = @planId,
    faculty_id = @facultyId,
    role = @role
WHERE assignment_id = @assignmentId;
", conn, tx);
        updateAssign.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planDisciplineIdValue.Value);
        updateAssign.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, facultyIdValue.Value);
        updateAssign.Parameters.AddWithValue("role", NpgsqlDbType.Text, (object?)role ?? DBNull.Value);
        updateAssign.Parameters.AddWithValue("assignmentId", NpgsqlDbType.Integer, assignmentIdValue.Value);
        await updateAssign.ExecuteNonQueryAsync();
    }

    await using var upsertHours = new NpgsqlCommand(@"
INSERT INTO assignment_hours(assignment_id, work_type_id, hours)
VALUES (@assignmentId, @workTypeId, @hours)
ON CONFLICT (assignment_id, work_type_id)
DO UPDATE SET hours = EXCLUDED.hours;
", conn, tx);
    upsertHours.Parameters.AddWithValue("assignmentId", NpgsqlDbType.Integer, assignmentIdValue!.Value);
    upsertHours.Parameters.AddWithValue("workTypeId", NpgsqlDbType.Integer, workTypeIdValue.Value);
    upsertHours.Parameters.AddWithValue("hours", NpgsqlDbType.Numeric, hoursValue.Value);
    await upsertHours.ExecuteNonQueryAsync();

    await tx.CommitAsync();
    return Results.Redirect("/uiworkload");
});

app.MapPost("/uiworkload/save-batch-form", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditWorkload) return Results.Forbid();
    var form = await request.ReadFormAsync();
    var rowKeys = form["rowId"];
    if (rowKeys.Count == 0) return Results.Redirect("/uiworkload");

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    var errors = new List<string>();
    var touchedAssignmentIds = new List<int>();

    foreach (var rowKey in rowKeys)
    {
        if (string.IsNullOrWhiteSpace(rowKey)) continue;
        var rowKeyStr = rowKey.ToString().Trim();
        string? Get(string key)
        {
            var value = form[$"{key}_{rowKeyStr}"].ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        string? GetIndexed(string key, int i)
        {
            var value = form[$"{key}_{i}_{rowKeyStr}"].ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        var assignmentId = ParseHelpers.IntOrNull(Get("assignmentId"));
        var planDisciplineId = ParseHelpers.IntOrNull(Get("planDisciplineId"));
        var facultyName = Get("facultyName");
        var role = Get("role");
        var roleForDb = string.IsNullOrWhiteSpace(role) ? "Преподаватель" : role.Trim();

        if (planDisciplineId is null && assignmentId is null) continue;

        if (string.IsNullOrWhiteSpace(facultyName))
        {
            errors.Add($"Укажите преподавателя (строка {rowKeyStr})");
            continue;
        }
        if (assignmentId is null && planDisciplineId is null)
        {
            errors.Add($"Укажите ID дисциплины для новой строки (строка {rowKeyStr})");
            continue;
        }

        int? facultyId;
        await using (var fcmd = new NpgsqlCommand(
            "SELECT faculty_id FROM faculty_members WHERE full_name = @name",
            conn, tx))
        {
            fcmd.Parameters.AddWithValue("name", NpgsqlDbType.Text, facultyName);
            var found = await fcmd.ExecuteScalarAsync();
            facultyId = found as int?;
        }
        if (facultyId is null)
        {
            errors.Add($"Преподаватель не найден: {facultyName} (строка {rowKeyStr})");
            continue;
        }

        int assignmentIdValue;
        if (assignmentId is null)
        {
            await using var insertAssign = new NpgsqlCommand(@"
INSERT INTO teaching_assignments (plan_discipline_id, faculty_id, role)
VALUES (@planDisciplineId, @facultyId, @role)
RETURNING assignment_id;", conn, tx);
            insertAssign.Parameters.AddWithValue("planDisciplineId", NpgsqlDbType.Integer, planDisciplineId!.Value);
            insertAssign.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, facultyId.Value);
            insertAssign.Parameters.AddWithValue("role", NpgsqlDbType.Text, roleForDb);
            assignmentIdValue = (int)(await insertAssign.ExecuteScalarAsync() ?? 0);
        }
        else
        {
            assignmentIdValue = assignmentId.Value;
            await using var updateAssign = new NpgsqlCommand(@"
UPDATE teaching_assignments
SET faculty_id = COALESCE(@facultyId, faculty_id),
    role = COALESCE(@role, role)
WHERE assignment_id = @assignmentId;", conn, tx);
            updateAssign.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, (object?)facultyId ?? DBNull.Value);
            updateAssign.Parameters.AddWithValue("role", NpgsqlDbType.Text, (object?)role ?? DBNull.Value);
            updateAssign.Parameters.AddWithValue("assignmentId", NpgsqlDbType.Integer, assignmentIdValue);
            await updateAssign.ExecuteNonQueryAsync();
        }

        var submittedWorkTypeIds = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            var workTypeId = ParseHelpers.IntOrNull(GetIndexed("workTypeId", i));
            var hours = ParseHelpers.DecimalOrNull(GetIndexed("hours", i));
            if (workTypeId is null && hours is null) continue;
            if (workTypeId is null)
            {
                errors.Add($"Выберите вид работ (строка {rowKeyStr}, позиция {i + 1})");
                continue;
            }
            if (hours is null)
            {
                errors.Add($"Укажите часы (строка {rowKeyStr}, вид работ {i + 1})");
                continue;
            }
            if (hours < 0)
            {
                errors.Add($"Часы не могут быть отрицательными (строка {rowKeyStr})");
                continue;
            }
            submittedWorkTypeIds.Add(workTypeId.Value);
            await using var upsertHours = new NpgsqlCommand(@"
INSERT INTO assignment_hours (assignment_id, work_type_id, hours)
VALUES (@assignmentId, @workTypeId, @hours)
ON CONFLICT (assignment_id, work_type_id) DO UPDATE
SET hours = EXCLUDED.hours;", conn, tx);
            upsertHours.Parameters.AddWithValue("assignmentId", NpgsqlDbType.Integer, assignmentIdValue);
            upsertHours.Parameters.AddWithValue("workTypeId", NpgsqlDbType.Integer, workTypeId.Value);
            upsertHours.Parameters.AddWithValue("hours", NpgsqlDbType.Numeric, hours.Value);
            await upsertHours.ExecuteNonQueryAsync();
        }

        if (submittedWorkTypeIds.Count == 0)
        {
            errors.Add($"Укажите хотя бы один вид работ и часы (строка {rowKeyStr})");
            continue;
        }

        await using (var delCmd = new NpgsqlCommand(@"
DELETE FROM assignment_hours
WHERE assignment_id = @aid AND work_type_id <> ALL(@keepIds);", conn, tx))
        {
            delCmd.Parameters.AddWithValue("aid", NpgsqlDbType.Integer, assignmentIdValue);
            delCmd.Parameters.AddWithValue("keepIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, submittedWorkTypeIds.ToArray());
            await delCmd.ExecuteNonQueryAsync();
        }
        touchedAssignmentIds.Add(assignmentIdValue);
    }

    if (errors.Count > 0)
    {
        await tx.RollbackAsync();
        var msg = string.Join("\n", errors);
        var body = "<section class='card'><h2>Ошибки при сохранении</h2><p class='error'>"
            + ParseHelpers.H(msg).Replace("\n", "<br>")
            + "</p><p><a href='/uiworkload' class='btn'>Вернуться к таблице нагрузки</a></p></section>";
        var html = Layout("Ошибки сохранения", "workload", body, user.Login, user.IsAdmin);
        return Results.Content(html, "text/html; charset=utf-8");
    }

    await tx.CommitAsync();
    return Results.Redirect("/uiworkload");
}).DisableAntiforgery();

app.MapPost("/uiworkload/seed-ops", async (NpgsqlDataSource ds) =>
{
    var opList = OpMagistracy.Concat(OpBachelor).Distinct();
    var (created, error) = await SeedOpsAsync(ds, opList);
    if (!string.IsNullOrWhiteSpace(error))
        return Results.BadRequest(error);
    return Results.Ok(new { created });
});

app.MapGet("/uiplan", async (HttpContext ctx, NpgsqlDataSource ds, string? year, string[]? opName, string? courseNo, string[]? departmentId, string[]? moduleNo, string? sortBy, string? sortOrder, string? importError) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    var redirectQuery = ctx.Request.QueryString.Value?.TrimStart('?') ?? "";
    var opNames = (opName ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
    var opNamesLike = opNames.Length == 0 ? null : opNames.Select(n => $"%{n}%").ToArray();
    var opNamesNormalized = opNames.Select(ParseHelpers.NormalizeOpName).ToArray();
    var opNorms = opNamesNormalized.Length == 0 ? null : opNamesNormalized;
    var courseNoValue = ParseHelpers.IntOrNull(courseNo);
    var moduleNos = ParseModuleNos(moduleNo);
    var departments = await LoadDepartments(ds);
    var departmentIds = ParseDepartmentIds(departmentId);
    var departmentNames = departmentIds.Length == 0 ? null : departments.Where(d => departmentIds.Contains(d.id)).Select(d => d.name).ToArray();
    var yearValue = string.IsNullOrWhiteSpace(year) ? null : year;
    var opQuery = opNames.Length == 0
        ? ""
        : string.Join("&", opNames.Select(n => $"opName={WebUtility.UrlEncode(n)}"));
    var moduleQuery = moduleNos.Length == 0 ? "" : string.Join("&", moduleNos.Select(n => $"moduleNo={n}"));
    var departmentQuery = departmentIds.Length == 0 ? "" : string.Join("&", departmentIds.Select(n => $"departmentId={n}"));
    var planExportUrl = $"/uiplan/export?year={WebUtility.UrlEncode(yearValue ?? "")}" +
                        (string.IsNullOrWhiteSpace(opQuery) ? "" : $"&{opQuery}") +
                        $"&courseNo={courseNoValue}" +
                        (string.IsNullOrWhiteSpace(departmentQuery) ? "" : $"&{departmentQuery}") +
                        (string.IsNullOrWhiteSpace(moduleQuery) ? "" : $"&{moduleQuery}");

    var body = new StringBuilder();
    body.Append("<section class='page-header'>")
        .Append("<div>")
        .Append("<h1 class='page-title'>Учебный план</h1>")
        .Append("<div class='page-subtitle'>Просмотр учебного плана по выбранным ОП</div>")
        .Append("</div>")
        .Append("</section>");
    if (!string.IsNullOrEmpty(importError))
        body.Append("<section class='card'><p class='").Append(importError == "noFile" ? "hint" : "error").Append("'>")
            .Append(importError == "noFile" ? "Выберите файл Excel для загрузки." : ParseHelpers.H(importError))
            .Append("</p></section>");
    body.Append("<section class=\"card\">")
        .Append("<div class=\"toolbar\">");
    if (user.CanEditPlan)
    {
        body.Append("<button class=\"btn\" type=\"submit\" form=\"plan-batch-form\">Сохранить все</button>")
            .Append("<button class=\"btn btn--danger\" type=\"button\" data-delete-selected=\"plan\">Удалить выбранные</button>")
            .Append("<button class=\"btn btn--ghost\" type=\"button\" data-plan-add>Добавить строку</button>");
    }
    else
        body.Append("<span class=\"hint\">Только просмотр. Редактирование: академический руководитель или администратор.</span>");
    body.Append("<button class=\"btn btn--ghost\" type=\"button\" data-dialog-open=\"#plan-filter\">Фильтр</button>")
        .Append("<button class=\"btn btn--ghost\" type=\"button\" onclick=\"window.location.href='/uiplan'\">Сброс</button>");
    var planSortBase = "/uiplan?" + (string.IsNullOrEmpty(redirectQuery) ? "" : redirectQuery + "&");
    body.Append("<span class=\"toolbar-sort\"><label class=\"hint\" style=\"margin-right:6px\">Сортировка:</label>")
        .Append("<select class=\"input input--sm\" style=\"width:auto;display:inline-block\" onchange=\"window.location=this.value\" aria-label=\"Сортировка\">")
        .Append("<option value=\"").Append(planSortBase).Append("sortBy=name&sortOrder=asc\"").Append((sortBy ?? "name") == "name" && sortOrder != "desc" ? " selected" : "").Append(">По названию А–Я</option>")
        .Append("<option value=\"").Append(planSortBase).Append("sortBy=name&sortOrder=desc\"").Append((sortBy ?? "") == "name" && sortOrder == "desc" ? " selected" : "").Append(">По названию Я–А</option>")
        .Append("<option value=\"").Append(planSortBase).Append("sortBy=no&sortOrder=asc\"").Append(sortBy == "no" ? " selected" : "").Append(">По № дисциплины</option>")
        .Append("<option value=\"").Append(planSortBase).Append("sortBy=course&sortOrder=asc\"").Append(sortBy == "course" ? " selected" : "").Append(">По курсу</option>")
        .Append("<option value=\"").Append(planSortBase).Append("sortBy=module&sortOrder=asc\"").Append(sortBy == "module" ? " selected" : "").Append(">По модулю</option>")
        .Append("<option value=\"").Append(planSortBase).Append("sortBy=department&sortOrder=asc\"").Append(sortBy == "department" ? " selected" : "").Append(">По департаменту</option>")
        .Append("<option value=\"").Append(planSortBase).Append("sortBy=newest&sortOrder=desc\"").Append(sortBy == "newest" ? " selected" : "").Append(">По новизне</option>")
        .Append("</select></span>")
        .Append("<a class=\"btn btn--excel\" data-export=\"excel\" href=\"").Append(planExportUrl)
        .Append("\" title=\"Выгрузить в Excel\" aria-label=\"Выгрузить в Excel\">")
        .Append("<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"currentColor\" d=\"M12 3v10.17l3.59-3.58L17 11l-5 5-5-5 1.41-1.41L11 13.17V3h1zm-7 14h14v2H5v-2z\"/></svg>")
        .Append("<span>Выгрузить в Excel</span></a>");
    if (user.CanEditPlan)
        body.Append("<span class=\"toolbar-import\"><form method='post' action='/uiplan/import' enctype='multipart/form-data'>")
            .Append("<input type='hidden' name='redirectQuery' value='").Append(ParseHelpers.H(redirectQuery)).Append("'>")
            .Append("<label class='btn btn--ghost btn--file'><input type='file' name='file' accept='.xlsx,.xls' required>Выберите файл</label>")
            .Append("<button type='submit' class='btn btn--ghost'>Загрузить из Excel</button>")
            .Append("</form></span>");
    body.Append("</div>")
        .Append("<div class=\"hint\">Показано до 500 строк</div>")
        .Append("</section>")
        .Append("<dialog class=\"modal\" id=\"plan-filter\">")
        .Append("<form method=\"get\" action=\"/uiplan\" class=\"form form--row\">")
        .Append("<div class=\"input-group\"><label class=\"input-group__label\">Учебный год</label>")
        .Append("<select class=\"input\" name=\"year\">").Append(SelectOptions.AcademicYearOptions(year)).Append("</select></div>")
        .Append(RenderOpCheckboxes(opNames, null))
        .Append(RenderDepartmentCheckboxes(departments, departmentIds, null))
        .Append(RenderModuleCheckboxes(moduleNos))
        .Append("<input class=\"input\" type=\"number\" name=\"courseNo\" placeholder=\"Курс\" value=\"").Append(courseNoValue?.ToString() ?? "").Append("\">")
        .Append("<input type='hidden' name='sortBy' value='").Append(ParseHelpers.H(sortBy ?? "name")).Append("'>")
        .Append("<input type='hidden' name='sortOrder' value='").Append(ParseHelpers.H(sortOrder ?? "asc")).Append("'>")
        .Append("<div class=\"modal__actions\">")
        .Append("<button class=\"btn\" type=\"submit\">Применить</button>")
        .Append("<button class=\"btn btn--ghost\" type=\"button\" data-dialog-close>Закрыть</button>")
        .Append("</div>")
        .Append("</form>")
        .Append("</dialog>");

    try
    {
    await using var conn = await ds.OpenConnectionAsync();
    var opLabel = opNames.Length == 0 ? "Все" : string.Join(", ", opNames);
    var opsAll = OpMagistracy.Concat(OpBachelor).Distinct().ToArray();
    var planCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var wlCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    int planFilterCount = 0;
    int workloadFilterCount = 0;
    string workloadOpSamples = "";
    var opIds = new List<int>();
    int seedCreated = 0;
    string? seedError = null;
    int opProgramsCount = 0;
    int pdpCount = 0;
    int pdCount = 0;
    int taCount = 0;
    int ahCount = 0;
    int vwCount = 0;
    var useOpFilter = opNames.Length > 0;

    if (opNamesLike is not null)
    {
        await using var opIdCmd = new NpgsqlCommand(@"
SELECT op_id
FROM educational_programs
WHERE name ILIKE ANY(@opNames);", conn);
        opIdCmd.Parameters.AddWithValue("opNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)opNamesLike ?? DBNull.Value);
        await using var opIdReader = await opIdCmd.ExecuteReaderAsync();
        while (await opIdReader.ReadAsync()) opIds.Add(PlanRowReader.SafeReadInt32(opIdReader, 0));
    }
    var opIdsParam = opIds.Count == 0 ? new[] { -1 } : opIds.ToArray();

    await using (var planFilterCmd = new NpgsqlCommand(@"
SELECT COUNT(*)
FROM plan_disciplines pd
JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
JOIN educational_programs ep ON pp.op_id = ep.op_id
JOIN study_plans sp ON pd.plan_id = sp.plan_id
WHERE
  (@useOpFilter = false OR ep.op_id = ANY(@opIds)) AND
  (@course IS NULL OR pd.course_no = @course) AND
  (@y IS NULL OR sp.academic_year = @y);
", conn))
    {
        planFilterCmd.Parameters.AddWithValue("useOpFilter", NpgsqlDbType.Boolean, useOpFilter);
        planFilterCmd.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam);
        planFilterCmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
        planFilterCmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearValue ?? DBNull.Value);
        var countObj = await planFilterCmd.ExecuteScalarAsync();
        planFilterCount = countObj is DBNull or null ? 0 : Convert.ToInt32(countObj);
    }

    if (opNames.Length > 0 && planFilterCount == 0)
    {
        var seedResult = await SeedOpsAsync(ds, opNames);
        seedCreated = seedResult.created;
        seedError = seedResult.error;
        if (seedCreated > 0 && string.IsNullOrWhiteSpace(seedError))
        {
            if (opNamesLike is not null)
            {
                opIds.Clear();
                await using var opIdCmd2 = new NpgsqlCommand(@"
SELECT op_id
FROM educational_programs
WHERE name ILIKE ANY(@opNames);", conn);
                opIdCmd2.Parameters.AddWithValue("opNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)opNamesLike ?? DBNull.Value);
                await using var opIdReader2 = await opIdCmd2.ExecuteReaderAsync();
                while (await opIdReader2.ReadAsync()) opIds.Add(PlanRowReader.SafeReadInt32(opIdReader2, 0));
            }
            opIdsParam = opIds.Count == 0 ? new[] { -1 } : opIds.ToArray();
            await using var planFilterCmd2 = new NpgsqlCommand(@"
SELECT COUNT(*)
FROM plan_disciplines pd
JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
JOIN educational_programs ep ON pp.op_id = ep.op_id
JOIN study_plans sp ON pd.plan_id = sp.plan_id
WHERE
  (@useOpFilter = false OR ep.op_id = ANY(@opIds)) AND
  (@course IS NULL OR pd.course_no = @course) AND
  (@y IS NULL OR sp.academic_year = @y);
", conn);
            planFilterCmd2.Parameters.AddWithValue("useOpFilter", NpgsqlDbType.Boolean, useOpFilter);
            planFilterCmd2.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam);
            planFilterCmd2.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
            planFilterCmd2.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearValue ?? DBNull.Value);
            var countObj2 = await planFilterCmd2.ExecuteScalarAsync();
            planFilterCount = countObj2 is DBNull or null ? 0 : Convert.ToInt32(countObj2);
        }
    }

    if (opIdsParam is not null)
    {
        await using (var diagCmd = new NpgsqlCommand(@"
SELECT
  (SELECT COUNT(*) FROM plan_programs WHERE op_id = ANY(@opIds)) AS pp_cnt,
  (SELECT COUNT(*) FROM plan_discipline_programs pdp
     JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
     WHERE pp.op_id = ANY(@opIds)) AS pdp_cnt,
  (SELECT COUNT(*) FROM plan_disciplines pd
     JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
     JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
     WHERE pp.op_id = ANY(@opIds)) AS pd_cnt,
  (SELECT COUNT(*) FROM teaching_assignments ta
     JOIN plan_disciplines pd ON ta.plan_discipline_id = pd.plan_discipline_id
     JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
     JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
     WHERE pp.op_id = ANY(@opIds)) AS ta_cnt,
  (SELECT COUNT(*) FROM assignment_hours ah
     JOIN teaching_assignments ta ON ah.assignment_id = ta.assignment_id
     JOIN plan_disciplines pd ON ta.plan_discipline_id = pd.plan_discipline_id
     JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
     JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
     WHERE pp.op_id = ANY(@opIds)) AS ah_cnt,
  (SELECT COUNT(*) FROM v_workload_by_worktype WHERE op_id = ANY(@opIds)) AS vw_cnt;
", conn))
        {
            diagCmd.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam);
            await using var dr = await diagCmd.ExecuteReaderAsync();
            if (await dr.ReadAsync())
            {
                opProgramsCount = PlanRowReader.SafeReadInt32(dr, 0);
                pdpCount = PlanRowReader.SafeReadInt32(dr, 1);
                pdCount = PlanRowReader.SafeReadInt32(dr, 2);
                taCount = PlanRowReader.SafeReadInt32(dr, 3);
                ahCount = PlanRowReader.SafeReadInt32(dr, 4);
                vwCount = PlanRowReader.SafeReadInt32(dr, 5);
            }
        }
    }

    await using (var workloadFilterCmd = new NpgsqlCommand(@"
SELECT
  COUNT(*) AS cnt,
  STRING_AGG(DISTINCT op_name, ' | ') AS ops
FROM (
  SELECT op_name
  FROM v_workload_by_worktype
  WHERE
    (@useOpFilter = false OR op_id = ANY(@opIds)) AND
    (@course IS NULL OR course_no = @course) AND
    (@y IS NULL OR academic_year = @y)
  LIMIT 200
) t;
", conn))
    {
        workloadFilterCmd.Parameters.AddWithValue("useOpFilter", NpgsqlDbType.Boolean, useOpFilter);
        workloadFilterCmd.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam!);
        workloadFilterCmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
        workloadFilterCmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearValue ?? DBNull.Value);
        await using var wr = await workloadFilterCmd.ExecuteReaderAsync();
        if (await wr.ReadAsync())
        {
            workloadFilterCount = PlanRowReader.SafeReadInt32(wr, 0);
            workloadOpSamples = wr.IsDBNull(1) ? "" : wr.GetString(1);
        }
    }

    await using (var planCountCmd = new NpgsqlCommand(@"
SELECT ep.name, COUNT(DISTINCT pd.plan_discipline_id) AS cnt
FROM educational_programs ep
LEFT JOIN plan_programs pp ON pp.op_id = ep.op_id
LEFT JOIN plan_discipline_programs pdp ON pdp.plan_program_id = pp.plan_program_id
LEFT JOIN plan_disciplines pd ON pd.plan_discipline_id = pdp.plan_discipline_id
WHERE ep.name = ANY(@ops)
GROUP BY ep.name;", conn))
    {
        planCountCmd.Parameters.AddWithValue("ops", NpgsqlDbType.Array | NpgsqlDbType.Text, opsAll);
        await using var pr = await planCountCmd.ExecuteReaderAsync();
        while (await pr.ReadAsync())
        {
            planCounts[pr.GetString(0)] = PlanRowReader.SafeReadInt32(pr, 1);
        }
    }

    await using (var wlCountCmd = new NpgsqlCommand(@"
SELECT op_name, COUNT(*) AS cnt
FROM v_workload_by_worktype
WHERE op_name ILIKE ANY(@opsLike)
GROUP BY op_name;", conn))
    {
        var opsLikeAll = opsAll.Select(n => $"%{n}%").ToArray();
        wlCountCmd.Parameters.AddWithValue("opsLike", NpgsqlDbType.Array | NpgsqlDbType.Text, opsLikeAll);
        await using var wr = await wlCountCmd.ExecuteReaderAsync();
        while (await wr.ReadAsync())
        {
            wlCounts[wr.GetString(0)] = PlanRowReader.SafeReadInt32(wr, 1);
        }
    }
    var useWorkloadFallback = opNames.Length > 0 && planFilterCount == 0;
    var cmdText = useWorkloadFallback
        ? @"
SELECT
  NULL::int AS id,
  op_name AS ops,
  education_level,
  ''::text AS direction,
  ''::text AS funding_type,
  module_name,
  ''::text AS module_subtype,
  discipline_no,
  discipline_name,
  department_name AS implementing_department,
  ''::text AS implementing_dep_parent,
  ''::text AS discipline_kind,
  NULL::boolean AS is_key_seminar,
  NULL::boolean AS has_online_course,
  course_no,
  NULL::boolean AS has_mu_request,
  ''::text AS language,
  ''::text AS mkd,
  NULL::numeric AS credits,
  NULL::numeric AS rup_lectures_hours,
  NULL::numeric AS rup_seminars_hours,
  NULL::numeric AS rup_total_hours,
  NULL::numeric AS hours_module1,
  NULL::numeric AS hours_module2,
  NULL::numeric AS hours_module3,
  NULL::numeric AS hours_module4,
  NULL::numeric AS streams_count,
  NULL::numeric AS groups_count,
  NULL::numeric AS aud_lecture_hours,
  NULL::numeric AS aud_seminar_hours,
  NULL::numeric AS aud_nis_ps_sn_hours,
  NULL::numeric AS aud_total_hours,
  NULL::numeric AS students_count,
  NULL::numeric AS current_control_hours,
  SUM(hours) AS total_hours,
  STRING_AGG(DISTINCT faculty_name, ', ') AS faculty_names,
  MIN(academic_year) AS academic_year
FROM v_workload_by_worktype
WHERE
  (@useOpFilter = false OR op_id = ANY(@opIds)) AND
  (@course IS NULL OR course_no = @course) AND
  (@y IS NULL OR academic_year = @y) AND
  (@depNames IS NULL OR array_length(@depNames, 1) IS NULL OR department_name = ANY(@depNames)) AND
  (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR module_number = ANY(@moduleNos))
GROUP BY op_name, education_level, module_name, discipline_no, discipline_name, department_name, course_no
ORDER BY discipline_name
LIMIT 500;
"
        : @"
SELECT
  pd.plan_discipline_id AS id,
  ep.name AS ops,
  ep.education_level,
  sp.direction,
  sp.funding_type,
  pm.module_name,
  pm.module_subtype,
  pd.discipline_no,
  pd.discipline_name,
  d.name AS implementing_department,
  pd.implementing_dep_parent,
  pd.discipline_kind::text AS discipline_kind,
  pd.is_key_seminar,
  pd.has_online_course,
  pd.course_no,
  pd.has_mu_request,
  pd.language,
  pd.mkd,
  pd.credits,
  pd.rup_lectures_hours,
  pd.rup_seminars_hours,
  pd.rup_total_hours,
  pd.hours_module1,
  pd.hours_module2,
  pd.hours_module3,
  pd.hours_module4,
  pd.streams_count,
  pd.groups_count,
  NULL::numeric AS aud_lecture_hours,
  NULL::numeric AS aud_seminar_hours,
  NULL::numeric AS aud_nis_ps_sn_hours,
  NULL::numeric AS aud_total_hours,
  pd.students_count,
  pd.current_control_hours,
  NULL::numeric AS total_hours,
  NULL::text AS faculty_names,
  sp.academic_year,
  pd.plan_id
FROM plan_disciplines pd
JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
JOIN educational_programs ep ON pp.op_id = ep.op_id
JOIN study_plans sp ON pd.plan_id = sp.plan_id
LEFT JOIN plan_modules pm ON pd.module_id = pm.module_id
LEFT JOIN departments d ON pd.implementing_department_id = d.department_id
WHERE
  (
    (@useOpFilter = false OR ep.op_id = ANY(@opIds))
  ) AND
  (@course IS NULL OR pd.course_no = @course) AND
  (@y IS NULL OR sp.academic_year = @y) AND
  (@departmentIds IS NULL OR array_length(@departmentIds, 1) IS NULL OR pd.implementing_department_id = ANY(@departmentIds)) AND
  (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR
   ((1 = ANY(@moduleNos) AND COALESCE(pd.hours_module1, 0) > 0) OR (2 = ANY(@moduleNos) AND COALESCE(pd.hours_module2, 0) > 0) OR (3 = ANY(@moduleNos) AND COALESCE(pd.hours_module3, 0) > 0) OR (4 = ANY(@moduleNos) AND COALESCE(pd.hours_module4, 0) > 0)))
ORDER BY " + GetPlanOrderBy(sortBy, sortOrder) + @"
LIMIT 500;
";
    var planModuleNosParam = moduleNos.Length == 0 ? (object?)null : moduleNos;
    var planDepartmentIdsParam = departmentIds.Length == 0 ? (object?)null : departmentIds;
    await using var cmd = new NpgsqlCommand(cmdText, conn);
    cmd.Parameters.AddWithValue("useOpFilter", NpgsqlDbType.Boolean, useOpFilter);
    cmd.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam!);
    cmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
    cmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearValue ?? DBNull.Value);
    cmd.Parameters.AddWithValue("depNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)departmentNames ?? DBNull.Value);
    cmd.Parameters.AddWithValue("departmentIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)planDepartmentIdsParam ?? DBNull.Value);
    cmd.Parameters.AddWithValue("moduleNos", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)planModuleNosParam ?? DBNull.Value);

    var sb = new StringBuilder(body.ToString());
    sb.Append("<section class='stats-grid'>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon'>📄</div><span class='stat-badge stat-badge--green'>План</span></div>")
      .Append("<div class='stat-label'>Строк в плане</div>")
      .Append("<div class='stat-value'>").Append(planFilterCount).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--purple'>🎓</div><span class='stat-badge stat-badge--blue'>ОП</span></div>")
      .Append("<div class='stat-label'>ОП в фильтре</div>")
      .Append("<div class='stat-value'>").Append(opIds.Count).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--green'>📊</div><span class='stat-badge stat-badge--purple'>Нагрузка</span></div>")
      .Append("<div class='stat-label'>Строк в нагрузке</div>")
      .Append("<div class='stat-value'>").Append(workloadFilterCount).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--orange'>📚</div><span class='stat-badge stat-badge--orange'>Дисциплины</span></div>")
      .Append("<div class='stat-label'>Дисциплин в плане</div>")
      .Append("<div class='stat-value'>").Append(pdCount).Append("</div>")
      .Append("</div>")
      .Append("</section>");
    sb.Append("<section class=\"card\"><div class=\"meta\">")
      .Append("<div><span class=\"meta__k\">ОП</span><span class=\"meta__v\">").Append(ParseHelpers.H(opLabel)).Append("</span></div>")
      .Append("<div><span class=\"meta__k\">Курс</span><span class=\"meta__v\">").Append(courseNoValue?.ToString() ?? "Все").Append("</span></div>")
      .Append("<div><span class=\"meta__k\">Год</span><span class=\"meta__v\">").Append(string.IsNullOrWhiteSpace(yearValue) ? "Все" : ParseHelpers.H(yearValue)).Append("</span></div>")
      .Append("</div></section>");

    // диагностика убрана из интерфейса

    if (opNames.Length > 0)
    {
        if (opIdsParam is not null)
        {
            await using var derivedCmd = new NpgsqlCommand(@"
SELECT
  op_name,
  education_level,
  module_name,
  discipline_no,
  discipline_name,
  department_name,
  course_no,
  SUM(hours) AS total_hours,
  STRING_AGG(DISTINCT faculty_name, ', ') AS faculty_names
FROM v_workload_by_worktype
WHERE
  op_id = ANY(@opIds) AND
  (@course IS NULL OR course_no = @course) AND
  (@y IS NULL OR academic_year = @y)
GROUP BY op_name, education_level, module_name, discipline_no, discipline_name, department_name, course_no
ORDER BY discipline_name
LIMIT 500;
", conn);
            derivedCmd.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam);
            derivedCmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
            derivedCmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearValue ?? DBNull.Value);

            sb.Append("<section class=\"card\">")
              .Append("<h2 class=\"h2\">Учебный план по выбранной ОП (из нагрузки)</h2>")
              .Append("<div class=\"hint\">Показано до 500 строк</div>")
              .Append("</section>")
              .Append("<section class=\"card card--flush\"><div class=\"table-scroll-top\"><div class=\"table-scroll-bar\" role=\"scrollbar\" aria-orientation=\"horizontal\"><div class=\"table-scroll-bar__inner\"></div></div><div class=\"table-wrap\"><table class=\"table\"><thead><tr>")
              .Append("<th>ОП</th>")
              .Append("<th>Уровень</th>")
              .Append("<th>Направление</th>")
              .Append("<th>Бюджетная / коммерческая</th>")
              .Append("<th>Модуль учебного плана</th>")
              .Append("<th>Подтип модуля</th>")
              .Append("<th>№ дисц.</th>")
              .Append("<th>Наименование дисциплины</th>")
              .Append("<th>Реализующее подразделение</th>")
              .Append("<th>Принадлежность реализующего департамента</th>")
              .Append("<th>Вид дисциплины (обязательная, по выбору, факультатив)</th>")
              .Append("<th>Является ключевым семинаром (НИС, ПС, СНаставника)</th>")
              .Append("<th>Наличие онлайн-курса</th>")
              .Append("<th>Курс</th>")
              .Append("<th>Наличие заявки на МУ от АР</th>")
              .Append("<th>Язык</th>")
              .Append("<th>МКД</th>")
              .Append("<th>Зач.ед.</th>")
              .Append("<th>Лекции по РУП</th>")
              .Append("<th>Семинары по РУП</th>")
              .Append("<th>Всего часов по РУП</th>")
              .Append("<th>В том числе всего в 1 модуле</th>")
              .Append("<th>В том числе всего в 2 модуле</th>")
              .Append("<th>В том числе всего в 3 модуле</th>")
              .Append("<th>В том числе всего в 4 модуле</th>")
              .Append("<th>Потоки</th>")
              .Append("<th>Группы</th>")
              .Append("<th>Общая ауд нагрузка по лекциям</th>")
              .Append("<th>Общая ауд нагрузка по семинарам</th>")
              .Append("<th>Общая ауд нагрузка по НИС / ПС / СН</th>")
              .Append("<th>Всего ауд нагрузка</th>")
              .Append("<th>Всего ауд нагрузка (с удовением)</th>")
              .Append("<th>Число студентов</th>")
              .Append("<th>Нагрузка на текущий контроль</th>")
              .Append("<th>Всего часов нагрузки</th>")
              .Append("<th>ФИО ППС</th>")
              .Append("</tr></thead><tbody>");

            var derivedHasRows = false;
            await using (var d = await derivedCmd.ExecuteReaderAsync())
            {
                while (await d.ReadAsync())
                {
                    derivedHasRows = true;
                    var op = d.IsDBNull(0) ? "" : d.GetString(0);
                    var level = d.IsDBNull(1) ? "" : d.GetString(1);
                    var moduleName = d.IsDBNull(2) ? "" : d.GetString(2);
                    var discNo = d.IsDBNull(3) ? "" : d.GetString(3);
                    var discName = d.IsDBNull(4) ? "" : d.GetString(4);
                    var depName = d.IsDBNull(5) ? "" : d.GetString(5);
                    var course = PlanRowReader.SafeReadIntOrStringAsString(d, 6);
                    var totalHours = d.IsDBNull(7) ? "" : d.GetDecimal(7).ToString(CultureInfo.InvariantCulture);
                    var facultyNames = d.IsDBNull(8) ? "" : d.GetString(8);

                    sb.Append("<tr>")
                      .Append("<td>").Append(ParseHelpers.H(op)).Append("</td>")
                      .Append("<td>").Append(ParseHelpers.H(level)).Append("</td>")
                      .Append("<td></td>")
                      .Append("<td>").Append(ParseHelpers.H(OpBudgetHelper.OpBudgetCommercial(op))).Append("</td>")
                      .Append("<td>").Append(ParseHelpers.H(moduleName)).Append("</td>")
                      .Append("<td></td>")
                      .Append("<td>").Append(ParseHelpers.H(discNo)).Append("</td>")
                      .Append("<td>").Append(ParseHelpers.H(discName)).Append("</td>")
                      .Append("<td>").Append(ParseHelpers.H(depName)).Append("</td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td>").Append(ParseHelpers.H(course)).Append("</td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td></td>")
                      .Append("<td>").Append(ParseHelpers.H(totalHours)).Append("</td>")
                      .Append("<td>").Append(ParseHelpers.H(facultyNames)).Append("</td>")
                      .Append("</tr>");
                }
            }

            if (!derivedHasRows)
            {
                sb.Append("<tr><td colspan=\"36\">Нет данных по выбранной ОП</td></tr>");
            }
            sb.Append("</tbody></table></div></div></section>");
        }

        await using var wlCmd = new NpgsqlCommand(@"
SELECT
  academic_year,
  op_name,
  module_number,
  module_name,
  discipline_no,
  discipline_name,
  work_type,
  hours,
  faculty_name
FROM v_workload_by_worktype
WHERE
  (@useOpFilter = false OR op_id = ANY(@opIds)) AND
  (@course IS NULL OR course_no = @course) AND
  (@y IS NULL OR academic_year = @y)
ORDER BY discipline_name, work_type
LIMIT 500;
", conn);
        wlCmd.Parameters.AddWithValue("useOpFilter", NpgsqlDbType.Boolean, useOpFilter);
        wlCmd.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam!);
        wlCmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
        wlCmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearValue ?? DBNull.Value);

        sb.Append("<section class=\"card\">")
          .Append("<h2 class=\"h2\">Нагрузка по выбранной ОП</h2>")
          .Append("<div class=\"hint\">Показано до 500 строк</div>")
          .Append("</section>")
          .Append("<section class=\"card card--flush\"><div class=\"table-scroll-top\"><div class=\"table-scroll-bar\" role=\"scrollbar\" aria-orientation=\"horizontal\"><div class=\"table-scroll-bar__inner\"></div></div><div class=\"table-wrap\"><table class=\"table\"><thead><tr>")
          .Append("<th class=\"col-year\">Год</th>")
          .Append("<th>ОП</th>")
          .Append("<th>Бюджет / Коммерческая</th>")
          .Append("<th>Модуль</th>")
          .Append("<th>№ дисциплины</th>")
          .Append("<th>Дисциплина</th>")
          .Append("<th>Вид работ</th>")
          .Append("<th>Часы</th>")
          .Append("<th>Преподаватель</th>")
          .Append("</tr></thead><tbody>");

        var wlHasRows = false;
        await using (var wl = await wlCmd.ExecuteReaderAsync())
        {
            while (await wl.ReadAsync())
            {
                wlHasRows = true;
                var wlOpName = wl.IsDBNull(1) ? "" : wl.GetString(1);
                var modNoStr = PlanRowReader.SafeReadIntOrStringAsString(wl, 2);
                var modNo = int.TryParse(modNoStr, out var mn) ? (int?)mn : null;
                var modName = wl.IsDBNull(3) ? "" : wl.GetString(3);
                sb.Append("<tr>")
                  .Append("<td class=\"col-year\">").Append(ParseHelpers.H(wl.IsDBNull(0) ? "" : wl.GetString(0))).Append("</td>")
                  .Append("<td>").Append(ParseHelpers.H(wlOpName)).Append("</td>")
                  .Append("<td>").Append(ParseHelpers.H(OpBudgetHelper.OpBudgetCommercial(wlOpName))).Append("</td>")
                  .Append("<td>").Append(ParseHelpers.H($"{(modNo is null ? "" : modNo.ToString())} {modName}".Trim())).Append("</td>")
                  .Append("<td>").Append(ParseHelpers.H(wl.IsDBNull(4) ? "" : wl.GetString(4))).Append("</td>")
                  .Append("<td>").Append(ParseHelpers.H(wl.IsDBNull(5) ? "" : wl.GetString(5))).Append("</td>")
                  .Append("<td>").Append(ParseHelpers.H(wl.IsDBNull(6) ? "" : wl.GetString(6))).Append("</td>")
                  .Append("<td>").Append(wl.IsDBNull(7) ? "" : wl.GetDecimal(7).ToString(CultureInfo.InvariantCulture)).Append("</td>")
                  .Append("<td>").Append(ParseHelpers.H(wl.IsDBNull(8) ? "" : wl.GetString(8))).Append("</td>")
                  .Append("</tr>");
            }
        }
        if (!wlHasRows)
        {
            sb.Append("<tr><td colspan=\"9\">Нет данных по выбранной ОП</td></tr>");
        }

        sb.Append("</tbody></table></div></div></section>");
    }

    sb.Append("<section class=\"card card--flush\"><div class=\"table-scroll-top\"><div class=\"table-scroll-bar\" role=\"scrollbar\" aria-orientation=\"horizontal\"><div class=\"table-scroll-bar__inner\"></div></div><div class=\"table-wrap\"><table class=\"table\"><thead><tr>")
      .Append("<th>ОП</th>")
      .Append("<th>Уровень</th>")
      .Append("<th>Направление</th>")
      .Append("<th>Бюджетная / коммерческая</th>")
      .Append("<th>Модуль учебного плана</th>")
      .Append("<th>Подтип модуля</th>")
      .Append("<th>№ дисц.</th>")
      .Append("<th>Наименование дисциплины</th>")
      .Append("<th>Реализующее подразделение</th>")
      .Append("<th>Принадлежность реализующего департамента</th>")
      .Append("<th>Вид дисциплины (обязательная, по выбору, факультатив)</th>")
      .Append("<th>Является ключевым семинаром (НИС, ПС, СНаставника)</th>")
      .Append("<th>Наличие онлайн-курса</th>")
      .Append("<th>Курс</th>")
      .Append("<th>Наличие заявки на МУ от АР</th>")
      .Append("<th>Язык</th>")
      .Append("<th>МКД</th>")
      .Append("<th>Зач.ед.</th>")
      .Append("<th>Лекции по РУП</th>")
      .Append("<th>Семинары по РУП</th>")
      .Append("<th>Всего часов по РУП</th>")
      .Append("<th>В том числе всего в 1 модуле</th>")
      .Append("<th>В том числе всего в 2 модуле</th>")
      .Append("<th>В том числе всего в 3 модуле</th>")
      .Append("<th>В том числе всего в 4 модуле</th>")
      .Append("<th>Потоки</th>")
      .Append("<th>Группы</th>")
      .Append("<th>Общая ауд нагрузка по лекциям</th>")
      .Append("<th>Общая ауд нагрузка по семинарам</th>")
      .Append("<th>Общая ауд нагрузка по НИС / ПС / СН</th>")
      .Append("<th>Всего ауд нагрузка</th>")
      .Append("<th>Всего ауд нагрузка (с удовением)</th>")
      .Append("<th>Число студентов</th>")
      .Append("<th>Нагрузка на текущий контроль</th>")
      .Append("<th>Всего часов нагрузки</th>")
      .Append("<th>ФИО ППС</th>")
      .Append("</tr></thead><tbody>");

    var hasRows = false;
    var defaultPlanId = 0;
    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            hasRows = true;
            int? planDisciplineIdDb = PlanRowReader.SafeParsePlanDisciplineId(r.GetValue(0));
            var planIdDb = PlanRowReader.SafeReadPlanId(r);
            if (defaultPlanId == 0 && planIdDb != 0) defaultPlanId = planIdDb;
            var opsName = r.IsDBNull(1) ? "" : r.GetString(1);
            var level = r.IsDBNull(2) ? "" : r.GetString(2);
            var direction = r.IsDBNull(3) ? "" : r.GetString(3);
            var fundingType = r.IsDBNull(4) ? "" : r.GetString(4);
            var moduleName = r.IsDBNull(5) ? "" : r.GetString(5);
            var moduleSubtype = r.IsDBNull(6) ? "" : r.GetString(6);
            var disciplineNo = r.IsDBNull(7) ? "" : r.GetValue(7)?.ToString() ?? "";
            var disciplineName = r.IsDBNull(8) ? "" : r.GetString(8);
            var implementingDepartment = r.IsDBNull(9) ? "" : r.GetString(9);
            var implementingParent = r.IsDBNull(10) ? "" : r.GetString(10);
            var disciplineKind = r.IsDBNull(11) ? "" : r.GetString(11);
            var isKeySeminar = r.IsDBNull(12) ? (bool?)null : PlanRowReader.SafeReadBoolean(r, 12);
            var hasOnline = r.IsDBNull(13) ? (bool?)null : PlanRowReader.SafeReadBoolean(r, 13);
            var course = PlanRowReader.SafeReadIntOrStringAsString(r, 14);
            var hasMuRequest = r.IsDBNull(15) ? (bool?)null : PlanRowReader.SafeReadBoolean(r, 15);
            var language = r.IsDBNull(16) ? "" : r.GetString(16);
            var mkd = r.IsDBNull(17) ? "" : r.GetString(17);
            var credits = r.IsDBNull(18) ? "" : PlanRowReader.SafeReadDecimal(r, 18).ToString(CultureInfo.InvariantCulture);
            var rupLectures = r.IsDBNull(19) ? "" : PlanRowReader.SafeReadDecimal(r, 19).ToString(CultureInfo.InvariantCulture);
            var rupSeminars = r.IsDBNull(20) ? "" : PlanRowReader.SafeReadDecimal(r, 20).ToString(CultureInfo.InvariantCulture);
            var rupTotal = r.IsDBNull(21) ? "" : PlanRowReader.SafeReadDecimal(r, 21).ToString(CultureInfo.InvariantCulture);
            var hoursM1 = r.IsDBNull(22) ? "" : PlanRowReader.SafeReadDecimal(r, 22).ToString(CultureInfo.InvariantCulture);
            var hoursM2 = r.IsDBNull(23) ? "" : PlanRowReader.SafeReadDecimal(r, 23).ToString(CultureInfo.InvariantCulture);
            var hoursM3 = r.IsDBNull(24) ? "" : PlanRowReader.SafeReadDecimal(r, 24).ToString(CultureInfo.InvariantCulture);
            var hoursM4 = r.IsDBNull(25) ? "" : PlanRowReader.SafeReadDecimal(r, 25).ToString(CultureInfo.InvariantCulture);
            var streams = PlanRowReader.SafeReadIntOrStringAsString(r, 26);
            var groups = PlanRowReader.SafeReadIntOrStringAsString(r, 27);
            var audLectures = r.IsDBNull(28) ? "" : PlanRowReader.SafeReadDecimal(r, 28).ToString(CultureInfo.InvariantCulture);
            var audSeminars = r.IsDBNull(29) ? "" : PlanRowReader.SafeReadDecimal(r, 29).ToString(CultureInfo.InvariantCulture);
            var audNisPsSn = r.IsDBNull(30) ? "" : PlanRowReader.SafeReadDecimal(r, 30).ToString(CultureInfo.InvariantCulture);
            var audTotal = r.IsDBNull(31) ? "" : PlanRowReader.SafeReadDecimal(r, 31).ToString(CultureInfo.InvariantCulture);
            var students = PlanRowReader.SafeReadIntOrStringAsString(r, 32);
            var currentControl = r.IsDBNull(33) ? "" : PlanRowReader.SafeReadDecimal(r, 33).ToString(CultureInfo.InvariantCulture);
            var totalHours = r.IsDBNull(34) ? "" : PlanRowReader.SafeReadDecimal(r, 34).ToString(CultureInfo.InvariantCulture);
            var facultyNames = r.IsDBNull(35) ? "" : r.GetString(35);
            var formId = $"plan-{planDisciplineIdDb?.ToString() ?? "new"}";

            sb.Append("<tr>")
              .Append("<td>").Append(ParseHelpers.H(opsName)).Append("</td>")
              .Append("<td>").Append(ParseHelpers.H(level)).Append("</td>")
              .Append("<td>").Append(ParseHelpers.H(direction)).Append("</td>")
              .Append("<td>").Append(ParseHelpers.H(OpBudgetHelper.OpBudgetCommercial(opsName))).Append("</td>")
              .Append("<td>").Append(ParseHelpers.H(moduleName)).Append("</td>")
              .Append("<td>").Append(ParseHelpers.H(moduleSubtype)).Append("</td>")
              .Append("<td>")
              .Append("<form id=\"").Append(formId).Append("\" method=\"post\" action=\"/uiplan/update\"></form>")
              .Append("<input type=\"hidden\" name=\"planId\" value=\"").Append(planIdDb).Append("\" form=\"").Append(formId).Append("\">")
              .Append("<input type=\"hidden\" name=\"planDisciplineId\" value=\"").Append(planDisciplineIdDb?.ToString() ?? "").Append("\" form=\"").Append(formId).Append("\">")
              .Append("<input class=\"input\" name=\"disciplineNo\" value=\"").Append(ParseHelpers.H(disciplineNo)).Append("\" form=\"").Append(formId).Append("\" placeholder=\"№\">")
              .Append("</td>")
              .Append("<td><input class=\"input\" name=\"disciplineName\" value=\"").Append(ParseHelpers.H(disciplineName)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td>").Append(ParseHelpers.H(implementingDepartment)).Append("</td>")
              .Append("<td><input class=\"input\" name=\"implementingDepParent\" value=\"").Append(ParseHelpers.H(implementingParent)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><select class=\"input\" name=\"disciplineKind\" form=\"").Append(formId).Append("\">").Append(OptionsListStrings(SelectOptions.DisciplineKinds, disciplineKind, "Вид")).Append("</select></td>")
              .Append("<td><label class=\"check\"><input type=\"checkbox\" name=\"isKeySeminar\" ").Append(isKeySeminar is not null && isKeySeminar.Value ? "checked" : "").Append(" form=\"").Append(formId).Append("\">Да</label></td>")
              .Append("<td><label class=\"check\"><input type=\"checkbox\" name=\"hasOnlineCourse\" ").Append(hasOnline is not null && hasOnline.Value ? "checked" : "").Append(" form=\"").Append(formId).Append("\">Да</label></td>")
              .Append("<td><input class=\"input\" name=\"courseNo\" value=\"").Append(ParseHelpers.H(course)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><label class=\"check\"><input type=\"checkbox\" name=\"hasMuRequest\" ").Append(hasMuRequest is not null && hasMuRequest.Value ? "checked" : "").Append(" form=\"").Append(formId).Append("\">Да</label></td>")
              .Append("<td><select class=\"input\" name=\"language\" form=\"").Append(formId).Append("\">").Append(OptionsListStrings(SelectOptions.LanguageOptions, language, "Язык")).Append("</select></td>")
              .Append("<td><input class=\"input\" name=\"mkd\" value=\"").Append(ParseHelpers.H(mkd)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"credits\" value=\"").Append(ParseHelpers.H(credits)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"rupLectures\" value=\"").Append(ParseHelpers.H(rupLectures)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"rupSeminars\" value=\"").Append(ParseHelpers.H(rupSeminars)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"rupTotal\" value=\"").Append(ParseHelpers.H(rupTotal)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"hoursM1\" value=\"").Append(ParseHelpers.H(hoursM1)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"hoursM2\" value=\"").Append(ParseHelpers.H(hoursM2)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"hoursM3\" value=\"").Append(ParseHelpers.H(hoursM3)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"hoursM4\" value=\"").Append(ParseHelpers.H(hoursM4)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"streams\" value=\"").Append(ParseHelpers.H(streams)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"groups\" value=\"").Append(ParseHelpers.H(groups)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"audLectures\" value=\"").Append(ParseHelpers.H(audLectures)).Append("\" form=\"").Append(formId).Append("\" placeholder=\"Лекции\"></td>")
              .Append("<td><input class=\"input\" name=\"audSeminars\" value=\"").Append(ParseHelpers.H(audSeminars)).Append("\" form=\"").Append(formId).Append("\" placeholder=\"Семинары\"></td>")
              .Append("<td><input class=\"input\" name=\"audNisPsSn\" value=\"").Append(ParseHelpers.H(audNisPsSn)).Append("\" form=\"").Append(formId).Append("\" placeholder=\"НИС/ПС/СН\"></td>")
              .Append("<td><input class=\"input\" name=\"audTotal\" value=\"").Append(ParseHelpers.H(audTotal)).Append("\" form=\"").Append(formId).Append("\" placeholder=\"Всего ауд\"></td>")
              .Append("<td><input class=\"input\" name=\"audTotalWith\" value=\"").Append(ParseHelpers.H(audTotal)).Append("\" form=\"").Append(formId).Append("\" placeholder=\"С удовением\"></td>")
              .Append("<td><input class=\"input\" name=\"students\" value=\"").Append(ParseHelpers.H(students)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"currentControl\" value=\"").Append(ParseHelpers.H(currentControl)).Append("\" form=\"").Append(formId).Append("\"></td>")
              .Append("<td><input class=\"input\" name=\"totalHours\" value=\"").Append(ParseHelpers.H(totalHours)).Append("\" form=\"").Append(formId).Append("\" placeholder=\"Всего часов\"></td>")
              .Append("<td>")
              .Append(ParseHelpers.H(facultyNames))
              .Append(" <button class=\"btn\" type=\"submit\" form=\"").Append(formId).Append("\">Сохранить</button>")
              .Append("</td>")
              .Append("</tr>");
        }
    }


    if (!hasRows && useOpFilter && opIdsParam != null && opIdsParam.Length > 0 && opIdsParam[0] != -1)
    {
        await using var defPlanCmd = new NpgsqlCommand("SELECT plan_id FROM plan_programs WHERE op_id = ANY(@opIds) LIMIT 1", conn);
        defPlanCmd.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam);
        var defPlan = await defPlanCmd.ExecuteScalarAsync();
        if (defPlan is int dp) defaultPlanId = dp;
    }

    var planOptions = new List<(int planId, string opName)>();
    await using (var planOptCmd = new NpgsqlCommand(@"
SELECT pp.plan_id, ep.name
FROM plan_programs pp
JOIN educational_programs ep ON ep.op_id = pp.op_id
WHERE ep.is_active = true
  AND (@isAdmin OR (array_length(@allowedOpNames, 1) > 0 AND ep.name = ANY(@allowedOpNames)))
ORDER BY ep.name", conn))
    {
        planOptCmd.Parameters.AddWithValue("isAdmin", NpgsqlDbType.Boolean, user.IsAdmin);
        planOptCmd.Parameters.AddWithValue("allowedOpNames", NpgsqlDbType.Array | NpgsqlDbType.Text, user.AllowedOpNames.Length == 0 ? Array.Empty<string>() : user.AllowedOpNames);
        await using var planOptR = await planOptCmd.ExecuteReaderAsync();
        while (await planOptR.ReadAsync())
            planOptions.Add((PlanRowReader.SafeReadInt32(planOptR, 0), planOptR.IsDBNull(1) ? "" : planOptR.GetString(1)));
    }

    if (!hasRows)
    {
        sb.Append("<tr><td colspan=\"36\">Нет данных по выбранным параметрам. Нажмите «Добавить строку» и выберите ОП в новой строке.</td></tr>");
    }
    if (planOptions.Count > 0)
    {
        var newFormId = "plan-new-template";
        var planSelectOptions = new StringBuilder();
        foreach (var (pid, label) in planOptions)
            planSelectOptions.Append("<option value=\"").Append(pid).Append("\"").Append(pid == defaultPlanId ? " selected" : "").Append(">").Append(ParseHelpers.H(label)).Append("</option>");
        sb.Append("<tr class=\"plan-new-template\" style=\"display:none\" data-plan-id=\"0\">")
          .Append("<td><select class=\"input\" name=\"planId\" form=\"").Append(newFormId).Append("\" required>").Append(planSelectOptions).Append("</select></td>")
          .Append("<td>—</td><td>—</td><td>—</td><td>—</td><td>—</td><td>")
          .Append("<form id=\"").Append(newFormId).Append("\" method=\"post\" action=\"/uiplan/update\"></form>")
          .Append("<input type=\"hidden\" name=\"planDisciplineId\" value=\"\" form=\"").Append(newFormId).Append("\">")
          .Append("<input class=\"input\" name=\"disciplineNo\" value=\"\" form=\"").Append(newFormId).Append("\" placeholder=\"№\"></td>")
          .Append("<td><input class=\"input\" name=\"disciplineName\" value=\"\" form=\"").Append(newFormId).Append("\" placeholder=\"Название\"></td>")
          .Append("<td>—</td><td><input class=\"input\" name=\"implementingDepParent\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><select class=\"input\" name=\"disciplineKind\" form=\"").Append(newFormId).Append("\">").Append(OptionsListStrings(SelectOptions.DisciplineKinds, "", "Вид")).Append("</select></td>")
          .Append("<td><label class=\"check\"><input type=\"checkbox\" name=\"isKeySeminar\" form=\"").Append(newFormId).Append("\">Да</label></td>")
          .Append("<td><label class=\"check\"><input type=\"checkbox\" name=\"hasOnlineCourse\" form=\"").Append(newFormId).Append("\">Да</label></td>")
          .Append("<td><input class=\"input\" name=\"courseNo\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><label class=\"check\"><input type=\"checkbox\" name=\"hasMuRequest\" form=\"").Append(newFormId).Append("\">Да</label></td>")
          .Append("<td><select class=\"input\" name=\"language\" form=\"").Append(newFormId).Append("\">").Append(OptionsListStrings(SelectOptions.LanguageOptions, "", "Язык")).Append("</select></td>")
          .Append("<td><input class=\"input\" name=\"mkd\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"credits\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"rupLectures\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"rupSeminars\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"rupTotal\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"hoursM1\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"hoursM2\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"hoursM3\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"hoursM4\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"streams\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"groups\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"audLectures\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"audSeminars\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"audNisPsSn\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"audTotal\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"audTotalWith\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"students\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"currentControl\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><input class=\"input\" name=\"totalHours\" value=\"\" form=\"").Append(newFormId).Append("\"></td>")
          .Append("<td><button class=\"btn\" type=\"submit\" form=\"").Append(newFormId).Append("\">Сохранить</button></td>")
          .Append("</tr>");
    }

    sb.Append("</tbody></table></div></div></section>");
    return Results.Content(Layout("Учебный план", "plan", sb.ToString(), user.Login, user.IsAdmin), "text/html; charset=utf-8");
    }
    catch (PostgresException ex) when (ex.SqlState == "42703")
    {
        var msg = $"Ошибка БД: {ex.Message}. Возможно, не выполнена миграция (add_aud_total_columns.sql) или нужно пересобрать проект (dotnet build) и перезапустить.";
        return Results.Content(Layout("Учебный план — ошибка", "plan", $"<section class='card'><p class='error'>{ParseHelpers.H(msg)}</p></section>", user.Login, user.IsAdmin), "text/html; charset=utf-8");
    }
});

app.MapGet("/uiplan/export", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    string? year,
    string[]? opName,
    string? courseNo,
    string[]? departmentId,
    string[]? moduleNo
) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    var opNames = (opName ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
    if (!user.IsAdmin && user.AllowedOpNames.Length > 0)
    {
        var allowedSet = new HashSet<string>(user.AllowedOpNames, StringComparer.OrdinalIgnoreCase);
        opNames = opNames.Length == 0 ? user.AllowedOpNames : opNames.Where(o => allowedSet.Contains(o)).ToArray();
    }
    string[]? opNamesLike;
    if (!user.IsAdmin && user.AllowedOpNames.Length == 0)
        opNamesLike = new[] { "%\u0000%" };
    else
        opNamesLike = opNames.Length == 0 ? null : opNames.Select(n => $"%{n}%").ToArray();
    var courseNoValue = ParseHelpers.IntOrNull(courseNo);
    var moduleNos = ParseModuleNos(moduleNo);
    var departmentIds = ParseDepartmentIds(departmentId);
    if (!user.IsAdmin && user.AllowedDepartmentIds.Length > 0)
    {
        var allowedDept = new HashSet<int>(user.AllowedDepartmentIds);
        departmentIds = departmentIds.Length == 0 ? user.AllowedDepartmentIds : departmentIds.Where(d => allowedDept.Contains(d)).ToArray();
    }
    if (!user.IsAdmin && user.AllowedDepartmentIds.Length == 0)
        departmentIds = new[] { -1 };
    var yearValue = string.IsNullOrWhiteSpace(year) ? null : year.Trim();

    await using var conn = await ds.OpenConnectionAsync();
    var opIds = new List<int>();
    if (opNamesLike is not null)
    {
        await using var opIdCmd = new NpgsqlCommand(@"
SELECT op_id FROM educational_programs WHERE name ILIKE ANY(@opNames);", conn);
        opIdCmd.Parameters.AddWithValue("opNames", NpgsqlDbType.Array | NpgsqlDbType.Text, (object?)opNamesLike ?? DBNull.Value);
        await using var opIdReader = await opIdCmd.ExecuteReaderAsync();
        while (await opIdReader.ReadAsync()) opIds.Add(PlanRowReader.SafeReadInt32(opIdReader, 0));
    }
    var useOpFilter = opNames.Length > 0;
    var opIdsParam = opIds.Count == 0 ? new[] { -1 } : opIds.ToArray();

    var cmdText = @"
SELECT
  pd.plan_discipline_id AS id,
  ep.name AS ops,
  ep.education_level,
  sp.direction,
  sp.funding_type,
  pm.module_name,
  pm.module_subtype,
  pd.discipline_no,
  pd.discipline_name,
  d.name AS implementing_department,
  pd.implementing_dep_parent,
  pd.discipline_kind::text AS discipline_kind,
  pd.is_key_seminar,
  pd.has_online_course,
  pd.course_no,
  pd.has_mu_request,
  pd.language,
  pd.mkd,
  pd.credits,
  pd.rup_lectures_hours,
  pd.rup_seminars_hours,
  pd.rup_total_hours,
  pd.hours_module1,
  pd.hours_module2,
  pd.hours_module3,
  pd.hours_module4,
  pd.streams_count,
  pd.groups_count,
  NULL::numeric AS aud_lecture_hours,
  NULL::numeric AS aud_seminar_hours,
  NULL::numeric AS aud_nis_ps_sn_hours,
  NULL::numeric AS aud_total_hours,
  pd.students_count,
  pd.current_control_hours,
  NULL::numeric AS total_hours,
  NULL::text AS faculty_names,
  sp.academic_year
FROM plan_disciplines pd
JOIN plan_discipline_programs pdp ON pd.plan_discipline_id = pdp.plan_discipline_id
JOIN plan_programs pp ON pdp.plan_program_id = pp.plan_program_id
JOIN educational_programs ep ON pp.op_id = ep.op_id
JOIN study_plans sp ON pd.plan_id = sp.plan_id
LEFT JOIN plan_modules pm ON pd.module_id = pm.module_id
LEFT JOIN departments d ON pd.implementing_department_id = d.department_id
WHERE
  (@useOpFilter = false OR ep.op_id = ANY(@opIds)) AND
  (@course IS NULL OR pd.course_no = @course) AND
  (@y IS NULL OR sp.academic_year = @y) AND
  (@departmentIds IS NULL OR array_length(@departmentIds, 1) IS NULL OR pd.implementing_department_id = ANY(@departmentIds)) AND
  (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR
   ((1 = ANY(@moduleNos) AND COALESCE(pd.hours_module1, 0) > 0) OR (2 = ANY(@moduleNos) AND COALESCE(pd.hours_module2, 0) > 0) OR (3 = ANY(@moduleNos) AND COALESCE(pd.hours_module3, 0) > 0) OR (4 = ANY(@moduleNos) AND COALESCE(pd.hours_module4, 0) > 0)))
ORDER BY pd.discipline_name;
";
    await using var cmd = new NpgsqlCommand(cmdText, conn);
    cmd.Parameters.AddWithValue("useOpFilter", NpgsqlDbType.Boolean, useOpFilter);
    var planExportModuleNosParam = moduleNos.Length == 0 ? (object?)null : moduleNos;
    var planExportDepartmentIdsParam = departmentIds.Length == 0 ? (object?)null : departmentIds;
    cmd.Parameters.AddWithValue("opIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, opIdsParam);
    cmd.Parameters.AddWithValue("course", NpgsqlDbType.Integer, (object?)courseNoValue ?? DBNull.Value);
    cmd.Parameters.AddWithValue("y", NpgsqlDbType.Text, (object?)yearValue ?? DBNull.Value);
    cmd.Parameters.AddWithValue("departmentIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)planExportDepartmentIdsParam ?? DBNull.Value);
    cmd.Parameters.AddWithValue("moduleNos", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)planExportModuleNosParam ?? DBNull.Value);

    using var wb = new XLWorkbook();
    var ws = wb.AddWorksheet("Учебный план");
    var headers = new[]
    {
        "ОП", "Уровень", "Направление", "Бюджетная / коммерческая",
        "Модуль учебного плана", "Подтип модуля", "№ дисц.", "Наименование дисциплины",
        "Реализующее подразделение", "Принадлежность реализующего департамента",
        "Вид дисциплины (обязательная, по выбору, факультатив)",
        "Является ключевым семинаром (НИС, ПС, СНаставника)", "Наличие онлайн-курса",
        "Курс", "Наличие заявки на МУ от АР", "Язык", "МКД", "Зач.ед.",
        "Лекции по РУП", "Семинары по РУП", "Всего часов по РУП",
        "В том числе всего в 1 модуле", "В том числе всего в 2 модуле",
        "В том числе всего в 3 модуле", "В том числе всего в 4 модуле",
        "Потоки", "Группы", "Общая ауд нагрузка по лекциям",
        "Общая ауд нагрузка по семинарам", "Общая ауд нагрузка по НИС / ПС / СН",
        "Всего ауд нагрузка", "Число студентов", "Нагрузка на текущий контроль", "Всего часов нагрузки",
        "ФИО ППС", "Год"
    };
    for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];

    var row = 2;
    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            // skip id (0), then 1..36 -> cols 1..35
            ws.Cell(row, 1).Value = r.IsDBNull(1) ? "" : r.GetString(1);
            ws.Cell(row, 2).Value = r.IsDBNull(2) ? "" : r.GetString(2);
            ws.Cell(row, 3).Value = r.IsDBNull(3) ? "" : r.GetString(3);
            ws.Cell(row, 4).Value = OpBudgetHelper.OpBudgetCommercial(r.IsDBNull(1) ? null : r.GetString(1));
            ws.Cell(row, 5).Value = r.IsDBNull(5) ? "" : r.GetString(5);
            ws.Cell(row, 6).Value = r.IsDBNull(6) ? "" : r.GetString(6);
            ws.Cell(row, 7).Value = r.IsDBNull(7) ? "" : r.GetValue(7)?.ToString() ?? "";
            ws.Cell(row, 8).Value = r.IsDBNull(8) ? "" : r.GetString(8);
            ws.Cell(row, 9).Value = r.IsDBNull(9) ? "" : r.GetString(9);
            ws.Cell(row, 10).Value = r.IsDBNull(10) ? "" : r.GetString(10);
            ws.Cell(row, 11).Value = r.IsDBNull(11) ? "" : r.GetString(11);
            ws.Cell(row, 12).Value = r.IsDBNull(12) ? "" : (r.GetBoolean(12) ? "Да" : "Нет");
            ws.Cell(row, 13).Value = r.IsDBNull(13) ? "" : (r.GetBoolean(13) ? "Да" : "Нет");
            ws.Cell(row, 14).Value = r.IsDBNull(14) ? "" : (r.GetValue(14) is int i14 ? i14 : r.GetValue(14)?.ToString() ?? "");
            ws.Cell(row, 15).Value = r.IsDBNull(15) ? "" : (r.GetBoolean(15) ? "Да" : "Нет");
            ws.Cell(row, 16).Value = r.IsDBNull(16) ? "" : r.GetString(16);
            ws.Cell(row, 17).Value = r.IsDBNull(17) ? "" : r.GetString(17);
            ws.Cell(row, 18).Value = r.IsDBNull(18) ? "" : r.GetDecimal(18);
            ws.Cell(row, 19).Value = r.IsDBNull(19) ? "" : r.GetDecimal(19);
            ws.Cell(row, 20).Value = r.IsDBNull(20) ? "" : r.GetDecimal(20);
            ws.Cell(row, 21).Value = r.IsDBNull(21) ? "" : r.GetDecimal(21);
            ws.Cell(row, 22).Value = r.IsDBNull(22) ? "" : r.GetDecimal(22);
            ws.Cell(row, 23).Value = r.IsDBNull(23) ? "" : r.GetDecimal(23);
            ws.Cell(row, 24).Value = r.IsDBNull(24) ? "" : r.GetDecimal(24);
            ws.Cell(row, 25).Value = r.IsDBNull(25) ? "" : (r.GetValue(25) is int i25 ? i25 : r.GetValue(25)?.ToString() ?? "");
            ws.Cell(row, 26).Value = r.IsDBNull(26) ? "" : (r.GetValue(26) is int i26 ? i26 : r.GetValue(26)?.ToString() ?? "");
            ws.Cell(row, 27).Value = r.IsDBNull(28) ? "" : r.GetValue(28)?.ToString() ?? "";
            ws.Cell(row, 28).Value = r.IsDBNull(29) ? "" : r.GetValue(29)?.ToString() ?? "";
            ws.Cell(row, 29).Value = r.IsDBNull(30) ? "" : r.GetValue(30)?.ToString() ?? "";
            ws.Cell(row, 30).Value = r.IsDBNull(31) ? "" : r.GetValue(31)?.ToString() ?? "";
            ws.Cell(row, 31).Value = r.IsDBNull(32) ? "" : r.GetValue(32)?.ToString() ?? "";
            ws.Cell(row, 32).Value = r.IsDBNull(33) ? "" : r.GetValue(33)?.ToString() ?? "";
            ws.Cell(row, 33).Value = r.IsDBNull(34) ? "" : r.GetValue(34)?.ToString() ?? "";
            ws.Cell(row, 34).Value = r.IsDBNull(34) ? "" : r.GetValue(34)?.ToString() ?? "";
            ws.Cell(row, 35).Value = r.IsDBNull(35) ? "" : r.GetString(35);
            if (r.FieldCount > 36) ws.Cell(row, 36).Value = r.IsDBNull(36) ? "" : r.GetValue(36)?.ToString() ?? "";
            row++;
        }
    }

    byte[] bytes;
    using (var stream = new MemoryStream())
    {
        wb.SaveAs(stream, false);
        bytes = stream.ToArray();
    }
    var fileName = $"plan_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
    return Results.File(bytes,
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        fileName);
});

app.MapPost("/uiplan/import", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditPlan) return Results.Forbid();
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        var q = form["redirectQuery"].ToString();
        return Results.Redirect("/uiplan" + (string.IsNullOrEmpty(q) ? "?importError=noFile" : q + (q.Contains("?") ? "&" : "?") + "importError=noFile"));
    }
    var redirectQuery = form["redirectQuery"].ToString();
    var planColumnMap = new Dictionary<string, string[]>
    {
        ["year"] = new[] { "Год", "Учебный год", "Год учебный" },
        ["op"] = new[] { "ОП", "Образовательная программа", "Образова" },
        ["discipline_no"] = new[] { "№ дисц.", "№ дисциплины", "№ дисцип" },
        ["discipline_name"] = new[] { "Наименование дисциплины", "Наименование", "Дисциплина" },
        ["implementing_department"] = new[] { "Реализующее подразделение", "Департамент" },
        ["discipline_kind"] = new[] { "Вид дисциплины (обязательная, по выбору, факультатив)", "Вид дисциплины" },
        ["language"] = new[] { "Язык" },
        ["credits"] = new[] { "Зач.ед." },
        ["course_no"] = new[] { "Курс" },
        ["rup_lectures"] = new[] { "Лекции по РУП" },
        ["rup_seminars"] = new[] { "Семинары по РУП" },
        ["hours_m1"] = new[] { "В том числе всего в 1 модуле" },
        ["hours_m2"] = new[] { "В том числе всего в 2 модуле" },
        ["hours_m3"] = new[] { "В том числе всего в 3 модуле" },
        ["hours_m4"] = new[] { "В том числе всего в 4 модуле" }
    };
    List<Dictionary<string, string>> rows;
    try
    {
        await using var stream = file.OpenReadStream();
        rows = ExcelImportHelper.ParseSheet(stream, planColumnMap);
    }
    catch (Exception ex)
    {
        return Results.Redirect("/uiplan" + (string.IsNullOrEmpty(redirectQuery) ? "?" : redirectQuery + "&") + "importError=" + Uri.EscapeDataString(ex.Message));
    }
    var errors = new List<string>();
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    foreach (var row in rows)
    {
        var yearVal = ExcelImportHelper.Get(row, "year");
        var disciplineNo = ExcelImportHelper.Get(row, "discipline_no");
        var disciplineName = (ExcelImportHelper.Get(row, "discipline_name") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(disciplineName)) continue;
        var opName = ExcelImportHelper.Get(row, "op");
        var departmentName = ExcelImportHelper.Get(row, "implementing_department");
        var yearNorm = (yearVal ?? "").Trim();
        var yearAlt = yearNorm.Replace("-", "/");
        if (yearAlt.Length == 4 && int.TryParse(yearAlt, out var y) && y >= 2000 && y < 2100)
            yearAlt = $"{y}/{y + 1}";
        if (string.IsNullOrWhiteSpace(yearAlt)) yearAlt = yearNorm;

        var (planDisciplineId, createError) = await GetOrCreatePlanDisciplineForImport(conn, tx, yearNorm, yearAlt, disciplineNo, disciplineName, opName, departmentName);
        if (createError != null)
        {
            errors.Add($"Дисциплина «{yearVal} / {disciplineName}»: {createError}");
            continue;
        }
        if (planDisciplineId is null)
        {
            errors.Add($"Не найдена строка плана: {yearVal} / {disciplineName}. Добавьте в Учебный план (год {yearAlt}).");
            continue;
        }
        var disciplineKind = ExcelImportHelper.Get(row, "discipline_kind");
        var language = ExcelImportHelper.Get(row, "language");
        var credits = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "credits"));
        var courseNo = ParseHelpers.IntOrNull(ExcelImportHelper.Get(row, "course_no"));
        var rupLectures = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "rup_lectures"));
        var rupSeminars = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "rup_seminars"));
        var h1 = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "hours_m1"));
        var h2 = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "hours_m2"));
        var h3 = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "hours_m3"));
        var h4 = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "hours_m4"));
        await using var upd = new NpgsqlCommand(@"
UPDATE plan_disciplines SET
  discipline_kind = (COALESCE(@discKind, discipline_kind::text))::discipline_kind,
  language = COALESCE(@language, language),
  credits = COALESCE(@credits, credits),
  course_no = COALESCE(@courseNo, course_no),
  rup_lectures_hours = COALESCE(@rupL, rup_lectures_hours),
  rup_seminars_hours = COALESCE(@rupS, rup_seminars_hours),
  hours_module1 = COALESCE(@h1, hours_module1),
  hours_module2 = COALESCE(@h2, hours_module2),
  hours_module3 = COALESCE(@h3, hours_module3),
  hours_module4 = COALESCE(@h4, hours_module4)
WHERE plan_discipline_id = @id", conn, tx);
        upd.Parameters.AddWithValue("discKind", NpgsqlDbType.Text, (object?)disciplineKind ?? DBNull.Value);
        upd.Parameters.AddWithValue("language", NpgsqlDbType.Text, (object?)language ?? DBNull.Value);
        upd.Parameters.AddWithValue("credits", NpgsqlDbType.Numeric, (object?)credits ?? DBNull.Value);
        upd.Parameters.AddWithValue("courseNo", NpgsqlDbType.Integer, (object?)courseNo ?? DBNull.Value);
        upd.Parameters.AddWithValue("rupL", NpgsqlDbType.Numeric, (object?)rupLectures ?? DBNull.Value);
        upd.Parameters.AddWithValue("rupS", NpgsqlDbType.Numeric, (object?)rupSeminars ?? DBNull.Value);
        upd.Parameters.AddWithValue("h1", NpgsqlDbType.Numeric, (object?)h1 ?? DBNull.Value);
        upd.Parameters.AddWithValue("h2", NpgsqlDbType.Numeric, (object?)h2 ?? DBNull.Value);
        upd.Parameters.AddWithValue("h3", NpgsqlDbType.Numeric, (object?)h3 ?? DBNull.Value);
        upd.Parameters.AddWithValue("h4", NpgsqlDbType.Numeric, (object?)h4 ?? DBNull.Value);
        upd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, planDisciplineId.Value);
        await upd.ExecuteNonQueryAsync();
    }
    if (errors.Count > 10) errors.Add($"… всего ошибок: {errors.Count}");
    if (errors.Count > 0)
    {
        await tx.RollbackAsync();
        return Results.Redirect("/uiplan" + (string.IsNullOrEmpty(redirectQuery) ? "?" : redirectQuery + "&") + "importError=" + Uri.EscapeDataString(string.Join("; ", errors.Take(10))));
    }
    await tx.CommitAsync();
    return Results.Redirect("/uiplan" + (string.IsNullOrEmpty(redirectQuery) ? "" : redirectQuery));
}).DisableAntiforgery();

app.MapPost("/uiplan/update", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    string? planId,
    string? planDisciplineId,
    string? disciplineNo,
    string? disciplineName,
    string? moduleId,
    string? implementingDepartmentId,
    string? implementingDepParent,
    string? disciplineKind,
    string? isKeySeminar,
    string? hasOnlineCourse,
    string? courseNo,
    string? hasMuRequest,
    string? language,
    string? mkd,
    string? credits,
    string? rupLectures,
    string? rupSeminars,
    string? rupTotal,
    string? hoursM1,
    string? hoursM2,
    string? hoursM3,
    string? hoursM4,
    string? streams,
    string? groups,
    string? students,
    string? currentControl,
    string? audLectures,
    string? audSeminars,
    string? audNisPsSn,
    string? audTotal,
    string? totalHours
) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditPlan) return Results.Forbid();
    var planDisciplineIdValue = ParseHelpers.IntOrNull(planDisciplineId);
    var planIdValue = ParseHelpers.IntOrNull(planId);
    var errors = new List<string>();

    if (string.IsNullOrWhiteSpace(disciplineName))
        errors.Add("Наименование дисциплины обязательно.");
    var creditsVal = ParseHelpers.DecimalOrNull(credits);
    if (creditsVal is not null && creditsVal < 0)
        errors.Add("Зач.ед. не могут быть отрицательными.");
    var courseNoVal = ParseHelpers.IntOrNull(courseNo);
    if (courseNoVal is not null && (courseNoVal < 1 || courseNoVal > 6))
        errors.Add("Курс должен быть от 1 до 6.");
    if (planDisciplineIdValue is null && planIdValue is null)
        errors.Add("Не указана дисциплина для сохранения.");
    if (errors.Count > 0)
        return Results.BadRequest(string.Join("\n", errors));

    await using var conn = await ds.OpenConnectionAsync();
    if (planDisciplineIdValue is null)
    {
        if (planIdValue is null)
            return Results.Redirect("/uiplan");

        await using var insert = new NpgsqlCommand(@"
INSERT INTO plan_disciplines (
    plan_id, module_id, discipline_no, discipline_name, implementing_department_id,
    implementing_dep_parent, discipline_kind, is_key_seminar, has_online_course,
    course_no, has_mu_request, language, mkd, credits, rup_lectures_hours,
    rup_seminars_hours, rup_total_hours, hours_module1, hours_module2, hours_module3,
    hours_module4, streams_count, groups_count, students_count, current_control_hours
)
VALUES (
    @planId, @moduleId, @disciplineNo, @disciplineName, @implDeptId,
    @implDepParent, @disciplineKind, @isKeySeminar, @hasOnline,
    @courseNo, @hasMuRequest, @language, @mkd, @credits, @rupLectures,
    @rupSeminars, @rupTotal, @hoursM1, @hoursM2, @hoursM3,
    @hoursM4, @streams, @groups, @students, @currentControl
) RETURNING plan_discipline_id;
", conn);
        insert.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planIdValue.Value);
        insert.Parameters.AddWithValue("disciplineNo", NpgsqlDbType.Text, (object?)disciplineNo ?? DBNull.Value);
        insert.Parameters.AddWithValue("disciplineName", NpgsqlDbType.Text, (object?)disciplineName ?? DBNull.Value);
        insert.Parameters.AddWithValue("moduleId", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(moduleId) ?? DBNull.Value);
        insert.Parameters.AddWithValue("implDeptId", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(implementingDepartmentId) ?? DBNull.Value);
        insert.Parameters.AddWithValue("implDepParent", NpgsqlDbType.Text, (object?)implementingDepParent ?? DBNull.Value);
        insert.Parameters.AddWithValue("disciplineKind", NpgsqlDbType.Text, (object?)disciplineKind ?? DBNull.Value);
        insert.Parameters.AddWithValue("isKeySeminar", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(isKeySeminar) ?? DBNull.Value);
        insert.Parameters.AddWithValue("hasOnline", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(hasOnlineCourse) ?? DBNull.Value);
        insert.Parameters.AddWithValue("courseNo", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(courseNo) ?? DBNull.Value);
        insert.Parameters.AddWithValue("hasMuRequest", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(hasMuRequest) ?? DBNull.Value);
        insert.Parameters.AddWithValue("language", NpgsqlDbType.Text, (object?)language ?? DBNull.Value);
        insert.Parameters.AddWithValue("mkd", NpgsqlDbType.Text, (object?)mkd ?? DBNull.Value);
        insert.Parameters.AddWithValue("credits", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(credits) ?? DBNull.Value);
        insert.Parameters.AddWithValue("rupLectures", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(rupLectures) ?? DBNull.Value);
        insert.Parameters.AddWithValue("rupSeminars", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(rupSeminars) ?? DBNull.Value);
        insert.Parameters.AddWithValue("rupTotal", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(rupTotal) ?? DBNull.Value);
        insert.Parameters.AddWithValue("hoursM1", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(hoursM1) ?? DBNull.Value);
        insert.Parameters.AddWithValue("hoursM2", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(hoursM2) ?? DBNull.Value);
        insert.Parameters.AddWithValue("hoursM3", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(hoursM3) ?? DBNull.Value);
        insert.Parameters.AddWithValue("hoursM4", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(hoursM4) ?? DBNull.Value);
        insert.Parameters.AddWithValue("streams", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(streams) ?? DBNull.Value);
        insert.Parameters.AddWithValue("groups", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(groups) ?? DBNull.Value);
        insert.Parameters.AddWithValue("students", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(students) ?? DBNull.Value);
        insert.Parameters.AddWithValue("currentControl", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(currentControl) ?? DBNull.Value);
        var newPlanDisciplineId = (int)(await insert.ExecuteScalarAsync() ?? 0);
        if (newPlanDisciplineId != 0)
        {
            await using var linkCmd = new NpgsqlCommand("SELECT plan_program_id FROM plan_programs WHERE plan_id = @planId LIMIT 1", conn);
            linkCmd.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planIdValue!.Value);
            var ppId = await linkCmd.ExecuteScalarAsync();
            if (ppId is int planProgramId)
            {
                await using var insLink = new NpgsqlCommand("INSERT INTO plan_discipline_programs (plan_discipline_id, plan_program_id) VALUES (@pdId, @ppId)", conn);
                insLink.Parameters.AddWithValue("pdId", NpgsqlDbType.Integer, newPlanDisciplineId);
                insLink.Parameters.AddWithValue("ppId", NpgsqlDbType.Integer, planProgramId);
                await insLink.ExecuteNonQueryAsync();
            }
        }
    }
    else
    {
        await using var cmd = new NpgsqlCommand(@"
UPDATE plan_disciplines
SET discipline_no = COALESCE(@disciplineNo, discipline_no),
    discipline_name = COALESCE(@disciplineName, discipline_name),
    module_id = COALESCE(@moduleId, module_id),
    implementing_department_id = COALESCE(@implDeptId, implementing_department_id),
    implementing_dep_parent = COALESCE(@implDepParent, implementing_dep_parent),
    discipline_kind = (COALESCE(@disciplineKind, discipline_kind::text))::discipline_kind,
    is_key_seminar = COALESCE(@isKeySeminar, is_key_seminar),
    has_online_course = COALESCE(@hasOnline, has_online_course),
    course_no = COALESCE(@courseNo, course_no),
    has_mu_request = COALESCE(@hasMuRequest, has_mu_request),
    language = COALESCE(@language, language),
    mkd = COALESCE(@mkd, mkd),
    credits = COALESCE(@credits, credits),
    rup_lectures_hours = COALESCE(@rupLectures, rup_lectures_hours),
    rup_seminars_hours = COALESCE(@rupSeminars, rup_seminars_hours),
    rup_total_hours = COALESCE(@rupTotal, rup_total_hours),
    hours_module1 = COALESCE(@hoursM1, hours_module1),
    hours_module2 = COALESCE(@hoursM2, hours_module2),
    hours_module3 = COALESCE(@hoursM3, hours_module3),
    hours_module4 = COALESCE(@hoursM4, hours_module4),
    streams_count = COALESCE(@streams, streams_count),
    groups_count = COALESCE(@groups, groups_count),
    students_count = COALESCE(@students, students_count),
    current_control_hours = COALESCE(@currentControl, current_control_hours),
    aud_lecture_hours = COALESCE(@audLectures, aud_lecture_hours),
    aud_seminar_hours = COALESCE(@audSeminars, aud_seminar_hours),
    aud_nis_ps_sn_hours = COALESCE(@audNisPsSn, aud_nis_ps_sn_hours),
    aud_total_hours = COALESCE(@audTotal, aud_total_hours),
    total_hours = COALESCE(@totalHours, total_hours)
WHERE plan_discipline_id = @planDisciplineId;
", conn);
        cmd.Parameters.AddWithValue("planDisciplineId", NpgsqlDbType.Integer, planDisciplineIdValue.Value);
        cmd.Parameters.AddWithValue("disciplineNo", NpgsqlDbType.Text, (object?)disciplineNo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("disciplineName", NpgsqlDbType.Text, (object?)disciplineName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("moduleId", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(moduleId) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("implDeptId", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(implementingDepartmentId) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("implDepParent", NpgsqlDbType.Text, (object?)implementingDepParent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("disciplineKind", NpgsqlDbType.Text, (object?)disciplineKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("isKeySeminar", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(isKeySeminar) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hasOnline", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(hasOnlineCourse) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("courseNo", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(courseNo) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hasMuRequest", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(hasMuRequest) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("language", NpgsqlDbType.Text, (object?)language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mkd", NpgsqlDbType.Text, (object?)mkd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("credits", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(credits) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rupLectures", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(rupLectures) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rupSeminars", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(rupSeminars) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rupTotal", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(rupTotal) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hoursM1", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(hoursM1) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hoursM2", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(hoursM2) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hoursM3", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(hoursM3) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("hoursM4", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(hoursM4) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("streams", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(streams) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("groups", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(groups) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("students", NpgsqlDbType.Integer, (object?)ParseHelpers.IntOrNull(students) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("currentControl", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(currentControl) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("audLectures", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(audLectures) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("audSeminars", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(audSeminars) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("audNisPsSn", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(audNisPsSn) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("audTotal", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(audTotal) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("totalHours", NpgsqlDbType.Numeric, (object?)ParseHelpers.DecimalOrNull(totalHours) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    return Results.Redirect("/uiplan");
});

app.MapGet("/uiops", async (NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(@"
        SELECT op_id, name, education_level, study_format 
        FROM educational_programs 
        WHERE is_active = true 
        ORDER BY name", conn);

    var sb = new StringBuilder();
    sb.Append("<h1 class='h1'>Образовательные программы</h1>");
    sb.Append("<section class='card card--flush'>")
      .Append("<form method='post' action='/uifaculty/save-batch-form'>")
      .Append("<div class='toolbar'>")
      .Append("<button class='btn' type='submit'>Сохранить все</button>")
      .Append("</div>");
    sb.Append("<div class='table-wrap'><table class='table'>");
    sb.Append("<thead><tr><th>ID</th><th>Название</th><th>Уровень</th><th>Формат</th></tr></thead><tbody>");

    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        sb.Append("<tr>");
        sb.Append("<td>").Append(PlanRowReader.SafeReadInt32(r, 0)).Append("</td>");
        sb.Append("<td>").Append(ParseHelpers.H(r.GetString(1))).Append("</td>");
        sb.Append("<td>").Append(ParseHelpers.H(r.IsDBNull(2) ? "" : r.GetString(2))).Append("</td>");
        sb.Append("<td>").Append(ParseHelpers.H(r.IsDBNull(3) ? "" : r.GetString(3))).Append("</td>");
        sb.Append("</tr>");
    }
    sb.Append("</tbody></table></div></section>");

    return Results.Content(Layout("ОП", "ops", sb.ToString()), "text/html; charset=utf-8");
});

app.MapGet("/uifaculty", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    string? ppsNo,
    string? ppsName,
    string? position,
    string? rate,
    string? nrdShare,
    string? zeroOut,
    string? track,
    string? auditoryLoad,
    string? normHours,
    string? performedLoad,
    string? responsibleLoad,
    string? isActive,
    string[]? departmentId,
    string[]? moduleNo,
    string? sortBy,
    string? sortOrder,
    string? importError
) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    var moduleNos = ParseModuleNos(moduleNo);
    var departmentIdsFilter = ParseDepartmentIds(departmentId);
    var redirectQuery = ctx.Request.QueryString.Value?.TrimStart('?') ?? "";
    var departments = await LoadDepartments(ds);
    await using var conn = await ds.OpenConnectionAsync();
    await using var statsCmd = new NpgsqlCommand(@"
        SELECT
            COUNT(*) AS total,
            COUNT(DISTINCT department_id) AS departments,
            AVG(auditory_load) AS avg_auditory,
            SUM(CASE WHEN is_active THEN 1 ELSE 0 END) AS active_count
        FROM faculty_members;", conn);
    int totalTeachers = 0;
    int departmentsCount = 0;
    decimal avgAuditory = 0;
    int activeCount = 0;
    await using (var s = await statsCmd.ExecuteReaderAsync())
    {
        if (await s.ReadAsync())
        {
            totalTeachers = PlanRowReader.SafeReadInt32(s, 0);
            departmentsCount = PlanRowReader.SafeReadInt32(s, 1);
            avgAuditory = s.IsDBNull(2) ? 0 : s.GetDecimal(2);
            activeCount = PlanRowReader.SafeReadInt32(s, 3);
        }
    }

    var ppsDepartmentIdsParam = departmentIdsFilter.Length == 0 ? (object?)null : departmentIdsFilter;
    await using var cmd = new NpgsqlCommand(@"
        SELECT
            fm.faculty_id,
            fm.full_name,
            fm.position,
            fm.rate,
            fm.nrd_share,
            fm.zero_out_total,
            fm.track,
            fm.department_id,
            d.name as department_name,
            fm.employment_type,
            fm.auditory_load,
            fm.norm_hours,
            fm.performed_load,
            fm.responsible_load,
            fm.is_active
        FROM faculty_members fm 
        LEFT JOIN departments d ON fm.department_id = d.department_id 
        WHERE (@departmentIds IS NULL OR array_length(@departmentIds, 1) IS NULL OR fm.department_id = ANY(@departmentIds))
        ORDER BY " + GetFacultyOrderBy(sortBy, sortOrder) + @"
        LIMIT 500", conn);
    cmd.Parameters.AddWithValue("departmentIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)ppsDepartmentIdsParam ?? DBNull.Value);
    var positionOptions = new[]
    {
        "Профессор", "Доцент", "Старший преподаватель", "Преподаватель"
    };
    var trackOptions = new[]
    {
        "Академический", "Образовательно-методический", "Практико-ориентированный"
    };
    var employmentOptions = new[] { "штат", "ГПХ" };
    var departmentOptions = defaultDepartmentNames;

    var sb = new StringBuilder();
    sb.Append("<section class='page-header'>")
      .Append("<div>")
      .Append("<h1 class='page-title'>Преподаватели</h1>")
      .Append("<div class='page-subtitle'>Управление преподавателями и их данными</div>")
      .Append("</div>")
      .Append("</section>");
    if (!string.IsNullOrEmpty(importError))
      sb.Append("<section class='card'><p class='").Append(importError == "noFile" ? "hint" : "error").Append("'>")
        .Append(importError == "noFile" ? "Выберите файл Excel для загрузки." : ParseHelpers.H(importError))
        .Append("</p></section>");

    sb.Append("<section class='card'>")
      .Append("<div class='toolbar'>");
    if (user.CanEditFaculty)
    {
      sb.Append("<button class='btn' type='submit' form='pps-batch-form'>Сохранить все</button>")
        .Append("<button class='btn btn--danger' type='button' data-delete-selected='pps'>Удалить выбранные</button>")
        .Append("<button class='btn btn--ghost' type='button' data-pps-add>Добавить строку</button>");
    }
    else
      sb.Append("<span class=\"hint\">Только просмотр. Редактирование: менеджер департамента или администратор.</span>");
    sb.Append("<button class='btn btn--ghost' type='button' data-dialog-open='#pps-filter'>Фильтр</button>")
      .Append("<button class='btn btn--ghost' type='button' onclick=\"window.location.href='/uifaculty'\">Сброс</button>");
    var ppsSortBase = "/uifaculty?" + (string.IsNullOrEmpty(redirectQuery) ? "" : redirectQuery + "&");
    sb.Append("<span class=\"toolbar-sort\"><label class=\"hint\" style=\"margin-right:6px\">Сортировка:</label>")
      .Append("<select class=\"input input--sm\" style=\"width:auto;display:inline-block\" onchange=\"window.location=this.value\" aria-label=\"Сортировка\">")
      .Append("<option value=\"").Append(ppsSortBase).Append("sortBy=name&sortOrder=asc\"").Append((sortBy ?? "name") == "name" && sortOrder != "desc" ? " selected" : "").Append(">По ФИО А–Я</option>")
      .Append("<option value=\"").Append(ppsSortBase).Append("sortBy=name&sortOrder=desc\"").Append((sortBy ?? "") == "name" && sortOrder == "desc" ? " selected" : "").Append(">По ФИО Я–А</option>")
      .Append("<option value=\"").Append(ppsSortBase).Append("sortBy=department&sortOrder=asc\"").Append(sortBy == "department" ? " selected" : "").Append(">По департаменту</option>")
      .Append("<option value=\"").Append(ppsSortBase).Append("sortBy=position&sortOrder=asc\"").Append(sortBy == "position" ? " selected" : "").Append(">По должности</option>")
      .Append("<option value=\"").Append(ppsSortBase).Append("sortBy=newest&sortOrder=desc\"").Append(sortBy == "newest" ? " selected" : "").Append(">По новизне</option>")
      .Append("</select></span>")
      .Append("<a class='btn btn--excel' data-export=\"excel\" href='/uifaculty/export").Append(departmentIdsFilter.Length == 0 ? "" : "?" + string.Join("&", departmentIdsFilter.Select(n => "departmentId=" + n))).Append("' title='Выгрузить в Excel' aria-label='Выгрузить в Excel'><svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"currentColor\" d=\"M12 3v10.17l3.59-3.58L17 11l-5 5-5-5 1.41-1.41L11 13.17V3h1zm-7 14h14v2H5v-2z\"/></svg><span>Выгрузить в Excel</span></a>");
    if (user.CanEditFaculty)
      sb.Append("<span class=\"toolbar-import\"><form method='post' action='/uifaculty/import' enctype='multipart/form-data'>")
        .Append("<input type='hidden' name='redirectQuery' value='").Append(ParseHelpers.H(redirectQuery)).Append("'>")
        .Append("<label class='btn btn--ghost btn--file'><input type='file' name='file' accept='.xlsx,.xls' required>Выберите файл</label>")
        .Append("<button type='submit' class='btn btn--ghost'>Загрузить из Excel</button>")
        .Append("</form></span>");
    sb.Append("</div>")
      .Append("<div class='hint'>Показано до 500 строк</div>")
      .Append("</section>");

    sb.Append("<section class='stats-grid'>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon'>👥</div><span class='stat-badge stat-badge--green'>Активно</span></div>")
      .Append("<div class='stat-label'>Всего преподавателей</div>")
      .Append("<div class='stat-value'>").Append(totalTeachers).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--purple'>🏛️</div><span class='stat-badge stat-badge--blue'>Департаменты</span></div>")
      .Append("<div class='stat-label'>Департаментов</div>")
      .Append("<div class='stat-value'>").Append(departmentsCount).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--green'>📊</div><span class='stat-badge stat-badge--purple'>Среднее</span></div>")
      .Append("<div class='stat-label'>Ср. нагрузка</div>")
      .Append("<div class='stat-value'>").Append(avgAuditory.ToString("0")).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--orange'>✅</div><span class='stat-badge stat-badge--orange'>Активные</span></div>")
      .Append("<div class='stat-label'>Активных</div>")
      .Append("<div class='stat-value'>").Append(activeCount).Append("</div>")
      .Append("</div>")
      .Append("</section>");

    sb.Append("<dialog class='modal' id='pps-filter'>")
      .Append("<form method='get' action='/uifaculty' class='form form--row'>")
      .Append(RenderDepartmentCheckboxes(departments, departmentIdsFilter, null))
      .Append(RenderModuleCheckboxes(moduleNos))
      .Append("<input type='hidden' name='sortBy' value='").Append(ParseHelpers.H(sortBy ?? "name")).Append("'>")
      .Append("<input type='hidden' name='sortOrder' value='").Append(ParseHelpers.H(sortOrder ?? "asc")).Append("'>")
      .Append("<div class='modal__actions'>")
      .Append("<button class='btn' type='submit'>Применить</button>")
      .Append("<button class='btn btn--ghost' type='button' data-dialog-close>Закрыть</button>")
      .Append("</div></form></dialog>");

    sb.Append("<section class='card card--flush'>")
      .Append("<form id='pps-batch-form' method='post' action='/uifaculty/save-batch-form'>")
      .Append("<div class='toolbar'>")
      .Append("<button class='btn btn--ghost' type='button' data-filter-clear='#pps-table'>Сбросить фильтры</button>")
      .Append("</div>");
    sb.Append("<select id='pps-department-options' class='input' style='display:none'>")
      .Append(OptionsListStrings(departmentOptions.Concat(new[] { "Другое" }), null, "Департамент"))
      .Append("</select>");
    sb.Append("<div class='table-filter-bar' data-filter-table='#pps-table'>")
      .Append("<input class='input input--filter' data-filter-col='0' placeholder='Выбор' style='width:4rem'>")
      .Append("<input class='input input--filter' data-filter-col='1' placeholder='№'>")
      .Append("<input class='input input--filter' data-filter-col='2' placeholder='ФИО'>")
      .Append("<input class='input input--filter' data-filter-col='3' placeholder='Должность'>")
      .Append("<input class='input input--filter' data-filter-col='4' placeholder='Ставка'>")
      .Append("<input class='input input--filter' data-filter-col='5' placeholder='НРД (доля)'>")
      .Append("<input class='input input--filter' data-filter-col='6' placeholder='Трек'>")
      .Append("<input class='input input--filter' data-filter-col='7' placeholder='Департамент'>")
      .Append("<input class='input input--filter' data-filter-col='8' placeholder='Тип занятости'>")
      .Append("<input class='input input--filter' data-filter-col='9' placeholder='Аудит. нагрузка'>")
      .Append("<input class='input input--filter' data-filter-col='10' placeholder='Норма часов'>")
      .Append("<input class='input input--filter' data-filter-col='11' placeholder='Выполняемая'>")
      .Append("<input class='input input--filter' data-filter-col='12' placeholder='Ответственный'>")
      .Append("<input class='input input--filter' data-filter-col='13' placeholder='Активен'>")
      .Append("</div>");
    var canEditPps = user.CanEditFaculty;
    sb.Append("<div class='table-scroll-top'><div class='table-scroll-bar' role='scrollbar' aria-orientation='horizontal'><div class='table-scroll-bar__inner'></div></div><div class='table-wrap'><table id='pps-table' class='table' data-table-filter='pps'>");
    sb.Append("<thead><tr>")
      .Append("<th><input type='checkbox' data-select-all='pps' aria-label='Выбрать все'").Append(canEditPps ? ">" : " disabled>").Append("</th>")
      .Append("<th>№</th>")
      .Append("<th>ФИО</th>")
      .Append("<th>Должность</th>")
      .Append("<th>Ставка</th>")
      .Append("<th>НРД (доля)</th>")
      .Append("<th>Трек</th>")
      .Append("<th>Департамент</th>")
      .Append("<th>Тип занятости</th>")
      .Append("<th>Аудиторная нагрузка 2025/2026</th>")
      .Append("<th>Норма часов для трека и ставки</th>")
      .Append("<th>Выполняемая для аудиторной нагрузки</th>")
      .Append("<th>Аудиторная нагрузка (ответственный)</th>")
      .Append("<th>Активен</th>")
      .Append("</tr></thead><tbody>");

    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        var facultyId = PlanRowReader.SafeReadInt32(r, 0);
        var positionValue = r.IsDBNull(2) ? "" : r.GetString(2);
        var trackValue = r.IsDBNull(6) ? "" : r.GetString(6);
        var departmentName = r.IsDBNull(8) ? "" : r.GetString(8);
        var employmentValue = r.IsDBNull(9) ? "" : r.GetString(9);
        var departmentIsOther = !string.IsNullOrWhiteSpace(departmentName) && !departmentOptions.Contains(departmentName);
        var departmentSelected = departmentIsOther ? "Другое" : departmentName;
        sb.Append("<tr>");
        if (canEditPps)
            sb.Append("<td><input type='checkbox' class='row-select row-select-pps' name='selectFacultyId' value='").Append(facultyId).Append("'></td>");
        else
            sb.Append("<td><input type='checkbox' class='row-select row-select-pps' disabled></td>");
        sb.Append("<td>").Append(facultyId).Append("</td>");
        sb.Append("<td>").Append("<input type=\"hidden\" name=\"rowId\" value=\"").Append(facultyId).Append("\">");
        if (canEditPps)
            sb.Append("<input class=\"input\" name=\"ppsName_").Append(facultyId).Append("\" value=\"").Append(ParseHelpers.H(r.GetString(1))).Append("\">");
        else
            sb.Append("<input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(r.GetString(1))).Append("\" readonly>");
        sb.Append("</td>");
        if (canEditPps)
        {
            sb.Append("<td><select class=\"input\" name=\"position_").Append(facultyId).Append("\">").Append(OptionsListStrings(positionOptions, positionValue, "Должность")).Append("</select></td>");
            sb.Append("<td><input class=\"input\" name=\"rate_").Append(facultyId).Append("\" value=\"").Append(r.IsDBNull(3) ? "" : r.GetDecimal(3).ToString(CultureInfo.InvariantCulture)).Append("\"></td>");
            sb.Append("<td><input class=\"input\" name=\"nrdShare_").Append(facultyId).Append("\" value=\"").Append(r.IsDBNull(4) ? "" : r.GetDecimal(4).ToString(CultureInfo.InvariantCulture)).Append("\"></td>");
            sb.Append("<td><select class=\"input\" name=\"track_").Append(facultyId).Append("\">").Append(OptionsListStrings(trackOptions, trackValue, "Трек")).Append("</select></td>");
            sb.Append("<td>")
              .Append("<select class=\"input\" name=\"departmentName_").Append(facultyId).Append("\" data-dept-select>")
              .Append(OptionsListStrings(departmentOptions.Concat(new[] { "Другое" }), departmentSelected, "Департамент"))
              .Append("</select>")
              .Append("<input class=\"input input--dept-custom\" name=\"departmentCustom_").Append(facultyId).Append("\" value=\"").Append(ParseHelpers.H(departmentIsOther ? departmentName : "")).Append("\" placeholder=\"Департамент (с большой буквы)\" ").Append(departmentIsOther ? "" : "style=\"display:none\"").Append(">")
              .Append("</td>");
            sb.Append("<td><select class=\"input\" name=\"employmentType_").Append(facultyId).Append("\">").Append(OptionsListStrings(employmentOptions, string.IsNullOrWhiteSpace(employmentValue) ? null : employmentValue.Trim(), "Тип")).Append("</select></td>");
            sb.Append("<td><input class=\"input\" name=\"auditoryLoad_").Append(facultyId).Append("\" value=\"").Append(r.IsDBNull(10) ? "" : r.GetDecimal(10).ToString(CultureInfo.InvariantCulture)).Append("\"></td>");
            sb.Append("<td><input class=\"input\" name=\"normHours_").Append(facultyId).Append("\" value=\"").Append(r.IsDBNull(11) ? "" : r.GetDecimal(11).ToString(CultureInfo.InvariantCulture)).Append("\"></td>");
            sb.Append("<td><input class=\"input\" name=\"performedLoad_").Append(facultyId).Append("\" value=\"").Append(r.IsDBNull(12) ? "" : r.GetDecimal(12).ToString(CultureInfo.InvariantCulture)).Append("\"></td>");
            sb.Append("<td><input class=\"input\" name=\"responsibleLoad_").Append(facultyId).Append("\" value=\"").Append(r.IsDBNull(13) ? "" : r.GetDecimal(13).ToString(CultureInfo.InvariantCulture)).Append("\"></td>");
            sb.Append("<td><div class=\"cell-actions\"><label class=\"check\"><input type=\"checkbox\" name=\"isActive_").Append(facultyId).Append("\" ").Append(r.GetBoolean(14) ? "checked" : "").Append(">Активен</label></div></td>");
        }
        else
        {
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(positionValue)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(r.IsDBNull(3) ? "" : r.GetDecimal(3).ToString(CultureInfo.InvariantCulture)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(r.IsDBNull(4) ? "" : r.GetDecimal(4).ToString(CultureInfo.InvariantCulture)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(trackValue)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(departmentName)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(ParseHelpers.H(employmentValue)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(r.IsDBNull(10) ? "" : r.GetDecimal(10).ToString(CultureInfo.InvariantCulture)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(r.IsDBNull(11) ? "" : r.GetDecimal(11).ToString(CultureInfo.InvariantCulture)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(r.IsDBNull(12) ? "" : r.GetDecimal(12).ToString(CultureInfo.InvariantCulture)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(r.IsDBNull(13) ? "" : r.GetDecimal(13).ToString(CultureInfo.InvariantCulture)).Append("\" readonly></td>");
            sb.Append("<td><input class=\"input input--readonly\" value=\"").Append(r.GetBoolean(14) ? "Да" : "Нет").Append("\" readonly></td>");
        }
        sb.Append("</tr>");
    }
    sb.Append("</tbody></table></div></div></form></section>");

    return Results.Content(Layout("Преподаватели", "faculty", sb.ToString(), user.Login, user.IsAdmin), "text/html; charset=utf-8");
});

app.MapGet("/uifaculty/export", async (HttpContext ctx, NpgsqlDataSource ds, string[]? departmentId) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    var departmentIds = ParseDepartmentIds(departmentId);
    if (!user.IsAdmin && user.AllowedDepartmentIds.Length > 0)
    {
        var allowedDept = new HashSet<int>(user.AllowedDepartmentIds);
        departmentIds = departmentIds.Length == 0 ? user.AllowedDepartmentIds : departmentIds.Where(d => allowedDept.Contains(d)).ToArray();
    }
    if (!user.IsAdmin && user.AllowedDepartmentIds.Length == 0)
        departmentIds = new[] { -1 };
    var ppsExportDepartmentIdsParam = departmentIds.Length == 0 ? (object?)null : departmentIds;
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(@"
        SELECT
            fm.faculty_id,
            fm.full_name,
            fm.position,
            fm.rate,
            fm.nrd_share,
            fm.zero_out_total,
            fm.track,
            d.name as department_name,
            fm.auditory_load,
            fm.norm_hours,
            fm.performed_load,
            fm.responsible_load,
            fm.is_active
        FROM faculty_members fm
        LEFT JOIN departments d ON fm.department_id = d.department_id
        WHERE (@departmentIds IS NULL OR array_length(@departmentIds, 1) IS NULL OR fm.department_id = ANY(@departmentIds))
        ORDER BY fm.full_name;
", conn);
    cmd.Parameters.AddWithValue("departmentIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)ppsExportDepartmentIdsParam ?? DBNull.Value);
    using var wb = new XLWorkbook();
    var ws = wb.AddWorksheet("ППС");
    var headers = new[] { "№", "ФИО", "Должность", "Ставка", "НРД (доля)", "Обнуление", "Трек", "Департамент", "Аудиторная нагрузка", "Норма часов", "Выполняемая", "Ответственный", "Активен" };
    for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
    var row = 2;
    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            ws.Cell(row, 1).Value = r.IsDBNull(0) ? "" : PlanRowReader.SafeReadInt32(r, 0);
            ws.Cell(row, 2).Value = r.IsDBNull(1) ? "" : r.GetString(1);
            ws.Cell(row, 3).Value = r.IsDBNull(2) ? "" : r.GetString(2);
            ws.Cell(row, 4).Value = r.IsDBNull(3) ? "" : r.GetDecimal(3);
            ws.Cell(row, 5).Value = r.IsDBNull(4) ? "" : r.GetDecimal(4);
            ws.Cell(row, 6).Value = r.IsDBNull(5) ? "" : r.GetValue(5)?.ToString() ?? "";
            ws.Cell(row, 7).Value = r.IsDBNull(6) ? "" : r.GetString(6);
            ws.Cell(row, 8).Value = r.IsDBNull(7) ? "" : r.GetString(7);
            ws.Cell(row, 9).Value = r.IsDBNull(8) ? "" : r.GetDecimal(8);
            ws.Cell(row, 10).Value = r.IsDBNull(9) ? "" : r.GetDecimal(9);
            ws.Cell(row, 11).Value = r.IsDBNull(10) ? "" : r.GetDecimal(10);
            ws.Cell(row, 12).Value = r.IsDBNull(11) ? "" : r.GetDecimal(11);
            ws.Cell(row, 13).Value = r.IsDBNull(12) ? "" : (r.GetBoolean(12) ? "Да" : "Нет");
            row++;
        }
    }
    byte[] bytes;
    using (var stream = new MemoryStream())
    {
        wb.SaveAs(stream, false);
        bytes = stream.ToArray();
    }
    var fileName = $"pps_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

app.MapPost("/uifaculty/import", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditFaculty) return Results.Forbid();
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        var q = form["redirectQuery"].ToString();
        return Results.Redirect("/uifaculty" + (string.IsNullOrEmpty(q) ? "?importError=noFile" : q + (q.Contains("?") ? "&" : "?") + "importError=noFile"));
    }
    var redirectQuery = form["redirectQuery"].ToString();
    var ppsColumnMap = new Dictionary<string, string[]>
    {
        ["no"] = new[] { "№", "№ пп", "Номер" },
        ["full_name"] = new[] { "ФИО", "ФИО преподавателя", "Преподаватель", "ФИО ППС" },
        ["position"] = new[] { "Должность" },
        ["rate"] = new[] { "Ставка" },
        ["nrd_share"] = new[] { "НРД (доля)", "НРД", "нрд" },
        ["zero_out_total"] = new[] { "Обнуление" },
        ["track"] = new[] { "Трек" },
        ["department"] = new[] { "Департамент", "Реализующий департамент" },
        ["auditory_load"] = new[] { "Аудиторная нагрузка", "Аудиторная нагрузка (ч)" },
        ["norm_hours"] = new[] { "Норма часов" },
        ["performed_load"] = new[] { "Выполняемая", "Выполняемая нагрузка" },
        ["responsible_load"] = new[] { "Ответственный", "Ответственный нагрузка" },
        ["is_active"] = new[] { "Активен" }
    };
    List<Dictionary<string, string>> rows;
    try
    {
        await using var stream = file.OpenReadStream();
        rows = ExcelImportHelper.ParseSheet(stream, ppsColumnMap);
    }
    catch (Exception ex)
    {
        return Results.Redirect("/uifaculty" + (string.IsNullOrEmpty(redirectQuery) ? "?" : redirectQuery + "&") + "importError=" + Uri.EscapeDataString(ex.Message));
    }
    var positionOptions = new HashSet<string>(new[] { "Профессор", "Доцент", "Старший преподаватель", "Преподаватель" });
    var trackOptions = new HashSet<string>(new[] { "Академический", "Образовательно-методический", "Практико-ориентированный" });
    var departmentOptions = new HashSet<string>(defaultDepartmentNames);
    var errors = new List<string>();
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    async Task<int?> ResolveDepartmentId(string name)
    {
        await using var sel = new NpgsqlCommand("SELECT department_id FROM departments WHERE name = @name", conn, tx);
        sel.Parameters.AddWithValue("name", NpgsqlDbType.Text, name);
        var existing = await sel.ExecuteScalarAsync();
        if (existing is int id) return id;
        await using var ins = new NpgsqlCommand("INSERT INTO departments (name) VALUES (@name) RETURNING department_id", conn, tx);
        ins.Parameters.AddWithValue("name", NpgsqlDbType.Text, name);
        var newId = await ins.ExecuteScalarAsync();
        return newId is int created ? created : null;
    }
    var rowNum = 2;
    foreach (var row in rows)
    {
        var fullName = ExcelImportHelper.Get(row, "full_name");
        if (string.IsNullOrWhiteSpace(fullName)) { rowNum++; continue; }
        var noStr = ExcelImportHelper.Get(row, "no");
        var facultyId = ParseHelpers.IntOrNull(noStr);
        var position = ExcelImportHelper.Get(row, "position");
        var rate = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "rate"));
        var nrdShare = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "nrd_share"));
        var track = ExcelImportHelper.Get(row, "track");
        var departmentName = ExcelImportHelper.Get(row, "department");
        var auditoryLoad = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "auditory_load"));
        var normHours = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "norm_hours"));
        var performedLoad = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "performed_load"));
        var responsibleLoad = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "responsible_load"));
        var isActiveStr = ExcelImportHelper.Get(row, "is_active");
        var isActive = !string.IsNullOrWhiteSpace(isActiveStr) && (isActiveStr.Equals("Да", StringComparison.OrdinalIgnoreCase) || isActiveStr.Equals("1") || isActiveStr.Equals("да", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(position)) position = "Преподаватель";
        if (!positionOptions.Contains(position)) position = "Преподаватель";
        if (rate is null) rate = 0m;
        if (nrdShare is null) nrdShare = 0m;
        if (string.IsNullOrWhiteSpace(track)) track = "Академический";
        if (!trackOptions.Contains(track)) track = "Академический";
        int? departmentId = null;
        if (!string.IsNullOrWhiteSpace(departmentName))
            departmentId = await ResolveDepartmentId(departmentName);
        if (facultyId is null)
        {
            await using var insert = new NpgsqlCommand(@"
INSERT INTO faculty_members (full_name, department_id, is_active, position, rate, nrd_share, zero_out_total, track, auditory_load, norm_hours, performed_load, responsible_load)
VALUES (@fullName, @departmentId, @isActive, @position, @rate, @nrd, false, @track, @auditoryLoad, @normHours, @performedLoad, @responsibleLoad);", conn, tx);
            insert.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, fullName);
            insert.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
            insert.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, isActive);
            insert.Parameters.AddWithValue("position", NpgsqlDbType.Text, position);
            insert.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, rate.Value);
            insert.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, nrdShare.Value);
            insert.Parameters.AddWithValue("track", NpgsqlDbType.Text, track);
            insert.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
            insert.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
            insert.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
            insert.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        else
        {
            await using var update = new NpgsqlCommand(@"
UPDATE faculty_members SET full_name = @fullName, position = @position, rate = @rate, nrd_share = @nrd, track = @track,
  department_id = COALESCE(@departmentId, department_id), is_active = @isActive,
  auditory_load = COALESCE(@auditoryLoad, auditory_load), norm_hours = COALESCE(@normHours, norm_hours),
  performed_load = COALESCE(@performedLoad, performed_load), responsible_load = COALESCE(@responsibleLoad, responsible_load)
WHERE faculty_id = @facultyId;", conn, tx);
            update.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, fullName);
            update.Parameters.AddWithValue("position", NpgsqlDbType.Text, position);
            update.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, rate!.Value);
            update.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, nrdShare!.Value);
            update.Parameters.AddWithValue("track", NpgsqlDbType.Text, track);
            update.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
            update.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, isActive);
            update.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
            update.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
            update.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
            update.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
            update.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, facultyId.Value);
            await update.ExecuteNonQueryAsync();
        }
        rowNum++;
    }
    await tx.CommitAsync();
    return Results.Redirect("/uifaculty" + (string.IsNullOrEmpty(redirectQuery) ? "" : redirectQuery));
}).DisableAntiforgery();

app.MapPost("/uifaculty/save", async (
    HttpContext ctx,
    NpgsqlDataSource ds,
    int? ppsNo,
    string? ppsName,
    string? position,
    decimal? rate,
    decimal? nrdShare,
    string? zeroOut,
    string? track,
    int? departmentId,
    decimal? auditoryLoad,
    decimal? normHours,
    decimal? performedLoad,
    decimal? responsibleLoad,
    string? isActive
) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditFaculty) return Results.Forbid();
    var facultyIdValue = ppsNo;
    if (string.IsNullOrWhiteSpace(ppsName))
        return Results.Redirect("/uifaculty");

    await using var conn = await ds.OpenConnectionAsync();
    var isActiveValue = ParseHelpers.BoolOrNull(isActive);

    if (facultyIdValue is null)
    {
        await using var insert = new NpgsqlCommand(@"
INSERT INTO faculty_members (
    full_name, department_id, is_active, position, rate, nrd_share, zero_out_total,
    track, auditory_load, norm_hours, performed_load, responsible_load
)
VALUES (
    @fullName, @departmentId, COALESCE(@isActive, true), @position, @rate, @nrd, @zeroOut,
    @track, @auditoryLoad, @normHours, @performedLoad, @responsibleLoad
);
", conn);
        insert.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, ppsName);
        insert.Parameters.AddWithValue("position", NpgsqlDbType.Text, (object?)position ?? DBNull.Value);
        insert.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, (object?)rate ?? DBNull.Value);
        insert.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, (object?)nrdShare ?? DBNull.Value);
        insert.Parameters.AddWithValue("zeroOut", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(zeroOut) ?? DBNull.Value);
        insert.Parameters.AddWithValue("track", NpgsqlDbType.Text, (object?)track ?? DBNull.Value);
        insert.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
        insert.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, (object?)isActiveValue ?? DBNull.Value);
        insert.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
        insert.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
        insert.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
        insert.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }
    else
    {
        await using var cmd = new NpgsqlCommand(@"
UPDATE faculty_members
SET full_name = COALESCE(@fullName, full_name),
    position = COALESCE(@position, position),
    rate = COALESCE(@rate, rate),
    nrd_share = COALESCE(@nrd, nrd_share),
    zero_out_total = COALESCE(@zeroOut, zero_out_total),
    track = COALESCE(@track, track),
    department_id = COALESCE(@departmentId, department_id),
    is_active = COALESCE(@isActive, is_active),
    auditory_load = COALESCE(@auditoryLoad, auditory_load),
    norm_hours = COALESCE(@normHours, norm_hours),
    performed_load = COALESCE(@performedLoad, performed_load),
    responsible_load = COALESCE(@responsibleLoad, responsible_load)
WHERE faculty_id = @facultyId;
", conn);
        cmd.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, (object?)ppsName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("position", NpgsqlDbType.Text, (object?)position ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, (object?)rate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, (object?)nrdShare ?? DBNull.Value);
        cmd.Parameters.AddWithValue("zeroOut", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(zeroOut) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("track", NpgsqlDbType.Text, (object?)track ?? DBNull.Value);
        cmd.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, (object?)isActiveValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
        cmd.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
        cmd.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
        cmd.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
        cmd.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, facultyIdValue.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    return Results.Redirect("/uifaculty");
});

app.MapPost("/uifaculty/save-raw", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditFaculty) return Results.Forbid();
    var form = await request.ReadFormAsync();
    int? ppsNo = ParseHelpers.IntOrNull(form["ppsNo"]);
    var ppsName = form["ppsName"].ToString();
    var position = form["position"].ToString();
    var rate = ParseHelpers.DecimalOrNull(form["rate"]);
    var nrdShare = ParseHelpers.DecimalOrNull(form["nrdShare"]);
    var zeroOut = form["zeroOut"].ToString();
    var track = form["track"].ToString();
    int? departmentId = ParseHelpers.IntOrNull(form["departmentId"]);
    var auditoryLoad = ParseHelpers.DecimalOrNull(form["auditoryLoad"]);
    var normHours = ParseHelpers.DecimalOrNull(form["normHours"]);
    var performedLoad = ParseHelpers.DecimalOrNull(form["performedLoad"]);
    var responsibleLoad = ParseHelpers.DecimalOrNull(form["responsibleLoad"]);
    var isActive = form["isActive"].ToString();

    if (string.IsNullOrWhiteSpace(ppsName))
        return Results.BadRequest("ppsName required");

    var isActiveValue = ParseHelpers.BoolOrNull(isActive);

    await using var conn = await ds.OpenConnectionAsync();

    if (ppsNo is null)
    {
        await using var insert = new NpgsqlCommand(@"
INSERT INTO faculty_members (
    full_name, department_id, is_active, position, rate, nrd_share, zero_out_total,
    track, auditory_load, norm_hours, performed_load, responsible_load
)
VALUES (
    @fullName, @departmentId, COALESCE(@isActive, true), @position, @rate, @nrd, @zeroOut,
    @track, @auditoryLoad, @normHours, @performedLoad, @responsibleLoad
);
", conn);
        insert.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, ppsName);
        insert.Parameters.AddWithValue("position", NpgsqlDbType.Text, (object?)position ?? DBNull.Value);
        insert.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, (object?)rate ?? DBNull.Value);
        insert.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, (object?)nrdShare ?? DBNull.Value);
        insert.Parameters.AddWithValue("zeroOut", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(zeroOut) ?? DBNull.Value);
        insert.Parameters.AddWithValue("track", NpgsqlDbType.Text, (object?)track ?? DBNull.Value);
        insert.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
        insert.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, (object?)isActiveValue ?? DBNull.Value);
        insert.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
        insert.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
        insert.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
        insert.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }
    else
    {
        await using var cmd = new NpgsqlCommand(@"
UPDATE faculty_members
SET full_name = COALESCE(@fullName, full_name),
    position = COALESCE(@position, position),
    rate = COALESCE(@rate, rate),
    nrd_share = COALESCE(@nrd, nrd_share),
    zero_out_total = COALESCE(@zeroOut, zero_out_total),
    track = COALESCE(@track, track),
    department_id = COALESCE(@departmentId, department_id),
    is_active = COALESCE(@isActive, is_active),
    auditory_load = COALESCE(@auditoryLoad, auditory_load),
    norm_hours = COALESCE(@normHours, norm_hours),
    performed_load = COALESCE(@performedLoad, performed_load),
    responsible_load = COALESCE(@responsibleLoad, responsible_load)
WHERE faculty_id = @facultyId;
", conn);
        cmd.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, (object?)ppsName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("position", NpgsqlDbType.Text, (object?)position ?? DBNull.Value);
        cmd.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, (object?)rate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, (object?)nrdShare ?? DBNull.Value);
        cmd.Parameters.AddWithValue("zeroOut", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(zeroOut) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("track", NpgsqlDbType.Text, (object?)track ?? DBNull.Value);
        cmd.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, (object?)isActiveValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
        cmd.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
        cmd.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
        cmd.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
        cmd.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, ppsNo.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    return Results.Ok();
}).DisableAntiforgery();

app.MapPost("/uifaculty/save-batch-form", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditFaculty) return Results.Forbid();
    var form = await request.ReadFormAsync();
    var rowKeys = form["rowId"];
    if (rowKeys.Count == 0) return Results.Redirect("/uifaculty");

    var positionOptions = new HashSet<string>(new[]
    {
        "Профессор", "Доцент", "Старший преподаватель", "Преподаватель"
    });
    var trackOptions = new HashSet<string>(new[]
    {
        "Академический", "Образовательно-методический", "Практико-ориентированный"
    });
    var departmentOptions = new HashSet<string>(defaultDepartmentNames);

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    var errors = new List<string>();

    foreach (var rowKey in rowKeys)
    {
        if (string.IsNullOrWhiteSpace(rowKey)) continue;
        var facultyId = ParseHelpers.IntOrNull(rowKey);

        string? GetText(string key)
        {
            var value = form[$"{key}_{rowKey}"].ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        var ppsName = GetText("ppsName");
        if (string.IsNullOrWhiteSpace(ppsName)) continue;

        var position = GetText("position");
        var rate = ParseHelpers.DecimalOrNull(GetText("rate"));
        var nrdShare = ParseHelpers.DecimalOrNull(GetText("nrdShare"));
        var track = GetText("track");
        var departmentName = GetText("departmentName");
        var departmentCustom = GetText("departmentCustom");
        var employmentType = GetText("employmentType");
        var auditoryLoad = ParseHelpers.DecimalOrNull(GetText("auditoryLoad"));
        var normHours = ParseHelpers.DecimalOrNull(GetText("normHours"));
        var performedLoad = ParseHelpers.DecimalOrNull(GetText("performedLoad"));
        var responsibleLoad = ParseHelpers.DecimalOrNull(GetText("responsibleLoad"));
        var isActiveValue = form.ContainsKey($"isActive_{rowKey}");

        bool IsFullNameValid(string name)
        {
            if (name.Contains('.')) return false;
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            foreach (var part in parts)
            {
                var clean = part.Replace("-", "");
                if (clean.Length < 2) return false;
            }
            return true;
        }

        if (!IsFullNameValid(ppsName))
            errors.Add($"ФИО должно быть полностью (строка {rowKey})");

        if (string.IsNullOrWhiteSpace(position) || !positionOptions.Contains(position))
            errors.Add($"Должность обязательна (строка {rowKey})");

        if (rate is null)
            errors.Add($"Ставка должна быть числом (строка {rowKey})");

        if (nrdShare is null)
            errors.Add($"НРД должна быть числом (строка {rowKey})");
        else if (rate is not null && nrdShare > rate)
            errors.Add($"НРД не может быть больше ставки (строка {rowKey})");

        if (string.IsNullOrWhiteSpace(track) || !trackOptions.Contains(track))
            errors.Add($"Трек обязателен (строка {rowKey})");

        string? finalDepartmentName = null;
        if (string.IsNullOrWhiteSpace(departmentName))
        {
            errors.Add($"Департамент обязателен (строка {rowKey})");
        }
        else if (departmentName == "Другое")
        {
            if (string.IsNullOrWhiteSpace(departmentCustom))
                errors.Add($"Укажите департамент (строка {rowKey})");
            else if (!char.IsUpper(departmentCustom[0]))
                errors.Add($"Департамент должен начинаться с большой буквы (строка {rowKey})");
            else
                finalDepartmentName = departmentCustom;
        }
        else if (departmentOptions.Contains(departmentName))
        {
            finalDepartmentName = departmentName;
        }
        else
        {
            errors.Add($"Недопустимый департамент (строка {rowKey})");
        }

        if (errors.Count > 0) continue;

        async Task<int?> ResolveDepartmentId(string name)
        {
            await using var sel = new NpgsqlCommand(
                "SELECT department_id FROM departments WHERE name = @name",
                conn, tx);
            sel.Parameters.AddWithValue("name", NpgsqlDbType.Text, name);
            var existing = await sel.ExecuteScalarAsync();
            if (existing is int id) return id;

            await using var ins = new NpgsqlCommand(
                "INSERT INTO departments (name) VALUES (@name) RETURNING department_id",
                conn, tx);
            ins.Parameters.AddWithValue("name", NpgsqlDbType.Text, name);
            var newId = await ins.ExecuteScalarAsync();
            return newId is int created ? created : null;
        }

        var departmentId = finalDepartmentName is null ? (int?)null : await ResolveDepartmentId(finalDepartmentName);

        if (facultyId is null)
        {
            await using var insert = new NpgsqlCommand(@"
INSERT INTO faculty_members (
    full_name, department_id, is_active, position, rate, nrd_share, zero_out_total,
    track, employment_type, auditory_load, norm_hours, performed_load, responsible_load
)
VALUES (
    @fullName, @departmentId, @isActive, @position, @rate, @nrd, @zeroOut,
    @track, @employmentType, @auditoryLoad, @normHours, @performedLoad, @responsibleLoad
);
", conn, tx);
            insert.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, ppsName);
            insert.Parameters.AddWithValue("position", NpgsqlDbType.Text, (object?)position ?? DBNull.Value);
            insert.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, (object?)rate ?? DBNull.Value);
            insert.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, (object?)nrdShare ?? DBNull.Value);
            insert.Parameters.AddWithValue("zeroOut", NpgsqlDbType.Boolean, DBNull.Value);
            insert.Parameters.AddWithValue("track", NpgsqlDbType.Text, (object?)track ?? DBNull.Value);
            insert.Parameters.AddWithValue("employmentType", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(employmentType) ? DBNull.Value : employmentType.Trim());
            insert.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
            insert.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, isActiveValue);
            insert.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
            insert.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
            insert.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
            insert.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        else
        {
            await using var cmd = new NpgsqlCommand(@"
UPDATE faculty_members
SET full_name = COALESCE(@fullName, full_name),
    position = COALESCE(@position, position),
    rate = COALESCE(@rate, rate),
    nrd_share = COALESCE(@nrd, nrd_share),
    track = COALESCE(@track, track),
    department_id = COALESCE(@departmentId, department_id),
    employment_type = @employmentType,
    is_active = @isActive,
    auditory_load = COALESCE(@auditoryLoad, auditory_load),
    norm_hours = COALESCE(@normHours, norm_hours),
    performed_load = COALESCE(@performedLoad, performed_load),
    responsible_load = COALESCE(@responsibleLoad, responsible_load)
WHERE faculty_id = @facultyId;
", conn, tx);
            cmd.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, (object?)ppsName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("position", NpgsqlDbType.Text, (object?)position ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, (object?)rate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, (object?)nrdShare ?? DBNull.Value);
            cmd.Parameters.AddWithValue("track", NpgsqlDbType.Text, (object?)track ?? DBNull.Value);
            cmd.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("employmentType", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(employmentType) ? DBNull.Value : employmentType.Trim());
            cmd.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, isActiveValue);
            cmd.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
            cmd.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
            cmd.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
            cmd.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
            cmd.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, facultyId.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    if (errors.Count > 0)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(string.Join("\n", errors));
    }

    await tx.CommitAsync();
    return Results.Redirect("/uifaculty");
}).DisableAntiforgery();

app.MapPost("/uifaculty/delete", async (HttpContext ctx, NpgsqlDataSource ds, int? facultyId) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditFaculty) return Results.Forbid();
    if (facultyId is null) return Results.Redirect("/uifaculty");
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(
        "DELETE FROM faculty_members WHERE faculty_id = @id",
        conn);
    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, facultyId.Value);
    await cmd.ExecuteNonQueryAsync();
    return Results.Redirect("/uifaculty");
});

app.MapPost("/uifaculty/delete-batch", async (HttpContext ctx, NpgsqlDataSource ds, int[]? facultyIds) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditFaculty) return Results.Forbid();
    if (facultyIds is null || facultyIds.Length == 0) return Results.Redirect("/uifaculty");
    await using var conn = await ds.OpenConnectionAsync();
    foreach (var id in facultyIds)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM faculty_members WHERE faculty_id = @id", conn);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        await cmd.ExecuteNonQueryAsync();
    }
    return Results.Redirect("/uifaculty");
});

app.MapGet("/uidisciplines", async (HttpContext ctx, NpgsqlDataSource ds, string? q, string[]? departmentId, string[]? moduleNo, string? sortBy, string? sortOrder, string? importError) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    var moduleNos = ParseModuleNos(moduleNo);
    var departmentIds = ParseDepartmentIds(departmentId);
    var redirectQuery = ctx.Request.QueryString.Value?.TrimStart('?') ?? "";
    await using var conn = await ds.OpenConnectionAsync();
    var departments = await LoadDepartments(ds);
    var modules = await LoadModules(ds);

    int planIdDefault = 0;
    await using (var planCmd = new NpgsqlCommand(
        "SELECT plan_id FROM study_plans ORDER BY plan_id LIMIT 1",
        conn))
    {
        var planObj = await planCmd.ExecuteScalarAsync();
        planIdDefault = planObj is int id ? id : 0;
    }

    var qLike = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%";
    int discTotal = 0;
    int discModules = 0;
    int discDepts = 0;
    decimal discCredits = 0;
    await using (var statsCmd = new NpgsqlCommand(@"
SELECT
  COUNT(*) AS total,
  COUNT(DISTINCT pd.module_id) AS modules,
  COUNT(DISTINCT pd.implementing_department_id) AS depts,
  COALESCE(SUM(pd.credits), 0) AS total_credits
FROM plan_disciplines pd
WHERE (@q IS NULL OR pd.discipline_name ILIKE @q OR pd.discipline_no ILIKE @q);", conn))
    {
        statsCmd.Parameters.AddWithValue("q", NpgsqlDbType.Text, (object?)qLike ?? DBNull.Value);
        await using var sr = await statsCmd.ExecuteReaderAsync();
        if (await sr.ReadAsync())
        {
            discTotal = PlanRowReader.SafeReadInt32(sr, 0);
            discModules = PlanRowReader.SafeReadInt32(sr, 1);
            discDepts = PlanRowReader.SafeReadInt32(sr, 2);
            discCredits = sr.IsDBNull(3) ? 0 : sr.GetDecimal(3);
        }
    }

    await using var cmd = new NpgsqlCommand(@"
SELECT
  pd.plan_discipline_id,
  pd.discipline_no,
  pd.discipline_name,
  pd.module_id,
  pd.implementing_department_id,
  pd.course_no,
  pd.discipline_kind::text,
  pd.language,
  pd.credits
FROM plan_disciplines pd
LEFT JOIN plan_modules pm ON pd.module_id = pm.module_id
LEFT JOIN departments d ON pd.implementing_department_id = d.department_id
WHERE (@q IS NULL OR pd.discipline_name ILIKE @q OR pd.discipline_no ILIKE @q) AND
  (@departmentIds IS NULL OR array_length(@departmentIds, 1) IS NULL OR pd.implementing_department_id = ANY(@departmentIds)) AND
  (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR pm.module_number = ANY(@moduleNos))
ORDER BY " + GetDisciplinesOrderBy(sortBy, sortOrder) + @"
LIMIT 500;", conn);
    var discDepartmentIdsParam = departmentIds.Length == 0 ? (object?)null : departmentIds;
    var discModuleNosParam = moduleNos.Length == 0 ? (object?)null : moduleNos;
    cmd.Parameters.AddWithValue("q", NpgsqlDbType.Text, (object?)qLike ?? DBNull.Value);
    cmd.Parameters.AddWithValue("departmentIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)discDepartmentIdsParam ?? DBNull.Value);
    cmd.Parameters.AddWithValue("moduleNos", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)discModuleNosParam ?? DBNull.Value);

    var sb = new StringBuilder();
    sb.Append("<section class='page-header'>")
      .Append("<div>")
      .Append("<h1 class='page-title'>Дисциплины</h1>")
      .Append("<div class='page-subtitle'>Список формируется из учебного плана: дисциплины, добавленные в разделе <a href='/uiplan' class='link'>Учебный план</a>, автоматически отображаются здесь</div>")
      .Append("</div>")
      .Append("</section>");

    var discExportParams = new List<string>();
    if (!string.IsNullOrWhiteSpace(q)) discExportParams.Add("q=" + Uri.EscapeDataString(q ?? ""));
    foreach (var n in departmentIds) discExportParams.Add("departmentId=" + n);
    foreach (var n in moduleNos) discExportParams.Add("moduleNo=" + n);
    var discExportQuery = discExportParams.Count == 0 ? "" : "?" + string.Join("&", discExportParams);
    if (!string.IsNullOrEmpty(importError))
      sb.Append("<section class='card'><p class='").Append(importError == "noFile" ? "hint" : "error").Append("'>")
        .Append(importError == "noFile" ? "Выберите файл Excel для загрузки." : ParseHelpers.H(importError))
        .Append("</p></section>");
    sb.Append("<section class='card'>")
      .Append("<div class='toolbar'>");
    if (user.CanEditDisciplines)
    {
      sb.Append("<button class='btn' type='submit' form='disc-batch-form'>Сохранить все</button>")
        .Append("<button class='btn btn--danger' type='button' data-delete-selected='disc'>Удалить выбранные</button>")
        .Append("<button class='btn btn--ghost' type='button' data-disc-add>Добавить строку</button>");
    }
    else
      sb.Append("<span class=\"hint\">Только просмотр. Редактирование: менеджер департамента или администратор.</span>");
    sb.Append("<button class='btn btn--ghost' type='button' data-dialog-open='#disc-filter'>Фильтр</button>")
      .Append("<button class='btn btn--ghost' type='button' onclick=\"window.location.href='/uidisciplines'\">Сброс</button>");
    var discSortBase = "/uidisciplines?" + (string.IsNullOrEmpty(redirectQuery) ? "" : redirectQuery + "&");
    sb.Append("<span class=\"toolbar-sort\"><label class=\"hint\" style=\"margin-right:6px\">Сортировка:</label>")
      .Append("<select class=\"input input--sm\" style=\"width:auto;display:inline-block\" onchange=\"window.location=this.value\" aria-label=\"Сортировка\">")
      .Append("<option value=\"").Append(discSortBase).Append("sortBy=name&sortOrder=asc\"").Append((sortBy ?? "name") == "name" && sortOrder != "desc" ? " selected" : "").Append(">По названию А–Я</option>")
      .Append("<option value=\"").Append(discSortBase).Append("sortBy=name&sortOrder=desc\"").Append((sortBy ?? "") == "name" && sortOrder == "desc" ? " selected" : "").Append(">По названию Я–А</option>")
      .Append("<option value=\"").Append(discSortBase).Append("sortBy=no&sortOrder=asc\"").Append(sortBy == "no" ? " selected" : "").Append(">По № дисциплины</option>")
      .Append("<option value=\"").Append(discSortBase).Append("sortBy=course&sortOrder=asc\"").Append(sortBy == "course" ? " selected" : "").Append(">По курсу</option>")
      .Append("<option value=\"").Append(discSortBase).Append("sortBy=department&sortOrder=asc\"").Append(sortBy == "department" ? " selected" : "").Append(">По департаменту</option>")
      .Append("<option value=\"").Append(discSortBase).Append("sortBy=credits&sortOrder=desc\"").Append(sortBy == "credits" ? " selected" : "").Append(">По зач. ед. (убыв.)</option>")
      .Append("<option value=\"").Append(discSortBase).Append("sortBy=newest&sortOrder=desc\"").Append(sortBy == "newest" ? " selected" : "").Append(">По новизне</option>")
      .Append("</select></span>")
      .Append("<a class='btn btn--excel' data-export=\"excel\" href=\"/uidisciplines/export").Append(discExportQuery).Append("\" title='Выгрузить в Excel' aria-label='Выгрузить в Excel'><svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path fill=\"currentColor\" d=\"M12 3v10.17l3.59-3.58L17 11l-5 5-5-5 1.41-1.41L11 13.17V3h1zm-7 14h14v2H5v-2z\"/></svg><span>Выгрузить в Excel</span></a>");
    if (user.CanEditDisciplines)
      sb.Append("<span class=\"toolbar-import\"><form method='post' action='/uidisciplines/import' enctype='multipart/form-data'>")
        .Append("<input type='hidden' name='redirectQuery' value='").Append(ParseHelpers.H(redirectQuery)).Append("'>")
        .Append("<input type='hidden' name='planId' value='").Append(planIdDefault).Append("'>")
        .Append("<label class='btn btn--ghost btn--file'><input type='file' name='file' accept='.xlsx,.xls' required>Выберите файл</label>")
        .Append("<button type='submit' class='btn btn--ghost'>Загрузить из Excel</button>")
        .Append("</form></span>");
    sb.Append("</div>")
      .Append("<p class='hint'>Новые дисциплины добавляются в <a href='/uiplan' class='link'>Учебный план</a> — они автоматически появятся в этом списке.</p>")
      .Append("<div class='hint'>Показано до 500 строк</div>")
      .Append("</section>");

    sb.Append("<section class='stats-grid'>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon'>📚</div><span class='stat-badge stat-badge--green'>Всего</span></div>")
      .Append("<div class='stat-label'>Дисциплин</div>")
      .Append("<div class='stat-value'>").Append(discTotal).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--purple'>📦</div><span class='stat-badge stat-badge--blue'>Модули</span></div>")
      .Append("<div class='stat-label'>Модулей</div>")
      .Append("<div class='stat-value'>").Append(discModules).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--green'>🏛️</div><span class='stat-badge stat-badge--purple'>Департаменты</span></div>")
      .Append("<div class='stat-label'>Департаментов</div>")
      .Append("<div class='stat-value'>").Append(discDepts).Append("</div>")
      .Append("</div>")
      .Append("<div class='stat-card'>")
      .Append("<div class='stat-head'><div class='stat-icon stat-icon--orange'>⏱️</div><span class='stat-badge stat-badge--orange'>Зач.ед.</span></div>")
      .Append("<div class='stat-label'>Всего зач.ед.</div>")
      .Append("<div class='stat-value'>").Append(discCredits.ToString("0.##", CultureInfo.InvariantCulture)).Append("</div>")
      .Append("</div>")
      .Append("</section>")
      .Append("<dialog class='modal' id='disc-filter'>")
      .Append("<form method='get' action='/uidisciplines' class='form form--row'>")
      .Append("<input class='input' type='text' name='q' placeholder='Поиск по названию' value='").Append(ParseHelpers.H(q ?? "")).Append("'>")
      .Append(RenderDepartmentCheckboxes(departments, departmentIds, null))
      .Append(RenderModuleCheckboxes(moduleNos))
      .Append("<input type='hidden' name='sortBy' value='").Append(ParseHelpers.H(sortBy ?? "name")).Append("'>")
      .Append("<input type='hidden' name='sortOrder' value='").Append(ParseHelpers.H(sortOrder ?? "asc")).Append("'>")
      .Append("<div class='modal__actions'>")
      .Append("<button class='btn' type='submit'>Применить</button>")
      .Append("<button class='btn btn--ghost' type='button' data-dialog-close>Закрыть</button>")
      .Append("</div></form></dialog>");

    sb.Append("<section class='card card--flush'>")
      .Append("<form id='disc-batch-form' method='post' action='/uidisciplines/save-batch-form'>")
      .Append("<input type='hidden' name='planIdDefault' value='").Append(planIdDefault).Append("'>")
      .Append("<select id='disc-module-options' class='input' style='display:none'>")
      .Append(OptionsList(modules, null, "Модуль"))
      .Append("</select>")
      .Append("<select id='disc-dept-options' class='input' style='display:none'>")
      .Append(OptionsList(departments, null, "Департамент"))
      .Append("</select>")
      .Append("<select id='disc-disciplinekind-options' class='input' style='display:none'>")
      .Append(OptionsListStrings(SelectOptions.DisciplineKinds, null, "Вид"))
      .Append("</select>")
      .Append("<select id='disc-language-options' class='input' style='display:none'>")
      .Append(OptionsListStrings(SelectOptions.LanguageOptions, null, "Язык"))
      .Append("</select>");

    var canEditDisc = user.CanEditDisciplines;
    sb.Append("<div class='table-scroll-top'><div class='table-scroll-bar' role='scrollbar' aria-orientation='horizontal'><div class='table-scroll-bar__inner'></div></div><div class='table-wrap'><table class='table'><thead><tr>")
      .Append("<th><input type='checkbox' data-select-all='disc' aria-label='Выбрать все'").Append(canEditDisc ? ">" : " disabled>").Append("</th>")
      .Append("<th>№ дисциплины</th>")
      .Append("<th>Наименование дисциплины</th>")
      .Append("<th>Модуль</th>")
      .Append("<th>Департамент</th>")
      .Append("<th>Курс</th>")
      .Append("<th>Вид дисциплины</th>")
      .Append("<th>Язык</th>")
      .Append("<th>Зач.ед.</th>")
      .Append("</tr></thead><tbody>");

    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            var id = PlanRowReader.SafeReadInt32(r, 0);
            var moduleIdVal = r.IsDBNull(3) ? (int?)null : PlanRowReader.SafeReadInt32(r, 3);
            var deptIdVal = r.IsDBNull(4) ? (int?)null : PlanRowReader.SafeReadInt32(r, 4);
            var moduleName = moduleIdVal is null ? "" : (modules.FirstOrDefault(m => m.id == moduleIdVal.Value).name ?? "");
            var deptName = deptIdVal is null ? "" : (departments.FirstOrDefault(d => d.id == deptIdVal.Value).name ?? "");
            sb.Append("<tr>");
            if (canEditDisc)
                sb.Append("<td><input type='checkbox' class='row-select row-select-disc' name='selectPlanDisciplineId' value='").Append(id).Append("'><input type='hidden' name='rowId' value='").Append(id).Append("'></td>");
            else
                sb.Append("<td><input type='checkbox' class='row-select row-select-disc' disabled></td>");
            if (canEditDisc)
            {
                sb.Append("<td><input class='input' name='disciplineNo_").Append(id).Append("' value='").Append(ParseHelpers.H(r.IsDBNull(1) ? "" : r.GetString(1))).Append("'></td>")
                  .Append("<td><input class='input' name='disciplineName_").Append(id).Append("' value='").Append(ParseHelpers.H(r.IsDBNull(2) ? "" : r.GetString(2))).Append("'></td>")
                  .Append("<td><select class='input' name='moduleId_").Append(id).Append("'>").Append(OptionsList(modules, moduleIdVal, "Модуль")).Append("</select></td>")
                  .Append("<td><select class='input' name='departmentId_").Append(id).Append("'>").Append(OptionsList(departments, deptIdVal, "Департамент")).Append("</select></td>")
                  .Append("<td><input class='input' name='courseNo_").Append(id).Append("' value='").Append(PlanRowReader.SafeReadIntOrStringAsString(r, 5)).Append("'></td>")
                  .Append("<td><select class='input' name='disciplineKind_").Append(id).Append("'>").Append(OptionsListStrings(SelectOptions.DisciplineKinds, r.IsDBNull(6) ? null : r.GetString(6), "Вид")).Append("</select></td>")
                  .Append("<td><select class='input' name='language_").Append(id).Append("'>").Append(OptionsListStrings(SelectOptions.LanguageOptions, r.IsDBNull(7) ? null : r.GetString(7), "Язык")).Append("</select></td>")
                  .Append("<td><input class='input' name='credits_").Append(id).Append("' value='").Append(r.IsDBNull(8) ? "" : r.GetDecimal(8).ToString(CultureInfo.InvariantCulture)).Append("'></td>");
            }
            else
            {
                sb.Append("<td><input class='input input--readonly' value='").Append(ParseHelpers.H(r.IsDBNull(1) ? "" : r.GetString(1))).Append("' readonly></td>")
                  .Append("<td><input class='input input--readonly' value='").Append(ParseHelpers.H(r.IsDBNull(2) ? "" : r.GetString(2))).Append("' readonly></td>")
                  .Append("<td><input class='input input--readonly' value='").Append(ParseHelpers.H(moduleName)).Append("' readonly></td>")
                  .Append("<td><input class='input input--readonly' value='").Append(ParseHelpers.H(deptName)).Append("' readonly></td>")
                  .Append("<td><input class='input input--readonly' value='").Append(PlanRowReader.SafeReadIntOrStringAsString(r, 5)).Append("' readonly></td>")
                  .Append("<td>").Append(ParseHelpers.H(r.IsDBNull(6) ? "" : r.GetString(6))).Append("</td>")
                  .Append("<td>").Append(ParseHelpers.H(r.IsDBNull(7) ? "" : r.GetString(7))).Append("</td>")
                  .Append("<td><input class='input input--readonly' value='").Append(r.IsDBNull(8) ? "" : r.GetDecimal(8).ToString(CultureInfo.InvariantCulture)).Append("' readonly></td>");
            }
            sb.Append("</tr>");
        }
    }

    sb.Append("</tbody></table></div></div></form></section>");
    return Results.Content(Layout("Дисциплины", "disciplines", sb.ToString(), user.Login, user.IsAdmin), "text/html; charset=utf-8");
});

app.MapGet("/uidisciplines/export", async (HttpContext ctx, NpgsqlDataSource ds, string? q, string[]? departmentId, string[]? moduleNo) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    var qLike = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%";
    var moduleNos = ParseModuleNos(moduleNo);
    var departmentIds = ParseDepartmentIds(departmentId);
    if (!user.IsAdmin && user.AllowedDepartmentIds.Length > 0)
    {
        var allowedDept = new HashSet<int>(user.AllowedDepartmentIds);
        departmentIds = departmentIds.Length == 0 ? user.AllowedDepartmentIds : departmentIds.Where(d => allowedDept.Contains(d)).ToArray();
    }
    if (!user.IsAdmin && user.AllowedDepartmentIds.Length == 0)
        departmentIds = new[] { -1 };
    var discExportDepartmentIdsParam = departmentIds.Length == 0 ? (object?)null : departmentIds;
    var discExportModuleNosParam = moduleNos.Length == 0 ? (object?)null : moduleNos;
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(@"
        SELECT
            pd.plan_discipline_id,
            pd.discipline_no,
            pd.discipline_name,
            pm.module_name,
            d.name AS implementing_department,
            pd.course_no,
            pd.discipline_kind::text,
            pd.language,
            pd.credits
        FROM plan_disciplines pd
        LEFT JOIN plan_modules pm ON pd.module_id = pm.module_id
        LEFT JOIN departments d ON pd.implementing_department_id = d.department_id
        WHERE (@q IS NULL OR pd.discipline_name ILIKE @q OR pd.discipline_no ILIKE @q) AND
          (@departmentIds IS NULL OR array_length(@departmentIds, 1) IS NULL OR pd.implementing_department_id = ANY(@departmentIds)) AND
          (@moduleNos IS NULL OR array_length(@moduleNos, 1) IS NULL OR pm.module_number = ANY(@moduleNos))
        ORDER BY pd.discipline_name;
", conn);
    cmd.Parameters.AddWithValue("q", NpgsqlDbType.Text, (object?)qLike ?? DBNull.Value);
    cmd.Parameters.AddWithValue("departmentIds", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)discExportDepartmentIdsParam ?? DBNull.Value);
    cmd.Parameters.AddWithValue("moduleNos", NpgsqlDbType.Array | NpgsqlDbType.Integer, (object?)discExportModuleNosParam ?? DBNull.Value);
    using var wb = new XLWorkbook();
    var ws = wb.AddWorksheet("Дисциплины");
    var headers = new[] { "№ дисц.", "Наименование", "Модуль", "Реализующее подразделение", "Курс", "Вид дисциплины", "Язык", "Зач.ед." };
    for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
    var row = 2;
    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            ws.Cell(row, 1).Value = r.IsDBNull(1) ? "" : r.GetValue(1)?.ToString() ?? "";
            ws.Cell(row, 2).Value = r.IsDBNull(2) ? "" : r.GetString(2);
            ws.Cell(row, 3).Value = r.IsDBNull(3) ? "" : r.GetString(3);
            ws.Cell(row, 4).Value = r.IsDBNull(4) ? "" : r.GetString(4);
            ws.Cell(row, 5).Value = r.IsDBNull(5) ? "" : r.GetValue(5)?.ToString() ?? "";
            ws.Cell(row, 6).Value = r.IsDBNull(6) ? "" : r.GetString(6);
            ws.Cell(row, 7).Value = r.IsDBNull(7) ? "" : r.GetString(7);
            ws.Cell(row, 8).Value = r.IsDBNull(8) ? "" : r.GetDecimal(8);
            row++;
        }
    }
    byte[] bytes;
    using (var stream = new MemoryStream())
    {
        wb.SaveAs(stream, false);
        bytes = stream.ToArray();
    }
    var fileName = $"disciplines_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
    return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
});

app.MapPost("/uidisciplines/import", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditDisciplines) return Results.Forbid();
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file == null || file.Length == 0)
    {
        var q = form["redirectQuery"].ToString();
        return Results.Redirect("/uidisciplines" + (string.IsNullOrEmpty(q) ? "?" : "?" + q + "&") + "importError=noFile");
    }
    var redirectQuery = form["redirectQuery"].ToString();
    var planIdVal = ParseHelpers.IntOrNull(form["planId"].ToString());
    if (planIdVal is null || planIdVal == 0)
    {
        await using var conn0 = await ds.OpenConnectionAsync();
        await using var planCmd = new NpgsqlCommand("SELECT plan_id FROM study_plans ORDER BY plan_id LIMIT 1", conn0);
        var planObj = await planCmd.ExecuteScalarAsync();
        planIdVal = planObj is int id ? id : 0;
    }
    if (planIdVal == 0) return Results.Redirect("/uidisciplines" + (string.IsNullOrEmpty(redirectQuery) ? "?" : "?" + redirectQuery + "&") + "importError=" + Uri.EscapeDataString("Нет учебного плана в БД"));
    var discColumnMap = new Dictionary<string, string[]>
    {
        ["discipline_no"] = new[] { "№ дисц.", "№ дисциплины", "Номер" },
        ["discipline_name"] = new[] { "Наименование", "Наименование дисциплины", "Дисциплина" },
        ["module"] = new[] { "Модуль", "Модуль учебного плана" },
        ["department"] = new[] { "Реализующее подразделение", "Департамент", "Реализующий департамент" },
        ["course_no"] = new[] { "Курс" },
        ["discipline_kind"] = new[] { "Вид дисциплины", "Вид" },
        ["language"] = new[] { "Язык" },
        ["credits"] = new[] { "Зач.ед.", "Зачетные единицы" }
    };
    List<Dictionary<string, string>> rows;
    try
    {
        await using var stream = file.OpenReadStream();
        rows = ExcelImportHelper.ParseSheet(stream, discColumnMap);
    }
    catch (Exception ex)
    {
        return Results.Redirect("/uidisciplines" + (string.IsNullOrEmpty(redirectQuery) ? "?" : "?" + redirectQuery + "&") + "importError=" + Uri.EscapeDataString(ex.Message));
    }
    var departments = await LoadDepartments(ds);
    var modules = await LoadModules(ds);
    var errors = new List<string>();
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    foreach (var row in rows)
    {
        var disciplineName = ExcelImportHelper.Get(row, "discipline_name");
        if (string.IsNullOrWhiteSpace(disciplineName)) continue;
        var disciplineNo = ExcelImportHelper.Get(row, "discipline_no");
        var moduleName = ExcelImportHelper.Get(row, "module");
        var departmentName = ExcelImportHelper.Get(row, "department");
        var courseNo = ParseHelpers.IntOrNull(ExcelImportHelper.Get(row, "course_no"));
        var disciplineKind = ExcelImportHelper.Get(row, "discipline_kind");
        var language = ExcelImportHelper.Get(row, "language");
        var credits = ParseHelpers.DecimalOrNull(ExcelImportHelper.Get(row, "credits"));
        if (courseNo is not null && (courseNo < 1 || courseNo > 6)) { errors.Add($"Строка «{ParseHelpers.H(disciplineName)}»: курс должен быть 1–6"); continue; }
        int? moduleId = null;
        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            var mod = modules.FirstOrDefault(m => string.Equals(m.name, moduleName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (mod.id != 0) moduleId = mod.id;
        }
        int? departmentId = null;
        if (!string.IsNullOrWhiteSpace(departmentName))
        {
            var dept = departments.FirstOrDefault(d => string.Equals(d.name, departmentName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (dept.id != 0) departmentId = dept.id;
            else
            {
                await using var insDept = new NpgsqlCommand("INSERT INTO departments (name) VALUES (@name) RETURNING department_id", conn, tx);
                insDept.Parameters.AddWithValue("name", NpgsqlDbType.Text, departmentName.Trim());
                var newId = await insDept.ExecuteScalarAsync();
                if (newId is int did) departmentId = did;
            }
        }
        int? existingId = null;
        await using (var findCmd = new NpgsqlCommand(@"
SELECT plan_discipline_id FROM plan_disciplines
WHERE plan_id = @planId AND (TRIM(COALESCE(discipline_no,'')) = TRIM(COALESCE(@discNo,''))) LIMIT 1", conn, tx))
        {
            findCmd.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planIdVal!.Value);
            findCmd.Parameters.AddWithValue("discNo", NpgsqlDbType.Text, (object?)disciplineNo ?? DBNull.Value);
            var fid = await findCmd.ExecuteScalarAsync();
            if (fid is int id) existingId = id;
        }
        try
        {
            if (existingId is int eid)
            {
                await using var update = new NpgsqlCommand(@"
UPDATE plan_disciplines SET discipline_name = @discName, module_id = @moduleId, implementing_department_id = @depId,
  course_no = COALESCE(@courseNo, course_no), discipline_kind = (COALESCE(@discKind, discipline_kind::text))::discipline_kind,
  language = COALESCE(@language, language), credits = COALESCE(@credits, credits)
WHERE plan_discipline_id = @id", conn, tx);
                update.Parameters.AddWithValue("discName", NpgsqlDbType.Text, disciplineName);
                update.Parameters.AddWithValue("moduleId", NpgsqlDbType.Integer, (object?)moduleId ?? DBNull.Value);
                update.Parameters.AddWithValue("depId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
                update.Parameters.AddWithValue("courseNo", NpgsqlDbType.Integer, (object?)courseNo ?? DBNull.Value);
                update.Parameters.AddWithValue("discKind", NpgsqlDbType.Text, (object?)disciplineKind ?? DBNull.Value);
                update.Parameters.AddWithValue("language", NpgsqlDbType.Text, (object?)language ?? DBNull.Value);
                update.Parameters.AddWithValue("credits", NpgsqlDbType.Numeric, (object?)credits ?? DBNull.Value);
                update.Parameters.AddWithValue("id", NpgsqlDbType.Integer, eid);
                await update.ExecuteNonQueryAsync();
            }
            else
            {
                await using var insert = new NpgsqlCommand(@"
INSERT INTO plan_disciplines (plan_id, module_id, discipline_no, discipline_name, implementing_department_id, course_no, discipline_kind, language, credits)
VALUES (@planId, @moduleId, @discNo, @discName, @depId, @courseNo, @discKind::discipline_kind, @language, @credits);", conn, tx);
                insert.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planIdVal!.Value);
                insert.Parameters.AddWithValue("moduleId", NpgsqlDbType.Integer, (object?)moduleId ?? DBNull.Value);
                insert.Parameters.AddWithValue("discNo", NpgsqlDbType.Text, (object?)disciplineNo ?? DBNull.Value);
                insert.Parameters.AddWithValue("discName", NpgsqlDbType.Text, disciplineName);
                insert.Parameters.AddWithValue("depId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
                insert.Parameters.AddWithValue("courseNo", NpgsqlDbType.Integer, (object?)courseNo ?? DBNull.Value);
                insert.Parameters.AddWithValue("discKind", NpgsqlDbType.Text, (object?)disciplineKind ?? DBNull.Value);
                insert.Parameters.AddWithValue("language", NpgsqlDbType.Text, (object?)language ?? DBNull.Value);
                insert.Parameters.AddWithValue("credits", NpgsqlDbType.Numeric, (object?)credits ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex) { errors.Add($"«{ParseHelpers.H(disciplineName)}»: {ex.Message}"); }
    }
    if (errors.Count > 0)
    {
        await tx.RollbackAsync();
        var errText = string.Join("; ", errors.Take(5)) + (errors.Count > 5 ? " …" : "");
        if (errText.Length > 800) errText = errText.Substring(0, 797) + "...";
        return Results.Redirect("/uidisciplines" + (string.IsNullOrEmpty(redirectQuery) ? "?" : "?" + redirectQuery + "&") + "importError=" + Uri.EscapeDataString(errText));
    }
    await tx.CommitAsync();
    return Results.Redirect("/uidisciplines" + (string.IsNullOrEmpty(redirectQuery) ? "" : "?" + redirectQuery));
}).DisableAntiforgery();

app.MapPost("/uidisciplines/delete", async (HttpContext ctx, NpgsqlDataSource ds, int? planDisciplineId) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditDisciplines) return Results.Forbid();
    if (planDisciplineId is null) return Results.Redirect("/uidisciplines");
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    await using (var cmd = new NpgsqlCommand(@"
DELETE FROM assignment_hours
WHERE assignment_id IN (SELECT assignment_id FROM teaching_assignments WHERE plan_discipline_id = @id);", conn, tx))
    {
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, planDisciplineId.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    await using (var cmd2 = new NpgsqlCommand("DELETE FROM teaching_assignments WHERE plan_discipline_id = @id", conn, tx))
    {
        cmd2.Parameters.AddWithValue("id", NpgsqlDbType.Integer, planDisciplineId.Value);
        await cmd2.ExecuteNonQueryAsync();
    }
    await using (var cmd3 = new NpgsqlCommand("DELETE FROM plan_disciplines WHERE plan_discipline_id = @id", conn, tx))
    {
        cmd3.Parameters.AddWithValue("id", NpgsqlDbType.Integer, planDisciplineId.Value);
        await cmd3.ExecuteNonQueryAsync();
    }
    await tx.CommitAsync();
    return Results.Redirect("/uidisciplines");
});

app.MapPost("/uidisciplines/delete-batch", async (HttpContext ctx, NpgsqlDataSource ds, int[]? planDisciplineIds) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditDisciplines) return Results.Forbid();
    if (planDisciplineIds is null || planDisciplineIds.Length == 0) return Results.Redirect("/uidisciplines");
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    foreach (var id in planDisciplineIds)
    {
        await using (var cmd = new NpgsqlCommand(@"
DELETE FROM assignment_hours
WHERE assignment_id IN (SELECT assignment_id FROM teaching_assignments WHERE plan_discipline_id = @id);", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd2 = new NpgsqlCommand("DELETE FROM teaching_assignments WHERE plan_discipline_id = @id", conn, tx))
        {
            cmd2.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
            await cmd2.ExecuteNonQueryAsync();
        }
        await using (var cmd3 = new NpgsqlCommand("DELETE FROM plan_disciplines WHERE plan_discipline_id = @id", conn, tx))
        {
            cmd3.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
            await cmd3.ExecuteNonQueryAsync();
        }
    }
    await tx.CommitAsync();
    return Results.Redirect("/uidisciplines");
});

app.MapPost("/uidisciplines/save-batch-form", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditDisciplines) return Results.Forbid();
    var form = await request.ReadFormAsync();
    var rowKeys = form["rowId"];
    if (rowKeys.Count == 0) return Results.Redirect("/uidisciplines");

    var planIdDefault = ParseHelpers.IntOrNull(form["planIdDefault"]);
    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();
    var errors = new List<string>();

    foreach (var rowKey in rowKeys)
    {
        if (string.IsNullOrWhiteSpace(rowKey)) continue;
        var id = ParseHelpers.IntOrNull(rowKey);

        string? Get(string key)
        {
            var value = form[$"{key}_{rowKey}"].ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        var disciplineNo = Get("disciplineNo");
        var disciplineName = Get("disciplineName");
        var moduleId = ParseHelpers.IntOrNull(Get("moduleId"));
        var departmentId = ParseHelpers.IntOrNull(Get("departmentId"));
        var courseNo = ParseHelpers.IntOrNull(Get("courseNo"));
        var disciplineKind = Get("disciplineKind");
        var language = Get("language");
        var credits = ParseHelpers.DecimalOrNull(Get("credits"));

        if (string.IsNullOrWhiteSpace(disciplineName))
        {
            errors.Add($"Наименование дисциплины обязательно (строка {rowKey})");
            continue;
        }
        if (credits is not null && credits < 0)
        {
            errors.Add($"Зач.ед. не могут быть отрицательными (строка {rowKey})");
            continue;
        }
        if (courseNo is not null && (courseNo < 1 || courseNo > 6))
        {
            errors.Add($"Курс должен быть от 1 до 6 (строка {rowKey})");
            continue;
        }

        if (id is null)
        {
            if (planIdDefault is null)
            {
                errors.Add($"Не указан план по умолчанию для новой строки (строка {rowKey})");
                continue;
            }
            await using var insert = new NpgsqlCommand(@"
INSERT INTO plan_disciplines (
    plan_id, module_id, discipline_no, discipline_name, implementing_department_id,
    course_no, discipline_kind, language, credits
)
VALUES (
    @planId, @moduleId, @discNo, @discName, @depId,
    @courseNo, @discKind, @language, @credits
);", conn, tx);
            insert.Parameters.AddWithValue("planId", NpgsqlDbType.Integer, planIdDefault.Value);
            insert.Parameters.AddWithValue("moduleId", NpgsqlDbType.Integer, (object?)moduleId ?? DBNull.Value);
            insert.Parameters.AddWithValue("discNo", NpgsqlDbType.Text, (object?)disciplineNo ?? DBNull.Value);
            insert.Parameters.AddWithValue("discName", NpgsqlDbType.Text, disciplineName);
            insert.Parameters.AddWithValue("depId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
            insert.Parameters.AddWithValue("courseNo", NpgsqlDbType.Integer, (object?)courseNo ?? DBNull.Value);
            insert.Parameters.AddWithValue("discKind", NpgsqlDbType.Text, (object?)disciplineKind ?? DBNull.Value);
            insert.Parameters.AddWithValue("language", NpgsqlDbType.Text, (object?)language ?? DBNull.Value);
            insert.Parameters.AddWithValue("credits", NpgsqlDbType.Numeric, (object?)credits ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        else
        {
            await using var update = new NpgsqlCommand(@"
UPDATE plan_disciplines
SET discipline_no = COALESCE(@discNo, discipline_no),
    discipline_name = COALESCE(@discName, discipline_name),
    module_id = COALESCE(@moduleId, module_id),
    implementing_department_id = COALESCE(@depId, implementing_department_id),
    course_no = COALESCE(@courseNo, course_no),
    discipline_kind = (COALESCE(@discKind, discipline_kind::text))::discipline_kind,
    language = COALESCE(@language, language),
    credits = COALESCE(@credits, credits)
WHERE plan_discipline_id = @id;", conn, tx);
            update.Parameters.AddWithValue("discNo", NpgsqlDbType.Text, (object?)disciplineNo ?? DBNull.Value);
            update.Parameters.AddWithValue("discName", NpgsqlDbType.Text, (object?)disciplineName ?? DBNull.Value);
            update.Parameters.AddWithValue("moduleId", NpgsqlDbType.Integer, (object?)moduleId ?? DBNull.Value);
            update.Parameters.AddWithValue("depId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
            update.Parameters.AddWithValue("courseNo", NpgsqlDbType.Integer, (object?)courseNo ?? DBNull.Value);
            update.Parameters.AddWithValue("discKind", NpgsqlDbType.Text, (object?)disciplineKind ?? DBNull.Value);
            update.Parameters.AddWithValue("language", NpgsqlDbType.Text, (object?)language ?? DBNull.Value);
            update.Parameters.AddWithValue("credits", NpgsqlDbType.Numeric, (object?)credits ?? DBNull.Value);
            update.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id.Value);
            await update.ExecuteNonQueryAsync();
        }
    }

    if (errors.Count > 0)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(string.Join("\n", errors));
    }

    await tx.CommitAsync();
    return Results.Redirect("/uidisciplines");
}).DisableAntiforgery();

app.MapPost("/uifaculty/save-batch", async (HttpContext ctx, NpgsqlDataSource ds, HttpRequest request) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.CanEditFaculty) return Results.Forbid();
    static string? JsonToString(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => el.ToString()
        };
    }

    var rows = await JsonSerializer.DeserializeAsync<List<Dictionary<string, JsonElement>>>(
        request.Body,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
    );

    if (rows is null || rows.Count == 0)
        return Results.BadRequest("rows required");

    await using var conn = await ds.OpenConnectionAsync();
    await using var tx = await conn.BeginTransactionAsync();

    foreach (var row in rows)
    {
        string? Get(string key) => row.TryGetValue(key, out var el) ? JsonToString(el) : null;

        var ppsNo = ParseHelpers.IntOrNull(Get("ppsNo"));
        var ppsName = Get("ppsName");
        var position = Get("position");
        var rate = ParseHelpers.DecimalOrNull(Get("rate"));
        var nrdShare = ParseHelpers.DecimalOrNull(Get("nrdShare"));
        var zeroOut = Get("zeroOut");
        var track = Get("track");
        var departmentId = ParseHelpers.IntOrNull(Get("departmentId"));
        var auditoryLoad = ParseHelpers.DecimalOrNull(Get("auditoryLoad"));
        var normHours = ParseHelpers.DecimalOrNull(Get("normHours"));
        var performedLoad = ParseHelpers.DecimalOrNull(Get("performedLoad"));
        var responsibleLoad = ParseHelpers.DecimalOrNull(Get("responsibleLoad"));
        var isActiveValue = ParseHelpers.BoolOrNull(Get("isActive"));

        if (string.IsNullOrWhiteSpace(ppsName)) continue;

        if (ppsNo is null)
        {
            await using var insert = new NpgsqlCommand(@"
INSERT INTO faculty_members (
    full_name, department_id, is_active, position, rate, nrd_share, zero_out_total,
    track, auditory_load, norm_hours, performed_load, responsible_load
)
VALUES (
    @fullName, @departmentId, COALESCE(@isActive, true), @position, @rate, @nrd, @zeroOut,
    @track, @auditoryLoad, @normHours, @performedLoad, @responsibleLoad
);
", conn, tx);
            insert.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, ppsName);
            insert.Parameters.AddWithValue("position", NpgsqlDbType.Text, (object?)position ?? DBNull.Value);
            insert.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, (object?)rate ?? DBNull.Value);
            insert.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, (object?)nrdShare ?? DBNull.Value);
            insert.Parameters.AddWithValue("zeroOut", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(zeroOut) ?? DBNull.Value);
            insert.Parameters.AddWithValue("track", NpgsqlDbType.Text, (object?)track ?? DBNull.Value);
            insert.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
            insert.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, (object?)isActiveValue ?? DBNull.Value);
            insert.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
            insert.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
            insert.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
            insert.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        else
        {
            await using var cmd = new NpgsqlCommand(@"
UPDATE faculty_members
SET full_name = COALESCE(@fullName, full_name),
    position = COALESCE(@position, position),
    rate = COALESCE(@rate, rate),
    nrd_share = COALESCE(@nrd, nrd_share),
    zero_out_total = COALESCE(@zeroOut, zero_out_total),
    track = COALESCE(@track, track),
    department_id = COALESCE(@departmentId, department_id),
    is_active = COALESCE(@isActive, is_active),
    auditory_load = COALESCE(@auditoryLoad, auditory_load),
    norm_hours = COALESCE(@normHours, norm_hours),
    performed_load = COALESCE(@performedLoad, performed_load),
    responsible_load = COALESCE(@responsibleLoad, responsible_load)
WHERE faculty_id = @facultyId;
", conn, tx);
            cmd.Parameters.AddWithValue("fullName", NpgsqlDbType.Text, (object?)ppsName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("position", NpgsqlDbType.Text, (object?)position ?? DBNull.Value);
            cmd.Parameters.AddWithValue("rate", NpgsqlDbType.Numeric, (object?)rate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("nrd", NpgsqlDbType.Numeric, (object?)nrdShare ?? DBNull.Value);
            cmd.Parameters.AddWithValue("zeroOut", NpgsqlDbType.Boolean, (object?)ParseHelpers.BoolOrNull(zeroOut) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("track", NpgsqlDbType.Text, (object?)track ?? DBNull.Value);
            cmd.Parameters.AddWithValue("departmentId", NpgsqlDbType.Integer, (object?)departmentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("isActive", NpgsqlDbType.Boolean, (object?)isActiveValue ?? DBNull.Value);
            cmd.Parameters.AddWithValue("auditoryLoad", NpgsqlDbType.Numeric, (object?)auditoryLoad ?? DBNull.Value);
            cmd.Parameters.AddWithValue("normHours", NpgsqlDbType.Numeric, (object?)normHours ?? DBNull.Value);
            cmd.Parameters.AddWithValue("performedLoad", NpgsqlDbType.Numeric, (object?)performedLoad ?? DBNull.Value);
            cmd.Parameters.AddWithValue("responsibleLoad", NpgsqlDbType.Numeric, (object?)responsibleLoad ?? DBNull.Value);
            cmd.Parameters.AddWithValue("facultyId", NpgsqlDbType.Integer, ppsNo.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    await tx.CommitAsync();
    return Results.Ok();
}).DisableAntiforgery();

// ==================== Admin: пользователи (только Admin) ====================
app.MapGet("/admin/users", async (HttpContext ctx, NpgsqlDataSource ds) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.IsAdmin) return Results.Redirect("/uiworkload");
    await EnsureAuthTables(ds);
    var list = new List<(int id, string login, string displayName, string role)>();
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("SELECT user_id, login, COALESCE(display_name,''), role FROM app_users ORDER BY login", conn);
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync()) list.Add((PlanRowReader.SafeReadInt32(r, 0), r.GetString(1), r.GetString(2), r.GetString(3)));
    var sb = new StringBuilder();
    sb.Append("<section class='page-header'><h1 class='page-title'>Пользователи</h1><p class='page-subtitle'>Управление доступами (роль, ОП, департаменты)</p></section>");
    sb.Append("<section class='card'><a class='btn' href='/admin/users/new'>Добавить пользователя</a></section>");
    sb.Append("<section class='card card--flush'><table class='table'><thead><tr><th>Логин</th><th>Имя</th><th>Роль</th><th></th></tr></thead><tbody>");
    foreach (var u in list)
        sb.Append("<tr><td>").Append(ParseHelpers.H(u.login)).Append("</td><td>").Append(ParseHelpers.H(u.displayName)).Append("</td><td>").Append(ParseHelpers.H(u.role)).Append("</td><td><a class='link' href='/admin/users/edit?userId=").Append(u.id).Append("'>Изменить</a></td></tr>");
    sb.Append("</tbody></table></section>");
    return Results.Content(Layout("Пользователи", "admin", sb.ToString(), user.Login, user.IsAdmin), "text/html; charset=utf-8");
});

app.MapGet("/admin/users/new", async (HttpContext ctx, NpgsqlDataSource ds) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.IsAdmin) return Results.Redirect("/uiworkload");
    var departments = await LoadDepartments(ds);
    var allOps = OpMagistracy.Concat(OpBachelor).Distinct().ToArray();
    var sb = new StringBuilder();
    var formStyles = @"<style>
.card.card--form{width:100%;max-width:100%;padding:2rem;}
form.form.form--stacked.form--admin{max-width:none!important;width:100%!important;}
.form.form--admin .form__row{display:grid!important;grid-template-columns:1fr 1fr!important;gap:2.5rem;width:100%;}
.form.form--admin .form__col{min-width:0;width:100%;}
.form.form--admin .form__field{width:100%;}
.form.form--admin .form__field .form__input{margin-top:1.75rem;}
.form.form--admin .form__input{width:100%!important;max-width:none!important;box-sizing:border-box;}
.form.form--admin .form__input--multi{width:100%!important;max-width:none!important;min-height:8rem;}
@media(max-width:640px){.form.form--admin .form__row{grid-template-columns:1fr!important;}}
</style>";
    sb.Append(formStyles);
    sb.Append("<section class='page-header'><h1 class='page-title'>Новый пользователь</h1></section>");
    sb.Append("<section class='card card--form'><form method='post' action='/admin/users/create' class='form form--stacked form--admin'>");
    sb.Append("<div class='form__row'>");
    sb.Append("<div class='form__col'><div class='form__field'><label class='form__label' for='login'>Логин</label><input id='login' class='form__input' name='login' required autocomplete='username'></div>");
    sb.Append("<div class='form__field'><label class='form__label' for='password'>Пароль</label><input id='password' class='form__input' type='password' name='password' required autocomplete='new-password'></div>");
    sb.Append("<div class='form__field'><label class='form__label' for='displayName'>Отображаемое имя</label><input id='displayName' class='form__input' name='displayName' placeholder='Как показывать в интерфейсе'></div>");
    sb.Append("<div class='form__field'><label class='form__label' for='role'>Роль</label><select id='role' class='form__input' name='role'>")
      .Append("<option value='User'>User</option>")
      .Append("<option value='Admin'>Admin</option>")
      .Append("<option value='AcademicDirector'>Академический руководитель</option>")
      .Append("<option value='DepartmentManager'>Менеджер департамента</option>")
      .Append("</select></div></div>");
    sb.Append("<div class='form__col'><div class='form__field'><label class='form__label' for='opName'>Образовательные программы</label><select id='opName' class='form__input form__input--multi' name='opName' multiple size='6' title='Ctrl+клик для выбора нескольких'>");
    foreach (var op in allOps)
        sb.Append("<option value='").Append(ParseHelpers.H(op)).Append("'>").Append(ParseHelpers.H(op)).Append("</option>");
    sb.Append("</select><span class='form__hint'>Удерживайте Ctrl для выбора нескольких</span></div>");
    sb.Append("<div class='form__field'><label class='form__label' for='departmentId'>Департаменты</label><select id='departmentId' class='form__input form__input--multi' name='departmentId' multiple size='6' title='Ctrl+клик для выбора нескольких'>");
    foreach (var d in departments)
        sb.Append("<option value='").Append(d.id).Append("'>").Append(ParseHelpers.H(d.name)).Append("</option>");
    sb.Append("</select><span class='form__hint'>Удерживайте Ctrl для выбора нескольких</span></div></div>");
    sb.Append("</div>");
    sb.Append("<div class='form__actions'><button class='btn' type='submit'>Создать</button><a class='btn btn--ghost' href='/admin/users'>Отмена</a></div></form></section>");
    return Results.Content(Layout("Новый пользователь", "admin", sb.ToString(), user.Login, user.IsAdmin), "text/html; charset=utf-8");
});

app.MapPost("/admin/users/create", async (HttpContext ctx, NpgsqlDataSource ds, IFormCollection form) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.IsAdmin) return Results.Redirect("/uiworkload");
    var login = form["login"].ToString()?.Trim();
    var password = form["password"].ToString();
    var displayName = form["displayName"].ToString()?.Trim();
    var role = form["role"].ToString()?.Trim();
    if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password)) return Results.Redirect("/admin/users/new?error=1");
    if (role != "Admin" && role != "User" && role != "AcademicDirector" && role != "DepartmentManager") role = "User";
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("INSERT INTO app_users (login, password_hash, display_name, role) VALUES (@login, @hash, @dn, @role) RETURNING user_id", conn);
    cmd.Parameters.AddWithValue("login", NpgsqlDbType.Varchar, login);
    cmd.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, AuthHelpers.HashPassword(password));
    cmd.Parameters.AddWithValue("dn", NpgsqlDbType.Varchar, (object?)displayName ?? DBNull.Value);
    cmd.Parameters.AddWithValue("role", NpgsqlDbType.Varchar, role);
    var userId = (int)(await cmd.ExecuteScalarAsync() ?? 0);
    foreach (var op in form["opName"].Where(x => !string.IsNullOrWhiteSpace(x)))
    {
        await using var c = new NpgsqlCommand("INSERT INTO user_allowed_ops (user_id, op_name) VALUES (@uid, @op) ON CONFLICT DO NOTHING", conn);
        c.Parameters.AddWithValue("uid", NpgsqlDbType.Integer, userId);
        c.Parameters.AddWithValue("op", NpgsqlDbType.Varchar, (op?.ToString() ?? "").Trim());
        await c.ExecuteNonQueryAsync();
    }
    foreach (var didStr in form["departmentId"])
    {
        if (!int.TryParse(didStr, out var id)) continue;
        await using var c = new NpgsqlCommand("INSERT INTO user_allowed_departments (user_id, department_id) VALUES (@uid, @did) ON CONFLICT DO NOTHING", conn);
        c.Parameters.AddWithValue("uid", NpgsqlDbType.Integer, userId);
        c.Parameters.AddWithValue("did", NpgsqlDbType.Integer, id);
        await c.ExecuteNonQueryAsync();
    }
    return Results.Redirect("/admin/users");
}).DisableAntiforgery();

app.MapGet("/admin/users/edit", async (HttpContext ctx, NpgsqlDataSource ds, int? userId) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.IsAdmin) return Results.Redirect("/uiworkload");
    if (userId is null) return Results.Redirect("/admin/users");
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("SELECT login, COALESCE(display_name,''), role FROM app_users WHERE user_id = @id", conn);
    cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, userId.Value);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync()) return Results.Redirect("/admin/users");
    var login = r.GetString(0);
    var displayName = r.GetString(1);
    var role = r.GetString(2);
    await r.CloseAsync();
    var allowedOps = new List<string>();
    await using (var c = new NpgsqlCommand("SELECT op_name FROM user_allowed_ops WHERE user_id = @id", conn))
    {
        c.Parameters.AddWithValue("id", NpgsqlDbType.Integer, userId.Value);
        await using var rr = await c.ExecuteReaderAsync();
        while (await rr.ReadAsync()) allowedOps.Add(rr.GetString(0));
    }
    var allowedDepts = new List<int>();
    await using (var c = new NpgsqlCommand("SELECT department_id FROM user_allowed_departments WHERE user_id = @id", conn))
    {
        c.Parameters.AddWithValue("id", NpgsqlDbType.Integer, userId.Value);
        await using var rr = await c.ExecuteReaderAsync();
        while (await rr.ReadAsync()) allowedDepts.Add(PlanRowReader.SafeReadInt32(rr, 0));
    }
    var departments = await LoadDepartments(ds);
    var allOps = OpMagistracy.Concat(OpBachelor).Distinct().ToArray();
    var sb = new StringBuilder();
    var formStylesEdit = @"<style>
.card.card--form{width:100%;max-width:100%;padding:2rem;}
form.form.form--stacked.form--admin{max-width:none!important;width:100%!important;}
.form.form--admin .form__row{display:grid!important;grid-template-columns:1fr 1fr!important;gap:2.5rem;width:100%;}
.form.form--admin .form__col{min-width:0;width:100%;}
.form.form--admin .form__field{width:100%;}
.form.form--admin .form__field .form__input{margin-top:1.75rem;}
.form.form--admin .form__input{width:100%!important;max-width:none!important;box-sizing:border-box;}
.form.form--admin .form__input--multi{width:100%!important;max-width:none!important;min-height:8rem;}
@media(max-width:640px){.form.form--admin .form__row{grid-template-columns:1fr!important;}}
</style>";
    sb.Append(formStylesEdit);
    sb.Append("<section class='page-header'><h1 class='page-title'>Редактировать: ").Append(ParseHelpers.H(login)).Append("</h1></section>");
    sb.Append("<section class='card card--form'><form method='post' action='/admin/users/save' class='form form--stacked form--admin'>");
    sb.Append("<input type='hidden' name='userId' value='").Append(userId.Value).Append("'>");
    sb.Append("<div class='form__row'>");
    sb.Append("<div class='form__col'><div class='form__field'><label class='form__label'>Логин</label><input class='form__input' value='").Append(ParseHelpers.H(login)).Append("' disabled></div>");
    sb.Append("<div class='form__field'><label class='form__label' for='displayName'>Отображаемое имя</label><input id='displayName' class='form__input' name='displayName' value='").Append(ParseHelpers.H(displayName)).Append("'></div>");
    sb.Append("<div class='form__field'><label class='form__label' for='role'>Роль</label><select id='role' class='form__input' name='role'>")
      .Append("<option value='User'" + (role == "User" ? " selected" : "") + ">User</option>")
      .Append("<option value='Admin'" + (role == "Admin" ? " selected" : "") + ">Admin</option>")
      .Append("<option value='AcademicDirector'" + (role == "AcademicDirector" ? " selected" : "") + ">Академический руководитель</option>")
      .Append("<option value='DepartmentManager'" + (role == "DepartmentManager" ? " selected" : "") + ">Менеджер департамента</option>")
      .Append("</select></div>");
    sb.Append("<div class='form__field'><label class='form__label' for='newPassword'>Новый пароль</label><input id='newPassword' class='form__input' type='password' name='newPassword' placeholder='Оставьте пустым, чтобы не менять' autocomplete='new-password'></div></div>");
    sb.Append("<div class='form__col'><div class='form__field'><label class='form__label' for='opName'>Образовательные программы</label><select id='opName' class='form__input form__input--multi' name='opName' multiple size='6' title='Ctrl+клик для выбора нескольких'>");
    var opSet = new HashSet<string>(allowedOps, StringComparer.OrdinalIgnoreCase);
    foreach (var op in allOps)
        sb.Append("<option value='").Append(ParseHelpers.H(op)).Append("'").Append(opSet.Contains(op) ? " selected" : "").Append(">").Append(ParseHelpers.H(op)).Append("</option>");
    sb.Append("</select><span class='form__hint'>Удерживайте Ctrl для выбора нескольких</span></div>");
    sb.Append("<div class='form__field'><label class='form__label' for='departmentId'>Департаменты</label><select id='departmentId' class='form__input form__input--multi' name='departmentId' multiple size='6' title='Ctrl+клик для выбора нескольких'>");
    var deptSet = new HashSet<int>(allowedDepts);
    foreach (var d in departments)
        sb.Append("<option value='").Append(d.id).Append("'").Append(deptSet.Contains(d.id) ? " selected" : "").Append(">").Append(ParseHelpers.H(d.name)).Append("</option>");
    sb.Append("</select><span class='form__hint'>Удерживайте Ctrl для выбора нескольких</span></div></div>");
    sb.Append("</div>");
    sb.Append("<div class='form__actions'><button class='btn' type='submit'>Сохранить</button><a class='btn btn--ghost' href='/admin/users'>Отмена</a></div></form></section>");
    return Results.Content(Layout("Редактировать пользователя", "admin", sb.ToString(), user.Login, user.IsAdmin), "text/html; charset=utf-8");
});

app.MapPost("/admin/users/save", async (HttpContext ctx, NpgsqlDataSource ds, IFormCollection form) =>
{
    var user = await GetCurrentUser(ds, ctx.User);
    if (user is null) return Results.Redirect("/login");
    if (!user.IsAdmin) return Results.Redirect("/uiworkload");
    var userId = ParseHelpers.IntOrNull(form["userId"].ToString());
    if (userId is null) return Results.Redirect("/admin/users");
    var displayName = form["displayName"].ToString()?.Trim();
    var role = form["role"].ToString()?.Trim();
    if (role != "Admin" && role != "User" && role != "AcademicDirector" && role != "DepartmentManager") role = "User";
    await using var conn = await ds.OpenConnectionAsync();
    await using (var cmd = new NpgsqlCommand("UPDATE app_users SET display_name = @dn, role = @role WHERE user_id = @id", conn))
    {
        cmd.Parameters.AddWithValue("dn", NpgsqlDbType.Varchar, (object?)displayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("role", NpgsqlDbType.Varchar, role);
        cmd.Parameters.AddWithValue("id", NpgsqlDbType.Integer, userId.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    var newPassword = form["newPassword"].ToString()?.Trim();
    if (!string.IsNullOrEmpty(newPassword))
        await SetPassword(ds, userId.Value, newPassword);
    await using (var del = new NpgsqlCommand("DELETE FROM user_allowed_ops WHERE user_id = @id", conn))
    {
        del.Parameters.AddWithValue("id", NpgsqlDbType.Integer, userId.Value);
        await del.ExecuteNonQueryAsync();
    }
    await using (var del = new NpgsqlCommand("DELETE FROM user_allowed_departments WHERE user_id = @id", conn))
    {
        del.Parameters.AddWithValue("id", NpgsqlDbType.Integer, userId.Value);
        await del.ExecuteNonQueryAsync();
    }
    foreach (var op in form["opName"].Where(x => !string.IsNullOrWhiteSpace(x)))
    {
        await using var c = new NpgsqlCommand("INSERT INTO user_allowed_ops (user_id, op_name) VALUES (@uid, @op)", conn);
        c.Parameters.AddWithValue("uid", NpgsqlDbType.Integer, userId.Value);
        c.Parameters.AddWithValue("op", NpgsqlDbType.Varchar, (op?.ToString() ?? "").Trim());
        await c.ExecuteNonQueryAsync();
    }
    foreach (var didStr in form["departmentId"])
    {
        if (!int.TryParse(didStr, out var id)) continue;
        await using var c = new NpgsqlCommand("INSERT INTO user_allowed_departments (user_id, department_id) VALUES (@uid, @did)", conn);
        c.Parameters.AddWithValue("uid", NpgsqlDbType.Integer, userId.Value);
        c.Parameters.AddWithValue("did", NpgsqlDbType.Integer, id);
        await c.ExecuteNonQueryAsync();
    }
    return Results.Redirect("/admin/users");
}).DisableAntiforgery();

app.MapGet("/uidb", async (NpgsqlDataSource ds) =>
{
    await using var conn = await ds.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(@"
SELECT table_name
FROM information_schema.tables
WHERE table_schema='public'
ORDER BY table_name;
", conn);

    var sb = new StringBuilder();
    sb.Append("<h1 class=\"h1\">DB</h1><section class=\"card\"><ul class=\"list\">");

    await using (var r = await cmd.ExecuteReaderAsync())
    {
        while (await r.ReadAsync())
        {
            var t = r.GetString(0);
            sb.Append("<li class=\"list__item\"><a class=\"link\" href=\"/uidbtable?table=")
              .Append(WebUtility.UrlEncode(t)).Append("\">").Append(ParseHelpers.H(t)).Append("</a></li>");
        }
    }

    sb.Append("</ul></section>");
    return Results.Content(Layout("DB", "db", sb.ToString()), "text/html; charset=utf-8");
});

app.MapGet("/uidbtable", async (NpgsqlDataSource ds, string table, int limit = 50, int offset = 0) =>
{
    bool IsSafeIdentifier(string name) => Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$");
    string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    if (!IsSafeIdentifier(table))
        return Results.Content(Layout("DB", "db", "<section class=\"card\">Bad table name</section>"), "text/html; charset=utf-8");

    limit = Math.Clamp(limit, 1, 200);
    offset = Math.Max(offset, 0);

    await using var conn = await ds.OpenConnectionAsync();
    var sql = $"SELECT * FROM {QuoteIdent(table)} ORDER BY 1 LIMIT @limit OFFSET @offset";
    await using var cmd = new NpgsqlCommand(sql, conn);
    cmd.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
    cmd.Parameters.AddWithValue("offset", NpgsqlDbType.Integer, offset);

    var sb = new StringBuilder();
    sb.Append("<h1 class=\"h1\">").Append(ParseHelpers.H(table)).Append("</h1>")
      .Append("<section class=\"card\"><form method=\"get\" class=\"form form--row\">")
      .Append("<input class=\"input\" type=\"hidden\" name=\"table\" value=\"").Append(ParseHelpers.H(table)).Append("\">")
      .Append("<input class=\"input\" type=\"number\" name=\"limit\" value=\"").Append(limit).Append("\">")
      .Append("<input class=\"input\" type=\"number\" name=\"offset\" value=\"").Append(offset).Append("\">")
      .Append("<button class=\"btn\" type=\"submit\">Загрузить</button>")
      .Append("<a class=\"link\" href=\"/uidb\">Назад</a>")
      .Append("</form></section>");

    await using var r = await cmd.ExecuteReaderAsync();

    sb.Append("<section class=\"card card--flush\"><div class=\"table-wrap\"><table class=\"table\"><thead><tr>");
    for (int i = 0; i < r.FieldCount; i++) sb.Append("<th>").Append(ParseHelpers.H(r.GetName(i))).Append("</th>");
    sb.Append("</tr></thead><tbody>");

    while (await r.ReadAsync())
    {
        sb.Append("<tr>");
        for (int i = 0; i < r.FieldCount; i++)
            sb.Append("<td>").Append(ParseHelpers.H(r.IsDBNull(i) ? "" : r.GetValue(i)?.ToString())).Append("</td>");
        sb.Append("</tr>");
    }

    sb.Append("</tbody></table></div></section>");

    return Results.Content(Layout("DB", "db", sb.ToString()), "text/html; charset=utf-8");
});

// При старте: обновить представление нагрузки (идемпотентно)
using (var scope = app.Services.CreateScope())
{
    var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    EnsureWorkloadView(ds).GetAwaiter().GetResult();
    EnsureEmploymentTypeColumn(ds).GetAwaiter().GetResult();
}

app.Run();

// Платные (коммерческие) ОП; остальные — бюджетные
static class OpBudgetHelper
{
    public static readonly HashSet<string> CommercialOpNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Цифровые инновации в управлении предприятием",
        "Международный бизнес",
        "Менеджмент в ритейле",
        "Маркетинг-менеджмент",
        "Управление цифровым продуктом",
        "Управление устойчивым развитием компании",
        "Управление людьми: цифровые технологии и организационное развитие",
        "Управление B2C-бизнесом: технологии и инновации",
        "Управление продуктом в ИТ-бизнесе"
    };
    public static string OpBudgetCommercial(string? opName) =>
        !string.IsNullOrWhiteSpace(opName) && CommercialOpNames.Contains(opName.Trim()) ? "Коммерческая" : "Бюджетная";
}

static class SelectOptions
{
    public static readonly string[] EducationLevels = { "Бакалавриат", "Магистратура", "Аспирантура" };
    public static readonly string[] DisciplineKinds = { "обязательная", "по выбору", "факультатив" };
    public static readonly string[] LanguageOptions = { "Русский", "Английский", "Русский/Английский" };

    public static string AcademicYearOptions(string? selected, int fromYear = 2020, int toYear = 2032)
    {
        var sb = new StringBuilder();
        sb.Append("<option value=\"\">Учебный год</option>");
        for (var y = fromYear; y <= toYear; y++)
        {
            var val = $"{y}-{y + 1}";
            sb.Append("<option value=\"").Append(val).Append("\"");
            if (!string.IsNullOrWhiteSpace(selected) && selected == val) sb.Append(" selected");
            sb.Append(">").Append(val).Append("</option>");
        }
        return sb.ToString();
    }

    public static string AcademicYearToMonthValue(string? academicYear)
    {
        if (string.IsNullOrWhiteSpace(academicYear)) return "";
        var m = Regex.Match(academicYear.Trim(), @"^(\d{4})-(\d{4})$");
        return m.Success ? $"{m.Groups[1].Value}-09" : "";
    }
}

record UserInfo(int UserId, string Login, string DisplayName, string Role, string[] AllowedOpNames, int[] AllowedDepartmentIds)
{
    static bool RoleEquals(string? role, string expected) =>
        string.Equals(role?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    public bool IsAdmin => RoleEquals(Role, "Admin");
    public bool IsAcademicDirector => RoleEquals(Role, "AcademicDirector");
    public bool IsDepartmentManager => RoleEquals(Role, "DepartmentManager");
    public bool CanEditPlan => IsAdmin || IsAcademicDirector;
    public bool CanEditWorkload => IsAdmin || IsDepartmentManager;
    public bool CanEditFaculty => IsAdmin || IsDepartmentManager;
    public bool CanEditDisciplines => IsAdmin || IsAcademicDirector;
}

/// <summary>Парсинг Excel: первая строка — заголовки (можно свои), далее данные. Колонки сопоставляются по смыслу.</summary>
static class ExcelImportHelper
{
    /// <param name="columnMap">Ключ — логическое имя поля, значение — варианты заголовков (например "ФИО", "ФИО преподавателя").</param>
    /// <returns>Список строк; каждая строка — словарь логическое_имя → значение ячейки (строка).</returns>
    public static List<Dictionary<string, string>> ParseSheet(Stream excelStream, Dictionary<string, string[]> columnMap)
    {
        var rows = new List<Dictionary<string, string>>();
        using var wb = new XLWorkbook(excelStream);
        var ws = wb.Worksheet(1);
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (lastRow < 2) return rows;

        var headerRow = ws.Row(1);
        var colCount = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var headerToCol = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 1; c <= colCount; c++)
        {
            var v = headerRow.Cell(c).GetString().Trim();
            if (string.IsNullOrEmpty(v)) continue;
            if (!headerToCol.ContainsKey(v)) headerToCol[v] = c;
        }

        var logicalToCol = new Dictionary<string, int>();
        foreach (var (logicalName, possibleHeaders) in columnMap)
        {
            if (possibleHeaders is null) continue;
            foreach (var h in possibleHeaders)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                var key = h.Trim();
                if (headerToCol.TryGetValue(key, out var col))
                {
                    logicalToCol[logicalName] = col;
                    break;
                }
                var match = headerToCol.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    logicalToCol[logicalName] = headerToCol[match];
                    break;
                }
            }
        }

        for (var r = 2; r <= lastRow; r++)
        {
            var row = ws.Row(r);
            var dict = new Dictionary<string, string>();
            foreach (var (logicalName, col) in logicalToCol)
            {
                var cell = row.Cell(col);
                string val;
                if (cell.HasFormula) val = cell.Value.ToString() ?? "";
                else
                {
                    var str = cell.GetString();
                    val = !string.IsNullOrWhiteSpace(str) ? str : (cell.Value.ToString() ?? "");
                }
                dict[logicalName] = (val ?? "").Trim();
            }
            rows.Add(dict);
        }
        return rows;
    }

    public static string? Get(Dictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var v) ? (string.IsNullOrWhiteSpace(v) ? null : v.Trim()) : null;
}

// Expose entry point for WebApplicationFactory in tests
public partial class Program { }

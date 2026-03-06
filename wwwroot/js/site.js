// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Ссылки data-export="excel" не перехватываем — браузер сам скачивает файл по переходу по href
// (программное скачивание после fetch блокируется из‑за потери контекста «жеста пользователя»).

document.addEventListener("click", (event) => {
  const openBtn = event.target.closest("[data-dialog-open]");
  if (openBtn) {
    const selector = openBtn.getAttribute("data-dialog-open");
    const dialog = selector ? document.querySelector(selector) : null;
    if (dialog && typeof dialog.showModal === "function") dialog.showModal();
    return;
  }

  const closeBtn = event.target.closest("[data-dialog-close]");
  if (closeBtn) {
    const dialog = closeBtn.closest("dialog");
    if (dialog && typeof dialog.close === "function") dialog.close();
  }
});

// Учебный год: сентябрь–август. Выбор по календарю (месяц) → значение "YYYY-YYYY".
function academicMonthToYear(monthValue) {
  if (!monthValue || monthValue.length < 7) return "";
  const y = parseInt(monthValue.substring(0, 4), 10);
  const m = parseInt(monthValue.substring(5, 7), 10);
  if (m >= 9) return y + "-" + (y + 1);
  return (y - 1) + "-" + y;
}
function yearToAcademicMonth(yearValue) {
  if (!yearValue || !/^\d{4}-\d{4}$/.test(String(yearValue).trim())) return "";
  return String(yearValue).trim().substring(0, 4) + "-09";
}
document.addEventListener("change", (e) => {
  if (!e.target.matches(".input--academic-month")) return;
  const wrap = e.target.closest(".input-group--academic-year");
  const hidden = wrap ? wrap.querySelector('input[type="hidden"]') : null;
  if (hidden) hidden.value = academicMonthToYear(e.target.value);
});
document.querySelectorAll(".input--academic-month").forEach((monthInput) => {
  const wrap = monthInput.closest(".input-group--academic-year");
  const hidden = wrap ? wrap.querySelector('input[type="hidden"]') : null;
  if (!hidden) return;
  if (!monthInput.value && hidden.value) monthInput.value = yearToAcademicMonth(hidden.value);
});

document.addEventListener("mousedown", (event) => {
  const btn = event.target.closest("[data-pps-add]");
  if (!btn) return;
  event.preventDefault();
  const table = document.querySelector("#pps-table");
  const tbody = table ? table.querySelector("tbody") : null;
  if (!tbody) return;

  const options = document.querySelector("#pps-department-options");
  const optionsHtml = options ? options.innerHTML : "";
  const rowKey = `new-${Date.now()}`;

  const row = document.createElement("tr");
  row.innerHTML = `
    <td><input type="checkbox" class="row-select row-select-pps" name="selectFacultyId" value=""></td>
    <td>новая<input type="hidden" name="rowId" value="${rowKey}"></td>
    <td><input class="input" name="ppsName_${rowKey}" value=""></td>
    <td>
      <select class="input" name="position_${rowKey}">
        <option value="">Должность</option>
        <option value="Профессор">Профессор</option>
        <option value="Доцент">Доцент</option>
        <option value="Старший преподаватель">Старший преподаватель</option>
        <option value="Преподаватель">Преподаватель</option>
      </select>
    </td>
    <td><input class="input" name="rate_${rowKey}" value=""></td>
    <td><input class="input" name="nrdShare_${rowKey}" value=""></td>
    <td>
      <select class="input" name="track_${rowKey}">
        <option value="">Трек</option>
        <option value="Академический">Академический</option>
        <option value="Образовательно-методический">Образовательно-методический</option>
        <option value="Практико-ориентированный">Практико-ориентированный</option>
      </select>
    </td>
    <td>
      <select class="input" name="departmentName_${rowKey}" data-dept-select>${optionsHtml}</select>
      <input class="input input--dept-custom" name="departmentCustom_${rowKey}" value="" placeholder="Департамент (с большой буквы)" style="display:none">
    </td>
    <td>
      <select class="input" name="employmentType_${rowKey}">
        <option value="">Тип</option>
        <option value="штат">штат</option>
        <option value="ГПХ">ГПХ</option>
      </select>
    </td>
    <td><input class="input" name="auditoryLoad_${rowKey}" value=""></td>
    <td><input class="input" name="normHours_${rowKey}" value=""></td>
    <td><input class="input" name="performedLoad_${rowKey}" value=""></td>
    <td><input class="input" name="responsibleLoad_${rowKey}" value=""></td>
    <td><label class="check"><input type="checkbox" name="isActive_${rowKey}">Активен</label></td>
  `;
  tbody.prepend(row);
});

document.addEventListener("mousedown", (event) => {
  const btn = event.target.closest("[data-wl-add]");
  if (!btn) return;
  event.preventDefault();
  const table = document.querySelector("#wl-table");
  const tbody = table ? table.querySelector("tbody") : null;
  if (!tbody) return;

  const workTypeOptions = document.querySelector("#wl-worktype-options");
  const workTypeHtml = workTypeOptions ? workTypeOptions.innerHTML : "";
  const yearOptionsHtml = (document.querySelector("#wl-year-options") || {}).innerHTML || "";
  const opOptionsHtml = (document.querySelector("#wl-op-options") || {}).innerHTML || "";
  const levelOptionsHtml = (document.querySelector("#wl-level-options") || {}).innerHTML || "";
  const deptOptionsHtml = (document.querySelector("#wl-dept-options") || {}).innerHTML || "";
  const rowKey = `new-${Date.now()}`;

  const row = document.createElement("tr");
  row.className = "wl-row wl-row--new";
  row.setAttribute("data-row-key", rowKey);
  row.innerHTML = `
    <td><input type="checkbox" class="row-select row-select-wl" name="selectAssignmentId" value=""></td>
    <td><select class="input" name="year_${rowKey}">${yearOptionsHtml}</select></td>
    <td><input class="input" name="module_${rowKey}" value="" placeholder="Модуль"></td>
    <td><input class="input" name="disciplineNo_${rowKey}" value="" placeholder="№ дисциплины"></td>
    <td><input class="input" name="disciplineName_${rowKey}" value="" placeholder="Дисциплина"></td>
    <td><select class="input" name="opName_${rowKey}">${opOptionsHtml}</select></td>
    <td></td>
    <td><select class="input" name="educationLevel_${rowKey}">${levelOptionsHtml}</select></td>
    <td><select class="input" name="departmentName_${rowKey}">${deptOptionsHtml}</select></td>
    <td class="wl-cell-worktypes">
      <input type="hidden" name="rowId" value="${rowKey}">
      <input type="hidden" name="assignmentId_${rowKey}" value="">
      <input class="input" name="planDisciplineId_${rowKey}" value="" placeholder="ID дисциплины">
      <div class="wl-worktypes-list">
        <div class="wl-worktype-pair">
          <select class="input" name="workTypeId_0_${rowKey}">${workTypeHtml}</select>
          <input class="input" name="hours_0_${rowKey}" value="" placeholder="Часы">
        </div>
      </div>
      <button type="button" class="btn btn--secondary btn--small wl-add-worktype" data-row-key="${rowKey}">+ Вид работ</button>
    </td>
    <td></td>
    <td>
      <input class="input" name="facultyName_${rowKey}" list="wl-faculty-list" value="" placeholder="Преподаватель">
    </td>
  `;
  tbody.prepend(row);
});

document.addEventListener("mousedown", (event) => {
  const btn = event.target.closest(".wl-add-worktype");
  if (!btn) return;
  event.preventDefault();
  const rowKey = btn.getAttribute("data-row-key");
  if (!rowKey) return;
  const row = btn.closest("tr");
  const list = row ? row.querySelector(".wl-worktypes-list") : null;
  const workTypeOptions = document.querySelector("#wl-worktype-options");
  const workTypeHtml = workTypeOptions ? workTypeOptions.innerHTML : "";
  if (!list) return;
  const nextIndex = list.querySelectorAll(".wl-worktype-pair").length;
  const div = document.createElement("div");
  div.className = "wl-worktype-pair";
  div.innerHTML = `
    <select class="input" name="workTypeId_${nextIndex}_${rowKey}">${workTypeHtml}</select>
    <input class="input" name="hours_${nextIndex}_${rowKey}" value="" placeholder="Часы">
  `;
  list.appendChild(div);
});

// Нераспределённая нагрузка по дисциплине = план − совокупная распределённая. Пересчёт при изменении часов.
function recalcUnallocatedWorkload(ev) {
  var target = ev && ev.target;
  if (!target || !target.name || target.name.indexOf("hours_") !== 0) return;
  var table = document.getElementById("wl-table");
  if (!table) return;
  var row = target.closest("tr");
  if (!row || !row.getAttribute("data-plan-discipline-id")) return;
  var pdId = row.getAttribute("data-plan-discipline-id");
  var planTotal = parseFloat(row.getAttribute("data-plan-total")) || 0;
  var initialAllocated = parseFloat(row.getAttribute("data-initial-allocated")) || 0;
  var effectivePlan = planTotal > 0 ? planTotal : initialAllocated;
  var allRows = table.querySelectorAll("tr[data-plan-discipline-id=\"" + pdId + "\"]");
  var totalAllocated = 0;
  for (var i = 0; i < allRows.length; i++) {
    var inputs = allRows[i].querySelectorAll("input[name^=\"hours_\"]");
    for (var j = 0; j < inputs.length; j++) totalAllocated += parseFloat(inputs[j].value) || 0;
  }
  var unallocated = Math.max(0, effectivePlan - totalAllocated);
  var val = (Math.round(unallocated * 100) / 100).toString();
  for (var k = 0; k < allRows.length; k++) {
    var r = allRows[k];
    var cell = r.querySelector("td.cell-unallocated") || (r.cells && r.cells.length > 10 ? r.cells[10] : null);
    if (cell) cell.textContent = val;
  }
}
function attachWorkloadRecalc() {
  var table = document.getElementById("wl-table");
  if (!table) return;
  table.addEventListener("input", recalcUnallocatedWorkload);
  table.addEventListener("change", recalcUnallocatedWorkload);
  table.addEventListener("keyup", recalcUnallocatedWorkload);
  var inputs = table.querySelectorAll("input[name^=\"hours_\"]");
  for (var i = 0; i < inputs.length; i++) inputs[i].dispatchEvent(new Event("input", { bubbles: true }));
}
document.addEventListener("DOMContentLoaded", attachWorkloadRecalc);
document.addEventListener("input", function(ev) {
  if (ev.target && ev.target.name && ev.target.name.indexOf("hours_") === 0) recalcUnallocatedWorkload(ev);
});
document.addEventListener("change", function(ev) {
  if (ev.target && ev.target.name && ev.target.name.indexOf("hours_") === 0) recalcUnallocatedWorkload(ev);
});
document.addEventListener("keyup", function(ev) {
  if (ev.target && ev.target.name && ev.target.name.indexOf("hours_") === 0) recalcUnallocatedWorkload(ev);
});

document.addEventListener("mousedown", (event) => {
  const btn = event.target.closest("[data-disc-add]");
  if (!btn) return;
  event.preventDefault();
  const table = document.querySelector("#disc-batch-form table");
  const tbody = table ? table.querySelector("tbody") : null;
  if (!tbody) return;

  const moduleOptions = document.querySelector("#disc-module-options");
  const deptOptions = document.querySelector("#disc-dept-options");
  const kindOptions = document.querySelector("#disc-disciplinekind-options");
  const langOptions = document.querySelector("#disc-language-options");
  const moduleHtml = moduleOptions ? moduleOptions.innerHTML : "";
  const deptHtml = deptOptions ? deptOptions.innerHTML : "";
  const kindHtml = kindOptions ? kindOptions.innerHTML : "";
  const langHtml = langOptions ? langOptions.innerHTML : "";
  const rowKey = `new-${Date.now()}`;

  const row = document.createElement("tr");
  row.innerHTML = `
    <td>новая<input type="hidden" name="rowId" value="${rowKey}"></td>
    <td><input class="input" name="disciplineNo_${rowKey}" value=""></td>
    <td><input class="input" name="disciplineName_${rowKey}" value=""></td>
    <td><select class="input" name="moduleId_${rowKey}">${moduleHtml}</select></td>
    <td><select class="input" name="departmentId_${rowKey}">${deptHtml}</select></td>
    <td><input class="input" name="courseNo_${rowKey}" value=""></td>
    <td><select class="input" name="disciplineKind_${rowKey}">${kindHtml}</select></td>
    <td><select class="input" name="language_${rowKey}">${langHtml}</select></td>
    <td><input class="input" name="credits_${rowKey}" value=""></td>
  `;
  tbody.prepend(row);
});

document.addEventListener("mousedown", (event) => {
  const btn = event.target.closest("[data-plan-add]");
  if (!btn) return;
  event.preventDefault();
  const template = document.querySelector(".plan-new-template");
  if (!template) {
    alert("Нет доступных образовательных программ для добавления строки. Обратитесь к администратору.");
    return;
  }
  const clone = template.cloneNode(true);
  clone.classList.remove("plan-new-template");
  clone.removeAttribute("style");
  const newId = "plan-new-" + Date.now();
  const form = clone.querySelector("form");
  if (form) {
    form.id = newId;
    clone.querySelectorAll("[form=\"plan-new-template\"]").forEach((el) => { el.setAttribute("form", newId); });
  }
  const tbody = template.closest("tbody");
  if (tbody) tbody.insertBefore(clone, tbody.firstChild);
});

// «Выбрать все»: переключает все чекбоксы строк в таблице
document.addEventListener("change", (event) => {
  const all = event.target.closest("[data-select-all]");
  if (!all || all.type !== "checkbox") return;
  const type = all.getAttribute("data-select-all");
  const table = all.closest("table");
  if (!table) return;
  const rowChecks = table.querySelectorAll(".row-select-" + type);
  rowChecks.forEach((cb) => { cb.checked = all.checked; });
});

// «Удалить выбранные»: отправка выбранных id на batch-delete
document.addEventListener("click", (event) => {
  const btn = event.target.closest("[data-delete-selected]");
  if (!btn) return;
  const type = btn.getAttribute("data-delete-selected");
  const table = document.querySelector(type === "pps" ? "#pps-table" : type === "wl" ? "#wl-table" : "#disc-batch-form table");
  if (!table) return;
  const selector = ".row-select-" + type;
  const checked = Array.from(table.querySelectorAll(selector)).filter((cb) => cb.checked);
  const ids = checked.map((cb) => parseInt(cb.value, 10)).filter((n) => !Number.isNaN(n) && n > 0);
  if (type === "wl") {
    checked.forEach((cb) => {
      const row = cb.closest("tr");
      if (row) row.remove();
    });
  }
  if (ids.length === 0) {
    if (type === "wl") return;
    alert("Выберите хотя бы одну строку.");
    return;
  }
  if (!confirm("Удалить выбранные записи (" + ids.length + ")?")) return;
  const actions = { pps: "/uifaculty/delete-batch", wl: "/uiworkload/delete-batch", disc: "/uidisciplines/delete-batch" };
  const names = { pps: "facultyIds", wl: "assignmentIds", disc: "planDisciplineIds" };
  const form = document.createElement("form");
  form.method = "post";
  form.action = actions[type];
  ids.forEach((id) => {
    const input = document.createElement("input");
    input.type = "hidden";
    input.name = names[type];
    input.value = id;
    form.appendChild(input);
  });
  const token = document.querySelector("input[name='__RequestVerificationToken']");
  if (token) form.appendChild(token.cloneNode(true));
  document.body.appendChild(form);
  form.submit();
});

document.addEventListener("change", (event) => {
  const select = event.target.closest("select[data-dept-select]");
  if (!select) return;
  const customInput = select.parentElement
    ? select.parentElement.querySelector(".input--dept-custom")
    : null;
  if (!customInput) return;
  customInput.style.display = select.value === "Другое" ? "" : "none";
});

document.addEventListener("submit", (event) => {
  const form = event.target.closest("#pps-batch-form");
  if (!form) return;

  const rows = Array.from(form.querySelectorAll("tbody tr"));
  const errors = [];
  rows.forEach((row) => {
    const rowId = row.querySelector("input[name='rowId']")?.value || "";
    const name = row.querySelector(`input[name='ppsName_${rowId}']`)?.value || "";
    if (!name) return;
    const parts = name.trim().split(" ").filter(Boolean);
    if (name.includes(".") || parts.length < 2 || parts.some((p) => p.replace("-", "").length < 2)) {
      errors.push(`ФИО должно быть полностью (строка ${rowId})`);
    }
    const position = row.querySelector(`select[name='position_${rowId}']`)?.value || "";
    if (!position) errors.push(`Должность обязательна (строка ${rowId})`);
    const rate = parseFloat((row.querySelector(`input[name='rate_${rowId}']`)?.value || "").replace(",", "."));
    const nrd = parseFloat((row.querySelector(`input[name='nrdShare_${rowId}']`)?.value || "").replace(",", "."));
    if (Number.isNaN(rate)) errors.push(`Ставка должна быть числом (строка ${rowId})`);
    if (Number.isNaN(nrd)) errors.push(`НРД должна быть числом (строка ${rowId})`);
    if (!Number.isNaN(rate) && !Number.isNaN(nrd) && nrd > rate) {
      errors.push(`НРД не может быть больше ставки (строка ${rowId})`);
    }
    const track = row.querySelector(`select[name='track_${rowId}']`)?.value || "";
    if (!track) errors.push(`Трек обязателен (строка ${rowId})`);
    const dept = row.querySelector(`select[name='departmentName_${rowId}']`)?.value || "";
    const deptCustom = row.querySelector(`input[name='departmentCustom_${rowId}']`)?.value || "";
    if (!dept) {
      errors.push(`Департамент обязателен (строка ${rowId})`);
    } else if (dept === "Другое") {
      if (!deptCustom) errors.push(`Укажите департамент (строка ${rowId})`);
      else if (deptCustom[0] !== deptCustom[0].toUpperCase()) {
        errors.push(`Департамент должен начинаться с большой буквы (строка ${rowId})`);
      }
    }
  });

  if (errors.length > 0) {
    event.preventDefault();
    alert(errors.join("\n"));
  }
});

document.addEventListener("submit", (event) => {
  const form = event.target.closest("#wl-batch-form");
  if (!form) return;

  const errors = [];
  const rows = Array.from(form.querySelectorAll("tbody tr"));
  rows.forEach((row) => {
    const rowId = (row.querySelector("input[name='rowId']")?.value || "").trim();
    if (!rowId) return;
    const assignmentId = (row.querySelector("input[name='assignmentId_" + rowId + "']")?.value || "").trim();
    const planDiscId = (row.querySelector("input[name='planDisciplineId_" + rowId + "']")?.value || "").trim();
    if (!assignmentId && !planDiscId) return;
    const facultyName = (row.querySelector("input[name='facultyName_" + rowId + "']")?.value || "").trim();
    if (!facultyName) {
      errors.push("Укажите преподавателя (строка " + rowId + ")");
      return;
    }
    let hasValidPair = false;
    for (let i = 0; i < 50; i++) {
      const workTypeId = row.querySelector("select[name='workTypeId_" + i + "_" + rowId + "']")?.value || "";
      const hoursVal = (row.querySelector("input[name='hours_" + i + "_" + rowId + "']")?.value || "").replace(",", ".");
      const hours = parseFloat(hoursVal);
      if (!workTypeId && !hoursVal) continue;
      if (!workTypeId) {
        errors.push("Выберите вид работ (строка " + rowId + ", позиция " + (i + 1) + ")");
        continue;
      }
      if (Number.isNaN(hours)) {
        errors.push("Укажите часы (строка " + rowId + ", вид работ " + (i + 1) + ")");
        continue;
      }
      if (hours < 0) {
        errors.push("Часы не могут быть отрицательными (строка " + rowId + ")");
        continue;
      }
      hasValidPair = true;
    }
    if (!hasValidPair) errors.push("Укажите хотя бы один вид работ и часы (строка " + rowId + ")");
  });
  if (errors.length > 0) {
    event.preventDefault();
    alert(errors.join("\n"));
  }
});

document.addEventListener("submit", (event) => {
  const form = event.target.closest("#disc-batch-form");
  if (!form) return;

  const errors = [];
  const rows = Array.from(form.querySelectorAll("tbody tr"));
  rows.forEach((row) => {
    const rowId = (row.querySelector("input[name='rowId']")?.value || "").trim();
    if (!rowId) return;
    const name = (row.querySelector("input[name='disciplineName_" + rowId + "']")?.value || "").trim();
    if (!name) return;
    const credits = parseFloat((row.querySelector("input[name='credits_" + rowId + "']")?.value || "").replace(",", "."));
    const courseNo = parseInt((row.querySelector("input[name='courseNo_" + rowId + "']")?.value || ""), 10);
    if (!Number.isNaN(credits) && credits < 0) errors.push("Зач.ед. не могут быть отрицательными (строка " + rowId + ")");
    if (!Number.isNaN(courseNo) && (courseNo < 1 || courseNo > 6)) errors.push("Курс должен быть от 1 до 6 (строка " + rowId + ")");
  });
  if (errors.length > 0) {
    event.preventDefault();
    alert(errors.join("\n"));
  }
});

document.addEventListener("submit", (event) => {
  const form = event.target;
  if (!form || !form.action || form.action.indexOf("uiplan/update") === -1) return;

  const name = (form.querySelector("input[name='disciplineName']")?.value || "").trim();
  const credits = parseFloat((form.querySelector("input[name='credits']")?.value || "").replace(",", "."));
  const courseNo = parseInt((form.querySelector("input[name='courseNo']")?.value || ""), 10);
  const errors = [];
  if (!name) errors.push("Наименование дисциплины обязательно.");
  if (!Number.isNaN(credits) && credits < 0) errors.push("Зач.ед. не могут быть отрицательными.");
  if (!Number.isNaN(courseNo) && (courseNo < 1 || courseNo > 6)) errors.push("Курс должен быть от 1 до 6.");
  if (errors.length > 0) {
    event.preventDefault();
    alert(errors.join("\n"));
  }
});

const applyTableFilters = (table, filters) => {
  const rows = Array.from(table.querySelectorAll("tbody tr"));
  const values = filters.map((f) => (f.value || "").trim().toLowerCase());

  const cellText = (cell) => {
    if (!cell) return "";
    const inputs = Array.from(cell.querySelectorAll("input, select, textarea"));
    if (inputs.length) {
      return inputs
        .map((el) => {
          if (el.type === "checkbox") return el.checked ? "да" : "нет";
          if (el.tagName === "SELECT") {
            const opt = el.selectedOptions && el.selectedOptions[0];
            return (opt ? opt.textContent : el.value) || "";
          }
          return el.value || "";
        })
        .join(" ")
        .toLowerCase();
    }
    return (cell.textContent || "").toLowerCase();
  };

  rows.forEach((row) => {
    let visible = true;
    values.forEach((val, idx) => {
      if (!val || !visible) return;
      const cell = row.children[idx];
      const text = cellText(cell);
      if (!text.includes(val)) visible = false;
    });
    row.style.display = visible ? "" : "none";
  });
};

document.querySelectorAll("[data-filter-table]").forEach((bar) => {
  const selector = bar.getAttribute("data-filter-table");
  const table = selector ? document.querySelector(selector) : null;
  if (!table) return;

  const getFilters = () =>
    Array.from(bar.querySelectorAll("input[data-filter-col]"));

  bar.addEventListener("input", (event) => {
    if (!event.target.matches("input[data-filter-col]")) return;
    applyTableFilters(table, getFilters());
  });
});

document.addEventListener("click", (event) => {
  const btn = event.target.closest("[data-filter-clear]");
  if (!btn) return;
  const selector = btn.getAttribute("data-filter-clear");
  const table = selector ? document.querySelector(selector) : null;
  if (!table) return;

  const bar = selector
    ? document.querySelector(`[data-filter-table='${selector}']`)
    : null;
  const filters = bar
    ? Array.from(bar.querySelectorAll("input[data-filter-col]"))
    : [];

  filters.forEach((input) => {
    input.value = "";
  });
  applyTableFilters(table, filters);
});

// Горизонтальная полоса прокрутки сверху для всех таблиц
function initScrollTopForTables() {
  document.querySelectorAll(".table-wrap").forEach((wrap) => {
    const wrapper = wrap.closest(".table-scroll-top");
    let bar, barInner;

    if (wrapper) {
      bar = wrapper.querySelector(".table-scroll-bar");
      barInner = bar ? bar.querySelector(".table-scroll-bar__inner") : null;
      if (!bar || !barInner) return;
    } else {
      const newWrapper = document.createElement("div");
      newWrapper.className = "table-scroll-top";
      bar = document.createElement("div");
      bar.className = "table-scroll-bar";
      bar.setAttribute("role", "scrollbar");
      bar.setAttribute("aria-orientation", "horizontal");
      barInner = document.createElement("div");
      barInner.className = "table-scroll-bar__inner";
      bar.appendChild(barInner);

      wrap.parentNode.insertBefore(newWrapper, wrap);
      newWrapper.appendChild(bar);
      newWrapper.appendChild(wrap);
    }

    function syncWidth() {
      const w = wrap.scrollWidth;
      barInner.style.width = w + "px";
      // Полосу всегда показываем; при узкой таблице скролл просто не активен
      bar.style.display = "";
    }
    function barToWrap() {
      wrap.scrollLeft = bar.scrollLeft;
    }
    function wrapToBar() {
      bar.scrollLeft = wrap.scrollLeft;
    }

    syncWidth();
    bar.addEventListener("scroll", barToWrap);
    wrap.addEventListener("scroll", wrapToBar);

    const ro = new ResizeObserver(syncWidth);
    ro.observe(wrap);

    // Пересчёт после полной загрузки и с задержкой (таблица может ещё не иметь итоговой ширины)
    window.addEventListener("load", syncWidth);
    setTimeout(syncWidth, 100);
    requestAnimationFrame(syncWidth);
  });
}
if (document.readyState === "loading") {
  document.addEventListener("DOMContentLoaded", initScrollTopForTables);
} else {
  initScrollTopForTables();
}

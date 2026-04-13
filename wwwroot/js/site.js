// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Ссылки data-export="excel" не перехватываем — браузер сам скачивает файл по переходу по href
// (программное скачивание после fetch блокируется из‑за потери контекста «жеста пользователя»).

// ===== CSRF PROTECTION =====
// Читаем токен из <meta name="csrf-token"> и автоматически вставляем
// в каждый POST-запрос как скрытое поле __RequestVerificationToken.
(function() {
  var meta = document.querySelector('meta[name="csrf-token"]');
  if (!meta) return;
  var token = meta.getAttribute("content");
  if (!token) return;
  document.addEventListener("submit", function(e) {
    var form = e.target;
    if (!form || form.method.toLowerCase() !== "post") return;
    if (form.querySelector('input[name="__RequestVerificationToken"]')) return;
    var input = document.createElement("input");
    input.type = "hidden";
    input.name = "__RequestVerificationToken";
    input.value = token;
    form.appendChild(input);
  }, true); // capture-фаза: запускаем до других обработчиков submit
})();

// ===== MOBILE SIDEBAR TOGGLE =====
(function() {
  var toggle = document.getElementById("sidebar-toggle");
  var sidebar = document.getElementById("sidebar");
  if (toggle && sidebar) {
    toggle.addEventListener("click", function(e) {
      e.stopPropagation();
      sidebar.classList.toggle("is-open");
    });
    document.addEventListener("click", function(e) {
      if (sidebar.classList.contains("is-open") && !sidebar.contains(e.target)) {
        sidebar.classList.remove("is-open");
      }
    });
  }
})();

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

function showErrorDialog(message, title) {
  const modalId = "app-error-modal";
  let dialog = document.getElementById(modalId);
  if (!dialog) {
    dialog = document.createElement("dialog");
    dialog.id = modalId;
    dialog.className = "err-dialog";
    dialog.innerHTML = `
      <div class="err-dialog__top">
        <div class="err-dialog__icon" aria-hidden="true">
          <svg viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
            <circle cx="12" cy="12" r="11" fill="#d32f2f"/>
            <path d="M12 6v7" stroke="#fff" stroke-width="2.2" stroke-linecap="round"/>
            <circle cx="12" cy="17" r="1.2" fill="#fff"/>
          </svg>
        </div>
        <div class="err-dialog__right">
          <div class="err-dialog__title"></div>
          <div class="err-dialog__body"></div>
        </div>
      </div>
      <div class="err-dialog__footer">
        <button class="err-dialog__ok" type="button">ОК</button>
      </div>
    `;
    dialog.querySelector(".err-dialog__ok").addEventListener("click", function() {
      dialog.close();
    });
    dialog.addEventListener("click", function(e) {
      if (e.target === dialog) dialog.close();
    });
    document.body.appendChild(dialog);
  }
  const titleEl = dialog.querySelector(".err-dialog__title");
  const bodyEl = dialog.querySelector(".err-dialog__body");
  if (titleEl) titleEl.textContent = title || "Ошибка";
  if (bodyEl) bodyEl.innerHTML = String(message || "")
    .split("\n")
    .filter(function(x) { return x.trim(); })
    .map(function(line) { return "<p>" + line.replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;") + "</p>"; })
    .join("");
  if (typeof dialog.showModal === "function") dialog.showModal();
  var okBtn = dialog.querySelector(".err-dialog__ok");
  if (okBtn) okBtn.focus();
}
window.__showErrorDialog = showErrorDialog;

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
  const moduleOptionsHtml = (document.querySelector("#wl-module-options") || {}).innerHTML || "";
  const opOptionsHtml = (document.querySelector("#wl-op-options") || {}).innerHTML || "";
  const levelOptionsHtml = (document.querySelector("#wl-level-options") || {}).innerHTML || "";
  const deptOptionsHtml = (document.querySelector("#wl-dept-options") || {}).innerHTML || "";
  const facultyOptionsHtml = (document.querySelector("#wl-faculty-options") || {}).innerHTML || "";
  const rowKey = `new-${Date.now()}`;

  const row = document.createElement("tr");
  row.className = "wl-row wl-row--new";
  row.setAttribute("data-row-key", rowKey);
  row.innerHTML = `
    <td><input type="checkbox" class="row-select row-select-wl" name="selectAssignmentId" value=""></td>
    <td><select class="input" name="year_${rowKey}">${yearOptionsHtml}</select></td>
    <td><select class="input" name="module_${rowKey}">${moduleOptionsHtml}</select></td>
    <td><input class="input" name="disciplineNo_${rowKey}" value="" placeholder="№ дисциплины" pattern="[0-9]*" title="Только цифры"></td>
    <td><input class="input" name="disciplineName_${rowKey}" value="" placeholder="Дисциплина"></td>
    <td>
      <select class="input" name="opName_${rowKey}" data-другое-select>${opOptionsHtml}</select>
      <input class="input input--другое-custom" name="opNameCustom_${rowKey}" value="" placeholder="Название ОП" style="display:none">
    </td>
    <td class="cell-budget">Бюджетная</td>
    <td><select class="input" name="educationLevel_${rowKey}">${levelOptionsHtml}</select></td>
    <td>
      <select class="input" name="departmentName_${rowKey}" data-другое-select>${deptOptionsHtml}</select>
      <input class="input input--другое-custom" name="departmentNameCustom_${rowKey}" value="" placeholder="Департамент" style="display:none">
    </td>
    <td class="cell-groups"><span class="hint" title="Появится после привязки к строке учебного плана">—</span></td>
    <td>
      <input type="hidden" name="rowId" value="${rowKey}">
      <input type="hidden" name="assignmentId_${rowKey}" value="">
      <input type="hidden" name="planDisciplineId_${rowKey}" value="">
      <select class="input" name="workTypeId_${rowKey}">${workTypeHtml}</select>
    </td>
    <td><input class="input" name="hours_${rowKey}" value="" placeholder="Часы"></td>
    <td></td>
    <td>
      <select class="input" name="facultyId_${rowKey}" data-searchable>${facultyOptionsHtml}</select>
    </td>
  `;
  tbody.prepend(row);
});

// (removed: wl-add-worktype handler — each row now has exactly one work type)

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
  var diff = effectivePlan - totalAllocated;
  var val = diff >= 0
    ? (Math.round(diff * 100) / 100).toString()
    : "+" + (Math.round(-diff * 100) / 100).toString();
  for (var k = 0; k < allRows.length; k++) {
    var r = allRows[k];
    var cell = r.querySelector("td.cell-unallocated") || (r.cells && r.cells.length >= 2 ? r.cells[r.cells.length - 2] : null);
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

  const deptOptions = document.querySelector("#disc-dept-options");
  const kindOptions = document.querySelector("#disc-disciplinekind-options");
  const langOptions = document.querySelector("#disc-language-options");
  const deptHtml = deptOptions ? deptOptions.innerHTML : "";
  const kindHtml = kindOptions ? kindOptions.innerHTML : "";
  const langHtml = langOptions ? langOptions.innerHTML : "";
  const rowKey = `new-${Date.now()}`;

  const row = document.createElement("tr");
  row.innerHTML = `
    <td>новая<input type="hidden" name="rowId" value="${rowKey}"></td>
    <td><input class="input" name="disciplineNo_${rowKey}" value="" pattern="[0-9]*" title="Только цифры"></td>
    <td><input class="input" name="disciplineName_${rowKey}" value=""></td>
    <td><div class="module-multiselect" data-module-ms><div class="module-ms__display" tabindex="0">Модуль</div><div class="module-ms__dropdown"><label class="module-ms__item"><input type="checkbox" name="moduleNums_${rowKey}" value="1"> 1 модуль</label><label class="module-ms__item"><input type="checkbox" name="moduleNums_${rowKey}" value="2"> 2 модуль</label><label class="module-ms__item"><input type="checkbox" name="moduleNums_${rowKey}" value="3"> 3 модуль</label><label class="module-ms__item"><input type="checkbox" name="moduleNums_${rowKey}" value="4"> 4 модуль</label></div></div></td>
    <td>
      <select class="input" name="departmentId_${rowKey}" data-другое-select>${deptHtml}</select>
      <input class="input input--другое-custom" name="departmentNameCustom_${rowKey}" value="" placeholder="Департамент" style="display:none">
    </td>
    <td><input class="input" name="courseNo_${rowKey}" value=""></td>
    <td><select class="input" name="disciplineKind_${rowKey}">${kindHtml}</select></td>
    <td><select class="input" name="language_${rowKey}">${langHtml}</select></td>
    <td><input class="input" name="credits_${rowKey}" value=""></td>
    <td class="hint">—</td>
  `;
  tbody.prepend(row);
});

document.addEventListener("mousedown", (event) => {
  const btn = event.target.closest("[data-plan-add]");
  if (!btn) return;
  event.preventDefault();
  const template = document.querySelector(".plan-new-template");
  if (!template) {
    showErrorDialog("Нет доступных образовательных программ для добавления строки. Обратитесь к администратору.");
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
  rowChecks.forEach((cb) => {
    cb.checked = all.checked;
    var row = cb.closest("tr");
    if (row) row.classList.toggle("row-checked", cb.checked);
  });
});

document.addEventListener("change", (event) => {
  var cb = event.target;
  if (!cb.classList || !cb.classList.contains("row-select")) return;
  var row = cb.closest("tr");
  if (row) row.classList.toggle("row-checked", cb.checked);
});

// «Удалить выбранные»: отправка выбранных id на batch-delete
document.addEventListener("click", (event) => {
  const btn = event.target.closest("[data-delete-selected]");
  if (!btn) return;
  const type = btn.getAttribute("data-delete-selected");
  const table = document.querySelector(type === "pps" ? "#pps-table" : type === "wl" ? "#wl-table" : type === "plan" ? "#plan-table" : "#disc-batch-form table");
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
    showErrorDialog("Выберите хотя бы одну строку.");
    return;
  }
  if (!confirm("Удалить выбранные записи (" + ids.length + ")?")) return;
  const actions = { pps: "/uifaculty/delete-batch", wl: "/uiworkload/delete-batch", disc: "/uidisciplines/delete-batch", plan: "/uiplan/delete-batch" };
  const names = { pps: "facultyIds", wl: "assignmentIds", disc: "planDisciplineIds", plan: "planDisciplineIds" };
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
  const select = event.target.closest("select[data-другое-select]");
  if (!select) return;
  const customInput = select.parentElement
    ? select.parentElement.querySelector(".input--другое-custom")
    : null;
  if (!customInput) return;
  const isOther = select.value === "Другое" || select.value === "-1";
  customInput.style.display = isOther ? "" : "none";
  if (!isOther) customInput.value = "";
});

// backward compat: data-dept-select still triggers same logic
document.addEventListener("change", (event) => {
  const select = event.target.closest("select[data-dept-select]");
  if (!select) return;
  const customInput = select.parentElement
    ? select.parentElement.querySelector(".input--dept-custom")
    : null;
  if (!customInput) return;
  customInput.style.display = select.value === "Другое" ? "" : "none";
});

function updateTrackForRow(row) {
  if (!row) return;
  const rowId = (row.querySelector("input[name='rowId']") || {}).value || "";
  const empSel = row.querySelector("select[name='employmentType_" + rowId + "']");
  const trackSel = row.querySelector("select[name='track_" + rowId + "']");
  if (!empSel || !trackSel) return;
  const isGph = empSel.value === "ГПХ";
  trackSel.disabled = isGph;
  trackSel.style.opacity = isGph ? "0.4" : "";
  trackSel.style.pointerEvents = isGph ? "none" : "";
  trackSel.title = isGph ? "Трек не применяется для ГПХ" : "";
}

document.addEventListener("change", (event) => {
  const empSel = event.target.closest("select[name^='employmentType_']");
  if (!empSel) return;
  const row = empSel.closest("tr");
  updateTrackForRow(row);
});

document.addEventListener("DOMContentLoaded", function() {
  const table = document.getElementById("pps-table");
  if (!table) return;
  Array.from(table.querySelectorAll("tbody tr")).forEach(updateTrackForRow);
});

const _ppsTableObserver = new MutationObserver(function(mutations) {
  mutations.forEach(function(m) {
    m.addedNodes.forEach(function(node) {
      if (node.nodeType === 1 && node.tagName === "TR") updateTrackForRow(node);
    });
  });
});
(function() {
  var tbl = document.getElementById("pps-table");
  var tbody = tbl ? tbl.querySelector("tbody") : null;
  if (tbody) _ppsTableObserver.observe(tbody, { childList: true });
})();

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
    const empType = row.querySelector(`select[name='employmentType_${rowId}']`)?.value || "";
    const track = row.querySelector(`select[name='track_${rowId}']`)?.value || "";
    if (empType !== "ГПХ" && !track) errors.push(`Трек обязателен (строка ${rowId})`);
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
    showErrorDialog(errors.join("\n"));
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
    const facultyId = (row.querySelector("select[name='facultyId_" + rowId + "']")?.value || "").trim();
    if (!facultyId) {
      errors.push("Укажите преподавателя (строка " + rowId + ")");
      return;
    }
    const workTypeId = (row.querySelector("select[name='workTypeId_" + rowId + "']")?.value || "").trim();
    const hoursRaw = (row.querySelector("input[name='hours_" + rowId + "']")?.value || "").replace(",", ".");
    const hours = parseFloat(hoursRaw);
    if (!workTypeId) {
      errors.push("Выберите вид работ (строка " + rowId + ")");
    } else if (!hoursRaw) {
      errors.push("Укажите часы (строка " + rowId + ")");
    } else if (Number.isNaN(hours)) {
      errors.push("Некорректное значение часов (строка " + rowId + ")");
    } else if (hours < 0) {
      errors.push("Часы не могут быть отрицательными (строка " + rowId + ")");
    }
  });
  if (errors.length > 0) {
    event.preventDefault();
    showErrorDialog(errors.join("\n"));
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
    showErrorDialog(errors.join("\n"));
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
    showErrorDialog(errors.join("\n"));
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

// ===== SEARCHABLE SELECT (for faculty and other long lists) =====
(function() {
  function initSearchableSelect(originalSelect) {
    if (originalSelect.dataset.searchableInit) return;
    originalSelect.dataset.searchableInit = "1";
    originalSelect.style.display = "none";

    const wrap = document.createElement("div");
    wrap.className = "searchable-select-wrap";
    originalSelect.parentNode.insertBefore(wrap, originalSelect);
    wrap.appendChild(originalSelect);

    const display = document.createElement("div");
    display.className = "searchable-select-display";
    display.textContent = (originalSelect.options[originalSelect.selectedIndex]?.text || "Выберите...").trim();
    wrap.insertBefore(display, originalSelect);

    let dropdown = null;

    function open() {
      if (dropdown) return;
      dropdown = document.createElement("div");
      dropdown.className = "searchable-select-dropdown";

      const searchBox = document.createElement("div");
      searchBox.className = "searchable-select-search";
      const searchInput = document.createElement("input");
      searchInput.type = "text";
      searchInput.placeholder = "Поиск...";
      searchBox.appendChild(searchInput);
      dropdown.appendChild(searchBox);

      const optList = document.createElement("div");
      optList.className = "searchable-select-options";
      dropdown.appendChild(optList);

      const options = Array.from(originalSelect.options);

      function render(filter) {
        optList.innerHTML = "";
        const q = (filter || "").toLowerCase();
        options.forEach(opt => {
          const text = opt.text.trim();
          if (q && !text.toLowerCase().includes(q)) return;
          const div = document.createElement("div");
          div.className = "searchable-select-option";
          if (opt.value === originalSelect.value) div.classList.add("is-selected");
          div.textContent = text;
          div.addEventListener("mousedown", function(e) {
            e.preventDefault();
            originalSelect.value = opt.value;
            originalSelect.dispatchEvent(new Event("change", { bubbles: true }));
            display.textContent = text || "Выберите...";
            close();
          });
          optList.appendChild(div);
        });
      }

      render("");
      wrap.appendChild(dropdown);
      searchInput.focus();

      searchInput.addEventListener("input", () => render(searchInput.value));
      searchInput.addEventListener("keydown", (e) => {
        if (e.key === "Escape") close();
      });
    }

    function close() {
      if (dropdown) { dropdown.remove(); dropdown = null; }
    }

    display.addEventListener("click", (e) => {
      e.stopPropagation();
      if (dropdown) close(); else open();
    });

    document.addEventListener("click", (e) => {
      if (dropdown && !wrap.contains(e.target)) close();
    });
  }

  function initAll() {
    document.querySelectorAll("select[data-searchable]").forEach(initSearchableSelect);
  }

  initAll();
  const observer = new MutationObserver(initAll);
  observer.observe(document.body, { childList: true, subtree: true });
})();

(function() {
  var commercialOpsEl = document.getElementById("wl-commercial-ops");
  if (commercialOpsEl) {
    var commercialOps = {};
    try { commercialOps = JSON.parse(commercialOpsEl.textContent || "{}"); } catch(e) {}

    function getBudgetLabel(opName) {
      return commercialOps[opName] ? "Коммерческая" : "Бюджетная";
    }

    function updateBudgetCell(selectEl) {
      var row = selectEl.closest("tr");
      if (!row) return;
      var budgetCell = row.querySelector("td.cell-budget");
      if (budgetCell) budgetCell.textContent = getBudgetLabel(selectEl.value);
    }

    document.addEventListener("change", function(ev) {
      if (ev.target && ev.target.name && ev.target.name.indexOf("opName_") === 0)
        updateBudgetCell(ev.target);
    });
  }

  // ===== EXCEL-STYLE TABLE FILTERING =====
  const activeFilters = new Map(); // Map<table, Map<colIndex, Set<values>>>

  document.addEventListener("click", function(e) {
    // Open filter menu
    if (e.target.closest(".btn-filter-excel")) {
      const btn = e.target.closest(".btn-filter-excel");
      const th = btn.closest("th");
      const table = btn.closest("table");
      if (table && th) {
        closeAllMenus();
        showFilterMenu(btn, th, table);
        e.stopPropagation();
      }
    } 
    // Click outside to close
    else if (!e.target.closest(".excel-filter-menu")) {
      closeAllMenus();
    }
  });

  function closeAllMenus() {
    document.querySelectorAll(".excel-filter-menu").forEach(m => m.remove());
  }

  function getCellValue(cell) {
    if (!cell) return "";
    const select = cell.querySelector("select");
    const input = cell.querySelector("input:not([type=hidden])");
    if (select) return (select.options[select.selectedIndex]?.text || "").trim();
    if (input) return (input.value || "").trim();
    return (cell.textContent || "").trim();
  }

  function showFilterMenu(btn, th, table) {
    const colIndex = th.cellIndex;
    const tbody = table.querySelector("tbody");
    if (!tbody) return;
    
    // 1. Collect unique values
    const rows = Array.from(tbody.rows);
    const uniqueValues = new Set();
    rows.forEach(row => {
      // Only collect values from rows that are visible by OTHER filters? 
      // Excel shows ALL values present in the column usually, but respects applied filters.
      // For simplicity and correctness: collect ALL values in the column.
      const val = getCellValue(row.cells[colIndex]);
      if (val) uniqueValues.add(val);
    });
    const sortedValues = Array.from(uniqueValues).sort();

    // 2. Get current state
    let tableFilters = activeFilters.get(table);
    if (!tableFilters) {
        tableFilters = new Map();
        activeFilters.set(table, tableFilters);
    }
    const currentSelection = tableFilters.get(colIndex); // Set of allowed values. If undefined -> all allowed.

    // 3. Build Menu DOM
    const menu = document.createElement("div");
    menu.className = "excel-filter-menu";
    
    // Position
    const rect = btn.getBoundingClientRect();
    const tableRect = table.getBoundingClientRect();
    
    // Container for checkboxes
    const list = document.createElement("div");
    list.className = "excel-filter-list";

    // "Select All" option
    const allItem = document.createElement("label");
    allItem.className = "excel-filter-item";
    const allCheck = document.createElement("input");
    allCheck.type = "checkbox";
    allCheck.checked = !currentSelection; // If no filter set, All is checked
    allItem.appendChild(allCheck);
    allItem.appendChild(document.createTextNode("(Выделить все)"));
    list.appendChild(allItem);

    const checkMap = new Map(); // value -> checkbox

    sortedValues.forEach(val => {
      const item = document.createElement("label");
      item.className = "excel-filter-item";
      const check = document.createElement("input");
      check.type = "checkbox";
      check.value = val;
      // If no filter (currentSelection undefined), checked. If filter exists, check if present.
      check.checked = !currentSelection || currentSelection.has(val); 
      item.appendChild(check);
      item.appendChild(document.createTextNode(val));
      list.appendChild(item);
      checkMap.set(val, check);
    });

    menu.appendChild(list);

    // Actions
    const actions = document.createElement("div");
    actions.className = "excel-filter-actions";
    
    const applyBtn = document.createElement("button");
    applyBtn.type = "button";
    applyBtn.className = "btn btn--small";
    applyBtn.textContent = "OK";
    
    const clearBtn = document.createElement("button");
    clearBtn.type = "button";
    clearBtn.className = "btn btn--ghost btn--small";
    clearBtn.textContent = "Сброс";

    actions.appendChild(applyBtn);
    actions.appendChild(clearBtn);
    menu.appendChild(actions);

    // Event Handlers
    allCheck.addEventListener("change", () => {
      const checked = allCheck.checked;
      checkMap.forEach(c => c.checked = checked);
    });

    clearBtn.addEventListener("click", () => {
        tableFilters.delete(colIndex);
        btn.classList.remove("active");
        applyTableFilters(table);
        closeAllMenus();
    });

    applyBtn.addEventListener("click", () => {
        // Collect checked values
        const selected = new Set();
        let allSelected = allCheck.checked; // simple optimization check
        
        // If "Select All" was unchecked but users manually checked everything, treating it as "All" is safer?
        // Excel logic: strict set of allowed values.
        
        let checkedCount = 0;
        checkMap.forEach((c, val) => {
            if (c.checked) {
                selected.add(val);
                checkedCount++;
            }
        });

        // If everything checked -> remove filter
        if (checkedCount === sortedValues.length && allCheck.checked) {
            tableFilters.delete(colIndex);
            btn.classList.remove("active");
        } else {
            tableFilters.set(colIndex, selected);
            btn.classList.add("active");
        }
        
        applyTableFilters(table);
        closeAllMenus();
    });

    // Positioning logic (simplified)
    document.body.appendChild(menu);
    menu.style.top = (window.scrollY + rect.bottom + 4) + "px";
    menu.style.left = (window.scrollX + rect.left) + "px";
  }

  function applyTableFilters(table) {
    const tableFilters = activeFilters.get(table);
    if (!tableFilters || tableFilters.size === 0) {
        // Show all
        Array.from(table.querySelector("tbody").rows).forEach(r => r.style.display = "");
        return;
    }

    const rows = Array.from(table.querySelector("tbody").rows);
    rows.forEach(row => {
        let visible = true;
        // Check ALL active filters (AND logic)
        for (const [colIndex, allowedSet] of tableFilters.entries()) {
            const cell = row.cells[colIndex];
            const val = getCellValue(cell);
            // In Excel, empty matches "(Blanks)". We simplify: exact match.
            // If value is NOT in allowedSet, hide.
            if (!allowedSet.has(val)) {
                visible = false;
                break;
            }
        }
        row.style.display = visible ? "" : "none";
    });
  }
})();

// ===== MODULE MULTI-SELECT =====
(function() {
  function updateLabel(ms) {
    var checks = ms.querySelectorAll('input[type="checkbox"]');
    var sel = [];
    checks.forEach(function(c) { if (c.checked) sel.push(c.value + " модуль"); });
    var display = ms.querySelector(".module-ms__display");
    if (display) display.textContent = sel.length ? sel.join(", ") : "Модуль";
  }

  document.addEventListener("click", function(e) {
    var display = e.target.closest(".module-ms__display");
    if (display) {
      var ms = display.closest("[data-module-ms]");
      if (ms) {
        var wasOpen = ms.classList.contains("is-open");
        document.querySelectorAll("[data-module-ms].is-open").forEach(function(m) { m.classList.remove("is-open"); });
        if (!wasOpen) ms.classList.add("is-open");
      }
      return;
    }
    if (!e.target.closest(".module-ms__dropdown")) {
      document.querySelectorAll("[data-module-ms].is-open").forEach(function(m) { m.classList.remove("is-open"); });
    }
  });

  document.addEventListener("change", function(e) {
    var ms = e.target.closest("[data-module-ms]");
    if (ms && e.target.type === "checkbox") updateLabel(ms);
  });
})();

// ===== FEATURE 1: TOAST NOTIFICATION =====
(function() {
  function showToast(message, isError) {
    var el = document.createElement("div");
    el.className = "toast" + (isError ? " toast--error" : "");
    el.textContent = message;
    document.body.appendChild(el);
    setTimeout(function() {
      el.classList.add("toast--out");
      setTimeout(function() { el.remove(); }, 300);
    }, 3000);
  }
  var params = new URLSearchParams(window.location.search);
  if (params.get("saved") === "1") {
    showToast("\u2714 \u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u043e!");
    var url = new URL(window.location);
    url.searchParams.delete("saved");
    history.replaceState(null, "", url.pathname + url.search);
  }
  if (params.get("deleted") === "1") {
    showToast("\u2714 \u0423\u0434\u0430\u043b\u0435\u043d\u043e!");
    var url2 = new URL(window.location);
    url2.searchParams.delete("deleted");
    history.replaceState(null, "", url2.pathname + url2.search);
  }
  if (params.get("passwordChanged") === "1") {
    showToast("\u2714 \u041f\u0430\u0440\u043e\u043b\u044c \u0443\u0441\u043f\u0435\u0448\u043d\u043e \u0438\u0437\u043c\u0435\u043d\u0451\u043d!");
    var url3 = new URL(window.location);
    url3.searchParams.delete("passwordChanged");
    history.replaceState(null, "", url3.pathname + url3.search);
  }
  window.__showToast = showToast;
})();

// ===== FEATURE 2: SELECTED ROWS COUNTER =====
(function() {
  function updateCounters() {
    document.querySelectorAll("[data-delete-selected]").forEach(function(btn) {
      var type = btn.getAttribute("data-delete-selected");
      var tableEl = document.querySelector(
        type === "pps" ? "#pps-table" : type === "wl" ? "#wl-table" : type === "plan" ? "#plan-table" : "#disc-batch-form table"
      );
      if (!tableEl) return;
      var total = tableEl.querySelectorAll(".row-select-" + type).length;
      var checked = tableEl.querySelectorAll(".row-select-" + type + ":checked").length;
      var counter = btn.parentElement.querySelector(".selected-count");
      if (!counter) {
        counter = document.createElement("span");
        counter.className = "selected-count";
        btn.insertAdjacentElement("afterend", counter);
      }
      counter.textContent = checked > 0 ? "\u0412\u044b\u0431\u0440\u0430\u043d\u043e: " + checked + " \u0438\u0437 " + total : "";
    });
  }
  document.addEventListener("change", function(e) {
    if (e.target.classList.contains("row-select") || e.target.hasAttribute("data-select-all")) {
      setTimeout(updateCounters, 0);
    }
  });
  document.addEventListener("DOMContentLoaded", updateCounters);
})();

// ===== FEATURE 4: UNSAVED CHANGES WARNING =====
(function() {
  var dirty = false;
  var submitting = false;
  function markDirty() { dirty = true; }
  document.addEventListener("input", function(e) {
    if (e.target.closest(".table") || e.target.closest(".toolbar")) markDirty();
  });
  document.addEventListener("change", function(e) {
    var t = e.target;
    if (t.classList.contains("row-select") || t.hasAttribute("data-select-all")) return;
    if (t.closest(".table") || t.closest(".toolbar")) markDirty();
  });
  document.addEventListener("submit", function() { submitting = true; });
  document.addEventListener("click", function(e) {
    if (e.target.closest("form[method='post']") && e.target.type === "submit") submitting = true;
  });
  window.addEventListener("beforeunload", function(e) {
    if (dirty && !submitting) {
      e.preventDefault();
      e.returnValue = "";
    }
  });
  window.__markSubmitting = function() { submitting = true; };
})();

// ===== FEATURE 5: COLLAPSIBLE FILTERS =====
(function() {
  document.querySelectorAll(".filter-bar").forEach(function(bar) {
    var toolbar = bar.closest(".toolbar") || bar.previousElementSibling;
    var btn = document.createElement("button");
    btn.type = "button";
    btn.className = "filter-toggle";
    btn.innerHTML = '<svg width="12" height="12" viewBox="0 0 24 24" fill="currentColor"><path d="M7 10l5 5 5-5z"/></svg> \u0424\u0438\u043b\u044c\u0442\u0440\u044b';
    btn.addEventListener("click", function() {
      bar.classList.toggle("is-collapsed");
      btn.classList.toggle("is-collapsed");
    });
    bar.parentNode.insertBefore(btn, bar);
  });
})();

// ===== FEATURE 6: TABLE ROW COUNTER =====
(function() {
  function updateRowCounts() {
    document.querySelectorAll(".table").forEach(function(table) {
      var tbody = table.querySelector("tbody");
      if (!tbody) return;
      var allRows = tbody.querySelectorAll("tr:not(.plan-new-template)");
      var visible = 0;
      allRows.forEach(function(r) { if (r.style.display !== "none") visible++; });
      var total = allRows.length;
      var existing = table.parentElement.querySelector(".table-row-count");
      if (!existing) {
        existing = document.createElement("div");
        existing.className = "table-row-count";
        table.parentElement.appendChild(existing);
      }
      if (visible < total) {
        existing.textContent = "\u041e\u0442\u043e\u0431\u0440\u0430\u0436\u0435\u043d\u043e: " + visible + " \u0438\u0437 " + total;
      } else {
        existing.textContent = "\u0412\u0441\u0435\u0433\u043e \u0441\u0442\u0440\u043e\u043a: " + total;
      }
    });
  }
  var observer = new MutationObserver(function() { setTimeout(updateRowCounts, 50); });
  document.querySelectorAll(".table tbody").forEach(function(tbody) {
    observer.observe(tbody, { childList: true, subtree: true, attributes: true, attributeFilter: ["style"] });
  });
  document.addEventListener("DOMContentLoaded", updateRowCounts);
  setTimeout(updateRowCounts, 500);
})();

// ===== FEATURE 7: DOUBLE-CLICK TO EDIT READONLY =====
(function() {
  document.addEventListener("dblclick", function(e) {
    var input = e.target.closest("input.input--readonly");
    if (!input) return;
    input.readOnly = false;
    input.classList.remove("input--readonly");
    input.focus();
    input.select();
    function revert() {
      input.readOnly = true;
      input.classList.add("input--readonly");
      input.removeEventListener("blur", revert);
    }
    input.addEventListener("blur", revert);
  });
})();

// ===== FEATURE 8: PLAN SAVE ALL =====
(function() {
  document.addEventListener("click", function(e) {
    var btn = e.target.closest("[data-plan-save-all]");
    if (!btn) return;
    e.preventDefault();
    var planForms = Array.from(document.querySelectorAll("form[action='/uiplan/update']"));
    if (planForms.length === 0) {
      window.__showToast && window.__showToast("\u2714 \u041d\u0435\u0447\u0435\u0433\u043e \u0441\u043e\u0445\u0440\u0430\u043d\u044f\u0442\u044c");
      return;
    }
    btn.disabled = true;
    var origHtml = btn.innerHTML;
    btn.textContent = "\u0421\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u0435...";
    var csrfToken = (document.querySelector("meta[name='csrf-token']") || {}).getAttribute
      ? document.querySelector("meta[name='csrf-token']").getAttribute("content") || ""
      : "";
    function collectFormData(form) {
      var data = new FormData();
      var formId = form.id;
      // collect own children + elements associated via form="" attribute
      var elements = [];
      if (formId) {
        document.querySelectorAll("[form='" + CSS.escape(formId) + "']").forEach(function(el) { elements.push(el); });
      }
      form.querySelectorAll("[name]").forEach(function(el) { elements.push(el); });
      elements.forEach(function(el) {
        if (!el.name) return;
        if (el.type === "checkbox") {
          if (el.checked) data.set(el.name, el.value !== "" ? el.value : "on");
        } else if (el.type === "radio") {
          if (el.checked) data.set(el.name, el.value);
        } else {
          data.set(el.name, el.value);
        }
      });
      if (csrfToken) data.set("__RequestVerificationToken", csrfToken);
      return data;
    }
    var promises = planForms.map(function(form) {
      return fetch("/uiplan/update", {
        method: "POST",
        body: collectFormData(form),
        redirect: "manual"
      });
    });
    Promise.all(promises).then(function() {
      window.__markSubmitting && window.__markSubmitting();
      var url = new URL(window.location);
      url.searchParams.set("saved", "1");
      window.location.href = url.toString();
    }).catch(function() {
      btn.disabled = false;
      btn.innerHTML = origHtml;
      window.__showErrorDialog && window.__showErrorDialog("\u041e\u0448\u0438\u0431\u043a\u0430 \u043f\u0440\u0438 \u0441\u043e\u0445\u0440\u0430\u043d\u0435\u043d\u0438\u0438. \u041f\u043e\u043f\u0440\u043e\u0431\u0443\u0439\u0442\u0435 \u0435\u0449\u0451 \u0440\u0430\u0437.");
    });
  });
})();

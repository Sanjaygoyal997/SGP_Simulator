const $ = selector => document.querySelector(selector);
const state = { config: null, equipmentIndex: 0, selected: 0, dirty: false,
  currentFile: new URLSearchParams(location.search).get('file'), dataFiles: [], live: null,
  liveRequestPending: false, processes: [], dataRecipes: null,
  master: { configured: false, loaded: false, equipment: [], recipes: {}, error: null, target: '', envFile: null } };

const defaultVariation = () => ({ cycleVariationPercent: 5, slowCyclePercent: 10, breakdownPercent: 5,
  breakdownMinMinutes: 10, breakdownMaxMinutes: 30, gapVariationPercent: 30, processVariationPercent: 2 });

const safeName = text => (text ?? '').trim().replace(/[^A-Za-z0-9._-]+/g, '_').replace(/^[^A-Za-z0-9]+/, '')
  .replace(/\.{2,}/g, '.').slice(0, 64);
const channelKinds = {
  BatchRunning: [1, 14], RecipeName: [15, 16], StepValue: [17, 22],
  Drift: [27, 28], Pulse: [29, 30], Setpoint: [31, 32]
};
const equipmentPattern = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;

const current = () => state.config?.equipment[state.equipmentIndex];

function message(text, error = false) {
  const toast = $('#toast');
  toast.textContent = text;
  toast.style.background = error ? '#a4423e' : '#263a3d';
  toast.classList.add('visible');
  clearTimeout(message.timer);
  message.timer = setTimeout(() => toast.classList.remove('visible'), 4500);
}

function setDirty() {
  state.dirty = true;
  $('#save-state').textContent = 'Unsaved changes';
  renderLiveStatus();
}

function newTag(fields = {}) {
  return { name: '', address: '', type: 'Analog', unitText: '', format: '0.00', alarm: false,
    simulationKind: 'None', simulationChannel: '', simulationFile: '', simulationColumn: '',
    simulationMin: 0, simulationMinSpecified: false,
    simulationMax: 0, simulationMaxSpecified: false, simulationValue: '', ...fields };
}

function processLabel(processId) {
  return processId || (state.processes[0] ? `${state.processes[0].processId} (default)` : 'Default');
}

function renderLiveStatus() {
  const live = state.live;
  const running = !!live?.running;
  const enabled = state.config?.equipment.filter(item => item.enabled) ?? [];
  $('#start-simulation').disabled = running || state.liveRequestPending || !enabled.length;
  $('#start-simulation').textContent = enabled.length > 1 ? `Start ${enabled.length} equipment` : 'Start simulation';
  $('#stop-simulation').disabled = !running || state.liveRequestPending;
  $('#save').disabled = running && live.fileName === state.currentFile;
  const failed = live?.equipment?.filter(item => item.error) ?? [];
  $('#live-state').textContent = running
    ? `Running ${live.equipment.filter(item => item.running).length} equipment from ${live.fileName}`
    : failed.length ? `Stopped with ${failed.length} error(s)` : 'Stopped';

  // A finished run's rows only stay while they still match equipment in the open XML.
  const names = new Set(state.config?.equipment.map(item => item.name.toLowerCase()) ?? []);
  const table = $('#live-equipment');
  const rows = running ? live.equipment : live?.fileName === state.currentFile
    ? (live.equipment ?? []).filter(item => names.has(item.equipment.toLowerCase())) : [];
  table.classList.toggle('hidden', !rows.length);
  table.tBodies[0].replaceChildren(...rows.map(item => {
    const row = document.createElement('tr');
    const schedule = item.schedule;
    const recipe = !schedule ? '-' : schedule.idle ? `Idle - schedule finished (${schedule.recipe})` :
      `${schedule.recipe} - row ${schedule.step}/${schedule.stepCount}, batch ${schedule.loop
        ? `${schedule.cycle + 1} (loop)` : `${Math.min(schedule.cycle + 1, schedule.cycles)}/${schedule.cycles}`}` +
      (schedule.recordedData === false ? ' (no recorded data)' : '');
    const lastCycle = !schedule?.lastCycleSeconds ? '-' : !schedule.lastSetSeconds ? `${schedule.lastCycleSeconds} s` :
      `${schedule.lastCycleSeconds} s / set ${schedule.lastSetSeconds} s` +
      (schedule.lastCycleSeconds > schedule.lastSetSeconds
        ? ` (over by ${Math.round((schedule.lastCycleSeconds - schedule.lastSetSeconds) * 10) / 10} s)` : '') +
      ` - ${schedule.cyclesOverSet} of ${schedule.cyclesCompared} over set`;
    const cells = [item.equipment, item.processId,
      item.error ? `Error: ${item.error}` : item.running ? 'Running' : 'Stopped', recipe, lastCycle,
      String(item.rowsWritten)]
      .map(text => { const cell = document.createElement('td'); cell.textContent = text; return cell; });
    if (item.error) cells[2].className = 'error';
    const output = document.createElement('td');
    if (item.outputFile) {
      const link = document.createElement('a');
      link.href = `/api/simulation/output?equipment=${encodeURIComponent(item.equipment)}`;
      link.textContent = item.outputFile.split(/[\\/]/).pop();
      link.title = item.outputFile;
      output.append(link);
    }
    row.append(...cells, output);
    return row;
  }));
}

async function refreshLiveStatus() {
  if (state.liveRequestPending) return;
  try {
    const response = await fetch('/api/simulation');
    if (!response.ok) throw new Error('Could not read simulation status');
    state.live = await response.json();
    renderLiveStatus();
  } catch (error) {
    state.live = null;
    renderLiveStatus();
    $('#live-state').textContent = error.message;
  }
}

async function loadProcesses() {
  try {
    const response = await fetch('/api/simulation/processes');
    if (!response.ok) throw new Error('Could not load processes');
    state.processes = (await response.json()).filter(item => typeof item === 'object');
    if (state.config) renderEquipment();
  } catch (error) { message(error.message, true); }
}

function renderEquipmentTabs() {
  const equipment = state.config.equipment;
  $('#equipment-tabs').replaceChildren(...equipment.map((item, index) => {
    const tab = document.createElement('button');
    tab.type = 'button';
    tab.role = 'tab';
    tab.className = `equipment-tab${index === state.equipmentIndex ? ' active' : ''}${item.enabled ? '' : ' disabled'}`;
    tab.setAttribute('aria-selected', String(index === state.equipmentIndex));
    const name = document.createElement('span');
    name.textContent = item.name || 'New equipment';
    const detail = document.createElement('small');
    detail.textContent = `${processLabel(item.processId)} · ${item.tags.length} tags`;
    tab.append(name, detail);
    tab.addEventListener('click', () => selectEquipment(index));
    return tab;
  }));
  const enabled = equipment.filter(item => item.enabled).length;
  $('#equipment-count').textContent = `${equipment.length} equipment, ${enabled} enabled`;
}

function renderEquipment() {
  const item = current();
  $('#group-name').value = item.name;
  $('#opc-group').value = item.opcGroup ?? '';
  $('#opc-group-names').replaceChildren(...[...new Set(state.config.equipment
    .map(other => other.opcGroup).filter(Boolean))].map(name => new Option(name)));
  $('#opc-server').value = item.opcServer ?? '';
  $('#equipment-enabled').checked = item.enabled;
  const picker = $('#equipment-process');
  picker.replaceChildren(new Option(processLabel(''), ''),
    ...state.processes.map(process => new Option(process.processId, process.processId)));
  if (item.processId && !state.processes.some(process => process.processId === item.processId))
    picker.add(new Option(`${item.processId} (not configured)`, item.processId));
  picker.value = item.processId ?? '';
  $('#delete-equipment').disabled = state.config.equipment.length <= 1;
  item.variation ??= defaultVariation();
  document.querySelectorAll('[data-variation]').forEach(input => { input.value = item.variation[input.dataset.variation]; });
  renderMaster();
  renderSchedule();
  renderEquipmentTabs();
  renderMode();
}

async function loadMaster() {
  try {
    const status = await (await fetch('/api/master')).json();
    state.master.configured = !!status.configured;
    state.master.target = status.target ?? '';
    state.master.envFile = status.envFile ?? null;
    if (!state.master.configured) { renderMasterStatus(); return; }
    const response = await fetch('/api/master/equipment');
    const result = await response.json();
    if (!response.ok) throw new Error(result.detail || 'Could not read master data equipment');
    state.master.equipment = result;
    state.master.loaded = true;
    state.master.error = null;
  } catch (error) {
    state.master.error = error.message;
    message(error.message, true);
  }
  renderMasterStatus();
  if (state.config) { applyMasterData(); renderEquipment(); }
}

function renderMasterStatus() {
  const status = $('#master-status');
  const master = state.master;
  const linked = state.config?.equipment.filter(item => item.masterEquipmentId).length ?? 0;
  status.className = `master-status ${master.configured && !master.error ? 'ok' : 'warn'}`;
  status.textContent = !master.configured
    ? 'Master data: not configured. Create .env in the repository folder with SGP_DB_HOST, SGP_DB_NAME, SGP_DB_USER and SGP_DB_PASSWORD, then restart the configurator.'
    : master.error ? `Master data (${master.target}): ${master.error}`
    : !master.loaded ? `Master data: connecting to ${master.target}...`
    : `Master data: ${master.target}, ${master.equipment.length} equipment available, ${linked} linked.`;
}

const masterById = id => state.master.equipment.find(equipment => equipment.id === id);

// Links equipment whose name matches a master-data equipment, and keeps linked names and OPC groups
// equal to display_name and area.
function applyMasterData() {
  if (!state.config || !state.master.loaded) return;
  let linked = 0, synced = 0;
  const taken = new Set(state.config.equipment.map(item => item.masterEquipmentId).filter(Boolean));
  for (const item of state.config.equipment) {
    if (!item.masterEquipmentId) {
      const key = item.name.toLowerCase();
      const match = state.master.equipment.find(equipment => !taken.has(equipment.id) &&
        [equipment.displayName, equipment.name].some(name => safeName(name).toLowerCase() === key));
      if (match) { item.masterEquipmentId = match.id; taken.add(match.id); linked++; }
    }
    const equipment = item.masterEquipmentId && masterById(item.masterEquipmentId);
    if (!equipment) continue;
    const name = uniqueName(safeName(equipment.displayName) || safeName(equipment.name), item);
    if (item.name !== name) { item.name = name; synced++; }
    if (equipment.area && item.opcGroup !== equipment.area) { item.opcGroup = equipment.area; synced++; }
  }
  if (linked || synced) {
    setDirty();
    message([linked && `Linked ${linked} equipment to the master data by name.`,
      synced && 'Updated names and OPC groups from the master data.'].filter(Boolean).join(' ') +
      ' Save XML to keep this.');
  }
  renderMasterStatus();
}

function uniqueName(name, self) {
  const used = new Set(state.config.equipment.filter(item => item !== self).map(item => item.name.toLowerCase()));
  let result = name || 'Equipment', number = 2;
  while (used.has(result.toLowerCase())) result = `${name}_${number++}`;
  return result;
}

// Recipes (item_master rows) of a master-data equipment, loaded once per page.
async function loadMasterRecipes(id) {
  if (!id || state.master.recipes[id] || !state.master.configured) return;
  state.master.recipes[id] = 'loading';
  try {
    const response = await fetch(`/api/master/equipment/${id}/recipes`);
    const result = await response.json();
    if (!response.ok) throw new Error(result.detail || 'Could not read recipes from master data');
    state.master.recipes[id] = result;
    syncCycleTimes(id);
  } catch (error) {
    delete state.master.recipes[id];
    message(error.message, true);
  }
  if (state.config) renderSchedule();
}

// Keep each step's cycle time equal to item_master.cycle_time for linked equipment.
function syncCycleTimes(id) {
  const recipes = state.master.recipes[id];
  if (!Array.isArray(recipes) || !state.config) return;
  let changed = 0;
  state.config.equipment.filter(item => item.masterEquipmentId === id).forEach(item =>
    item.schedule?.steps.forEach(step => {
      const recipe = recipes.find(entry => entry.code === step.recipe);
      if (recipe && step.cycleSeconds !== recipe.cycleSeconds) { step.cycleSeconds = recipe.cycleSeconds; changed++; }
    }));
  if (changed) { setDirty(); message(`Updated ${changed} cycle time(s) from the master data. Save XML to keep them.`); }
}

// Recipes a step can use: the equipment's item_master rows when linked, else the XML recipes.
function scheduleChoices(item) {
  if (item.masterEquipmentId) {
    const recipes = state.master.recipes[item.masterEquipmentId];
    return Array.isArray(recipes) ? recipes.map(recipe => ({ name: recipe.code,
      label: `${recipe.code} - ${recipe.name}`, cycleSeconds: recipe.cycleSeconds })) : null;
  }
  return state.config.recipes.map(recipe => ({ name: recipe.name, label: recipe.name, cycleSeconds: 0 }));
}

function renderMaster() {
  const item = current();
  const linked = !!(item.masterEquipmentId && masterById(item.masterEquipmentId));
  for (const id of ['#group-name', '#opc-group']) {
    $(id).readOnly = linked;
    $(id).title = linked ? 'From the master data (display_name / area). Choose "(not linked)" to edit.' : '';
  }
  const field = $('#master-field');
  field.classList.toggle('hidden', !state.master.configured);
  if (!state.master.configured) return;
  const picker = $('#master-equipment');
  picker.replaceChildren(new Option('(not linked)', ''), ...state.master.equipment.map(equipment =>
    new Option(`${equipment.area ? `${equipment.area} / ` : ''}${equipment.displayName} (${equipment.name})`,
      String(equipment.id))));
  if (item.masterEquipmentId && !state.master.equipment.some(equipment => equipment.id === item.masterEquipmentId))
    picker.add(new Option(`#${item.masterEquipmentId} (not in master data)`, String(item.masterEquipmentId)));
  picker.value = item.masterEquipmentId ? String(item.masterEquipmentId) : '';
  loadMasterRecipes(item.masterEquipmentId);
}

// One table for all equipment. Each equipment runs its own rows top to bottom, in parallel with the others.
function renderSchedule() {
  const equipment = state.config.equipment;
  equipment.forEach(item => loadMasterRecipes(item.masterEquipmentId));
  const rows = equipment.flatMap((item, equipmentIndex) =>
    (item.schedule?.steps ?? []).map((step, stepIndex) => ({ item, equipmentIndex, step, stepIndex })));
  $('#schedule-table').classList.toggle('hidden', !rows.length);
  $('#schedule-note').textContent = !rows.length
    ? 'No schedule yet. Press + Add row, then choose the equipment, the recipe, and the number of batches, or Loop to keep running it.'
    : 'Each equipment runs its rows from top to bottom, all equipment in parallel. After its last row an equipment stays idle; a Loop row runs until you stop. Equipment without rows run continuously on their tags\' data.';
  $('#schedule-steps').replaceChildren(...rows.map(({ item, equipmentIndex, step, stepIndex }, index) => {
    const steps = item.schedule.steps;
    const choices = scheduleChoices(item);
    const row = document.createElement('tr');
    row.classList.toggle('current-equipment', equipmentIndex === state.equipmentIndex);
    const cell = (...children) => { const td = document.createElement('td'); td.append(...children); return td; };

    const equipmentSelect = document.createElement('select');
    equipmentSelect.setAttribute('aria-label', `Row ${index + 1} equipment`);
    equipmentSelect.append(...equipment.map((other, otherIndex) => new Option(other.name || 'New equipment', String(otherIndex))));
    equipmentSelect.value = String(equipmentIndex);
    equipmentSelect.addEventListener('change', () => {
      const target = equipment[Number(equipmentSelect.value)];
      steps.splice(stepIndex, 1);
      target.schedule ??= { repeat: false, steps: [] };
      const targetChoices = scheduleChoices(target) ?? [];
      const choice = targetChoices.find(entry => entry.name === step.recipe) ?? targetChoices[0];
      target.schedule.steps.push({ ...step, recipe: choice?.name ?? step.recipe, cycleSeconds: choice?.cycleSeconds ?? 0 });
      renderSchedule(); renderEquipmentTabs(); setDirty();
    });

    const recipeSelect = document.createElement('select');
    recipeSelect.setAttribute('aria-label', `Row ${index + 1} recipe`);
    if (choices === null) recipeSelect.add(new Option(step.recipe ? `${step.recipe} (loading...)` : 'Loading recipes...', step.recipe));
    else {
      recipeSelect.append(...choices.map(choice => new Option(choice.label, choice.name)));
      if (!choices.some(choice => choice.name === step.recipe))
        recipeSelect.add(new Option(`${step.recipe || 'Select recipe'} (${item.masterEquipmentId
          ? 'not in master data' : 'not defined'})`, step.recipe));
    }
    recipeSelect.value = step.recipe;
    recipeSelect.addEventListener('change', () => {
      step.recipe = recipeSelect.value;
      step.cycleSeconds = (choices ?? []).find(choice => choice.name === step.recipe)?.cycleSeconds ?? 0;
      renderSchedule(); setDirty();
    });

    const batches = document.createElement('input');
    batches.type = 'number';
    batches.min = '1';
    batches.step = '1';
    batches.value = step.cycles;
    batches.disabled = !!step.loop;
    batches.setAttribute('aria-label', `Row ${index + 1} batches`);
    batches.addEventListener('input', () => { step.cycles = Number(batches.value); setDirty(); });

    const loop = document.createElement('input');
    loop.type = 'checkbox';
    loop.checked = !!step.loop;
    loop.setAttribute('aria-label', `Row ${index + 1} loop`);
    loop.addEventListener('change', () => {
      step.loop = loop.checked;
      if (!step.cycles || step.cycles < 1) step.cycles = 1;
      renderSchedule(); setDirty();
    });

    const cycleTime = document.createElement('span');
    cycleTime.className = 'step-cycle';
    cycleTime.textContent = step.cycleSeconds > 0 ? `${step.cycleSeconds} s` : 'recorded';
    cycleTime.title = step.cycleSeconds > 0 ? 'From master.item_master.cycle_time' : 'Cycle length from the recorded data';

    const button = (label, title, action, disabled = false, danger = false) => {
      const element = document.createElement('button');
      element.type = 'button';
      element.className = `icon-button${danger ? ' danger' : ''}`;
      element.textContent = label;
      element.title = title;
      element.setAttribute('aria-label', title);
      element.disabled = disabled;
      element.addEventListener('click', () => { action(); renderSchedule(); renderEquipmentTabs(); setDirty(); });
      return element;
    };
    const swap = offset => { [steps[stepIndex], steps[stepIndex + offset]] = [steps[stepIndex + offset], steps[stepIndex]]; };
    const warning = document.createElement('span');
    warning.className = 'step-warning';
    warning.textContent = [recipeWarning(step.recipe, item),
      steps.slice(0, stepIndex).some(previous => previous.loop) ? 'Never reached: an earlier row of this equipment loops' : '']
      .filter(Boolean).join('. ');
    const actions = document.createElement('div');
    actions.className = 'row-actions';
    actions.append(
      button('↑', 'Move up (within this equipment)', () => swap(-1), stepIndex === 0),
      button('↓', 'Move down (within this equipment)', () => swap(1), stepIndex === steps.length - 1),
      button('×', 'Delete row', () => steps.splice(stepIndex, 1), false, true), warning);

    row.append(cell(String(index + 1)), cell(equipmentSelect), cell(recipeSelect), cell(batches), cell(loop),
      cell(cycleTime), cell(actions));
    return row;
  }));
}

// Files with recorded rows for a recipe, or null while the data has not been read yet.
function recordedFiles(name) {
  return state.dataRecipes ? state.dataRecipes.find(item => item.name === name)?.files ?? [] : null;
}

function recipeWarning(name, equipment = current()) {
  const recipe = state.config.recipes.find(item => item.name === name) ??
    (equipment.masterEquipmentId && name ? { name, referenceFile: '' } : null);
  const files = recordedFiles(name);
  const usesData = equipment.tags.some(tag => tag.simulationKind === 'DataFile');
  if (!recipe || files === null || (!usesData && !recipe.referenceFile)) return '';
  const usable = recipe.referenceFile ? files.includes(recipe.referenceFile) : files.length > 0;
  return usable ? '' : 'No recorded data: uses all data, only the name changes';
}

async function loadDataRecipes() {
  try {
    const response = await fetch(`/api/data-recipes?file=${encodeURIComponent(state.currentFile)}`);
    state.dataRecipes = response.ok ? await response.json() : [];
  } catch { state.dataRecipes = []; }
  if (state.config) { renderSchedule(); if ($('#recipes-dialog').open) renderRecipes(); }
}

function renderRecipes() {
  const recipes = state.config.recipes;
  $('#recipe-rows').replaceChildren(...recipes.map((recipe, index) => {
    const row = document.createElement('tr');
    const nameCell = document.createElement('td');
    const name = document.createElement('input');
    name.value = recipe.name;
    name.maxLength = 64;
    name.placeholder = 'CT1482MLX3';
    name.setAttribute('aria-label', `Recipe ${index + 1} name`);
    name.addEventListener('change', () => {
      const previous = recipe.name;
      recipe.name = name.value.trim();
      // Keep schedules pointing at the renamed recipe.
      state.config.equipment.forEach(item => item.schedule?.steps.forEach(step => {
        if (step.recipe === previous) step.recipe = recipe.name;
      }));
      renderRecipes(); renderSchedule(); setDirty();
    });
    nameCell.append(name);
    const foundCell = document.createElement('td');
    foundCell.className = 'found-cell';
    const files = recordedFiles(recipe.name);
    foundCell.textContent = files === null ? 'Reading data...' : files.length ? files.join(', ') :
      'Not in reference data';
    foundCell.classList.add(files?.length ? 'recipe-found' : 'recipe-missing');
    const fileCell = document.createElement('td');
    const file = document.createElement('select');
    file.setAttribute('aria-label', `Recipe ${index + 1} reference file`);
    file.append(new Option('(any reference file)', ''),
      ...state.dataFiles.map(item => new Option(item.id, item.id)));
    if (recipe.referenceFile && !state.dataFiles.some(item => item.id === recipe.referenceFile))
      file.add(new Option(`${recipe.referenceFile} (unavailable)`, recipe.referenceFile));
    file.value = recipe.referenceFile ?? '';
    file.addEventListener('change', () => { recipe.referenceFile = file.value; renderSchedule(); setDirty(); });
    fileCell.append(file);
    const actionCell = document.createElement('td');
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'icon-button danger';
    remove.textContent = '×';
    remove.title = remove.ariaLabel = 'Delete recipe';
    remove.addEventListener('click', () => {
      const users = state.config.equipment.filter(item =>
        item.schedule?.steps.some(step => step.recipe === recipe.name)).map(item => item.name);
      if (users.length) { message(`${recipe.name} is used in the schedule of ${users.join(', ')}. Remove those steps first.`, true); return; }
      recipes.splice(index, 1);
      renderRecipes(); renderSchedule(); setDirty();
    });
    actionCell.append(remove);
    row.append(nameCell, foundCell, fileCell, actionCell);
    return row;
  }));
}

function selectEquipment(index) {
  state.equipmentIndex = index;
  $('#search').value = '';
  renderEquipment();
  select(0);
}

function renderList() {
  const tags = current().tags;
  const terms = $('#search').value.trim().toLowerCase();
  const tbody = $('#tag-list');
  tbody.replaceChildren();
  let shown = 0;
  tags.forEach((tag, index) => {
    if (terms && !`${tag.name} ${tag.address}`.toLowerCase().includes(terms)) return;
    shown++;
    const row = document.createElement('tr');
    row.className = index === state.selected ? 'selected' : '';
    row.tabIndex = 0;
    row.addEventListener('click', () => select(index));
    row.addEventListener('keydown', event => { if (event.key === 'Enter') select(index); });
    const position = document.createElement('td');
    position.className = 'index-col';
    position.textContent = index + 1;
    const name = document.createElement('td');
    const strong = document.createElement('strong');
    strong.textContent = tag.name || 'New tag';
    name.append(strong);
    const source = document.createElement('td');
    source.textContent = current().runtimeMode === 'Realtime' ? 'OPC' :
      tag.simulationKind === 'DataFile' ? 'Data pattern' :
      tag.simulationKind === 'None' ? 'Unmapped' : tag.simulationKind;
    row.append(position, name, source);
    tbody.append(row);
  });
  $('#result-count').textContent = terms ? `${shown} matching` : `${shown} in XML order`;
}

function renderChannelOptions(tag) {
  const select = $('[data-field="simulationChannel"]');
  select.replaceChildren(new Option('Select channel', ''));
  const range = channelKinds[tag.simulationKind];
  if (range) for (let number = range[0]; number <= range[1]; number++) {
    select.add(new Option(`ch${number}`, `ch${number}`));
  }
  if (range && ![...select.options].some(option => option.value === tag.simulationChannel)) {
    tag.simulationChannel = '';
  }
  select.value = tag.simulationChannel || '';
  $('#channel-field').classList.toggle('hidden', !range);
  $('#value-field').classList.toggle('hidden', tag.simulationKind !== 'Constant');
}

function renderDataOptions(tag) {
  const fileSelect = $('[data-field="simulationFile"]');
  fileSelect.replaceChildren(new Option('Select data file', ''));
  state.dataFiles.forEach(file => fileSelect.add(new Option(file.id, file.id)));
  if (tag.simulationFile && !state.dataFiles.some(file => file.id === tag.simulationFile))
    fileSelect.add(new Option(`${tag.simulationFile} (unavailable)`, tag.simulationFile));
  fileSelect.value = tag.simulationFile || '';

  const columnSelect = $('[data-field="simulationColumn"]');
  columnSelect.replaceChildren(new Option('Select column', ''));
  const file = state.dataFiles.find(item => item.id === tag.simulationFile);
  file?.columns.forEach(column => columnSelect.add(new Option(
    column.sample ? `${column.name} - ${column.sample}` : column.name, column.name)));
  if (tag.simulationColumn && !file?.columns.some(column => column.name === tag.simulationColumn))
    columnSelect.add(new Option(`${tag.simulationColumn} (unavailable)`, tag.simulationColumn));
  columnSelect.value = tag.simulationColumn || '';
  const visible = tag.simulationKind === 'DataFile';
  $('#data-file-field').classList.toggle('hidden', !visible);
  $('#data-column-field').classList.toggle('hidden', !visible);
}

function select(index) {
  const tags = current().tags;
  state.selected = index;
  const tag = tags[index];
  $('#detail-form').classList.toggle('hidden', !tag);
  $('.detail-actions').classList.toggle('hidden', !tag);
  $('#detail-title').textContent = tag ? tag.name || 'New tag' : 'Tag details';
  $('#detail-subtitle').textContent = tag ? `Position ${index + 1} of ${tags.length}` :
    'Add a tag, or use From reference';
  if (tag) {
    document.querySelectorAll('[data-field]').forEach(input => {
      const field = input.dataset.field;
      if (field === 'simulationMin' || field === 'simulationMax') {
        input.value = tag[`${field}Specified`] ? tag[field] : '';
      } else if (input.type === 'checkbox') input.checked = !!tag[field];
      else if (!['simulationChannel', 'simulationFile', 'simulationColumn'].includes(field)) input.value = tag[field] ?? '';
    });
    renderChannelOptions(tag);
    renderDataOptions(tag);
  }
  $('#move-up').disabled = index <= 0;
  $('#move-down').disabled = index >= tags.length - 1;
  $('#delete-tag').disabled = tags.length <= 1;
  renderList();
}

async function refreshFiles() {
  const response = await fetch('/api/configs');
  if (!response.ok) throw new Error('Could not list XML files');
  const files = await response.json();
  const picker = $('#file-picker');
  picker.replaceChildren(...files.map(name => new Option(name, name)));
  picker.value = state.currentFile;
}

async function load(file = state.currentFile) {
  try {
    const response = await fetch(`/api/config${file ? `?file=${encodeURIComponent(file)}` : ''}`);
    if (!response.ok) throw new Error((await response.json()).detail || 'Could not load XML');
    const config = await response.json();
    if (!Array.isArray(config.equipment))
      throw new Error('Restart the configurator (dotnet run) to use multiple equipment.');
    config.recipes ??= [];
    state.config = config;
    state.currentFile = config.fileName;
    history.replaceState(null, '', `?file=${encodeURIComponent(state.currentFile)}`);
    state.dirty = false;
    $('#file-name').textContent = config.fileName;
    await refreshFiles();
    const dataResponse = await fetch(`/api/simulation-files?file=${encodeURIComponent(state.currentFile)}`);
    state.dataFiles = dataResponse.ok ? await dataResponse.json() : [];
    $('#save-state').textContent = 'Saved';
    applyMasterData();
    selectEquipment(Math.min(state.equipmentIndex, config.equipment.length - 1));
    loadDataRecipes();
    await refreshLiveStatus();
  } catch (error) { message(error.message, true); $('#save-state').textContent = 'Load failed'; }
}

function renderMode() {
  const item = current();
  const realtime = item.runtimeMode === 'Realtime';
  document.querySelectorAll('[data-mode]').forEach(button => {
    const active = button.dataset.mode === item.runtimeMode;
    button.classList.toggle('active', active);
    button.setAttribute('aria-pressed', String(active));
  });
  $('#opc-server-field').classList.toggle('hidden', !realtime);
  $('#simulation-section').classList.toggle('hidden', realtime);
  $('#tag-section-title').textContent = realtime ? 'OPC tag' : 'Output tag';
  $('#address-label').textContent = realtime ? 'OPC address' : 'Tag identifier';
  $('#mode-note').textContent = realtime ?
    'OPC acquisition is not available yet. Disable this equipment or switch it to Simulation before starting.' : '';
  renderList();
  renderLiveStatus();
}

function validate() {
  const recipeNames = new Set();
  for (const recipe of state.config.recipes) {
    if (!recipe.name.trim()) return 'Every recipe needs a name.';
    if (recipeNames.has(recipe.name)) return `Recipe ${recipe.name} is defined more than once.`;
    recipeNames.add(recipe.name);
  }
  const names = new Set();
  for (const item of state.config.equipment) {
    const label = item.name.trim() || 'Unnamed equipment';
    const name = item.name.trim();
    if (!name) return 'Every equipment needs a name.';
    if (!(item.opcGroup ?? '').trim()) return `${label}: enter an OPC group.`;
    if (!equipmentPattern.test(name) || name.includes('..'))
      return `${label}: use letters, numbers, dots, hyphens, or underscores in the equipment name.`;
    if (names.has(name.toLowerCase())) return `Equipment name ${name} is used more than once.`;
    names.add(name.toLowerCase());
    if (item.runtimeMode === 'Realtime' && !(item.opcServer ?? '').trim()) return `${label}: enter an OPC server.`;
    if (!item.tags.length) return `${label}: add at least one tag.`;
    const v = item.variation ?? defaultVariation();
    if (!(v.cycleVariationPercent >= 0 && v.cycleVariationPercent <= 50) ||
        !(v.slowCyclePercent >= 0 && v.slowCyclePercent <= 100) || !(v.breakdownPercent >= 0 && v.breakdownPercent <= 100) ||
        !(v.breakdownMinMinutes >= 0) || !(v.breakdownMaxMinutes >= v.breakdownMinMinutes) ||
        !(v.gapVariationPercent >= 0 && v.gapVariationPercent <= 90) ||
        !(v.processVariationPercent >= 0 && v.processVariationPercent <= 50))
      return `${label}: variation needs cycle 0-50%, gap 0-90%, process values 0-50%, slow cycles and breakdowns 0-100%, and breakdown max at least min.`;
    for (const [i, step] of (item.schedule?.steps ?? []).entries()) {
      if (!step.recipe) return `${label}: schedule step ${i + 1} needs a recipe.`;
      if (!item.masterEquipmentId && !recipeNames.has(step.recipe))
        return `${label}: schedule step ${i + 1} needs a defined recipe.`;
      if (!step.loop && (!Number.isInteger(step.cycles) || step.cycles < 1))
        return `${label}: schedule row ${i + 1} needs a whole number of batches (1 or more), or Loop.`;
    }
    for (let i = 0; i < item.tags.length; i++) {
      const tag = item.tags[i];
      if (!tag.name.trim() || !tag.address.trim()) return `${label}: tag ${i + 1} needs a name and address.`;
      if (channelKinds[tag.simulationKind] && !tag.simulationChannel) return `${label}: tag ${i + 1} needs a simulation channel.`;
      if (tag.simulationKind === 'DataFile' && (!tag.simulationFile || !tag.simulationColumn))
        return `${label}: tag ${i + 1} needs a reference file and column.`;
      if (tag.simulationKind === 'Constant' && !tag.simulationValue.trim()) return `${label}: tag ${i + 1} needs a constant value.`;
      if (tag.simulationMinSpecified && tag.simulationMaxSpecified && tag.simulationMin > tag.simulationMax)
        return `${label}: tag ${i + 1} has a minimum above its maximum.`;
    }
  }
  return null;
}

async function save() {
  const problem = validate();
  if (problem) { message(problem, true); return false; }
  const button = $('#save');
  button.disabled = true;
  try {
    const response = await fetch(`/api/config?file=${encodeURIComponent(state.currentFile)}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ revision: state.config.revision, equipment: state.config.equipment,
        recipes: state.config.recipes })
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.message || result.detail || 'Could not save XML');
    // Reload so new equipment get their XML positions and the revision matches the file.
    await load(state.currentFile);
    message('XML saved');
    return true;
  } catch (error) { message(error.message, true); return false; }
  finally { renderLiveStatus(); }
}

document.querySelectorAll('[data-field]').forEach(input => input.addEventListener('input', () => {
  const tag = current().tags[state.selected];
  const field = input.dataset.field;
  if (field === 'simulationMin' || field === 'simulationMax') {
    tag[`${field}Specified`] = input.value !== '';
    tag[field] = input.value === '' ? 0 : Number(input.value);
  } else tag[field] = input.type === 'checkbox' ? input.checked : input.value;
  if (field === 'simulationKind') {
    tag.simulationChannel = '';
    tag.simulationFile = '';
    tag.simulationColumn = '';
    renderChannelOptions(tag);
    renderDataOptions(tag);
  }
  if (field === 'simulationFile') {
    tag.simulationColumn = '';
    renderDataOptions(tag);
  }
  if (['name', 'simulationKind', 'simulationFile'].includes(field)) {
    $('#detail-title').textContent = tag.name || 'New tag';
    renderList();
  }
  setDirty();
}));

$('#group-name').addEventListener('input', event => { current().name = event.target.value; renderEquipmentTabs(); renderSchedule(); setDirty(); });
$('#opc-group').addEventListener('input', event => { current().opcGroup = event.target.value; setDirty(); });
$('#opc-server').addEventListener('input', event => { current().opcServer = event.target.value; setDirty(); });
$('#equipment-process').addEventListener('change', event => { current().processId = event.target.value; renderEquipmentTabs(); setDirty(); });
$('#equipment-enabled').addEventListener('change', event => { current().enabled = event.target.checked; renderEquipmentTabs(); setDirty(); });
$('#search').addEventListener('input', renderList);
document.querySelectorAll('[data-variation]').forEach(input => input.addEventListener('input', () => {
  const item = current();
  item.variation ??= defaultVariation();
  item.variation[input.dataset.variation] = input.value === '' ? 0 : Number(input.value);
  setDirty();
}));
$('#master-equipment').addEventListener('change', event => {
  const item = current();
  item.masterEquipmentId = event.target.value ? Number(event.target.value) : null;
  applyMasterData();
  if (item.schedule?.steps.length)
    message('The schedule recipes now come from this equipment\'s item_master list; check each step.');
  renderEquipment(); setDirty();
});
$('#add-step').addEventListener('click', () => {
  const item = current();
  const choices = scheduleChoices(item) ?? [];
  item.schedule ??= { repeat: false, steps: [] };
  const last = item.schedule.steps.at(-1)?.recipe;
  const next = choices.find(choice => choice.name !== last) ?? choices[0];
  item.schedule.steps.push({ recipe: next?.name ?? '', cycles: 1, cycleSeconds: next?.cycleSeconds ?? 0, loop: false });
  if (!choices.length) message(item.masterEquipmentId
    ? `${item.name} has no recipes in item_master.`
    : `No recipes for ${item.name}. Link it to a DB equipment, or add recipes with Recipes at the top.`, true);
  renderSchedule(); renderEquipmentTabs(); setDirty();
});
$('#open-recipes').addEventListener('click', () => { renderRecipes(); $('#recipes-dialog').showModal(); });
$('#find-recipes').addEventListener('click', async () => {
  if (!state.dataRecipes) await loadDataRecipes();
  const existing = new Set(state.config.recipes.map(recipe => recipe.name));
  const added = state.dataRecipes.map(item => item.name).filter(name => !existing.has(name));
  added.forEach(name => state.config.recipes.push({ name, referenceFile: '', description: '' }));
  renderRecipes(); renderSchedule();
  if (added.length) setDirty();
  message(added.length ? `Added ${added.join(', ')} from the reference data.` :
    state.dataRecipes.length ? 'All recipes in the reference data are already listed.' :
    'No recipe names were found in the reference data.');
});
$('#add-recipe').addEventListener('click', () => {
  state.config.recipes.push({ name: '', referenceFile: '', description: '' });
  renderRecipes(); setDirty();
  $('#recipe-rows').lastElementChild.querySelector('input').focus();
});
$('#recipes-form').addEventListener('submit', event => {
  event.preventDefault();
  const blank = state.config.recipes.findIndex(recipe => !recipe.name.trim());
  if (blank >= 0) { message('Give every recipe a name, or delete the empty row.', true); return; }
  $('#recipes-dialog').close();
  renderSchedule();
});
document.querySelectorAll('[data-mode]').forEach(button => button.addEventListener('click', () => {
  current().runtimeMode = button.dataset.mode;
  renderMode(); setDirty();
}));
function addEquipment(fields, copyFrom) {
  const used = new Set(state.config.equipment.map(item => item.processId || state.processes[0]?.processId));
  const byName = fields.processName && state.processes.find(process =>
    process.processId.toLowerCase() === fields.processName.toLowerCase());
  const process = byName ?? state.processes.find(item => !used.has(item.processId)) ?? state.processes[0];
  const tags = copyFrom ? structuredClone(copyFrom.tags) : [];
  const item = { index: null, name: uniqueName(fields.name), opcGroup: fields.opcGroup ?? current()?.opcGroup ?? '',
    description: '', opcServer: '', runtimeMode: 'Simulation', enabled: true, processId: process?.processId ?? '',
    tags, schedule: null, masterEquipmentId: fields.masterEquipmentId ?? null,
    variation: copyFrom?.variation ? { ...copyFrom.variation } : defaultVariation() };
  state.config.equipment.push(item);
  return item;
}

$('#add-equipment').addEventListener('click', () => {
  const form = $('#add-equipment-form');
  const master = state.master;
  const taken = new Set(state.config.equipment.map(item => item.masterEquipmentId).filter(Boolean));
  const list = $('#master-choices');
  list.classList.toggle('hidden', !master.loaded);
  $('#add-selected').classList.toggle('hidden', !master.loaded);
  $('#add-equipment-note').textContent = master.loaded
    ? 'Select equipment from the master data. Each gets its name from display_name, its OPC group from the area, the process with the same name, and its recipes from item_master.'
    : `${$('#master-status').textContent} You can still add an equipment manually.`;
  list.replaceChildren(...(master.equipment.length ? master.equipment.map(equipment => {
    const label = document.createElement('label');
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.value = String(equipment.id);
    box.disabled = taken.has(equipment.id);
    label.classList.toggle('taken', box.disabled);
    const text = document.createElement('span');
    text.textContent = `${equipment.area ? `${equipment.area} / ` : ''}${equipment.displayName} (${equipment.name})`;
    const note = document.createElement('small');
    note.textContent = box.disabled ? 'already in this XML' : '';
    label.append(box, text, note);
    return label;
  }) : [Object.assign(document.createElement('p'), { className: 'empty',
    textContent: 'No active equipment for this plant in master.equipment_master.' })]));
  form.elements.copyFrom.replaceChildren(new Option('(no tags - add them later)', ''),
    ...state.config.equipment.map((item, index) => new Option(`${item.name} (${item.tags.length} tags)`, String(index))));
  form.elements.copyFrom.value = state.config.equipment.length ? String(state.equipmentIndex) : '';
  $('#add-equipment-dialog').showModal();
});
const copySource = () => {
  const value = $('#add-equipment-form').elements.copyFrom.value;
  return value === '' ? null : state.config.equipment[Number(value)];
};
$('#cancel-add-equipment').addEventListener('click', () => $('#add-equipment-dialog').close());
$('#add-manual').addEventListener('click', () => {
  const source = copySource();
  const item = addEquipment({ name: `Equipment${state.config.equipment.length + 1}` }, source);
  $('#add-equipment-dialog').close();
  selectEquipment(state.config.equipment.length - 1); setDirty();
  $('#group-name').select();
  message(`${item.name} added. Rename it${source ? '' : ', then add tags or use From reference'}.`);
});
$('#add-equipment-form').addEventListener('submit', event => {
  event.preventDefault();
  const ids = [...document.querySelectorAll('#master-choices input:checked')].map(box => Number(box.value));
  if (!ids.length) { message('Select at least one equipment, or use Add manual equipment.', true); return; }
  const source = copySource();
  const added = ids.map(id => {
    const equipment = masterById(id);
    return addEquipment({ name: safeName(equipment.displayName) || safeName(equipment.name),
      opcGroup: equipment.area || undefined, processName: equipment.name, masterEquipmentId: id }, source);
  });
  $('#add-equipment-dialog').close();
  selectEquipment(state.config.equipment.length - 1); setDirty();
  message(`Added ${added.map(item => item.name).join(', ')}${source ? ` with ${source.tags.length} tags each` : ''}. Add schedule steps, then save.`);
});
$('#delete-equipment').addEventListener('click', () => {
  if (state.config.equipment.length <= 1) return;
  if (!confirm(`Remove equipment ${current().name || 'New equipment'} and its tags?`)) return;
  state.config.equipment.splice(state.equipmentIndex, 1);
  selectEquipment(Math.min(state.equipmentIndex, state.config.equipment.length - 1)); setDirty();
});
$('#add-tag').addEventListener('click', () => {
  const tags = current().tags;
  tags.push(newTag());
  $('#search').value = '';
  select(tags.length - 1); setDirty(); renderEquipmentTabs();
  $('[data-field="name"]').focus();
});
function referenceTag(file, column) {
  const name = column.name.replace(/\(Y\)$/, '');
  const text = column.sample !== '' && column.sample !== 'null' && isNaN(Number(column.sample));
  return newTag({ name, address: name, type: text ? 'Text' : 'Analog', format: text ? '' : '0.00',
    simulationKind: 'DataFile', simulationFile: file.id, simulationColumn: column.name });
}
function renderReferenceSummary() {
  const form = $('#reference-form');
  const file = state.dataFiles.find(item => item.id === form.elements.file.value);
  const existing = current().tags.length;
  $('#reference-summary').textContent = !file ? '' : form.elements.mode.value === 'replace'
    ? `Creates ${file.columns.length} tags for ${current().name || 'this equipment'} in the reference column order and removes its ${existing} current tag(s). Its output file will have the same columns as ${file.id}.`
    : `Adds ${file.columns.length} tags after the ${existing} current tag(s). Output columns are numbered by tag position, so they will not line up with the reference.`;
}
$('#from-reference').addEventListener('click', () => {
  if (!state.dataFiles.length) { message('No reference files were found in the data folder.', true); return; }
  const form = $('#reference-form');
  const used = current().tags.find(tag => tag.simulationKind === 'DataFile')?.simulationFile;
  form.elements.file.replaceChildren(...state.dataFiles.map(file =>
    new Option(`${file.id} (${file.columns.length} columns)`, file.id)));
  if (state.dataFiles.some(file => file.id === used)) form.elements.file.value = used;
  form.elements.mode.value = 'replace';
  renderReferenceSummary();
  $('#reference-dialog').showModal();
});
$('#reference-form').elements.file.addEventListener('change', renderReferenceSummary);
$('#reference-form').elements.mode.addEventListener('change', renderReferenceSummary);
$('#cancel-reference').addEventListener('click', () => $('#reference-dialog').close());
$('#reference-form').addEventListener('submit', event => {
  event.preventDefault();
  const form = event.currentTarget;
  const file = state.dataFiles.find(item => item.id === form.elements.file.value);
  if (!file) return;
  const tags = file.columns.map(column => referenceTag(file, column));
  const replace = form.elements.mode.value === 'replace';
  const item = current();
  const start = replace ? 0 : item.tags.length;
  item.tags = replace ? tags : item.tags.concat(tags);
  $('#reference-dialog').close();
  $('#search').value = '';
  select(start); setDirty(); renderEquipmentTabs();
  message(`${tags.length} tags created for ${item.name} from ${file.id}. Save XML to keep them.`);
});
$('#delete-tag').addEventListener('click', () => {
  const tags = current().tags;
  if (tags.length <= 1) return;
  tags.splice(state.selected, 1);
  select(Math.min(state.selected, tags.length - 1)); setDirty(); renderEquipmentTabs();
});
function move(offset) {
  const tags = current().tags;
  const from = state.selected, to = from + offset;
  if (to < 0 || to >= tags.length) return;
  [tags[from], tags[to]] = [tags[to], tags[from]];
  select(to); setDirty();
}
$('#move-up').addEventListener('click', () => move(-1));
$('#move-down').addEventListener('click', () => move(1));
$('#reload').addEventListener('click', () => {
  if (state.dirty && !confirm('Discard unsaved changes and reload XML?')) return;
  load();
});
$('#file-picker').addEventListener('change', event => {
  const target = event.target.value;
  if (state.dirty && !confirm('Discard unsaved changes and open another XML file?')) {
    event.target.value = state.currentFile;
    return;
  }
  state.equipmentIndex = 0;
  load(target);
});
function updateNewMode() {
  const form = $('#new-form');
  const realtime = form.elements.runtimeMode.value === 'Realtime';
  $('#new-opc-server').hidden = !realtime;
  form.elements.opcServer.required = realtime;
}
$('#new-form').elements.runtimeMode.addEventListener('change', updateNewMode);
$('#new-file').addEventListener('click', () => {
  const form = $('#new-form');
  form.reset();
  updateNewMode();
  $('#new-dialog').showModal();
  form.elements.fileName.focus();
});
$('#cancel-new').addEventListener('click', () => $('#new-dialog').close());
$('#new-form').addEventListener('submit', async event => {
  event.preventDefault();
  const form = event.currentTarget;
  const payload = Object.fromEntries(new FormData(form));
  if (payload.runtimeMode === 'Simulation') payload.opcServer = '';
  const button = form.querySelector('[type="submit"]');
  button.disabled = true;
  try {
    const response = await fetch('/api/configs', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.message || result.detail || 'Could not create XML file');
    $('#new-dialog').close();
    state.equipmentIndex = 0;
    await load(result.fileName);
    message(`${result.fileName} created. Add a tag to complete it.`);
  } catch (error) { message(error.message, true); }
  finally { button.disabled = false; }
});
$('#save').addEventListener('click', save);
$('#start-simulation').addEventListener('click', async () => {
  if (state.dirty && !await save()) return;
  state.liveRequestPending = true;
  renderLiveStatus();
  try {
    const response = await fetch('/api/simulation', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fileName: state.currentFile, revision: state.config.revision })
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.message || result.detail || 'Could not start simulation');
    state.live = result;
    renderLiveStatus();
  } catch (error) { message(error.message, true); }
  finally { state.liveRequestPending = false; await refreshLiveStatus(); }
});
$('#stop-simulation').addEventListener('click', async () => {
  state.liveRequestPending = true;
  renderLiveStatus();
  try {
    const response = await fetch('/api/simulation', { method: 'DELETE' });
    const result = await response.json();
    if (!response.ok) throw new Error(result.message || 'Could not stop simulation');
    state.live = result;
    renderLiveStatus();
  } catch (error) { message(error.message, true); }
  finally { state.liveRequestPending = false; await refreshLiveStatus(); }
});
window.addEventListener('beforeunload', event => { if (state.dirty) { event.preventDefault(); event.returnValue = ''; } });
loadProcesses().then(() => load());
loadMaster();
setInterval(refreshLiveStatus, 1000);

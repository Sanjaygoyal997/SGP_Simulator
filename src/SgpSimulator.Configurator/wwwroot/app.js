const $ = selector => document.querySelector(selector);
const state = { config: null, equipmentIndex: 0, selected: 0, dirty: false,
  currentFile: new URLSearchParams(location.search).get('file'), dataFiles: [], live: null,
  liveRequestPending: false, processes: [] };
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
    const cells = [item.equipment, item.processId,
      item.error ? `Error: ${item.error}` : item.running ? 'Running' : 'Stopped', String(item.rowsWritten)]
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
    ...state.processes.map(process => new Option(`${process.processId} - ${process.activeRecipe}`, process.processId)));
  if (item.processId && !state.processes.some(process => process.processId === item.processId))
    picker.add(new Option(`${item.processId} (not configured)`, item.processId));
  picker.value = item.processId ?? '';
  $('#delete-equipment').disabled = state.config.equipment.length <= 1;
  renderEquipmentTabs();
  renderMode();
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
    state.config = config;
    state.currentFile = config.fileName;
    history.replaceState(null, '', `?file=${encodeURIComponent(state.currentFile)}`);
    state.dirty = false;
    $('#file-name').textContent = config.fileName;
    await refreshFiles();
    const dataResponse = await fetch(`/api/simulation-files?file=${encodeURIComponent(state.currentFile)}`);
    state.dataFiles = dataResponse.ok ? await dataResponse.json() : [];
    $('#save-state').textContent = 'Saved';
    selectEquipment(Math.min(state.equipmentIndex, config.equipment.length - 1));
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
      body: JSON.stringify({ revision: state.config.revision, equipment: state.config.equipment })
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

$('#group-name').addEventListener('input', event => { current().name = event.target.value; renderEquipmentTabs(); setDirty(); });
$('#opc-group').addEventListener('input', event => { current().opcGroup = event.target.value; setDirty(); });
$('#opc-server').addEventListener('input', event => { current().opcServer = event.target.value; setDirty(); });
$('#equipment-process').addEventListener('change', event => { current().processId = event.target.value; renderEquipmentTabs(); setDirty(); });
$('#equipment-enabled').addEventListener('change', event => { current().enabled = event.target.checked; renderEquipmentTabs(); setDirty(); });
$('#search').addEventListener('input', renderList);
document.querySelectorAll('[data-mode]').forEach(button => button.addEventListener('click', () => {
  current().runtimeMode = button.dataset.mode;
  renderMode(); setDirty();
}));
$('#add-equipment').addEventListener('click', () => {
  const names = new Set(state.config.equipment.map(item => item.name.toLowerCase()));
  let number = state.config.equipment.length + 1, name;
  do name = `Equipment${number++}`; while (names.has(name.toLowerCase()));
  const used = new Set(state.config.equipment.map(item => item.processId || state.processes[0]?.processId));
  const process = state.processes.find(item => !used.has(item.processId)) ?? state.processes[0];
  state.config.equipment.push({ index: null, name, opcGroup: current()?.opcGroup ?? '', description: '', opcServer: '', runtimeMode: 'Simulation',
    enabled: true, processId: process?.processId ?? '', tags: [] });
  selectEquipment(state.config.equipment.length - 1); setDirty();
  $('#group-name').select();
  message(`${name} added. Rename it, then add tags or use From reference.`);
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
setInterval(refreshLiveStatus, 1000);

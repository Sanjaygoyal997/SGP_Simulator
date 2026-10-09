const $ = selector => document.querySelector(selector);
const state = { config: null, selected: 0, dirty: false,
  currentFile: new URLSearchParams(location.search).get('file'), dataFiles: [], live: null,
  liveRequestPending: false, equipment: [], renameSupported: false };
const channelKinds = {
  BatchRunning: [1, 14], RecipeName: [15, 16], StepValue: [17, 22],
  Drift: [27, 28], Pulse: [29, 30], Setpoint: [31, 32]
};

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

function renderLiveStatus() {
  const live = state.live;
  const running = !!live?.running;
  $('#live-controls').classList.toggle('hidden',
    state.config?.runtimeMode !== 'Simulation' && !running);
  $('#start-simulation').disabled = running || state.liveRequestPending || !state.config ||
    state.config.runtimeMode !== 'Simulation';
  $('#stop-simulation').disabled = !running || state.liveRequestPending;
  $('#run-process').disabled = running;
  renderEquipmentName();
  $('#save').disabled = running && live.fileName === state.currentFile;
  $('#live-state').textContent = running ? `Running ${live.fileName} on ${live.equipmentName}` :
    live?.error ? `Stopped: ${live.error}` : 'Stopped';
  $('#live-rows').textContent = `${live?.rowsWritten ?? 0} rows`;
  const output = $('#live-output');
  output.classList.toggle('hidden', !live?.outputFile);
  output.title = live?.outputFile ?? '';
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

function selectedEquipment() {
  return state.equipment.find(item => item.processId === $('#run-process').value);
}

function renderEquipmentName() {
  const input = $('#equipment-name');
  const equipment = selectedEquipment();
  const locked = !equipment || !state.renameSupported ||
    (!!state.live?.running && state.live.processId === equipment.processId);
  input.disabled = locked;
  $('#rename-equipment').disabled = locked || !input.value.trim() ||
    input.value.trim() === equipment.equipmentName;
}

function renderProcesses(selected = $('#run-process').value) {
  const picker = $('#run-process');
  picker.replaceChildren(...state.equipment.map(item => new Option(
    item.equipmentName === item.processId ? item.processId : `${item.equipmentName} (${item.processId})`,
    item.processId)));
  if (state.equipment.some(item => item.processId === selected)) picker.value = selected;
  $('#equipment-name').value = selectedEquipment()?.equipmentName ?? '';
  renderEquipmentName();
}

async function loadProcesses() {
  try {
    const response = await fetch('/api/simulation/processes');
    if (!response.ok) throw new Error('Could not load processes');
    const processes = await response.json();
    // A configurator started before equipment names existed returns plain process IDs.
    state.renameSupported = processes.every(item => typeof item === 'object');
    state.equipment = processes.map(item => typeof item === 'object' ? item :
      { processId: item, equipmentName: item });
    renderProcesses();
    if (!state.renameSupported)
      message('Restart the configurator (dotnet run) to edit equipment names.', true);
  } catch (error) { message(error.message, true); }
}

async function renameEquipment() {
  const equipment = selectedEquipment();
  const name = $('#equipment-name').value.trim();
  if (!equipment || !name || name === equipment.equipmentName) return;
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(name) || name.includes('..')) {
    message('Use letters, numbers, dots, hyphens, or underscores in the equipment name.', true);
    return;
  }
  $('#rename-equipment').disabled = true;
  try {
    const response = await fetch(`/api/simulation/processes/${encodeURIComponent(equipment.processId)}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ equipmentName: name })
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.message || result.detail || 'Could not rename equipment');
    equipment.equipmentName = result.equipmentName;
    renderProcesses(equipment.processId);
    message(`${equipment.processId} equipment name saved as ${result.equipmentName}`);
  } catch (error) { message(error.message, true); renderEquipmentName(); }
}

function renderList() {
  const terms = $('#search').value.trim().toLowerCase();
  const tbody = $('#tag-list');
  tbody.replaceChildren();
  let shown = 0;
  state.config.tags.forEach((tag, index) => {
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
    source.textContent = state.config.runtimeMode === 'Realtime' ? 'OPC' :
      tag.simulationKind === 'DataFile' ? 'Data pattern' :
      tag.simulationKind === 'None' ? 'Unmapped' : tag.simulationKind;
    row.append(position, name, source);
    tbody.append(row);
  });
  $('#result-count').textContent = terms ? `${shown} matching` : `${shown} in XML order`;
  $('#tag-count').textContent = `${state.config.tags.length} tags`;
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
  state.selected = index;
  const tag = state.config.tags[index];
  $('#detail-form').classList.toggle('hidden', !tag);
  $('.detail-actions').classList.toggle('hidden', !tag);
  $('#detail-title').textContent = tag ? tag.name || 'New tag' : 'Tag details';
  $('#detail-subtitle').textContent = tag ? `Position ${index + 1} of ${state.config.tags.length}` : 'Select a tag to edit';
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
  $('#move-down').disabled = index >= state.config.tags.length - 1;
  $('#delete-tag').disabled = state.config.tags.length <= 1;
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
    state.config = await response.json();
    state.currentFile = state.config.fileName;
    history.replaceState(null, '', `?file=${encodeURIComponent(state.currentFile)}`);
    state.dirty = false;
    state.selected = 0;
    $('#file-name').textContent = state.config.fileName;
    await refreshFiles();
    const dataResponse = await fetch(`/api/simulation-files?file=${encodeURIComponent(state.currentFile)}`);
    state.dataFiles = dataResponse.ok ? await dataResponse.json() : [];
    $('#group-name').value = state.config.groupName;
    $('#opc-server').value = state.config.opcServer;
    $('#save-state').textContent = 'Saved';
    $('#search').value = '';
    renderMode();
    select(0);
    await refreshLiveStatus();
  } catch (error) { message(error.message, true); $('#save-state').textContent = 'Load failed'; }
}

function renderMode() {
  const realtime = state.config.runtimeMode === 'Realtime';
  document.querySelectorAll('[data-mode]').forEach(button => {
    const active = button.dataset.mode === state.config.runtimeMode;
    button.classList.toggle('active', active);
    button.setAttribute('aria-pressed', String(active));
  });
  $('#opc-server-field').classList.toggle('hidden', !realtime);
  $('#simulation-section').classList.toggle('hidden', realtime);
  $('.settings-grid').classList.toggle('simulation-layout', !realtime);
  $('#settings-title').textContent = realtime ? 'OPC setup' : 'Simulation setup';
  $('#settings-subtitle').textContent = realtime ? 'Server and tag addresses' : 'Recorded and generated values';
  $('#group-name-label').textContent = realtime ? 'OPC tag group' : 'Output group';
  $('#tag-section-title').textContent = realtime ? 'OPC tag' : 'Output tag';
  $('#address-label').textContent = realtime ? 'OPC address' : 'Tag identifier';
  $('#mode-note').textContent = realtime ? 'OPC acquisition is not available in the worker yet.' : '';
  renderList();
  renderLiveStatus();
}

function validate() {
  const config = state.config;
  if (!config.groupName.trim()) return 'Enter a tag group name.';
  if (!config.tags.length) return 'Add at least one tag.';
  if (config.runtimeMode === 'Realtime' && !config.opcServer.trim()) return 'Enter an OPC server.';
  for (let i = 0; i < config.tags.length; i++) {
    const tag = config.tags[i];
    if (!tag.name.trim() || !tag.address.trim()) return `Tag ${i + 1} needs a name and address.`;
    if (channelKinds[tag.simulationKind] && !tag.simulationChannel) return `Tag ${i + 1} needs a simulation channel.`;
    if (tag.simulationKind === 'DataFile' && (!tag.simulationFile || !tag.simulationColumn))
      return `Tag ${i + 1} needs a reference file and column.`;
    if (tag.simulationKind === 'Constant' && !tag.simulationValue.trim()) return `Tag ${i + 1} needs a constant value.`;
    if (tag.simulationMinSpecified && tag.simulationMaxSpecified && tag.simulationMin > tag.simulationMax)
      return `Tag ${i + 1} has a minimum above its maximum.`;
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
      body: JSON.stringify(state.config)
    });
    const result = await response.json();
    if (!response.ok) throw new Error(result.message || result.detail || 'Could not save XML');
    state.config.revision = result.revision;
    state.dirty = false;
    $('#save-state').textContent = 'Saved';
    renderLiveStatus();
    message('XML saved');
    return true;
  } catch (error) { message(error.message, true); return false; }
  finally { renderLiveStatus(); }
}

document.querySelectorAll('[data-field]').forEach(input => input.addEventListener('input', () => {
  const tag = state.config.tags[state.selected];
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

$('#group-name').addEventListener('input', event => { state.config.groupName = event.target.value; setDirty(); });
$('#opc-server').addEventListener('input', event => { state.config.opcServer = event.target.value; setDirty(); });
$('#search').addEventListener('input', renderList);
document.querySelectorAll('[data-mode]').forEach(button => button.addEventListener('click', () => {
  state.config.runtimeMode = button.dataset.mode;
  renderMode(); setDirty();
}));
$('#add-tag').addEventListener('click', () => {
  const tag = { name: '', address: '', type: 'Analog', unitText: '', format: '0.00', alarm: false,
    simulationKind: 'None', simulationChannel: '', simulationFile: '', simulationColumn: '',
    simulationMin: 0, simulationMinSpecified: false,
    simulationMax: 0, simulationMaxSpecified: false, simulationValue: '' };
  state.config.tags.push(tag);
  $('#search').value = '';
  select(state.config.tags.length - 1); setDirty();
  $('[data-field="name"]').focus();
});
function referenceTag(file, column) {
  const name = column.name.replace(/\(Y\)$/, '');
  const text = column.sample !== '' && column.sample !== 'null' && isNaN(Number(column.sample));
  return { name, address: name, type: text ? 'Text' : 'Analog', unitText: '', format: text ? '' : '0.00',
    alarm: false, simulationKind: 'DataFile', simulationChannel: '', simulationFile: file.id,
    simulationColumn: column.name, simulationMin: 0, simulationMinSpecified: false,
    simulationMax: 0, simulationMaxSpecified: false, simulationValue: '' };
}
function renderReferenceSummary() {
  const form = $('#reference-form');
  const file = state.dataFiles.find(item => item.id === form.elements.file.value);
  const existing = state.config.tags.length;
  $('#reference-summary').textContent = !file ? '' : form.elements.mode.value === 'replace'
    ? `Creates ${file.columns.length} tags in the reference column order and removes the ${existing} current tag(s). The output file will have the same columns as ${file.id}.`
    : `Adds ${file.columns.length} tags after the ${existing} current tag(s). Output columns are numbered by tag position, so they will not line up with the reference.`;
}
$('#from-reference').addEventListener('click', () => {
  if (!state.dataFiles.length) { message('No reference files were found in the data folder.', true); return; }
  const form = $('#reference-form');
  const current = state.config.tags.find(tag => tag.simulationKind === 'DataFile')?.simulationFile;
  form.elements.file.replaceChildren(...state.dataFiles.map(file =>
    new Option(`${file.id} (${file.columns.length} columns)`, file.id)));
  if (state.dataFiles.some(file => file.id === current)) form.elements.file.value = current;
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
  state.config.tags = replace ? tags : state.config.tags.concat(tags);
  $('#reference-dialog').close();
  $('#search').value = '';
  select(replace ? 0 : state.config.tags.length - tags.length); setDirty();
  message(`${tags.length} tags created from ${file.id}. Save XML to keep them.`);
});
$('#delete-tag').addEventListener('click', () => {
  if (state.config.tags.length <= 1) return;
  state.config.tags.splice(state.selected, 1);
  select(Math.min(state.selected, state.config.tags.length - 1)); setDirty();
});
function move(offset) {
  const current = state.selected, next = current + offset;
  if (next < 0 || next >= state.config.tags.length) return;
  [state.config.tags[current], state.config.tags[next]] = [state.config.tags[next], state.config.tags[current]];
  select(next); setDirty();
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
    await load(result.fileName);
    message(`${result.fileName} created. Add a tag to complete it.`);
  } catch (error) { message(error.message, true); }
  finally { button.disabled = false; }
});
$('#save').addEventListener('click', save);
$('#run-process').addEventListener('change', () => renderProcesses());
$('#equipment-name').addEventListener('input', renderEquipmentName);
$('#equipment-name').addEventListener('keydown', event => { if (event.key === 'Enter') renameEquipment(); });
$('#rename-equipment').addEventListener('click', renameEquipment);
$('#start-simulation').addEventListener('click', async () => {
  if (state.dirty && !await save()) return;
  state.liveRequestPending = true;
  renderLiveStatus();
  try {
    const response = await fetch('/api/simulation', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ fileName: state.currentFile, processId: $('#run-process').value,
        revision: state.config.revision })
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
loadProcesses();
load();
setInterval(refreshLiveStatus, 1000);

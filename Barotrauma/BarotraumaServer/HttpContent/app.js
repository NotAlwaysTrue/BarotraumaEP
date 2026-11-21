import { moduleNames, classifyTps, chartMetrics, combineChartMetrics, historySeconds, drawHistory } from './performance.js';
import { tokenEnabled, enableManagement, serverName as initialServerName } from './panel-config.js';
import { createServerManagement, isTokenRejected } from './management.js';

const byId = id => document.getElementById(id);
const tokenStorageKey = 'barotraumaPerformanceToken';
const themeStorageKey = 'barotraumaMonitorTheme';
const themeMedia = window.matchMedia('(prefers-color-scheme: dark)');

let token = '';
let busy = false;
let authenticated = false;
let lastData = null;
let history = [];
let historyLoaded = false;
let exporting = false;
let recordingState = null;
let recordingRevision = 0;
let changingRecording = false;
let recordingError = '';
let preferredTheme = null;
let activeTheme = themeMedia.matches ? 'dark' : 'light';
const managementPanel = enableManagement ? createServerManagement(
    () => tokenEnabled ? token : null,
    () => {
        forget();
        message('Invalid access token. Please try again.', true);
    },
    updateNavStatus
) : null;

try
{
    if (tokenEnabled)
    {
        token = sessionStorage.getItem(tokenStorageKey) || '';
    }
    const storedTheme = localStorage.getItem(themeStorageKey);
    if (storedTheme === 'light' || storedTheme === 'dark')
    {
        preferredTheme = storedTheme;
        activeTheme = storedTheme;
    }
}
catch
{
    // Authentication and theme selection also work without browser storage.
}

function number(value, digits = 1)
{
    return Number.isFinite(value) ? value.toFixed(digits) : '-';
}

function duration(value)
{
    const seconds = Math.max(0, Math.floor(value));
    return Math.floor(seconds / 3600) + 'h '
        + Math.floor(seconds % 3600 / 60) + 'm '
        + seconds % 60 + 's';
}

function currentPage()
{
    return enableManagement && window.location.hash === '#server-management'
        ? 'server-management' : 'dashboard';
}

function updatePage()
{
    const canViewPanel = !tokenEnabled || authenticated;
    const page = currentPage();
    const management = canViewPanel && page === 'server-management';
    byId('login').hidden = canViewPanel;
    byId('dashboard').hidden = !canViewPanel || management;
    byId('serverManagement').hidden = !management;
    byId('pageTabs').hidden = !canViewPanel || !enableManagement;
    byId('panelRefresh').hidden = !canViewPanel;
    byId('logout').hidden = !tokenEnabled || !authenticated;
    byId('shutdownServer').hidden = !canViewPanel || !enableManagement;
    managementPanel?.setVisible(management, canViewPanel);
    updateNavStatus();

    document.title = !canViewPanel || management ? 'Server Management' : 'Server Performance';

    for (const [id, tabPage] of [['dashboardTab', 'dashboard'], ['managementTab', 'server-management']])
    {
        if (!byId('pageTabs').hidden && page === tabPage)
        {
            byId(id).setAttribute('aria-current', 'page');
        }
        else
        {
            byId(id).removeAttribute('aria-current');
        }
    }
}

function applyTheme(theme, persist = false)
{
    activeTheme = theme;
    document.documentElement.dataset.theme = theme;
    const dark = theme === 'dark';
    byId('themeToggle').textContent = dark ? 'Light Mode' : 'Dark Mode';
    byId('themeToggle').setAttribute('aria-label', dark ? 'Switch to light mode' : 'Switch to dark mode');
    byId('themeToggle').setAttribute('aria-pressed', String(dark));

    if (persist)
    {
        preferredTheme = theme;
        try
        {
            localStorage.setItem(themeStorageKey, theme);
        }
        catch
        {
            // Keep the selected theme for the current page.
        }
    }
    if (lastData)
    {
        render(lastData);
    }
}

function updateNavStatus()
{
    const showManagement = !byId('serverManagement').hidden ||
        (byId('login').hidden && managementPanel?.isShuttingDown());
    byId('managementStatus').hidden = !showManagement;
    byId('status').hidden = showManagement || !byId('status').textContent;
    byId('navStatus').hidden = byId('server').hidden && byId('status').hidden && byId('managementStatus').hidden;
}

function updateServerName(value)
{
    const label = byId('server');
    const name = value || 'Barotrauma server';
    if (label.textContent !== name)
    {
        label.textContent = name;
        label.title = name;
    }
    label.hidden = false;
}

function message(value, warning = false)
{
    byId('status').textContent = value;
    byId('status').classList.toggle('warning', warning);
    updateNavStatus();
}

const performanceChart = byId('performanceChart');
const selectChartData = byId('selectChartData');
const metricPicker = byId('chartMetrics');
const metricGroupList = byId('chartMetricGroups');
let displayedMetric = chartMetrics.update;
const chartRange = byId('chartRange');
const chartRangeValue = byId('chartRangeValue');
const metricGroups = new Map();
const metricInputs = [];
for (const [key, { label, unit }] of Object.entries(chartMetrics))
{
    if (!metricGroups.has(unit))
    {
        const group = document.createElement('fieldset');
        const heading = document.createElement('legend');
        const choices = document.createElement('div');
        heading.textContent = unit;
        choices.className = 'chart-metric-options';
        group.append(heading, choices);
        metricGroupList.append(group);
        metricGroups.set(unit, choices);
    }
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.value = key;
    checkbox.checked = key === 'update';
    const choice = document.createElement('label');
    choice.append(checkbox, label);
    metricGroups.get(unit).append(choice);
    metricInputs.push(checkbox);
}

function renderChart()
{
    drawHistory(performanceChart, history, displayedMetric, chartRange.valueAsNumber);
}

function updateChartRange()
{
    const rounded = Math.round(chartRange.valueAsNumber);
    const minutes = Math.floor(rounded / 60);
    const seconds = rounded % 60;
    const rangeLabel = minutes + (minutes === 1 ? ' minute' : ' minutes')
        + (seconds ? ' ' + seconds + (seconds === 1 ? ' second' : ' seconds') : '');
    chartRangeValue.textContent = minutes + ':' + String(seconds).padStart(2, '0');
    chartRange.setAttribute('aria-valuetext', rangeLabel);
    const chartLabel = displayedMetric ? displayedMetric.label + ' over the last ' + rangeLabel : 'No chart data selected';
    performanceChart.setAttribute('aria-label', chartLabel);
    renderChart();
}

function updateChartMetric()
{
    const [primary, ...others] = metricInputs.filter(input => input.checked).map(input => input.value);
    displayedMetric = primary ? combineChartMetrics(primary, new Set(others)) : null;
    const series = displayedMetric?.series ?? [];
    byId('chartLegend').replaceChildren(...series.map(line => {
        const label = document.createElement('li');
        label.dataset.color = line.color;
        label.toggleAttribute('data-dashed', line.dash?.length > 0);
        label.textContent = line.label;
        return label;
    }));
    updateChartRange();
}

function addHistorySample(data)
{
    const previous = history.at(-1)?.uptimeSeconds;
    if (previous === data.uptimeSeconds) { return; }
    if (previous !== undefined && data.uptimeSeconds < previous)
    {
        history = [];
        historyLoaded = false;
    }
    else if (previous !== undefined && data.uptimeSeconds - previous > 3)
    {
        historyLoaded = false;
    }

    const update = data.modules.find(module => module.module === 'Update');
    history.push({
        ...data,
        averageUpdateMs: update?.averageMilliseconds
    });
    history = history.filter(sample => data.uptimeSeconds - sample.uptimeSeconds < historySeconds).slice(-historySeconds);
}

async function loadHistory(requestToken, signal)
{
    try
    {
        const response = await fetch('/api/performance/history', {
            headers: performanceHeaders(requestToken), cache: 'no-store', signal
        });
        if (!response.ok) { return; }
        const samples = await response.json();
        if (token !== requestToken) { return; }
        const merged = new Map(samples.map(sample => [sample.uptimeSeconds, sample]));
        for (const sample of history) { merged.set(sample.uptimeSeconds, sample); }
        history = [...merged.values()].sort((a, b) => a.uptimeSeconds - b.uptimeSeconds).slice(-historySeconds);
        historyLoaded = true;
        renderChart();
    }
    catch
    {
        // Live samples are still available. Retry loading history on a later poll.
    }
}

function performanceHeaders(requestToken)
{
    return tokenEnabled ? { Authorization: 'Bearer ' + requestToken } : {};
}

function renderRecording()
{
    const toggle = byId('recordPerformance');
    if (!changingRecording) { toggle.checked = recordingState?.enabled ?? false; }
    toggle.disabled = !recordingState || changingRecording;
    byId('exportRecording').disabled = exporting || !recordingState?.hasArchive;
    const status = byId('recordingStatus');
    const text = recordingError || recordingState?.errorMessage || 'Saved every 5 minutes and at server shutdown.';
    if (status.textContent !== text) { status.textContent = text; }
}

async function setRecording(enabled)
{
    const requestToken = token;
    const revision = ++recordingRevision;
    changingRecording = true;
    recordingError = '';
    renderRecording();
    try
    {
        const response = await fetch('/api/performance/recording', {
            method: 'POST',
            headers: { ...performanceHeaders(requestToken), 'Content-Type': 'application/json', 'X-Barotrauma-Panel': '1' },
            body: JSON.stringify(enabled),
            signal: AbortSignal.timeout(7000)
        });
        if (!response.ok) { throw new Error('HTTP ' + response.status); }
        const state = await response.json();
        if (revision === recordingRevision) { recordingState = state; }
    }
    catch
    {
        if (revision === recordingRevision) { recordingError = 'Could not change full performance recording.'; }
    }
    finally
    {
        if (revision === recordingRevision)
        {
            changingRecording = false;
            renderRecording();
        }
    }
}

async function exportPerformance(fullRecording)
{
    const requestToken = token;
    exporting = true;
    byId('exportPerformance').disabled = true;
    renderRecording();
    try
    {
        const path = fullRecording ? '/api/performance/recording/export' : '/api/performance/export';
        const response = await fetch(path, {
            headers: performanceHeaders(requestToken), cache: 'no-store',
            signal: fullRecording ? undefined : AbortSignal.timeout(7000)
        });
        if (!response.ok) { throw new Error('HTTP ' + response.status); }
        const contents = await response.blob();
        if (token !== requestToken) { return; }
        const url = URL.createObjectURL(contents);
        const link = document.createElement('a');
        link.href = url;
        const prefix = fullRecording ? 'performance-recording-' : 'performance-';
        link.download = prefix + new Date().toISOString().replace(/[:.]/g, '-') + '.csv';
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
    catch
    {
        if (token === requestToken) { message('Could not export performance history. Try again.', true); }
    }
    finally
    {
        exporting = false;
        byId('exportPerformance').disabled = !lastData;
        renderRecording();
    }
}

function render(data)
{
    lastData = data;
    updateServerName(data.serverName);
    updatePage();

    const state = classifyTps(data.updatesPerSecond);
    const stateLabels = {
        normal: 'Normal',
        warning: 'Below 55 TPS',
        critical: 'Below 50 TPS',
        unavailable: 'Unavailable'
    };
    byId('ratePanel').dataset.state = state;
    byId('rateState').textContent = stateLabels[state];
    byId('rate').textContent = number(data.updatesPerSecond) + ' TPS';
    byId('rateDetail').textContent = 'Target ' + data.targetUpdateRate
        + ' TPS | Network TickRate ' + data.networkTickRate;

    const status = data.roundStatus ?? (data.roundStarted ? (data.paused ? 'paused' : 'running') : 'lobby');
    const roundLabels = { lobby: 'Waiting in lobby', starting: 'Starting round', loading: 'Loading', running: 'In progress', paused: 'Paused' };
    byId('round').textContent = roundLabels[status] ?? 'Unavailable';
    const roundDuration = byId('roundDuration');
    roundDuration.hidden = !data.roundStarted;
    roundDuration.textContent = data.roundStarted ? duration(data.roundSeconds) : '';

    byId('cpu').textContent = number(data.cpuPercent) + ' %';
    byId('cpuDetail').textContent = 'Using ' + number(data.cpuCores, 2) + ' cores';
    byId('memory').textContent = data.workingSetBytes === null
        ? 'Unavailable'
        : number(data.workingSetBytes / 1048576) + ' MiB';
    byId('heap').textContent = 'Managed heap ' + number(data.managedHeapBytes / 1048576) + ' MiB';
    byId('players').textContent = String(data.onlinePlayers);
    byId('uptime').textContent = duration(data.uptimeSeconds);
    byId('gc').textContent = 'GC generations 0 / 1 / 2: ' + data.gcCollections.join(' / ');

    byId('exportPerformance').disabled = exporting;
    renderRecording();
    renderChart();
    const rows = [];
    for (const [key, label] of Object.entries(moduleNames))
    {
        const rolling = data.moduleTimings5m?.[key];
        const row = document.createElement('tr');
        const name = document.createElement('td');
        const moduleLabel = document.createElement('span');
        moduleLabel.className = 'module-label';
        if (key === 'Update')
        {
            row.className = 'total-row';
        }
        const text = document.createElement('span');
        text.textContent = label;
        moduleLabel.append(text);
        name.append(moduleLabel);
        row.append(name);

        for (const value of [
            number(rolling?.spikeMilliseconds, 3),
            number(rolling?.meanMilliseconds, 3)
        ])
        {
            const cell = document.createElement('td');
            cell.textContent = value;
            row.append(cell);
        }
        rows.push(row);
    }
    byId('modules').replaceChildren(...rows);
}

function forget()
{
    token = '';
    byId('freshness').hidden = true;
    authenticated = false;
    lastData = null;
    history = [];
    historyLoaded = false;
    recordingState = null;
    recordingRevision++;
    changingRecording = false;
    recordingError = '';
    renderRecording();
    try
    {
        sessionStorage.removeItem(tokenStorageKey);
    }
    catch
    {
        // Clearing the in-memory token still logs out this page.
    }
    byId('token').value = '';
    updatePage();
}

async function poll()
{
    if (busy || (tokenEnabled && !token))
    {
        return;
    }

    busy = true;
    const requestToken = token;
    const revision = recordingRevision;
    const readRecordingState = !changingRecording;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 5000);
    try
    {
        const response = await fetch('/api/performance', {
            headers: performanceHeaders(requestToken),
            cache: 'no-store',
            signal: controller.signal
        });
        if (token !== requestToken)
        {
            return;
        }
        if (tokenEnabled && isTokenRejected(response))
        {
            forget();
            message('Invalid access token. Please try again.', true);
            return;
        }
        if (response.status === 503)
        {
            byId('freshness').hidden = true;
            message('Waiting for a server sample. Retrying automatically.', true);
            return;
        }
        if (!response.ok)
        {
            throw new Error('HTTP ' + response.status);
        }

        const data = await response.json();
        if (token !== requestToken)
        {
            return;
        }
        const recordingHeader = response.headers.get('X-Barotrauma-Recording');
        if (recordingHeader && readRecordingState && revision === recordingRevision)
        {
            recordingState = JSON.parse(recordingHeader);
        }
        if (tokenEnabled)
        {
            authenticated = true;
            try
            {
                sessionStorage.setItem(tokenStorageKey, requestToken);
            }
            catch
            {
                // Keep the current login when browser storage is unavailable.
            }
        }
        addHistorySample(data);
        byId('freshness').hidden = true;
        render(data);
        message('Connected');
        if (!historyLoaded) { await loadHistory(requestToken, controller.signal); }
    }
    catch
    {
        if (token === requestToken)
        {
            byId('freshness').hidden = false;
            message(byId('login').hidden ? 'Disconnected' : 'Connection interrupted. Retrying.', true);
        }
    }
    finally
    {
        clearTimeout(timeout);
        busy = false;
    }
}

if (tokenEnabled)
{
    byId('form').addEventListener('submit', event => {
        event.preventDefault();
        token = byId('token').value.trim();
        message('Connecting...');
        poll();
    });
    byId('logout').addEventListener('click', async () => {
        const button = byId('logout');
        button.disabled = true;
        await managementPanel?.finishEdits();
        forget();
        button.disabled = false;
        message('Logged out.');
    });
}
byId('themeToggle').addEventListener('click', () => {
    applyTheme(activeTheme === 'dark' ? 'light' : 'dark', true);
});
byId('panelRefresh').addEventListener('click', () => {
    if (currentPage() === 'server-management') { managementPanel?.refresh(); }
    else
    {
        if (managementPanel?.isShuttingDown()) { managementPanel.refresh(); }
        poll();
    }
});
selectChartData.addEventListener('click', () => {
    metricPicker.hidden = !metricPicker.hidden;
    selectChartData.setAttribute('aria-expanded', String(!metricPicker.hidden));
});
metricGroupList.addEventListener('change', event => {
    const choice = event.target;
    if (choice.checked)
    {
        const unit = chartMetrics[choice.value].unit;
        for (const input of metricInputs)
        {
            if (chartMetrics[input.value].unit !== unit) { input.checked = false; }
        }
    }
    updateChartMetric();
});
chartRange.addEventListener('input', updateChartRange);
byId('recordPerformance').addEventListener('change', event => setRecording(event.target.checked));
byId('exportPerformance').addEventListener('click', () => exportPerformance(false));
byId('exportRecording').addEventListener('click', () => exportPerformance(true));
new ResizeObserver(renderChart).observe(performanceChart);
window.addEventListener('hashchange', () => {
    updatePage();
    window.scrollTo(0, 0);
});
themeMedia.addEventListener('change', event => {
    if (!preferredTheme)
    {
        applyTheme(event.matches ? 'dark' : 'light');
    }
});

updateServerName(initialServerName);
applyTheme(activeTheme);
updateChartMetric();
updatePage();
setInterval(poll, 1000);
if (!tokenEnabled)
{
    poll();
}
else if (token)
{
    message('Reconnecting...');
    poll();
}

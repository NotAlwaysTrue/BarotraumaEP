function createPermissionPicker(input, list, onChange)
{
    let choices = [];
    let matches = [];
    let selected = null;
    let activeIndex = -1;

    function close()
    {
        list.hidden = true;
        input.setAttribute('aria-expanded', 'false');
        input.removeAttribute('aria-activedescendant');
    }

    function render()
    {
        const query = selected ? '' : input.value.trim().toLowerCase();
        matches = choices.filter(choice => choice.label.toLowerCase().includes(query));
        activeIndex = -1;
        input.removeAttribute('aria-activedescendant');
        list.replaceChildren(...matches.map(({ label }, index) => {
            const option = document.createElement('li');
            option.id = list.id + '-' + index;
            option.dataset.index = String(index);
            option.setAttribute('role', 'option');
            option.setAttribute('aria-selected', 'false');
            option.textContent = label;
            return option;
        }));
        if (!matches.length)
        {
            const empty = document.createElement('li');
            empty.setAttribute('role', 'option');
            empty.setAttribute('aria-disabled', 'true');
            empty.textContent = 'No matching options';
            list.append(empty);
        }
        list.scrollTop = 0;
    }

    function open()
    {
        if (input.disabled || !list.hidden) { return; }
        list.hidden = false;
        input.setAttribute('aria-expanded', 'true');
        render();
    }

    function choose(index)
    {
        selected = matches[index];
        input.value = selected.label;
        close();
        onChange();
    }

    input.addEventListener('focus', open);
    input.addEventListener('click', open);
    input.addEventListener('blur', close);
    input.addEventListener('input', () => {
        selected = null;
        if (list.hidden) { open(); }
        else { render(); }
        onChange();
    });
    list.addEventListener('mousedown', event => {
        if (event.target.closest('[data-index]')) { event.preventDefault(); }
    });
    list.addEventListener('click', event => {
        const option = event.target.closest('[data-index]');
        if (option) { choose(Number(option.dataset.index)); }
    });
    input.addEventListener('keydown', event => {
        if (event.isComposing) { return; }
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp')
        {
            event.preventDefault();
            open();
            if (!matches.length) { return; }
            if (activeIndex >= 0) { list.children[activeIndex].setAttribute('aria-selected', 'false'); }
            activeIndex = event.key === 'ArrowDown' ? Math.min(activeIndex + 1, matches.length - 1)
                : activeIndex < 0 ? matches.length - 1 : Math.max(activeIndex - 1, 0);
            const option = list.children[activeIndex];
            option.setAttribute('aria-selected', 'true');
            input.setAttribute('aria-activedescendant', option.id);
            option.scrollIntoView({ block: 'nearest' });
        }
        else if (!list.hidden && event.key === 'Enter')
        {
            event.preventDefault();
            if (matches.length) { choose(Math.max(activeIndex, 0)); }
        }
        else if (!list.hidden && event.key === 'Escape')
        {
            event.preventDefault();
            close();
        }
    });

    return {
        get count() { return choices.length; },
        get value() { return selected?.value; },
        setDisabled(disabled) {
            input.disabled = disabled;
            if (disabled) { close(); }
        },
        setChoices(available) {
            choices = available;
            if (selected && !choices.some(choice => choice.value === selected.value))
            {
                selected = null;
                input.value = '';
            }
            if (!list.hidden) { render(); }
        },
        reset() {
            selected = null;
            input.value = '';
            close();
        }
    };
}

export function isTokenRejected(response)
{
    return response.status === 418 && response.headers.get('WWW-Authenticate') === 'Bearer';
}

export function createServerManagement(getToken, onUnauthorized, onShutdownStatusChanged)
{
    const byId = id => document.getElementById(id);
    const players = byId('managementPlayers');
    const bans = byId('managementBans');
    const banListDialog = byId('banListDialog');
    const openBanList = byId('openBanList');
    const openServerLogs = byId('openServerLogs');
    const logsDialog = byId('serverLogsDialog');
    const logFile = byId('serverLogFile');
    const logStatus = byId('serverLogStatus');
    const logContents = byId('serverLogContents');
    const status = byId('managementStatus');
    const roundStatus = byId('managementRoundStatus');
    const startRound = byId('startRound');
    const endRound = byId('endRound');
    const saveAndEndRound = byId('saveAndEndRound');
    const shutdownServer = byId('shutdownServer');
    const dialog = byId('managementDialog');
    const dialogForm = byId('managementDialogForm');
    const dialogError = byId('managementDialogError');
    const dialogSubmit = byId('managementDialogSubmit');
    const permissionPreset = byId('permissionPreset');
    const permissionPicker = createPermissionPicker(byId('permissionChoice'), byId('permissionSuggestions'), updatePermissionControls);
    const commandPicker = createPermissionPicker(byId('commandChoice'), byId('commandSuggestions'), updatePermissionControls);
    const addPermission = byId('addPermission');
    const addCommandPermission = byId('addCommandPermission');
    const addAllPermissions = byId('addAllPermissions');
    const clearAllPermissions = byId('clearAllPermissions');
    const grantedPermissions = byId('grantedPermissions');
    const grantedCommands = byId('grantedCommands');
    const roundForm = byId('roundSettingsForm');
    const openRoundSetup = byId('openRoundSetup');
    const openRespawnSettings = byId('openRespawnSettings');
    const mode = roundForm.elements.namedItem('mode');
    const difficulty = roundForm.elements.namedItem('difficulty');
    const difficultyNumber = byId('roundDifficultyNumber');
    const selectRoundOption = byId('selectRoundOption');
    const missionDialog = byId('missionDialog');
    const missionChoices = byId('missionChoices');
    const campaignDialog = byId('campaignDialog');
    const campaignSave = byId('campaignSave');
    const campaignStatus = byId('campaignStatus');
    const campaignSaveForm = byId('campaignSaveForm');
    const openCampaignSettings = byId('openCampaignSettings');
    const showNewCampaign = byId('showNewCampaign');
    const newCampaignPanel = byId('newCampaignPanel');
    const newCampaignForm = byId('newCampaignForm');
    const newCampaignName = byId('newCampaignName');
    const newCampaignSubmarine = byId('newCampaignSubmarine');
    const newCampaignSeed = byId('newCampaignSeed');
    const newCampaignSettingsMount = byId('newCampaignSettingsMount');
    const createCampaignSave = byId('createCampaignSave');
    const loadCampaignSave = byId('loadCampaignSave');
    const closeCampaignDialog = byId('closeCampaignDialog');
    const miscForm = byId('miscSettingsForm');
    const networkForm = byId('networkSettingsForm');
    const miscForms = [miscForm, byId('votingSettingsForm'), byId('gameplaySettingsForm'), networkForm];
    const moreSettings = byId('moreSettings');
    const moreSettingsToggle = byId('moreSettingsToggle');
    const lobbyForm = byId('lobbySettingsForm');
    const openNetworking = byId('openNetworking');
    const openLogSettings = byId('openLogSettings');
    const openKarmaSettings = byId('openKarmaSettings');
    const karmaSwitchMount = byId('karmaSwitchMount');
    const advancedDialog = byId('advancedSettingsDialog');
    const advancedContent = byId('advancedSettingsContent');
    const serverSettingsStatus = byId('serverSettingsStatus');
    const dialogSettingsMount = byId('dialogSettingsMount');
    const advancedForm = byId('advancedSettingsForm');
    const advancedGroups = byId('advancedSettingsGroups');
    const advancedStatus = byId('advancedSettingsStatus');
    const cargoSettings = byId('extraCargoSettings');
    const cargoChoice = byId('extraCargoChoice');
    const addCargo = byId('addExtraCargo');
    const cargoItems = byId('extraCargoItems');
    const serverPassword = byId('serverPassword');
    const advancedFields = new Map();
    const advancedSections = new Map();
    const lobbySections = new Map();
    const lobbyOnlySettings = new Set();
    const cargoRows = new Map();
    let cargoCatalog = [];
    let settingsView = null;
    let latestAdvancedFields = null;
    const inlineSettings = new Map([
        ['Basic server settings', byId('basicAdvancedSettings')],
        ['Voting settings', byId('votingAdvancedSettings')],
        ['Gameplay settings', byId('gameplayAdvancedSettings')]
    ]);
    const roundSettingsTitles = { roundsetup: 'Round setup', respawn: 'Respawn' };
    const settingsTitles = {
        ...roundSettingsTitles,
        networking: 'Network settings', logs: 'Log settings', karma: 'Karma settings',
        pvp: 'PvP settings', multiplayercampaign: 'Campaign settings'
    };
    const campaignDefaultsGroup = 'Campaign Settings';
    const campaignRestrictedRoundSettings = ['seed', 'difficulty', 'randomizeSeed', 'autoRestart', 'autoRestartInterval'];
    const campaignRestrictedLobbySettings = ['Biome', 'SelectedOutpostName'];
    const loadedCampaignRestrictedLobbySettings = ['BotCount', 'MaxBotCount', 'BotSpawnMode'];
    const voteRatioFields = new Set(['EndVoteRequiredRatio', 'VoteRequiredRatio', 'KickVoteRequiredRatio']);
    const roundEdits = new Map();
    const miscEdits = new Map();
    const advancedEdits = new Map();
    let permissionOptions = null;
    const failedSettings = new Set();
    let authorized = false;
    let visible = false;
    let state = null;
    let activeRequest = null;
    let mutating = false;
    let shuttingDown = false;
    let connectionError = false;
    let dialogAction = null;
    let saveTimer = null;
    let saveRequested = false;
    let logRequest = null;
    let mutationDone = Promise.resolve();

    function report(message, warning = false)
    {
        if (status.textContent !== message) { status.textContent = message; }
        status.classList.toggle('warning', warning);
    }

    function dangerButton(label, run)
    {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'danger';
        button.textContent = label;
        button.addEventListener('click', run);
        return button;
    }

    function rowsOrEmpty(rows, count, message)
    {
        if (rows.length) { return rows; }
        const row = document.createElement('tr');
        const cell = row.insertCell();
        cell.colSpan = count;
        cell.className = 'muted';
        cell.textContent = message;
        return [row];
    }

    function switchLabel(text, value, checked, name = '')
    {
        const label = document.createElement('label');
        const input = document.createElement('input');
        input.type = 'checkbox';
        input.setAttribute('role', 'switch');
        input.name = name;
        input.value = value;
        input.defaultChecked = checked;
        label.append(input, text);
        return label;
    }

    function renderPlayers(entries)
    {
        byId('playerCount').textContent = '(' + entries.length + ')';
        const rows = entries.map(player => {
            const row = document.createElement('tr');
            row.insertCell().textContent = player.name;
            row.insertCell().textContent = player.inGame ? 'In game' : 'Connected';
            const actions = row.insertCell();
            if (player.owner)
            {
                actions.textContent = 'Server owner';
                return row;
            }
            const buttons = document.createElement('div');
            buttons.className = 'management-actions';
            const kick = dangerButton('Kick', () => editPlayer('kick', player));
            const ban = dangerButton('Ban', () => editPlayer('ban', player));
            ban.disabled = !player.canBan;
            if (ban.disabled) { ban.title = 'Local player without a bannable account'; }
            const permissions = dangerButton('Set permissions', () => editPlayer('permissions', player));
            buttons.append(kick, ban, permissions);
            actions.append(buttons);
            return row;
        });
        players.replaceChildren(...rowsOrEmpty(rows, 3, 'No connected players.'));
    }

    function renderBans(entries)
    {
        byId('banCount').textContent = '(' + entries.length + ')';
        const rows = entries.map(ban => {
            const row = document.createElement('tr');
            row.insertCell().textContent = ban.name;
            row.insertCell().textContent = ban.reason || '-';
            row.insertCell().textContent = ban.expiresAtUtc ? new Date(ban.expiresAtUtc).toLocaleString() : 'Permanent';
            row.insertCell().append(dangerButton('Unban', () => openDialog(
                { action: 'unban', banId: ban.id }, 'Unban ' + ban.name, 'Unban',
                'This removes the selected ban entry.'
            )));
            return row;
        });
        bans.replaceChildren(...rowsOrEmpty(rows, 4, 'No active bans.'));
    }

    function setOptions(select, options)
    {
        const selected = select.value;
        select.replaceChildren(...options.map(({ value, label }) => new Option(label, value)));
        select.value = selected;
    }

    function setFields(form, values, edits)
    {
        for (const [name, value] of Object.entries(values))
        {
            if (edits.has(name)) { continue; }
            const field = form.elements.namedItem(name);
            if (!field) { continue; }
            if (field.type === 'checkbox') { field.checked = value; }
            else { field.value = value ?? ''; }
        }
    }

    function roundSettingAvailable(name, selectedMode, campaignLoaded)
    {
        return selectedMode !== 'multiplayercampaign' ||
            (!campaignRestrictedRoundSettings.includes(name) && !(name === 'submarine' && campaignLoaded));
    }

    function discardUnavailableRoundEdits(selectedMode, campaignLoaded)
    {
        for (const name of roundEdits.keys())
        {
            if (!roundSettingAvailable(name, selectedMode, campaignLoaded)) { roundEdits.delete(name); }
        }
    }

    function updateControls()
    {
        const inLobby = state?.roundStatus === 'lobby';
        const campaignLoaded = state?.campaignLoaded;
        const campaignSelected = mode.value === 'multiplayercampaign';
        const modeSaved = inLobby && state.round.mode === mode.value && !roundEdits.has('mode');
        const canSelectCampaign = modeSaved && campaignSelected;
        const canSelectPvp = modeSaved && mode.value === 'pvp';
        const canSelectMissions = inLobby && mode.value === 'mission' && state.missionTypes.length > 0;
        startRound.disabled = mutating || !inLobby || roundEdits.size > 0 || (campaignSelected && !campaignLoaded);
        endRound.disabled = mutating || !['running', 'paused'].includes(state?.roundStatus);
        saveAndEndRound.disabled = mutating || !state?.canSaveAndEndRound;
        shutdownServer.disabled = !authorized || mutating || shuttingDown;
        openBanList.disabled = mutating || !state;
        openServerLogs.disabled = !state;
        moreSettingsToggle.setAttribute('aria-disabled', String(!inLobby));
        if (!inLobby) { moreSettings.open = false; }
        openRoundSetup.disabled = !inLobby;
        openRespawnSettings.disabled = !inLobby;
        openNetworking.disabled = !state;
        openLogSettings.disabled = !state;
        openKarmaSettings.disabled = !state || !latestAdvancedFields;
        selectRoundOption.disabled = !(canSelectMissions || ((canSelectCampaign || canSelectPvp) && !mutating));
        const modeLabels = { multiplayercampaign: 'Campaign settings', mission: 'Mission type settings', pvp: 'PvP settings' };
        selectRoundOption.setAttribute('aria-label', modeLabels[mode.value] ?? 'Game mode settings');
        campaignSave.disabled = mutating || !canSelectCampaign || campaignSave.options.length < 2;
        loadCampaignSave.disabled = campaignSave.disabled || !campaignSave.value;
        closeCampaignDialog.disabled = mutating;
        openCampaignSettings.disabled = !canSelectCampaign || mutating;
        showNewCampaign.disabled = !canSelectCampaign || mutating || roundEdits.size > 0;
        const savingCampaignDefaults = [...advancedEdits.keys()].some(name => name.startsWith('campaign:'));
        createCampaignSave.disabled = showNewCampaign.disabled || savingCampaignDefaults || !newCampaignName.value.trim() ||
            !newCampaignSubmarine.value || !newCampaignSeed.value.trim();
        newCampaignName.disabled = mutating || !canSelectCampaign;
        byId('cancelNewCampaign').disabled = mutating;
        byId('setServerPassword').disabled = !state || mutating || (!serverPassword.value && !state.hasPassword);
        const selectingKarmaPreset = advancedEdits.has('KarmaPreset');
        const editingKarmaPreset = [...advancedEdits].some(([name, value]) => name.startsWith('karma:') && value !== undefined);
        for (const [name, field] of advancedFields)
        {
            const unavailableInCampaign = campaignSelected && (campaignRestrictedLobbySettings.includes(name) ||
                (campaignLoaded && loadedCampaignRestrictedLobbySettings.includes(name)));
            field.input.disabled = !state || field.readOnly || (name === 'KarmaPreset' && editingKarmaPreset) ||
                (selectingKarmaPreset && name.startsWith('karma:')) || unavailableInCampaign || (field.view === 'lobby' && !inLobby);
            if (name === 'RespawnMode')
            {
                const permadeath = [...field.input.options].find(option => option.value === 'Permadeath');
                if (permadeath) { permadeath.disabled = !campaignSelected; }
            }
        }
        cargoChoice.disabled = !inLobby || !cargoCatalog.length || cargoRows.size >= state.cargoTypeLimit || cargoChoice.options.length < 2;
        addCargo.disabled = cargoChoice.disabled || !cargoChoice.value;
        for (const { quantity, remove } of cargoRows.values()) { quantity.disabled = remove.disabled = !inLobby; }
        roundForm.querySelector('fieldset').disabled = !inLobby;
        for (const name of [...campaignRestrictedRoundSettings, 'submarine'])
        {
            roundForm.elements.namedItem(name).disabled = !roundSettingAvailable(name, mode.value, campaignLoaded);
        }
        difficultyNumber.disabled = difficulty.disabled;
        for (const form of miscForms) { form.querySelector('fieldset').disabled = !state; }
        dialogSubmit.disabled = mutating || (dialogAction?.action === 'saveAndEndRound' && !state?.canSaveAndEndRound);
        byId('managementDialogCancel').disabled = mutating;
        if (dialog.open && dialogAction?.action === 'permissions') { updatePermissionControls(); }
    }

    function updateDifficulty()
    {
        difficultyNumber.value = difficulty.value;
    }

    function updateMissionSelection()
    {
        const selected = missionChoices.querySelectorAll('input:checked');
        for (const input of missionChoices.querySelectorAll('input'))
        {
            input.disabled = selected.length === 1 && input.checked;
        }
    }

    function renderCampaignSaves(saves)
    {
        campaignSave.replaceChildren(new Option('Select a save', ''),
            ...saves.map(save => new Option([
                save.name,
                save.submarine,
                save.savedAtUtc ? new Date(save.savedAtUtc).toLocaleString() : '',
                save.loaded ? 'Current campaign' : ''
            ].filter(Boolean).join(' | '), save.value)));
        campaignStatus.textContent = saves.length ? '' : 'No compatible multiplayer campaign saves found.';
        campaignStatus.classList.remove('warning');
        updateControls();
    }

    function reportAdvanced(text, warning = false)
    {
        const status = settingsView === null ? serverSettingsStatus : advancedStatus;
        if (status.textContent !== text) { status.textContent = text; }
        status.hidden = !text;
        status.classList.toggle('warning', warning);
    }

    function renderCargo()
    {
        cargoSettings.hidden = !cargoCatalog.length;
        const selected = new Map();
        const choices = [{ value: '', label: 'Select an item' }];
        for (const field of cargoCatalog)
        {
            const draft = advancedEdits.get(field.name);
            const invalidDraft = draft === undefined && advancedEdits.has(field.name) && cargoRows.has(field.name);
            if ((draft ?? field.value) > 0 || invalidDraft)
            {
                selected.set(field.name, field);
            }
            else if (field.canAdd)
            {
                choices.push({ value: field.name, label: field.label });
            }
        }
        for (const [name, entry] of cargoRows)
        {
            if (selected.has(name)) { continue; }
            entry.row.remove();
            cargoRows.delete(name);
        }
        for (const field of selected.values())
        {
            let entry = cargoRows.get(field.name);
            if (!entry)
            {
                const row = document.createElement('div');
                const name = document.createElement('span');
                const quantity = document.createElement('input');
                row.className = 'cargo-row';
                name.textContent = field.label;
                quantity.type = 'number';
                quantity.name = field.name;
                quantity.min = '1';
                quantity.max = String(field.max);
                quantity.step = '1';
                quantity.required = true;
                quantity.setAttribute('aria-label', field.label + ' quantity');
                const remove = dangerButton('Remove', () => changeCargo(field.name, 0));
                remove.setAttribute('aria-label', 'Remove ' + field.label);
                row.append(name, quantity, remove);
                cargoItems.append(row);
                entry = { row, quantity, remove };
                cargoRows.set(field.name, entry);
            }
            const value = advancedEdits.has(field.name) ? advancedEdits.get(field.name) : field.value;
            if (value !== undefined && (state.roundStatus !== 'lobby' || !entry.quantity.matches(':focus')))
            {
                entry.quantity.value = value;
            }
        }
        if (choices.length !== cargoChoice.options.length ||
            choices.some((choice, index) => choice.value !== cargoChoice.options[index].value || choice.label !== cargoChoice.options[index].text))
        {
            setOptions(cargoChoice, choices);
            if (cargoChoice.selectedIndex < 0) { cargoChoice.value = ''; }
        }
        byId('extraCargoCount').textContent = selected.size + ' / ' + state.cargoTypeLimit + ' item types';
        updateControls();
    }

    function changeCargo(name, quantity)
    {
        if (state?.roundStatus !== 'lobby') { return; }
        queueSettingEdit(advancedEdits, name, quantity);
        renderCargo();
    }

    function setAdvancedValue(entry, value)
    {
        if (entry.input.type === 'checkbox') { entry.input.checked = value; }
        else if (entry.isVoteRatio && value != null) { entry.input.value = Number((value * 100).toPrecision(9)); }
        else if (value !== undefined) { entry.input.value = value ?? ''; }
    }

    function fieldInView(field, view)
    {
        if (view === null) { return !field.mode && inlineSettings.has(field.group); }
        if (view === 'lobby')
        {
            return !field.mode && field.lobbyOnly && !Object.values(roundSettingsTitles).includes(field.group);
        }
        if (field.group === campaignDefaultsGroup) { return view === 'newcampaign'; }
        return field.mode ? field.mode === view : field.group === settingsTitles[view];
    }

    function renderAdvanced(fields)
    {
        cargoCatalog = fields.filter(field => field.name.startsWith('cargo:'));
        lobbyOnlySettings.clear();
        for (const field of fields)
        {
            if (field.lobbyOnly) { lobbyOnlySettings.add(field.name); }
            const inline = fieldInView(field, null);
            const lobby = fieldInView(field, 'lobby');
            if ((!inline && !lobby && !fieldInView(field, settingsView)) || field.name.startsWith('cargo:')) { continue; }
            const view = inline ? null : lobby ? 'lobby' : settingsView;
            let entry = advancedFields.get(field.name);
            if (!entry)
            {
                const sections = lobby ? lobbySections : advancedSections;
                if (!inline && !sections.has(field.group))
                {
                    const group = document.createElement('div');
                    const controls = document.createElement('div');
                    controls.className = 'settings-fields';
                    if (field.group !== settingsTitles[view])
                    {
                        const title = document.createElement('h3');
                        title.textContent = field.group;
                        group.className = 'settings-section';
                        controls.classList.add('settings-section-body');
                        group.append(title);
                    }
                    group.append(controls);
                    if (lobby) { lobbyForm.insertBefore(group, cargoSettings); }
                    else { advancedGroups.append(group); }
                    sections.set(field.group, { group, controls });
                }
                const controls = inline ? inlineSettings.get(field.group) : sections.get(field.group).controls;
                const label = document.createElement('label');
                const input = document.createElement(field.kind === 'select' ? 'select' : 'input');
                const isVoteRatio = voteRatioFields.has(field.name);
                const labelText = isVoteRatio ? field.label.replace(' (0-1)', ' (%)') : field.label;
                input.name = field.name;
                if (field.kind === 'switch')
                {
                    input.type = 'checkbox';
                    input.setAttribute('role', 'switch');
                    label.className = 'switch-field';
                    label.append(input, labelText);
                }
                else
                {
                    if (field.kind === 'number')
                    {
                        input.type = 'number';
                        input.required = true;
                        const scale = isVoteRatio ? 100 : 1;
                        if (field.min != null) { input.min = String(field.min * scale); }
                        if (field.max != null) { input.max = String(field.max * scale); }
                        input.step = field.step == null ? 'any' : String(field.step);
                    }
                    else
                    {
                        const choices = field.choices.slice();
                        if (!choices.some(choice => choice.value === field.value))
                        {
                            choices.unshift({ value: field.value, label: field.value || 'Unavailable' });
                        }
                        for (const choice of choices)
                        {
                            input.add(new Option(choice.label, choice.value));
                        }
                    }
                    label.append(labelText, input);
                }
                const mount = field.name === 'KarmaEnabled' ? karmaSwitchMount : controls;
                mount.append(label);
                entry = { input, view, isVoteRatio };
                advancedFields.set(field.name, entry);
            }
            entry.value = field.value;
            entry.readOnly = field.readOnly;
            const draft = advancedEdits.has(field.name);
            if (!draft || !entry.input.matches(':focus'))
            {
                setAdvancedValue(entry, draft ? advancedEdits.get(field.name) : field.value);
            }
        }
        renderCargo();
    }

    function clearDialogSettings()
    {
        for (const [name, entry] of advancedFields)
        {
            if (entry.view !== null && entry.view !== 'lobby') { advancedFields.delete(name); }
        }
        advancedSections.clear();
        advancedGroups.replaceChildren();
    }

    function showSettingsView(view)
    {
        clearDialogSettings();
        settingsView = view;
        networkForm.hidden = view !== 'networking';
        advancedDialog.classList.toggle('network-settings-dialog', view === 'networking');
        const mount = view === 'newcampaign' ? newCampaignSettingsMount : dialogSettingsMount;
        mount.append(advancedContent);
        reportAdvanced(state && !latestAdvancedFields ? 'Loading settings...' : '');
        if (state && latestAdvancedFields) { renderAdvanced(latestAdvancedFields); }
        updateControls();
    }

    function openSettings(view)
    {
        byId('advancedSettingsHeading').textContent = settingsTitles[view];
        showSettingsView(view);
        advancedDialog.showModal();
        request({ action: 'advancedSettings' });
    }

    function showCampaignCreate(show)
    {
        newCampaignPanel.hidden = !show;
        campaignSaveForm.hidden = show;
        campaignStatus.textContent = '';
        campaignStatus.classList.remove('warning');
        if (show)
        {
            newCampaignForm.reset();
            setOptions(newCampaignSubmarine, state.submarines.map(name => ({ value: name, label: name })));
            newCampaignSubmarine.value = state.round.submarine ?? '';
            if (newCampaignSubmarine.selectedIndex < 0 && newCampaignSubmarine.options.length) { newCampaignSubmarine.selectedIndex = 0; }
            newCampaignSeed.value = state.round.seed ?? '';
            showSettingsView('newcampaign');
            newCampaignName.focus();
            request({ action: 'advancedSettings' });
        }
        else
        {
            if (settingsView === 'newcampaign') { showSettingsView(null); }
            if (campaignDialog.open) { showNewCampaign.focus(); }
        }
        updateControls();
    }

    function render(result)
    {
        if (JSON.stringify(state?.players) !== JSON.stringify(result.players)) { renderPlayers(result.players); }
        if (JSON.stringify(state?.bans) !== JSON.stringify(result.bans)) { renderBans(result.bans); }
        if (JSON.stringify(state?.modes) !== JSON.stringify(result.modes))
        {
            setOptions(mode, result.modes);
        }
        if (JSON.stringify(state?.submarines) !== JSON.stringify(result.submarines))
        {
            setOptions(roundForm.elements.namedItem('submarine'), result.submarines.map(name => ({ value: name, label: name })));
        }
        if (JSON.stringify(state?.playStyles) !== JSON.stringify(result.playStyles))
        {
            setOptions(miscForm.elements.namedItem('playStyle'), result.playStyles.map(name => ({ value: name, label: name })));
        }
        const inLobby = result.roundStatus === 'lobby';
        let discardedAdvancedEdit = false;
        if (!inLobby)
        {
            roundEdits.clear();
            failedSettings.delete(roundEdits);
            for (const name of lobbyOnlySettings)
            {
                if (!advancedEdits.delete(name)) { continue; }
                discardedAdvancedEdit = true;
                const entry = advancedFields.get(name);
                if (entry) { setAdvancedValue(entry, entry.value); }
            }
        }
        miscForm.elements.namedItem('maxPlayers').max = result.maxPlayerLimit;
        discardUnavailableRoundEdits(roundEdits.get('mode') ?? result.round.mode, result.campaignLoaded);
        setFields(roundForm, result.round, roundEdits);
        for (const form of miscForms) { setFields(form, result.misc, miscEdits); }
        if (!inLobby || (!roundEdits.has('difficulty') && !difficultyNumber.matches(':focus'))) { updateDifficulty(); }
        const labels = { lobby: 'Waiting in lobby', starting: 'Starting round', loading: 'Loading', running: 'In progress', paused: 'Paused' };
        const details = [labels[result.roundStatus], result.activeMode, result.activeSubmarine];
        if (result.roundSeconds != null)
        {
            const seconds = Math.max(0, Math.floor(result.roundSeconds));
            details.push(Math.floor(seconds / 60) + ':' + String(seconds % 60).padStart(2, '0'));
        }
        if (result.autoRestartSeconds != null)
        {
            details.push('Auto-restart in ' + Math.ceil(result.autoRestartSeconds) + 's');
        }
        roundStatus.textContent = details.filter(Boolean).join(' | ');
        state = result;
        if (campaignDialog.open && (!inLobby || result.round.mode !== 'multiplayercampaign')) { campaignDialog.close(); }
        if (missionDialog.open && (!inLobby || result.round.mode !== 'mission')) { missionDialog.close(); }
        const lobbyRoundSettingsOpen = Object.hasOwn(roundSettingsTitles, settingsView);
        const modeSettingsOpen = ['pvp', 'multiplayercampaign'].includes(settingsView);
        if (advancedDialog.open && ((lobbyRoundSettingsOpen && !inLobby) ||
            (modeSettingsOpen && (!inLobby || result.round.mode !== settingsView))))
        {
            advancedDialog.close();
        }
        if (discardedAdvancedEdit)
        {
            if (cargoCatalog.length) { renderCargo(); }
            failedSettings.delete(advancedEdits);
            if (advancedEdits.size) { scheduleSave(0); }
        }
        byId('serverPasswordStatus').textContent = result.hasPassword ? 'A game password is set. Submit an empty field to clear it.' : 'No game password is set.';
        if (dialog.open && dialogAction?.action === 'permissions') { renderPermissionEditor(); }
        updateControls();
    }

    async function request(command, success, save)
    {
        const isShutdown = command?.action === 'shutdownServer';
        if (!authorized || shuttingDown || (!visible && !save && !isShutdown) || mutating || (!command && !success && activeRequest)) { return; }
        activeRequest?.abort();
        const controller = new AbortController();
        activeRequest = controller;
        mutating = Boolean(command);
        let finishMutation;
        if (mutating)
        {
            mutationDone = new Promise(resolve => { finishMutation = resolve; });
        }
        updateControls();
        const timeout = setTimeout(() => controller.abort(), 7000);
        const headers = {};
        const token = getToken();
        if (token != null) { headers.Authorization = 'Bearer ' + token; }
        if (command)
        {
            headers['Content-Type'] = 'application/json';
            headers['X-Barotrauma-Panel'] = '1';
        }
        if (save)
        {
            if (command.action === 'advancedSettings' && visible) { reportAdvanced('Saving settings...'); }
            else { report('Saving settings...'); }
        }
        try
        {
            const response = await fetch('/api/management', {
                method: command ? 'POST' : 'GET',
                body: command ? JSON.stringify(command) : undefined,
                headers,
                cache: 'no-store',
                signal: controller.signal
            });
            if (controller !== activeRequest) { return; }
            if (token != null && isTokenRejected(response))
            {
                onUnauthorized();
                return;
            }
            const result = await response.json();
            if (controller !== activeRequest) { return; }
            if (!response.ok) { throw new Error(result.errorMessage || 'Management request failed.'); }
            if (isShutdown)
            {
                if (!result.shuttingDown) { throw new Error('The server did not acknowledge shutdown.'); }
                shuttingDown = true;
                state = null;
                dialog.close();
                report(success);
                onShutdownStatusChanged();
                return;
            }
            const firstLoad = !state;
            save?.commit();
            if (result.permissionOptions) { permissionOptions = result.permissionOptions; }
            render(result);
            if (command?.action === 'campaignSaves') { renderCampaignSaves(result.campaignSaves); }
            if (['loadCampaign', 'createCampaign'].includes(command?.action) && campaignDialog.open) { campaignDialog.close(); }
            if (result.advanced)
            {
                latestAdvancedFields = result.advanced;
                if (visible) { renderAdvanced(result.advanced); }
                reportAdvanced(success || '');
            }
            if (command?.action === 'setPassword')
            {
                if (visible) { reportAdvanced(success); }
            }
            if (command && !save && dialog.open && !['permissions', 'permissionOptions'].includes(command.action)) { dialog.close(); }
            if (command?.action === 'unban' && banListDialog.open)
            {
                banListDialog.querySelector('button[type="submit"]').focus();
            }
            if (success || firstLoad || connectionError) { report(success || 'Connected'); }
            connectionError = false;
        }
        catch (error)
        {
            if (controller !== activeRequest) { return; }
            const message = error instanceof TypeError || error.name === 'AbortError' || error instanceof SyntaxError
                ? 'Connection interrupted. Refresh to check server state.' : error.message;
            save?.fail();
            if (command?.action === 'permissionOptions') { byId('permissionOptionsStatus').hidden = true; }
            if (campaignDialog.open && ['campaignSaves', 'loadCampaign', 'createCampaign'].includes(command?.action))
            {
                campaignStatus.textContent = message;
                campaignStatus.classList.add('warning');
            }
            else if (command && dialog.open && !save)
            {
                if (command.action === 'permissions') { renderPermissionEditor(); }
                dialogError.textContent = message;
                dialogError.hidden = false;
            }
            else if (state && visible && ['advancedSettings', 'setPassword'].includes(command?.action))
            {
                reportAdvanced(message, true);
            }
            else
            {
                connectionError = !command || !state;
                report(message, true);
            }
        }
        finally
        {
            clearTimeout(timeout);
            finishMutation?.();
            if (controller === activeRequest)
            {
                activeRequest = null;
                mutating = false;
                updateControls();
                if (save || saveRequested) { scheduleSave(0); }
            }
        }
    }

    function openDialog(command, title, label, prompt = '')
    {
        if (mutating || shuttingDown) { return; }
        dialogAction = command;
        dialogForm.reset();
        byId('managementDialogTitle').textContent = title;
        const description = byId('managementDialogPrompt');
        description.textContent = prompt;
        description.hidden = !prompt;
        byId('moderationReason').hidden = !['kick', 'ban'].includes(command.action);
        const banDuration = byId('banDuration');
        banDuration.hidden = command.action !== 'ban';
        banDuration.querySelector('input').disabled = banDuration.hidden;
        byId('permissionEditor').hidden = command.action !== 'permissions';
        dialog.classList.toggle('permissions-dialog', command.action === 'permissions');
        dialogError.hidden = true;
        dialogSubmit.textContent = label;
        dialogSubmit.hidden = command.action === 'permissions';
        byId('managementDialogCancel').textContent = dialogSubmit.hidden ? 'Close' : 'Cancel';
        dialog.showModal();
    }

    function editPlayer(action, player)
    {
        if (mutating || shuttingDown) { return; }
        const labels = { kick: 'Kick', ban: 'Ban', permissions: 'Set permissions for' };
        openDialog({ action, playerId: player.id }, labels[action] + ' ' + player.name,
            labels[action]);
        if (action === 'permissions')
        {
            permissionPicker.reset();
            commandPicker.reset();
            renderPermissionEditor();
            if (!permissionOptions) { request({ action: 'permissionOptions' }); }
        }
    }

    function permissionPlayer()
    {
        return state?.players.find(player => player.id === dialogAction?.playerId);
    }

    function updatePermissionControls()
    {
        const player = permissionPlayer();
        const disabled = mutating || !permissionOptions || !player;
        permissionPreset.disabled = disabled;
        permissionPicker.setDisabled(disabled || !permissionPicker.count);
        commandPicker.setDisabled(disabled || !commandPicker.count);
        addPermission.disabled = disabled || permissionPicker.value === undefined;
        addCommandPermission.disabled = disabled || commandPicker.value === undefined;
        addAllPermissions.disabled = disabled || (!permissionPicker.count && !commandPicker.count);
        clearAllPermissions.disabled = disabled || (!player.permissions && !player.consoleCommands.length);
        for (const button of dialog.querySelectorAll('.permission-list button')) { button.disabled = disabled; }
    }

    function renderPermissionList(list, entries, remove)
    {
        const scrollTop = list.scrollTop;
        list.replaceChildren(...entries.map(({ value, label }) => {
            const row = document.createElement('li');
            const name = document.createElement('span');
            name.textContent = label;
            const button = dangerButton('Remove', () => remove(value));
            button.setAttribute('aria-label', 'Remove ' + label);
            row.append(name, button);
            return row;
        }));
        list.scrollTop = scrollTop;
    }

    function renderPermissionEditor()
    {
        const player = permissionPlayer();
        const status = byId('permissionOptionsStatus');
        status.hidden = Boolean(permissionOptions);
        status.textContent = permissionOptions ? '' : 'Loading permission options...';
        if (!player) { updatePermissionControls(); return; }

        const permissions = state.permissions.map(({ name, value }) => ({ label: name.replace(/([a-z])([A-Z])/g, '$1 $2'), value }));
        const commands = new Set(player.consoleCommands);
        const presets = permissionOptions?.presets ?? [];
        setOptions(permissionPreset, [{ value: '', label: 'Custom' }, ...presets.map(preset => ({
            value: preset.value, label: preset.isCustom ? preset.label + ' (Add)' : preset.label
        }))]);
        const matchingPreset = presets.find(preset => preset.permissions === player.permissions &&
            preset.consoleCommands.length === commands.size && preset.consoleCommands.every(name => commands.has(name)));
        permissionPreset.value = matchingPreset?.value ?? '';
        permissionPicker.setChoices(permissions.filter(({ value }) => !(player.permissions & value)));
        commandPicker.setChoices((permissionOptions?.consoleCommands ?? []).filter(name => !commands.has(name))
            .map(name => ({ value: name, label: name })));
        renderPermissionList(grantedPermissions, permissions.filter(({ value }) => player.permissions & value),
            value => changePermissions('removePermission', { permissions: value }));
        renderPermissionList(grantedCommands, player.consoleCommands.map(name => ({ value: name, label: name })),
            name => changePermissions('removeCommand', { consoleCommands: [name] }));
        updatePermissionControls();
    }

    function changePermissions(permissionOperation, details)
    {
        if (!dialog.open || dialogAction?.action !== 'permissions') { return; }
        dialogError.hidden = true;
        request({ ...dialogAction, permissionOperation, ...details }, 'Permissions saved.');
    }

    function scheduleSave(delay = 350)
    {
        if (!authorized) { return; }
        saveRequested = true;
        clearTimeout(saveTimer);
        saveTimer = setTimeout(flushSettings, delay);
    }

    function flushSettings()
    {
        if (!authorized || mutating || !state) { return; }
        saveRequested = false;
        if (mode.value !== 'mission') { roundEdits.delete('missionTypes'); }
        discardUnavailableRoundEdits(mode.value, state.campaignLoaded);
        const groups = [
            { key: 'round', edits: roundEdits, allowed: state.roundStatus === 'lobby', success: 'Round settings saved.' },
            { key: 'misc', edits: miscEdits, allowed: true, success: 'Server settings saved.' },
            { key: 'advanced', edits: advancedEdits, allowed: true, success: 'Server settings saved.' }
        ];
        for (const { key, edits, allowed, success } of groups)
        {
            if (!allowed || failedSettings.has(edits)) { continue; }
            const changes = [...edits].filter(([, value]) => value !== undefined);
            if (!changes.length) { continue; }
            return request({ action: key + 'Settings', [key]: Object.fromEntries(changes) }, success, {
                commit() {
                    for (const [name, value] of changes)
                    {
                        if (edits.get(name) === value) { edits.delete(name); }
                    }
                },
                fail() {
                    if (changes.every(([name, value]) => edits.get(name) === value)) { failedSettings.add(edits); }
                }
            });
        }
    }

    async function finishEdits()
    {
        while (authorized && !shuttingDown)
        {
            clearTimeout(saveTimer);
            if (mutating) { await mutationDone; continue; }
            const pending = flushSettings();
            if (!pending) { return; }
            await pending;
        }
    }

    function trackSettings(form, edits)
    {
        form.addEventListener('submit', event => event.preventDefault());
        form.addEventListener('input', event => {
            const field = event.target;
            if (!field.name || field.matches(':disabled')) { return; }
            let value = field.type === 'checkbox' ? field.checked
                : ['number', 'range'].includes(field.type) ? field.valueAsNumber : field.value;
            if (voteRatioFields.has(field.name)) { value /= 100; }
            queueSettingEdit(edits, field.name, field.validity.valid ? value : undefined, field.validationMessage);
        });
        form.addEventListener('change', () => scheduleSave(0));
    }

    function queueSettingEdit(edits, name, value, warning)
    {
        if (name === 'KarmaPreset')
        {
            for (const [key, draft] of advancedEdits)
            {
                if (key.startsWith('karma:') && draft === undefined) { advancedEdits.delete(key); }
            }
        }
        edits.set(name, value);
        failedSettings.delete(edits);
        if (warning)
        {
            if (edits === advancedEdits) { reportAdvanced(warning, true); }
            else { report(warning, true); }
        }
        updateControls();
        scheduleSave();
    }

    async function loadServerLogs()
    {
        logRequest?.abort();
        const controller = new AbortController();
        logRequest = controller;
        const timeout = setTimeout(() => controller.abort(), 7000);
        const file = logFile.value;
        const token = getToken();
        const url = '/api/management/logs' + (file ? '?file=' + encodeURIComponent(file) : '');
        logStatus.textContent = 'Loading logs...';
        logStatus.classList.remove('warning');
        try
        {
            const response = await fetch(url, {
                headers: token != null ? { Authorization: 'Bearer ' + token } : {},
                cache: 'no-store', signal: controller.signal
            });
            if (logRequest !== controller) { return; }
            if (token != null && isTokenRejected(response))
            {
                onUnauthorized();
                return;
            }
            const data = await response.json();
            if (logRequest !== controller) { return; }
            if (!response.ok) { throw new Error(data.errorMessage || 'Could not read the server log.'); }
            if (file)
            {
                logContents.textContent = data.contents;
                logStatus.textContent = data.truncated ? 'Showing the end of this file.' : file;
            }
            else
            {
                logFile.replaceChildren(new Option('Recent log entries', ''),
                    ...data.files.map(name => new Option(name, name)));
                logContents.textContent = data.live.entries.join('\n');
                logStatus.textContent = data.live.recording ? data.live.entries.length + ' recent entries' : 'Server log recording is disabled.';
            }
            logContents.scrollTop = logContents.scrollHeight;
        }
        catch (error)
        {
            if (logRequest !== controller) { return; }
            logContents.textContent = '';
            logStatus.textContent = error.name === 'AbortError' ? 'The log request timed out.' : error.message;
            logStatus.classList.add('warning');
        }
        finally
        {
            clearTimeout(timeout);
            if (logRequest === controller) { logRequest = null; }
        }
    }

    trackSettings(roundForm, roundEdits);
    for (const form of miscForms) { trackSettings(form, miscEdits); }
    for (const mount of inlineSettings.values()) { trackSettings(mount, advancedEdits); }
    trackSettings(karmaSwitchMount, advancedEdits);
    trackSettings(advancedForm, advancedEdits);
    trackSettings(lobbyForm, advancedEdits);
    moreSettingsToggle.addEventListener('click', event => {
        if (state?.roundStatus !== 'lobby') { event.preventDefault(); }
    });
    openRoundSetup.addEventListener('click', () => openSettings('roundsetup'));
    openRespawnSettings.addEventListener('click', () => openSettings('respawn'));
    openNetworking.addEventListener('click', () => openSettings('networking'));
    openLogSettings.addEventListener('click', () => openSettings('logs'));
    openKarmaSettings.addEventListener('click', () => openSettings('karma'));
    openCampaignSettings.addEventListener('click', () => openSettings('multiplayercampaign'));
    cargoChoice.addEventListener('change', updateControls);
    addCargo.addEventListener('click', () => {
        if (!addCargo.disabled) { changeCargo(cargoChoice.value, 1); }
    });
    advancedDialog.addEventListener('close', () => {
        if (!advancedDialog.open) { showSettingsView(null); }
    });
    serverPassword.addEventListener('input', updateControls);
    byId('serverPasswordForm').addEventListener('submit', event => {
        event.preventDefault();
        if (byId('setServerPassword').disabled) { return; }
        if (!serverPassword.value)
        {
            openDialog({ action: 'setPassword', password: '' }, 'Clear the server password?', 'Clear password');
            return;
        }
        request({ action: 'setPassword', password: serverPassword.value }, 'Server password changed.');
    });
    openBanList.addEventListener('click', () => banListDialog.showModal());
    openServerLogs.addEventListener('click', () => {
        logFile.value = '';
        logsDialog.showModal();
        loadServerLogs();
    });
    logFile.addEventListener('change', loadServerLogs);
    byId('refreshServerLogs').addEventListener('click', loadServerLogs);
    logsDialog.addEventListener('close', () => {
        const previous = logRequest;
        logRequest = null;
        previous?.abort();
        logContents.textContent = '';
    });
    mode.addEventListener('input', updateControls);
    difficulty.addEventListener('input', updateDifficulty);
    difficultyNumber.addEventListener('input', () => {
        if (difficultyNumber.matches(':disabled')) { return; }
        const value = difficultyNumber.validity.valid ? difficultyNumber.valueAsNumber : undefined;
        if (value !== undefined) { difficulty.value = value; }
        queueSettingEdit(roundEdits, 'difficulty', value, difficultyNumber.validationMessage);
    });
    selectRoundOption.addEventListener('click', () => {
        if (mode.value === 'pvp')
        {
            openSettings('pvp');
            return;
        }
        if (mode.value === 'multiplayercampaign')
        {
            showCampaignCreate(false);
            campaignSave.replaceChildren(new Option('Select a save', ''));
            campaignStatus.textContent = 'Loading campaign saves...';
            campaignStatus.classList.remove('warning');
            campaignDialog.showModal();
            request({ action: 'campaignSaves' });
            return;
        }
        const selected = roundEdits.get('missionTypes') ?? state.round.missionTypes ?? [];
        missionChoices.replaceChildren(...state.missionTypes.map(({ value, label }) =>
            switchLabel(label, value, selected.includes(value))));
        updateMissionSelection();
        missionDialog.showModal();
    });
    missionChoices.addEventListener('change', () => {
        updateMissionSelection();
        const selected = Array.from(missionChoices.querySelectorAll('input:checked'), input => input.value);
        if (!selected.length || selectRoundOption.disabled || mode.value !== 'mission') { return; }
        roundEdits.set('missionTypes', selected);
        failedSettings.delete(roundEdits);
        updateControls();
        scheduleSave();
    });
    campaignSave.addEventListener('change', updateControls);
    showNewCampaign.addEventListener('click', () => showCampaignCreate(true));
    byId('cancelNewCampaign').addEventListener('click', () => showCampaignCreate(false));
    newCampaignForm.addEventListener('input', updateControls);
    closeCampaignDialog.addEventListener('click', () => campaignDialog.close());
    campaignDialog.addEventListener('close', () => {
        if (!campaignDialog.open && settingsView === 'newcampaign') { showSettingsView(null); }
    });
    campaignDialog.addEventListener('cancel', event => { if (mutating) { event.preventDefault(); } });
    campaignSaveForm.addEventListener('submit', event => {
        event.preventDefault();
        if (loadCampaignSave.disabled) { return; }
        campaignStatus.textContent = 'Loading save...';
        campaignStatus.classList.remove('warning');
        request({ action: 'loadCampaign', campaignSaveId: campaignSave.value }, 'Campaign save loaded.');
    });
    newCampaignForm.addEventListener('submit', event => {
        event.preventDefault();
        if (createCampaignSave.disabled) { return; }
        campaignStatus.textContent = 'Creating save...';
        campaignStatus.classList.remove('warning');
        request({ action: 'createCampaign', campaignSaveName: newCampaignName.value.trim(),
            campaignSubmarine: newCampaignSubmarine.value, campaignSeed: newCampaignSeed.value }, 'Campaign save created.');
    });
    permissionPreset.addEventListener('change', () => {
        if (permissionPreset.value) { changePermissions('preset', { permissionPresetId: permissionPreset.value }); }
    });
    addPermission.addEventListener('click', () => changePermissions('addPermission', { permissions: permissionPicker.value }));
    addCommandPermission.addEventListener('click', () => changePermissions('addCommand', { consoleCommands: [commandPicker.value] }));
    addAllPermissions.addEventListener('click', () => changePermissions('addAll'));
    clearAllPermissions.addEventListener('click', () => changePermissions('clearAll'));
    byId('managementDialogCancel').addEventListener('click', () => dialog.close());
    dialog.addEventListener('cancel', event => { if (mutating) { event.preventDefault(); } });
    dialogForm.addEventListener('submit', async event => {
        event.preventDefault();
        if (!dialogAction || dialogSubmit.disabled || dialogAction.action === 'permissions') { return; }
        const command = { ...dialogAction };
        if (['kick', 'ban'].includes(command.action))
        {
            command.reason = dialogForm.elements.namedItem('reason').value;
        }
        if (command.action === 'ban')
        {
            const duration = dialogForm.elements.namedItem('banMinutes');
            command.banMinutes = duration.value ? duration.valueAsNumber : null;
        }
        const messages = {
            kick: 'Player kicked.', ban: 'Player banned.', unban: 'Ban removed.',
            endRound: 'Round ended.', saveAndEndRound: 'Campaign saved and round ended.',
            setPassword: 'Server password cleared.',
            shutdownServer: 'Server shutdown requested.'
        };
        dialogError.hidden = true;
        if (command.action === 'shutdownServer')
        {
            await finishEdits();
            if (!dialog.open || dialogAction.action !== 'shutdownServer') { return; }
        }
        request(command, messages[command.action]);
    });
    startRound.addEventListener('click', () => request({ action: 'startRound' }, 'Round start requested.'));
    endRound.addEventListener('click', () => openDialog({ action: 'endRound' }, 'End the current round?', 'End round',
        'The running round will end immediately.'));
    saveAndEndRound.addEventListener('click', () => openDialog({ action: 'saveAndEndRound' }, 'Save the campaign and end the round?',
        'Save and End Round'));
    shutdownServer.addEventListener('click', () => {
        openDialog({ action: 'shutdownServer' }, 'Shut down the server?', 'Shut down server',
            'Connected players will be disconnected and this panel will go offline.');
        byId('managementDialogCancel').focus();
    });
    setInterval(() => {
        if ((!dialog.open || dialogAction?.action === 'saveAndEndRound') && !missionDialog.open && (!campaignDialog.open || advancedDialog.open))
        {
            request(latestAdvancedFields ? null : { action: 'advancedSettings' });
        }
    }, 3000);

    return {
        finishEdits,
        isShuttingDown: () => shuttingDown,
        refresh()
        {
            if (shuttingDown)
            {
                shuttingDown = false;
                updateControls();
                onShutdownStatusChanged();
            }
            failedSettings.clear();
            scheduleSave(0);
            request({ action: 'advancedSettings' }, 'Server state refreshed.');
        },
        setVisible(show, canView)
        {
            if (visible === show && authorized === canView) { return; }
            visible = show;
            authorized = canView;
            if (visible)
            {
                if (!shuttingDown)
                {
                    if (!state && !activeRequest) { report('Loading server settings...'); }
                    if (state && latestAdvancedFields) { renderAdvanced(latestAdvancedFields); }
                    request(latestAdvancedFields ? null : { action: 'advancedSettings' });
                }
                return;
            }
            for (const modal of [dialog, banListDialog, missionDialog, campaignDialog, advancedDialog, logsDialog])
            {
                if (modal.open) { modal.close(); }
            }
            if (authorized)
            {
                updateControls();
                if (saveRequested) { scheduleSave(0); }
                return;
            }
            clearTimeout(saveTimer);
            saveRequested = false;
            activeRequest?.abort();
            activeRequest = null;
            mutating = false;
            shuttingDown = false;
            connectionError = false;
            state = null;
            latestAdvancedFields = null;
            roundEdits.clear();
            miscEdits.clear();
            advancedEdits.clear();
            lobbyOnlySettings.clear();
            clearDialogSettings();
            for (const { group } of lobbySections.values()) { group.remove(); }
            lobbySections.clear();
            cargoCatalog = [];
            cargoRows.clear();
            cargoItems.replaceChildren();
            cargoSettings.hidden = true;
            for (const mount of inlineSettings.values()) { mount.replaceChildren(); }
            karmaSwitchMount.replaceChildren();
            advancedFields.clear();
            permissionOptions = null;
            failedSettings.clear();
            players.replaceChildren();
            bans.replaceChildren();
            byId('playerCount').textContent = '';
            byId('banCount').textContent = '';
            roundStatus.textContent = '';
            serverPassword.value = '';
            roundForm.reset();
            for (const form of miscForms) { form.reset(); }
            updateDifficulty();
            updateControls();
        }
    };
}

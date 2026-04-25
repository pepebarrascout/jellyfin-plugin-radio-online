/**
 * Radio Online Plugin - Configuration Page JavaScript
 * Handles tabs, config load/save, schedule management, and status polling.
 */
(function (RadioOnline) {
    'use strict';

    // Initialize namespace
    if (!window.RadioOnline) {
        window.RadioOnline = {};
        RadioOnline = window.RadioOnline;
    }

    // ── Constants ──────────────────────────────────────────────
    RadioOnline.PLUGIN_ID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
    RadioOnline.dayNames = { 1: 'Lunes', 2: 'Martes', 3: 'Miercoles', 4: 'Jueves', 5: 'Viernes' };
    RadioOnline.serverConfig = {};
    RadioOnline.playlists = [];
    RadioOnline._page = null;
    RadioOnline._navInitialized = false;
    RadioOnline._eventsBound = false;

    // ── Utility Functions ──────────────────────────────────────

    RadioOnline.escapeHtml = function (text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    };

    RadioOnline.isValidTime = function (str) {
        return /^([01]\d|2[0-3]):([0-5]\d)$/.test(str);
    };

    RadioOnline.cfgVal = function (key) {
        if (RadioOnline.serverConfig[key] !== undefined) return RadioOnline.serverConfig[key];
        var camel = key.charAt(0).toLowerCase() + key.slice(1);
        if (RadioOnline.serverConfig[camel] !== undefined) return RadioOnline.serverConfig[camel];
        return undefined;
    };

    RadioOnline.cfgSet = function (key, value) {
        RadioOnline.serverConfig[key] = value;
        var camel = key.charAt(0).toLowerCase() + key.slice(1);
        RadioOnline.serverConfig[camel] = value;
    };

    // ── Tab Management ─────────────────────────────────────────

    RadioOnline.getCurrentTab = function () {
        var hash = window.location.hash;
        var match = hash.match(/[?&]tab=([^&]*)/);
        return match ? decodeURIComponent(match[1]) : 'config';
    };

    RadioOnline.switchToTab = function (tabId) {
        var page = RadioOnline._page;
        if (!page) return;

        // Update navigation buttons
        var navContainer = page.querySelector('.localnav');
        if (navContainer) {
            var navButtons = navContainer.querySelectorAll('a[data-tab]');
            for (var i = 0; i < navButtons.length; i++) {
                var btn = navButtons[i];
                if (btn.getAttribute('data-tab') === tabId) {
                    btn.classList.add('ui-btn-active');
                } else {
                    btn.classList.remove('ui-btn-active');
                }
            }
        }

        // Update tab content visibility
        var tabContents = page.querySelectorAll('[data-tab-content]');
        for (var j = 0; j < tabContents.length; j++) {
            var content = tabContents[j];
            if (content.getAttribute('data-tab-content') === tabId) {
                content.classList.remove('hide');
            } else {
                content.classList.add('hide');
            }
        }

        // Tab-specific actions
        if (tabId === 'status') {
            RadioOnline.loadStatus();
        }
        if (tabId === 'schedule') {
            RadioOnline.loadPlaylists();
        }

        // Update URL
        var newHash = 'configurationpage?name=Radio Online' + '&tab=' + encodeURIComponent(tabId);
        window.location.hash = newHash;
    };

    RadioOnline.setupNavigation = function () {
        var page = RadioOnline._page;
        if (!page) return;

        var navContainer = page.querySelector('.localnav');
        if (!navContainer) return;

        if (RadioOnline._navInitialized) return;
        RadioOnline._navInitialized = true;

        // Set initial active tab
        var initialTab = RadioOnline.getCurrentTab();
        requestAnimationFrame(function () {
            RadioOnline.switchToTab(initialTab);
        });

        // Handle click events on tab buttons
        var navButtons = navContainer.querySelectorAll('a[data-tab]');
        for (var i = 0; i < navButtons.length; i++) {
            navButtons[i].addEventListener('click', function (e) {
                e.preventDefault();
                var tabId = this.getAttribute('data-tab');
                RadioOnline.switchToTab(tabId);
            });
        }

        // Handle browser back/forward via hashchange
        window.addEventListener('hashchange', function () {
            var currentTab = RadioOnline.getCurrentTab();
            RadioOnline.switchToTab(currentTab);
        });
    };

    // ── Configuration Load/Save ────────────────────────────────

    RadioOnline.loadConfiguration = function () {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(RadioOnline.PLUGIN_ID).then(function (config) {
            RadioOnline.serverConfig = config || {};
            RadioOnline.populateForm();
            RadioOnline.renderSchedule();
            Dashboard.hideLoadingMsg();
        }).catch(function (error) {
            console.error('[RadioOnline] Error loading config:', error);
            Dashboard.hideLoadingMsg();
        });
    };

    RadioOnline.populateForm = function () {
        var page = RadioOnline._page;
        if (!page) return;

        var el;
        el = page.querySelector('#icecastUrl');
        if (el) el.value = RadioOnline.cfgVal('IcecastUrl') || '';
        el = page.querySelector('#icecastUsername');
        if (el) el.value = RadioOnline.cfgVal('IcecastUsername') || 'source';
        el = page.querySelector('#icecastPassword');
        if (el) el.value = RadioOnline.cfgVal('IcecastPassword') || '';
        el = page.querySelector('#icecastMountPoint');
        if (el) el.value = RadioOnline.cfgVal('IcecastMountPoint') || '/radio';
        el = page.querySelector('#audioFormat');
        if (el) el.value = RadioOnline.cfgVal('AudioFormat') || 'ogg';
        el = page.querySelector('#audioBitrate');
        if (el) el.value = RadioOnline.cfgVal('AudioBitrate') || 128;
        el = page.querySelector('#streamName');
        if (el) el.value = RadioOnline.cfgVal('StreamName') || '';
        el = page.querySelector('#streamDescription');
        if (el) el.value = RadioOnline.cfgVal('StreamDescription') || '';
        el = page.querySelector('#streamGenre');
        if (el) el.value = RadioOnline.cfgVal('StreamGenre') || '';
        el = page.querySelector('#streamPublic');
        if (el) el.checked = !!(RadioOnline.cfgVal('StreamPublic'));
        el = page.querySelector('#isEnabled');
        if (el) el.checked = !!(RadioOnline.cfgVal('IsEnabled'));
        el = page.querySelector('#jellyfinUserId');
        if (el) el.value = RadioOnline.cfgVal('JellyfinUserId') || '';
    };

    RadioOnline.readForm = function () {
        var page = RadioOnline._page;
        if (!page) return;

        var el;
        el = page.querySelector('#icecastUrl');
        if (el) RadioOnline.cfgSet('IcecastUrl', el.value.trim());
        el = page.querySelector('#icecastUsername');
        if (el) RadioOnline.cfgSet('IcecastUsername', el.value.trim() || 'source');
        el = page.querySelector('#icecastPassword');
        if (el) RadioOnline.cfgSet('IcecastPassword', el.value);
        el = page.querySelector('#icecastMountPoint');
        if (el) RadioOnline.cfgSet('IcecastMountPoint', el.value.trim() || '/radio');
        el = page.querySelector('#audioFormat');
        if (el) RadioOnline.cfgSet('AudioFormat', el.value);
        el = page.querySelector('#audioBitrate');
        if (el) RadioOnline.cfgSet('AudioBitrate', parseInt(el.value, 10) || 128);
        el = page.querySelector('#streamName');
        if (el) RadioOnline.cfgSet('StreamName', el.value.trim());
        el = page.querySelector('#streamDescription');
        if (el) RadioOnline.cfgSet('StreamDescription', el.value.trim());
        el = page.querySelector('#streamGenre');
        if (el) RadioOnline.cfgSet('StreamGenre', el.value.trim());
        el = page.querySelector('#streamPublic');
        if (el) RadioOnline.cfgSet('StreamPublic', el.checked);
        el = page.querySelector('#isEnabled');
        if (el) RadioOnline.cfgSet('IsEnabled', el.checked);
        el = page.querySelector('#jellyfinUserId');
        if (el) RadioOnline.cfgSet('JellyfinUserId', el.value);
    };

    RadioOnline.saveConfiguration = function () {
        RadioOnline.readForm();
        Dashboard.showLoadingMsg();
        ApiClient.updatePluginConfiguration(RadioOnline.PLUGIN_ID, RadioOnline.serverConfig).then(function (result) {
            Dashboard.hideLoadingMsg();
            Dashboard.processPluginConfigurationUpdateResult(result);
        }).catch(function (error) {
            console.error('[RadioOnline] Error saving config:', error);
            Dashboard.hideLoadingMsg();
            Dashboard.alert({ title: 'Error', message: 'No se pudo guardar la configuracion.' });
        });
    };

    // ── Status ─────────────────────────────────────────────────

    RadioOnline.loadStatus = function () {
        var url = ApiClient.getUrl('Plugins/RadioOnline/Status');
        fetch(url, {
            headers: { 'X-Emby-Token': ApiClient.accessToken() }
        })
        .then(function (r) { return r.json(); })
        .then(function (status) { RadioOnline.updateStatusUI(status); })
        .catch(function () { RadioOnline.updateStatusUI(null); });
    };

    RadioOnline.updateStatusUI = function (status) {
        var page = RadioOnline._page;
        if (!page) return;

        var enabledEl = page.querySelector('#statusEnabled');
        var icecastEl = page.querySelector('#statusIcecast');
        var streamingEl = page.querySelector('#statusStreaming');
        var formatEl = page.querySelector('#statusFormat');
        var countEl = page.querySelector('#statusScheduleCount');

        if (status) {
            if (enabledEl) enabledEl.innerHTML = status.isEnabled
                ? '<span class="statusActive">Activado</span>'
                : '<span class="statusInactive">Desactivado</span>';
            if (icecastEl) {
                icecastEl.textContent = status.icecastUrl ? 'Configurado' : 'Sin configurar';
                icecastEl.style.color = status.icecastUrl ? '#52b54b' : '#888';
            }
            if (streamingEl) streamingEl.innerHTML = status.isStreaming
                ? '<span class="statusStreaming">En Vivo</span>'
                : '<span class="statusInactive">Inactivo</span>';
            if (formatEl) formatEl.textContent = (status.audioFormat || '--').toUpperCase() + ' / ' + (status.audioBitrate || '--') + 'kbps';
            if (countEl) countEl.textContent = status.scheduleEntriesCount || 0;
        } else {
            if (enabledEl) enabledEl.innerHTML = '<span class="statusInactive">--</span>';
            if (icecastEl) icecastEl.textContent = '--';
            if (streamingEl) streamingEl.innerHTML = '<span class="statusInactive">--</span>';
            if (formatEl) formatEl.textContent = '--';
            if (countEl) countEl.textContent = '--';
        }
    };

    // ── Users ──────────────────────────────────────────────────

    RadioOnline.loadUsers = function () {
        ApiClient.getUsers().then(function (users) {
            var page = RadioOnline._page;
            if (!page) return;

            var select = page.querySelector('#jellyfinUserId');
            if (!select) return;

            // Remove all options except first
            while (select.options.length > 1) { select.remove(1); }

            for (var i = 0; i < users.length; i++) {
                var opt = document.createElement('option');
                opt.value = users[i].Id;
                opt.textContent = users[i].Name;
                select.appendChild(opt);
            }

            var userId = RadioOnline.cfgVal('JellyfinUserId') || '';
            if (userId) { select.value = userId; }
        }).catch(function (err) {
            console.error('[RadioOnline] Error loading users:', err);
        });
    };

    // ── Playlists ──────────────────────────────────────────────

    RadioOnline.loadPlaylists = function () {
        var url = ApiClient.getUrl('Plugins/RadioOnline/Playlists');
        fetch(url, {
            headers: { 'X-Emby-Token': ApiClient.accessToken() }
        })
        .then(function (r) { return r.json(); })
        .then(function (data) {
            RadioOnline.playlists = data || [];
            RadioOnline.populatePlaylistSelect();
        })
        .catch(function (err) {
            console.error('[RadioOnline] Error loading playlists:', err);
        });
    };

    RadioOnline.populatePlaylistSelect = function () {
        var page = RadioOnline._page;
        if (!page) return;

        var select = page.querySelector('#newPlaylist');
        if (!select) return;

        while (select.options.length > 1) { select.remove(1); }

        var noMsg = page.querySelector('#noPlaylistsMsg');
        var items = RadioOnline.playlists;

        if (items.length === 0) {
            if (noMsg) noMsg.style.display = 'block';
        } else {
            if (noMsg) noMsg.style.display = 'none';
            for (var i = 0; i < items.length; i++) {
                var opt = document.createElement('option');
                opt.value = items[i].id;
                opt.textContent = items[i].name;
                select.appendChild(opt);
            }
        }
    };

    // ── Schedule Management ────────────────────────────────────

    RadioOnline.getScheduleEntries = function () {
        return RadioOnline.cfgVal('ScheduleEntries') || [];
    };

    RadioOnline.setScheduleEntries = function (entries) {
        RadioOnline.cfgSet('ScheduleEntries', entries);
    };

    RadioOnline.buildDayOptions = function (selectedDay) {
        var days = [
            { value: 1, label: 'Lunes' }, { value: 2, label: 'Martes' },
            { value: 3, label: 'Miercoles' }, { value: 4, label: 'Jueves' },
            { value: 5, label: 'Viernes' }
        ];
        var html = '';
        for (var i = 0; i < days.length; i++) {
            var d = days[i];
            html += '<option value="' + d.value + '"' + (d.value === selectedDay ? ' selected' : '') + '>' + d.label + '</option>';
        }
        return html;
    };

    RadioOnline.buildPlaylistOptionsHtml = function (selectedId) {
        var html = '';
        for (var i = 0; i < RadioOnline.playlists.length; i++) {
            var p = RadioOnline.playlists[i];
            html += '<option value="' + p.id + '"' + (p.id === selectedId ? ' selected' : '') + '>' + RadioOnline.escapeHtml(p.name) + '</option>';
        }
        return html;
    };

    RadioOnline.renderSchedule = function () {
        var page = RadioOnline._page;
        if (!page) return;

        var entries = RadioOnline.getScheduleEntries();
        var body = page.querySelector('#scheduleBody');
        var table = page.querySelector('#scheduleTable');
        var emptyMsg = page.querySelector('#emptyScheduleMsg');

        if (!body) return;

        body.innerHTML = '';

        if (!entries || entries.length === 0) {
            if (table) table.style.display = 'none';
            if (emptyMsg) emptyMsg.style.display = 'block';
            return;
        }

        if (table) table.style.display = 'table';
        if (emptyMsg) emptyMsg.style.display = 'none';

        // Sort by day then start time
        var sorted = [];
        for (var i = 0; i < entries.length; i++) {
            sorted.push({ entry: entries[i], index: i });
        }
        sorted.sort(function (a, b) {
            var dA = a.entry.DayOfWeek !== undefined ? a.entry.DayOfWeek : (a.entry.dayOfWeek || 1);
            var dB = b.entry.DayOfWeek !== undefined ? b.entry.DayOfWeek : (b.entry.dayOfWeek || 1);
            if (dA !== dB) return dA - dB;
            var sA = a.entry.StartTime || a.entry.startTime || '00:00';
            var sB = b.entry.StartTime || b.entry.startTime || '00:00';
            return sA.localeCompare(sB);
        });

        for (var s = 0; s < sorted.length; s++) {
            var item = sorted[s];
            var entry = item.entry;
            var idx = item.index;
            var day = entry.DayOfWeek !== undefined ? entry.DayOfWeek : entry.dayOfWeek;
            var startTime = entry.StartTime || entry.startTime || '';
            var endTime = entry.EndTime || entry.endTime || '';
            var playlistId = entry.PlaylistId || entry.playlistId || '';
            var displayName = entry.DisplayName || entry.displayName || '';
            var isEnabled = entry.IsEnabled !== undefined ? entry.IsEnabled : (entry.isEnabled !== undefined ? entry.isEnabled : true);

            var tr = document.createElement('tr');
            if (!isEnabled) { tr.className = 'disabled-row'; }

            tr.innerHTML =
                '<td>' + (idx + 1) + '</td>' +
                '<td><select class="tbl-day-select" data-idx="' + idx + '" data-field="DayOfWeek">' + RadioOnline.buildDayOptions(day) + '</select></td>' +
                '<td><input type="text" value="' + RadioOnline.escapeHtml(startTime) + '" class="tbl-time-input" data-idx="' + idx + '" data-field="StartTime" placeholder="HH:mm" /></td>' +
                '<td><input type="text" value="' + RadioOnline.escapeHtml(endTime) + '" class="tbl-time-input" data-idx="' + idx + '" data-field="EndTime" placeholder="HH:mm" /></td>' +
                '<td><select class="tbl-playlist-select" data-idx="' + idx + '" data-field="PlaylistId"><option value="">-- Aleatoria --</option>' + RadioOnline.buildPlaylistOptionsHtml(playlistId) + '</select></td>' +
                '<td><input type="text" value="' + RadioOnline.escapeHtml(displayName) + '" class="tbl-name-input" data-idx="' + idx + '" data-field="DisplayName" /></td>' +
                '<td style="text-align:center;"><input type="checkbox" ' + (isEnabled ? 'checked' : '') + ' class="tbl-enabled-check" data-idx="' + idx + '" /></td>' +
                '<td><button type="button" is="emby-button" class="raised btn-danger tbl-remove-btn" data-idx="' + idx + '"><span>Borrar</span></button></td>';

            body.appendChild(tr);
        }
    };

    RadioOnline.toggleAddForm = function () {
        var page = RadioOnline._page;
        if (!page) return;
        var form = page.querySelector('#addScheduleForm');
        if (form) {
            form.style.display = form.style.display === 'none' ? 'block' : 'none';
        }
        var msg = page.querySelector('#scheduleValidationMsg');
        if (msg) msg.style.display = 'none';
    };

    RadioOnline.addScheduleEntry = function () {
        var page = RadioOnline._page;
        if (!page) return;

        var day = parseInt(page.querySelector('#newDay').value, 10);
        var startTime = page.querySelector('#newStartTime').value.trim();
        var endTime = page.querySelector('#newEndTime').value.trim();
        var playlistId = page.querySelector('#newPlaylist').value;
        var displayName = page.querySelector('#newDisplayName').value.trim();
        var msgEl = page.querySelector('#scheduleValidationMsg');

        if (!startTime || !endTime) {
            if (msgEl) { msgEl.style.display = 'block'; msgEl.textContent = 'Las horas de inicio y fin son requeridas (HH:mm).'; }
            return;
        }
        if (!RadioOnline.isValidTime(startTime)) {
            if (msgEl) { msgEl.style.display = 'block'; msgEl.textContent = 'Hora de inicio invalida. Usa formato HH:mm.'; }
            return;
        }
        if (!RadioOnline.isValidTime(endTime)) {
            if (msgEl) { msgEl.style.display = 'block'; msgEl.textContent = 'Hora de fin invalida. Usa formato HH:mm.'; }
            return;
        }
        if (startTime >= endTime) {
            if (msgEl) { msgEl.style.display = 'block'; msgEl.textContent = 'La hora de fin debe ser despues de la hora de inicio.'; }
            return;
        }

        var name = displayName || (RadioOnline.dayNames[day] + ' ' + startTime + '-' + endTime);

        var entry = {
            DayOfWeek: day, dayOfWeek: day,
            StartTime: startTime, startTime: startTime,
            EndTime: endTime, endTime: endTime,
            PlaylistId: playlistId || '', playlistId: playlistId || '',
            DisplayName: name, displayName: name,
            IsEnabled: true, isEnabled: true
        };

        var entries = RadioOnline.getScheduleEntries();
        entries.push(entry);
        RadioOnline.setScheduleEntries(entries);

        RadioOnline.renderSchedule();
        RadioOnline.toggleAddForm();

        // Clear form
        var el;
        el = page.querySelector('#newStartTime'); if (el) el.value = '';
        el = page.querySelector('#newEndTime'); if (el) el.value = '';
        el = page.querySelector('#newDisplayName'); if (el) el.value = '';
        el = page.querySelector('#newPlaylist'); if (el) el.value = '';
    };

    // ── CSS Injection ──────────────────────────────────────────

    RadioOnline.applyCustomStyles = function () {
        if (document.getElementById('radioonline-custom-styles')) return;

        var style = document.createElement('style');
        style.id = 'radioonline-custom-styles';
        style.textContent = '\
            .RadioOnlineConfigPage button[is="emby-button"].raised {\
                background: #303030; color: #fff; border: 1px solid #444;\
                border-radius: 4px; padding: 0.6em 1.2em; font-size: inherit;\
                cursor: pointer; vertical-align: middle;\
            }\
            .RadioOnlineConfigPage button[is="emby-button"].raised:hover {\
                background: #404040; border-color: #555;\
            }\
            .RadioOnlineConfigPage button[is="emby-button"].raised.button-submit {\
                background: #00a4dc; color: #fff; border: 1px solid #00a4dc;\
            }\
            .RadioOnlineConfigPage button[is="emby-button"].raised.button-submit:hover {\
                background: #1db5eb;\
            }\
            .RadioOnlineConfigPage button[is="emby-button"].raised.btn-danger {\
                background: #c0392b; color: #fff; border: 1px solid #e74c3c;\
            }\
            .RadioOnlineConfigPage button[is="emby-button"].raised.btn-danger:hover {\
                background: #e74c3c;\
            }\
            .RadioOnlineConfigPage .selectContainer select {\
                background: #101010 !important; color: #ccc !important;\
                border: 1px solid #444; border-radius: 4px;\
                padding: 0.5em 0.7em; width: 100%; font-size: inherit;\
            }\
            .RadioOnlineConfigPage .selectContainer select option {\
                background: #1a1a1a; color: #ccc; padding: 0.4em 0.5em;\
            }\
            .RadioOnlineConfigPage .checkboxContainer label {\
                display: flex; align-items: center; gap: 0.6em;\
                cursor: pointer; padding: 0.4em 0; color: inherit;\
            }\
            .RadioOnlineConfigPage .checkboxContainer input[type="checkbox"] {\
                width: 20px; height: 20px; min-width: 20px;\
                accent-color: #00a4dc; cursor: pointer; flex-shrink: 0;\
            }\
            .statusGrid {\
                display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));\
                gap: 0.8em; margin-bottom: 1.5em;\
            }\
            .statusCard {\
                background: #1a1a1a; padding: 0.8em 1em;\
                border-radius: 4px; border: 1px solid #333;\
            }\
            .statusCardLabel {\
                font-size: 0.75em; color: #888; text-transform: uppercase; letter-spacing: 0.5px;\
            }\
            .statusCardValue { font-size: 1.3em; font-weight: 600; margin-top: 0.2em; }\
            .statusActive { color: #52b54b; }\
            .statusInactive { color: #888; }\
            .statusStreaming { color: #00a4dc; }\
            .scheduleTable {\
                width: 100%; border-collapse: collapse; margin-top: 0.8em;\
            }\
            .scheduleTable th {\
                text-align: left; padding: 0.6em 0.8em; background: #1a1a1a;\
                border: 1px solid #333; font-size: 0.85em; color: #888; font-weight: 500;\
            }\
            .scheduleTable td {\
                padding: 0.5em 0.6em; border: 1px solid #333; font-size: 0.9em; vertical-align: middle;\
            }\
            .scheduleTable input, .scheduleTable select {\
                background: #101010; color: #ccc; border: 1px solid #444;\
                border-radius: 3px; padding: 0.3em 0.5em; width: 100%; font-size: 0.9em;\
            }\
            .scheduleTable .disabled-row { opacity: 0.4; }\
            .add-schedule-form {\
                background: #1a1a1a; padding: 1em; border-radius: 4px;\
                margin-top: 1em; display: none; border: 1px solid #333;\
            }\
            .add-schedule-form h3 { margin-top: 0; font-size: 1.1em; }\
            .sectionHeader {\
                font-size: 1.2em; font-weight: 600; margin: 1.5em 0 0.8em 0;\
                padding-bottom: 0.5em; border-bottom: 1px solid #333; color: #ddd;\
            }\
            .inlineMsg {\
                padding: 0.5em 0.8em; border-radius: 4px; margin: 0.5em 0; font-size: 0.9em;\
            }\
            .inlineMsg.info { background: #1a2a3a; border-left: 4px solid #2196F3; color: #64b5f6; }\
            .inlineMsg.error { background: #3a1a1a; border-left: 4px solid #f44336; color: #ef9a9a; }\
        ';
        document.head.appendChild(style);
    };

    // ── Event Binding ──────────────────────────────────────────

    RadioOnline.setupEventListeners = function () {
        var page = RadioOnline._page;
        if (!page || RadioOnline._eventsBound) return;
        RadioOnline._eventsBound = true;

        // Save config button
        var btnSaveConfig = page.querySelector('#btnSaveConfig');
        if (btnSaveConfig) {
            btnSaveConfig.addEventListener('click', function () { RadioOnline.saveConfiguration(); });
        }

        // Save schedule button
        var btnSaveScheduleConfig = page.querySelector('#btnSaveScheduleConfig');
        if (btnSaveScheduleConfig) {
            btnSaveScheduleConfig.addEventListener('click', function () { RadioOnline.saveConfiguration(); });
        }

        // Add schedule button
        var btnAddSchedule = page.querySelector('#btnAddSchedule');
        if (btnAddSchedule) {
            btnAddSchedule.addEventListener('click', function () { RadioOnline.toggleAddForm(); });
        }

        // Cancel schedule
        var btnCancelSchedule = page.querySelector('#btnCancelSchedule');
        if (btnCancelSchedule) {
            btnCancelSchedule.addEventListener('click', function () { RadioOnline.toggleAddForm(); });
        }

        // Save new schedule entry
        var btnSaveSchedule = page.querySelector('#btnSaveSchedule');
        if (btnSaveSchedule) {
            btnSaveSchedule.addEventListener('click', function () { RadioOnline.addScheduleEntry(); });
        }

        // Refresh status
        var btnRefreshStatus = page.querySelector('#btnRefreshStatus');
        if (btnRefreshStatus) {
            btnRefreshStatus.addEventListener('click', function () { RadioOnline.loadStatus(); });
        }

        // Schedule table: change events
        var scheduleBody = page.querySelector('#scheduleBody');
        if (scheduleBody) {
            scheduleBody.addEventListener('change', function (e) {
                var target = e.target;
                var idx = parseInt(target.getAttribute('data-idx'), 10);
                var field = target.getAttribute('data-field');
                if (isNaN(idx) || !field) return;

                var entries = RadioOnline.getScheduleEntries();
                if (!entries[idx]) return;

                if (field === 'DayOfWeek') {
                    var val = parseInt(target.value, 10);
                    entries[idx].DayOfWeek = val;
                    entries[idx].dayOfWeek = val;
                } else if (field === 'PlaylistId') {
                    entries[idx].PlaylistId = target.value;
                    entries[idx].playlistId = target.value;
                }
                RadioOnline.setScheduleEntries(entries);
            });

            scheduleBody.addEventListener('input', function (e) {
                var target = e.target;
                var idx = parseInt(target.getAttribute('data-idx'), 10);
                var field = target.getAttribute('data-field');
                if (isNaN(idx) || !field) return;

                var entries = RadioOnline.getScheduleEntries();
                if (!entries[idx]) return;

                if (field === 'StartTime') { entries[idx].StartTime = target.value; entries[idx].startTime = target.value; }
                else if (field === 'EndTime') { entries[idx].EndTime = target.value; entries[idx].endTime = target.value; }
                else if (field === 'DisplayName') { entries[idx].DisplayName = target.value; entries[idx].displayName = target.value; }
                RadioOnline.setScheduleEntries(entries);
            });

            scheduleBody.addEventListener('click', function (e) {
                var target = e.target;
                var removeBtn = target.closest('.tbl-remove-btn');
                if (removeBtn) {
                    var idx = parseInt(removeBtn.getAttribute('data-idx'), 10);
                    if (!isNaN(idx)) {
                        var entries = RadioOnline.getScheduleEntries();
                        entries.splice(idx, 1);
                        RadioOnline.setScheduleEntries(entries);
                        RadioOnline.renderSchedule();
                    }
                    return;
                }
                if (target.classList && target.classList.contains('tbl-enabled-check')) {
                    var idx2 = parseInt(target.getAttribute('data-idx'), 10);
                    if (!isNaN(idx2)) {
                        var entries2 = RadioOnline.getScheduleEntries();
                        if (entries2[idx2]) {
                            entries2[idx2].IsEnabled = target.checked;
                            entries2[idx2].isEnabled = target.checked;
                            RadioOnline.setScheduleEntries(entries2);
                            RadioOnline.renderSchedule();
                        }
                    }
                }
            });
        }
    };

    // ── Page Initialization ────────────────────────────────────

    RadioOnline.initPage = function (page) {
        RadioOnline._page = page;
        RadioOnline.applyCustomStyles();
        RadioOnline.setupEventListeners();
        RadioOnline.setupNavigation();
        RadioOnline.loadConfiguration();
        RadioOnline.loadUsers();
        RadioOnline.loadPlaylists();
    };

    // ── Auto-Initialization (multiple event listeners) ─────────

    var findAndInit = function () {
        var page = document.querySelector('.RadioOnlineConfigPage');
        if (page) {
            RadioOnline.initPage(page);
        }
    };

    // Standard Jellyfin page show
    document.addEventListener('pageshow', function (e) {
        var page = e.target ? e.target.querySelector ? e.target.querySelector('.RadioOnlineConfigPage') : null : null;
        if (page) {
            RadioOnline.initPage(page);
            return;
        }
        // Fallback: check if target itself is the page
        if (e.target && e.target.classList && e.target.classList.contains('RadioOnlineConfigPage')) {
            RadioOnline.initPage(e.target);
        }
    });

    // Plugin Pages integration
    document.addEventListener('viewshow', function (e) {
        var page = e.target ? e.target.querySelector ? e.target.querySelector('.RadioOnlineConfigPage') : null : null;
        if (page) {
            RadioOnline.initPage(page);
            return;
        }
        if (e.target && e.target.classList && e.target.classList.contains('RadioOnlineConfigPage')) {
            RadioOnline.initPage(e.target);
        }
    });

    // DOMContentLoaded fallback
    document.addEventListener('DOMContentLoaded', function () {
        setTimeout(findAndInit, 100);
    });

    // MutationObserver for dynamically inserted pages (Plugin Pages)
    var observer = new MutationObserver(function (mutations) {
        for (var i = 0; i < mutations.length; i++) {
            for (var j = 0; j < mutations[i].addedNodes.length; j++) {
                var node = mutations[i].addedNodes[j];
                if (node.nodeType === 1) {
                    if (node.classList && node.classList.contains('RadioOnlineConfigPage')) {
                        RadioOnline.initPage(node);
                    }
                    var inner = node.querySelector ? node.querySelector('.RadioOnlineConfigPage') : null;
                    if (inner) {
                        RadioOnline.initPage(inner);
                    }
                }
            }
        }
    });
    observer.observe(document.body, { childList: true, subtree: true });

})(window.RadioOnline || {});

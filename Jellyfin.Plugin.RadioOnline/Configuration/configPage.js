/**
 * Radio Online Plugin - Configuration Page JavaScript
 * Handles loading/saving configuration, schedule management, and status display.
 */

(function () {
    'use strict';

    const PLUGIN_GUID = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
    const DAY_NAMES = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
    const DAY_SHORT = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

    let currentConfig = null;
    let playlists = [];

    // ── Initialization ────────────────────────────────────────────────

    document.addEventListener('DOMContentLoaded', function () {
        Dashboard.showLoadingMsg();
        loadConfiguration();
        bindEvents();
        loadStatus();
    });

    // ── Event Bindings ────────────────────────────────────────────────

    function bindEvents() {
        document.getElementById('btnSave').addEventListener('click', saveConfiguration);
        document.getElementById('btnReload').addEventListener('click', function () {
            loadConfiguration();
            loadStatus();
        });

        // Schedule management
        document.getElementById('btnAddSchedule').addEventListener('click', toggleAddForm);
        document.getElementById('btnCancelSchedule').addEventListener('click', toggleAddForm);
        document.getElementById('btnSaveSchedule').addEventListener('click', addScheduleEntry);
    }

    // ── Configuration Load/Save ───────────────────────────────────────

    function loadConfiguration() {
        ApiClient.getPluginConfiguration(PLUGIN_GUID).then(function (config) {
            currentConfig = config;

            // Populate Icecast settings
            document.getElementById('icecastUrl').value = config.IcecastUrl || '';
            document.getElementById('icecastUsername').value = config.IcecastUsername || 'source';
            document.getElementById('icecastPassword').value = config.IcecastPassword || '';
            document.getElementById('icecastMountPoint').value = config.IcecastMountPoint || '/radio';
            document.getElementById('audioFormat').value = config.AudioFormat || 'ogg';
            document.getElementById('audioBitrate').value = config.AudioBitrate || 128;
            document.getElementById('streamName').value = config.StreamName || '';
            document.getElementById('streamDescription').value = config.StreamDescription || '';
            document.getElementById('streamGenre').value = config.StreamGenre || '';
            document.getElementById('streamPublic').checked = config.StreamPublic || false;
            document.getElementById('isEnabled').checked = config.IsEnabled || false;
            document.getElementById('jellyfinUserId').value = config.JellyfinUserId || '';

            // Load playlists and users
            loadPlaylists();
            loadUsers();

            // Render schedule
            renderSchedule(config.ScheduleEntries || []);

            Dashboard.hideLoadingMsg();
        }).catch(function (error) {
            console.error('Error loading configuration:', error);
            Dashboard.hideLoadingMsg();
        });
    }

    function saveConfiguration() {
        Dashboard.showLoadingMsg();

        if (!currentConfig) {
            Dashboard.hideLoadingMsg();
            return;
        }

        // Read values from form
        currentConfig.IcecastUrl = document.getElementById('icecastUrl').value.trim();
        currentConfig.IcecastUsername = document.getElementById('icecastUsername').value.trim() || 'source';
        currentConfig.IcecastPassword = document.getElementById('icecastPassword').value;
        currentConfig.IcecastMountPoint = document.getElementById('icecastMountPoint').value.trim() || '/radio';
        currentConfig.AudioFormat = document.getElementById('audioFormat').value;
        currentConfig.AudioBitrate = parseInt(document.getElementById('audioBitrate').value) || 128;
        currentConfig.StreamName = document.getElementById('streamName').value.trim();
        currentConfig.StreamDescription = document.getElementById('streamDescription').value.trim();
        currentConfig.StreamGenre = document.getElementById('streamGenre').value.trim();
        currentConfig.StreamPublic = document.getElementById('streamPublic').checked;
        currentConfig.IsEnabled = document.getElementById('isEnabled').checked;
        currentConfig.JellyfinUserId = document.getElementById('jellyfinUserId').value;

        ApiClient.updatePluginConfiguration(PLUGIN_GUID, currentConfig).then(function () {
            Dashboard.hideLoadingMsg();
            loadStatus();
            Dashboard.alert({ title: 'Saved', message: 'Radio Online configuration saved successfully.' });
        }).catch(function (error) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert({ title: 'Error', message: 'Failed to save configuration: ' + error.message });
        });
    }

    // ── Status ────────────────────────────────────────────────────────

    function loadStatus() {
        fetch(ApiClient.getUrl('Plugins/RadioOnline/Status'), {
            headers: ApiClient.getRequestHeaders()
        }).then(function (response) {
            return response.json();
        }).then(function (status) {
            updateStatusUI(status);
        }).catch(function (error) {
            console.error('Error loading status:', error);
            updateStatusUI(null);
        });
    }

    function updateStatusUI(status) {
        var enabledEl = document.getElementById('statusEnabled');
        var icecastEl = document.getElementById('statusIcecast');
        var streamingEl = document.getElementById('statusStreaming');
        var formatEl = document.getElementById('statusFormat');
        var countEl = document.getElementById('statusScheduleCount');

        if (status) {
            enabledEl.textContent = status.isEnabled ? 'Enabled' : 'Disabled';
            enabledEl.className = 'status-badge ' + (status.isEnabled ? 'status-active' : 'status-inactive');

            icecastEl.textContent = status.icecastUrl && status.icecastUrl !== '' ? 'Connected' : 'Not Configured';
            icecastEl.style.color = status.icecastUrl ? 'var(--success)' : 'var(--text-secondary)';

            streamingEl.textContent = status.isStreaming ? 'Live' : 'Idle';
            streamingEl.className = 'status-badge ' + (status.isStreaming ? 'status-streaming' : 'status-inactive');

            formatEl.textContent = (status.audioFormat || '--').toUpperCase() + ' / ' + (status.audioBitrate || '--') + 'kbps';
            countEl.textContent = status.scheduleEntriesCount || 0;
        } else {
            enabledEl.textContent = 'Unknown';
            enabledEl.className = 'status-badge status-inactive';
            icecastEl.textContent = '--';
            streamingEl.textContent = '--';
            streamingEl.className = 'status-badge status-inactive';
            formatEl.textContent = '--';
            countEl.textContent = '--';
        }
    }

    // ── Playlists ─────────────────────────────────────────────────────

    function loadPlaylists() {
        fetch(ApiClient.getUrl('Plugins/RadioOnline/Playlists'), {
            headers: ApiClient.getRequestHeaders()
        }).then(function (response) {
            return response.json();
        }).then(function (data) {
            playlists = data || [];
            populatePlaylistSelect(playlists);
        }).catch(function (error) {
            console.error('Error loading playlists:', error);
        });
    }

    function populatePlaylistSelect(items) {
        var select = document.getElementById('newPlaylist');
        // Keep the first option (Random Music)
        while (select.options.length > 1) {
            select.remove(1);
        }

        if (items.length === 0) {
            document.getElementById('noPlaylistsMsg').style.display = 'block';
        } else {
            document.getElementById('noPlaylistsMsg').style.display = 'none';
            items.forEach(function (item) {
                var opt = document.createElement('option');
                opt.value = item.id;
                opt.textContent = item.name;
                select.appendChild(opt);
            });
        }

        // Also update playlist columns in existing schedule rows
        updateSchedulePlaylistOptions(items);
    }

    function updateSchedulePlaylistOptions(items) {
        var selects = document.querySelectorAll('.schedule-playlist-select');
        selects.forEach(function (sel) {
            var currentVal = sel.value;
            while (sel.options.length > 1) {
                sel.remove(1);
            }
            items.forEach(function (item) {
                var opt = document.createElement('option');
                opt.value = item.id;
                opt.textContent = item.name;
                if (item.id === currentVal) opt.selected = true;
                sel.appendChild(opt);
            });
        });
    }

    // ── Users ─────────────────────────────────────────────────────────

    function loadUsers() {
        ApiClient.getUsers().then(function (users) {
            var select = document.getElementById('jellyfinUserId');
            while (select.options.length > 1) {
                select.remove(1);
            }

            users.forEach(function (user) {
                var opt = document.createElement('option');
                opt.value = user.Id;
                opt.textContent = user.Name;
                select.appendChild(opt);
            });

            // Set selected user
            if (currentConfig && currentConfig.JellyfinUserId) {
                select.value = currentConfig.JellyfinUserId;
            }
        }).catch(function (error) {
            console.error('Error loading users:', error);
        });
    }

    // ── Schedule Management ───────────────────────────────────────────

    function toggleAddForm() {
        var form = document.getElementById('addScheduleForm');
        form.classList.toggle('visible');
    }

    function addScheduleEntry() {
        var validationMsg = document.getElementById('scheduleValidationMsg');
        validationMsg.textContent = '';

        var day = parseInt(document.getElementById('newDay').value);
        var startTime = document.getElementById('newStartTime').value.trim();
        var endTime = document.getElementById('newEndTime').value.trim();
        var playlistId = document.getElementById('newPlaylist').value;
        var displayName = document.getElementById('newDisplayName').value.trim();

        // Basic validation
        if (!startTime || !endTime) {
            validationMsg.textContent = 'Start and end times are required (HH:mm format).';
            return;
        }

        if (!isValidTime(startTime) || !isValidTime(endTime)) {
            validationMsg.textContent = 'Invalid time format. Use HH:mm (e.g., 08:00, 23:30).';
            return;
        }

        if (startTime >= endTime) {
            validationMsg.textContent = 'End time must be after start time.';
            return;
        }

        // Create entry
        var entry = {
            DayOfWeek: day,
            StartTime: startTime,
            EndTime: endTime,
            PlaylistId: playlistId || '',
            DisplayName: displayName || DAY_NAMES[day] + ' ' + startTime + '-' + endTime,
            IsEnabled: true
        };

        if (!currentConfig.ScheduleEntries) {
            currentConfig.ScheduleEntries = [];
        }

        currentConfig.ScheduleEntries.push(entry);
        renderSchedule(currentConfig.ScheduleEntries);
        toggleAddForm();

        // Clear form
        document.getElementById('newStartTime').value = '';
        document.getElementById('newEndTime').value = '';
        document.getElementById('newDisplayName').value = '';
    }

    function removeScheduleEntry(index) {
        if (currentConfig && currentConfig.ScheduleEntries) {
            currentConfig.ScheduleEntries.splice(index, 1);
            renderSchedule(currentConfig.ScheduleEntries);
        }
    }

    function toggleScheduleEntry(index) {
        if (currentConfig && currentConfig.ScheduleEntries && currentConfig.ScheduleEntries[index]) {
            currentConfig.ScheduleEntries[index].IsEnabled = !currentConfig.ScheduleEntries[index].IsEnabled;
            renderSchedule(currentConfig.ScheduleEntries);
        }
    }

    function updateScheduleEntryField(index, field, value) {
        if (currentConfig && currentConfig.ScheduleEntries && currentConfig.ScheduleEntries[index]) {
            currentConfig.ScheduleEntries[index][field] = value;
        }
    }

    function renderSchedule(entries) {
        var body = document.getElementById('scheduleBody');
        var table = document.getElementById('scheduleTable');
        var emptyMsg = document.getElementById('emptyScheduleMsg');

        body.innerHTML = '';

        if (!entries || entries.length === 0) {
            table.style.display = 'none';
            emptyMsg.style.display = 'block';
            document.getElementById('statusScheduleCount').textContent = '0';
            return;
        }

        table.style.display = 'table';
        emptyMsg.style.display = 'none';
        document.getElementById('statusScheduleCount').textContent = entries.length;

        // Sort by day then start time
        var sorted = entries.map(function (e, i) { return { entry: e, index: i }; })
            .sort(function (a, b) {
                if (a.entry.DayOfWeek !== b.entry.DayOfWeek) return a.entry.DayOfWeek - b.entry.DayOfWeek;
                return a.entry.StartTime.localeCompare(b.entry.StartTime);
            });

        sorted.forEach(function (item) {
            var entry = item.entry;
            var idx = item.index;
            var tr = document.createElement('tr');
            if (!entry.IsEnabled) {
                tr.style.opacity = '0.5';
            }

            var playlistName = '-- Random Music --';
            if (entry.PlaylistId) {
                var found = playlists.find(function (p) { return p.id === entry.PlaylistId; });
                playlistName = found ? found.name : entry.PlaylistId;
            }

            tr.innerHTML =
                '<td>' + (idx + 1) + '</td>' +
                '<td><select class="schedule-day-select" onchange="window._radioUpdateField(' + idx + ',\'DayOfWeek\',parseInt(this.value))">' +
                    buildDayOptions(entry.DayOfWeek) +
                '</select></td>' +
                '<td><input type="text" value="' + escapeAttr(entry.StartTime) + '" class="schedule-time-input" ' +
                    'onchange="window._radioUpdateField(' + idx + ',\'StartTime\',this.value)" placeholder="HH:mm" /></td>' +
                '<td><input type="text" value="' + escapeAttr(entry.EndTime) + '" class="schedule-time-input" ' +
                    'onchange="window._radioUpdateField(' + idx + ',\'EndTime\',this.value)" placeholder="HH:mm" /></td>' +
                '<td><select class="schedule-playlist-select" onchange="window._radioUpdateField(' + idx + ',\'PlaylistId\',this.value)">' +
                    '<option value="">-- Random Music --</option>' +
                    buildPlaylistOptions(entry.PlaylistId) +
                '</select></td>' +
                '<td><input type="text" value="' + escapeAttr(entry.DisplayName) + '" ' +
                    'onchange="window._radioUpdateField(' + idx + ',\'DisplayName\',this.value)" /></td>' +
                '<td style="text-align:center;"><input type="checkbox" ' + (entry.IsEnabled ? 'checked' : '') +
                    ' onchange="window._radioToggle(' + idx + ')" /></td>' +
                '<td><button class="btn btn-danger btn-sm" onclick="window._radioRemove(' + idx + ')">Remove</button></td>';

            body.appendChild(tr);
        });
    }

    // Expose schedule functions to inline handlers
    window._radioUpdateField = updateScheduleEntryField;
    window._radioRemove = removeScheduleEntry;
    window._radioToggle = toggleScheduleEntry;

    // ── Helpers ───────────────────────────────────────────────────────

    function buildDayOptions(selectedDay) {
        var days = [
            { value: 1, label: 'Monday' },
            { value: 2, label: 'Tuesday' },
            { value: 3, label: 'Wednesday' },
            { value: 4, label: 'Thursday' },
            { value: 5, label: 'Friday' }
        ];
        return days.map(function (d) {
            return '<option value="' + d.value + '"' + (d.value === selectedDay ? ' selected' : '') + '>' + d.label + '</option>';
        }).join('');
    }

    function buildPlaylistOptions(selectedId) {
        return playlists.map(function (p) {
            return '<option value="' + p.id + '"' + (p.id === selectedId ? ' selected' : '') + '>' + escapeHtml(p.name) + '</option>';
        }).join('');
    }

    function isValidTime(str) {
        return /^([01]\d|2[0-3]):([0-5]\d)$/.test(str);
    }

    function escapeHtml(str) {
        var div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }

    function escapeAttr(str) {
        return String(str).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

})();

(function () {
    'use strict';

    var Scryer = window.Scryer;
    var apiGet = Scryer.apiGet, apiPost = Scryer.apiPost, apiPut = Scryer.apiPut, escapeHtml = Scryer.escapeHtml;
    var getCapabilities = Scryer.getCapabilities, getQualityProfiles = Scryer.getQualityProfiles, getLibraries = Scryer.getLibraries, LOADING_HTML = Scryer.LOADING_HTML;
    var FACETS = ['ALL', 'MOVIE', 'SERIES', 'ANIME'];
    var STATUSES = ['ALL', 'PENDING', 'APPROVED', 'REJECTED', 'CANCELED'];

    function label(value) { return value === 'ALL' ? 'All' : value.replace(/_/g, ' ').toLowerCase().replace(/^./, function (character) { return character.toUpperCase(); }); }
    function optionHtml(options, selected, labelOf) {
        return options.map(function (option) { return '<option value="' + escapeHtml(option.value || option) + '"' + ((option.value || option) === selected ? ' selected' : '') + '>' + escapeHtml(labelOf ? labelOf(option) : label(option.label || option)) + '</option>'; }).join('');
    }
    function profileOptions(profiles, selected) {
        return profiles.map(function (profile) { return '<option value="' + escapeHtml(profile.id) + '"' + (profile.id === selected ? ' selected' : '') + '>' + escapeHtml(profile.name) + '</option>'; }).join('');
    }

    function renderRequests(container, scope) {
        container.innerHTML = '<h1>Requests</h1><div class="scryerTabGroup scryerRequestViewTabs"></div><div class="scryerRequestTabs"><div class="scryerTabGroup scryerFacetTabs"></div><div class="scryerTabGroup scryerStatusTabs"></div><label class="scryerLibraryFilter hide"><span class="inputLabel">Library</span><select class="scryerRequestLibraryFilter"></select></label></div><div class="scryerRequestMessage" role="status" aria-live="polite"></div><div class="scryerRequestsList">' + LOADING_HTML + '</div>';
        var viewTabs = container.querySelector('.scryerRequestViewTabs');
        var facets = container.querySelector('.scryerFacetTabs');
        var statuses = container.querySelector('.scryerStatusTabs');
        var libraryFilter = container.querySelector('.scryerLibraryFilter');
        var librarySelect = container.querySelector('.scryerRequestLibraryFilter');
        var message = container.querySelector('.scryerRequestMessage');
        var list = container.querySelector('.scryerRequestsList');
        var capabilities = null;
        var profiles = [];
        var libraries = [];
        var myRequests = [];
        var manageableRequests = [];
        var state = { view: 'mine', facet: 'ALL', status: 'ALL', libraryId: 'ALL' };

        function canManage() {
            return capabilities && (capabilities.libraries || []).some(function (library) { return library.canManageTitles; });
        }
        function source() { return state.view === 'manage' ? manageableRequests : myRequests; }
        function filtered() {
            return source().filter(function (request) {
                return (state.facet === 'ALL' || request.facet === state.facet) && (state.status === 'ALL' || request.status === state.status) && (state.view !== 'manage' || state.libraryId === 'ALL' || request.libraryId === state.libraryId);
            });
        }
        function requestProfiles(request) {
            var library = libraries.filter(function (entry) { return entry.id === request.libraryId; })[0];
            var allowed = Scryer.ui.profilesForLibrary(profiles, library);
            return allowed.length ? allowed : profiles;
        }
        function renderViewTabs() {
            viewTabs.innerHTML = '<button type="button" class="scryerTab' + (state.view === 'mine' ? ' scryerTabActive' : '') + '" data-view="mine">My requests <span class="scryerTabCount">' + myRequests.length + '</span></button>' + (canManage() ? '<button type="button" class="scryerTab' + (state.view === 'manage' ? ' scryerTabActive' : '') + '" data-view="manage">Manage requests <span class="scryerTabCount">' + manageableRequests.length + '</span></button>' : '');
        }
        function renderFilters() {
            facets.innerHTML = FACETS.map(function (facet) { return '<button type="button" class="scryerTab' + (facet === state.facet ? ' scryerTabActive' : '') + '" data-facet="' + facet + '">' + label(facet) + '</button>'; }).join('');
            statuses.innerHTML = STATUSES.map(function (status) { return '<button type="button" class="scryerTab' + (status === state.status ? ' scryerTabActive' : '') + '" data-status="' + status + '">' + label(status) + '</button>'; }).join('');
            if (state.view !== 'manage') { libraryFilter.classList.add('hide'); return; }
            var ids = Array.from(new Set(manageableRequests.map(function (request) { return request.libraryId; }))).sort();
            librarySelect.innerHTML = '<option value="ALL">All libraries</option>' + ids.map(function (id) { return '<option value="' + escapeHtml(id) + '"' + (id === state.libraryId ? ' selected' : '') + '>Library ' + escapeHtml(id) + '</option>'; }).join('');
            libraryFilter.classList.remove('hide');
        }
        function ownActions(request) {
            if (request.status !== 'PENDING') return '';
            var choices = requestProfiles(request);
            if (!choices.length) return '<span>Quality profile unavailable</span>';
            return '<select data-own-quality="' + escapeHtml(request.id) + '">' + profileOptions(choices, request.requestedQualityProfileId) + '</select><select data-own-monitor="' + escapeHtml(request.id) + '">' + optionHtml(Scryer.ui.monitorOptions, request.requestedMonitorType || 'MONITORED', function (option) { return option.label; }) + '</select><button type="button" class="raised" data-action="update-own" data-id="' + escapeHtml(request.id) + '">Save</button><button type="button" data-action="cancel-own" data-id="' + escapeHtml(request.id) + '">Cancel</button>';
        }
        function managerActions(request) {
            if (request.status !== 'PENDING') return '';
            var choices = requestProfiles(request);
            if (!choices.length) return '<span>Quality profile unavailable</span>';
            return '<select data-manage-quality="' + escapeHtml(request.id) + '">' + profileOptions(choices, request.requestedQualityProfileId || request.approvedQualityProfileId) + '</select><select data-manage-monitor="' + escapeHtml(request.id) + '">' + optionHtml(Scryer.ui.monitorOptions, request.requestedMonitorType || 'MONITORED', function (option) { return option.label; }) + '</select><button type="button" class="raised" data-action="approve" data-id="' + escapeHtml(request.id) + '">Approve</button><button type="button" data-action="dismiss" data-id="' + escapeHtml(request.id) + '">Dismiss</button>';
        }
        function renderRows() {
            var requests = filtered();
            list.innerHTML = '';
            if (!requests.length) { list.innerHTML = '<p role="status">No requests for this filter.</p>'; return; }
            requests.forEach(function (request) {
                var row = document.createElement('div');
                row.className = 'scryerRequestRow';
                row.innerHTML = '<span><strong>' + escapeHtml(request.title) + '</strong><br><small>' + escapeHtml(label(request.facet)) + ' · ' + escapeHtml(request.libraryId) + '</small></span><span class="scryerStatus-' + escapeHtml(request.status) + '">' + escapeHtml(label(request.status)) + '</span><span>' + (state.view === 'mine' ? ownActions(request) : managerActions(request)) + '</span>';
                list.appendChild(row);
            });
        }
        function render() { renderViewTabs(); renderFilters(); renderRows(); }
        function setMessage(text, error) {
            message.textContent = text || '';
            message.className = 'scryerRequestMessage' + (error ? ' scryerModalMessage-error' : '');
        }
        function reload() {
            list.innerHTML = LOADING_HTML;
            var calls = [apiGet('Scryer/Requests/Mine')];
            if (canManage()) calls.push(apiGet('Scryer/Requests'));
            return Promise.all(calls).then(scope.guard(function (results) {
                myRequests = (results[0].myMediaRequests || []);
                manageableRequests = results[1] ? (results[1].mediaRequests || []) : [];
                render();
            }), scope.guard(function (error) { list.innerHTML = '<p role="alert">' + escapeHtml(error.message) + '</p>'; setMessage(error.message, true); }));
        }
        function run(action, successMessage) {
            setMessage('Working…');
            action().then(scope.guard(function (result) {
                setMessage(typeof successMessage === 'function' ? successMessage(result) : successMessage);
                reload();
            }), scope.guard(function (error) { setMessage(error.message, true); }));
        }
        function approvalMessage(result) {
            var approval = result && result.approveMediaRequest;
            var wantedSearch = approval && approval.wantedSearch;
            var parts = ['Request approved.'];
            if (wantedSearch && wantedSearch.queuedCount) parts.push('Queued ' + wantedSearch.queuedCount + ' search' + (wantedSearch.queuedCount === 1 ? '' : 'es') + '.');
            if (wantedSearch && wantedSearch.skippedInProgressCount) parts.push('Skipped ' + wantedSearch.skippedInProgressCount + ' already in progress.');
            if (approval && approval.searchError) parts.push('Automated search reported: ' + approval.searchError);
            return parts.join(' ');
        }
        scope.on(viewTabs, 'click', function (event) {
            var button = event.target.closest('button[data-view]');
            if (!button) return;
            state.view = button.dataset.view;
            state.libraryId = 'ALL';
            render();
        });
        scope.on(facets, 'click', function (event) { var button = event.target.closest('button[data-facet]'); if (button) { state.facet = button.dataset.facet; render(); } });
        scope.on(statuses, 'click', function (event) { var button = event.target.closest('button[data-status]'); if (button) { state.status = button.dataset.status; render(); } });
        scope.on(librarySelect, 'change', function () { state.libraryId = librarySelect.value; renderRows(); });
        scope.on(list, 'click', function (event) {
            var button = event.target.closest('button[data-action]');
            if (!button) return;
            var id = button.dataset.id;
            var row = button.closest('.scryerRequestRow');
            if (!row) return;
            if (button.dataset.action === 'update-own') {
                var quality = row.querySelector('select[data-own-quality]');
                var monitor = row.querySelector('select[data-own-monitor]');
                if (!quality || !monitor) return;
                run(function () { return apiPut('Scryer/Requests/' + encodeURIComponent(id), { RequestedQualityProfileId: quality.value, RequestedMonitorType: monitor.value }); }, 'Your pending request was updated.');
            } else if (button.dataset.action === 'cancel-own') {
                if (window.confirm && !window.confirm('Cancel this pending request?')) return;
                run(function () { return apiPost('Scryer/Requests/' + encodeURIComponent(id) + '/cancel'); }, 'Your pending request was canceled.');
            } else if (button.dataset.action === 'approve') {
                var approvalQuality = row.querySelector('select[data-manage-quality]');
                var approvalMonitor = row.querySelector('select[data-manage-monitor]');
                if (!approvalQuality || !approvalMonitor) return;
                run(function () { return apiPost('Scryer/Requests/' + encodeURIComponent(id) + '/approve?qualityProfileId=' + encodeURIComponent(approvalQuality.value) + '&monitorType=' + encodeURIComponent(approvalMonitor.value)); }, approvalMessage);
            } else if (button.dataset.action === 'dismiss') {
                if (window.confirm && !window.confirm('Dismiss this request?')) return;
                run(function () { return apiPost('Scryer/Requests/' + encodeURIComponent(id) + '/dismiss'); }, 'Request dismissed.');
            }
        });
        Promise.all([getCapabilities(), getQualityProfiles(), getLibraries().catch(function () { return []; })]).then(scope.guard(function (results) {
            capabilities = results[0];
            profiles = results[1] || [];
            libraries = results[2] || [];
            return reload();
        }), scope.guard(function (error) { list.innerHTML = '<p role="alert">' + escapeHtml(error.message) + '</p>'; setMessage(error.message, true); }));
    }

    Scryer.lifecycle.registerFeature('requests', function (container, scope, context) {
        Scryer.withConnectionGate(container, scope, context.page, renderRequests);
    });
})();

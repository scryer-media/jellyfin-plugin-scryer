(function () {
    'use strict';

    var Scryer = window.Scryer;
    var apiGet = Scryer.apiGet, escapeHtml = Scryer.escapeHtml, LOADING_HTML = Scryer.LOADING_HTML;
    function formatBytes(bytes) {
        if (!bytes) return '';
        var units = ['B', 'KB', 'MB', 'GB', 'TB'];
        var index = 0;
        var value = bytes;
        while (value >= 1024 && index < units.length - 1) { value /= 1024; index++; }
        return value.toFixed(value >= 10 || index === 0 ? 0 : 1) + ' ' + units[index];
    }
    function formatRemaining(seconds) {
        if (!seconds || seconds <= 0) return '';
        var hours = Math.floor(seconds / 3600);
        var minutes = Math.floor((seconds % 3600) / 60);
        return hours > 0 ? hours + 'h ' + minutes + 'm left' : minutes + 'm left';
    }
    function formatWhen(value) {
        if (!value) return '';
        var minutes = Math.round((Date.now() - new Date(value).getTime()) / 60000);
        if (minutes < 1) return 'just now';
        if (minutes < 60) return minutes + 'm ago';
        var hours = Math.round(minutes / 60);
        return hours < 24 ? hours + 'h ago' : Math.round(hours / 24) + 'd ago';
    }
    function stateLabel(value) { return String(value || '').replace(/_/g, ' ').toLowerCase().replace(/^./, function (character) { return character.toUpperCase(); }); }
    function itemHtml(item, history) {
        var progress = Math.max(0, Math.min(100, item.progressPercent || 0));
        var meta = [];
        if (item.clientName) meta.push(escapeHtml(item.clientName));
        if (item.clientType) meta.push(escapeHtml(item.clientType));
        if (item.facet) meta.push(escapeHtml(String(item.facet).toLowerCase()));
        if (item.sizeBytes) meta.push(formatBytes(item.sizeBytes));
        if (item.importStatus) meta.push('Import: ' + escapeHtml(stateLabel(item.importStatus)));
        var time = history ? formatWhen(item.importedAt) : formatRemaining(item.remainingSeconds);
        if (time) meta.push(time);
        var attention = item.attentionRequired ? item.attentionReason || 'Needs attention' : item.importErrorMessage;
        return '<div class="scryerDownloadItem"><div class="scryerDownloadHeader"><span class="scryerDownloadTitle">' + escapeHtml(item.titleName || 'Unknown') + '</span><span class="scryerDownloadState scryerDownloadState-' + escapeHtml(item.displayState || '') + '">' + escapeHtml(stateLabel(item.displayState)) + '</span></div>' + (history ? '' : '<div class="scryerDownloadProgressTrack"><div class="scryerDownloadProgressFill" style="width:' + progress + '%"></div></div>') + '<div class="scryerDownloadMeta">' + (history ? '' : '<span>' + progress + '%</span>') + (meta.length ? '<span>' + meta.join(' · ') + '</span>' : '') + '</div>' + (attention ? '<div class="scryerDownloadAttention">' + escapeHtml(attention) + '</div>' : '') + '</div>';
    }
    function renderDownloads(container, scope) {
        container.innerHTML = '<h1>Downloads</h1><div class="scryerTabGroup scryerDownloadTabs"><button type="button" class="scryerTab" data-tab="active">Active</button><button type="button" class="scryerTab" data-tab="history">History</button></div><div class="scryerDownloadList">' + LOADING_HTML + '</div>';
        var list = container.querySelector('.scryerDownloadList');
        var buttons = container.querySelectorAll('.scryerTab');
        var activeTab = 'active';
        var failureCount = 0;
        var nextPoll = null;
        function updateTabs() { buttons.forEach(function (button) { button.classList.toggle('scryerTabActive', button.dataset.tab === activeTab); }); }
        function schedulePoll() {
            if (nextPoll) window.clearTimeout(nextPoll);
            if (document.hidden || activeTab !== 'active' || !scope.isCurrent()) return;
            var delay = Math.min(60000, 5000 * Math.pow(2, failureCount));
            nextPoll = window.setTimeout(scope.guard(function () { load(false); }), delay);
        }
        scope.own(function () { if (nextPoll) window.clearTimeout(nextPoll); });
        function load(showLoading) {
            if (showLoading) list.innerHTML = LOADING_HTML;
            var history = activeTab === 'history';
            var path = history ? 'Scryer/Downloads/History' : 'Scryer/Downloads';
            var key = history ? 'downloadHistory' : 'downloadQueuePage';
            var empty = history ? 'No download history yet.' : 'Nothing downloading right now.';
            return apiGet(path).then(scope.guard(function (data) {
                failureCount = 0;
                var items = (data[key] && data[key].items) || [];
                list.innerHTML = items.length ? items.map(function (item) { return itemHtml(item, history); }).join('') : '<p role="status">' + empty + '</p>';
                schedulePoll();
            }), scope.guard(function (error) {
                failureCount++;
                list.innerHTML = '<p role="alert">' + escapeHtml(error.message) + '</p>';
                schedulePoll();
            }));
        }
        scope.on(container, 'click', function (event) {
            var button = event.target.closest('button[data-tab]');
            if (!button || button.dataset.tab === activeTab) return;
            activeTab = button.dataset.tab;
            updateTabs();
            load(true);
        });
        scope.on(document, 'visibilitychange', function () {
            if (!document.hidden && activeTab === 'active') load(false);
            if (document.hidden && nextPoll) window.clearTimeout(nextPoll);
        });
        updateTabs();
        load(true);
    }
    Scryer.lifecycle.registerFeature('download', function (container, scope, context) {
        Scryer.withConnectionGate(container, scope, context.page, renderDownloads);
    });
})();

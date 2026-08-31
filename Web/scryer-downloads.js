(function () {
    'use strict';

    var Scryer = window.Scryer;
    var apiGet = Scryer.apiGet, escapeHtml = Scryer.escapeHtml, LOADING_HTML = Scryer.LOADING_HTML;

    function formatBytes(bytes) {
        if (!bytes) return '';
        var units = ['B', 'KB', 'MB', 'GB', 'TB'];
        var i = 0;
        var value = bytes;
        while (value >= 1024 && i < units.length - 1) {
            value /= 1024;
            i++;
        }
        return value.toFixed(value >= 10 || i === 0 ? 0 : 1) + ' ' + units[i];
    }

    function formatRemaining(seconds) {
        if (!seconds || seconds <= 0) return '';
        var h = Math.floor(seconds / 3600);
        var m = Math.floor((seconds % 3600) / 60);
        if (h > 0) return h + 'h ' + m + 'm left';
        return m + 'm left';
    }

    function formatWhen(isoString) {
        if (!isoString) return '';
        var then = new Date(isoString).getTime();
        var diffMin = Math.round((Date.now() - then) / 60000);
        if (diffMin < 1) return 'just now';
        if (diffMin < 60) return diffMin + 'm ago';
        var diffHour = Math.round(diffMin / 60);
        if (diffHour < 24) return diffHour + 'h ago';
        return Math.round(diffHour / 24) + 'd ago';
    }

    function stateLabel(state) {
        return String(state || '').replace(/_/g, ' ').toLowerCase().replace(/^./, function (c) { return c.toUpperCase(); });
    }

    function itemHtml(item, isHistory) {
        var progress = Math.max(0, Math.min(100, item.progressPercent || 0));
        var meta = [];
        if (item.clientName) meta.push(escapeHtml(item.clientName));
        if (item.sizeBytes) meta.push(formatBytes(item.sizeBytes));

        if (isHistory) {
            var when = formatWhen(item.importedAt);
            if (when) meta.push(when);
        } else {
            var remaining = formatRemaining(item.remainingSeconds);
            if (remaining) meta.push(remaining);
        }

        var attention = item.attentionRequired
            ? '<div class="scryerDownloadAttention">' + escapeHtml(item.attentionReason || 'Needs attention') + '</div>'
            : (item.importErrorMessage
                ? '<div class="scryerDownloadAttention">' + escapeHtml(item.importErrorMessage) + '</div>'
                : '');

        return (
            '<div class="scryerDownloadItem">' +
                '<div class="scryerDownloadHeader">' +
                    '<span class="scryerDownloadTitle">' + escapeHtml(item.titleName || 'Unknown') + '</span>' +
                    '<span class="scryerDownloadState scryerDownloadState-' + escapeHtml(item.displayState || '') + '">' +
                        escapeHtml(stateLabel(item.displayState)) +
                    '</span>' +
                '</div>' +
                (isHistory ? '' :
                    '<div class="scryerDownloadProgressTrack">' +
                        '<div class="scryerDownloadProgressFill" style="width:' + progress + '%"></div>' +
                    '</div>'
                ) +
                '<div class="scryerDownloadMeta">' +
                    (isHistory ? '' : '<span>' + progress + '%</span>') +
                    (meta.length ? '<span>' + meta.join(' · ') + '</span>' : '') +
                '</div>' +
                attention +
            '</div>'
        );
    }

    var refreshTimer = null;
    var activeTab = 'active';

    function renderDownloads(container) {
        if (refreshTimer) {
            clearInterval(refreshTimer);
            refreshTimer = null;
        }

        container.innerHTML =
            '<h1>Downloads</h1>' +
            '<div class="scryerTabGroup scryerDownloadTabs">' +
                '<button type="button" class="scryerTab" data-tab="active">Active</button>' +
                '<button type="button" class="scryerTab" data-tab="history">History</button>' +
            '</div>' +
            '<div class="scryerDownloadList">' + LOADING_HTML + '</div>';

        var list = container.querySelector('.scryerDownloadList');
        var tabButtons = container.querySelectorAll('.scryerTab');

        function updateTabButtons() {
            tabButtons.forEach(function (btn) {
                btn.classList.toggle('scryerTabActive', btn.dataset.tab === activeTab);
            });
        }

        function load(showLoading) {
            if (showLoading) list.innerHTML = LOADING_HTML;

            var isHistory = activeTab === 'history';
            var path = isHistory ? 'Scryer/Downloads/History' : 'Scryer/Downloads';
            var payloadKey = isHistory ? 'downloadHistory' : 'downloadQueuePage';
            var emptyMessage = isHistory ? 'No download history yet.' : 'Nothing downloading right now.';

            return apiGet(path).then(function (data) {
                var items = (data[payloadKey] && data[payloadKey].items) || [];
                if (!items.length) {
                    list.innerHTML = '<p>' + emptyMessage + '</p>';
                    return;
                }
                list.innerHTML = items.map(function (item) { return itemHtml(item, isHistory); }).join('');
            }).catch(function (err) { list.innerHTML = '<p>' + escapeHtml(err.message) + '</p>'; });
        }

        tabButtons.forEach(function (btn) {
            btn.addEventListener('click', function () {
                if (btn.dataset.tab === activeTab) return;
                activeTab = btn.dataset.tab;
                updateTabButtons();
                load(true);
            });
        });

        updateTabButtons();
        load(true);

        refreshTimer = setInterval(function () {
            if (container.offsetParent === null) {
                clearInterval(refreshTimer);
                refreshTimer = null;
                return;
            }
            if (activeTab === 'active') load(false);
        }, 5000);
    }

    Scryer.pages.download = renderDownloads;
})();

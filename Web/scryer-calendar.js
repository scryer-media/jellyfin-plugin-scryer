(function () {
    'use strict';

    var Scryer = window.Scryer;
    var apiGet = Scryer.apiGet, escapeHtml = Scryer.escapeHtml, LOADING_HTML = Scryer.LOADING_HTML;
    var resolveImageUrl = Scryer.resolveImageUrl;
    var AVAILABILITY_LABEL = { AVAILABLE: 'Available', PENDING_SCAN: 'Scanning', SCAN_FAILED: 'Scan failed', MISSING: 'Missing', UNMONITORED: 'Unmonitored' };

    function formatDateLabel(value) {
        var date = new Date(value + 'T00:00:00');
        var today = new Date();
        today.setHours(0, 0, 0, 0);
        var days = Math.round((date - today) / 86400000);
        if (days === 0) return 'Today';
        if (days === 1) return 'Tomorrow';
        return date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
    }
    function episodeLabel(item) {
        var parts = [];
        if (item.seasonNumber) parts.push('S' + item.seasonNumber);
        if (item.episodeNumber) parts.push('E' + item.episodeNumber);
        return parts.join('');
    }
    function renderPosterInto(element, url, scope) {
        element.innerHTML = '<div class="scryerPosterPlaceholder"><svg class="scryerPlaceholderIcon" viewBox="0 0 24 24" focusable="false" aria-hidden="true"><path fill="currentColor" d="M19 4h-1V2h-2v2H8V2H6v2H5c-1.11 0-1.99.9-1.99 2L3 20c0 1.1.89 2 2 2h14c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 16H5V9h14v11z"></path></svg></div>';
        if (!url) return;
        resolveImageUrl(url).then(scope.guard(function (resolved) {
            var image = new Image();
            image.alt = '';
            image.onload = scope.guard(function () { element.innerHTML = ''; element.appendChild(image); });
            image.src = resolved;
        }));
    }
    function renderCalendar(container, scope) {
        container.innerHTML = '<h1>Calendar</h1><div class="scryerCalendarGrid">' + LOADING_HTML + '</div>';
        var grid = container.querySelector('.scryerCalendarGrid');
        apiGet('Scryer/Calendar/Upcoming?days=30').then(scope.guard(function (data) {
            var episodes = (data.calendarEpisodes || []).slice().sort(function (left, right) { return (left.airDate || '').localeCompare(right.airDate || ''); });
            var titlePosters = data.titlePosters || {};
            grid.innerHTML = '';
            if (!episodes.length) { grid.innerHTML = '<p role="status">Nothing airing in the next 30 days.</p>'; return; }
            Scryer.ui.groupByDate(episodes).forEach(function (group) {
                var section = document.createElement('section');
                section.className = 'scryerCalendarDateGroup';
                section.innerHTML = '<h2 class="scryerCategoryTitle">' + escapeHtml(formatDateLabel(group.date)) + '</h2><div class="scryerCalendarGrid"></div>';
                var groupGrid = section.querySelector('.scryerCalendarGrid');
                grid.appendChild(section);
                group.items.forEach(function (item) {
                    var availability = item.mediaAvailability && item.mediaAvailability.state;
                    var label = availability ? AVAILABILITY_LABEL[availability] || availability : '';
                    var card = document.createElement('div');
                    card.className = 'scryerCalendarCard';
                    card.title = item.overview || '';
                    card.innerHTML = '<div class="scryerCalendarPoster"></div><div class="scryerCalendarCardBody"><div class="scryerCalendarCardTitle">' + escapeHtml(item.titleName || 'Untitled') + '</div><div class="scryerCalendarCardEpisode">' + escapeHtml(episodeLabel(item)) + '</div>' + (label ? '<span class="scryerCalendarBadge scryerCalendarBadge-' + escapeHtml(availability) + '">' + escapeHtml(label) + '</span>' : '') + '</div>';
                    groupGrid.appendChild(card);
                    renderPosterInto(card.querySelector('.scryerCalendarPoster'), titlePosters[item.titleId] || item.imageUrl, scope);
                });
            });
        }), scope.guard(function (error) { grid.innerHTML = '<p role="alert">' + escapeHtml(error.message) + '</p>'; }));
    }
    Scryer.lifecycle.registerFeature('calendar', function (container, scope, context) {
        Scryer.withConnectionGate(container, scope, context.page, renderCalendar);
    });
})();

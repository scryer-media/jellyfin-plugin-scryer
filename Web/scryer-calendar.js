(function () {
    'use strict';

    var Scryer = window.Scryer;
    var apiGet = Scryer.apiGet, escapeHtml = Scryer.escapeHtml, LOADING_HTML = Scryer.LOADING_HTML;
    var resolveImageUrl = Scryer.resolveImageUrl;

    var AVAILABILITY_LABEL = {
        AVAILABLE: 'Available',
        PENDING_SCAN: 'Scanning',
        SCAN_FAILED: 'Scan failed',
        MISSING: 'Missing',
        UNMONITORED: 'Unmonitored'
    };

    function formatDateLabel(dateStr) {
        var date = new Date(dateStr + 'T00:00:00');
        var today = new Date();
        today.setHours(0, 0, 0, 0);
        var diffDays = Math.round((date - today) / 86400000);

        if (diffDays === 0) return 'Today';
        if (diffDays === 1) return 'Tomorrow';
        return date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
    }

    function episodeLabel(item) {
        var parts = [];
        if (item.seasonNumber) parts.push('S' + item.seasonNumber);
        if (item.episodeNumber) parts.push('E' + item.episodeNumber);
        return parts.join('');
    }

    function posterHtml() {
        return '<div class="scryerPosterPlaceholder"><span class="material-icons" aria-hidden="true">event</span></div>';
    }

    function renderPosterInto(el, url) {
        el.innerHTML = posterHtml();
        if (!url) return;
        resolveImageUrl(url).then(function (resolved) {
            var img = new Image();
            img.alt = '';
            img.onload = function () {
                el.innerHTML = '';
                el.appendChild(img);
            };
            img.src = resolved;
        });
    }

    function renderCalendar(container) {
        container.innerHTML = '<h1>Calendar</h1><div class="scryerCalendarGrid">' + LOADING_HTML + '</div>';
        var grid = container.querySelector('.scryerCalendarGrid');

        apiGet('Scryer/Calendar/Upcoming?days=30').then(function (data) {
            var episodes = (data.calendarEpisodes || []).slice().sort(function (a, b) {
                return (a.airDate || '').localeCompare(b.airDate || '');
            });
            var titlePosters = data.titlePosters || {};
            grid.innerHTML = '';

            if (!episodes.length) {
                grid.innerHTML = '<p>Nothing airing in the next 30 days.</p>';
                return;
            }

            episodes.forEach(function (item) {
                var availability = (item.mediaAvailability && item.mediaAvailability.state) || null;
                var availabilityLabel = availability ? AVAILABILITY_LABEL[availability] || availability : '';

                var card = document.createElement('div');
                card.className = 'scryerCalendarCard';
                card.title = item.overview || '';
                card.innerHTML =
                    '<div class="scryerCalendarPoster"></div>' +
                    '<div class="scryerCalendarCardBody">' +
                        '<div class="scryerCalendarCardDate">' + escapeHtml(formatDateLabel(item.airDate)) + '</div>' +
                        '<div class="scryerCalendarCardTitle">' + escapeHtml(item.titleName || 'Untitled') + '</div>' +
                        '<div class="scryerCalendarCardEpisode">' + escapeHtml(episodeLabel(item)) + '</div>' +
                        (availabilityLabel
                            ? '<span class="scryerCalendarBadge scryerCalendarBadge-' + escapeHtml(availability) + '">' + escapeHtml(availabilityLabel) + '</span>'
                            : '') +
                    '</div>';
                grid.appendChild(card);
                renderPosterInto(card.querySelector('.scryerCalendarPoster'), titlePosters[item.titleId] || item.imageUrl);
            });
        }).catch(function (err) { grid.innerHTML = '<p>' + escapeHtml(err.message) + '</p>'; });
    }

    Scryer.pages.calendar = renderCalendar;
})();

(() => {
    'use strict';

    const scriptUrl = new URL(document.currentScript.src);
    const endpoint = new URL('./', scriptUrl);
    let active;

    const style = document.createElement('style');
    style.textContent = `
        #frame-web-results[data-has-movies] ~ .noItemsMessage { display: none; }
        #frame-web-results .frame-web-status:empty { display: none; }
        #frame-web-results .frame-web-cards {
            display: flex;
            align-items: flex-start;
            margin: 0 -.6em;
            padding: 0;
            overflow-x: auto;
            scrollbar-width: thin;
            list-style: none;
        }
        #frame-web-results .frame-web-cards > .card { flex: 0 0 auto; min-width: 0; }
        #frame-web-results .frame-web-open {
            width: 100%;
            border: 0;
            padding: 0;
            background: transparent;
            color: inherit;
            font: inherit;
            text-align: center;
            cursor: pointer;
        }
        #frame-web-results .frame-web-open:focus-visible {
            outline: 2px solid currentColor;
            outline-offset: -2px;
            border-radius: .4em;
        }
        #frame-web-results .frame-web-open:disabled { cursor: wait; }
        #frame-web-results .frame-web-open[aria-busy="true"] .cardScalable { opacity: .6; }
        #frame-web-results .frame-web-open .cardBox,
        #frame-web-results .frame-web-open .cardScalable,
        #frame-web-results .frame-web-open .cardPadder,
        #frame-web-results .frame-web-open .cardText { display: block; }
        #frame-web-results .frame-web-poster { display: block; width: 100%; height: 100%; object-fit: cover; }
    `;
    document.head.append(style);

    function requestClient() {
        const api = window.ApiClient;
        const token = api?.accessToken?.();
        if (!token || typeof api.getUrl !== 'function') return null;
        const url = new URL(api.getUrl('Frame/Browse/WebScript'), location.href);
        if (url.origin !== endpoint.origin || url.pathname !== scriptUrl.pathname) return null;
        return { api, token };
    }

    async function request({ token }, route, query, options = {}) {
        const url = new URL(route, endpoint);
        if (query) url.search = new URLSearchParams(query);
        const headers = { 'X-Emby-Token': token };
        if (options.body) headers['Content-Type'] = 'application/json';
        const response = await fetch(url, { cache: 'no-store', redirect: 'error', ...options, headers });
        if (response.status === 401 || response.status === 403 || response.status === 404) return null;
        const data = await response.json().catch(() => null);
        if (!response.ok) throw new Error(data?.message || data?.Message || 'O Frame não respondeu. Tente novamente.');
        return data;
    }

    function button(label, action) {
        const element = document.createElement('button');
        element.type = 'button';
        element.className = 'emby-button raised';
        element.textContent = label;
        element.addEventListener('click', action);
        return element;
    }

    function movieLabel(item) {
        const suffix = item.name.match(/\s+(?:[-–—]\s*(\d{4})|\((\d{4})\))(\s*\([^()]+\))?\s*$/);
        const titleYear = suffix ? Number(suffix[1] || suffix[2]) : 0;
        const year = item.year > 0 ? item.year : titleYear;
        const title = suffix && year === titleYear
            ? (item.name.slice(0, suffix.index) + (suffix[3] || '')).trim() : item.name;
        return { title, year };
    }

    function movieCard(state, item, page) {
        const { title, year } = movieLabel(item);
        const card = document.createElement('li');
        card.className = 'card overflowPortraitCard';
        const open = button('', event => openMovie(state, event.currentTarget, item, page));
        open.className = 'frame-web-open';
        open.setAttribute('aria-label', 'Abrir ' + title + (year ? ' (' + year + ')' : '') + ' no Jellyfin');

        const box = document.createElement('span');
        box.className = 'cardBox cardBox-bottompadded';
        const scalable = document.createElement('span');
        scalable.className = 'cardScalable';
        const padder = document.createElement('span');
        padder.className = 'cardPadder cardPadder-overflowPortrait';
        const placeholder = document.createElement('span');
        placeholder.className = 'cardImageIcon material-icons movie';
        placeholder.setAttribute('aria-hidden', 'true');
        padder.append(placeholder);
        scalable.append(padder);

        if (item.posterUrl) {
            const cover = document.createElement('span');
            cover.className = 'cardContent cardImageContainer';
            const poster = document.createElement('img');
            poster.className = 'frame-web-poster';
            poster.alt = '';
            poster.loading = 'lazy';
            poster.decoding = 'async';
            poster.referrerPolicy = 'no-referrer';
            poster.addEventListener('error', () => cover.remove(), { once: true });
            poster.src = item.posterUrl;
            cover.append(poster);
            scalable.append(cover);
        }

        const name = document.createElement('span');
        name.className = 'cardText cardText-first';
        name.textContent = title;
        name.title = title;
        box.append(scalable, name);
        if (year) {
            const yearText = document.createElement('span');
            yearText.className = 'cardText cardText-secondary secondaryText';
            yearText.textContent = String(year);
            box.append(yearText);
        }

        open.append(box);
        card.append(open);
        return card;
    }

    function createSection(field) {
        const section = document.createElement('section');
        section.id = 'frame-web-results';
        section.className = 'padded-left padded-right';
        section.setAttribute('aria-labelledby', 'frame-web-heading');
        section.hidden = true;

        const heading = document.createElement('h2');
        heading.id = 'frame-web-heading';
        heading.className = 'sectionTitle sectionTitle-cards';
        heading.textContent = 'Resultados do Frame';
        const status = document.createElement('p');
        status.setAttribute('role', 'status');
        status.className = 'frame-web-status';
        const list = document.createElement('ul');
        list.className = 'frame-web-cards';
        const navigation = document.createElement('div');
        navigation.className = 'flex align-items-center';
        section.append(heading, status, list, navigation);
        field.closest('.searchFields').after(section);
        return { section, status, list, navigation };
    }

    function reset() {
        if (!active) return;
        clearTimeout(active.queryTimer);
        active.searchRequest?.abort();
        active.section.remove();
        active = undefined;
    }

    async function loadPage(state, page) {
        const controller = new AbortController();
        state.searchRequest?.abort();
        state.searchRequest = controller;
        delete state.section.dataset.hasMovies;
        state.list.replaceChildren();
        state.navigation.replaceChildren();
        state.status.textContent = 'Buscando no Frame…';

        try {
            const data = await request(state.client, 'WebSearch', { term: state.term, page }, { signal: controller.signal });
            if (active !== state || controller.signal.aborted) return;
            if (!data) {
                state.section.hidden = true;
                return;
            }
            state.section.hidden = false;
            state.status.textContent = data.items.length ? '' : 'Nenhum filme nesta página.';
            if (data.items.length) state.section.dataset.hasMovies = '';
            for (const item of data.items) state.list.append(movieCard(state, item, page));
            if (page > 1) state.navigation.append(button('Página anterior', () => loadPage(state, page - 1)));
            if (page > 1 || data.canPageForward) {
                const number = document.createElement('span');
                number.textContent = 'Página ' + page;
                state.navigation.append(number);
            }
            if (data.canPageForward) state.navigation.append(button('Próxima página', () => loadPage(state, page + 1)));
        } catch (error) {
            if (active !== state || controller.signal.aborted) return;
            state.section.hidden = false;
            state.status.textContent = error.message;
            state.navigation.replaceChildren(button('Tentar novamente', () => loadPage(state, page)));
        }
    }

    async function openMovie(state, selectedButton, item, page) {
        if (state.opening) return;
        state.opening = true;
        const controls = [...state.section.querySelectorAll('button')];
        controls.forEach(control => { control.disabled = true; });
        selectedButton.setAttribute('aria-busy', 'true');
        state.status.textContent = 'Abrindo ' + movieLabel(item).title + '…';
        try {
            let result = await request(state.client, 'WebImport', null, {
                method: 'POST',
                body: JSON.stringify({ term: state.term, page, externalId: item.externalId, isSeries: false })
            });
            if (active !== state) return;
            if (!result) throw new Error('O filme está indisponível. Faça uma nova busca.');
            if (!result.itemId) state.status.textContent = 'Filme importado. Aguardando o Jellyfin atualizar a biblioteca…';

            for (let attempt = 0; active === state && !result.itemId && attempt < 40; attempt++) {
                await new Promise(resolve => setTimeout(resolve, 2000));
                if (active !== state) return;
                result = await request(state.client, 'WebMovie', { externalId: item.externalId });
                if (!result) throw new Error('A biblioteca Frame ficou indisponível.');
            }
            if (active !== state) return;
            if (!result.itemId) {
                state.status.textContent = 'O Jellyfin ainda está atualizando a biblioteca. Tente abrir novamente em instantes.';
                return;
            }

            const details = new URLSearchParams({ id: result.itemId });
            const serverId = state.client.api.serverId?.();
            if (serverId) details.set('serverId', serverId);
            location.hash = '/details?' + details;
        } catch (error) {
            if (active === state) state.status.textContent = error.message;
        } finally {
            state.opening = false;
            selectedButton.removeAttribute('aria-busy');
            controls.forEach(control => { control.disabled = false; });
        }
    }

    function syncSearch() {
        const field = document.querySelector('#searchPage .searchFields #searchTextInput');
        const term = field?.value.trim();
        const params = new URLSearchParams(location.hash.split('?')[1]);
        if (!field || !term || term.length < 2 || params.has('parentId')) {
            reset();
            return;
        }
        const client = requestClient();
        if (!client) {
            reset();
            return;
        }
        if (active?.field === field && active.term === term && active.client.token === client.token &&
            active.section.isConnected) return;

        reset();
        const state = { field, term, client, ...createSection(field) };
        active = state;
        state.queryTimer = setTimeout(() => loadPage(state, 1), 500);
    }

    document.addEventListener('input', event => {
        if (event.target.id === 'searchTextInput' && !event.isComposing) syncSearch();
    });
    document.addEventListener('compositionend', event => {
        if (event.target.id === 'searchTextInput') syncSearch();
    });
    window.addEventListener('hashchange', syncSearch);
    new MutationObserver(syncSearch).observe(document.body, { childList: true, subtree: true });
    syncSearch();
})();
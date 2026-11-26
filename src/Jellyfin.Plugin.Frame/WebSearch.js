(() => {
    'use strict';

    const scriptUrl = new URL(document.currentScript.src);
    const endpoint = new URL('./', scriptUrl);
    let active;

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

    function createSection(field) {
        const section = document.createElement('section');
        section.id = 'frame-web-results';
        section.className = 'padded-left padded-right';
        section.setAttribute('aria-labelledby', 'frame-web-heading');
        section.hidden = true;

        const heading = document.createElement('h2');
        heading.id = 'frame-web-heading';
        heading.textContent = 'Resultados do Frame';
        const status = document.createElement('p');
        status.setAttribute('role', 'status');
        const list = document.createElement('ul');
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
            for (const item of data.items) {
                const row = document.createElement('li');
                const name = document.createElement('span');
                name.textContent = item.name + (item.year > 0 ? ' (' + item.year + ')' : '');
                row.append(name, ' ', button('Abrir no Jellyfin', event => openMovie(state, event.currentTarget, item, page)));
                state.list.append(row);
            }
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
        selectedButton.textContent = 'Abrindo…';
        state.status.textContent = '';
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
            selectedButton.textContent = 'Abrir no Jellyfin';
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
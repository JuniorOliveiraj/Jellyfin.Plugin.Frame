(() => {
    'use strict';

    const scriptUrl = new URL(document.currentScript.src);
    const endpoint = new URL('./', scriptUrl);
    let active;

    const style = document.createElement('style');
    style.textContent = `
        #searchPage[data-frame-has-movies] .noItemsMessage { display: none; }
        #frame-web-results .frame-web-status:empty { display: none; }
        #frame-web-results .frame-web-cards,
        #searchPage[data-frame-vertical] [data-frame-library-movies] .itemsContainer {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(min(100%, clamp(8rem, 14vw, 12rem)), 1fr));
            align-items: start;
        }
        #frame-web-results .frame-web-cards { margin: 0 -.6em; padding: 0; list-style: none; }
        #frame-web-results .frame-web-cards > .card,
        #searchPage[data-frame-vertical] [data-frame-library-movies] .itemsContainer > .card {
            width: 100%;
            min-width: 0;
        }
        #searchPage[data-frame-vertical] .searchResults { padding-bottom: 0 !important; }
        #searchPage[data-frame-vertical] [data-frame-library-movies] > .emby-scrollbuttons { display: none; }
        #searchPage[data-frame-vertical] [data-frame-library-movies] > [is="emby-scroller"] {
            overflow: visible !important;
            touch-action: auto !important;
        }
        #searchPage[data-frame-vertical] [data-frame-library-movies] .itemsContainer {
            width: auto !important;
            white-space: normal !important;
            transform: none !important;
        }
        #frame-web-results .frame-web-sentinel { height: 1px; }
        #frame-web-results .frame-web-feedback {
            margin: -.7em .6em 1em;
            font-size: .88em;
            white-space: normal;
            overflow-wrap: anywhere;
        }
        #frame-web-results .frame-web-feedback[hidden],
        #frame-web-results .frame-web-retry[hidden] { display: none; }
        #frame-web-results .frame-web-feedback .frame-web-status { margin: 0 0 .35em; }
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
        const data = await response.json().catch(() => null);
        const message = data?.message || data?.Message;
        if (!message && [401, 403, 404].includes(response.status)) return null;
        if (!response.ok) throw new Error(message || 'O Frame não respondeu. Tente novamente.');
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

    const languageMarker = 'LEG(?:ENDAD[OA])?|DUB(?:LAD[OA])?|DUAL(?:[ -]+[AÁ]UDIO)?';
    const trailingLanguage = new RegExp(
        '\\s*(?:\\((' + languageMarker + ')\\)|\\[(' + languageMarker + ')\\]|[-–—]\\s*(' + languageMarker + '))\\s*$', 'i');

    function titleLanguage(title) {
        const match = title.match(trailingLanguage);
        if (!match || match.index === 0) return { title, language: '' };

        const marker = (match[1] || match[2] || match[3]).toUpperCase();
        const language = marker.startsWith('LEG') ? 'Legendado' : marker.startsWith('DUB') ? 'Dublado' : 'Dual áudio';
        return { title: title.slice(0, match.index).trimEnd(), language };
    }

    function movieLabel(item) {
        let label = titleLanguage(item.name.trim());
        const suffix = label.title.match(/\s+(?:[-–—]\s*(\d{4})|\((\d{4})\))(\s*\([^()]+\))?\s*$/);
        const titleYear = suffix ? Number(suffix[1] || suffix[2]) : 0;
        const year = item.year > 0 ? item.year : titleYear;
        if (suffix && year === titleYear) {
            const clean = titleLanguage((label.title.slice(0, suffix.index) + (suffix[3] || '')).trim());
            label = { title: clean.title, language: label.language || clean.language };
        }
        return { ...label, year };
    }

    function addPoster(container, url, onError) {
        const cover = document.createElement('span');
        cover.className = 'cardContent cardImageContainer';
        const poster = document.createElement('img');
        poster.className = 'frame-web-poster';
        poster.alt = '';
        poster.loading = 'lazy';
        poster.decoding = 'async';
        poster.referrerPolicy = 'no-referrer';
        poster.addEventListener('error', () => {
            cover.remove();
            onError?.();
        }, { once: true });
        poster.src = url;
        cover.append(poster);
        container.append(cover);
    }

    function observePoster(state, card, container, item) {
        if (active !== state || !window.IntersectionObserver) return;
        state.posterObserver ??= new IntersectionObserver(entries => {
            for (const { target, isIntersecting } of entries) {
                if (!isIntersecting || active !== state) continue;
                const load = state.pendingPosters.get(target);
                state.pendingPosters.delete(target);
                state.posterObserver.unobserve(target);
                load?.();
            }
        });

        state.pendingPosters.set(card, async () => {
            try {
                const data = await request(state.client, 'WebPoster', { externalId: item.externalId },
                    { signal: state.posterAbort.signal });
                if (active === state && card.isConnected && data?.posterUrl && data.posterUrl !== item.posterUrl) {
                    addPoster(container, data.posterUrl);
                }
            } catch {
                // Keep the placeholder when the cached poster is unavailable.
            }
        });
        state.posterObserver.observe(card);
    }

    function movieCard(state, item, page) {
        const { title, year, language } = movieLabel(item);
        const metadata = [year, language].filter(Boolean).join(' · ');
        const card = document.createElement('li');
        card.className = 'card overflowPortraitCard';

        const start = () => openMovie(state, item, page, { open, feedback, status, retry });
        const open = button('', start);
        open.className = 'frame-web-open';
        open.disabled = !!state.opening;
        open.setAttribute('aria-label', 'Abrir ' + title + (metadata ? ' (' + metadata + ')' : '') + ' no Jellyfin');

        const feedback = document.createElement('div');
        feedback.className = 'frame-web-feedback';
        feedback.hidden = true;
        const status = document.createElement('p');
        status.className = 'frame-web-status';
        status.setAttribute('role', 'status');
        const retry = button('Tentar novamente', start);
        retry.classList.add('frame-web-retry');
        retry.setAttribute('aria-label', 'Tentar novamente: ' + title + (metadata ? ' (' + metadata + ')' : ''));
        retry.hidden = true;
        feedback.append(status, retry);

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

        const findPoster = () => observePoster(state, card, scalable, item);
        if (item.posterUrl) {
            addPoster(scalable, item.posterUrl, findPoster);
        } else {
            findPoster();
        }

        const name = document.createElement('span');
        name.className = 'cardText cardText-first';
        name.textContent = title;
        name.title = title;
        box.append(scalable, name);
        if (metadata) {
            const details = document.createElement('span');
            details.className = 'cardText cardText-secondary secondaryText';
            details.textContent = metadata;
            box.append(details);
        }

        open.append(box);
        card.append(open, feedback);
        return card;
    }

    function createSection() {
        const section = document.createElement('section');
        section.id = 'frame-web-results';
        section.className = 'padded-left padded-right padded-bottom-page';
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
        const footer = document.createElement('div');
        footer.setAttribute('aria-live', 'polite');
        const sentinel = document.createElement('div');
        sentinel.className = 'frame-web-sentinel';
        sentinel.setAttribute('aria-hidden', 'true');
        section.append(heading, status, list, footer, sentinel);
        return { section, status, list, footer, sentinel };
    }

    function placeAfterLibrary(state) {
        const parent = state.field.closest('.searchFields').parentElement;
        if (parent.lastElementChild !== state.section) parent.append(state.section);

        if (!state.page.hasAttribute('data-frame-vertical')) return;
        for (const row of state.page.querySelectorAll('.searchResults .verticalSection')) {
            row.toggleAttribute('data-frame-library-movies', !!row.querySelector('.card[data-type="Movie"]'));
        }
    }

    function reset() {
        if (!active) return;
        clearTimeout(active.queryTimer);
        active.searchRequest?.abort();
        active.observer?.disconnect();
        active.posterObserver?.disconnect();
        active.posterAbort.abort();
        active.page.removeAttribute('data-frame-vertical');
        active.page.removeAttribute('data-frame-has-movies');
        active.section.remove();
        active = undefined;
    }

    function observeNextPage(state) {
        if (!state.hasNext) return;
        if (!window.IntersectionObserver) {
            state.footer.append(button('Carregar mais filmes', () => loadNextPage(state)));
            return;
        }

        state.observer ??= new IntersectionObserver(entries => {
            if (entries.some(entry => entry.isIntersecting)) loadNextPage(state);
        }, { rootMargin: '0px 0px 240px' });
        state.observer.observe(state.sentinel);
    }

    async function loadNextPage(state) {
        if (active !== state || state.searchRequest || !state.hasNext) return;

        state.observer?.unobserve(state.sentinel);
        const controller = new AbortController();
        state.searchRequest = controller;
        const page = state.nextPage;
        state.footer.textContent = page === 1 ? 'Buscando no Frame…' : 'Carregando mais filmes…';

        try {
            const data = await request(state.client, 'WebSearch', { term: state.term, page }, { signal: controller.signal });
            if (active !== state || controller.signal.aborted) return;
            if (!data) {
                state.hasNext = false;
                state.section.hidden = true;
                state.page.removeAttribute('data-frame-vertical');
                state.page.removeAttribute('data-frame-has-movies');
                return;
            }
            if (!Array.isArray(data.items)) throw new Error('O Frame devolveu uma resposta de busca inválida.');

            const repeated = data.pageKey && state.seenPages.has(data.pageKey);
            if (data.pageKey) state.seenPages.add(data.pageKey);
            if (!repeated) {
                for (const item of data.items) {
                    if (state.seenMovies.has(item.externalId)) continue;
                    state.seenMovies.add(item.externalId);
                    state.list.append(movieCard(state, item, page));
                }
            }

            state.hasNext = data.canPageForward === true && !repeated;
            state.nextPage = page + 1;
            state.section.hidden = false;
            state.page.setAttribute('data-frame-vertical', '');
            state.page.toggleAttribute('data-frame-has-movies', state.seenMovies.size > 0);
            placeAfterLibrary(state);
            state.footer.textContent = '';
            if (!state.hasNext && state.seenMovies.size === 0) {
                state.status.textContent = 'Nenhum filme adicional no Frame.';
            }
            observeNextPage(state);
        } catch (error) {
            if (active !== state || controller.signal.aborted) return;
            state.section.hidden = false;
            state.footer.replaceChildren(document.createTextNode(error.message + ' '),
                button('Tentar novamente', () => loadNextPage(state)));
        } finally {
            state.searchRequest = null;
        }
    }

    function setOpening(state, opening) {
        state.opening = opening;
        for (const control of state.list.querySelectorAll('button')) control.disabled = opening;
    }

    async function openMovie(state, item, page, { open, feedback, status, retry }) {
        if (active !== state || state.opening) return;

        feedback.hidden = false;
        retry.hidden = true;
        status.textContent = 'Importando…';
        setOpening(state, true);
        open.setAttribute('aria-busy', 'true');
        try {
            let result = await request(state.client, 'WebImport', null, {
                method: 'POST',
                body: JSON.stringify({ term: state.term, page, externalId: item.externalId, isSeries: false })
            });
            if (active !== state) return;
            if (!result) throw new Error('O filme está indisponível. Faça uma nova busca.');
            if (!result.itemId) status.textContent = 'Filme importado. Aguardando a biblioteca do Jellyfin…';

            for (let attempt = 0; active === state && !result.itemId && attempt < 40; attempt++) {
                await new Promise(resolve => setTimeout(resolve, 2000));
                if (active !== state) return;
                result = await request(state.client, 'WebMovie', { externalId: item.externalId });
                if (!result) throw new Error('A biblioteca Frame ficou indisponível.');
            }
            if (active !== state) return;
            if (!result.itemId) throw new Error('Filme salvo. O Jellyfin ainda está atualizando a biblioteca.');

            status.textContent = 'Abrindo no Jellyfin…';
            const details = new URLSearchParams({ id: result.itemId });
            const serverId = state.client.api.serverId?.();
            if (serverId) details.set('serverId', serverId);
            location.hash = '/details?' + details;
        } catch (error) {
            if (active === state) {
                status.textContent = error.message;
                retry.hidden = false;
            }
        } finally {
            setOpening(state, false);
            open.removeAttribute('aria-busy');
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
        if (active?.field === field && active.term === term && active.client.token === client.token) {
            placeAfterLibrary(active);
            return;
        }

        reset();
        const state = {
            field, term, client,
            page: field.closest('#searchPage'),
            nextPage: 1,
            hasNext: true,
            seenMovies: new Set(),
            seenPages: new Set(),
            pendingPosters: new Map(),
            posterAbort: new AbortController(),
            ...createSection()
        };
        active = state;
        placeAfterLibrary(state);
        state.queryTimer = setTimeout(() => loadNextPage(state), 500);
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
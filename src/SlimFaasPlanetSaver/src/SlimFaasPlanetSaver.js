const normalizeBaseUrl = (url) => {
    let tempUrl = url;
    if (tempUrl.endsWith('/')) tempUrl = tempUrl.slice(0, -1);
    return tempUrl;
}

let id = 1;

export default class SlimFaasPlanetSaver {
    constructor(baseUrl, options = {}) {
        this.baseUrl = normalizeBaseUrl(baseUrl);
        this.updateCallback = options.updateCallback || (() => {});
        this.errorCallback = options.errorCallback || (() => {});
        this.interval = options.interval || 5000;
        this.overlayStartingMessage = options.overlayStartingMessage || '🌳 Starting the environment.... 🌳';
        this.overlayNoActivityMessage = options.overlayNoActivityMessage || 'Waiting activity to start environment...';
        this.overlayErrorMessage = options.overlayErrorMessage || 'An error occurred while starting the environment.';
        this.overlaySecondaryMessage = options.overlaySecondaryMessage || 'Startup should be fast, but if no machines are available it can take several minutes.';
        this.overlayErrorSecondaryMessage = options.overlayErrorSecondaryMessage || 'If the error persists, please contact an administrator.';
        this.overlayLoadingIcon = options.overlayLoadingIcon || '🌍';
        this.noActivityTimeout = options.noActivityTimeout || 60000;
        this.wakeUpTimeout = options.wakeUpTimeout || 60000;
        this.fetch = options.fetch || fetch;

        // Ajout de la configuration de comportement
        // Les valeurs possibles sont "WakeUp+BockUI", "WakeUp", "None"
        this.behavior = options.behavior || {};

        this.intervalId = null;
        this.isDocumentVisible = !document.hidden;
        this.overlayElement = null;
        this.spanElement = null;
        this.styleElement = null;
        this.isReady = false;
        this.id = id++;
        this.cleanned = false;
        this.lastWakeUpTime = null;
    }

    /**
     * Retourne le comportement à appliquer pour une fonction donnée
     * S'il n'est pas renseigné, renvoie "WakeUp+BockUI" par défaut
     */
    getBehavior(name) {
        return this.behavior[name] || 'WakeUp+BockUI';
    }

    initialize() {
        this.cleanned = false;
        this.lastMouseMoveTime = Date.now();
        this.handleMouseMove = this.handleMouseMove.bind(this);
        this.handleVisibilityChange = this.handleVisibilityChange.bind(this);

        document.addEventListener('visibilitychange', this.handleVisibilityChange);
        document.addEventListener('mousemove', this.handleMouseMove);

        this.createOverlay();
        this.injectStyles();
    }

    handleMouseMove() {
        this.lastMouseMoveTime = Date.now();
    }

    handleVisibilityChange() {
        this.isDocumentVisible = !document.hidden;
    }

    /**
     * Appelle le wake-up sur les fonctions dont le comportement n'est pas "None"
     * et qui ne sont pas prêtes (NumberReady === 0), sauf si on a déjà fait
     * un wake-up trop récemment (selon wakeUpTimeout).
     */
    async wakeUpPods(data, lastWakeUpTime) {
        const currentTime = Date.now();
        let isWakeUpCallMade = false;

        // On évite de rappeler trop souvent la même fonction
        const shouldFilter = lastWakeUpTime && (currentTime - lastWakeUpTime) <= this.wakeUpTimeout;

        // On ne fait un wake-up que pour les fonctions dont le comportement est "WakeUp" ou "WakeUp+BockUI"
        const wakePromises = data
            .filter((item) => this.getBehavior(item.Name) !== 'None')
            .filter((item) => item.NumberReady === 0 || !shouldFilter)
            .map(async (item) => {
                const response = await this.fetch(`${this.baseUrl}/wake-function/${item.Name}`, {
                    method: 'POST',
                });
                if (response.status >= 400) {
                    throw new Error(`HTTP Error! status: ${response.status} for function ${item.Name}`);
                }
                isWakeUpCallMade = true;
                return response;
            });

        try {
            await Promise.all(wakePromises);
        } catch (error) {
            console.error("Error waking up pods:", error);
            throw error;
        }
        return isWakeUpCallMade;
    }

    /**
     * Récupère le status de chaque fonction et met à jour l'UI et l'overlay
     */
    async fetchStatus() {
        try {
            const response = await this.fetch(`${this.baseUrl}/status-functions`);
            if (response.status >= 400) {
                throw new Error(`HTTP Error! status: ${response.status}`);
            }
            const data = await response.json();

            // On ne considère comme bloquantes que les fonctions dont le comportement est "WakeUp+BockUI"
            const blockingItems = data.filter((item) => this.getBehavior(item.Name) === 'WakeUp+BockUI');
            const allBlockingReady = blockingItems.every((item) => item.NumberReady >= 1);

            // Si toutes les fonctions "bloquantes" sont prêtes, on estime que c'est "ready".
            this.setReadyState(allBlockingReady);

            // Callback pour l'extérieur
            this.updateCallback(data);

            // On vérifie l'activité de la souris pour "réveiller" (wakeUp) les fonctions
            const now = Date.now();
            const mouseMovedRecently = now - this.lastMouseMoveTime <= this.noActivityTimeout;

            if (!allBlockingReady && this.isDocumentVisible && !mouseMovedRecently) {
                // Pas de mouvement de souris, document visible => message "no activity"
                this.updateOverlayMessage(this.overlayNoActivityMessage, 'waiting-action');
            } else if (mouseMovedRecently) {
                // Il y a une activité de souris
                if (!allBlockingReady && this.isDocumentVisible) {
                    this.updateOverlayMessage(this.overlayStartingMessage, 'waiting');
                }
                if (!this.lastWakeUpTime) {
                    this.lastWakeUpTime = Date.now();
                }
                const isWakeUpCallMade = await this.wakeUpPods(data, this.lastWakeUpTime);
                if (isWakeUpCallMade) {
                    this.lastWakeUpTime = Date.now();
                }
            } else if (!this.isDocumentVisible && !allBlockingReady) {
                // Document caché, pas prêt => message "no activity"
                this.updateOverlayMessage(this.overlayNoActivityMessage, 'waiting');
            }
        } catch (error) {
            const errorMessage = error.message;
            // On garde l'état précédent de readiness en cas d'erreur
            this.setReadyState(this.isReady);
            this.updateOverlayMessage(this.overlayErrorMessage, 'error', this.overlayErrorSecondaryMessage);
            this.errorCallback(errorMessage);
            console.error('Error fetching slimfaas data:', errorMessage);
        } finally {
            if (this.intervalId) {
                this.intervalId = setTimeout(() => {
                    this.fetchStatus();
                }, this.interval);
            }
        }
    }

    /**
     * Met à jour l'état ready. Si ready = true => on cache l'overlay
     */
    setReadyState(isReady) {
        this.isReady = isReady;
        if (isReady) {
            this.hideOverlay();
        } else {
            this.showOverlay();
        }
    }

    /**
     * Lance le polling régulier
     */
    startPolling() {
        if (this.intervalId || !this.baseUrl || this.cleanned) return;
        this.fetchStatus();
        this.intervalId = setTimeout(() => {
            this.fetchStatus();
        }, this.interval);
    }

    /**
     * Arrête le polling
     */
    stopPolling() {
        if (this.intervalId) {
            clearTimeout(this.intervalId);
            this.intervalId = null;
        }
    }

    /**
     * Injecte dans le DOM le style pour l'overlay
     */
    injectStyles() {
        const cssString = `
            .slimfaas-environment-overlay {
                position: fixed;
                top: 0;
                left: 0;
                width: 100%;
                cursor: not-allowed;
                height: 100%;
                background-color: rgba(0, 0, 0, 0.8);
                display: flex;
                flex-direction: column;
                justify-content: center;
                align-items: center;
                font-size: 2rem;
                font-weight: bold;
                z-index: 1000;
                text-align: center;
            }

            .slimfaas-environment-overlay__icon {
                font-size: 4rem;
                animation: slimfaas-environment-overlay__icon-spin 0.5s linear infinite;
            }

            @keyframes slimfaas-environment-overlay__icon-spin {
                from {
                    transform: rotate(0deg);
                }
                to {
                    transform: rotate(360deg);
                }
            }

            .slimfaas-environment-overlay__main-message {
                display: flex;
                align-items: center;
                gap: 0.5rem;
            }

            .slimfaas-environment-overlay__secondary-message {
                font-size: 1.2rem;
                font-weight: normal;
                margin-top: 1rem;
            }

            .slimfaas-environment-overlay--waiting {
                color: white;
            }

            .slimfaas-environment-overlay--waiting-action {
                color: lightyellow;
            }
            .slimfaas-environment-overlay--waiting-action .slimfaas-environment-overlay__secondary-message {
                visibility: hidden;
            }
            .slimfaas-environment-overlay--waiting-action .slimfaas-environment-overlay__icon {
                animation: none;
            }

            .slimfaas-environment-overlay--error {
                color: lightcoral;
            }
        `;

        this.styleElement = document.createElement('style');
        this.styleElement.textContent = cssString;
        document.head.appendChild(this.styleElement);
    }

    /**
     * Crée dans le DOM l'overlay en lui-même (sans l'ajouter encore)
     */
    createOverlay() {
        this.overlayElement = document.createElement('div');
        this.overlayElement.className = 'slimfaas-environment-overlay';
        this.overlayElement.id = `slimfaas-environment-overlay-${this.id}`;

        // Élément icône
        this.iconElement = document.createElement('div');
        this.iconElement.className = 'slimfaas-environment-overlay__icon';
        this.iconElement.innerText = this.overlayLoadingIcon;

        // Message principal
        this.spanElement = document.createElement('span');
        this.spanElement.className = 'slimfaas-environment-overlay__main-message';
        this.spanElement.innerHTML = `${this.overlayStartingMessage}`;

        // Message secondaire
        this.secondarySpanElement = document.createElement('span');
        this.secondarySpanElement.className = 'slimfaas-environment-overlay__secondary-message';
        this.secondarySpanElement.innerText = this.overlaySecondaryMessage;

        // Ajout à l'overlay
        this.overlayElement.appendChild(this.iconElement);
        this.overlayElement.appendChild(this.spanElement);
        this.overlayElement.appendChild(this.secondarySpanElement);
    }

    /**
     * Affiche l'overlay dans la page si nécessaire
     */
    showOverlay() {
        if (this.cleanned) return;
        if (this.overlayElement && !document.body.contains(this.overlayElement)) {
            document.body.appendChild(this.overlayElement);
        }
    }

    /**
     * Cache l'overlay
     */
    hideOverlay() {
        if (this.overlayElement && document.body.contains(this.overlayElement)) {
            document.body.removeChild(this.overlayElement);
        }
    }

    /**
     * Met à jour le message affiché dans l'overlay
     */
    updateOverlayMessage(newMessage, status = 'waiting', secondaryMessage = null) {
        if (this.spanElement) {
            this.spanElement.innerHTML = `${newMessage}`;
        }
        if (this.secondarySpanElement && secondaryMessage !== null) {
            this.secondarySpanElement.innerText = secondaryMessage;
        } else {
            this.secondarySpanElement.innerText = this.overlaySecondaryMessage;
        }
        if (this.overlayElement) {
            this.overlayElement.classList.remove(
                'slimfaas-environment-overlay--error',
                'slimfaas-environment-overlay--waiting',
                'slimfaas-environment-overlay--waiting-action'
            );
            this.overlayElement.classList.add('slimfaas-environment-overlay--' + status);
        }
    }

    /**
     * Nettoie le composant : retire listeners, styles et overlay
     */
    cleanup() {
        this.cleanned = true;
        this.stopPolling();
        document.removeEventListener('visibilitychange', this.handleVisibilityChange);
        document.removeEventListener('mousemove', this.handleMouseMove);

        document.getElementById(`slimfaas-environment-overlay-${this.id}`)?.remove();

        if (this.overlayElement && document.body.contains(this.overlayElement)) {
            document.body.removeChild(this.overlayElement);
        }
        if (this.styleElement && document.head.contains(this.styleElement)) {
            document.head.removeChild(this.styleElement);
        }
    }
}

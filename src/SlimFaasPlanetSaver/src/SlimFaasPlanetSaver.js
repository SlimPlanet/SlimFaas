const normalizeBaseUrl = (url) => {
    let tempUrl = url;
    if (tempUrl.endsWith('/')) tempUrl = tempUrl.slice(0, -1);
    return tempUrl;
}

let id =1;

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

    async wakeUpPods(data, lastWakeUpTime) {
        const currentTime = Date.now();
        let isWakeUpCallMade = false;
        const shouldFilter = lastWakeUpTime && (currentTime - lastWakeUpTime) <= this.wakeUpTimeout;
        const wakePromises = data
            .filter((item) => item.NumberReady === 0 || !shouldFilter)
            .map(async (item) => {
                const response = await this.fetch(`${this.baseUrl}/wake-function/${item.Name}`, {
                    method: 'POST',
                });
                if (response.status >= 400) {
                    throw new Error(`HTTP Error! status: ${response.status} for function ${item.Name}`);
                }
                isWakeUpCallMade = true
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

    async fetchStatus() {
        try {
            const response = await this.fetch(`${this.baseUrl}/status-functions`);
            if (response.status >= 400) {
                throw new Error(`HTTP Error! status: ${response.status}`);
            }
            const data = await response.json();

            const allReady = data.every((item) => item.NumberReady >= 1);
            this.setReadyState(allReady);

            this.updateCallback(data);

            const now = Date.now();
            const mouseMovedRecently = now - this.lastMouseMoveTime <= this.noActivityTimeout; // 1 minute

            if (!allReady && this.isDocumentVisible && !mouseMovedRecently) {
                this.updateOverlayMessage(this.overlayNoActivityMessage, 'waiting-action');
            } else if (mouseMovedRecently) {
                if(!allReady  && this.isDocumentVisible) {
                    this.updateOverlayMessage(this.overlayStartingMessage, 'waiting');
                }
                if(!this.lastWakeUpTime) {
                    this.lastWakeUpTime = Date.now();
                }
                const isWakeUpCallMade = await this.wakeUpPods(data, this.lastWakeUpTime);
                if(isWakeUpCallMade) {
                    this.lastWakeUpTime = Date.now();
                }
            } else if(!this.isDocumentVisible && !allReady) {
                this.updateOverlayMessage(this.overlayNoActivityMessage, 'waiting');
            }
        } catch (error) {
            const errorMessage = error.message;
            this.setReadyState(this.isReady);
            this.updateOverlayMessage(this.overlayErrorMessage, 'error', this.overlayErrorSecondaryMessage);
            this.errorCallback(errorMessage);
            console.error('Error fetching slimfaas data:', errorMessage);
        } finally {
            if(this.intervalId)  {
                this.intervalId = setTimeout(() => {
                    this.fetchStatus();
                }, this.interval);
            }
        }
    }

    setReadyState(isReady) {
        this.isReady = isReady;
        if (isReady) {
            this.hideOverlay();
        } else {
            this.showOverlay();
        }
    }

    startPolling() {
        if (this.intervalId || !this.baseUrl || this.cleanned) return;

        this.fetchStatus();

        this.intervalId = setTimeout(() => {
            this.fetchStatus();
        }, this.interval);
    }

    stopPolling() {
        if (this.intervalId) {
            clearTimeout(this.intervalId);
            this.intervalId = null;
        }
    }

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

    createOverlay() {
        this.overlayElement = document.createElement('div');
        this.overlayElement.className = 'slimfaas-environment-overlay';
        this.overlayElement.id = `slimfaas-environment-overlay-${this.id}`;

        // Créer l'élément icône
        this.iconElement = document.createElement('div');
        this.iconElement.className = 'slimfaas-environment-overlay__icon';
        this.iconElement.innerText = this.overlayLoadingIcon;

        // Créer l'élément du message principal
        this.spanElement = document.createElement('span');
        this.spanElement.className = 'slimfaas-environment-overlay__main-message';
        this.spanElement.innerHTML = `${this.overlayStartingMessage}`;

        // Créer l'élément du message secondaire
        this.secondarySpanElement = document.createElement('span');
        this.secondarySpanElement.className = 'slimfaas-environment-overlay__secondary-message';
        this.secondarySpanElement.innerText = this.overlaySecondaryMessage;

        // Ajouter les éléments à l'overlay
        this.overlayElement.appendChild(this.iconElement);
        this.overlayElement.appendChild(this.spanElement);
        this.overlayElement.appendChild(this.secondarySpanElement);

        // Ne pas ajouter l'overlay au DOM ici
        // document.body.appendChild(this.overlayElement);
    }

    showOverlay() {
        if(this.cleanned) return;
        if (this.overlayElement && !document.body.contains(this.overlayElement)) {
            document.body.appendChild(this.overlayElement);
        }
    }

    hideOverlay() {
        if (this.overlayElement && document.body.contains(this.overlayElement)) {
            document.body.removeChild(this.overlayElement);
        }
    }

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

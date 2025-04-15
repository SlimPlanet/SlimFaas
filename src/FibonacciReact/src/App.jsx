import { useState, useEffect, useRef } from 'react'
import './App.css'

// Logo SlimFaas (URL GitHub)
const slimFaasLogoUrl =
    'https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true';

// Petite fonction pour formater la durée en "Xs Yms"
function formatDuration(durationMs) {
    const seconds = Math.floor(durationMs / 1000);
    const ms = Math.floor(durationMs % 1000);
    return `${seconds}s ${ms}ms`;
}

/**
 * Hook personnalisé pour exécuter un callback à intervalle régulier
 */
function useInterval(callback, delay) {
    const savedCallback = useRef();

    useEffect(() => {
        savedCallback.current = callback;
    }, [callback]);

    useEffect(() => {
        if (delay === null) return;
        function tick() {
            if (savedCallback.current) {
                savedCallback.current();
            }
        }
        const id = setInterval(tick, delay);
        return () => clearInterval(id);
    }, [delay]);
}

function Main({ url }) {
    const [states, setStates] = useState([]);
    const [stateInterval, setInterval] = useState(true);

    // Contrôle l’affichage de l’overlay
    const [showOverlay, setShowOverlay] = useState(false);

    // On stocke la dernière requête dans lastRequest
    const [lastRequest, setLastRequest] = useState(null);
    // Indique si une requête est en cours
    const [isLoading, setIsLoading] = useState(false);

    // Pour calculer la durée en temps réel
    const [requestTimeMs, setRequestTimeMs] = useState(0);
    // On garde en mémoire le temps de départ
    const startTimeRef = useRef(null);

    // Met à jour requestTimeMs en continu (si isLoading = true)
    useInterval(() => {
        if (isLoading && startTimeRef.current !== null) {
            const now = performance.now();
            setRequestTimeMs(now - startTimeRef.current);
        }
    }, isLoading ? 50 : null); // Mise à jour toutes les 50 ms

    /**
     * Lance une requête et met à jour l'overlay :
     * - Montre l'overlay (showOverlay = true)
     * - Timer reset à 0 + démarrage
     * - On fetch
     * - On arrête le timer lorsque la requête est terminée
     */
    const doRequest = async ({ fullUrl, method = 'GET', body = null }) => {
        setShowOverlay(true);    // Ré-affiche l’overlay si l’utilisateur l’avait fermé
        setIsLoading(true);
        setRequestTimeMs(0);
        startTimeRef.current = performance.now(); // On mémorise l’instant de départ

        // On initialise lastRequest pour afficher immédiatement l'URL
        setLastRequest({
            url: fullUrl,
            method,
            status: null,
            responseData: null,
        });

        try {
            const headers = { 'Content-Type': 'application/json' };
            const fetchOptions = body
                ? { method, headers, body: JSON.stringify(body) }
                : { method };

            const res = await fetch(fullUrl, fetchOptions);
            const statusCode = res.status;

            let responseData;
            try {
                // On tente de parser la réponse en JSON
                responseData = await res.json();
            } catch {
                // Si la réponse n'est pas du JSON, on stocke juste le code HTTP
                responseData = { statusCode };
            }

            // On arrête le timer
            const endTime = performance.now();
            setRequestTimeMs(endTime - startTimeRef.current);
            setIsLoading(false);

            // Mise à jour finale de lastRequest
            setLastRequest({
                url: fullUrl,
                method,
                status: statusCode,
                responseData,
            });
        } catch (error) {
            // En cas d’erreur réseau ou autre
            const endTime = performance.now();
            setRequestTimeMs(endTime - startTimeRef.current);
            setIsLoading(false);

            setLastRequest({
                url: fullUrl,
                method,
                status: 'error',
                responseData: { error: error.message },
            });
        }
    };

    /**
     * Récupération régulière du status des fonctions SlimFaas
     */
    useInterval(() => {
        if (stateInterval) {
            setInterval(false);
            fetch(url + '/status-functions')
                .then((res) => res.json())
                .then((data) => {
                    const result = data.map((item) => {
                        const r = {
                            name: item.Name,
                            visibility: item.Visibility,
                            podType: item.PodType,
                            numberReady: item.NumberReady || 0,
                            numberRequested: item.NumberRequested || 0,
                        };
                        let status = r.numberReady === r.numberRequested ? 'ready' : 'loading';
                        if (r.numberRequested === 0) status = 'not_started';
                        r.status = status;
                        return r;
                    });
                    setStates(result);
                })
                .finally(() => {
                    setInterval(true);
                });
        }
    }, 100);

    return (
        <>
            {/* Header (barre d'en-tête) */}
            <header className="header-bar">
                <img src={slimFaasLogoUrl} alt="SlimFaas Logo" className="logo" />
                <h1 className="header-title">SlimFaas Demo</h1>
            </header>

            {/* Zone principale : cartes de déploiement + boutons "events" */}
            <div className="main">
                {states.map((state, idx) => (
                    <Deployment key={idx} data={state} url={url} doRequest={doRequest} />
                ))}
                <Buttons url={url} doRequest={doRequest} />
            </div>

            {/* Overlay (affiché uniquement si showOverlay == true) */}
            {showOverlay && (
                <OverlayRequest
                    lastRequest={lastRequest}
                    isLoading={isLoading}
                    requestTimeMs={requestTimeMs}
                    onClose={() => setShowOverlay(false)}  // Le bouton "Close"
                />
            )}
        </>
    );
}

/**
 * Composant qui représente un bloc de déploiement
 */
function Deployment({ data, url, doRequest }) {
    // Les différentes actions possibles
    const postFibonacciAsync = (method = 'fibonacci') => {
        doRequest({
            fullUrl: `${url}/function/${data.name}/${method}`,
            method: 'POST',
            body: { input: 10 },
        });
    };

    const postStartAsync = () => {
        doRequest({
            fullUrl: `${url}/wake-function/${data.name}`,
            method: 'POST',
        });
    };

    const eventFibonacciAsync = (method = 'fibonacci', eventName = 'fibo-public') => {
        doRequest({
            fullUrl: `${url}/publish-event/${eventName}/${method}`,
            method: 'POST',
            body: { input: 10 },
        });
    };

    const privateEventFibonacciAsync = (method = 'send-private-fibonacci-event') => {
        doRequest({
            fullUrl: `${url}/function/${data.name}/${method}`,
            method: 'POST',
            body: { input: 10 },
        });
    };

    return (
        <div className="deployment-card">
            <h2>
                {data.name}
                <span className={`environment environment_${data.status}`}>
          {data.status}
        </span>
            </h2>
            <div className="actions">
                {data.name !== 'mysql' && (
                    <>
                        <button onClick={() => postFibonacciAsync()}>
                            Post /fibonacci 10
                        </button>
                        <button onClick={() => privateEventFibonacciAsync()}>
                            Post /send-private-fibonacci-event 10
                        </button>
                    </>
                )}
                {data.name !== 'mysql' && data.name !== 'fibonacci4' && (
                    <button onClick={() => postFibonacciAsync('fibonacci4')}>
                        Post /fibonacci4 10
                    </button>
                )}
                <button onClick={postStartAsync}>Wake up</button>
            </div>
        </div>
    );
}

/**
 * Composant pour les events globaux
 */
function Buttons({ url, doRequest }) {
    const eventFibonacciAsync = (method = 'fibonacci', eventName = 'fibo-public') => {
        doRequest({
            fullUrl: `${url}/publish-event/${eventName}/${method}`,
            method: 'POST',
            body: { input: 10 },
        });
    };

    return (
        <div className="buttons-card">
            <h2>Events</h2>
            <div className="actions">
                <button onClick={() => eventFibonacciAsync()}>
                    Send event: fibo-public
                </button>
                <button onClick={() => eventFibonacciAsync('fibonacci', 'fibo-private')}>
                    Send event: fibo-private
                </button>
            </div>
        </div>
    );
}

/**
 * OverlayRequest : n'affiche que la dernière requête + un timer
 * + un bouton "Close" pour masquer l'overlay
 */
function OverlayRequest({ lastRequest, isLoading, requestTimeMs, onClose }) {
    if (!lastRequest) return null;

    const { url, method, status, responseData } = lastRequest;

    // Calcul du temps écoulé en Xs Yms
    const seconds = Math.floor(requestTimeMs / 1000);
    const ms = Math.floor(requestTimeMs % 1000);

    return (
        <div className="overlay-requests">
            {/* Bouton Close en haut à droite */}
            <button className="overlay-close" onClick={onClose}>×</button>

            <div className="overlay-content">
                {isLoading && <span className="loader" />}
                <div className="overlay-line">
                    <strong>Requête :</strong> {method} {url}
                    {status !== null && !isLoading && (
                        <span className="overlay-status">({status})</span>
                    )}
                </div>
                <div className="overlay-line">
                    <strong>Temps écoulé : </strong> {seconds}s {ms}ms
                </div>
                {!isLoading && responseData && Object.keys(responseData).length > 0 && (
                    <div className="overlay-response">
                        <strong>Réponse :</strong> {JSON.stringify(responseData)}
                    </div>
                )}
            </div>
        </div>
    );
}

export default Main;

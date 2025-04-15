import React, { useState, useEffect, useRef } from 'react';
import SlimFaasPlanetSaver from '@axa-fr/slimfaas-planet-saver';

const PlanetSaver = ({ children, baseUrl, fetch, noActivityTimeout=10000, interval=2000, wakeUpTimeout=1000, behavior={} }) => {
    const [isFirstStart, setIsFirstStart] = useState(true);
    const environmentStarterRef = useRef(null);

    useEffect(() => {
        if (!baseUrl) return;

        if (environmentStarterRef.current) return;

        const instance = new SlimFaasPlanetSaver(baseUrl, {
            interval,
            fetch,
            behavior,
            updateCallback: (data) => {
                // Filter only the items that block the UI (WakeUp+BockUI)
                const blockingItems = data.filter(
                    (item) => instance.getBehavior(item.Name) === 'WakeUp+BlockUI'
                );

                // If all blocking items are ready, set isFirstStart to false
                const allBlockingReady = blockingItems.every(
                    (item) => item.NumberReady >= 1
                );
                console.log(blockingItems)
                console.log(allBlockingReady)
                if (allBlockingReady && isFirstStart) {
                    setIsFirstStart(false);
                }
            },
            errorCallback: (error) => {
                console.error('Error detected :', error);
            },
            overlayStartingMessage: '🌳 Starting the environment.... 🌳',
            overlayNoActivityMessage: 'Waiting activity to start environment...',
            overlayErrorMessage: 'An error occurred when starting environment. Please contact an administrator.',
            overlaySecondaryMessage: 'Startup should be fast, but if no machines are available it can take several minutes.',
            overlayLoadingIcon: '🌍',
            overlayErrorSecondaryMessage: 'If the error persists, please contact an administrator.',
            noActivityTimeout,
            wakeUpTimeout,
        });

        environmentStarterRef.current = instance;

        // Initialiser les effets de bord
        instance.initialize();
        instance.startPolling();

        return () => {
            instance.cleanup();
            environmentStarterRef.current = null;
        };
    }, [baseUrl]);

    if (isFirstStart) {
        return null;
    }

    return <>{children}</>;
};

export default PlanetSaver;

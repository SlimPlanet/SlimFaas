import React, { useState, useEffect, useRef } from 'react';
import SlimFaasPlanetSaver from "./SlimFaasPlanetSaver.js";

const PlanetSaver = ({ children, baseUrl, fetch, noActivityTimeout=60000, behavior={} }) => {
    const [isFirstStart, setIsFirstStart] = useState(true);
    const instanceRef = useRef<null | any>(null);

    useEffect(() => {
        if (!baseUrl) return;

        if (instanceRef.current) return;

        const instance = new SlimFaasPlanetSaver(baseUrl, {
            interval: 2000,
            fetch,
            behavior,
            updateCallback: (data) => {
                const inst = instanceRef.current;
                // Filter only the items that block the UI (WakeUp+BlockUI)
                const blockingItems = data.filter(
                    (item) => inst?.getBehavior(item.Name) === 'WakeUp+BlockUI'
                );

                // If all blocking items are ready, set isFirstStart to false
                const allBlockingReady = blockingItems.every(
                    (item) => item.NumberReady >= 1
                );
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
            noActivityTimeout
        });

        instanceRef.current = instance;

        // Initialiser les effets de bord
        instance.initialize();
        instance.startPolling();

        return () => {
            try {
                instance.cleanup();
            } finally {
                instanceRef.current = null;
            }
        };
    }, [baseUrl]);

    if (isFirstStart) {
        return null;
    }

    return <>{children}</>;
};

export default PlanetSaver;

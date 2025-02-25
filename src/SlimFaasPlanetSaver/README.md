# @axa-fr/slimfaas-planet-saver

[![npm version](https://badge.fury.io/js/%40axa-fr%2Fslimfaas-planet-saver.svg)](https://badge.fury.io/js/%40axa-fr%2Fslimfaas-planet-saver)

![SlimFaas.png](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimFaas.png)

A Vanilla JS project to save the planet. SlimFaas (https://github.com/SlimPlanet/slimfaas) is the slimest and simplest Function As A Service on Kubernetes.
It works as a proxy that you can be deployed in your namespace.

SlimFaas API can give to the frontend information about the infrastructure state. **It is a mind changer !**

**Why?**

Because in production instead of setting up 2 replicas of your API backend, you can set up 0 replicas and use an UX that will show the user that the backend is down instead !
**@axa-fr/slimfaas-planet-saver** is here to for doing that easily.

![SlimFaasPlanetSaver.gif](https://github.com/AxaFrance/SlimFaas/blob/main/documentation/SlimfaasPlanetSaver.gif)

## Getting Started

```javascript
npm install @axa-fr/slimfaas-planet-saver
```

Example usage with react :
```javascript
import React, { useState, useEffect, useRef } from 'react';
import { SlimFaasPlanetSaver } from "@axa-fr/slimfaas-planet-saver";

const PlanetSaver = ({ children, baseUrl, fetch }) => {
    const [isFirstStart, setIsFirstStart] = useState(true);
    const environmentStarterRef = useRef(null);

    useEffect(() => {
        if (!baseUrl) return;

        if (environmentStarterRef.current) return;

        const instance = new SlimFaasPlanetSaver(baseUrl, {
            interval: 2000,
            fetch,
            updateCallback: (data) => {
                const allReady = data.every((item) => item.NumberReady >= 1);
                if (allReady && isFirstStart) {
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
            overlayErrorSecondaryMessage: 'If the error persists, please contact an administrator.'
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


```

## Run the demo

```javascript
git clone https://github.com/SlimPlanet/slimfaas.git
cd slimfaas/src/SlimFaasPlanetSaver
npm i
npm run dev
```

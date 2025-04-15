export type BehaviorValue = 'WakeUp+BlockUI' | 'WakeUp' | 'None';

export interface BehaviorMap {
    [functionName: string]: BehaviorValue;
}

export interface SlimFaasPlanetSaverOptions {
    updateCallback?: (data: any[]) => void;
    errorCallback?: (errorMessage: string) => void;
    interval?: number;
    overlayStartingMessage?: string;
    overlayNoActivityMessage?: string;
    overlayErrorMessage?: string;
    overlaySecondaryMessage?: string;
    overlayErrorSecondaryMessage?: string;
    overlayLoadingIcon?: string;
    noActivityTimeout?: number;
    wakeUpTimeout?: number;
    fetch?: typeof fetch;
    behavior?: BehaviorMap;
}

export default class SlimFaasPlanetSaver {
    constructor(baseUrl: string, options?: SlimFaasPlanetSaverOptions);
    initialize(): void;
    startPolling(): void;
    stopPolling(): void;
    protected fetchStatus(): Promise<void>;
    protected wakeUpPods(data: any[], lastWakeUpTime: number | null): Promise<boolean>;
    protected setReadyState(isReady: boolean): void;
    protected createOverlay(): void;
    protected injectStyles(): void;
    protected showOverlay(): void;
    protected hideOverlay(): void;
    protected updateOverlayMessage(newMessage: string, status?: string, secondaryMessage?: string | null): void;
    cleanup(): void;
}

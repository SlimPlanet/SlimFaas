'use client';
import { useEffect, useState, useCallback } from 'react';

type ConsentPrefs = {
    analytics: boolean;
    ads: boolean;
    adUserData: boolean;
    adPersonalization: boolean;
};

const STORAGE_KEY = 'sf-consent-v1';

declare global {
    interface Window {
        gtag?: (...args: unknown[]) => void;
        __sf_openConsent?: () => void;
    }
}

function updateConsent(prefs: ConsentPrefs) {
    const to = (v: boolean) => (v ? 'granted' : 'denied');
    window.gtag?.('consent', 'update', {
        analytics_storage: to(prefs.analytics),
        ad_storage: to(prefs.ads),
        ad_user_data: to(prefs.adUserData),
        ad_personalization: to(prefs.adPersonalization),
    });
}

function savePrefs(prefs: ConsentPrefs) {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(prefs)); } catch {}
}

function loadPrefs(): ConsentPrefs | null {
    try { const raw = localStorage.getItem(STORAGE_KEY); return raw ? (JSON.parse(raw) as ConsentPrefs) : null; } catch { return null; }
}

export default function CookieConsent() {
    const [showBanner, setShowBanner] = useState(false);
    const [showPrefs, setShowPrefs] = useState(false);
    const [prefs, setPrefs] = useState<ConsentPrefs>({ analytics: false, ads: false, adUserData: false, adPersonalization: false });

    // Footer "Cookie settings" link
    useEffect(() => {
        window.__sf_openConsent = () => { setShowPrefs(true); setShowBanner(false); };
    }, []);

    // Optional: allow ?consent=reset to force banner in dev
    useEffect(() => {
        if (typeof window !== 'undefined') {
            const p = new URLSearchParams(window.location.search);
            if (p.get('consent') === 'reset') {
                try { localStorage.removeItem(STORAGE_KEY); } catch {}
            }
        }

        const stored = loadPrefs();
        if (stored) { setPrefs(stored); updateConsent(stored); setShowBanner(false); }
        else { setShowBanner(true); }
    }, []);

    const acceptAll = useCallback(() => {
        const all: ConsentPrefs = { analytics: true, ads: true, adUserData: true, adPersonalization: true };
        setPrefs(all); updateConsent(all); savePrefs(all); setShowBanner(false); setShowPrefs(false);
    }, []);

    const rejectAll = useCallback(() => {
        const none: ConsentPrefs = { analytics: false, ads: false, adUserData: false, adPersonalization: false };
        setPrefs(none); updateConsent(none); savePrefs(none); setShowBanner(false); setShowPrefs(false);
    }, []);

    const saveCustom = useCallback(() => {
        updateConsent(prefs); savePrefs(prefs); setShowPrefs(false); setShowBanner(false);
    }, [prefs]);

    return (
        <>
            {showBanner && (
                <div className="cookie-consent__backdrop">
                    <div className="cookie-consent__card">
                        <h3 style={{ marginTop: 0 }}>Cookies & Privacy</h3>
                        <p className="cookie-consent__text">
                            We use cookies for measurement (analytics) and—optionally—ads. Choose “Accept all”, “Reject all”, or “Customize”.
                        </p>
                        <div className="cookie-consent__actions">
                            <button onClick={rejectAll} className="btn btn--secondary">Reject all</button>
                            <button onClick={() => { setShowPrefs(true); setShowBanner(false); }} className="btn btn--ghost">Customize</button>
                            <button onClick={acceptAll} className="btn btn--primary">Accept all</button>
                        </div>
                    </div>
                </div>
            )}

            {showPrefs && (
                <div className="cookie-consent__backdrop">
                    <div className="cookie-consent__card">
                        <h3 style={{ marginTop: 0 }}>Cookie Preferences</h3>

                        <div className="cookie-consent__row">
                            <label className="cookie-consent__label">
                                <input type="checkbox" checked={prefs.analytics} onChange={(e) => setPrefs({ ...prefs, analytics: e.target.checked })} /> Analytics (GA4 via GTM)
                            </label>
                        </div>

                        <div className="cookie-consent__row">
                            <label className="cookie-consent__label">
                                <input type="checkbox" checked={prefs.ads} onChange={(e) => setPrefs({ ...prefs, ads: e.target.checked })} /> Ads Storage
                            </label>
                        </div>

                        <div className="cookie-consent__row">
                            <label className="cookie-consent__label">
                                <input type="checkbox" checked={prefs.adUserData} onChange={(e) => setPrefs({ ...prefs, adUserData: e.target.checked })} /> Ad User Data
                            </label>
                        </div>

                        <div className="cookie-consent__row">
                            <label className="cookie-consent__label">
                                <input type="checkbox" checked={prefs.adPersonalization} onChange={(e) => setPrefs({ ...prefs, adPersonalization: e.target.checked })} /> Ad Personalization
                            </label>
                        </div>

                        <p className="cookie-consent__text" style={{ fontSize: '0.9rem' }}>
                            You can change your choice anytime via “Cookie settings” in the footer. Until you accept, storage is denied.
                        </p>

                        <div className="cookie-consent__actions">
                            <button onClick={rejectAll} className="btn btn--secondary">Reject all</button>
                            <button onClick={saveCustom} className="btn btn--primary">Save</button>
                        </div>
                    </div>
                </div>
            )}
        </>
    );
}

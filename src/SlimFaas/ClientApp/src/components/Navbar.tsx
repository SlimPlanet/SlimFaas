import { useRef, useState } from 'react';
import { trafficHome } from '../lib/dashboard';
import Icon from './Icon';

interface Props {
  path: string; connected: string; canWakeAll: boolean; waking: boolean; wakeAll: () => void;
  streamsEnabled: boolean; deadLettersEnabled: boolean;
}

export default function Navbar({ path, connected, canWakeAll, waking, wakeAll, streamsEnabled, deadLettersEnabled }: Props) {
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const links = [[trafficHome, 'Live Stream'], ['#/overview', 'Overview'], ['#/live/data', 'Data'], ['#/live/scaling', 'Scaling']];
  if (streamsEnabled) links.push(['#/streams', 'Streams']);
  if (deadLettersEnabled) links.push(['#/dead-letters', 'Dead Letter Queues']);
  return <header className="navbar">
    <a href="#main" onClick={event => { event.preventDefault(); document.getElementById('main')?.focus(); }} className="navbar__skip">Skip to content</a>
    <a className="navbar__brand" href={trafficHome}><img className="navbar__logo" src="/slimfaas.svg" width="32" height="32" alt="" />SlimFaas</a>
    <span className={`navbar__connection ${connected === 'Connected' ? 'navbar__connection--live' : ''}`}><span>{connected}</span></span>
    <button className="button navbar__wake" type="button" disabled={!canWakeAll || waking} onClick={wakeAll}><Icon name="bolt" />{waking ? 'Waking functions…' : 'Wake all functions'}</button>
    <div className="navbar__menu" onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget)) setOpen(false); }}
      onKeyDown={e => { if (e.key === 'Escape') { setOpen(false); trigger.current?.focus(); } }}>
      <button ref={trigger} type="button" className="navbar__toggle" aria-expanded={open} aria-controls="dashboard-navigation" onClick={() => setOpen(!open)}>Views ▾</button>
      <nav id="dashboard-navigation" className="navbar__navigation" aria-label="Main navigation" hidden={!open} onClick={() => { setOpen(false); trigger.current?.focus(); }}>
        {links.map(([url, name]) => <a key={url} className="navbar__link" href={url} aria-current={path === url || path.startsWith(url + '/') ? 'page' : undefined}>{name}</a>)}
        <a className="navbar__link" href="https://slimfaas.dev/user-interface" target="_blank" rel="noreferrer">Documentation ↗</a>
      </nav>
    </div>
  </header>;
}
